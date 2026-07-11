using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal sealed record ProposalApplyFileInput(string RelativePath, string ReplacementText);
internal sealed record ProposalApplyDraft(Guid DraftId, Guid ProposalId, int ProposalRevision, Guid RootId, int SelectionRevision, string ApprovalDigest, bool IsApproved, IReadOnlyList<ApplyTarget> Files, IReadOnlyList<ApplyHunk> Hunks);

internal sealed class ProposalApplyService
{
    private readonly Dictionary<Guid, ProposalApplyDraft> drafts = [];
    private readonly object sync = new();
    private readonly ProposalApplyTransaction transaction;
    private readonly ISessionStore? store;
    private readonly string draftPath;
    private readonly Dictionary<Guid, ProposalApplyLinkage> applies = [];

    public ProposalApplyService(IReadOnlyList<ConfiguredApplyRoot> roots, string runtimePath, Action<string>? fault = null) : this(roots, runtimePath, null, fault) { }
    public ProposalApplyService(IReadOnlyList<ConfiguredApplyRoot> roots, string runtimePath, ISessionStore? store, Action<string>? fault = null)
    {
        transaction = new ProposalApplyTransaction(runtimePath, roots, fault); Roots = transaction.ConfiguredRoots; this.store = store;
        draftPath = Path.Combine(runtimePath, "drafts"); Directory.CreateDirectory(draftPath); transaction.RecoverUncommitted(); ReconcilePending(); LoadDrafts(); LoadAppliedLinkages();
    }
    public IReadOnlyList<ConfiguredApplyRoot> Roots { get; }

    public ProposalApplyDraft CreateDraft(Guid proposalId, Guid rootId, IReadOnlyList<ProposalApplyFileInput> inputs)
    {
        var proposalRevision = store?.GetImprovementProposal(proposalId)?.Revision ?? 1;
        if (store is not null && store.GetImprovementProposal(proposalId) is null) throw new ApplyPathException("proposal_not_found");
        var root = Roots.SingleOrDefault(item => item.RootId == rootId) ?? throw new ApplyPathException("invalid_root_id");
        if (inputs.Count is < 1 or > 10) throw new ApplyPathException("invalid_apply_request");
        var files = inputs.Select(input =>
        {
            if (Encoding.UTF8.GetByteCount(input.ReplacementText) > 262_144) throw new ApplyPathException("request_too_large");
            var resolved = ApplyPathPolicy.Resolve(root, input.RelativePath); var original = File.ReadAllText(resolved.FullPath);
            if (Encoding.UTF8.GetByteCount(original) > 262_144) throw new ApplyPathException("request_too_large");
            return new ApplyTarget(root, resolved.RelativePath, original, input.ReplacementText, LineDiff.Sha256(original), LineDiff.Sha256(input.ReplacementText));
        }).ToArray();
        if (files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new ApplyPathException("duplicate_target");
        var hunks = files.SelectMany(file => LineDiff.Create(file.RelativePath, file.OriginalText, file.ReplacementText)).ToArray(); if (hunks.Length == 0) throw new ApplyPathException("invalid_selection");
        var id = Guid.CreateVersion7(); var draft = new ProposalApplyDraft(id, proposalId, proposalRevision, rootId, 1, Digest(id, proposalId, proposalRevision, rootId, files, hunks, 1), false, files, hunks);
        lock (sync) { drafts.Add(id, draft); Persist(draft, ProposalApplyState.Draft); } return draft;
    }
    public ProposalApplyDraft GetDraft(Guid id) { lock (sync) return Get(id); }
    public ProposalApplyDraft Select(Guid id, int revision, IReadOnlyList<string> selectedHunkIds)
    {
        lock (sync) { var draft = Get(id); if (draft.SelectionRevision != revision) throw new ApplyPathException("selection_stale");
            var selected = draft.Hunks.Select(h => h with { Selected = selectedHunkIds.Contains(h.HunkId, StringComparer.Ordinal) }).ToArray(); if (!selected.Any(h => h.Selected)) throw new ApplyPathException("invalid_selection");
            if (!HasCurrentProposalRevision(draft)) throw new ApplyPathException("selection_stale");
            var files = RebuildFiles(draft.Files, selected); var updated = draft with { SelectionRevision = revision + 1, Files = files, Hunks = selected, IsApproved = false, ApprovalDigest = Digest(draft.DraftId, draft.ProposalId, draft.ProposalRevision, draft.RootId, files, selected, revision + 1) };
            drafts[id] = updated; Persist(updated, ProposalApplyState.Draft); return updated; }
    }
    public ProposalApplyDraft Approve(Guid id, int revision, string digest)
    {
        lock (sync) { var draft = Get(id); if (draft.SelectionRevision != revision || !HasCurrentProposalRevision(draft)) throw new ApplyPathException("selection_stale"); if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(draft.ApprovalDigest), Encoding.UTF8.GetBytes(digest))) throw new ApplyPathException("approval_digest_mismatch");
            var approved = draft with { IsApproved = true }; drafts[id] = approved; Persist(approved, ProposalApplyState.Approved); return approved; }
    }
    public ApplyTransactionResult Apply(Guid id) => ApplyWithId(id).Result;
    public (Guid ApplyId, ApplyTransactionResult Result, ProposalApplyDraft Draft) ApplyWithId(Guid id)
    {
        lock (sync) { var draft = Get(id); if (!draft.IsApproved || !IsTrusted(draft, requireApproved: true) || !HasCurrentProposalRevision(draft)) throw new ApplyPathException("approval_required"); var apply = Guid.CreateVersion7();
            store?.SaveProposalApplyPending(new(apply, id, draft.ProposalId, draft.RootId, draft.Files.Count, "apply", DateTimeOffset.UtcNow));
            var result = transaction.Apply(apply, draft.Files);
            if (result == ApplyTransactionResult.Applied) Complete(apply, draft, ProposalApplyState.Applied, null);
            else Complete(apply, draft, ProposalApplyState.Failed, result == ApplyTransactionResult.Stale ? "apply_stale" : "apply_failed");
            if (result == ApplyTransactionResult.Applied) applies[apply] = new(apply, draft.DraftId, draft.ProposalId, draft.ProposalRevision, draft.RootId, draft.Files.Count, draft.SelectionRevision, draft.ApprovalDigest);
            return (apply, result, draft); }
    }
    public ApplyTransactionResult Rollback(Guid applyId)
    {
        lock (sync)
        {
            if (!applies.TryGetValue(applyId, out var linkage)) return ApplyTransactionResult.RollbackUnavailable;
            if (store is not null && !store.TryStartProposalApplyRollback(new(applyId, linkage.DraftId, linkage.ProposalId, linkage.RootId, linkage.FileCount, "rollback", DateTimeOffset.UtcNow))) return ApplyTransactionResult.RollbackUnavailable;
            var result = transaction.Rollback(applyId);
            if (store is null) { if (!drafts.TryGetValue(linkage.DraftId, out var draft)) return ApplyTransactionResult.RollbackUnavailable; if (result == ApplyTransactionResult.RolledBack) Complete(applyId, draft, ProposalApplyState.RolledBack, null); }
            else Complete(applyId, linkage, result == ApplyTransactionResult.RolledBack ? ProposalApplyState.RolledBack : ProposalApplyState.Failed, result == ApplyTransactionResult.RollbackStale ? "rollback_stale" : "rollback_failed");
            if (result == ApplyTransactionResult.RolledBack) applies.Remove(applyId);
            return result;
        }
    }
    private void Complete(Guid applyId, ProposalApplyDraft draft, ProposalApplyState state, string? errorCode) => store?.CompleteProposalApplyPending(new(applyId, draft.DraftId, state, DateTimeOffset.UtcNow), draft.ProposalId, draft.RootId, draft.Files.Count, errorCode);
    private void Complete(Guid applyId, ProposalApplyLinkage linkage, ProposalApplyState state, string? errorCode) => store?.CompleteProposalApplyPending(new(applyId, linkage.DraftId, state, DateTimeOffset.UtcNow), linkage.ProposalId, linkage.RootId, linkage.FileCount, errorCode);
    private void ReconcilePending()
    {
        if (store is null) return;
        foreach (var pending in store.ListProposalApplyPending())
        {
            var marker = transaction.GetJournalState(pending.ApplyId);
            var rolledBack = marker is "rolled_back" or "restored";
            var outcome = pending.OperationKind == "apply" ? marker == "committed" ? ProposalApplyState.Applied : ProposalApplyState.Failed : rolledBack ? ProposalApplyState.RolledBack : ProposalApplyState.Failed;
            var error = outcome == ProposalApplyState.Failed ? pending.OperationKind == "apply" ? "apply_failed" : "rollback_failed" : null;
            store.CompleteProposalApplyPending(new(pending.ApplyId, pending.DraftId, outcome, DateTimeOffset.UtcNow), pending.ProposalId, pending.RootId, pending.FileCount, error);
        }
    }
    private void LoadAppliedLinkages()
    {
        if (store is null) return;
        foreach (var linkage in store.ListAppliedProposalApplyLinkages()) applies[linkage.ApplyId] = linkage;
    }
    private ProposalApplyDraft Get(Guid id) => drafts.TryGetValue(id, out var draft) ? draft : throw new ApplyPathException("draft_not_found");
    private void Persist(ProposalApplyDraft draft, ProposalApplyState state)
    {
        if (store is null) { WritePrivate(draft); return; } var now = DateTimeOffset.UtcNow; var metadata = new ProposalApplyDraftMetadata(draft.DraftId, draft.ProposalId, draft.ProposalRevision, draft.RootId, draft.SelectionRevision, draft.ApprovalDigest, state, draft.Files.Count, now, now);
        var files = draft.Files.Select(f => (f.BaseSha256, f.ReplacementSha256)).ToArray(); var hunks = draft.Hunks.Select(h => (h.HunkId, h.Selected, LineDiff.Sha256(h.ReplacementText))).ToArray();
        if (store.GetProposalApplyDraft(draft.DraftId) is null) store.SaveProposalApplyDraft(metadata, files, hunks, new(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest, draft.IsApproved ? now : null));
        else if (draft.IsApproved) store.SaveProposalApplyApproval(draft.DraftId, new(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest, now));
        else store.UpdateProposalApplyDraft(metadata, files, hunks, new(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest, null));
        WritePrivate(draft);
    }
    private void WritePrivate(ProposalApplyDraft draft) { var path = Path.Combine(draftPath, draft.DraftId.ToString("N") + ".json"); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(draft)); using (var s = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read)) s.Flush(true); File.Move(temp, path, true); }
    private void LoadDrafts()
    {
        if (store is null)
        {
            foreach (var path in Directory.EnumerateFiles(draftPath, "*.json")) TryLoad(path, null);
            return;
        }
        foreach (var metadata in store.ListActiveProposalApplyDrafts())
        {
            var path = Path.Combine(draftPath, metadata.DraftId.ToString("N") + ".json");
            if (File.Exists(path)) TryLoad(path, metadata.DraftId);
        }
    }
    private void TryLoad(string path, Guid? expectedId)
    {
        try
        {
            var draft = JsonSerializer.Deserialize<ProposalApplyDraft>(File.ReadAllText(path));
            if (draft is null || (expectedId is not null && draft.DraftId != expectedId) || !Roots.Any(r => r.RootId == draft.RootId) || !IsTrusted(draft, requireApproved: draft.IsApproved)) return;
            drafts[draft.DraftId] = draft;
        }
        catch { }
    }
    private bool IsTrusted(ProposalApplyDraft draft, bool requireApproved)
    {
        if (store is null) return true;
        var immutable = store.GetProposalApplyImmutableMetadata(draft.DraftId);
        if (immutable is null || store.GetImprovementProposal(draft.ProposalId) is null) return false;
        var metadata = immutable.Draft;
        if (metadata.ProposalId != draft.ProposalId || metadata.ProposalRevision != draft.ProposalRevision || metadata.RootId != draft.RootId || metadata.SelectionRevision != draft.SelectionRevision || metadata.ApprovalDigest != draft.ApprovalDigest || metadata.FileCount != draft.Files.Count) return false;
        if ((metadata.State == ProposalApplyState.Approved) != draft.IsApproved || (requireApproved && (metadata.State != ProposalApplyState.Approved || immutable.Revision.ApprovedAt is null))) return false;
        if (immutable.Revision.SelectionRevision != draft.SelectionRevision || immutable.Revision.ApprovalDigest != draft.ApprovalDigest) return false;
        if (draft.Files.Any(file => LineDiff.Sha256(file.OriginalText) != file.BaseSha256 || LineDiff.Sha256(file.ReplacementText) != file.ReplacementSha256)) return false;
        var files = draft.Files.Select(file => (file.BaseSha256, file.ReplacementSha256)).ToArray();
        if (!files.SequenceEqual(immutable.Files)) return false;
        var hunks = draft.Hunks.Select(hunk => (hunk.HunkId, hunk.Selected, LineDiff.Sha256(hunk.ReplacementText))).OrderBy(hunk => hunk.HunkId, StringComparer.Ordinal).ToArray();
        if (!hunks.SequenceEqual(immutable.Hunks)) return false;
        return HasCurrentProposalRevision(draft) && Digest(draft.DraftId, draft.ProposalId, draft.ProposalRevision, draft.RootId, draft.Files, draft.Hunks, draft.SelectionRevision) == draft.ApprovalDigest;
    }
    private static IReadOnlyList<ApplyTarget> RebuildFiles(IReadOnlyList<ApplyTarget> files, IReadOnlyList<ApplyHunk> hunks) => files.Select(file => { var replacement = LineDiff.Replay(file.OriginalText, hunks.Where(h => h.Selected && h.RelativePath == file.RelativePath)); return file with { ReplacementText = replacement, ReplacementSha256 = LineDiff.Sha256(replacement) }; }).Where(file => !string.Equals(file.OriginalText, file.ReplacementText, StringComparison.Ordinal)).ToArray();
    private bool HasCurrentProposalRevision(ProposalApplyDraft draft) => store?.GetImprovementProposal(draft.ProposalId)?.Revision == draft.ProposalRevision || store is null;
    private static string Digest(Guid draftId, Guid proposalId, int proposalRevision, Guid rootId, IReadOnlyList<ApplyTarget> files, IReadOnlyList<ApplyHunk> hunks, int revision) => LineDiff.Sha256(string.Join("\n", new[] { draftId.ToString("D"), proposalId.ToString("D"), proposalRevision.ToString(CultureInfo.InvariantCulture), rootId.ToString("D"), revision.ToString(CultureInfo.InvariantCulture) }.Concat(files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => $"{f.RelativePath}|{f.BaseSha256}|{f.ReplacementSha256}")).Concat(hunks.Where(h => h.Selected).OrderBy(h => h.HunkId, StringComparer.Ordinal).Select(h => $"{h.HunkId}|{LineDiff.Sha256(h.ReplacementText)}"))));
}

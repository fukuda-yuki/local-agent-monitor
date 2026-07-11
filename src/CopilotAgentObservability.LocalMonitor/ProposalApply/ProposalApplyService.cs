using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal sealed record ProposalApplyFileInput(string RelativePath, string ReplacementText);
internal sealed record ProposalApplyDraft(Guid DraftId, Guid ProposalId, Guid RootId, int SelectionRevision, string ApprovalDigest, bool IsApproved, IReadOnlyList<ApplyTarget> Files, IReadOnlyList<ApplyHunk> Hunks);

internal sealed class ProposalApplyService
{
    private readonly Dictionary<Guid, ProposalApplyDraft> drafts = [];
    private readonly ProposalApplyTransaction transaction;

    public ProposalApplyService(IReadOnlyList<ConfiguredApplyRoot> roots, string runtimePath)
    {
        Roots = roots;
        transaction = new ProposalApplyTransaction(runtimePath);
        transaction.RecoverUncommitted();
    }

    public IReadOnlyList<ConfiguredApplyRoot> Roots { get; }

    public ProposalApplyDraft CreateDraft(Guid proposalId, Guid rootId, IReadOnlyList<ProposalApplyFileInput> inputs)
    {
        var root = Roots.SingleOrDefault(item => item.RootId == rootId) ?? throw new ApplyPathException("invalid_root_id");
        if (inputs.Count is < 1 or > 10) throw new ApplyPathException("invalid_apply_request");
        var files = inputs.Select(input =>
        {
            if (Encoding.UTF8.GetByteCount(input.ReplacementText) > 262_144) throw new ApplyPathException("request_too_large");
            var resolved = ApplyPathPolicy.Resolve(root, input.RelativePath);
            return new ApplyTarget(resolved.RelativePath, resolved.FullPath, File.ReadAllText(resolved.FullPath), input.ReplacementText, LineDiff.Sha256(File.ReadAllText(resolved.FullPath)), LineDiff.Sha256(input.ReplacementText));
        }).ToArray();
        if (files.Select(file => file.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new ApplyPathException("duplicate_target");
        var hunks = files.SelectMany(file => LineDiff.Create(file.OriginalText, file.ReplacementText)).ToArray();
        if (hunks.Length == 0) throw new ApplyPathException("invalid_selection");
        var draft = new ProposalApplyDraft(Guid.CreateVersion7(), proposalId, rootId, 1, Digest(proposalId, rootId, files, hunks, 1), false, files, hunks);
        drafts.Add(draft.DraftId, draft);
        return draft;
    }

    public ProposalApplyDraft Select(Guid draftId, int revision, IReadOnlyList<string> selectedHunkIds)
    {
        var draft = Get(draftId);
        if (draft.SelectionRevision != revision) throw new ApplyPathException("selection_stale");
        var selected = draft.Hunks.Select(hunk => hunk with { Selected = selectedHunkIds.Contains(hunk.HunkId, StringComparer.Ordinal) }).ToArray();
        if (!selected.Any(hunk => hunk.Selected)) throw new ApplyPathException("invalid_selection");
        var updated = draft with { SelectionRevision = revision + 1, Hunks = selected, IsApproved = false, ApprovalDigest = Digest(draft.ProposalId, draft.RootId, draft.Files, selected, revision + 1) };
        drafts[draftId] = updated;
        return updated;
    }

    public ProposalApplyDraft Approve(Guid draftId, int revision, string digest)
    {
        var draft = Get(draftId);
        if (draft.SelectionRevision != revision) throw new ApplyPathException("selection_stale");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(draft.ApprovalDigest), Encoding.UTF8.GetBytes(digest))) throw new ApplyPathException("approval_digest_mismatch");
        var approved = draft with { IsApproved = true };
        drafts[draftId] = approved;
        return approved;
    }

    public ApplyTransactionResult Apply(Guid draftId) => Get(draftId).IsApproved ? transaction.Apply(Guid.CreateVersion7(), Get(draftId).Files) : throw new ApplyPathException("approval_required");
    private ProposalApplyDraft Get(Guid draftId) => drafts.TryGetValue(draftId, out var draft) ? draft : throw new ApplyPathException("draft_not_found");
    private static string Digest(Guid proposalId, Guid rootId, IReadOnlyList<ApplyTarget> files, IReadOnlyList<ApplyHunk> hunks, int revision) => LineDiff.Sha256(string.Join("\n", new[] { proposalId.ToString("D"), rootId.ToString("D"), revision.ToString(CultureInfo.InvariantCulture) }.Concat(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).Select(file => $"{file.RelativePath}|{file.BaseSha256}")).Concat(hunks.Where(hunk => hunk.Selected).OrderBy(hunk => hunk.HunkId, StringComparer.Ordinal).Select(hunk => $"{hunk.HunkId}|{LineDiff.Sha256(hunk.ReplacementText)}"))));
}

using CopilotAgentObservability.LocalMonitor.ProposalApply;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ProposalApplyServiceTests
{
    [Fact]
    public void Apply_replays_only_selected_hunks_for_each_file()
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "a\nb\nc\n");
        File.WriteAllText(Path.Combine(directory.Path, "two.txt"), "x\ny\n");
        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath);
        var draft = service.CreateDraft(Guid.CreateVersion7(), service.Roots.Single().RootId,
        [
            new ProposalApplyFileInput("one.txt", "A\nb\nC\n"),
            new ProposalApplyFileInput("two.txt", "X\ny\n"),
        ]);

        var oneFirstHunk = draft.Hunks.Single(hunk => hunk.RelativePath == "one.txt" && hunk.ReplacementText == "A\n");
        var selected = service.Select(draft.DraftId, draft.SelectionRevision, [oneFirstHunk.HunkId]);
        service.Approve(selected.DraftId, selected.SelectionRevision, selected.ApprovalDigest);

        Assert.Equal(ApplyTransactionResult.Applied, service.Apply(selected.DraftId));
        Assert.Equal("A\nb\nc\n", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
        Assert.Equal("x\ny\n", File.ReadAllText(Path.Combine(directory.Path, "two.txt")));
    }

    [Fact]
    public void Root_ids_are_runtime_opaque_not_path_derived()
    {
        using var directory = new ApplyTestDirectory();
        var first = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var second = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);

        Assert.NotEqual(first.RootId, second.RootId);
    }

    [Fact]
    public void Fully_deselected_file_is_not_replaced()
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "one\n");
        File.WriteAllText(Path.Combine(directory.Path, "two.txt"), "two\n");
        var replacements = new List<string>();
        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, point => replacements.Add(point));
        var draft = service.CreateDraft(Guid.CreateVersion7(), service.Roots.Single().RootId,
        [new ProposalApplyFileInput("one.txt", "ONE\n"), new ProposalApplyFileInput("two.txt", "TWO\n")]);
        var selected = service.Select(draft.DraftId, draft.SelectionRevision, [draft.Hunks.Single(hunk => hunk.RelativePath == "one.txt").HunkId]);
        service.Approve(selected.DraftId, selected.SelectionRevision, selected.ApprovalDigest);

        Assert.Equal(ApplyTransactionResult.Applied, service.Apply(selected.DraftId));
        Assert.Equal("ONE\n", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(directory.Path, "two.txt")));
        Assert.DoesNotContain(replacements, point => point == "before_replace:1");
    }

    [Fact]
    public void Many_short_lines_use_bounded_diff_memory()
    {
        var original = string.Concat(Enumerable.Repeat("a\n", 131_072));
        var replacement = string.Concat(Enumerable.Repeat("b\n", 131_072));

        var hunk = Assert.Single(LineDiff.Create("large.txt", original, replacement));

        Assert.Equal(0, hunk.StartLine);
        Assert.Equal(replacement, LineDiff.Replay(original, [hunk]));
    }

    [Fact]
    public void Selection_change_invalidates_approval_and_changes_digest()
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "before\n");
        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath);
        var draft = service.CreateDraft(Guid.CreateVersion7(), service.Roots.Single().RootId, [new ProposalApplyFileInput("one.txt", "after\n")]);
        var approved = service.Approve(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest);

        var selected = service.Select(draft.DraftId, draft.SelectionRevision, [draft.Hunks.Single().HunkId]);

        Assert.NotEqual(approved.ApprovalDigest, selected.ApprovalDigest);
        Assert.False(selected.IsApproved);
    }

    [Fact]
    public void Draft_rejects_actual_reparse_target()
    {
        using var directory = new ApplyTestDirectory();
        var external = Path.Combine(directory.Path, "external.txt");
        File.WriteAllText(external, "content\n");
        try { _ = File.CreateSymbolicLink(Path.Combine(directory.Path, "linked.txt"), external); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Cannot create file reparse fixture: {exception.GetType().Name}");
        }

        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath);

        Assert.Equal("unsafe_reparse_path", Assert.Throws<ApplyPathException>(() => service.CreateDraft(Guid.CreateVersion7(), service.Roots.Single().RootId, [new ProposalApplyFileInput("linked.txt", "changed\n")])).Code);
    }

    [Theory]
    [InlineData("after_rollback_prepared")]
    [InlineData("after_rolled_back_journal")]
    public void Restart_after_rollback_crash_completes_one_durable_rollback_without_duplicate_audit(string boundary)
    {
        using var directory = new ApplyTestDirectory();
        var target = Path.Combine(directory.Path, "target.txt");
        File.WriteAllText(target, "before\n");
        var store = CreateStore(directory, out var proposalId);
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var initial = new ProposalApplyService([root], directory.RuntimePath, store);
        var draft = ApproveSingleFile(initial, proposalId, "target.txt", "after\n");
        var (applyId, result, _) = initial.ApplyWithId(draft.DraftId);
        Assert.Equal(ApplyTransactionResult.Applied, result);

        var crashing = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store,
            point => { if (point == boundary) throw new ApplyTransactionCrashException(); });
        Assert.Throws<ApplyTransactionCrashException>(() => crashing.Rollback(applyId));

        var restarted = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        Assert.Equal("before\n", File.ReadAllText(target));
        Assert.Equal(ProposalApplyState.RolledBack, store.GetProposalApplyDraft(draft.DraftId)!.State);
        Assert.Equal(2, AuditCount(directory.DatabasePath));
        Assert.Empty(store.ListProposalApplyPending());
        Assert.Equal(ApplyTransactionResult.RollbackUnavailable, restarted.Rollback(applyId));

        _ = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        Assert.Equal(2, AuditCount(directory.DatabasePath));
    }

    [Fact]
    public void Rollback_io_failure_after_replacement_reports_the_durable_rolled_back_outcome()
    {
        using var directory = new ApplyTestDirectory();
        const string original = "rollback-original-leak-marker\n";
        const string replacement = "rollback-replacement-leak-marker\n";
        const string targetName = "rollback-path-leak-marker.txt";
        var target = Path.Combine(directory.Path, targetName);
        File.WriteAllText(target, original);
        var store = CreateStore(directory, out var proposalId);
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var initial = new ProposalApplyService([root], directory.RuntimePath, store);
        var draft = ApproveSingleFile(initial, proposalId, targetName, replacement);
        var (applyId, applyResult, _) = initial.ApplyWithId(draft.DraftId);
        Assert.Equal(ApplyTransactionResult.Applied, applyResult);

        var failing = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store,
            point => { if (point == "after_atomic_rollback_replace:0") throw new IOException("synthetic rollback progress failure"); });

        Assert.Equal(ApplyTransactionResult.RolledBack, failing.Rollback(applyId));
        Assert.Equal(original, File.ReadAllText(target));
        Assert.Equal(ProposalApplyState.RolledBack, store.GetProposalApplyDraft(draft.DraftId)!.State);
        Assert.Equal(1, AuditCount(directory.DatabasePath, null, "rolled_back"));
        Assert.Empty(store.ListProposalApplyPending());
        Assert.Empty(store.ListAppliedProposalApplyLinkages());

        var restarted = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        Assert.Equal(ApplyTransactionResult.RollbackUnavailable, restarted.Rollback(applyId));
        Assert.Equal(1, AuditCount(directory.DatabasePath, null, "rolled_back"));
        var durableText = ReadApplyDurableText(directory.DatabasePath);
        Assert.DoesNotContain(targetName, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Trim(), durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(replacement.Trim(), durableText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_with_missing_or_corrupt_snapshot_fails_closed_and_blocks_restart(bool corruptSnapshot)
    {
        using var directory = new ApplyTestDirectory();
        const string original = "rollback-snapshot-original-marker\n";
        const string replacement = "rollback-snapshot-replacement-marker\n";
        var target = Path.Combine(directory.Path, "snapshot-target.txt");
        File.WriteAllText(target, original);
        var store = CreateStore(directory, out var proposalId);
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var service = new ProposalApplyService([root], directory.RuntimePath, store);
        var draft = ApproveSingleFile(service, proposalId, "snapshot-target.txt", replacement);
        var (applyId, applyResult, _) = service.ApplyWithId(draft.DraftId);
        Assert.Equal(ApplyTransactionResult.Applied, applyResult);

        var snapshot = Path.Combine(directory.RuntimePath, applyId.ToString("N"), "0.snapshot");
        if (corruptSnapshot) File.WriteAllText(snapshot, "corrupt snapshot\n");
        else File.Delete(snapshot);

        Assert.Equal(ApplyTransactionResult.Failed, service.Rollback(applyId));
        Assert.Equal(replacement, File.ReadAllText(target));
        Assert.NotEqual(ProposalApplyState.RolledBack, store.GetProposalApplyDraft(draft.DraftId)!.State);
        Assert.Equal(0, AuditCount(directory.DatabasePath, null, "rolled_back"));
        Assert.Empty(store.ListProposalApplyPending());
        Assert.Single(store.ListAppliedProposalApplyLinkages());

        Assert.Throws<ApplyRecoveryException>(() => _ = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store));
    }

    [Fact]
    public void Apply_stale_two_file_preflight_records_one_non_reusable_failure_without_restart_duplicate()
    {
        using var directory = new ApplyTestDirectory();
        var first = Path.Combine(directory.Path, "first.txt");
        var second = Path.Combine(directory.Path, "second.txt");
        File.WriteAllText(first, "before-first\n");
        File.WriteAllText(second, "before-second\n");
        var store = CreateStore(directory, out var proposalId);
        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        var draft = service.CreateDraft(proposalId, service.Roots.Single().RootId,
        [new ProposalApplyFileInput("first.txt", "after-first\n"), new ProposalApplyFileInput("second.txt", "after-second\n")]);
        var approved = service.Approve(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest);
        File.WriteAllText(second, "externally-stale\n");

        var (_, result, _) = service.ApplyWithId(approved.DraftId);

        Assert.Equal(ApplyTransactionResult.Stale, result);
        Assert.Equal("before-first\n", File.ReadAllText(first));
        Assert.Equal("externally-stale\n", File.ReadAllText(second));
        Assert.Equal(ProposalApplyState.Failed, store.GetProposalApplyDraft(approved.DraftId)!.State);
        Assert.Empty(store.ListProposalApplyPending());
        Assert.Empty(store.ListAppliedProposalApplyLinkages());
        Assert.Equal(1, AuditCount(directory.DatabasePath, "apply_stale"));
        Assert.Equal("approval_required", Assert.Throws<ApplyPathException>(() => service.Apply(approved.DraftId)).Code);

        var restarted = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        Assert.Equal("draft_not_found", Assert.Throws<ApplyPathException>(() => restarted.Apply(approved.DraftId)).Code);
        Assert.Equal(1, AuditCount(directory.DatabasePath, "apply_stale"));
    }

    [Fact]
    public void Restart_after_committed_apply_marker_reconciles_once_and_allows_rollback()
    {
        using var directory = new ApplyTestDirectory();
        var first = Path.Combine(directory.Path, "first.txt");
        var second = Path.Combine(directory.Path, "second.txt");
        File.WriteAllText(first, "before-first\n");
        File.WriteAllText(second, "before-second\n");
        var store = CreateStore(directory, out var proposalId);
        var initial = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store,
            point => { if (point == "after_committed_journal") throw new ApplyTransactionCrashException(); });
        var draft = initial.CreateDraft(proposalId, initial.Roots.Single().RootId,
        [new ProposalApplyFileInput("first.txt", "after-first\n"), new ProposalApplyFileInput("second.txt", "after-second\n")]);
        var approved = initial.Approve(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest);

        Assert.Throws<ApplyTransactionCrashException>(() => initial.ApplyWithId(approved.DraftId));

        var restarted = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        Assert.Equal("after-first\n", File.ReadAllText(first));
        Assert.Equal("after-second\n", File.ReadAllText(second));
        Assert.Empty(store.ListProposalApplyPending());
        Assert.Single(store.ListAppliedProposalApplyLinkages());
        Assert.Equal(1, AuditCount(directory.DatabasePath, null));
        Assert.Equal(ApplyTransactionResult.RolledBack, restarted.Rollback(store.ListAppliedProposalApplyLinkages().Single().ApplyId));
        Assert.Equal("before-first\n", File.ReadAllText(first));
        Assert.Equal("before-second\n", File.ReadAllText(second));

        _ = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        Assert.Equal(2, AuditCount(directory.DatabasePath, null));
    }

    [Theory]
    [InlineData("after_snapshot:0")]
    [InlineData("after_prepared_journal")]
    [InlineData("after_atomic_replace:0")]
    public void Restart_after_precommit_apply_crash_restores_originals_and_records_one_sanitized_failure(string boundary)
    {
        using var directory = new ApplyTestDirectory();
        var first = Path.Combine(directory.Path, "first.txt");
        var second = Path.Combine(directory.Path, "second.txt");
        File.WriteAllText(first, "before-first\n");
        File.WriteAllText(second, "before-second\n");
        var store = CreateStore(directory, out var proposalId);
        var initial = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store,
            point => { if (point == boundary) throw new ApplyTransactionCrashException(); });
        var draft = initial.CreateDraft(proposalId, initial.Roots.Single().RootId,
        [new ProposalApplyFileInput("first.txt", "after-first\n"), new ProposalApplyFileInput("second.txt", "after-second\n")]);
        var approved = initial.Approve(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest);

        Assert.Throws<ApplyTransactionCrashException>(() => initial.ApplyWithId(approved.DraftId));

        _ = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        Assert.Equal("before-first\n", File.ReadAllText(first));
        Assert.Equal("before-second\n", File.ReadAllText(second));
        Assert.Empty(store.ListProposalApplyPending());
        Assert.Empty(store.ListAppliedProposalApplyLinkages());
        Assert.Equal(ProposalApplyState.Failed, store.GetProposalApplyDraft(approved.DraftId)!.State);
        Assert.Equal(1, AuditCount(directory.DatabasePath, "apply_failed"));

        _ = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        Assert.Equal(1, AuditCount(directory.DatabasePath, "apply_failed"));
    }

    [Fact]
    public void Apply_durable_rows_do_not_contain_test_path_or_content_markers()
    {
        using var directory = new ApplyTestDirectory();
        const string rootMarker = "root-marker";
        const string pathMarker = "path-marker";
        const string sourceMarker = "source-marker";
        const string diffMarker = "diff-marker";
        const string replacementMarker = "replacement-marker";
        var rootPath = Path.Combine(directory.Path, rootMarker);
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, pathMarker + ".txt"), sourceMarker + diffMarker + "\n");
        var store = CreateStore(directory, out var proposalId);
        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath)], directory.RuntimePath, store);
        var draft = service.CreateDraft(proposalId, service.Roots.Single().RootId, [new ProposalApplyFileInput(pathMarker + ".txt", replacementMarker + "\n")]);
        var approved = service.Approve(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest);
        Assert.Equal(ApplyTransactionResult.Applied, service.Apply(approved.DraftId));

        var durableText = ReadApplyDurableText(directory.DatabasePath);
        Assert.DoesNotContain(rootMarker, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(pathMarker, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceMarker, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(diffMarker, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(replacementMarker, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot", durableText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_private_draft_without_proposal_revision_is_upgraded_to_the_migrated_durable_revision_and_has_an_active_receipt()
    {
        using var directory = new ApplyTestDirectory();
        var target = Path.Combine(directory.Path, "legacy.txt");
        File.WriteAllText(target, "before\n");
        var store = CreateStore(directory, out var proposalId);
        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        var approved = ApproveSingleFile(service, proposalId, "legacy.txt", "after\n");
        Assert.Equal(ApplyTransactionResult.Applied, service.Apply(approved.DraftId));
        Assert.Equal(1, store.GetProposalApplyDraft(approved.DraftId)!.ProposalRevision);

        var privatePath = Path.Combine(directory.RuntimePath, "drafts", approved.DraftId.ToString("N") + ".json");
        var legacy = JsonNode.Parse(File.ReadAllText(privatePath))!.AsObject();
        legacy.Remove("ProposalRevision");
        File.WriteAllText(privatePath, legacy.ToJsonString());

        var restarted = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        var migratedBytes = File.ReadAllBytes(privatePath);
        var migratedWriteTime = File.GetLastWriteTimeUtc(privatePath);
        var receipt = Assert.Single(restarted.ListApplicationReceipts(proposalId));
        var repeatedReceipt = Assert.Single(restarted.ListApplicationReceipts(proposalId));

        Assert.Equal("active", receipt.CurrentState);
        Assert.Equal("active", repeatedReceipt.CurrentState);
        Assert.Equal(1, receipt.ProposalRevision);
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(privatePath))!["ProposalRevision"]!.GetValue<int>());
        Assert.Equal(migratedBytes, File.ReadAllBytes(privatePath));
        Assert.Equal(migratedWriteTime, File.GetLastWriteTimeUtc(privatePath));
    }

    [Fact]
    public void Current_application_receipt_queries_do_not_mutate_private_draft_files()
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "current.txt"), "before\n");
        var store = CreateStore(directory, out var proposalId);
        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        var approved = ApproveSingleFile(service, proposalId, "current.txt", "after\n");
        Assert.Equal(ApplyTransactionResult.Applied, service.Apply(approved.DraftId));
        var privatePath = Path.Combine(directory.RuntimePath, "drafts", approved.DraftId.ToString("N") + ".json");
        var before = File.ReadAllBytes(privatePath);
        var writeTime = File.GetLastWriteTimeUtc(privatePath);

        Assert.Equal("active", Assert.Single(service.ListApplicationReceipts(proposalId)).CurrentState);
        Assert.Equal("active", Assert.Single(service.ListApplicationReceipts(proposalId)).CurrentState);

        Assert.Equal(before, File.ReadAllBytes(privatePath));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(privatePath));
    }

    [Fact]
    public async Task Apply_authorization_blocks_lifecycle_transition_until_completion_and_stale_transition_rejects_before_writing_files()
    {
        using var directory = new ApplyTestDirectory();
        var staleTarget = Path.Combine(directory.Path, "stale.txt");
        File.WriteAllText(staleTarget, "before-stale\n");
        var store = CreateStore(directory, out _);
        var staleProposalId = Guid.CreateVersion7();
        InsertProposal(directory.DatabasePath, staleProposalId, ImprovementProposalStatus.Recommended);
        var staleService = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store);
        var staleDraft = ApproveSingleFile(staleService, staleProposalId, "stale.txt", "after-stale\n");
        store.UpdateImprovementProposalStatus(staleProposalId, ImprovementProposalStatus.Candidate, DateTimeOffset.UnixEpoch);

        var stale = Assert.Throws<ApplyPathException>(() => staleService.Apply(staleDraft.DraftId));

        Assert.Equal("approval_required", stale.Code);
        Assert.Equal("before-stale\n", File.ReadAllText(staleTarget));
        Assert.Empty(store.ListProposalApplyPending());
        Assert.Equal(0, AuditCount(directory.DatabasePath));

        var target = Path.Combine(directory.Path, "concurrent.txt");
        File.WriteAllText(target, "before-concurrent\n");
        var proposalId = Guid.CreateVersion7();
        InsertProposal(directory.DatabasePath, proposalId, ImprovementProposalStatus.Recommended);
        using var authorized = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var service = new ProposalApplyService([ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)], directory.RuntimePath, store,
            point => { if (point == "after_prepared_journal") { authorized.Set(); release.Wait(TimeSpan.FromSeconds(10)); } });
        var approved = ApproveSingleFile(service, proposalId, "concurrent.txt", "after-concurrent\n");

        var apply = Task.Run(() => service.ApplyWithId(approved.DraftId));
        Assert.True(authorized.Wait(TimeSpan.FromSeconds(10)), "Apply did not reach the authorized barrier.");
        Assert.Throws<InvalidOperationException>(() => store.UpdateImprovementProposalStatus(proposalId, ImprovementProposalStatus.Candidate, DateTimeOffset.UnixEpoch.AddMinutes(1)));
        Assert.Equal(1, store.GetImprovementProposal(proposalId)!.Revision);
        Assert.Equal(ImprovementProposalStatus.Recommended, store.GetImprovementProposal(proposalId)!.Status);

        release.Set();
        Assert.Equal(ApplyTransactionResult.Applied, (await apply).Result);
        store.UpdateImprovementProposalStatus(proposalId, ImprovementProposalStatus.Candidate, DateTimeOffset.UnixEpoch.AddMinutes(2));
        Assert.Equal(2, store.GetImprovementProposal(proposalId)!.Revision);
        Assert.Equal(ImprovementProposalStatus.Candidate, store.GetImprovementProposal(proposalId)!.Status);
    }

    private sealed class ApplyTestDirectory : IDisposable
    {
        public ApplyTestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cao-apply-{Guid.NewGuid():N}");
            RuntimePath = System.IO.Path.Combine(Path, "runtime");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string RuntimePath { get; }
        public string DatabasePath => System.IO.Path.Combine(Path, "monitor.db");
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, true);
        }
    }

    private static SqliteSessionStore CreateStore(ApplyTestDirectory directory, out Guid proposalId)
    {
        var store = new SqliteSessionStore(directory.DatabasePath);
        store.CreateSchema();
        proposalId = Guid.CreateVersion7();
        using var connection = new SqliteConnection($"Data Source={directory.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO improvement_proposals(proposal_id,status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at) VALUES($id,'candidate','skill','fixture','fixture','fixture','fixture','fixture','2026-07-12T00:00:00+00:00','2026-07-12T00:00:00+00:00');";
        command.Parameters.AddWithValue("$id", proposalId.ToString("D"));
        command.ExecuteNonQuery();
        return store;
    }

    private static void InsertProposal(string databasePath, Guid proposalId, ImprovementProposalStatus status)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO improvement_proposals(proposal_id,status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at,recommended_at) VALUES($id,$status,'skill','fixture','fixture','fixture','fixture','fixture','2026-07-12T00:00:00+00:00','2026-07-12T00:00:00+00:00',$recommended);";
        command.Parameters.AddWithValue("$id", proposalId.ToString("D"));
        command.Parameters.AddWithValue("$status", status == ImprovementProposalStatus.Recommended ? "recommended" : "candidate");
        command.Parameters.AddWithValue("$recommended", status == ImprovementProposalStatus.Recommended ? "2026-07-12T00:00:00+00:00" : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static ProposalApplyDraft ApproveSingleFile(ProposalApplyService service, Guid proposalId, string relativePath, string replacement)
    {
        var draft = service.CreateDraft(proposalId, service.Roots.Single().RootId, [new ProposalApplyFileInput(relativePath, replacement)]);
        return service.Approve(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest);
    }

    private static long AuditCount(string databasePath, string? errorCode = null, string? state = null)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = errorCode is not null ? "SELECT COUNT(*) FROM proposal_apply_audit WHERE error_code=$error;" : state is not null ? "SELECT COUNT(*) FROM proposal_apply_audit WHERE state=$state;" : "SELECT COUNT(*) FROM proposal_apply_audit;";
        if (errorCode is not null) command.Parameters.AddWithValue("$error", errorCode);
        if (state is not null) command.Parameters.AddWithValue("$state", state);
        return (long)command.ExecuteScalar()!;
    }

    private static string ReadApplyDurableText(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(quote(draft_id) || quote(proposal_id) || quote(root_id) || quote(approval_digest) || quote(state) || quote(error_code), '|') FROM (SELECT draft_id, proposal_id, root_id, approval_digest, state, NULL AS error_code FROM proposal_apply_drafts UNION ALL SELECT draft_id, NULL, NULL, base_sha256 || replacement_sha256, NULL, NULL FROM proposal_apply_files UNION ALL SELECT draft_id, NULL, NULL, hunk_id || replacement_sha256, NULL, NULL FROM proposal_apply_hunks UNION ALL SELECT draft_id, NULL, NULL, NULL, state, NULL FROM proposal_applies UNION ALL SELECT draft_id, proposal_id, root_id, NULL, state, error_code FROM proposal_apply_audit UNION ALL SELECT draft_id, proposal_id, root_id, operation_kind, NULL, NULL FROM proposal_apply_pending);";
        return command.ExecuteScalar() as string ?? string.Empty;
    }
}

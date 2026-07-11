using CopilotAgentObservability.LocalMonitor.ProposalApply;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

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

    private static ProposalApplyDraft ApproveSingleFile(ProposalApplyService service, Guid proposalId, string relativePath, string replacement)
    {
        var draft = service.CreateDraft(proposalId, service.Roots.Single().RootId, [new ProposalApplyFileInput(relativePath, replacement)]);
        return service.Approve(draft.DraftId, draft.SelectionRevision, draft.ApprovalDigest);
    }

    private static long AuditCount(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM proposal_apply_audit;";
        return (long)command.ExecuteScalar()!;
    }
}

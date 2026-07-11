using CopilotAgentObservability.LocalMonitor.ProposalApply;

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
        public void Dispose() => Directory.Delete(Path, true);
    }
}

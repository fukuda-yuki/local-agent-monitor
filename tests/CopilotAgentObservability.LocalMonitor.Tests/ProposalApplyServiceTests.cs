using CopilotAgentObservability.LocalMonitor.ProposalApply;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ProposalApplyServiceTests
{
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

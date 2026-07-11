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

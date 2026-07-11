using CopilotAgentObservability.LocalMonitor.ProposalApply;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ProposalApplyTransactionTests
{
    [Fact]
    public void Stale_second_target_changes_neither_target()
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "one");
        File.WriteAllText(Path.Combine(directory.Path, "two.txt"), "two");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var transaction = new ProposalApplyTransaction(directory.RuntimePath);
        var files = new[]
        {
            ApplyTarget.Create(root, "one.txt", "one", "ONE"),
            ApplyTarget.Create(root, "two.txt", "two", "TWO"),
        };
        File.WriteAllText(Path.Combine(directory.Path, "two.txt"), "changed");

        Assert.Equal(ApplyTransactionResult.Stale, transaction.Apply(Guid.CreateVersion7(), files));
        Assert.Equal("one", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(directory.Path, "two.txt")));
    }

    [Fact]
    public void Fault_after_replacement_restores_original()
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "one");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var transaction = new ProposalApplyTransaction(directory.RuntimePath, point => { if (point == "after_replace") throw new IOException("synthetic"); });

        Assert.Equal(ApplyTransactionResult.Failed, transaction.Apply(Guid.CreateVersion7(), [ApplyTarget.Create(root, "one.txt", "one", "ONE")]));
        Assert.Equal("one", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
    }

    [Fact]
    public void Mutation_between_preflight_and_replace_is_not_overwritten()
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "one");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var transaction = new ProposalApplyTransaction(directory.RuntimePath, point =>
        {
            if (point == "before_replace:0") File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "external");
        });

        var result = transaction.Apply(Guid.CreateVersion7(), [ApplyTarget.Create(root, "one.txt", "one", "ONE")]);

        Assert.Equal(ApplyTransactionResult.Failed, result);
        Assert.Equal("external", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
    }

    [Fact]
    public void Rollback_requires_post_apply_hash_and_is_available_once()
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "one");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var transaction = new ProposalApplyTransaction(directory.RuntimePath);
        var id = Guid.CreateVersion7();
        Assert.Equal(ApplyTransactionResult.Applied, transaction.Apply(id, [ApplyTarget.Create(root, "one.txt", "one", "ONE")]));
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "changed");
        Assert.Equal(ApplyTransactionResult.RollbackStale, transaction.Rollback(id));
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "ONE");
        Assert.Equal(ApplyTransactionResult.RolledBack, transaction.Rollback(id));
        Assert.Equal("one", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
        Assert.Equal(ApplyTransactionResult.RollbackUnavailable, transaction.Rollback(id));
    }

    [Theory]
    [InlineData("after_prepared_journal")]
    [InlineData("after_atomic_replace:0")]
    [InlineData("after_atomic_replace:1")]
    public void Restart_after_atomic_apply_replace_restores_all_originals(string boundary)
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "one");
        File.WriteAllText(Path.Combine(directory.Path, "two.txt"), "two");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var id = Guid.CreateVersion7();
        var crashing = new ProposalApplyTransaction(directory.RuntimePath, [root], point => { if (point == boundary) throw new ApplyTransactionCrashException(); });

        Assert.Throws<ApplyTransactionCrashException>(() => crashing.Apply(id, [ApplyTarget.Create(root, "one.txt", "one", "ONE"), ApplyTarget.Create(root, "two.txt", "two", "TWO")]));
        var restarted = new ProposalApplyTransaction(directory.RuntimePath, [ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)]);
        restarted.RecoverUncommitted();

        Assert.Equal("one", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(directory.Path, "two.txt")));
    }

    [Fact]
    public void Restart_with_changed_root_leaves_target_untouched_and_blocks_mutation()
    {
        using var directory = new ApplyTestDirectory();
        var replacementRoot = Path.Combine(directory.Path, "other");
        Directory.CreateDirectory(replacementRoot);
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "one");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var crashing = new ProposalApplyTransaction(directory.RuntimePath, [root], point => { if (point == "after_atomic_replace:0") throw new ApplyTransactionCrashException(); });
        Assert.Throws<ApplyTransactionCrashException>(() => crashing.Apply(Guid.CreateVersion7(), [ApplyTarget.Create(root, "one.txt", "one", "ONE")]));

        var restarted = new ProposalApplyTransaction(directory.RuntimePath, [ConfiguredApplyRoot.Create(ApplyRootKind.Repository, replacementRoot)]);

        restarted.RecoverUncommitted();
        Assert.Equal("ONE", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
        File.WriteAllText(Path.Combine(replacementRoot, "other.txt"), "other");
        var changedRoot = restarted.ConfiguredRoots.Single();
        Assert.Equal(ApplyTransactionResult.Failed, restarted.Apply(Guid.CreateVersion7(), [ApplyTarget.Create(changedRoot, "other.txt", "other", "OTHER")]));
    }

    [Theory]
    [InlineData("after_rollback_prepared")]
    [InlineData("after_atomic_rollback_replace:0")]
    [InlineData("after_atomic_rollback_replace:1")]
    public void Restart_after_atomic_rollback_replace_restores_all_originals(string boundary)
    {
        using var directory = new ApplyTestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "one.txt"), "one");
        File.WriteAllText(Path.Combine(directory.Path, "two.txt"), "two");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path);
        var id = Guid.CreateVersion7();
        var transaction = new ProposalApplyTransaction(directory.RuntimePath, [root], point => { if (point == boundary) throw new ApplyTransactionCrashException(); });
        Assert.Equal(ApplyTransactionResult.Applied, transaction.Apply(id, [ApplyTarget.Create(root, "one.txt", "one", "ONE"), ApplyTarget.Create(root, "two.txt", "two", "TWO")]));

        Assert.Throws<ApplyTransactionCrashException>(() => transaction.Rollback(id));
        var restarted = new ProposalApplyTransaction(directory.RuntimePath, [ConfiguredApplyRoot.Create(ApplyRootKind.Repository, directory.Path)]);
        restarted.RecoverUncommitted();

        Assert.Equal("one", File.ReadAllText(Path.Combine(directory.Path, "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(directory.Path, "two.txt")));
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

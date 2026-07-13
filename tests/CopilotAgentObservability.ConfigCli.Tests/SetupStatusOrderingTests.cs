using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Status;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupStatusOrderingTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Project_PrioritizesRecoveryRowsThenPlannedThenTerminalAndPreservesTargetOrder()
    {
        var rows = new[]
        {
            Row("00000000-0000-7000-8000-000000000009", SetupChangeSetState.Applied, 9),
            Row("00000000-0000-7000-8000-000000000008", SetupChangeSetState.Planned, 8),
            Row("00000000-0000-7000-8000-000000000007", SetupChangeSetState.RollingBack, 7),
            Row("00000000-0000-7000-8000-000000000006", SetupChangeSetState.Restored, 6),
            Row("00000000-0000-7000-8000-000000000005", SetupChangeSetState.Compensating, 5),
            Row("00000000-0000-7000-8000-000000000004", SetupChangeSetState.NoChanges, 4),
            Row("00000000-0000-7000-8000-000000000003", SetupChangeSetState.Applying, 3),
            Row("00000000-0000-7000-8000-000000000002", SetupChangeSetState.RolledBack, 2),
            Row("00000000-0000-7000-8000-000000000001", SetupChangeSetState.Partial, 1),
        };

        var projected = SetupStatusListProjector.Project(StatusResult(), rows, null, null, Project);

        Assert.Equal(new[]
        {
            "00000000-0000-7000-8000-000000000007",
            "00000000-0000-7000-8000-000000000005",
            "00000000-0000-7000-8000-000000000003",
            "00000000-0000-7000-8000-000000000001",
            "00000000-0000-7000-8000-000000000008",
            "00000000-0000-7000-8000-000000000009",
            "00000000-0000-7000-8000-000000000006",
            "00000000-0000-7000-8000-000000000004",
            "00000000-0000-7000-8000-000000000002",
        }, projected.ChangeSets.Select(changeSet => changeSet.ChangeSetId));
        Assert.All(projected.ChangeSets, changeSet =>
            Assert.Equal(new[] { "first", "second" }, changeSet.Targets.Select(target => target.TargetLabel)));
        Assert.False(projected.Truncated);
    }

    [Fact]
    public void Project_UsesLowercaseCanonicalUuidOrdinalTieBreakWithinEqualPriorityAndTimestamp()
    {
        var rows = new[]
        {
            Row("00000000-0000-7000-8000-00000000000b", SetupChangeSetState.Applying, 1),
            Row("00000000-0000-7000-8000-00000000000a", SetupChangeSetState.Partial, 1),
        };

        var projected = SetupStatusListProjector.Project(StatusResult(), rows, null, null, Project);

        Assert.Equal(new[]
        {
            "00000000-0000-7000-8000-00000000000a",
            "00000000-0000-7000-8000-00000000000b",
        }, projected.ChangeSets.Select(changeSet => changeSet.ChangeSetId));
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void Project_CapsEligibleRowsAtOneHundredAndSetsTruncatedFromEligibleCount(int count, bool expectedTruncated)
    {
        var rows = Enumerable.Range(1, count)
            .Select(index => Row($"00000000-0000-7000-8000-{index:D12}", SetupChangeSetState.Planned, index))
            .Reverse()
            .ToArray();

        var projected = SetupStatusListProjector.Project(StatusResult(), rows, null, null, Project);

        Assert.Equal(Math.Min(count, 100), projected.ChangeSets.Count);
        Assert.Equal(expectedTruncated, projected.Truncated);
        Assert.Equal($"00000000-0000-7000-8000-{count:D12}", projected.ChangeSets[0].ChangeSetId);
    }

    [Fact]
    public void Project_AppliesExactAdapterAndChangeSetFiltersBeforePriorityCapAndProjection()
    {
        var selected = Row("00000000-0000-7000-8000-000000000111", SetupChangeSetState.Applied, 1, "github-copilot");
        var rows = Enumerable.Range(1, 100)
            .Select(index => Row($"00000000-0000-7000-8000-{index:D12}", SetupChangeSetState.Partial, index, "other-adapter"))
            .Append(selected)
            .ToArray();
        var projectedIds = new List<Guid>();

        var projected = SetupStatusListProjector.Project(
            StatusResult(),
            rows,
            "github-copilot",
            "00000000-0000-7000-8000-000000000111",
            changeSet =>
            {
                projectedIds.Add(changeSet.ChangeSetId);
                return Project(changeSet);
            });

        var entry = Assert.Single(projected.ChangeSets);
        Assert.Equal(selected.ChangeSetId.ToString("D"), entry.ChangeSetId);
        Assert.Equal(new[] { selected.ChangeSetId }, projectedIds);
        Assert.False(projected.Truncated);
    }

    [Theory]
    [InlineData("GitHub-Copilot", null)]
    [InlineData("github-copilot", "00000000-0000-7000-8000-0000000000AA")]
    [InlineData("github-copilot", "00000000-0000-6000-8000-0000000000aa")]
    public void Project_RejectsNonCanonicalFilters(string adapter, string? changeSetId)
    {
        var row = Row("00000000-0000-7000-8000-000000000001", SetupChangeSetState.Planned, 1);

        Assert.Throws<FormatException>(() => SetupStatusListProjector.Project(StatusResult(), [row], adapter, changeSetId, Project));
    }

    [Fact]
    public void Project_RejectsNonVersionSevenLedgerId()
    {
        var row = Row("00000000-0000-6000-8000-000000000001", SetupChangeSetState.Planned, 1);

        Assert.Throws<FormatException>(() => SetupStatusListProjector.Project(StatusResult(), [row], null, null, Project));
    }

    private static SetupLedgerChangeSet Row(string id, SetupChangeSetState state, int seconds, string adapter = "github-copilot") => new(
        Guid.ParseExact(id, "D"),
        adapter,
        "vscode",
        Timestamp,
        Timestamp.AddSeconds(seconds),
        "1.0.0",
        null,
        state,
        []);

    private static SetupCommandResult StatusResult() => new(
        SetupCommand.Status,
        true,
        SetupCodes.StatusReady,
        null,
        null,
        null,
        null,
        [],
        [],
        [],
        [],
        false);

    private static SetupChangeSetStatusResult Project(SetupLedgerChangeSet changeSet) => new(
        changeSet.ChangeSetId.ToString("D"),
        changeSet.Adapter,
        changeSet.SelectedTarget,
        changeSet.CreatedAt.ToString("O"),
        changeSet.UpdatedAt.ToString("O"),
        changeSet.State,
        changeSet.OutcomeCode,
        SetupCurrentState.NotApplicable,
        false,
        [
            Target("first"),
            Target("second"),
        ]);

    private static SetupTargetResult Target(string label) => new(
        "00000000-0000-7000-8000-000000000001",
        SetupTargetKind.Guidance,
        label,
        false,
        null,
        SetupOperation.NoOp,
        null,
        SetupReferenceState.None,
        SetupCurrentState.NotApplicable,
        SetupRestartRequirement.None,
        false,
        null,
        null,
        new SetupGuidance("caller_managed_sample", "dotnet", string.Empty),
        []);
}

using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Status;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupStatusOrderingTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> InvalidAdapterIds => new()
    {
        string.Empty,
        "UPPERCASE",
        "adapter_name",
        "-adapter",
        "adapter-",
        "adapter--name",
        "adapter-é",
        new string('a', 129),
    };

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
        var projectedIds = new List<Guid>();

        var projected = SetupStatusListProjector.Project(
            StatusResult(),
            rows,
            null,
            null,
            changeSet =>
            {
                projectedIds.Add(changeSet.ChangeSetId);
                return Project(changeSet);
            });

        Assert.Equal(Math.Min(count, 100), projected.ChangeSets.Count);
        Assert.Equal(Math.Min(count, 100), projectedIds.Count);
        Assert.Equal(expectedTruncated, projected.Truncated);
        Assert.Equal($"00000000-0000-7000-8000-{count:D12}", projected.ChangeSets[0].ChangeSetId);
        if (count == 101)
        {
            Assert.DoesNotContain(Guid.ParseExact("00000000-0000-7000-8000-000000000001", "D"), projectedIds);
        }
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

    [Fact]
    public void Project_FilteredResultUsesAuthoritativeAdapterInValidStatusWireObject()
    {
        var row = Row("00000000-0000-7000-8000-000000000111", SetupChangeSetState.Planned, 1);

        var projected = SetupStatusListProjector.Project(
            StatusResult(),
            [row],
            "github-copilot",
            null,
            Project);

        Assert.Equal("github-copilot", projected.Adapter);
        using var document = JsonDocument.Parse(SetupJson.Serialize(projected));
        Assert.Equal("github-copilot", document.RootElement.GetProperty("adapter").GetString());
        var targets = document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            ["first", "second"],
            targets.Select(target => target.GetProperty("target_label").GetString()));
        Assert.All(targets, target =>
            Assert.Equal(JsonValueKind.Null, target.GetProperty("guidance").ValueKind));
        Assert.All(projected.ChangeSets[0].Targets, target =>
            Assert.Null(target.Guidance));
    }

    [Fact]
    public void Project_DigitLeadingAdapterFiltersHistoricalLedgerExactlyAndSerializes()
    {
        var selected = Row("00000000-0000-7000-8000-000000000001", SetupChangeSetState.Planned, 1, "1");
        var other = Row("00000000-0000-7000-8000-000000000002", SetupChangeSetState.Planned, 2, "2");

        var projected = SetupStatusListProjector.Project(StatusResult(), [other, selected], "1", null, Project);
        using var document = JsonDocument.Parse(SetupJson.Serialize(projected));

        Assert.Equal("1", projected.Adapter);
        Assert.Equal(selected.ChangeSetId.ToString("D"), Assert.Single(projected.ChangeSets).ChangeSetId);
        Assert.Equal("1", document.RootElement.GetProperty("adapter").GetString());
        Assert.Equal("1", document.RootElement.GetProperty("change_sets")[0].GetProperty("adapter").GetString());
    }

    [Fact]
    public void Project_MaximumLengthAdapterFiltersHistoricalLedgerExactlyAndSerializes()
    {
        var adapter = $"1-{new string('a', 126)}";
        var selected = Row("00000000-0000-7000-8000-000000000001", SetupChangeSetState.Planned, 1, adapter);
        var other = Row("00000000-0000-7000-8000-000000000002", SetupChangeSetState.Planned, 2);

        var projected = SetupStatusListProjector.Project(StatusResult(), [other, selected], adapter, null, Project);
        using var document = JsonDocument.Parse(SetupJson.Serialize(projected));

        Assert.Equal(128, adapter.Length);
        Assert.Equal(adapter, projected.Adapter);
        Assert.Equal(adapter, Assert.Single(projected.ChangeSets).Adapter);
        Assert.Equal(adapter, document.RootElement.GetProperty("adapter").GetString());
        Assert.Equal(adapter, document.RootElement.GetProperty("change_sets")[0].GetProperty("adapter").GetString());
    }

    [Fact]
    public void Project_NoAdapterFilterClearsStaleInputAdapterWithoutMutatingInputs()
    {
        var statusResult = StatusResult("stale-adapter");
        var originalResultTargets = statusResult.Targets;
        var originalResultChangeSets = statusResult.ChangeSets;
        var rows = new[]
        {
            Row("00000000-0000-7000-8000-000000000002", SetupChangeSetState.Planned, 2),
            Row("00000000-0000-7000-8000-000000000001", SetupChangeSetState.Applied, 1),
        };
        var originalRows = rows.ToArray();

        var projected = SetupStatusListProjector.Project(statusResult, rows, null, null, Project);

        Assert.Null(projected.Adapter);
        Assert.Equal("stale-adapter", statusResult.Adapter);
        Assert.Same(originalResultTargets, statusResult.Targets);
        Assert.Same(originalResultChangeSets, statusResult.ChangeSets);
        Assert.Equal(originalRows, rows);
        Assert.All(rows.Select((row, index) => (row, index)), item =>
            Assert.Same(originalRows[item.index], item.row));
        Assert.All(rows.Select((row, index) => (row, index)), item =>
            Assert.Same(originalRows[item.index].Targets, item.row.Targets));
    }

    [Fact]
    public void Project_AdapterFilterOverwritesMismatchedInputAdapter()
    {
        var statusResult = StatusResult("other-adapter");
        var row = Row("00000000-0000-7000-8000-000000000001", SetupChangeSetState.Planned, 1);

        var projected = SetupStatusListProjector.Project(
            statusResult,
            [row],
            "github-copilot",
            null,
            Project);

        Assert.Equal("github-copilot", projected.Adapter);
        Assert.Equal("other-adapter", statusResult.Adapter);
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

    [Fact]
    public void Project_RejectsNonCanonicalLedgerAdapter()
    {
        var row = Row(
            "00000000-0000-7000-8000-000000000001",
            SetupChangeSetState.Planned,
            1,
            "GitHub-Copilot");

        Assert.Throws<FormatException>(() =>
            SetupStatusListProjector.Project(StatusResult(), [row], null, null, Project));
    }

    [Theory]
    [MemberData(nameof(InvalidAdapterIds))]
    public void Project_RejectsMalformedAdapterFilter(string adapter)
    {
        var row = Row("00000000-0000-7000-8000-000000000001", SetupChangeSetState.Planned, 1);

        Assert.Throws<FormatException>(() =>
            SetupStatusListProjector.Project(StatusResult(), [row], adapter, null, Project));
    }

    [Theory]
    [MemberData(nameof(InvalidAdapterIds))]
    public void Project_RejectsMalformedHistoricalLedgerAdapter(string adapter)
    {
        var row = Row(
            "00000000-0000-7000-8000-000000000001",
            SetupChangeSetState.Planned,
            1,
            adapter);

        Assert.Throws<FormatException>(() =>
            SetupStatusListProjector.Project(StatusResult(), [row], null, null, Project));
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

    private static SetupCommandResult StatusResult(string? adapter = null) => new(
        SetupCommand.Status,
        true,
        SetupCodes.StatusReady,
        null,
        null,
        null,
        adapter,
        [],
        [],
        [],
        [],
        false);

    private static SetupChangeSetStatusResult Project(SetupLedgerChangeSet changeSet) => new(
        changeSet.ChangeSetId.ToString("D"),
        changeSet.Adapter,
        changeSet.SelectedTarget,
        SetupStorageJson.FormatTimestamp(changeSet.CreatedAt),
        SetupStorageJson.FormatTimestamp(changeSet.UpdatedAt),
        changeSet.State,
        changeSet.OutcomeCode,
        SetupCurrentState.Current,
        false,
        [
            Target("first", changeSet.State),
            Target("second", changeSet.State),
        ]);

    private static SetupTargetResult Target(string label, SetupChangeSetState state) => new(
        "00000000-0000-7000-8000-000000000001",
        SetupTargetKind.File,
        label,
        false,
        null,
        SetupOperation.NoOp,
        null,
        state switch
        {
            SetupChangeSetState.Planned => SetupReferenceState.Base,
            SetupChangeSetState.Restored or SetupChangeSetState.RolledBack => SetupReferenceState.Previous,
            SetupChangeSetState.Applying or SetupChangeSetState.Compensating or SetupChangeSetState.RollingBack => SetupReferenceState.Base,
            _ => SetupReferenceState.Desired,
        },
        SetupCurrentState.Current,
        SetupRestartRequirement.None,
        false,
        null,
        null,
        null,
        [new SetupMemberChangeResult("ordering.setting", SetupOperation.NoOp, "present_equal", "present_equal", "none", false)]);
}

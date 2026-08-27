using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionDetailApplicationTests
{
    [Fact]
    public void SerializerContractPreservesRecordedSkillZeroAndOptionalRichTokenComponentsWithoutInventingProductionEvidence()
    {
        var recordedZero = new LocalWorkspaceFact<long>("recorded", 0);
        var tokens = new LocalWorkspaceTokenFacts("llm_span", "recorded", 1, 1,
            new("recorded", 10), new("recorded", 5), new("recorded", 15), new("recorded", 2),
            new("recorded", 4), new("recorded", 1), new("recorded", 6), new("recorded", 4000));
        var row = new LocalWorkspaceProjectionRow("018f0000-0000-7000-8000-000000000001", 0, 0, "recorded", "instruction",
            "completed", "full", new("recorded", ["vscode"]), new("recorded", ["gpt-5.6-sol"]),
            new(recordedZero, recordedZero, recordedZero, recordedZero, recordedZero), tokens, "recorded",
            "2026-08-26T01:02:03.0000000+00:00", "2026-08-26T01:02:04.0000000+00:00",
            "2026-08-26T01:02:04.0000000+00:00", 1000, [], "serializer-contract");
        var snapshot = new LocalRepositorySessionDetailSnapshot(
            new(row.SessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned,
                LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null),
            new([], [], [], [], [], [], null, null, "canonical", "generation"), new string('1', 64));

        var json = System.Text.Encoding.UTF8.GetString(LocalMonitorV1SessionDetailApplication.SerializeSummary(snapshot));

        Assert.Contains("\"skill\":{\"state\":\"recorded\",\"count\":0}", json, StringComparison.Ordinal);
        Assert.Contains("\"reasoning\":{\"state\":\"recorded\",\"value\":2}", json, StringComparison.Ordinal);
        Assert.Contains("\"new_input\":{\"state\":\"recorded\",\"value\":6}", json, StringComparison.Ordinal);
        Assert.Contains("\"cache_read_ratio_basis_points\":{\"state\":\"recorded\",\"value\":4000}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TimelineCursor_RoundTripsTheExact119ByteFrame()
    {
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var filter = new LocalMonitorV1TimelineFilter(
            "018f0000-0000-7000-8000-000000000001",
            new string('1', 64),
            "018f0000-0000-7000-8000-000000000003",
            null,
            100);
        var position = new LocalMonitorV1TimelinePosition(
            0,
            638918245230000000,
            7,
            "node-00000000000000000000000000000002");

        var cursor = LocalMonitorV1TimelineCursor.Encode(key, filter, position);

        Assert.Equal(159, cursor.Length);
        Assert.True(LocalMonitorV1TimelineCursor.TryDecode(cursor, key, filter, out var decoded));
        Assert.Equal(position, decoded);
        Assert.False(LocalMonitorV1TimelineCursor.TryDecode(cursor, key, filter with { Limit = 101 }, out _));
    }
}

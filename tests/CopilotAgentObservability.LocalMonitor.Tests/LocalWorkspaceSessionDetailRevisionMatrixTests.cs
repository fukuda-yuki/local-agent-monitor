using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceSessionDetailRevisionMatrixTests
{
    public static TheoryData<string> RevisionInputs => new()
    {
        "session_row", "assignment", "session_archive", "repository_archive",
        "run_event_span_projection", "skill_generation", "skill_current_fact",
        "retention_state", "raw_availability_binding"
    };

    [Theory]
    [MemberData(nameof(RevisionInputs))]
    public void EveryContractRevisionInputChangesTheProductionDigestInIsolation(string input)
    {
        var original = Snapshot();
        var changed = input switch
        {
            "session_row" => original with { Session = original.Session with { Session = ((LocalWorkspaceProjectionRow)original.Session.Session) with { Status = "active" } } },
            "assignment" => original with { Session = original.Session with { AssignmentRevision = 2 } },
            "session_archive" => original with { Session = original.Session with { ArchiveRevision = 3 } },
            "repository_archive" => original with { Session = original.Session with { AssignedRepositoryArchiveRevision = 4 } },
            "run_event_span_projection" => original with { Detail = original.Detail with { CanonicalRevisionInput = "run-event-span-node-edge:v2" } },
            "skill_generation" => original with { Detail = original.Detail with { SkillRegistryGenerationIdentity = "generation:v2" } },
            "skill_current_fact" => original with { Detail = original.Detail with { CanonicalRevisionInput = "canonical-current-skill:v2" } },
            "retention_state" => original with { Detail = original.Detail with { CanonicalRevisionInput = "retention:expired" } },
            "raw_availability_binding" => original with { Detail = original.Detail with { CanonicalRevisionInput = "raw:unavailable" } },
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };

        Assert.NotEqual(Revision(original), Revision(changed));
    }

    [Fact]
    public void IdenticalCoherentSnapshotHasTheSameRevision()
    {
        var snapshot = Snapshot();
        Assert.Equal(Revision(snapshot), Revision(snapshot with { }));
    }

    [Fact]
    public void CompleteJsonEntityAcceptsExactlyEightMiBAndRejectsTheNextByte()
    {
        var baseline = Snapshot(label: string.Empty);
        var baselineLength = LocalMonitorV1SessionDetailApplication.SerializeSummary(baseline).Length;
        var accepted = Snapshot(label: new string('a', 8_388_608 - baselineLength));

        Assert.Equal(8_388_608, LocalMonitorV1SessionDetailApplication.SerializeSummary(accepted).Length);

        var rejected = accepted with
        {
            Session = accepted.Session with
            {
                Session = ((LocalWorkspaceProjectionRow)accepted.Session.Session) with
                {
                    LabelText = new string('a', 8_388_609 - baselineLength),
                },
            },
        };
        var error = Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeSummary(rejected));
        Assert.Equal("workspace_too_large", error.Error);
    }

    private static string Revision(LocalRepositorySessionDetailSnapshot snapshot) =>
        SqliteLocalRepositoryScopeSnapshotService.ComputeRevisionForTest(snapshot.Session, snapshot.Detail);

    private static LocalRepositorySessionDetailSnapshot Snapshot(string? label = null)
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null);
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var activity = new LocalWorkspaceActivityFacts(zero, zero, zero, zero, zero);
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none);
        var row = new LocalWorkspaceProjectionRow(SessionId, 1, 1, label is null ? "not_observed" : "recorded", label, "completed", "full",
            new("not_observed", []), new("not_observed", []), activity, tokens,
            "missing", null, null, null, null, [], "session:v1");
        var session = new LocalRepositoryScopeSessionSnapshot(SessionId, row, 1,
            LocalRepositoryScopeAssignmentState.Assigned, LocalRepositoryScopeAssignmentAuthority.Manual,
            RepositoryId, [RepositoryId], true, true, true, LocalArchiveState.Active, 1, true, null, 1);
        var detail = new LocalWorkspaceSessionDetailContribution([], [], [], [], [], [], null, null,
            "run-event-span-node-edge-skill-retention-raw:v1", "generation:v1");
        return new(session, detail, string.Empty);
    }

    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000002";
}

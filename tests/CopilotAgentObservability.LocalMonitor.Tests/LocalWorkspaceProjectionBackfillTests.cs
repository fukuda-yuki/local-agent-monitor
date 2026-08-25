namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceProjectionBackfillTests
{
    [Fact]
    public void EnsureBackfillsCurrentSessionsAndRerunDoesNotDrift()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','completed','partial',NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00'); INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,'gpt-5','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00',10,3,NULL,'completed');");

        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var before = LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT session_id || ':' || revision_seed FROM local_workspace_sessions;");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T01:00:00Z"));

        Assert.Equal(before, LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT session_id || ':' || revision_seed FROM local_workspace_sessions;"));
        Assert.Equal(["session_run:10:3:null"], LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT authority || ':' || input_tokens || ':' || output_tokens || ':' || COALESCE(CAST(total_tokens AS TEXT),'null') FROM local_workspace_token_observations;"));
    }
}

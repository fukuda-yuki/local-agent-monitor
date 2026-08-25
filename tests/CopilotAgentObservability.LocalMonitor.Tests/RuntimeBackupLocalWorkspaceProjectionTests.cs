using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using System.Reflection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupLocalWorkspaceProjectionTests
{
    [Theory]
    [InlineData("UPDATE local_workspace_sessions SET label_text='tampered';")]
    [InlineData("UPDATE local_workspace_sessions SET label_search_text='tampered';")]
    [InlineData("UPDATE local_workspace_sessions SET revision_seed='tampered';")]
    [InlineData("UPDATE local_workspace_session_models SET model='tampered';")]
    [InlineData("UPDATE local_workspace_token_observations SET input_tokens=999;")]
    [InlineData("DELETE FROM local_workspace_session_activity WHERE kind='retry';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='unknown';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='raw_content_expired,raw_content_expired';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='raw_content_not_captured,projection_invalid';")]
    public void ValidationRejectsSemanticProjectionTampering(string mutation)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,'gpt-5',NULL,NULL,10,3,13,'active');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001',NULL,NULL,NULL,NULL,NULL,'synthetic','prompt-1','user.message','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000002','application/json','{"value":"hello"}','2026-08-24T00:00:00.0000000+00:00','2099-01-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    [Fact]
    public void ValidationAcceptsExactComponentAndRejectsTamperedOwnedSchema()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction);
            transaction.Rollback();
        }

        LocalWorkspaceProjectionSchemaTests.Execute(connection, "ALTER TABLE local_workspace_projection_state ADD COLUMN injected TEXT;");
        using var tampered = connection.BeginTransaction(deferred: true);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, tampered));
        Assert.Equal("local_workspace_projection_backup_invalid", exception.Message);
    }

    [Fact]
    public void CurrentBackupInventoryIncludesVersionAndAllSixRowCounts()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(7, LocalWorkspaceProjectionSchemaV1.TableNames.Length);
        Assert.All(LocalWorkspaceProjectionSchemaV1.TableNames, table =>
            Assert.Equal([table], LocalWorkspaceProjectionSchemaTests.Strings(connection, $"SELECT name FROM sqlite_schema WHERE type='table' AND name='{table}';")));
    }

    [Fact]
    public void MigrationOrderPlacesProjectionAfterBothSkillAuthorities()
    {
        var order = Assert.IsType<string[]>(typeof(SqliteRuntimeBackupService)
            .GetField("MigrationOrder", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        var projection = Array.IndexOf(order, "local_workspace_projection");
        Assert.True(projection > Array.IndexOf(order, "skill_projection"));
        Assert.True(projection > Array.IndexOf(order, "skill_invocation_snapshot"));
    }

    [Fact]
    public void ValidationRejectsRecordedLabelWithoutExactAuthorizedContent()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE local_workspace_sessions SET label_state='recorded',label_text='x',label_search_text='x',label_source_identity='0198f5b8-0c00-7000-8000-000000000099',label_expires_at='2099-01-01T00:00:00.0000000+00:00';");

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }
}

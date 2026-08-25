using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupLocalWorkspaceProjectionTests
{
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

        Assert.Equal(6, LocalWorkspaceProjectionSchemaV1.TableNames.Length);
        Assert.All(LocalWorkspaceProjectionSchemaV1.TableNames, table =>
            Assert.Equal([table], LocalWorkspaceProjectionSchemaTests.Strings(connection, $"SELECT name FROM sqlite_schema WHERE type='table' AND name='{table}';")));
    }
}

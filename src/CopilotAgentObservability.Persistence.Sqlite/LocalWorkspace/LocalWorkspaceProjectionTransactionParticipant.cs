using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ILocalWorkspaceProjectionTransactionParticipant
{
    void RefreshSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset now);
}

internal sealed class LocalWorkspaceProjectionTransactionParticipant : ILocalWorkspaceProjectionTransactionParticipant
{
    internal static LocalWorkspaceProjectionTransactionParticipant Instance { get; } = new();

    private LocalWorkspaceProjectionTransactionParticipant() { }

    public void RefreshSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset now)
    {
        if (sessionIds.Count == 0 || !IsInstalled(connection, transaction)) return;
        LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction, sessionIds, now);
    }

    private static bool IsInstalled(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_version WHERE component='local_workspace_projection' AND version=1);";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}

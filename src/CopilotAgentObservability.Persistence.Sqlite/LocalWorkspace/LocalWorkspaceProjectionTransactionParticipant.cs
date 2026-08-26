using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ILocalWorkspaceProjectionTransactionParticipant
{
    void RefreshSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset now);

    void CompleteSessionEventContentDeletion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceItemId,
        DateTimeOffset now);
}

internal sealed class UnconfiguredLocalWorkspaceProjectionTransactionParticipant : ILocalWorkspaceProjectionTransactionParticipant
{
    internal static UnconfiguredLocalWorkspaceProjectionTransactionParticipant Instance { get; } = new();

    private UnconfiguredLocalWorkspaceProjectionTransactionParticipant() { }

    public void RefreshSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset now)
    {
        if (sessionIds.Count != 0 && IsInstalled(connection, transaction))
            throw new InvalidOperationException("local_workspace_projection_authority_unavailable");
    }

    public void CompleteSessionEventContentDeletion(SqliteConnection connection, SqliteTransaction transaction, string sourceItemId, DateTimeOffset now)
    {
        if (IsInstalled(connection, transaction))
            throw new InvalidOperationException("local_workspace_projection_authority_unavailable");
    }

    private static bool IsInstalled(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_version WHERE component='local_workspace_projection' AND version=4);";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}

internal sealed class LocalWorkspaceProjectionTransactionParticipant : ILocalWorkspaceProjectionTransactionParticipant
{
    private readonly ISkillRegistryGenerationAuthority skillRegistryAuthority;

    internal LocalWorkspaceProjectionTransactionParticipant(ISkillRegistryGenerationAuthority skillRegistryAuthority)
    {
        this.skillRegistryAuthority = skillRegistryAuthority ?? throw new ArgumentNullException(nameof(skillRegistryAuthority));
    }

    public void RefreshSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset now)
    {
        if (sessionIds.Count == 0 || !IsInstalled(connection, transaction)) return;
        LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction, sessionIds, now, skillRegistryAuthority);
    }

    public void CompleteSessionEventContentDeletion(SqliteConnection connection, SqliteTransaction transaction, string sourceItemId, DateTimeOffset now)
    {
        if (!IsInstalled(connection, transaction)) return;
        LocalWorkspaceProjectionStore.CompleteSessionEventContentDeletion(connection, transaction, sourceItemId, now);
        using var owner = connection.CreateCommand();
        owner.Transaction = transaction;
        owner.CommandText = "SELECT session_id FROM session_events WHERE event_id=$event_id;";
        owner.Parameters.AddWithValue("$event_id", sourceItemId);
        if (owner.ExecuteScalar() is string sessionId)
            LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction, [sessionId], now, skillRegistryAuthority);
    }

    private static bool IsInstalled(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_version WHERE component='local_workspace_projection' AND version=4);";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}

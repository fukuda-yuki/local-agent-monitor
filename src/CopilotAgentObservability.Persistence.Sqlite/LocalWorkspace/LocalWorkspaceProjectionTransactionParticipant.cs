using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalWorkspaceProjectionInstallationState
{
    Absent,
    Current,
    Unsupported
}

internal interface ILocalWorkspaceProjectionTransactionParticipant
{
    void ValidateInstallationState(SqliteConnection connection, SqliteTransaction transaction) { }

    void RefreshSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset now);

    void RefreshSessionBatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<IReadOnlyCollection<string>> sessionIdBatches,
        DateTimeOffset now)
    {
        foreach (var sessionIds in sessionIdBatches)
            RefreshSessions(connection, transaction, sessionIds, now);
    }

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

    public void ValidateInstallationState(SqliteConnection connection, SqliteTransaction transaction)
    {
        var state = LocalWorkspaceProjectionSchemaV1.ReadInstallationState(connection, transaction);
        if (state == LocalWorkspaceProjectionInstallationState.Unsupported)
            throw new InvalidOperationException("local_workspace_projection_schema_unsupported");
        if (state == LocalWorkspaceProjectionInstallationState.Current)
            throw new InvalidOperationException("local_workspace_projection_authority_unavailable");
    }

    public void RefreshSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset now)
    {
        ValidateInstallationState(connection, transaction);
    }

    public void CompleteSessionEventContentDeletion(SqliteConnection connection, SqliteTransaction transaction, string sourceItemId, DateTimeOffset now)
    {
        var state = LocalWorkspaceProjectionSchemaV1.ReadInstallationState(connection, transaction);
        if (state == LocalWorkspaceProjectionInstallationState.Unsupported)
            throw new InvalidOperationException("local_workspace_projection_schema_unsupported");
        if (state == LocalWorkspaceProjectionInstallationState.Current)
            throw new InvalidOperationException("local_workspace_projection_authority_unavailable");
    }
}

internal sealed class LocalWorkspaceProjectionTransactionParticipant : ILocalWorkspaceProjectionTransactionParticipant
{
    private readonly ISkillRegistryGenerationAuthority skillRegistryAuthority;

    internal LocalWorkspaceProjectionTransactionParticipant(ISkillRegistryGenerationAuthority skillRegistryAuthority)
    {
        this.skillRegistryAuthority = skillRegistryAuthority ?? throw new ArgumentNullException(nameof(skillRegistryAuthority));
    }

    public void ValidateInstallationState(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (LocalWorkspaceProjectionSchemaV1.ReadInstallationState(connection, transaction) == LocalWorkspaceProjectionInstallationState.Unsupported)
            throw new InvalidOperationException("local_workspace_projection_schema_unsupported");
    }

    public void RefreshSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessionIds,
        DateTimeOffset now)
    {
        var state = LocalWorkspaceProjectionSchemaV1.ReadInstallationState(connection, transaction);
        if (state == LocalWorkspaceProjectionInstallationState.Absent) return;
        if (state == LocalWorkspaceProjectionInstallationState.Unsupported)
            throw new InvalidOperationException("local_workspace_projection_schema_unsupported");
        if (sessionIds.Count == 0) return;
        LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction, sessionIds, now, skillRegistryAuthority);
    }

    public void RefreshSessionBatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<IReadOnlyCollection<string>> sessionIdBatches,
        DateTimeOffset now)
    {
        var state = LocalWorkspaceProjectionSchemaV1.ReadInstallationState(connection, transaction);
        if (state == LocalWorkspaceProjectionInstallationState.Absent) return;
        if (state == LocalWorkspaceProjectionInstallationState.Unsupported)
            throw new InvalidOperationException("local_workspace_projection_schema_unsupported");
        LocalWorkspaceProjectionStore.RefreshSessionBatches(connection, transaction, sessionIdBatches, now, skillRegistryAuthority);
    }

    public void CompleteSessionEventContentDeletion(SqliteConnection connection, SqliteTransaction transaction, string sourceItemId, DateTimeOffset now)
    {
        var state = LocalWorkspaceProjectionSchemaV1.ReadInstallationState(connection, transaction);
        if (state == LocalWorkspaceProjectionInstallationState.Absent) return;
        if (state == LocalWorkspaceProjectionInstallationState.Unsupported)
            throw new InvalidOperationException("local_workspace_projection_schema_unsupported");
        LocalWorkspaceProjectionStore.CompleteSessionEventContentDeletion(connection, transaction, sourceItemId, now);
        using var owner = connection.CreateCommand();
        owner.Transaction = transaction;
        owner.CommandText = "SELECT session_id FROM session_events WHERE event_id=$event_id;";
        owner.Parameters.AddWithValue("$event_id", sourceItemId);
        if (owner.ExecuteScalar() is string sessionId)
            LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction, [sessionId], now, skillRegistryAuthority);
    }

}

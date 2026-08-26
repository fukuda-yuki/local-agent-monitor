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

    private static bool IsInstalled(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_version WHERE component='local_workspace_projection' AND version=4);";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}

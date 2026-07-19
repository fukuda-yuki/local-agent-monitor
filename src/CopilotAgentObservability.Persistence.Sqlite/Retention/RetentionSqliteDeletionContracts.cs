using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum RetentionSqliteDeletePhase
{
    AfterSourceMutation,
    AfterSourceAbsenceVerified,
    AfterDeleteCursorAdvanced,
    AfterTombstoneInserted,
    AfterDeletedStateUpdated,
    BeforeCommit
}

internal delegate ValueTask<int> RetentionSqliteSourceMutation(
    SqliteConnection connection,
    SqliteTransaction transaction,
    RetentionSqliteDeletionGrant grant);

internal sealed class RetentionSqliteDeletionGrant
{
    private readonly byte[] sourceToken;
    internal RetentionSqliteDeletionGrant(RetentionOwnershipKey ownershipKey, byte[] sourceToken)
    {
        OwnershipKey = ownershipKey;
        this.sourceToken = sourceToken;
    }

    internal RetentionOwnershipKey OwnershipKey { get; }

    internal void BindSourceToken(SqliteCommand command, string parameterName = "$retention_owner_token")
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Parameters.AddWithValue(parameterName, sourceToken);
    }
}

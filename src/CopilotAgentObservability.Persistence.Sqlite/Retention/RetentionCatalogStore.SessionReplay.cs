using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum RetentionSessionEventContentReplayComparison
{
    Unavailable,
    Match,
    Conflict,
}

public sealed partial class RetentionCatalogStore
{
    internal RetentionSessionEventContentReplayComparison CompareSessionEventContentForReplay(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        string candidateContentKind,
        string candidateContentJson,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateContentKind);
        ArgumentNullException.ThrowIfNull(candidateContentJson);
        var storeInstanceId = context?.StoreInstanceId ?? throw new RetentionCatalogUnavailableException();
        if (!string.Equals(StoreId(connection, transaction), storeInstanceId, StringComparison.Ordinal))
            throw new RetentionCatalogUnavailableException();

        var key = new RetentionOwnershipKey(
            storeInstanceId,
            RetentionStoreKind.SessionEventContent,
            eventId);
        var item = FindForUpdate(connection, transaction, key);
        var sourceExists = SessionEventContentSourceExists(connection, transaction, eventId);
        if (item is null)
        {
            if (!sourceExists) return RetentionSessionEventContentReplayComparison.Unavailable;
            throw new RetentionCatalogUnavailableException();
        }

        var proof = SourceProof(connection, transaction, key);
        var isReadableLifecycle = item.State is (
            RetentionItemLifecycle.Expiring
            or RetentionItemLifecycle.RetainedByPolicy);
        if (isReadableLifecycle != (item.ReadDeniedAt is null))
            throw new RetentionCatalogUnavailableException();
        if (proof != SourceReceiptProof.Match)
        {
            if (proof == SourceReceiptProof.Missing
                && !sourceExists
                && IsFinalizedDeletedItem(connection, transaction, key))
            {
                return RetentionSessionEventContentReplayComparison.Unavailable;
            }
            throw new RetentionCatalogUnavailableException();
        }

        if (item.ReadDeniedAt is not null)
            return RetentionSessionEventContentReplayComparison.Unavailable;
        if (item.State == RetentionItemLifecycle.Expiring)
        {
            if (now >= item.ExpiresAt
                || now >= ReadSessionEventContentExpiry(connection, transaction, eventId))
            {
                return RetentionSessionEventContentReplayComparison.Unavailable;
            }
        }
        else if (item.State != RetentionItemLifecycle.RetainedByPolicy)
        {
            return RetentionSessionEventContentReplayComparison.Unavailable;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT typeof(content_kind),content_kind,typeof(content_json),content_json COLLATE BINARY FROM session_event_content WHERE event_id=$event;";
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !string.Equals(reader.GetString(0), "text", StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), "text", StringComparison.Ordinal))
        {
            throw new RetentionCatalogUnavailableException();
        }
        var match = string.Equals(reader.GetString(1), candidateContentKind, StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), candidateContentJson, StringComparison.Ordinal);
        if (reader.Read()) throw new RetentionCatalogUnavailableException();
        return match
            ? RetentionSessionEventContentReplayComparison.Match
            : RetentionSessionEventContentReplayComparison.Conflict;
    }

    private static bool SessionEventContentSourceExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM session_event_content WHERE event_id=$event);";
        command.Parameters.AddWithValue("$event", eventId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static DateTimeOffset ReadSessionEventContentExpiry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT typeof(expires_at),expires_at FROM session_event_content WHERE event_id=$event;";
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !string.Equals(reader.GetString(0), "text", StringComparison.Ordinal)
            || !DateTimeOffset.TryParseExact(
                reader.GetString(1),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var expiresAt)
            || reader.Read())
        {
            throw new RetentionCatalogUnavailableException();
        }
        return expiresAt;
    }
}

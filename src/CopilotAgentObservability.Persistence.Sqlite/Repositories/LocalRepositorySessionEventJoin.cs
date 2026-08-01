using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalRepositoryCaptureProvenanceStatus
{
    Valid,
    CatalogSchemaViolation,
}

internal sealed record LocalRepositoryCaptureProvenance(
    long RawRecordId,
    string RawPayloadSha256,
    string SourceSurface,
    string? SourceApplicationVersion,
    DateTimeOffset ObservedAt);

internal sealed record LocalRepositoryCaptureProvenanceResult(
    LocalRepositoryCaptureProvenanceStatus Status,
    LocalRepositoryCaptureProvenance? Provenance);

internal enum LocalRepositorySessionEventJoinStatus
{
    Matched,
    WaitingSession,
    CatalogSessionIdentityConflict,
    CatalogSchemaViolation,
}

internal sealed record LocalRepositorySessionEventJoinResult(
    LocalRepositorySessionEventJoinStatus Status,
    string? SessionEventId,
    string? SessionId);

internal static class LocalRepositorySessionEventJoin
{
    internal static LocalRepositoryCaptureProvenanceResult ReadCaptureProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        string expectedRawPayloadSha256)
    {
        ValidateTransaction(connection, transaction);
        if (rawRecordId < 1)
            throw new ArgumentOutOfRangeException(nameof(rawRecordId));
        if (!LocalRepositoryCatalogValidation.IsLowerSha256(expectedRawPayloadSha256))
            throw new ArgumentException("Expected payload digest must be lowercase SHA-256 hexadecimal.", nameof(expectedRawPayloadSha256));

        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT raw_record_id,input_evidence_kind,raw_payload_sha256,
                       source_surface,source_application_version,observed_at
                FROM source_schema_observations
                WHERE raw_record_id=$raw_record_id
                LIMIT 2;
                """;
            command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || !TryReadProvenance(reader, rawRecordId, expectedRawPayloadSha256, out var provenance))
                return SchemaViolation();
            if (reader.Read())
                return SchemaViolation();
            return new(LocalRepositoryCaptureProvenanceStatus.Valid, provenance);
        }
        catch (Exception exception) when (IsMalformedSchemaRead(exception))
        {
            return SchemaViolation();
        }
    }

    internal static LocalRepositorySessionEventJoinResult ResolveContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryCaptureProvenance provenance,
        string traceId,
        string spanId)
    {
        ValidateTransaction(connection, transaction);
        ArgumentNullException.ThrowIfNull(provenance);
        if (!IsLowerHex(traceId, 32) || !IsLowerHex(spanId, 16))
            return JoinSchemaViolation();
        var expectedSessionSurface = provenance.SourceSurface switch
        {
            "github-copilot-cli" => "copilot-cli",
            "github-copilot-vscode" => "vscode",
            _ => null,
        };
        if (expectedSessionSurface is null)
            return JoinSchemaViolation();

        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT event_id,session_id,type,trace_id,source_surface
                FROM session_events
                WHERE source_adapter='otel-exact' COLLATE BINARY
                  AND source_event_id=$source_event_id COLLATE BINARY
                LIMIT 2;
                """;
            command.Parameters.AddWithValue("$source_event_id", $"{traceId}/{spanId}");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new(LocalRepositorySessionEventJoinStatus.WaitingSession, null, null);

            var eventId = Text(reader, 0);
            var sessionId = Text(reader, 1);
            var type = Text(reader, 2);
            var storedTraceId = Text(reader, 3);
            var storedSurface = Text(reader, 4);
            if (reader.Read()
                || eventId is null
                || sessionId is null
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(eventId)
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId)
                || !string.Equals(type, "otel.span", StringComparison.Ordinal)
                || !string.Equals(storedTraceId, traceId, StringComparison.Ordinal)
                || !string.Equals(storedSurface, expectedSessionSurface, StringComparison.Ordinal))
            {
                return new(LocalRepositorySessionEventJoinStatus.CatalogSessionIdentityConflict, null, null);
            }

            return new(LocalRepositorySessionEventJoinStatus.Matched, eventId, sessionId);
        }
        catch (Exception exception) when (IsMalformedSchemaRead(exception))
        {
            return JoinSchemaViolation();
        }
    }

    private static bool TryReadProvenance(
        SqliteDataReader reader,
        long expectedRawRecordId,
        string expectedRawPayloadSha256,
        out LocalRepositoryCaptureProvenance? provenance)
    {
        provenance = null;
        if (reader.GetValue(0) is not long rawRecordId
            || rawRecordId != expectedRawRecordId
            || Text(reader, 1) is not "payload_sha256"
            || Text(reader, 2) is not { } rawPayloadSha256
            || !LocalRepositoryCatalogValidation.IsLowerSha256(rawPayloadSha256)
            || !string.Equals(rawPayloadSha256, expectedRawPayloadSha256, StringComparison.Ordinal)
            || Text(reader, 3) is not { } sourceSurface
            || sourceSurface is not ("github-copilot-cli" or "github-copilot-vscode")
            || !TryNullableText(reader, 4, out var sourceApplicationVersion)
            || sourceApplicationVersion is not null && !IsVisibleApplicationVersion(sourceApplicationVersion)
            || Text(reader, 5) is not { } observedAtText
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(observedAtText)
            || !DateTimeOffset.TryParseExact(
                observedAtText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var observedAt))
        {
            return false;
        }

        provenance = new(rawRecordId, rawPayloadSha256, sourceSurface, sourceApplicationVersion, observedAt);
        return true;
    }

    private static bool IsVisibleApplicationVersion(string value) =>
        value.Length is >= 1 and <= 64
        && value.All(static character => character is >= '!' and <= '~' and not '/' and not '\\');

    private static bool IsLowerHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is string value ? value : null;

    private static bool TryNullableText(SqliteDataReader reader, int ordinal, out string? value)
    {
        var stored = reader.GetValue(ordinal);
        value = stored as string;
        return stored is DBNull || value is not null;
    }

    private static void ValidateTransaction(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must be active on the supplied open connection.", nameof(transaction));
    }

    private static bool IsMalformedSchemaRead(Exception exception) =>
        exception is InvalidCastException or FormatException or OverflowException;

    private static LocalRepositoryCaptureProvenanceResult SchemaViolation() =>
        new(LocalRepositoryCaptureProvenanceStatus.CatalogSchemaViolation, null);

    private static LocalRepositorySessionEventJoinResult JoinSchemaViolation() =>
        new(LocalRepositorySessionEventJoinStatus.CatalogSchemaViolation, null, null);
}

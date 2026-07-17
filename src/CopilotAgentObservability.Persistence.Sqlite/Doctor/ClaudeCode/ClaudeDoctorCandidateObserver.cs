using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace CopilotAgentObservability.Persistence.Sqlite.Doctor.ClaudeCode;

internal sealed class ClaudeDoctorCandidateObserver
{
    private const string SourceSurface = "claude-code";
    private const string SourceAdapter = "claude-code-otel";
    private const int TraceIdLength = 32;
    private const int SpanIdLength = 16;

    private readonly string databasePath;
    private readonly SqliteDoctorApplicationService doctorApplication;
    private readonly ClaudeExactBindingRule exactBindingRule;
    private readonly TimeProvider timeProvider;

    public ClaudeDoctorCandidateObserver(string databasePath, TimeProvider? timeProvider = null)
        : this(
            databasePath,
            SqliteDoctorApplicationService.Create(
                new SqliteDoctorVerificationStore(databasePath, timeProvider)),
            timeProvider)
    {
    }

    internal ClaudeDoctorCandidateObserver(
        string databasePath,
        SqliteDoctorApplicationService doctorApplication,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(doctorApplication);
        this.databasePath = databasePath;
        this.doctorApplication = doctorApplication;
        exactBindingRule = new(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void RunOnce()
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var active = doctorApplication.ListActive(SourceSurface, now);
        if (active.Code != DoctorResultCode.VerificationActive || active.Verifications is null)
        {
            return;
        }

        foreach (var verification in active.Verifications)
        {
            if (!string.Equals(verification.ExpectedSourceAdapter, SourceAdapter, StringComparison.Ordinal))
            {
                continue;
            }

            ObserveVerification(verification);
        }
    }

    private void ObserveVerification(DoctorVerification verification)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var completenessReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in ReadEligibleRecords(verification))
        {
            var spans = ReadPayloadSpans(record.PayloadJson);
            var projectedSpans = ReadProjectedSpans(record.RawRecordId);

            foreach (var span in spans)
            {
                if (!IsValidIdentity(span.TraceId, span.SpanId))
                {
                    continue;
                }

                var identity = EvidenceIdentity(span.TraceId!, span.SpanId!);
                Observe(
                    verification,
                    references,
                    DoctorEvidenceKind.Ingest,
                    $"claude-otel-ingest-{identity}",
                    record.ReceivedAt);
                Observe(
                    verification,
                    references,
                    DoctorEvidenceKind.RawPersistence,
                    $"claude-otel-raw-{identity}",
                    record.ReceivedAt);

                var sessionId = exactBindingRule.Resolve(record.PayloadJson, span.TraceId!, span.SpanId!);
                if (sessionId is null)
                {
                    continue;
                }

                var sessionGuid = sessionId.Value.ToString("D");
                Observe(
                    verification,
                    references,
                    DoctorEvidenceKind.ExactSessionBinding,
                    $"claude-otel-binding-{span.TraceId!}-{sessionGuid}",
                    record.ReceivedAt);

                if (!HasPartialOrBetterCompleteness(sessionId.Value)
                    || ReadAgreedContentState(span.TraceId!) is null)
                {
                    continue;
                }

                var completenessReference = $"claude-otel-completeness-{sessionGuid}";
                if (completenessReferences.Add(completenessReference))
                {
                    Observe(
                        verification,
                        references,
                        DoctorEvidenceKind.CompletenessContent,
                        completenessReference,
                        record.ReceivedAt);
                }
            }

            foreach (var span in projectedSpans)
            {
                if (!IsValidIdentity(span.TraceId, span.SpanId))
                {
                    continue;
                }

                var identity = EvidenceIdentity(span.TraceId!, span.SpanId!);
                Observe(
                    verification,
                    references,
                    DoctorEvidenceKind.Projection,
                    $"claude-otel-projection-{identity}",
                    record.ReceivedAt);
            }
        }
    }

    private void Observe(
        DoctorVerification verification,
        ISet<string> references,
        DoctorEvidenceKind kind,
        string evidenceRef,
        DateTimeOffset observedAt)
    {
        if (!references.Add(evidenceRef))
        {
            return;
        }

        doctorApplication.ObserveCandidate(new(
            Guid.CreateVersion7().ToString("D"),
            verification.VerificationId,
            SourceSurface,
            SourceAdapter,
            DoctorEvidenceClass.RealSource,
            kind,
            evidenceRef,
            observedAt,
            verification.ExpiresAt));
    }

    private IReadOnlyList<ClaudeOtelRecord> ReadEligibleRecords(DoctorVerification verification)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id,r.received_at,r.payload_json
            FROM raw_records r
            JOIN source_schema_observations o ON o.raw_record_id=r.id
            WHERE o.source_surface=$source_surface
              AND o.source_adapter=$source_adapter
              AND r.received_at >= $started_at
            ORDER BY r.received_at COLLATE BINARY,r.id;
            """;
        Add(command, "$source_surface", SourceSurface);
        Add(command, "$source_adapter", SourceAdapter);
        Add(command, "$started_at", Timestamp(verification.StartedAt));

        using var reader = command.ExecuteReader();
        var records = new List<ClaudeOtelRecord>();
        while (reader.Read())
        {
            records.Add(new(
                reader.GetInt64(0),
                ParseTimestamp(reader.GetString(1)),
                reader.GetString(2)));
        }

        return records;
    }

    private IReadOnlyList<OtelSpanReference> ReadProjectedSpans(long rawRecordId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT trace_id,span_id
            FROM monitor_spans
            WHERE raw_record_id=$raw_record_id
            ORDER BY span_ordinal;
            """;
        Add(command, "$raw_record_id", rawRecordId);

        using var reader = command.ExecuteReader();
        var spans = new List<OtelSpanReference>();
        while (reader.Read())
        {
            spans.Add(new(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return spans;
    }

    private bool HasPartialOrBetterCompleteness(Guid sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT completeness FROM sessions WHERE session_id=$session_id;";
        Add(command, "$session_id", sessionId.ToString("D"));
        var value = command.ExecuteScalar() as string;
        return value is "partial" or "rich" or "full";
    }

    private string? ReadAgreedContentState(string traceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT o.capture_content_state
            FROM source_schema_observations o
            JOIN raw_records r ON r.id=o.raw_record_id
            WHERE o.source_surface=$source_surface
              AND o.source_adapter=$source_adapter
              AND r.trace_id=$trace_id COLLATE BINARY
              AND o.capture_content_state IS NOT NULL;
            """;
        Add(command, "$source_surface", SourceSurface);
        Add(command, "$source_adapter", SourceAdapter);
        Add(command, "$trace_id", traceId);

        using var reader = command.ExecuteReader();
        string? state = null;
        if (!reader.Read())
        {
            return null;
        }

        state = reader.GetString(0);
        return reader.Read() ? null : state;
    }

    private static IReadOnlyList<OtelSpanReference> ReadPayloadSpans(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var spans = new List<OtelSpanReference>();
        foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(document.RootElement, "resourceSpans"))
        {
            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    spans.Add(new(
                        OtlpSpanReader.ReadString(span, "traceId"),
                        OtlpSpanReader.ReadString(span, "spanId")));
                }
            }
        }

        return spans;
    }

    private static bool IsValidIdentity(string? traceId, string? spanId) =>
        IsLowerHex(traceId, TraceIdLength) && IsLowerHex(spanId, SpanIdLength);

    private static bool IsLowerHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string EvidenceIdentity(string traceId, string spanId) => $"{traceId}-{spanId}";

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record ClaudeOtelRecord(long RawRecordId, DateTimeOffset ReceivedAt, string PayloadJson);

    private sealed record OtelSpanReference(string? TraceId, string? SpanId);
}

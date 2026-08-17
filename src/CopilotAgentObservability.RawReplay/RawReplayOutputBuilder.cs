using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.RawReplay;

internal sealed record RawReplayOutputs(byte[] Normalized, byte[] Projection, byte[] Dashboard)
{
    internal string NormalizedSha256 => RawReplayHash.Sha256(Normalized);
    internal string ProjectionSha256 => RawReplayHash.Sha256(Projection);
    internal string DashboardSha256 => RawReplayHash.Sha256(Dashboard);
}

internal static class RawReplayOutputBuilder
{
    internal static RawReplayOutputs Build(IReadOnlyList<RawReplayRecord> records)
    {
        var ordered = records.OrderBy(record => record.RawRecordId).ToArray();
        // D069 freezes these v1 target bytes. Live source-resolution semantics
        // require a separately accepted target version before raw replay can use them.
        var rows = BuildCanonicalMeasurements(ordered);
        byte[]? normalized = null;
        byte[]? projection = null;
        byte[]? dashboard = null;
        var transferred = false;
        try
        {
            normalized = RawReplayJson.SerializeCanonical(new
            {
                schema_version = RawReplayContractVersions.Normalization,
                rows = rows.Select(item => item.Row).ToArray(),
            });

            var projections = ordered.Select(record =>
            {
                var raw = ToTelemetry(record);
                var monitor = MonitorProjectionBuilder.BuildRawReplayV1(raw);
                var contributions = BuildCanonicalContributions(monitor.TraceContributions);
                var spans = BuildCanonicalSpans(MonitorSpanProjectionBuilder.Build(raw));
                var primary = contributions.FirstOrDefault(contribution =>
                        string.Equals(contribution.TraceId, monitor.TraceId, StringComparison.Ordinal))
                    ?? contributions.FirstOrDefault();
                return new
                {
                    raw_record_id = record.RawRecordId,
                    projection = monitor with
                    {
                        ClientKind = primary?.ClientKind,
                        TraceContributions = contributions,
                    },
                    spans,
                };
            }).ToArray();
            projection = RawReplayJson.SerializeCanonical(new
            {
                schema_version = RawReplayContractVersions.Projection,
                records = projections,
            });

            var dashboardRows = rows.Select(item => item.Row)
                .Select(row => new
                {
                    trace_id = row.TraceId,
                    client_kind = row.ClientKind,
                    input_tokens = row.InputTokens,
                    output_tokens = row.OutputTokens,
                    total_tokens = row.TotalTokens,
                    turn_count = row.TurnCount,
                    tool_call_count = row.ToolCallCount,
                    error_count = row.ErrorCount,
                    duration_ms = row.DurationMs,
                    success_status = row.SuccessStatus,
                })
                .ToArray();
            dashboard = RawReplayJson.SerializeCanonical(new
            {
                schema_version = RawReplayContractVersions.Dashboard,
                rows = dashboardRows,
            });
            transferred = true;
            return new(normalized, projection, dashboard);
        }
        finally
        {
            foreach (var row in rows) CryptographicOperations.ZeroMemory(row.Bytes);
            if (!transferred)
            {
                if (normalized is not null) CryptographicOperations.ZeroMemory(normalized);
                if (projection is not null) CryptographicOperations.ZeroMemory(projection);
                if (dashboard is not null) CryptographicOperations.ZeroMemory(dashboard);
            }
        }
    }

    private static CanonicalMeasurement[] BuildCanonicalMeasurements(IReadOnlyList<RawReplayRecord> records)
    {
        var values = new List<CanonicalMeasurement>();
        try
        {
            foreach (var row in records.SelectMany(record => RawMeasurementNormalizer.NormalizeRawReplayV1(record.PayloadJson)))
                values.Add(new(row, RawReplayJson.SerializeCanonical(row)));
            return values
                .OrderBy(item => item.Row.TraceId, StringComparer.Ordinal)
                .ThenBy(item => Convert.ToHexString(item.Bytes), StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            foreach (var value in values) CryptographicOperations.ZeroMemory(value.Bytes);
            throw;
        }
    }

    private static MonitorTraceContribution[] BuildCanonicalContributions(IReadOnlyList<MonitorTraceContribution> contributions)
    {
        var canonical = new List<CanonicalContribution>();
        try
        {
            foreach (var contribution in contributions)
                canonical.Add(new(contribution, RawReplayJson.SerializeCanonical(contribution)));
            return canonical
                .OrderBy(item => item.Contribution.TraceId, StringComparer.Ordinal)
                .ThenBy(item => Convert.ToHexString(item.Bytes), StringComparer.Ordinal)
                .Select(item => item.Contribution)
                .ToArray();
        }
        finally
        {
            foreach (var item in canonical) CryptographicOperations.ZeroMemory(item.Bytes);
        }
    }

    private static MonitorSpanProjection[] BuildCanonicalSpans(IReadOnlyList<MonitorSpanProjection> spans)
    {
        var canonical = new List<CanonicalSpan>();
        try
        {
            foreach (var span in spans.Select(span => span with { SpanOrdinal = 0 }))
                canonical.Add(new(span, RawReplayJson.SerializeCanonical(span)));
            return canonical
                .OrderBy(item => item.Span.TraceId, StringComparer.Ordinal)
                .ThenBy(item => Convert.ToHexString(item.Bytes), StringComparer.Ordinal)
                .Select((item, index) => item.Span with { SpanOrdinal = index })
                .ToArray();
        }
        finally
        {
            foreach (var item in canonical) CryptographicOperations.ZeroMemory(item.Bytes);
        }
    }

    private static RawTelemetryRecord ToTelemetry(RawReplayRecord record) => new(
        record.RawRecordId,
        record.Source,
        record.TraceId,
        record.ReceivedAt,
        record.ResourceAttributesJson,
        record.PayloadJson,
        record.SchemaVersion);

    private sealed record CanonicalMeasurement(MeasurementRow Row, byte[] Bytes);
    private sealed record CanonicalContribution(MonitorTraceContribution Contribution, byte[] Bytes);
    private sealed record CanonicalSpan(MonitorSpanProjection Span, byte[] Bytes);
}

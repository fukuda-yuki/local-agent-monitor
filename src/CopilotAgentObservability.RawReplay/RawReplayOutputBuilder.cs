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
        var rows = ordered
            .SelectMany(record => RawMeasurementNormalizer.Normalize(record.PayloadJson))
            .Select(row => new CanonicalMeasurement(row, RawReplayJson.SerializeCanonical(row)))
            .OrderBy(item => item.Row.TraceId, StringComparer.Ordinal)
            .ThenBy(item => Convert.ToHexString(item.Bytes), StringComparer.Ordinal)
            .ToArray();
        var normalized = RawReplayJson.SerializeCanonical(new
        {
            schema_version = RawReplayContractVersions.Normalization,
            rows = rows.Select(item => item.Row).ToArray(),
        });

        var projections = ordered.Select(record =>
        {
            var raw = ToTelemetry(record);
            return new
            {
                raw_record_id = record.RawRecordId,
                projection = MonitorProjectionBuilder.Build(raw),
                spans = MonitorSpanProjectionBuilder.Build(raw)
                    .OrderBy(span => span.TraceId, StringComparer.Ordinal)
                    .ThenBy(span => span.SpanOrdinal)
                    .ToArray(),
            };
        }).ToArray();
        var projection = RawReplayJson.SerializeCanonical(new
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
        var dashboard = RawReplayJson.SerializeCanonical(new
        {
            schema_version = RawReplayContractVersions.Dashboard,
            rows = dashboardRows,
        });
        return new(normalized, projection, dashboard);
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
}

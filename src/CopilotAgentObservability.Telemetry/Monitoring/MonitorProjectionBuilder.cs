namespace CopilotAgentObservability.Telemetry;

/// <summary>
/// Builds the sanitized <see cref="MonitorRecordProjection"/> for one ingested
/// raw record. Reuses <see cref="RawMeasurementNormalizer"/> for trace-level
/// classification (tool / error counts and allowlisted attributes) and adds a
/// per-trace span count. Reads only allowlisted keys, so raw prompt / response /
/// tool content and PII never enter a projection value.
/// </summary>
internal static class MonitorProjectionBuilder
{
    public static MonitorRecordProjection Build(RawTelemetryRecord record)
    {
        var rows = RawMeasurementNormalizer.Normalize(record.PayloadJson);
        var (totalSpans, spansByTrace) = CountSpans(record.PayloadJson);

        var contributions = new List<MonitorTraceContribution>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.TraceId))
            {
                // No usable trace key: counted in the ingestion span total, but
                // never written to monitor_traces (one row per trace_id).
                continue;
            }

            contributions.Add(new MonitorTraceContribution(
                TraceId: row.TraceId,
                ClientKind: row.ClientKind,
                ExperimentId: row.ExperimentId,
                TaskId: row.TaskId,
                TaskCategory: row.TaskCategory,
                AgentVariant: row.AgentVariant,
                PromptVersion: row.PromptVersion,
                SpanCount: spansByTrace.TryGetValue(row.TraceId, out var spanCount) ? spanCount : 0,
                ToolCallCount: row.ToolCallCount ?? 0,
                ErrorCount: row.ErrorCount ?? 0));
        }

        var primary = contributions.FirstOrDefault(c => string.Equals(c.TraceId, record.TraceId, StringComparison.Ordinal))
            ?? contributions.FirstOrDefault();

        return new MonitorRecordProjection(
            TraceId: record.TraceId,
            ClientKind: primary?.ClientKind,
            SpanCount: totalSpans,
            TraceContributions: contributions);
    }

    private static (int Total, Dictionary<string, int> ByTrace) CountSpans(string payloadJson)
    {
        var byTrace = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            return (0, byTrace);
        }

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            foreach (var scopeSpan in EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    total++;
                    if (span.TryGetProperty("traceId", out var traceIdElement)
                        && traceIdElement.ValueKind == JsonValueKind.String)
                    {
                        var traceId = traceIdElement.GetString();
                        if (!string.IsNullOrWhiteSpace(traceId))
                        {
                            byTrace[traceId] = byTrace.TryGetValue(traceId, out var count) ? count + 1 : 1;
                        }
                    }
                }
            }
        }

        return (total, byTrace);
    }

    private static IEnumerable<JsonElement> EnumerateArrayProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            yield return item;
        }
    }
}

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
        var (totalSpans, spansByTrace, metadataByTrace) = CountSpansAndMetadata(record.PayloadJson);

        var contributions = new List<MonitorTraceContribution>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.TraceId))
            {
                // No usable trace key: counted in the ingestion span total, but
                // never written to monitor_traces (one row per trace_id).
                continue;
            }

            metadataByTrace.TryGetValue(row.TraceId, out var metadata);
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
                ErrorCount: row.ErrorCount ?? 0,
                RepositoryName: metadata?.RepositoryName,
                WorkspaceLabel: metadata?.WorkspaceLabel,
                RepoSnapshot: metadata?.RepoSnapshot));
        }

        var primary = contributions.FirstOrDefault(c => string.Equals(c.TraceId, record.TraceId, StringComparison.Ordinal))
            ?? contributions.FirstOrDefault();

        return new MonitorRecordProjection(
            TraceId: record.TraceId,
            ClientKind: primary?.ClientKind,
            SpanCount: totalSpans,
            TraceContributions: contributions);
    }

    private static (int Total, Dictionary<string, int> ByTrace, Dictionary<string, TraceRepositoryMetadata> MetadataByTrace) CountSpansAndMetadata(string payloadJson)
    {
        var byTrace = new Dictionary<string, int>(StringComparer.Ordinal);
        var metadataByTrace = new Dictionary<string, TraceRepositoryMetadata>(StringComparer.Ordinal);
        var total = 0;

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            return (0, byTrace, metadataByTrace);
        }

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var resourceAttributes = OtlpSpanReader.ReadResourceAttributes(resourceSpan);
            var resourceMetadata = new TraceRepositoryMetadata(
                RepositoryName: MeasurementSanitizer.SanitizeFreeFormName(OtlpSpanReader.ReadString(resourceAttributes, "repo.name")),
                WorkspaceLabel: MeasurementSanitizer.SanitizeFreeFormName(OtlpSpanReader.ReadString(resourceAttributes, "workspace.name")),
                RepoSnapshot: MeasurementSanitizer.SanitizeFreeFormName(OtlpSpanReader.ReadString(resourceAttributes, "repo.snapshot")));

            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    total++;
                    if (span.TryGetProperty("traceId", out var traceIdElement)
                        && traceIdElement.ValueKind == JsonValueKind.String)
                    {
                        var traceId = traceIdElement.GetString();
                        if (!string.IsNullOrWhiteSpace(traceId))
                        {
                            byTrace[traceId] = byTrace.TryGetValue(traceId, out var count) ? count + 1 : 1;
                            metadataByTrace[traceId] = metadataByTrace.TryGetValue(traceId, out var existing)
                                ? existing.FillNulls(resourceMetadata)
                                : resourceMetadata;
                        }
                    }
                }
            }
        }

        return (total, byTrace, metadataByTrace);
    }

    private sealed record TraceRepositoryMetadata(
        string? RepositoryName,
        string? WorkspaceLabel,
        string? RepoSnapshot)
    {
        public TraceRepositoryMetadata FillNulls(TraceRepositoryMetadata next) =>
            new(
                RepositoryName ?? next.RepositoryName,
                WorkspaceLabel ?? next.WorkspaceLabel,
                RepoSnapshot ?? next.RepoSnapshot);
    }
}

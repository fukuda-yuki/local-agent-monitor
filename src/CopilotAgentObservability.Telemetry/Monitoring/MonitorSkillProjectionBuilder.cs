namespace CopilotAgentObservability.Telemetry;

internal static class MonitorSkillProjectionBuilder
{
    internal const int MaximumAvailableNames = 100;

    private const string CopilotCliSourceSurface = "github-copilot-cli";
    private const string AvailableSkillsKey = "github.copilot.context.skills";

    private static readonly string[] SkillNameKeys =
    [
        "github.copilot.skill.name",
        "github.copilot.tool.parameters.skill_name",
    ];

    private static readonly string[] NativeSessionIdKeys =
    [
        "gen_ai.conversation.id",
        "conversation_id",
    ];

    public static MonitorSkillProjectionBatch Build(
        RawTelemetryRecord record,
        string? sourceSurface,
        Func<string, (TraceSourceVersionResolutionState State, string? SourceApplicationVersion)?> resolveTraceVersion)
    {
        ArgumentNullException.ThrowIfNull(resolveTraceVersion);
        if (!string.Equals(sourceSurface, CopilotCliSourceSurface, StringComparison.Ordinal))
        {
            return MonitorSkillProjectionBatch.Empty;
        }

        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            return MonitorSkillProjectionBatch.Empty;
        }

        var invocations = new List<MonitorSkillInvocationProjection>();
        var inventories = new Dictionary<string, MonitorSkillInventoryProjection>(StringComparer.Ordinal);
        var resolutions = new Dictionary<string, (TraceSourceVersionResolutionState State, string? Version)?>(StringComparer.Ordinal);
        var ordinal = 0;

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var spanElement in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var span = OtlpSpanReader.CreateSpan(spanElement);
                    if (string.IsNullOrWhiteSpace(span.TraceId))
                    {
                        ordinal++;
                        continue;
                    }

                    if (!resolutions.TryGetValue(span.TraceId, out var resolution))
                    {
                        var resolved = resolveTraceVersion(span.TraceId);
                        resolution = resolved is { } value
                            ? (value.State, value.SourceApplicationVersion)
                            : null;
                        resolutions.Add(span.TraceId, resolution);
                    }
                    if (resolution is not
                        {
                            State: TraceSourceVersionResolutionState.Resolved,
                            Version: not null,
                        })
                    {
                        ordinal++;
                        continue;
                    }

                    var nativeSessionId = OtlpSpanReader.ReadFirstString(span.Attributes, NativeSessionIdKeys);
                    var skillName = ReadFirstSanitizedIdentifier(span.Attributes, SkillNameKeys);
                    if (skillName is not null
                        && !string.IsNullOrEmpty(span.SpanId)
                        && string.Equals(
                            OtlpSpanReader.ReadString(span.Attributes, "gen_ai.operation.name"),
                            "execute_tool",
                            StringComparison.Ordinal)
                        && string.Equals(
                            OtlpSpanReader.ReadString(span.Attributes, "gen_ai.tool.name"),
                            "skill",
                            StringComparison.Ordinal))
                    {
                        invocations.Add(new MonitorSkillInvocationProjection(
                            span.TraceId,
                            span.SpanId,
                            ordinal,
                            nativeSessionId,
                            skillName,
                            MeasurementSanitizer.SanitizeFreeFormName(
                                OtlpSpanReader.ReadString(span.Attributes, "github.copilot.skill.source")),
                            MeasurementSanitizer.SanitizeFreeFormName(
                                OtlpSpanReader.ReadString(span.Attributes, "github.copilot.skill.invocation_trigger")),
                            resolution.Value.Version));
                    }

                    if (!inventories.ContainsKey(span.TraceId)
                        && span.Attributes.TryGetPropertyValue(AvailableSkillsKey, out var availableNode)
                        && availableNode is JsonArray availableNames)
                    {
                        inventories.Add(
                            span.TraceId,
                            BuildInventory(
                                span.TraceId,
                                nativeSessionId,
                                availableNames,
                                resolution.Value.Version));
                    }

                    ordinal++;
                }
            }
        }

        return new MonitorSkillProjectionBatch(invocations, inventories.Values.ToArray());
    }

    private static MonitorSkillInventoryProjection BuildInventory(
        string traceId,
        string? nativeSessionId,
        JsonArray availableNames,
        string sourceApplicationVersion)
    {
        var retained = new List<string>(Math.Min(availableNames.Count, MaximumAvailableNames));
        var truncated = availableNames.Count > MaximumAvailableNames;
        foreach (var node in availableNames)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var rawName))
            {
                continue;
            }

            var sanitized = MeasurementSanitizer.SanitizeFreeFormName(rawName);
            if (sanitized is null)
            {
                continue;
            }
            if (rawName.Length > MeasurementSanitizer.MaxSanitizedNameLength)
            {
                truncated = true;
            }
            if (retained.Count == MaximumAvailableNames)
            {
                truncated = true;
                continue;
            }
            retained.Add(sanitized);
        }

        return new MonitorSkillInventoryProjection(
            traceId,
            nativeSessionId,
            availableNames.Count,
            retained,
            truncated,
            sourceApplicationVersion);
    }

    private static string? ReadFirstSanitizedIdentifier(
        JsonObject attributes,
        IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            var sanitized = MeasurementSanitizer.SanitizeFreeFormName(
                OtlpSpanReader.ReadString(attributes, key));
            if (sanitized is not null)
            {
                return sanitized;
            }
        }
        return null;
    }
}

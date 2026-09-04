namespace CopilotAgentObservability.Telemetry;

internal enum TraceSourceResolutionState
{
    Resolved,
    Missing,
    Conflicting,
    Unrecognised,
}

internal sealed record TraceSourceResolutionDraft(
    string TraceId,
    TraceSourceResolutionState State,
    string? SourceFamily,
    bool CliCandidateObserved,
    bool VsCodeCandidateObserved,
    bool UnknownCandidateObserved,
    bool RelevantEvidenceObserved)
{
    public IReadOnlyDictionary<string, int> AttributeKeys { get; init; } = new Dictionary<string, int>();
    public bool AttributeInventoryIncomplete { get; init; }

    internal static TraceSourceResolutionDraft FromEvidence(
        string traceId,
        bool cliCandidateObserved,
        bool vsCodeCandidateObserved,
        bool unknownCandidateObserved,
        bool relevantEvidenceObserved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        var state = (cliCandidateObserved, vsCodeCandidateObserved, unknownCandidateObserved) switch
        {
            (true, true, _) => TraceSourceResolutionState.Conflicting,
            (_, _, true) => TraceSourceResolutionState.Unrecognised,
            (true, false, false) => TraceSourceResolutionState.Resolved,
            (false, true, false) => TraceSourceResolutionState.Resolved,
            _ => TraceSourceResolutionState.Missing,
        };
        var sourceFamily = state == TraceSourceResolutionState.Resolved
            ? cliCandidateObserved
                ? OtlpTraceSourceResolver.CopilotCliFamily
                : OtlpTraceSourceResolver.VsCodeCopilotChatFamily
            : null;
        return new TraceSourceResolutionDraft(
            traceId,
            state,
            sourceFamily,
            cliCandidateObserved,
            vsCodeCandidateObserved,
            unknownCandidateObserved,
            relevantEvidenceObserved);
    }
}

internal static class OtlpTraceSourceResolver
{
    internal const string CopilotCliFamily = "copilot-cli";
    internal const string VsCodeCopilotChatFamily = "vscode-copilot-chat";

    private const string ClientKindAttribute = "client.kind";
    private const string ServiceNameAttribute = "service.name";

    public static IReadOnlyList<TraceSourceResolutionDraft> Resolve(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        return Resolve([payloadJson]);
    }

    public static IReadOnlyList<TraceSourceResolutionDraft> Resolve(IEnumerable<string> payloadJsons)
    {
        ArgumentNullException.ThrowIfNull(payloadJsons);
        var evidenceByTrace = new Dictionary<string, TraceSourceEvidence>(StringComparer.Ordinal);
        var missingTraceIdentityObserved = false;
        foreach (var payloadJson in payloadJsons)
        {
            ArgumentNullException.ThrowIfNull(payloadJson);
            using var document = JsonDocument.Parse(payloadJson);
            foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(document.RootElement, "resourceSpans"))
            {
                var resourceEvidence = ReadResourceEvidence(resourceSpan);
                var traceIds = ReadTraceIds(resourceSpan, out var missingTraceIdentity);
                missingTraceIdentityObserved |= missingTraceIdentity;
                foreach (var traceId in traceIds)
                {
                    if (!evidenceByTrace.TryGetValue(traceId, out var traceEvidence))
                    {
                        traceEvidence = new TraceSourceEvidence();
                        evidenceByTrace.Add(traceId, traceEvidence);
                    }
                    traceEvidence.Merge(resourceEvidence);
                    if (OtlpSpanReader.TryGetObject(resourceSpan, "resource", out var resource))
                        traceEvidence.ObserveKeys(resource, SourceStructuralEnvelope.Resource);
                    foreach (var scopeSpans in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
                    {
                        var spans = OtlpSpanReader.EnumerateArrayProperty(scopeSpans, "spans")
                            .Where(span => StringComparer.Ordinal.Equals(OtlpSpanReader.ReadString(span, "traceId"), traceId)).ToArray();
                        if (spans.Length == 0) continue;
                        if (OtlpSpanReader.TryGetObject(scopeSpans, "scope", out var scope))
                            traceEvidence.ObserveKeys(scope, SourceStructuralEnvelope.Scope);
                        foreach (var span in spans) traceEvidence.ObserveKeys(span, SourceStructuralEnvelope.Span);
                    }
                }
            }
        }

        return evidenceByTrace
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value.ToResolution(item.Key, missingTraceIdentityObserved))
            .ToArray();
    }

    internal static TraceSourceResolutionDraft ResolveResourceAttributes(
        string traceId,
        JsonElement resourceAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        var evidence = new TraceSourceEvidence();
        if (resourceAttributes.ValueKind == JsonValueKind.Object)
        {
            foreach (var attribute in resourceAttributes.EnumerateObject())
            {
                ObserveAttribute(
                    evidence,
                    attribute.Name,
                    attribute.Value.ValueKind == JsonValueKind.String
                        ? attribute.Value.GetString()
                        : null);
            }
        }
        return evidence.ToResolution(traceId);
    }

    private static TraceSourceEvidence ReadResourceEvidence(JsonElement resourceSpan)
    {
        var evidence = new TraceSourceEvidence();
        if (!OtlpSpanReader.TryGetObject(resourceSpan, "resource", out var resource))
        {
            return evidence;
        }

        foreach (var attribute in OtlpSpanReader.EnumerateArrayProperty(resource, "attributes"))
        {
            var key = OtlpSpanReader.ReadString(attribute, "key");
            if (!string.Equals(key, ClientKindAttribute, StringComparison.Ordinal)
                && !string.Equals(key, ServiceNameAttribute, StringComparison.Ordinal))
            {
                continue;
            }

            evidence.RelevantEvidenceObserved = true;
            var value = OtlpSpanReader.TryGetObject(attribute, "value", out var attributeValue)
                ? OtlpSpanReader.ReadString(attributeValue, "stringValue")
                : null;
            ObserveAttribute(evidence, key, value);
        }
        return evidence;
    }

    private static void ObserveAttribute(
        TraceSourceEvidence evidence,
        string? key,
        string? value)
    {
        if (!string.Equals(key, ClientKindAttribute, StringComparison.Ordinal)
            && !string.Equals(key, ServiceNameAttribute, StringComparison.Ordinal))
        {
            return;
        }

        evidence.RelevantEvidenceObserved = true;
        if (string.Equals(key, ClientKindAttribute, StringComparison.Ordinal)
            && string.Equals(value, CopilotCliFamily, StringComparison.Ordinal)
            || string.Equals(key, ServiceNameAttribute, StringComparison.Ordinal)
            && string.Equals(value, "github-copilot", StringComparison.Ordinal))
        {
            evidence.CliCandidateObserved = true;
        }
        else if (string.Equals(key, ClientKindAttribute, StringComparison.Ordinal)
            && string.Equals(value, VsCodeCopilotChatFamily, StringComparison.Ordinal)
            || string.Equals(key, ServiceNameAttribute, StringComparison.Ordinal)
            && string.Equals(value, "copilot-chat", StringComparison.Ordinal))
        {
            evidence.VsCodeCandidateObserved = true;
        }
        else
        {
            evidence.UnknownCandidateObserved = true;
        }
    }

    private static IEnumerable<string> ReadTraceIds(JsonElement resourceSpan, out bool missingTraceIdentity)
    {
        missingTraceIdentity = false;
        var traceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
        {
            foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
            {
                var traceId = OtlpSpanReader.ReadString(span, "traceId");
                if (!string.IsNullOrWhiteSpace(traceId))
                {
                    traceIds.Add(traceId);
                }
                else missingTraceIdentity = true;
            }
        }
        return traceIds;
    }

    private sealed class TraceSourceEvidence
    {
        private readonly Dictionary<string, int> keys = new(StringComparer.Ordinal);
        private bool attributeInventoryIncomplete;

        public void ObserveKeys(JsonElement element, SourceStructuralEnvelope envelope)
        {
            var inventory = OtlpJsonStructuralWalker.ReadAttributeKeys(element, envelope);
            attributeInventoryIncomplete |= inventory.Incomplete;
            foreach (var key in inventory.Keys)
                keys[key.Name.Value] = (int)Math.Min(SourceOccurrenceCount.Maximum,
                    keys.GetValueOrDefault(key.Name.Value) + (long)key.Count.Value);
        }
        public bool CliCandidateObserved { get; set; }
        public bool VsCodeCandidateObserved { get; set; }
        public bool UnknownCandidateObserved { get; set; }
        public bool RelevantEvidenceObserved { get; set; }

        public void Merge(TraceSourceEvidence other)
        {
            CliCandidateObserved |= other.CliCandidateObserved;
            VsCodeCandidateObserved |= other.VsCodeCandidateObserved;
            UnknownCandidateObserved |= other.UnknownCandidateObserved;
            RelevantEvidenceObserved |= other.RelevantEvidenceObserved;
        }

        public TraceSourceResolutionDraft ToResolution(string traceId, bool missingTraceIdentity = false) =>
            TraceSourceResolutionDraft.FromEvidence(
                traceId,
                CliCandidateObserved,
                VsCodeCandidateObserved,
                UnknownCandidateObserved,
                RelevantEvidenceObserved) with
            {
                AttributeKeys = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>(keys)),
                AttributeInventoryIncomplete = attributeInventoryIncomplete || missingTraceIdentity
            };
    }
}

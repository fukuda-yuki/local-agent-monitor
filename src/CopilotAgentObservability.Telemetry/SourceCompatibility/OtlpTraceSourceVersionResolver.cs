namespace CopilotAgentObservability.Telemetry;

internal static class OtlpTraceSourceVersionResolver
{
    private const string ServiceVersionAttribute = "service.version";

    public static IReadOnlyList<TraceSourceVersionResolutionDraft> Resolve(
        string payloadJson,
        string sourceSurface,
        VerifiedSourceFingerprintRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        SourceMetadata.ValidateRequired(sourceSurface, nameof(sourceSurface));
        ArgumentNullException.ThrowIfNull(registry);

        using var document = JsonDocument.Parse(payloadJson);
        var evidenceByTrace = new Dictionary<string, TraceVersionEvidence>(StringComparer.Ordinal);
        foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(document.RootElement, "resourceSpans"))
        {
            var resourceEvidence = ReadResourceVersionEvidence(resourceSpan);

            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var traceId = OtlpSpanReader.ReadString(span, "traceId");
                    if (string.IsNullOrWhiteSpace(traceId))
                    {
                        continue;
                    }

                    if (!evidenceByTrace.TryGetValue(traceId, out var evidence))
                    {
                        evidence = new TraceVersionEvidence();
                        evidenceByTrace.Add(traceId, evidence);
                    }
                    evidence.Versions.UnionWith(resourceEvidence.Versions);
                    evidence.HasInvalidVersion |= resourceEvidence.HasInvalidVersion;
                    evidence.HasMissingVersion |=
                        resourceEvidence.Versions.Count == 0 && !resourceEvidence.HasInvalidVersion;
                }
            }
        }

        return evidenceByTrace
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => ResolveTrace(item.Key, item.Value, sourceSurface, registry))
            .ToArray();
    }

    private static TraceVersionEvidence ReadResourceVersionEvidence(JsonElement resourceSpan)
    {
        var evidence = new TraceVersionEvidence();
        if (!OtlpSpanReader.TryGetObject(resourceSpan, "resource", out var resource))
        {
            return evidence;
        }

        foreach (var attribute in OtlpSpanReader.EnumerateArrayProperty(resource, "attributes"))
        {
            if (!string.Equals(
                    OtlpSpanReader.ReadString(attribute, "key"),
                    ServiceVersionAttribute,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var version = OtlpSpanReader.TryGetObject(attribute, "value", out var value)
                ? OtlpSpanReader.ReadString(value, "stringValue")
                : null;
            if (SourceMetadata.IsValidToken(version))
            {
                evidence.Versions.Add(version!);
            }
            else
            {
                evidence.HasInvalidVersion = true;
            }
        }

        return evidence;
    }

    private static TraceSourceVersionResolutionDraft ResolveTrace(
        string traceId,
        TraceVersionEvidence evidence,
        string sourceSurface,
        VerifiedSourceFingerprintRegistry registry)
    {
        if (evidence.Versions.Count > 1)
        {
            return TraceSourceVersionResolutionDraft.Create(
                traceId, TraceSourceVersionResolutionState.Conflicting, sourceApplicationVersion: null);
        }
        if (evidence.HasInvalidVersion)
        {
            return TraceSourceVersionResolutionDraft.Create(
                traceId, TraceSourceVersionResolutionState.Unrecognised, sourceApplicationVersion: null);
        }

        var version = evidence.Versions.Count == 1 ? evidence.Versions.Single() : null;
        if (version is not null && !registry.RecognisesSourceVersion(sourceSurface, version))
        {
            return TraceSourceVersionResolutionDraft.Create(
                traceId, TraceSourceVersionResolutionState.Unrecognised, version);
        }
        if (evidence.HasMissingVersion || version is null)
        {
            return TraceSourceVersionResolutionDraft.Create(
                traceId, TraceSourceVersionResolutionState.Missing, sourceApplicationVersion: null);
        }

        return TraceSourceVersionResolutionDraft.Create(
            traceId,
            TraceSourceVersionResolutionState.Resolved,
            version);
    }

    private sealed class TraceVersionEvidence
    {
        public HashSet<string> Versions { get; } = new(StringComparer.Ordinal);
        public bool HasInvalidVersion { get; set; }
        public bool HasMissingVersion { get; set; }
    }
}

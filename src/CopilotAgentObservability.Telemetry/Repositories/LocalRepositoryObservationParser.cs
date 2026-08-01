using System.Text.Json;

namespace CopilotAgentObservability.Telemetry.Repositories;

internal static class LocalRepositoryObservationParser
{
    private const string VcsRepositoryUrlFull = "vcs.repository.url.full";
    private const string CopilotChatRepositoryRemoteUrl = "copilot_chat.repo.remote_url";

    public static LocalRepositoryObservationParseResult Parse(
        long rawRecordId,
        string payloadJson,
        string rawPayloadSha256,
        string sourceSurface,
        string? sourceApplicationVersion,
        DateTimeOffset observedAt)
    {
        if (rawRecordId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rawRecordId));
        }
        if (!IsLowercaseSha256(rawPayloadSha256))
        {
            throw new ArgumentException("Raw payload digest must be lowercase SHA-256 hexadecimal.", nameof(rawPayloadSha256));
        }
        ArgumentNullException.ThrowIfNull(payloadJson);
        ArgumentNullException.ThrowIfNull(sourceSurface);

        using var document = JsonDocument.Parse(payloadJson);
        var occurrences = new List<LocalRepositoryPhysicalOccurrence>();
        var contextLinks = new List<LocalRepositoryObservationContextLink>();
        var resourceSpanOrdinal = 0;
        foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(document.RootElement, "resourceSpans"))
        {
            var resourceOccurrences = ReadResourceOccurrences(
                resourceSpan,
                rawRecordId,
                rawPayloadSha256,
                sourceSurface,
                sourceApplicationVersion,
                observedAt,
                resourceSpanOrdinal);
            occurrences.AddRange(resourceOccurrences);

            var scopeSpanOrdinal = 0;
            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                var spanOrdinal = 0;
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var spanOccurrences = ReadOccurrences(
                        OtlpSpanReader.EnumerateArrayProperty(span, "attributes"),
                        rawRecordId,
                        rawPayloadSha256,
                        sourceSurface,
                        sourceApplicationVersion,
                        observedAt,
                        resourceSpanOrdinal,
                        scopeSpanOrdinal,
                        spanOrdinal);
                    occurrences.AddRange(spanOccurrences);

                    var traceId = OtlpSpanReader.ReadString(span, "traceId");
                    var spanId = OtlpSpanReader.ReadString(span, "spanId");
                    if (spanOccurrences.Count > 0)
                    {
                        foreach (var occurrence in resourceOccurrences)
                        {
                            contextLinks.Add(new LocalRepositoryObservationContextLink(
                                occurrence,
                                traceId,
                                spanId,
                                scopeSpanOrdinal,
                                spanOrdinal,
                                LocalRepositoryAdmissionState.Shadowed));
                        }
                        foreach (var occurrence in spanOccurrences)
                        {
                            contextLinks.Add(new LocalRepositoryObservationContextLink(
                                occurrence,
                                traceId,
                                spanId,
                                scopeSpanOrdinal,
                                spanOrdinal,
                                ToAdmissionState(occurrence.Classification)));
                        }
                    }
                    else
                    {
                        foreach (var occurrence in resourceOccurrences)
                        {
                            contextLinks.Add(new LocalRepositoryObservationContextLink(
                                occurrence,
                                traceId,
                                spanId,
                                scopeSpanOrdinal,
                                spanOrdinal,
                                ToAdmissionState(occurrence.Classification)));
                        }
                    }

                    spanOrdinal++;
                }

                scopeSpanOrdinal++;
            }

            resourceSpanOrdinal++;
        }

        return new LocalRepositoryObservationParseResult(occurrences, contextLinks);
    }

    private static List<LocalRepositoryPhysicalOccurrence> ReadResourceOccurrences(
        JsonElement resourceSpan,
        long rawRecordId,
        string rawPayloadSha256,
        string sourceSurface,
        string? sourceApplicationVersion,
        DateTimeOffset observedAt,
        int resourceSpanOrdinal)
    {
        return OtlpSpanReader.TryGetObject(resourceSpan, "resource", out var resource)
            ? ReadOccurrences(
                OtlpSpanReader.EnumerateArrayProperty(resource, "attributes"),
                rawRecordId,
                rawPayloadSha256,
                sourceSurface,
                sourceApplicationVersion,
                observedAt,
                resourceSpanOrdinal,
                scopeSpanOrdinal: null,
                spanOrdinal: null)
            : [];
    }

    private static List<LocalRepositoryPhysicalOccurrence> ReadOccurrences(
        IEnumerable<JsonElement> attributes,
        long rawRecordId,
        string rawPayloadSha256,
        string sourceSurface,
        string? sourceApplicationVersion,
        DateTimeOffset observedAt,
        int resourceSpanOrdinal,
        int? scopeSpanOrdinal,
        int? spanOrdinal)
    {
        var indexedAttributes = attributes.Select((attribute, ordinal) => new IndexedAttribute(attribute, ordinal)).ToArray();
        var counts = indexedAttributes
            .Select(indexed => OtlpSpanReader.ReadString(indexed.Attribute, "key"))
            .Where(IsApprovedKey)
            .GroupBy(key => key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var occurrences = new List<LocalRepositoryPhysicalOccurrence>();
        foreach (var indexed in indexedAttributes)
        {
            var key = OtlpSpanReader.ReadString(indexed.Attribute, "key");
            if (!IsApprovedKey(key))
            {
                continue;
            }

            GitHubRepositoryLocator? locator = null;
            var classification = counts[key!] > 1
                ? LocalRepositoryOccurrenceClassification.DuplicateKey
                : Classify(indexed.Attribute, out locator);
            var identityInput = scopeSpanOrdinal is null
                ? LocalRepositorySourceIdentityInput.Resource(rawRecordId, resourceSpanOrdinal, indexed.Ordinal, key!)
                : LocalRepositorySourceIdentityInput.Span(rawRecordId, resourceSpanOrdinal, scopeSpanOrdinal.Value, spanOrdinal!.Value, indexed.Ordinal, key!);
            occurrences.Add(new LocalRepositoryPhysicalOccurrence(
                identityInput,
                LocalRepositoryIdentityHashing.SourceIdentity(identityInput),
                rawPayloadSha256,
                sourceSurface,
                sourceApplicationVersion,
                observedAt,
                classification,
                classification == LocalRepositoryOccurrenceClassification.Admitted ? locator : null));
        }

        return occurrences;
    }

    private static LocalRepositoryOccurrenceClassification Classify(JsonElement attribute, out GitHubRepositoryLocator? locator)
    {
        locator = null;
        if (!TryReadStrictStringValue(attribute, out var value))
        {
            return LocalRepositoryOccurrenceClassification.InvalidType;
        }

        return GitHubRepositoryLocatorParser.TryParse(value, out locator)
            ? LocalRepositoryOccurrenceClassification.Admitted
            : LocalRepositoryOccurrenceClassification.InvalidLocator;
    }

    private static bool TryReadStrictStringValue(JsonElement attribute, out string? value)
    {
        value = null;
        if (!OtlpSpanReader.TryGetObject(attribute, "value", out var anyValue))
        {
            return false;
        }

        var properties = anyValue.EnumerateObject().ToArray();
        if (properties.Length != 1
            || !string.Equals(properties[0].Name, "stringValue", StringComparison.Ordinal)
            || properties[0].Value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = properties[0].Value.GetString();
        return value is not null;
    }

    private static bool IsApprovedKey(string? key) =>
        key is VcsRepositoryUrlFull or CopilotChatRepositoryRemoteUrl;

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static LocalRepositoryAdmissionState ToAdmissionState(LocalRepositoryOccurrenceClassification classification) =>
        classification switch
        {
            LocalRepositoryOccurrenceClassification.Admitted => LocalRepositoryAdmissionState.Admitted,
            LocalRepositoryOccurrenceClassification.InvalidLocator => LocalRepositoryAdmissionState.InvalidLocator,
            LocalRepositoryOccurrenceClassification.InvalidType => LocalRepositoryAdmissionState.InvalidType,
            LocalRepositoryOccurrenceClassification.DuplicateKey => LocalRepositoryAdmissionState.DuplicateKey,
            _ => throw new ArgumentOutOfRangeException(nameof(classification)),
        };

    private sealed record IndexedAttribute(JsonElement Attribute, int Ordinal);
}

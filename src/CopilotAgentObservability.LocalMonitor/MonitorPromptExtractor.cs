namespace CopilotAgentObservability.LocalMonitor;

/// <summary>
/// Extracts a single representative user-prompt label for a trace from its raw
/// OTLP payload, for the dashboard / trace-list raw-bearing surfaces (D032). The
/// label is a short, whitespace-collapsed, truncated one-liner shown only on
/// server-rendered pages, where Razor renders it as escaped inert text; it is
/// never copied into the sanitized projection, <c>/api/monitor/*</c>, or the SSE
/// stream. Pure and exception-safe: any parse failure yields <c>null</c> so the
/// caller falls back to a shortened TraceId.
/// </summary>
internal static class MonitorPromptExtractor
{
    internal const int MaxLabelLength = 120;

    // OTLP span attribute keys that carry the user prompt, in preference order.
    // The real VS Code Copilot payload shape is pending live validation; keep the
    // set small and the extraction exception-safe.
    private static readonly string[] PromptAttributeKeys =
    {
        "gen_ai.prompt",
    };

    /// <summary>
    /// Returns a one-line prompt label for <paramref name="traceId"/> from the
    /// raw OTLP <paramref name="payloadJson"/>, or <c>null</c> when no prompt is
    /// present or the payload cannot be parsed.
    /// </summary>
    public static string? ExtractPromptLabel(string? payloadJson, string traceId)
    {
        if (string.IsNullOrEmpty(payloadJson) || string.IsNullOrEmpty(traceId))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var raw = FindPromptText(document.RootElement, traceId);
            return raw is null ? null : Normalize(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindPromptText(JsonElement root, string traceId)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // A raw record may hold spans from more than one trace, so a prompt only
        // counts when it belongs to the requested trace. Spans that omit a
        // traceId are kept only as a last-resort fallback for single-trace
        // payloads (used when no span in the payload names any trace id at all).
        string? singleTraceFallback = null;
        var sawAnyTraceId = false;

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            if (resourceSpan.ValueKind != JsonValueKind.Object
                || !resourceSpan.TryGetProperty("scopeSpans", out var scopeSpans)
                || scopeSpans.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var scopeSpan in scopeSpans.EnumerateArray())
            {
                if (scopeSpan.ValueKind != JsonValueKind.Object
                    || !scopeSpan.TryGetProperty("spans", out var spans)
                    || spans.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var span in spans.EnumerateArray())
                {
                    if (span.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var spanTraceId = span.TryGetProperty("traceId", out var traceIdElement)
                        && traceIdElement.ValueKind == JsonValueKind.String
                            ? traceIdElement.GetString()
                            : null;
                    if (spanTraceId is not null)
                    {
                        sawAnyTraceId = true;
                    }

                    var prompt = ExtractFromAttributes(span);
                    if (prompt is null)
                    {
                        continue;
                    }

                    if (string.Equals(spanTraceId, traceId, StringComparison.Ordinal))
                    {
                        return prompt;
                    }

                    if (spanTraceId is null)
                    {
                        singleTraceFallback ??= prompt;
                    }
                }
            }
        }

        return sawAnyTraceId ? null : singleTraceFallback;
    }

    private static string? ExtractFromAttributes(JsonElement span)
    {
        if (!span.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var key in PromptAttributeKeys)
        {
            foreach (var attribute in attributes.EnumerateArray())
            {
                if (attribute.ValueKind == JsonValueKind.Object
                    && attribute.TryGetProperty("key", out var keyElement)
                    && keyElement.ValueKind == JsonValueKind.String
                    && string.Equals(keyElement.GetString(), key, StringComparison.Ordinal)
                    && attribute.TryGetProperty("value", out var valueElement)
                    && valueElement.ValueKind == JsonValueKind.Object
                    && valueElement.TryGetProperty("stringValue", out var stringValue)
                    && stringValue.ValueKind == JsonValueKind.String)
                {
                    var text = stringValue.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }

        return null;
    }

    private static string Normalize(string text)
    {
        // Collapse every whitespace run (including newlines) to a single space so
        // the label stays a clean one-liner regardless of the captured formatting.
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var collapsed = builder.ToString();
        return collapsed.Length <= MaxLabelLength
            ? collapsed
            : collapsed[..MaxLabelLength].TrimEnd() + "…";
    }
}

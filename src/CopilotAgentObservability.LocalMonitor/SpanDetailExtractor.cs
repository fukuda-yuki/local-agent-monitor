namespace CopilotAgentObservability.LocalMonitor;

/// <summary>Best-effort tool call detail: arguments, result tail, and a chars/4 token estimate (labelled 推定 in the UI).</summary>
internal sealed record SpanDetailToolInfo(
    string? Arguments,
    string? ResultTail,
    int? ResultTokenEstimate,
    string? ExitCode);

/// <summary>One input message for the llm formatted view.</summary>
internal sealed record SpanDetailMessage(
    string Role,
    int SizeChars,
    int TokenEstimate,
    string Preview);

/// <summary>Best-effort llm call detail: per-role input sizes/previews and the response preview.</summary>
internal sealed record SpanDetailLlmInfo(
    IReadOnlyList<SpanDetailMessage> Messages,
    string? ResponsePreview,
    int? ResponseTokenEstimate);

/// <summary>
/// Raw span detail for the inspector route (D043): the raw OTLP span JSON plus
/// whatever formatted tool / llm sections could be extracted.
/// </summary>
internal sealed record SpanDetail(
    string RawSpanJson,
    SpanDetailToolInfo? Tool,
    SpanDetailLlmInfo? Llm);

/// <summary>
/// Extracts the span inspector's raw-bearing detail (D043) from a raw OTLP
/// payload, modeled on <see cref="MonitorPromptExtractor"/>: pure,
/// exception-safe, best-effort. The real VS Code Copilot payload key names are
/// pending live validation, so every formatted section is optional — the raw
/// span JSON is always returned when the span exists, which keeps the raw tab
/// working even when formatted extraction finds nothing.
/// </summary>
internal static class SpanDetailExtractor
{
    private const int ArgumentsMaxChars = 2_000;
    private const int ResultTailMaxLines = 20;
    private const int ResultTailMaxChars = 2_000;
    private const int PreviewMaxChars = 300;

    private static readonly string[] ToolArgumentKeys =
    {
        "gen_ai.tool.arguments",
        "gen_ai.tool.call.arguments",
        "github.copilot.tool.arguments",
        "github.copilot.tool.parameters.arguments",
    };

    private static readonly string[] ToolResultKeys =
    {
        "gen_ai.tool.result",
        "gen_ai.tool.output",
        "github.copilot.tool.result",
    };

    private static readonly string[] ExitCodeKeys =
    {
        "gen_ai.tool.exit_code",
        "process.exit_code",
        "exit_code",
    };

    private static readonly (string Key, string Role)[] MessageKeys =
    {
        ("gen_ai.system_instructions", "system"),
        ("gen_ai.system.prompt", "system"),
        ("gen_ai.prompt", "user"),
        ("gen_ai.user.message", "user"),
    };

    private static readonly string[] ResponseKeys =
    {
        "gen_ai.completion",
        "gen_ai.response.text",
        "gen_ai.assistant.message",
    };

    /// <summary>
    /// Finds the span with <paramref name="spanId"/> (scoped to
    /// <paramref name="traceId"/> when spans carry trace ids) in the raw OTLP
    /// payload and returns its detail, or <c>null</c> when the span is absent or
    /// the payload cannot be parsed.
    /// </summary>
    public static SpanDetail? Extract(string? payloadJson, string traceId, string spanId)
    {
        if (string.IsNullOrEmpty(payloadJson) || string.IsNullOrEmpty(spanId))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            foreach (var span in EnumerateSpans(document.RootElement))
            {
                if (!MatchesIds(span, traceId, spanId))
                {
                    continue;
                }

                return new SpanDetail(
                    RawSpanJson: span.GetRawText(),
                    Tool: ExtractTool(span),
                    Llm: ExtractLlm(span));
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> EnumerateSpans(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

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
                    if (span.ValueKind == JsonValueKind.Object)
                    {
                        yield return span;
                    }
                }
            }
        }
    }

    private static bool MatchesIds(JsonElement span, string traceId, string spanId)
    {
        if (!span.TryGetProperty("spanId", out var spanIdElement)
            || spanIdElement.ValueKind != JsonValueKind.String
            || !string.Equals(spanIdElement.GetString(), spanId, StringComparison.Ordinal))
        {
            return false;
        }

        // A payload can hold spans from multiple traces; when the span names a
        // trace it must be the requested one. Spans without a traceId still match
        // (single-trace payload fallback, same posture as the prompt extractor).
        if (span.TryGetProperty("traceId", out var traceIdElement)
            && traceIdElement.ValueKind == JsonValueKind.String)
        {
            return string.Equals(traceIdElement.GetString(), traceId, StringComparison.Ordinal);
        }

        return true;
    }

    private static SpanDetailToolInfo? ExtractTool(JsonElement span)
    {
        var arguments = FirstAttributeString(span, ToolArgumentKeys);
        var result = FirstAttributeString(span, ToolResultKeys);
        var exitCode = FirstAttributeScalar(span, ExitCodeKeys);
        if (arguments is null && result is null && exitCode is null)
        {
            return null;
        }

        return new SpanDetailToolInfo(
            Arguments: Truncate(arguments, ArgumentsMaxChars),
            ResultTail: TailLines(result),
            ResultTokenEstimate: result is null ? null : Math.Max(1, result.Length / 4),
            ExitCode: exitCode);
    }

    private static SpanDetailLlmInfo? ExtractLlm(JsonElement span)
    {
        var messages = new List<SpanDetailMessage>();
        foreach (var (key, role) in MessageKeys)
        {
            var text = FirstAttributeString(span, [key]);
            if (text is not null)
            {
                messages.Add(new SpanDetailMessage(
                    Role: role,
                    SizeChars: text.Length,
                    TokenEstimate: Math.Max(1, text.Length / 4),
                    Preview: Truncate(text, PreviewMaxChars)!));
            }
        }

        var response = FirstAttributeString(span, ResponseKeys);
        if (messages.Count == 0 && response is null)
        {
            return null;
        }

        return new SpanDetailLlmInfo(
            Messages: messages,
            ResponsePreview: Truncate(response, PreviewMaxChars),
            ResponseTokenEstimate: response is null ? null : Math.Max(1, response.Length / 4));
    }

    private static string? FirstAttributeString(JsonElement span, IReadOnlyList<string> keys)
    {
        if (!span.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var key in keys)
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

    /// <summary>String or int attribute value as text (exit codes arrive both ways).</summary>
    private static string? FirstAttributeScalar(JsonElement span, IReadOnlyList<string> keys)
    {
        if (!span.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var key in keys)
        {
            foreach (var attribute in attributes.EnumerateArray())
            {
                if (attribute.ValueKind != JsonValueKind.Object
                    || !attribute.TryGetProperty("key", out var keyElement)
                    || keyElement.ValueKind != JsonValueKind.String
                    || !string.Equals(keyElement.GetString(), key, StringComparison.Ordinal)
                    || !attribute.TryGetProperty("value", out var valueElement)
                    || valueElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (valueElement.TryGetProperty("intValue", out var intValue))
                {
                    return intValue.ValueKind switch
                    {
                        JsonValueKind.String => intValue.GetString(),
                        JsonValueKind.Number => intValue.GetRawText(),
                        _ => null,
                    };
                }

                if (valueElement.TryGetProperty("stringValue", out var stringValue)
                    && stringValue.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(stringValue.GetString()))
                {
                    return stringValue.GetString();
                }
            }
        }

        return null;
    }

    private static string? Truncate(string? text, int maxChars)
    {
        if (text is null)
        {
            return null;
        }

        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }

    private static string? TailLines(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var lines = text.Split('\n');
        var tail = lines.Length <= ResultTailMaxLines
            ? text
            : string.Join('\n', lines[^ResultTailMaxLines..]);
        return tail.Length <= ResultTailMaxChars ? tail : tail[^ResultTailMaxChars..];
    }
}

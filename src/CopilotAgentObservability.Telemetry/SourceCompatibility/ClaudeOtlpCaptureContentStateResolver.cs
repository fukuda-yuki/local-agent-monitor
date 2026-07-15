using System.Text.Json;

namespace CopilotAgentObservability.Telemetry;

/// <summary>
/// Derives <see cref="SourceCaptureContentState"/> per raw OTLP ingest batch from
/// field presence in the decoded payload, per the pinned rule in
/// docs/specifications/interfaces/source-schema-drift-claude-code.md. Only
/// batches containing at least one recognized <c>claude_code.*</c> span carry
/// derivable evidence; every other batch keeps the surface's fixed provider
/// metadata state (the caller falls back on a <see langword="null"/> result).
/// Inspects attribute/event key presence only — content values are never read.
/// </summary>
public static class ClaudeOtlpCaptureContentStateResolver
{
    public static SourceCaptureContentState? Derive(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        using var document = JsonDocument.Parse(payloadJson);
        var hasClaudeSpan = false;
        var hasContentField = false;

        foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(document.RootElement, "resourceSpans"))
        {
            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    var name = OtlpSpanReader.ReadString(span, "name");
                    if (name is null || !ClaudeCodeSpanAdapter.RecognizedSpanNames.Contains(name))
                    {
                        continue;
                    }

                    hasClaudeSpan = true;

                    if (string.Equals(name, "claude_code.interaction", StringComparison.Ordinal)
                        && HasAttribute(span, "user_prompt"))
                    {
                        hasContentField = true;
                    }

                    if (string.Equals(name, "claude_code.tool", StringComparison.Ordinal)
                        && (HasAttribute(span, "file_path") || HasEvent(span, "tool.output")))
                    {
                        hasContentField = true;
                    }
                }
            }
        }

        if (!hasClaudeSpan)
        {
            return null;
        }

        return hasContentField ? SourceCaptureContentState.Available : SourceCaptureContentState.NotCaptured;
    }

    private static bool HasAttribute(JsonElement span, string key)
    {
        foreach (var attribute in OtlpSpanReader.EnumerateArrayProperty(span, "attributes"))
        {
            if (string.Equals(OtlpSpanReader.ReadString(attribute, "key"), key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEvent(JsonElement span, string name)
    {
        foreach (var spanEvent in OtlpSpanReader.EnumerateArrayProperty(span, "events"))
        {
            if (string.Equals(OtlpSpanReader.ReadString(spanEvent, "name"), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

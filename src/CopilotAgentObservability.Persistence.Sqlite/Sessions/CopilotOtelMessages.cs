using System.Text.Json;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

internal static class CopilotOtelMessages
{
    internal sealed record Identity(SessionSourceSurface Surface, string NativeId);
    internal sealed record Message(string Direction, int Ordinal, string Type, SessionContentState State, string? Json);

    internal static SessionSourceSurface? ReadSurface(string payload, string traceId) =>
        OtlpTraceSourceResolver.Resolve(payload).SingleOrDefault(item => item.TraceId == traceId)?.SourceFamily switch
        {
            "copilot-cli" => SessionSourceSurface.CopilotCli,
            "vscode-copilot-chat" => SessionSourceSurface.VisualStudioCode,
            _ => null,
        };

    internal static Identity? ReadIdentity(string payload, string traceId, string spanId)
    {
        if (ReadSurface(payload, traceId) is not { } admittedSurface) return null;
        using var document = JsonDocument.Parse(payload);
        var matches = Spans(document.RootElement, traceId, spanId, admittedSurface).ToArray();
        if (matches.Length != 1) return null;
        var (surface, span) = matches[0];
        var values = Attributes(span, "gen_ai.conversation.id").ToArray();
        if (values.Length != 1 || values[0].ValueKind != JsonValueKind.String) return null;
        var nativeId = values[0].GetString();
        return string.IsNullOrWhiteSpace(nativeId) || SessionSecretFilter.IsSensitiveCarrier(nativeId)
            ? null : new(surface, nativeId);
    }

    internal static IReadOnlyList<Message> Read(string payload, string traceId, string spanId)
    {
        if (ReadSurface(payload, traceId) is not { } admittedSurface) return [];
        using var document = JsonDocument.Parse(payload);
        var matches = Spans(document.RootElement, traceId, spanId, admittedSurface).ToArray();
        if (matches.Length != 1) return [];
        var result = new List<Message>();
        foreach (var direction in new[] { "input", "output" })
        {
            var values = Attributes(matches[0].Span, $"gen_ai.{direction}.messages").ToArray();
            if (values.Length == 0) continue;
            var type = direction == "input" ? "user.message" : "assistant.message";
            if (values.Length != 1 || values[0].ValueKind != JsonValueKind.String)
            {
                result.Add(new(direction, 0, type, SessionContentState.Unsupported, null));
                continue;
            }
            try
            {
                using var messages = JsonDocument.Parse(values[0].GetString()!);
                if (messages.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
                var ordinal = 0;
                var selected = new List<Message>();
                foreach (var message in messages.RootElement.EnumerateArray())
                {
                    var index = ordinal++;
                    if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var role)
                        || role.ValueKind != JsonValueKind.String)
                    {
                        selected.Add(new(direction, index, "otel.message", SessionContentState.Unsupported, null));
                        continue;
                    }
                    if (role.GetString() != (direction == "input" ? "user" : "assistant")) continue;
                    var text = new List<string>();
                    var supported = message.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array;
                    if (supported)
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var partType)
                                || partType.ValueKind != JsonValueKind.String) { supported = false; continue; }
                            if (partType.GetString() != "text") continue;
                            if (!part.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                                supported = false;
                            else text.Add(content.GetString()!);
                        }
                    if (direction == "input") selected.Clear();
                    if (!supported) selected.Add(new(direction, index, type, SessionContentState.Unsupported, null));
                    else if (text.Count == 0) selected.Add(new(direction, index, type, SessionContentState.Unsupported, null));
                    else
                    {
                        var value = string.Concat(text);
                        using var content = JsonDocument.Parse(direction == "input"
                            ? JsonSerializer.Serialize(new { value }) : JsonSerializer.Serialize(value));
                        selected.Add(new(direction, index, type, SessionContentState.Available, SessionSecretFilter.Filter(type, content.RootElement)));
                    }
                }
                result.AddRange(selected);
            }
            catch (JsonException) { result.Add(new(direction, 0, type, SessionContentState.Unsupported, null)); }
        }
        var operations = Attributes(matches[0].Span, "gen_ai.operation.name").ToArray();
        if (operations is [{ ValueKind: JsonValueKind.String }] && operations[0].GetString() == "execute_tool")
        {
            foreach (var (attribute, direction, type, property) in new[]
            {
                ("gen_ai.tool.call.arguments", "tool_input", "otel.tool.input", "tool_input"),
                ("gen_ai.tool.call.result", "tool_result", "otel.tool.result", "tool_response"),
            })
            {
                var values = Attributes(matches[0].Span, attribute).ToArray();
                if (values.Length == 0) continue;
                var supported = values.Length == 1 && values[0].ValueKind == JsonValueKind.String;
                result.Add(new(direction, 0, type, supported ? SessionContentState.Available : SessionContentState.Unsupported,
                    supported ? JsonSerializer.Serialize(new Dictionary<string, string?> { [property] = values[0].GetString() }) : null));
            }
        }
        return result;
    }

    private static IEnumerable<(SessionSourceSurface Surface, JsonElement Span)> Spans(JsonElement root, string traceId, string spanId, SessionSourceSurface surface)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("resourceSpans", out var resources) || resources.ValueKind != JsonValueKind.Array) yield break;
        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object || !resource.TryGetProperty("scopeSpans", out var scopes) || scopes.ValueKind != JsonValueKind.Array) continue;
            foreach (var scope in scopes.EnumerateArray())
            {
                if (scope.ValueKind != JsonValueKind.Object || !scope.TryGetProperty("spans", out var spans) || spans.ValueKind != JsonValueKind.Array) continue;
                foreach (var span in spans.EnumerateArray())
                    if (span.ValueKind == JsonValueKind.Object && span.TryGetProperty("traceId", out var trace) && trace.ValueKind == JsonValueKind.String && trace.GetString() == traceId
                        && span.TryGetProperty("spanId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == spanId)
                        yield return (surface, span);
            }
        }
    }

    private static IEnumerable<JsonElement> Attributes(JsonElement owner, string key)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array) yield break;
        foreach (var attribute in attributes.EnumerateArray())
            if (attribute.ValueKind == JsonValueKind.Object && attribute.TryGetProperty("key", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == key
                && attribute.TryGetProperty("value", out var value))
                yield return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("stringValue", out var text) ? text : value;
    }
}

using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Setup.Documents;

internal sealed record ClaudeSettingsEnvValue(string Key, string Value);

internal sealed record ClaudeSettingsHook(
    string EventName,
    string Command,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds);

internal enum ClaudeSettingsPlanDisposition
{
    NoOp,
    Change,
    HookCommandConflict,
}

internal sealed record ClaudeSettingsPlanResult(
    ClaudeSettingsPlanDisposition Disposition,
    string? RenderedContent);

internal sealed class ClaudeSettingsDocument
{
    private const string InvalidDocumentMessage = "Settings document is malformed or unsupported.";
    private const int MaximumDocumentBytes = 1024 * 1024;

    private ClaudeSettingsDocument(string content)
    {
        Content = content;
    }

    public string Content { get; }

    public static ClaudeSettingsDocument Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > MaximumDocumentBytes)
        {
            throw InvalidDocument();
        }

        try
        {
            ValidateNoDuplicateProperties(content);
            using var document = JsonDocument.Parse(content, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidDocument();
            }

            _ = ObjectEditor.Parse(content);
            return new ClaudeSettingsDocument(content);
        }
        catch (FormatException exception) when (exception.Message == InvalidDocumentMessage)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw InvalidDocument();
        }
    }

    public ClaudeSettingsPlanResult Plan(
        IReadOnlyList<ClaudeSettingsEnvValue> ownedEnv,
        IReadOnlyList<ClaudeSettingsHook> ownedHooks)
    {
        ValidateDesired(ownedEnv, ownedHooks);
        var rendered = Content;

        var root = ObjectEditor.Parse(rendered);
        var env = root.TryGetRaw("env", out var existingEnv)
            ? ObjectEditor.Parse(existingEnv!)
            : ObjectEditor.Parse("{}");
        foreach (var desired in ownedEnv)
        {
            if (env.TryGetRaw(desired.Key, out var rawValue))
            {
                using var value = JsonDocument.Parse(rawValue!);
                if (value.RootElement.ValueKind != JsonValueKind.String)
                {
                    throw InvalidDocument();
                }

                if (value.RootElement.GetString() != desired.Value)
                {
                    env = ObjectEditor.Parse(env.SetRaw(desired.Key, JsonSerializer.Serialize(desired.Value)));
                }
            }
            else
            {
                env = ObjectEditor.Parse(env.SetRaw(desired.Key, JsonSerializer.Serialize(desired.Value)));
            }
        }

        rendered = ObjectEditor.Parse(rendered).SetRaw("env", env.Content);

        root = ObjectEditor.Parse(rendered);
        var hooks = root.TryGetRaw("hooks", out var existingHooks)
            ? ObjectEditor.Parse(existingHooks!)
            : ObjectEditor.Parse("{}");
        foreach (var desired in ownedHooks)
        {
            var desiredGroup = SerializeHookGroup(desired);
            if (!hooks.TryGetRaw(desired.EventName, out var eventArray))
            {
                hooks = ObjectEditor.Parse(hooks.SetRaw(desired.EventName, $"[{desiredGroup}]"));
                continue;
            }

            using var eventDocument = JsonDocument.Parse(eventArray!, JsonOptions);
            if (eventDocument.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidDocument();
            }

            var exactOwnedCount = 0;
            foreach (var group in eventDocument.RootElement.EnumerateArray())
            {
                if (!IsOwnedCandidate(group))
                {
                    continue;
                }

                if (!IsExactOwned(group, desired))
                {
                    return new ClaudeSettingsPlanResult(ClaudeSettingsPlanDisposition.HookCommandConflict, null);
                }

                exactOwnedCount++;
                if (exactOwnedCount > 1)
                {
                    return new ClaudeSettingsPlanResult(ClaudeSettingsPlanDisposition.HookCommandConflict, null);
                }
            }

            if (exactOwnedCount == 0)
            {
                hooks = ObjectEditor.Parse(hooks.SetRaw(
                    desired.EventName,
                    AppendArray(eventArray!, desiredGroup)));
            }
        }

        rendered = ObjectEditor.Parse(rendered).SetRaw("hooks", hooks.Content);
        Parse(rendered);
        return new ClaudeSettingsPlanResult(
            rendered == Content ? ClaudeSettingsPlanDisposition.NoOp : ClaudeSettingsPlanDisposition.Change,
            rendered);
    }

    private static JsonDocumentOptions JsonOptions => new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static void ValidateDesired(
        IReadOnlyList<ClaudeSettingsEnvValue> ownedEnv,
        IReadOnlyList<ClaudeSettingsHook> ownedHooks)
    {
        ArgumentNullException.ThrowIfNull(ownedEnv);
        ArgumentNullException.ThrowIfNull(ownedHooks);
        if (ownedEnv.Any(value => value is null || string.IsNullOrEmpty(value.Key) || value.Value is null) ||
            ownedEnv.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != ownedEnv.Count ||
            ownedHooks.Any(hook => hook is null || string.IsNullOrEmpty(hook.EventName) ||
                string.IsNullOrEmpty(hook.Command) || hook.Arguments is null ||
                hook.Arguments.Any(argument => argument is null) || hook.TimeoutSeconds != 5) ||
            ownedHooks.Select(hook => hook.EventName).Distinct(StringComparer.Ordinal).Count() != ownedHooks.Count)
        {
            throw new ArgumentException("Owned Claude settings are invalid.");
        }
    }

    private static bool IsOwnedCandidate(JsonElement group)
    {
        if (group.ValueKind != JsonValueKind.Object ||
            !group.TryGetProperty("hooks", out var handlers) ||
            handlers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return handlers.EnumerateArray().Any(handler =>
            handler.ValueKind == JsonValueKind.Object &&
            handler.TryGetProperty("args", out var arguments) &&
            arguments.ValueKind == JsonValueKind.Array &&
            ContainsOwnedSelector(arguments));
    }

    private static bool ContainsOwnedSelector(JsonElement arguments)
    {
        var values = arguments.EnumerateArray()
            .Where(argument => argument.ValueKind == JsonValueKind.String)
            .Select(argument => argument.GetString())
            .ToArray();
        return values.Contains("hook-forward", StringComparer.Ordinal) &&
            Enumerable.Range(0, Math.Max(0, values.Length - 1))
                .Any(index => values[index] == "--source" && values[index + 1] == "claude-code");
    }

    private static bool IsExactOwned(JsonElement group, ClaudeSettingsHook desired)
    {
        if (group.EnumerateObject().Count() != 1 ||
            !group.TryGetProperty("hooks", out var hooks) ||
            hooks.ValueKind != JsonValueKind.Array ||
            hooks.GetArrayLength() != 1)
        {
            return false;
        }

        var handler = hooks[0];
        if (handler.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (handler.EnumerateObject().Count() != 4 ||
            !handler.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "command" ||
            !handler.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String || command.GetString() != desired.Command ||
            !handler.TryGetProperty("args", out var argumentsElement) || argumentsElement.ValueKind != JsonValueKind.Array ||
            !handler.TryGetProperty("timeout", out var timeoutElement) || timeoutElement.ValueKind != JsonValueKind.Number ||
            !timeoutElement.TryGetInt32(out var timeout) || timeout != desired.TimeoutSeconds)
        {
            return false;
        }

        var arguments = argumentsElement.EnumerateArray().ToArray();
        return arguments.Length == desired.Arguments.Count &&
            arguments.Select(argument => argument.ValueKind == JsonValueKind.String ? argument.GetString() : null)
                .SequenceEqual(desired.Arguments, StringComparer.Ordinal);
    }

    private static string SerializeHookGroup(ClaudeSettingsHook hook)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("hooks");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "command");
            writer.WriteString("command", hook.Command);
            writer.WritePropertyName("args");
            writer.WriteStartArray();
            foreach (var argument in hook.Arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            writer.WriteNumber("timeout", hook.TimeoutSeconds);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string AppendArray(string content, string serializedValue)
    {
        var scanner = new Scanner(content);
        var array = scanner.ScanRootArray();
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var multiline = content.Contains('\n') || content.Contains('\r');
        if (!multiline)
        {
            var separator = array.Count == 0 ? string.Empty : array.HasTrailingComma ? " " : ", ";
            return content.Insert(array.ClosingBracket, $"{separator}{serializedValue}");
        }

        var closingIndentStart = array.ClosingBracket;
        while (closingIndentStart > 0 && content[closingIndentStart - 1] is ' ' or '\t')
        {
            closingIndentStart--;
        }

        var indentation = array.Count == 0 ? "  " : DetectValueIndentation(content, array.FirstValueStart);
        var prefix = array.Count == 0 || array.HasTrailingComma ? string.Empty : ",";
        return content.Insert(closingIndentStart, $"{prefix}{newline}{indentation}{serializedValue}");
    }

    private static string DetectValueIndentation(string content, int valueStart)
    {
        var lineStart = valueStart;
        while (lineStart > 0 && content[lineStart - 1] is not '\r' and not '\n')
        {
            lineStart--;
        }

        var indentation = content[lineStart..valueStart];
        return indentation.All(character => character is ' ' or '\t') ? indentation : "  ";
    }

    private static void ValidateNoDuplicateProperties(string content)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(content), new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objects.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                objects.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName &&
                (!objects.TryPeek(out var properties) || !properties.Add(reader.GetString() ?? throw InvalidDocument())))
            {
                throw InvalidDocument();
            }
        }
    }

    private static FormatException InvalidDocument() => new(InvalidDocumentMessage);

    private sealed class ObjectEditor
    {
        private readonly IReadOnlyList<PropertySpan> properties;
        private readonly int closingBrace;

        private ObjectEditor(string content, IReadOnlyList<PropertySpan> properties, int closingBrace)
        {
            Content = content;
            this.properties = properties;
            this.closingBrace = closingBrace;
        }

        public string Content { get; }

        public static ObjectEditor Parse(string content)
        {
            using var document = JsonDocument.Parse(content, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidDocument();
            }

            var scanner = new Scanner(content);
            return new ObjectEditor(content, scanner.ScanRootObject(out var closingBrace), closingBrace);
        }

        public bool TryGetRaw(string key, out string? raw)
        {
            var matches = properties.Where(property => property.Name == key).ToArray();
            if (matches.Length > 1)
            {
                throw InvalidDocument();
            }

            if (matches.Length == 0)
            {
                raw = null;
                return false;
            }

            raw = Content[matches[0].ValueStart..matches[0].ValueEnd];
            return true;
        }

        public string SetRaw(string key, string raw)
        {
            if (TryGetRaw(key, out var existing))
            {
                if (existing == raw)
                {
                    return Content;
                }

                var target = properties.Single(property => property.Name == key);
                return string.Concat(Content.AsSpan(0, target.ValueStart), raw, Content.AsSpan(target.ValueEnd));
            }

            var serializedKey = JsonSerializer.Serialize(key);
            var newline = Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var multiline = Content.AsSpan(0, closingBrace).Contains('\n') || Content.AsSpan(0, closingBrace).Contains('\r');
            if (properties.Count == 0)
            {
                if (!multiline)
                {
                    return Content.Insert(closingBrace, $" {serializedKey}: {raw} ");
                }

                var insertAt = ClosingIndentStart();
                return Content.Insert(insertAt, $"  {serializedKey}: {raw}{newline}");
            }

            var last = properties[^1];
            var spacing = DetectColonSpacing(last);
            if (!multiline)
            {
                return last.CommaIndex is int trailingComma
                    ? Content.Insert(trailingComma + 1, $" {serializedKey}{spacing}{raw},")
                    : Content.Insert(last.ValueEnd, $", {serializedKey}{spacing}{raw}");
            }

            var indentation = DetectIndentation();
            var insertion = $"{indentation}{serializedKey}{spacing}{raw}{(last.CommaIndex is null ? string.Empty : ",")}{newline}";
            var withProperty = Content.Insert(ClosingIndentStart(), insertion);
            return last.CommaIndex is null ? withProperty.Insert(last.ValueEnd, ",") : withProperty;
        }

        private string DetectColonSpacing(PropertySpan property)
        {
            var candidate = Content[property.KeyEnd..property.ValueStart];
            return candidate.Count(character => character == ':') == 1 &&
                candidate.All(character => character is ':' or ' ' or '\t') ? candidate : ": ";
        }

        private string DetectIndentation()
        {
            var lineStart = properties[0].KeyStart;
            while (lineStart > 0 && Content[lineStart - 1] is not '\r' and not '\n')
            {
                lineStart--;
            }

            var indentation = Content[lineStart..properties[0].KeyStart];
            return indentation.All(character => character is ' ' or '\t') ? indentation : "  ";
        }

        private int ClosingIndentStart()
        {
            var start = closingBrace;
            while (start > 0 && Content[start - 1] is ' ' or '\t')
            {
                start--;
            }

            return start;
        }
    }

    private sealed record PropertySpan(
        string Name,
        int KeyStart,
        int KeyEnd,
        int ValueStart,
        int ValueEnd,
        int? CommaIndex);

    private sealed record ArrayScanResult(
        int Count,
        int FirstValueStart,
        bool HasTrailingComma,
        int ClosingBracket);

    private sealed class Scanner(string content)
    {
        private int position;

        public IReadOnlyList<PropertySpan> ScanRootObject(out int closingBrace)
        {
            SkipTrivia();
            Expect('{');
            var properties = new List<PropertySpan>();
            SkipTrivia();
            while (Peek() != '}')
            {
                var keyStart = position;
                var keyEnd = ScanString();
                var name = JsonSerializer.Deserialize<string>(content[keyStart..keyEnd]) ?? throw InvalidDocument();
                SkipTrivia();
                Expect(':');
                SkipTrivia();
                var valueStart = position;
                var valueEnd = ScanValue();
                SkipTrivia();
                int? comma = null;
                if (Peek() == ',')
                {
                    comma = position++;
                    SkipTrivia();
                    properties.Add(new PropertySpan(name, keyStart, keyEnd, valueStart, valueEnd, comma));
                    if (Peek() == '}')
                    {
                        break;
                    }

                    continue;
                }

                properties.Add(new PropertySpan(name, keyStart, keyEnd, valueStart, valueEnd, comma));
                break;
            }

            closingBrace = position;
            Expect('}');
            Finish();
            return properties;
        }

        public ArrayScanResult ScanRootArray()
        {
            SkipTrivia();
            Expect('[');
            SkipTrivia();
            var count = 0;
            var firstValueStart = position;
            var trailingComma = false;
            while (Peek() != ']')
            {
                if (count == 0)
                {
                    firstValueStart = position;
                }

                ScanValue();
                count++;
                SkipTrivia();
                if (Peek() != ',')
                {
                    trailingComma = false;
                    break;
                }

                position++;
                trailingComma = true;
                SkipTrivia();
                if (Peek() == ']')
                {
                    break;
                }

                trailingComma = false;
            }

            var closingBracket = position;
            Expect(']');
            Finish();
            return new ArrayScanResult(count, firstValueStart, trailingComma, closingBracket);
        }

        private void Finish()
        {
            SkipTrivia();
            if (position != content.Length)
            {
                throw InvalidDocument();
            }
        }

        private int ScanValue() => Peek() switch
        {
            '"' => ScanString(),
            '{' or '[' => ScanContainer(),
            _ => ScanPrimitive(),
        };

        private int ScanContainer()
        {
            var stack = new Stack<char>();
            stack.Push(content[position++] == '{' ? '}' : ']');
            while (stack.Count > 0 && position < content.Length)
            {
                if (content[position] == '"')
                {
                    ScanString();
                }
                else if (content[position] == '/' && position + 1 < content.Length && content[position + 1] is '/' or '*')
                {
                    SkipComment();
                }
                else if (content[position] is '{' or '[')
                {
                    stack.Push(content[position++] == '{' ? '}' : ']');
                }
                else
                {
                    if (content[position] == stack.Peek())
                    {
                        stack.Pop();
                    }

                    position++;
                }
            }

            return position;
        }

        private int ScanPrimitive()
        {
            while (position < content.Length && !char.IsWhiteSpace(content[position]) && content[position] is not ',' and not '}' and not ']')
            {
                if (content[position] == '/')
                {
                    break;
                }

                position++;
            }

            return position;
        }

        private int ScanString()
        {
            Expect('"');
            while (position < content.Length)
            {
                if (content[position] == '\\')
                {
                    position += 2;
                }
                else if (content[position++] == '"')
                {
                    return position;
                }
            }

            throw InvalidDocument();
        }

        private void SkipTrivia()
        {
            while (position < content.Length)
            {
                if (char.IsWhiteSpace(content[position]))
                {
                    position++;
                }
                else if (content[position] == '/' && position + 1 < content.Length && content[position + 1] is '/' or '*')
                {
                    SkipComment();
                }
                else
                {
                    break;
                }
            }
        }

        private void SkipComment()
        {
            if (content[position + 1] == '/')
            {
                position += 2;
                while (position < content.Length && content[position] is not '\r' and not '\n')
                {
                    position++;
                }

                return;
            }

            position += 2;
            var end = content.IndexOf("*/", position, StringComparison.Ordinal);
            if (end < 0)
            {
                throw InvalidDocument();
            }

            position = end + 2;
        }

        private char Peek() => position < content.Length ? content[position] : '\0';

        private void Expect(char expected)
        {
            if (Peek() != expected)
            {
                throw InvalidDocument();
            }

            position++;
        }
    }
}

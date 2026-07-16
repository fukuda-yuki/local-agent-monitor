using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Setup.Documents;

internal sealed class JsoncSettingsDocument
{
    private const string InvalidDocumentMessage = "Settings document is malformed or unsupported.";
    private readonly IReadOnlyList<PropertySpan> properties;
    private readonly int closingBrace;

    private JsoncSettingsDocument(string content, IReadOnlyList<PropertySpan> properties, int closingBrace)
    {
        Content = content;
        this.properties = properties;
        this.closingBrace = closingBrace;
    }

    public string Content { get; }

    public static JsoncSettingsDocument Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidDocument();
            }

            var scanner = new Scanner(content);
            var properties = scanner.ScanRootObject(out var closingBrace);
            return new JsoncSettingsDocument(content, properties, closingBrace);
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

    public bool TryGetString(string key, out string? value)
    {
        var property = FindUnique(key, required: false);
        if (property is null || !TryReadScalar(property, JsonValueKind.String, out var element))
        {
            value = null;
            return false;
        }

        value = element.GetString();
        return true;
    }

    public bool TryGetBoolean(string key, out bool value)
    {
        var property = FindUnique(key, required: false);
        if (property is null || !TryReadScalar(property, JsonValueKind.True, out var element, JsonValueKind.False))
        {
            value = default;
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    public string AddString(string key, string value) => Add(key, JsonSerializer.Serialize(value));

    public string AddBoolean(string key, bool value) => Add(key, value ? "true" : "false");

    public string ReplaceString(string key, string value) => Replace(key, JsonSerializer.Serialize(value), JsonValueKind.String);

    public string ReplaceBoolean(string key, bool value) => Replace(key, value ? "true" : "false", JsonValueKind.True, JsonValueKind.False);

    public string Remove(string key)
    {
        var target = FindUnique(key, required: true)!;
        var index = 0;
        while (properties[index] != target)
        {
            index++;
        }
        if (target.CommaIndex is int followingComma)
        {
            return string.Concat(
                Content.AsSpan(0, target.KeyStart),
                Content.AsSpan(target.ValueEnd, followingComma - target.ValueEnd),
                Content.AsSpan(followingComma + 1));
        }

        if (index > 0 && properties[index - 1].CommaIndex is int precedingComma)
        {
            return string.Concat(
                Content.AsSpan(0, precedingComma),
                Content.AsSpan(precedingComma + 1, target.KeyStart - precedingComma - 1),
                Content.AsSpan(target.ValueEnd));
        }

        return Content.Remove(target.KeyStart, target.ValueEnd - target.KeyStart);
    }

    private string Add(string key, string serializedValue)
    {
        ValidateKey(key);
        if (FindUnique(key, required: false) is not null)
        {
            throw new InvalidOperationException("The setting already exists.");
        }

        var serializedKey = JsonSerializer.Serialize(key);
        var newline = DetectNewline();
        var multiline = Content.AsSpan(0, closingBrace).Contains('\n') || Content.AsSpan(0, closingBrace).Contains('\r');
        if (properties.Count == 0)
        {
            if (!multiline)
            {
                return Content.Insert(closingBrace, $" {serializedKey}: {serializedValue} ");
            }

            var indentation = DetectIndentation();
            var insertAt = ClosingIndentStart();
            return Content.Insert(insertAt, $"{indentation}{serializedKey}: {serializedValue}{newline}");
        }

        var last = properties[^1];
        var colonSpacing = DetectColonSpacing(last);
        if (!multiline)
        {
            if (last.CommaIndex is int trailingComma)
            {
                return Content.Insert(trailingComma + 1, $" {serializedKey}{colonSpacing}{serializedValue},");
            }

            return Content.Insert(last.ValueEnd, $", {serializedKey}{colonSpacing}{serializedValue}");
        }

        var indentationForProperty = DetectIndentation();
        var insertion = $"{indentationForProperty}{serializedKey}{colonSpacing}{serializedValue}{(last.CommaIndex is null ? string.Empty : ",")}{newline}";
        var insertionPoint = ClosingIndentStart();
        var withProperty = Content.Insert(insertionPoint, insertion);
        return last.CommaIndex is null ? withProperty.Insert(last.ValueEnd, ",") : withProperty;
    }

    private string Replace(string key, string serializedValue, JsonValueKind expectedKind, JsonValueKind? alternateKind = null)
    {
        var target = FindUnique(key, required: true)!;
        if (!TryReadScalar(target, expectedKind, out var element, alternateKind))
        {
            throw InvalidDocument();
        }

        if ((expectedKind == JsonValueKind.String && element.GetString() == JsonSerializer.Deserialize<string>(serializedValue)) ||
            (expectedKind is JsonValueKind.True or JsonValueKind.False && element.GetBoolean() == (serializedValue == "true")))
        {
            return Content;
        }

        return string.Concat(Content.AsSpan(0, target.ValueStart), serializedValue, Content.AsSpan(target.ValueEnd));
    }

    private bool TryReadScalar(PropertySpan property, JsonValueKind expectedKind, out JsonElement element, JsonValueKind? alternateKind = null)
    {
        using var value = JsonDocument.Parse(Content[property.ValueStart..property.ValueEnd]);
        element = value.RootElement.Clone();
        return element.ValueKind == expectedKind || element.ValueKind == alternateKind;
    }

    private PropertySpan? FindUnique(string key, bool required)
    {
        ValidateKey(key);
        var matches = properties.Where(property => property.Name == key).ToArray();
        if (matches.Length > 1)
        {
            throw InvalidDocument();
        }

        if (matches.Length == 0 && required)
        {
            throw new InvalidOperationException("The setting does not exist.");
        }

        return matches.SingleOrDefault();
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("A setting key is required.", nameof(key));
        }
    }

    private string DetectNewline() => Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private string DetectColonSpacing(PropertySpan property)
    {
        var candidate = Content[property.KeyEnd..property.ValueStart];
        return candidate.Count(character => character == ':') == 1 && candidate.All(character => character is ':' or ' ' or '\t')
            ? candidate
            : ": ";
    }

    private string DetectIndentation()
    {
        if (properties.Count == 0)
        {
            return "  ";
        }

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

    private static FormatException InvalidDocument() => new(InvalidDocumentMessage);

    private sealed record PropertySpan(string Name, int KeyStart, int KeyEnd, int ValueStart, int ValueEnd, int? CommaIndex);

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
            SkipTrivia();
            if (position != content.Length)
            {
                throw InvalidDocument();
            }

            return properties;
        }

        private int ScanValue()
        {
            return Peek() switch
            {
                '"' => ScanString(),
                '{' or '[' => ScanContainer(),
                _ => ScanPrimitive(),
            };
        }

        private int ScanContainer()
        {
            var stack = new Stack<char>();
            stack.Push(content[position++] == '{' ? '}' : ']');
            while (stack.Count > 0 && position < content.Length)
            {
                if (content[position] == '"')
                {
                    ScanString();
                    continue;
                }

                if (content[position] == '/' && position + 1 < content.Length && content[position + 1] is '/' or '*')
                {
                    SkipComment();
                    continue;
                }

                if (content[position] is '{' or '[')
                {
                    stack.Push(content[position++] == '{' ? '}' : ']');
                    continue;
                }

                if (content[position] == stack.Peek())
                {
                    stack.Pop();
                }

                position++;
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
                    continue;
                }

                if (content[position++] == '"')
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
                    continue;
                }

                if (content[position] == '/' && position + 1 < content.Length && content[position + 1] is '/' or '*')
                {
                    SkipComment();
                    continue;
                }

                break;
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

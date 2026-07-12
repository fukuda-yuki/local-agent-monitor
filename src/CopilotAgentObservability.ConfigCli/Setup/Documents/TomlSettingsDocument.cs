using System.Globalization;
using System.Text;

namespace CopilotAgentObservability.ConfigCli.Setup.Documents;

internal sealed class TomlSettingsDocument
{
    private const string InvalidDocumentMessage = "Settings document is malformed or unsupported.";
    private readonly IReadOnlyList<TableSpan> tables;
    private readonly IReadOnlyList<AssignmentSpan> assignments;

    private TomlSettingsDocument(string content, IReadOnlyList<TableSpan> tables, IReadOnlyList<AssignmentSpan> assignments)
    {
        Content = content;
        this.tables = tables;
        this.assignments = assignments;
    }

    public string Content { get; }

    public static TomlSettingsDocument Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            var parser = new DocumentParser(content);
            parser.Parse(out var tables, out var assignments);
            return new TomlSettingsDocument(content, tables, assignments);
        }
        catch (FormatException exception) when (exception.Message == InvalidDocumentMessage)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw InvalidDocument();
        }
    }

    public bool TryGetString(string table, string key, out string? value)
    {
        var assignment = Find(table, key, required: false);
        if (assignment?.Value.Kind != ValueKind.String)
        {
            value = null;
            return false;
        }

        value = assignment.Value.StringValue;
        return true;
    }

    public bool TryGetBoolean(string table, string key, out bool value)
    {
        var assignment = Find(table, key, required: false);
        if (assignment?.Value.Kind != ValueKind.Boolean)
        {
            value = default;
            return false;
        }

        value = assignment.Value.BooleanValue;
        return true;
    }

    public string AddString(string table, string key, string value) => Add(table, key, Quote(value));

    public string AddBoolean(string table, string key, bool value) => Add(table, key, value ? "true" : "false");

    public string ReplaceString(string table, string key, string value) => Replace(table, key, Quote(value), ValueKind.String, value, default);

    public string ReplaceBoolean(string table, string key, bool value) => Replace(table, key, value ? "true" : "false", ValueKind.Boolean, null, value);

    public string Remove(string table, string key)
    {
        var target = Find(table, key, required: true)!;
        if (target.CommentStart is int commentStart)
        {
            return Content.Remove(target.LineStart, commentStart - target.LineStart);
        }

        return Content.Remove(target.LineStart, target.LineEnd - target.LineStart);
    }

    private string Add(string table, string key, string serializedValue)
    {
        ValidateName(table, nameof(table));
        ValidateName(key, nameof(key));
        if (Find(table, key, required: false) is not null)
        {
            throw new InvalidOperationException("The setting already exists.");
        }

        var newline = Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var tableSpan = tables.SingleOrDefault(candidate => candidate.Name == table);
        if (tableSpan is null)
        {
            var separator = Content.Length == 0 || Content.EndsWith('\n') || Content.EndsWith('\r') ? string.Empty : newline;
            return string.Concat(Content, separator, "[", FormatKey(table), "]", newline, FormatKey(key), " = ", serializedValue);
        }

        var nextTable = tables.FirstOrDefault(candidate => candidate.LineStart > tableSpan.LineStart);
        var insertAt = nextTable?.LineStart ?? Content.Length;
        var prefix = insertAt == 0 || Content[insertAt - 1] is '\r' or '\n' ? string.Empty : newline;
        return Content.Insert(insertAt, string.Concat(prefix, FormatKey(key), " = ", serializedValue, newline));
    }

    private string Replace(string table, string key, string serializedValue, ValueKind expectedKind, string? stringValue, bool booleanValue)
    {
        var target = Find(table, key, required: true)!;
        if (target.Value.Kind != expectedKind)
        {
            throw InvalidDocument();
        }

        if ((expectedKind == ValueKind.String && target.Value.StringValue == stringValue) ||
            (expectedKind == ValueKind.Boolean && target.Value.BooleanValue == booleanValue))
        {
            return Content;
        }

        return string.Concat(Content.AsSpan(0, target.ValueStart), serializedValue, Content.AsSpan(target.ValueEnd));
    }

    private AssignmentSpan? Find(string table, string key, bool required)
    {
        ValidateName(table, nameof(table));
        ValidateName(key, nameof(key));
        var match = assignments.SingleOrDefault(assignment => assignment.Table == table && assignment.Key == key);
        if (match is null && required)
        {
            throw new InvalidOperationException("The setting does not exist.");
        }

        return match;
    }

    private static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            _ = character switch
            {
                '\b' => builder.Append("\\b"),
                '\t' => builder.Append("\\t"),
                '\n' => builder.Append("\\n"),
                '\f' => builder.Append("\\f"),
                '\r' => builder.Append("\\r"),
                '"' => builder.Append("\\\""),
                '\\' => builder.Append("\\\\"),
                _ when char.IsControl(character) => builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture)),
                _ => builder.Append(character),
            };
        }

        return builder.Append('"').ToString();
    }

    private static string FormatKey(string key) => key.All(IsBareKeyCharacter) ? key : Quote(key);

    private static void ValidateName(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("A TOML table or key name is required.", parameterName);
        }
    }

    private static bool IsBareKeyCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '-';

    private static FormatException InvalidDocument() => new(InvalidDocumentMessage);

    private sealed record TableSpan(string Name, int LineStart);

    private sealed record AssignmentSpan(
        string Table,
        string Key,
        ParsedValue Value,
        int LineStart,
        int LineEnd,
        int ValueStart,
        int ValueEnd,
        int? CommentStart);

    private enum ValueKind
    {
        String,
        Boolean,
        InlineTable,
    }

    private sealed record ParsedValue(ValueKind Kind, string? StringValue = null, bool BooleanValue = false);

    private sealed class DocumentParser(string content)
    {
        public void Parse(out IReadOnlyList<TableSpan> tables, out IReadOnlyList<AssignmentSpan> assignments)
        {
            var parsedTables = new List<TableSpan>();
            var parsedAssignments = new List<AssignmentSpan>();
            var tableNames = new HashSet<string>(StringComparer.Ordinal);
            var assignmentNames = new HashSet<(string Table, string Key)>();
            string? currentTable = null;
            var lineStart = 0;
            while (lineStart < content.Length)
            {
                var lineContentEnd = lineStart;
                while (lineContentEnd < content.Length && content[lineContentEnd] is not '\r' and not '\n')
                {
                    lineContentEnd++;
                }

                var lineEnd = lineContentEnd;
                if (lineEnd < content.Length && content[lineEnd] == '\r')
                {
                    lineEnd++;
                }

                if (lineEnd < content.Length && content[lineEnd] == '\n')
                {
                    lineEnd++;
                }

                ParseLine(lineStart, lineContentEnd, lineEnd, parsedTables, parsedAssignments, tableNames, assignmentNames, ref currentTable);
                lineStart = lineEnd;
            }

            tables = parsedTables;
            assignments = parsedAssignments;
        }

        private void ParseLine(
            int lineStart,
            int lineContentEnd,
            int lineEnd,
            List<TableSpan> tables,
            List<AssignmentSpan> assignments,
            HashSet<string> tableNames,
            HashSet<(string Table, string Key)> assignmentNames,
            ref string? currentTable)
        {
            var position = lineStart;
            SkipHorizontalWhitespace(ref position, lineContentEnd);
            if (position == lineContentEnd || content[position] == '#')
            {
                return;
            }

            if (content[position] == '[')
            {
                if (position + 1 < lineContentEnd && content[position + 1] == '[')
                {
                    throw InvalidDocument();
                }

                position++;
                SkipHorizontalWhitespace(ref position, lineContentEnd);
                var tableName = ParseKey(ref position, lineContentEnd);
                SkipHorizontalWhitespace(ref position, lineContentEnd);
                Expect(ref position, lineContentEnd, ']');
                SkipHorizontalWhitespace(ref position, lineContentEnd);
                if (position < lineContentEnd && content[position] != '#')
                {
                    throw InvalidDocument();
                }

                if (!tableNames.Add(tableName))
                {
                    throw InvalidDocument();
                }

                currentTable = tableName;
                tables.Add(new TableSpan(tableName, lineStart));
                return;
            }

            if (currentTable is null)
            {
                throw InvalidDocument();
            }

            var key = ParseKey(ref position, lineContentEnd);
            SkipHorizontalWhitespace(ref position, lineContentEnd);
            Expect(ref position, lineContentEnd, '=');
            SkipHorizontalWhitespace(ref position, lineContentEnd);
            var valueStart = position;
            var valueParser = new ValueParser(content, position, lineContentEnd);
            var value = valueParser.Parse();
            position = valueParser.Position;
            var valueEnd = position;
            SkipHorizontalWhitespace(ref position, lineContentEnd);
            int? commentStart = null;
            if (position < lineContentEnd)
            {
                if (content[position] != '#')
                {
                    throw InvalidDocument();
                }

                commentStart = position;
            }

            if (!assignmentNames.Add((currentTable, key)))
            {
                throw InvalidDocument();
            }

            assignments.Add(new AssignmentSpan(currentTable, key, value, lineStart, lineEnd, valueStart, valueEnd, commentStart));
        }

        private string ParseKey(ref int position, int end)
        {
            if (position >= end)
            {
                throw InvalidDocument();
            }

            if (content[position] is '"' or '\'')
            {
                var parser = new ValueParser(content, position, end);
                var value = parser.ParseString();
                position = parser.Position;
                return value;
            }

            var start = position;
            while (position < end && IsBareKeyCharacter(content[position]))
            {
                position++;
            }

            if (position == start)
            {
                throw InvalidDocument();
            }

            return content[start..position];
        }

        private void Expect(ref int position, int end, char expected)
        {
            if (position >= end || content[position] != expected)
            {
                throw InvalidDocument();
            }

            position++;
        }

        private void SkipHorizontalWhitespace(ref int position, int end)
        {
            while (position < end && content[position] is ' ' or '\t')
            {
                position++;
            }
        }
    }

    private sealed class ValueParser(string content, int start, int end)
    {
        public int Position { get; private set; } = start;

        public ParsedValue Parse()
        {
            if (Position >= end)
            {
                throw InvalidDocument();
            }

            if (content[Position] is '"' or '\'')
            {
                return new ParsedValue(ValueKind.String, ParseString());
            }

            if (Match("true"))
            {
                return new ParsedValue(ValueKind.Boolean, BooleanValue: true);
            }

            if (Match("false"))
            {
                return new ParsedValue(ValueKind.Boolean, BooleanValue: false);
            }

            if (content[Position] == '{')
            {
                ParseInlineTable();
                return new ParsedValue(ValueKind.InlineTable);
            }

            throw InvalidDocument();
        }

        public string ParseString()
        {
            var quote = content[Position++];
            var builder = new StringBuilder();
            while (Position < end)
            {
                var character = content[Position++];
                if (character == quote)
                {
                    return builder.ToString();
                }

                if (character is '\r' or '\n' || char.IsControl(character) && character != '\t')
                {
                    throw InvalidDocument();
                }

                if (quote == '\'' || character != '\\')
                {
                    builder.Append(character);
                    continue;
                }

                if (Position >= end)
                {
                    throw InvalidDocument();
                }

                var escape = content[Position++];
                if (escape is 'u' or 'U')
                {
                    builder.Append(ParseUnicode(escape == 'u' ? 4 : 8));
                    continue;
                }

                builder.Append(escape switch
                {
                    'b' => '\b',
                    't' => '\t',
                    'n' => '\n',
                    'f' => '\f',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    _ => throw InvalidDocument(),
                });
            }

            throw InvalidDocument();
        }

        private void ParseInlineTable()
        {
            Position++;
            SkipWhitespace();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (Peek() == '}')
            {
                Position++;
                return;
            }

            while (true)
            {
                var key = ParseInlineKey();
                if (!keys.Add(key))
                {
                    throw InvalidDocument();
                }

                SkipWhitespace();
                Expect('=');
                SkipWhitespace();
                _ = Parse();
                SkipWhitespace();
                if (Peek() == '}')
                {
                    Position++;
                    return;
                }

                Expect(',');
                SkipWhitespace();
                if (Peek() == '}')
                {
                    throw InvalidDocument();
                }
            }
        }

        private string ParseInlineKey()
        {
            if (Peek() is '"' or '\'')
            {
                return ParseString();
            }

            var keyStart = Position;
            while (Position < end && IsBareKeyCharacter(content[Position]))
            {
                Position++;
            }

            if (Position == keyStart)
            {
                throw InvalidDocument();
            }

            return content[keyStart..Position];
        }

        private string ParseUnicode(int digits)
        {
            if (Position + digits > end || !int.TryParse(content.AsSpan(Position, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint))
            {
                throw InvalidDocument();
            }

            Position += digits;
            if (!Rune.IsValid(codePoint))
            {
                throw InvalidDocument();
            }

            return new Rune(codePoint).ToString();
        }

        private bool Match(string token)
        {
            if (!content.AsSpan(Position, end - Position).StartsWith(token, StringComparison.Ordinal))
            {
                return false;
            }

            Position += token.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (Position < end && content[Position] is ' ' or '\t')
            {
                Position++;
            }
        }

        private char Peek() => Position < end ? content[Position] : '\0';

        private void Expect(char expected)
        {
            if (Peek() != expected)
            {
                throw InvalidDocument();
            }

            Position++;
        }
    }

}

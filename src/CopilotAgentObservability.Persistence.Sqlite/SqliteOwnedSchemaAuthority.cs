using System.Collections.Frozen;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class SqliteOwnedSchemaAuthority
{
    internal static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        Compile(IReadOnlyList<SqliteOwnedSchemaDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var objects = new Dictionary<(string Type, string Name), SqliteOwnedSchemaObject>(definitions.Count);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            var value = new SqliteOwnedSchemaObject(
                definition.Type,
                definition.Name,
                definition.Table,
                NormalizeSql(definition.Sql));
            if (!objects.TryAdd((value.Type, value.Name), value))
                throw new ArgumentException("Owned schema definitions must have unique type and name keys.", nameof(definitions));
        }
        return objects.ToFrozenDictionary();
    }

    internal static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        Read(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            Func<string, string, bool> ownsObject)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT type,name,tbl_name,sql
            FROM sqlite_master
            WHERE sql IS NOT NULL
            ORDER BY type,name;
            """;
        using var reader = command.ExecuteReader();
        var objects =
            new Dictionary<(string Type, string Name), SqliteOwnedSchemaObject>();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var table = reader.GetString(2);
            if (!ownsObject(name, table))
                continue;
            var value = new SqliteOwnedSchemaObject(
                reader.GetString(0),
                name,
                table,
                NormalizeSql(reader.GetString(3)));
            objects.Add((value.Type, value.Name), value);
        }
        return objects;
    }

    internal static bool Equal(
        IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> actual,
        IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> expected)
    {
        if (actual.Count != expected.Count)
            return false;
        foreach (var expectedObject in expected)
        {
            if (!actual.TryGetValue(expectedObject.Key, out var actualObject)
                || actualObject.Table != expectedObject.Value.Table
                || actualObject.Sql != expectedObject.Value.Sql)
                return false;
        }
        return true;
    }

    private static string NormalizeSql(string sql)
    {
        var tokens = new List<SqlToken>();
        for (var index = 0; index < sql.Length; index++)
        {
            if (char.IsWhiteSpace(sql[index])
                || TryReadComment(sql, ref index))
                continue;
            if (TryReadQuotedToken(sql, ref index, out var quoted))
            {
                tokens.Add(new SqlToken(quoted, true));
                continue;
            }
            if (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '.' or '$')
            {
                var start = index;
                while (index + 1 < sql.Length
                       && (char.IsLetterOrDigit(sql[index + 1])
                           || sql[index + 1] is '_' or '.' or '$'))
                    index++;
                tokens.Add(new SqlToken(sql[start..(index + 1)].ToLowerInvariant(), false));
                continue;
            }
            tokens.Add(new SqlToken(sql[index].ToString(), false));
        }
        if (tokens.Count != 0 && IsUnquoted(tokens[^1], ";"))
            tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count >= 5
            && IsUnquoted(tokens[0], "create")
            && (IsUnquoted(tokens[1], "table")
                || IsUnquoted(tokens[1], "index")
                || IsUnquoted(tokens[1], "trigger"))
            && IsUnquoted(tokens[2], "if")
            && IsUnquoted(tokens[3], "not")
            && IsUnquoted(tokens[4], "exists"))
        {
            tokens.RemoveRange(2, 3);
        }
        var result = new System.Text.StringBuilder(sql.Length);
        foreach (var token in tokens)
            AppendToken(result, token.Value);
        return result.ToString();
    }

    private static bool IsUnquoted(SqlToken token, string value) =>
        !token.IsQuoted && token.Value.Equals(value, StringComparison.Ordinal);

    private static void AppendToken(
        System.Text.StringBuilder result,
        string token) =>
        result.Append(token.Length).Append(':').Append(token);

    private static bool TryReadComment(string value, ref int index)
    {
        if (index + 1 >= value.Length)
            return false;
        if (value[index] == '-' && value[index + 1] == '-')
        {
            index += 2;
            while (index < value.Length && value[index] is not ('\r' or '\n'))
                index++;
            index--;
            return true;
        }
        if (value[index] != '/' || value[index + 1] != '*')
            return false;
        index += 2;
        while (index + 1 < value.Length
               && !(value[index] == '*' && value[index + 1] == '/'))
            index++;
        index = Math.Min(value.Length - 1, index + 1);
        return true;
    }

    private static bool TryReadQuotedToken(
        string value,
        ref int index,
        out string token)
    {
        if (value[index] is not ('\'' or '"' or '`' or '['))
        {
            token = string.Empty;
            return false;
        }
        var start = index;
        var opening = value[index];
        var closing = opening == '[' ? ']' : opening;
        index++;
        while (index < value.Length)
        {
            if (value[index] != closing)
            {
                index++;
                continue;
            }
            if (index + 1 < value.Length && value[index + 1] == closing)
            {
                index += 2;
                continue;
            }
            index++;
            break;
        }
        token = value[start..index];
        index--;
        return true;
    }

    private sealed record SqlToken(string Value, bool IsQuoted);
}

internal sealed record SqliteOwnedSchemaObject(
    string Type,
    string Name,
    string Table,
    string Sql);

internal sealed record SqliteOwnedSchemaDefinition(
    string Type,
    string Name,
    string Table,
    string Sql);

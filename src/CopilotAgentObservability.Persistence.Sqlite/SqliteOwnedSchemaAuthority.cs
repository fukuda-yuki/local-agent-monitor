namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class SqliteOwnedSchemaAuthority
{
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
        var result = new System.Text.StringBuilder(sql.Length);
        for (var index = 0; index < sql.Length; index++)
        {
            if (char.IsWhiteSpace(sql[index])
                || TryReadComment(sql, ref index))
                continue;
            if (TryReadQuotedToken(sql, ref index, out var quoted))
            {
                AppendToken(result, quoted);
                continue;
            }
            if (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '.' or '$')
            {
                var start = index;
                while (index + 1 < sql.Length
                       && (char.IsLetterOrDigit(sql[index + 1])
                           || sql[index + 1] is '_' or '.' or '$'))
                    index++;
                AppendToken(
                    result,
                    sql[start..(index + 1)].ToLowerInvariant());
                continue;
            }
            AppendToken(result, sql[index].ToString());
        }
        return result.ToString();
    }

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
}

internal sealed record SqliteOwnedSchemaObject(
    string Type,
    string Name,
    string Table,
    string Sql);

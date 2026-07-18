using System.Text;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

internal static class SessionSchemaV11Validator
{
    private const string ValidationError = "Unsupported incomplete Session schema version 11.";
    private static readonly object ExpectedSchemasLock = new();
    private static readonly Dictionary<int, ExpectedSchemas> ExpectedSchemasByVersion = new();

    private static readonly string[] ReservedPrefixes =
    [
        "sessions",
        "session_",
        "improvement_proposal",
        "proposal_apply",
        "objective_evaluation",
        "effect_",
    ];

    internal static void Validate(
        SqliteConnection connection,
        Action<SqliteConnection> createCanonicalSchema,
        int expectedVersion = 11)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(createCanonicalSchema);

        try
        {
            var expected = GetExpectedSchemas(createCanonicalSchema, expectedVersion);
            if (!HasExactOwnedObjectSet(connection, expected.TableNames)
                || !HasExactSessionVersionRow(connection, expectedVersion))
            {
                Reject();
            }

            var actual = ReadProfile(connection, expected.TableNames);
            if (actual is null
                || expected.Profiles.Count(profile => ProfileEquals(profile, actual)) != 1)
            {
                Reject();
            }
        }
        catch (SqliteException)
        {
            Reject();
        }
        catch (InvalidOperationException)
        {
            Reject();
        }
        catch (InvalidCastException)
        {
            Reject();
        }
        catch (FormatException)
        {
            Reject();
        }
        catch (OverflowException)
        {
            Reject();
        }
    }

    private static ExpectedSchemas GetExpectedSchemas(Action<SqliteConnection> createCanonicalSchema, int expectedVersion)
    {
        lock (ExpectedSchemasLock)
        {
            if (ExpectedSchemasByVersion.TryGetValue(expectedVersion, out var cached))
            {
                return cached;
            }

            using var canonicalConnection = OpenMemoryDatabase();
            createCanonicalSchema(canonicalConnection);
            var tableSql = ReadCreateTableStatements(canonicalConnection)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (expectedVersion == 11)
            {
                tableSql["session_events"] = RemoveColumnDefinition(tableSql["session_events"], "match_kind");
            }
            var tableNames = tableSql.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var canonical = BuildProfile(tableSql, expectedVersion)
                ?? throw new InvalidOperationException("Unable to construct the canonical Session schema.");

            var versionThree = BuildDerivedProfile(
                tableSql,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["improvement_proposals"] = "revision",
                    ["improvement_proposal_sessions"] = "proposal_revision",
                },
                historicalUpdatedAtDefault: false,
                expectedVersion: expectedVersion);
            var versionFour = BuildDerivedProfile(
                tableSql,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["improvement_proposals"] = "revision",
                    ["improvement_proposal_sessions"] = "proposal_revision",
                    ["proposal_apply_drafts"] = "proposal_revision",
                    ["proposal_applies"] = "proposal_revision",
                },
                historicalUpdatedAtDefault: true,
                expectedVersion: expectedVersion);
            var versionsFiveAndSix = BuildDerivedProfile(
                tableSql,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["improvement_proposals"] = "revision",
                    ["improvement_proposal_sessions"] = "proposal_revision",
                    ["proposal_apply_drafts"] = "proposal_revision",
                    ["proposal_applies"] = "proposal_revision",
                },
                historicalUpdatedAtDefault: false,
                expectedVersion: expectedVersion);

            var expected = new ExpectedSchemas(
                tableNames,
                [canonical, versionThree, versionFour, versionsFiveAndSix]);
            ExpectedSchemasByVersion.Add(expectedVersion, expected);
            return expected;
        }
    }

    private static DatabaseProfile? BuildProfile(
        IReadOnlyDictionary<string, string> tableSql,
        int schemaVersion)
    {
        using var connection = OpenMemoryDatabase();
        foreach (var sql in tableSql.Values)
        {
            Execute(connection, sql);
        }
        Execute(connection, $"INSERT INTO schema_version(component,version) VALUES('session',{schemaVersion});");
        return ReadProfile(connection, tableSql.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static DatabaseProfile BuildDerivedProfile(
        IReadOnlyDictionary<string, string> canonicalTableSql,
        IReadOnlyDictionary<string, string> appendedRevisionColumns,
        bool historicalUpdatedAtDefault,
        int expectedVersion)
    {
        using var connection = OpenMemoryDatabase();
        foreach (var (table, canonicalSql) in canonicalTableSql)
        {
            var sql = canonicalSql;
            if (appendedRevisionColumns.TryGetValue(table, out var revisionColumn))
            {
                sql = MoveRevisionColumnToLegacyAppendPosition(sql, revisionColumn);
            }
            if (historicalUpdatedAtDefault
                && string.Equals(table, "proposal_apply_drafts", StringComparison.OrdinalIgnoreCase))
            {
                sql = ReplaceColumnDefinition(
                    sql,
                    "updated_at",
                    "updated_at TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00'");
            }
            Execute(connection, sql);
        }
        Execute(connection, $"INSERT INTO schema_version(component,version) VALUES('session',{expectedVersion});");

        return ReadProfile(connection, canonicalTableSql.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unable to construct a supported Session schema profile.");
    }

    private static string MoveRevisionColumnToLegacyAppendPosition(string sql, string column)
    {
        var table = ParseCreateTable(sql);
        var definitions = table.Definitions.ToList();
        var revisionIndex = definitions.FindIndex(definition =>
            string.Equals(ReadLeadingIdentifier(definition), column, StringComparison.OrdinalIgnoreCase));
        if (revisionIndex < 0)
        {
            throw new InvalidOperationException($"Canonical Session DDL is missing {column}.");
        }

        definitions.RemoveAt(revisionIndex);
        var constraintIndex = definitions.FindIndex(IsTableConstraint);
        if (constraintIndex < 0)
        {
            constraintIndex = definitions.Count;
        }
        definitions.Insert(constraintIndex, $"{column} INTEGER NOT NULL DEFAULT 1");
        return RecomposeCreateTable(sql, table, definitions);
    }

    private static string ReplaceColumnDefinition(string sql, string column, string replacement)
    {
        var table = ParseCreateTable(sql);
        var definitions = table.Definitions.ToList();
        var index = definitions.FindIndex(definition =>
            string.Equals(ReadLeadingIdentifier(definition), column, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Canonical Session DDL is missing {column}.");
        }
        definitions[index] = replacement;
        return RecomposeCreateTable(sql, table, definitions);
    }

    private static string RemoveColumnDefinition(string sql, string column)
    {
        var table = ParseCreateTable(sql);
        var definitions = table.Definitions.ToList();
        var index = definitions.FindIndex(definition =>
            string.Equals(ReadLeadingIdentifier(definition), column, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Canonical Session DDL is missing {column}.");
        }
        definitions.RemoveAt(index);
        return RecomposeCreateTable(sql, table, definitions);
    }

    private static string RecomposeCreateTable(
        string original,
        ParsedCreateTable table,
        IReadOnlyList<string> definitions) =>
        original[..(table.OpeningParenthesis + 1)]
        + string.Join(',', definitions)
        + original[table.ClosingParenthesis..];

    private static ParsedCreateTable ParseCreateTable(string sql)
    {
        var opening = FindNextOutsideQuotesAndComments(sql, 0, '(');
        if (opening < 0)
        {
            throw new InvalidOperationException("Canonical Session CREATE TABLE DDL is invalid.");
        }
        var closing = FindMatchingParenthesis(sql, opening);
        if (closing < 0)
        {
            throw new InvalidOperationException("Canonical Session CREATE TABLE DDL is invalid.");
        }
        return new(opening, closing, SplitTopLevel(sql[(opening + 1)..closing]));
    }

    private static IReadOnlyList<string> SplitTopLevel(string value)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (TrySkipQuoted(value, ref index) || TrySkipComment(value, ref index))
            {
                continue;
            }
            if (value[index] == '(')
            {
                depth++;
            }
            else if (value[index] == ')')
            {
                depth--;
            }
            else if (value[index] == ',' && depth == 0)
            {
                result.Add(value[start..index].Trim());
                start = index + 1;
            }
        }
        result.Add(value[start..].Trim());
        return result;
    }

    private static int FindMatchingParenthesis(string value, int opening)
    {
        var depth = 0;
        for (var index = opening; index < value.Length; index++)
        {
            if (TrySkipQuoted(value, ref index) || TrySkipComment(value, ref index))
            {
                continue;
            }
            if (value[index] == '(')
            {
                depth++;
            }
            else if (value[index] == ')' && --depth == 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindNextOutsideQuotesAndComments(string value, int start, char target)
    {
        for (var index = start; index < value.Length; index++)
        {
            if (TrySkipQuoted(value, ref index) || TrySkipComment(value, ref index))
            {
                continue;
            }
            if (value[index] == target)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsTableConstraint(string definition)
    {
        var identifier = ReadLeadingIdentifier(definition);
        return identifier.Equals("constraint", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("primary", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("unique", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("check", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("foreign", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadLeadingIdentifier(string definition)
    {
        var index = 0;
        SkipTrivia(definition, ref index);
        if (index >= definition.Length)
        {
            return string.Empty;
        }

        if (definition[index] is '"' or '`' or '[')
        {
            var opening = definition[index++];
            var closing = opening == '[' ? ']' : opening;
            var result = new StringBuilder();
            while (index < definition.Length)
            {
                var character = definition[index++];
                if (character != closing)
                {
                    result.Append(character);
                    continue;
                }
                if (index < definition.Length && definition[index] == closing)
                {
                    result.Append(closing);
                    index++;
                    continue;
                }
                break;
            }
            return result.ToString();
        }

        var start = index;
        while (index < definition.Length
               && (char.IsLetterOrDigit(definition[index]) || definition[index] is '_' or '$'))
        {
            index++;
        }
        return definition[start..index];
    }

    private static void SkipTrivia(string value, ref int index)
    {
        while (index < value.Length)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                index++;
                continue;
            }
            var before = index;
            if (TrySkipComment(value, ref index))
            {
                index++;
                continue;
            }
            index = before;
            break;
        }
    }

    private static bool HasExactOwnedObjectSet(
        SqliteConnection connection,
        IReadOnlySet<string> expectedTables)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type,name,tbl_name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            var name = reader.GetString(1);
            var table = reader.GetString(2);
            if (expectedTables.Contains(name))
            {
                if (!type.Equals("table", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                continue;
            }
            if (IsReservedName(name))
            {
                return false;
            }
            if (expectedTables.Contains(table)
                && type is not "index" and not "trigger")
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsReservedName(string name) => ReservedPrefixes.Any(prefix =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool HasExactSessionVersionRow(SqliteConnection connection, int expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version,typeof(version) FROM schema_version WHERE component='session';";
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !reader.GetString(1).Equals("integer", StringComparison.Ordinal)
            || reader.GetInt64(0) != expectedVersion)
        {
            return false;
        }
        return !reader.Read();
    }

    private static DatabaseProfile? ReadProfile(
        SqliteConnection connection,
        IReadOnlySet<string> tableNames)
    {
        var tables = new Dictionary<string, TableShape>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tableNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var shape = ReadTable(connection, table);
            if (shape is null)
            {
                return null;
            }
            tables.Add(table, shape);
        }
        return new(tables);
    }

    private static TableShape? ReadTable(SqliteConnection connection, string table)
    {
        TableListShape tableList;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type,ncol,wr,strict FROM pragma_table_list WHERE schema='main' AND name=$table;";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            tableList = new(reader.GetString(0).ToLowerInvariant(), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
            if (reader.Read())
            {
                return null;
            }
        }

        string sql;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table;";
            command.Parameters.AddWithValue("$table", table);
            if (command.ExecuteScalar() is not string value)
            {
                return null;
            }
            sql = CanonicalSql(value);
        }

        var columns = new List<ColumnShape>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT cid,name,type,\"notnull\",dflt_value,pk,hidden FROM pragma_table_xinfo($table) ORDER BY cid;";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(new(
                    reader.GetInt32(0),
                    reader.GetString(1).ToLowerInvariant(),
                    reader.GetString(2).ToLowerInvariant(),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : CanonicalSql(reader.GetString(4)),
                    reader.GetInt32(5),
                    reader.GetInt32(6)));
            }
        }

        return new(
            tableList,
            columns,
            sql,
            ReadIndexes(connection, table),
            ReadForeignKeys(connection, table),
            ReadTriggers(connection, table));
    }

    private static IReadOnlyList<IndexShape> ReadIndexes(SqliteConnection connection, string table)
    {
        var headers = new List<IndexHeader>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name,\"unique\",origin,partial FROM pragma_index_list($table);";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                headers.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2).ToLowerInvariant(), reader.GetInt32(3)));
            }
        }

        var indexes = new List<IndexShape>();
        foreach (var header in headers)
        {
            string? sql;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name=$index;";
                command.Parameters.AddWithValue("$index", header.Name);
                var value = command.ExecuteScalar();
                sql = value is null or DBNull ? null : CanonicalSql(Convert.ToString(value)!);
            }
            var autoIndex = sql is null && header.Name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase);

            var terms = new List<IndexTermShape>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT seqno,cid,name,\"desc\",coll,\"key\" FROM pragma_index_xinfo($index) ORDER BY seqno;";
                command.Parameters.AddWithValue("$index", header.Name);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    terms.Add(new(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2).ToLowerInvariant(),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4).ToLowerInvariant(),
                        reader.GetInt32(5)));
                }
            }

            indexes.Add(new(
                autoIndex ? null : header.Name.ToLowerInvariant(),
                header.Unique,
                header.Origin,
                header.Partial,
                sql,
                terms));
        }
        return indexes;
    }

    private static IReadOnlyList<ForeignKeyShape> ReadForeignKeys(SqliteConnection connection, string table)
    {
        var foreignKeys = new List<ForeignKeyShape>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,seq,\"table\",\"from\",\"to\",on_update,on_delete,match FROM pragma_foreign_key_list($table) ORDER BY id,seq;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            foreignKeys.Add(new(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2).ToLowerInvariant(),
                reader.GetString(3).ToLowerInvariant(),
                reader.IsDBNull(4) ? null : reader.GetString(4).ToLowerInvariant(),
                reader.GetString(5).ToLowerInvariant(),
                reader.GetString(6).ToLowerInvariant(),
                reader.GetString(7).ToLowerInvariant()));
        }
        return foreignKeys;
    }

    private static IReadOnlyList<TriggerShape> ReadTriggers(SqliteConnection connection, string table)
    {
        var triggers = new List<TriggerShape>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='trigger' AND tbl_name=$table ORDER BY name;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            triggers.Add(new(reader.GetString(0).ToLowerInvariant(), CanonicalSql(reader.GetString(1))));
        }
        return triggers;
    }

    private static IReadOnlyDictionary<string, string> ReadCreateTableStatements(SqliteConnection connection)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0), reader.GetString(1));
        }
        return result;
    }

    private static bool ProfileEquals(DatabaseProfile expected, DatabaseProfile actual)
    {
        if (expected.Tables.Count != actual.Tables.Count)
        {
            return false;
        }
        foreach (var (name, expectedTable) in expected.Tables)
        {
            if (!actual.Tables.TryGetValue(name, out var actualTable)
                || !TableEquals(expectedTable, actualTable))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TableEquals(TableShape expected, TableShape actual) =>
        expected.TableList == actual.TableList
        && expected.Columns.SequenceEqual(actual.Columns)
        && string.Equals(expected.Sql, actual.Sql, StringComparison.Ordinal)
        && MultisetEquals(expected.Indexes, actual.Indexes, IndexEquals)
        && expected.ForeignKeys.SequenceEqual(actual.ForeignKeys)
        && MultisetEquals(expected.Triggers, actual.Triggers, (left, right) => left == right);

    private static bool IndexEquals(IndexShape expected, IndexShape actual) =>
        string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)
        && expected.Unique == actual.Unique
        && string.Equals(expected.Origin, actual.Origin, StringComparison.Ordinal)
        && expected.Partial == actual.Partial
        && string.Equals(expected.Sql, actual.Sql, StringComparison.Ordinal)
        && expected.Terms.SequenceEqual(actual.Terms);

    private static bool MultisetEquals<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        Func<T, T, bool> equals)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }
        var matched = new bool[actual.Count];
        foreach (var expectedItem in expected)
        {
            var index = -1;
            for (var candidate = 0; candidate < actual.Count; candidate++)
            {
                if (!matched[candidate] && equals(expectedItem, actual[candidate]))
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0)
            {
                return false;
            }
            matched[index] = true;
        }
        return true;
    }

    private static string CanonicalSql(string sql)
    {
        var result = new StringBuilder(sql.Length);
        for (var index = 0; index < sql.Length; index++)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                continue;
            }
            if (TryReadComment(sql, ref index))
            {
                continue;
            }
            if (TryReadQuotedToken(sql, ref index, out var quoted))
            {
                AppendToken(result, quoted);
                continue;
            }
            if (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '.' or '$')
            {
                var start = index;
                while (index + 1 < sql.Length
                       && (char.IsLetterOrDigit(sql[index + 1]) || sql[index + 1] is '_' or '.' or '$'))
                {
                    index++;
                }
                AppendToken(result, sql[start..(index + 1)].ToLowerInvariant());
                continue;
            }
            AppendToken(result, sql[index].ToString());
        }
        return result.ToString();
    }

    private static void AppendToken(StringBuilder result, string token) =>
        result.Append(token.Length).Append(':').Append(token);

    private static bool TryReadComment(string value, ref int index)
    {
        if (index + 1 >= value.Length)
        {
            return false;
        }
        if (value[index] == '-' && value[index + 1] == '-')
        {
            index += 2;
            while (index < value.Length && value[index] is not ('\r' or '\n'))
            {
                index++;
            }
            index--;
            return true;
        }
        if (value[index] == '/' && value[index + 1] == '*')
        {
            index += 2;
            while (index + 1 < value.Length && !(value[index] == '*' && value[index + 1] == '/'))
            {
                index++;
            }
            index = Math.Min(value.Length - 1, index + 1);
            return true;
        }
        return false;
    }

    private static bool TryReadQuotedToken(string value, ref int index, out string token)
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

    private static bool TrySkipQuoted(string value, ref int index)
    {
        if (!TryReadQuotedToken(value, ref index, out _))
        {
            return false;
        }
        return true;
    }

    private static bool TrySkipComment(string value, ref int index) => TryReadComment(value, ref index);

    private static SqliteConnection OpenMemoryDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Reject() => throw new InvalidOperationException(ValidationError);

    private sealed record ExpectedSchemas(
        IReadOnlySet<string> TableNames,
        IReadOnlyList<DatabaseProfile> Profiles);

    private sealed record DatabaseProfile(IReadOnlyDictionary<string, TableShape> Tables);

    private sealed record TableShape(
        TableListShape TableList,
        IReadOnlyList<ColumnShape> Columns,
        string Sql,
        IReadOnlyList<IndexShape> Indexes,
        IReadOnlyList<ForeignKeyShape> ForeignKeys,
        IReadOnlyList<TriggerShape> Triggers);

    private sealed record TableListShape(string Type, int ColumnCount, int WithoutRowId, int Strict);

    private sealed record ColumnShape(
        int ColumnId,
        string Name,
        string DeclaredType,
        int NotNull,
        string? DefaultSql,
        int PrimaryKeyOrder,
        int Hidden);

    private sealed record IndexHeader(string Name, int Unique, string Origin, int Partial);

    private sealed record IndexShape(
        string? Name,
        int Unique,
        string Origin,
        int Partial,
        string? Sql,
        IReadOnlyList<IndexTermShape> Terms);

    private sealed record IndexTermShape(
        int Sequence,
        int ColumnId,
        string? Name,
        int Descending,
        string? Collation,
        int Key);

    private sealed record ForeignKeyShape(
        int Id,
        int Sequence,
        string Table,
        string From,
        string? To,
        string OnUpdate,
        string OnDelete,
        string Match);

    private sealed record TriggerShape(string Name, string Sql);

    private sealed record ParsedCreateTable(
        int OpeningParenthesis,
        int ClosingParenthesis,
        IReadOnlyList<string> Definitions);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class MonitorSchemaMigrationFixtureTests
{
    private const int CurrentMonitorSchemaVersion = 4;
    private const string GenerationCommand = "dotnet run --project scripts/test/GenerateMonitorSchemaFixtures/GenerateMonitorSchemaFixtures.csproj -- --output tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor";

    public static TheoryData<int, string> HistoricalSchemas => new()
    {
        { 1, "655e00243df9e07b9abb3392d6e4daf747064a77" },
        { 2, "f91e195b549fa2bbfc51b3245dd3fb19fcc8759c" },
        { 3, "9ca613a97fd0611ccff1d84b35261b7346112eab" },
        { 4, "65ec872eb541b2023f55c32d32edebb9cf83818b" },
    };

    public static TheoryData<string> SemanticReaderDifferences => new()
    {
        "missing-autoincrement",
        "changed-check",
        "changed-unique-order",
        "desc-index-term",
        "collation-index-term",
        "expression-index-identity",
        "partial-index-predicate",
        "foreign-key",
    };

    [Theory]
    [MemberData(nameof(SemanticReaderDifferences))]
    public void Semantic_reader_detects_each_isolated_scratch_schema_difference(string difference)
    {
        AssertScratchSchemaDifference(difference);
    }

    [Fact]
    public void Semantic_reader_maps_every_table_list_and_table_xinfo_field_from_distinct_scratch_values()
    {
        AssertIntrospectionFieldsFromScratchSchemas();
    }

    [Fact]
    public void Semantic_reader_preserves_without_rowid_index_auxiliary_term_identity_and_key_flag()
    {
        AssertWithoutRowIdAuxiliaryTerms();
    }

    [Fact]
    public void Semantic_reader_ignores_internal_autoindex_names_but_not_their_semantics()
    {
        AssertEquivalentAutoindexSchemasCompareEqual();
    }

    [Fact]
    public void Semantic_reader_does_not_treat_comments_quoted_identifiers_or_string_literals_as_autoincrement_or_check_syntax()
    {
        AssertSqlDecoysAreIgnored();
    }

    [Theory]
    [MemberData(nameof(HistoricalSchemas))]
    public void Historical_fixture_has_reproducible_provenance_and_preserves_complete_v4_state_after_restart(int version, string sourceCommit)
    {
        Assert.Equal(CurrentMonitorSchemaVersion, RawTelemetryStore.MonitorSchemaVersion);

        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor");
        var manifestPath = Path.Combine(fixtureDirectory, "manifest.json");
        Assert.True(File.Exists(manifestPath), $"Missing migration fixture manifest: {manifestPath}");

        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal("monitor", manifest.Component);
        Assert.Equal("git status --porcelain", manifest.GitStatusCommand);
        Assert.Equal(GenerationCommand, manifest.GenerationCommand);

        var fixture = Assert.Single(manifest.Fixtures, candidate => candidate.Version == version);
        Assert.Equal(sourceCommit, fixture.SourceCommit);
        Assert.Equal($"monitor-v{version}.sqlite", fixture.File);
        Assert.Equal(string.Empty, fixture.GitStatusBefore);
        Assert.Equal(string.Empty, fixture.GitStatusAfter);

        var fixturePath = Path.Combine(fixtureDirectory, fixture.File);
        Assert.True(File.Exists(fixturePath), $"Missing migration fixture: {fixturePath}");
        Assert.Equal(fixture.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant());

        if (version == CurrentMonitorSchemaVersion)
        {
            using var readOnlyFixture = OpenReadOnly(fixturePath);
            AssertSchemaContract(ExpectedV4SchemaContract, ReadSchemaContract(readOnlyFixture));
        }

        var migratedPath = Path.Combine(Path.GetTempPath(), $"monitor-migration-{Guid.NewGuid():N}.sqlite");
        File.Copy(fixturePath, migratedPath);
        try
        {
            AssertHistoricalState(migratedPath, version, fixture.Sentinels);

            new RawTelemetryStore(migratedPath).CreateMonitorSchema();
            AssertCompleteMigratedState(migratedPath, fixture.Sentinels);

            new RawTelemetryStore(migratedPath).CreateMonitorSchema();
            AssertCompleteMigratedState(migratedPath, fixture.Sentinels);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(migratedPath);
        }
    }

    private static void AssertHistoricalState(string databasePath, int expectedVersion, FixtureSentinels sentinels)
    {
        using var connection = Open(databasePath);
        Assert.Equal(expectedVersion, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component = 'monitor';"));
        Assert.Equal(sentinels.RawRecordId, Scalar<long>(connection, "SELECT id FROM raw_records WHERE id = $id;", sentinels.RawRecordId));
        Assert.Equal(sentinels.IngestionId, Scalar<long>(connection, "SELECT id FROM monitor_ingestions WHERE id = $id;", sentinels.IngestionId));
        Assert.Equal(sentinels.TraceId, Scalar<string>(connection, "SELECT trace_id FROM monitor_traces WHERE trace_id = $id;", sentinels.TraceId));
        if (sentinels.SpanId is not null)
        {
            Assert.Equal(sentinels.SpanId, Scalar<string>(connection, "SELECT span_id FROM monitor_spans WHERE span_id = $id;", sentinels.SpanId));
        }
    }

    private static void AssertCompleteMigratedState(string databasePath, FixtureSentinels sentinels)
    {
        using var connection = Open(databasePath);

        AssertSchemaContract(ExpectedV4SchemaContract, ReadSchemaContract(connection));

        Assert.Equal(new[] { $"s:monitor|i:{CurrentMonitorSchemaVersion}" }, ReadRows(connection, "schema_version"));
        Assert.Equal(new[] { $"i:{sentinels.RawRecordId}|s:raw-otlp|s:{sentinels.TraceId}|s:2026-07-12T00:00:00.0000000+00:00|<null>|s:{{\"fixture\":true}}|i:1" }, ReadRows(connection, "raw_records"));
        Assert.Equal(new[] { $"i:{sentinels.IngestionId}|i:{sentinels.RawRecordId}|s:2026-07-12T00:00:00.0000000+00:00|s:raw-otlp|s:{sentinels.TraceId}|<null>|<null>|s:2026-07-12T00:00:01.0000000+00:00|<null>" }, ReadRows(connection, "monitor_ingestions"));

        var traceNulls = string.Join('|', Enumerable.Repeat("<null>", 11));
        var traceTailNulls = string.Join('|', Enumerable.Repeat("<null>", 13));
        Assert.Equal(new[] { $"i:{sentinels.TraceRowId}|s:{sentinels.TraceId}|{traceNulls}|s:2026-07-12T00:00:01.0000000+00:00|{traceTailNulls}" }, ReadRows(connection, "monitor_traces"));

        var expectedSpanRows = sentinels.SpanId is null
            ? Array.Empty<string>()
            : new[] { $"i:{sentinels.SpanRowId}|i:{sentinels.RawRecordId}|s:{sentinels.TraceId}|s:{sentinels.SpanId}|<null>|i:0|{string.Join('|', Enumerable.Repeat("<null>", 22))}|s:2026-07-12T00:00:01.0000000+00:00" };
        Assert.Equal(expectedSpanRows, ReadRows(connection, "monitor_spans"));
    }

    private static void AssertSchemaContract(SchemaContract expected, SchemaContract actual)
    {
        Assert.Equal(expected.Tables, actual.Tables);
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.TableSql, actual.TableSql);
        Assert.Equal(expected.Indexes, actual.Indexes);
        Assert.Equal(expected.ForeignKeys, actual.ForeignKeys);
        Assert.Equal(expected.ViewsAndTriggers, actual.ViewsAndTriggers);
    }

    private static SchemaContract ReadSchemaContract(SqliteConnection connection)
    {
        var tables = new List<TableListDefinition>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT schema,name,type,ncol,wr,strict FROM pragma_table_list WHERE name NOT LIKE 'sqlite_%' ORDER BY schema,name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(new TableListDefinition(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));
            }
        }

        var columns = new List<ColumnDefinition>();
        var tableSql = new List<TableSqlDefinition>();
        var indexes = new List<IndexDefinition>();
        var foreignKeys = new List<ForeignKeyDefinition>();
        foreach (var table in tables.Where(candidate => candidate.Schema == "main" && candidate.Type == "table"))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT cid,name,type,\"notnull\",dflt_value,pk,hidden FROM pragma_table_xinfo($table) ORDER BY cid;";
                command.Parameters.AddWithValue("$table", table.Name);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    columns.Add(new ColumnDefinition(
                        table.Name,
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6)));
                }
            }

            var sql = Scalar<string>(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$id;", table.Name);
            var tokens = TokenizeSql(sql);
            tableSql.Add(new TableSqlDefinition(table.Name, tokens.Any(token => token.Kind == SqlTokenKind.Word && token.Text == "autoincrement"), CheckSignature(ExtractChecks(tokens))));
            indexes.AddRange(ReadIndexes(connection, table.Name));
            foreignKeys.AddRange(ReadForeignKeys(connection, table.Name));
        }

        var viewsAndTriggers = ReadStrings(connection, "SELECT type || ':' || name FROM sqlite_schema WHERE type IN ('view','trigger') AND name NOT LIKE 'sqlite_%' ORDER BY type,name;");
        return new SchemaContract(
            tables.ToArray(),
            columns.OrderBy(column => column.Table, StringComparer.Ordinal).ThenBy(column => column.Cid).ToArray(),
            tableSql.OrderBy(table => table.Table, StringComparer.Ordinal).ToArray(),
            indexes.OrderBy(index => index.Table, StringComparer.Ordinal).ThenBy(index => index.SemanticSortKey, StringComparer.Ordinal).ToArray(),
            foreignKeys.OrderBy(key => key.Table, StringComparer.Ordinal).ThenBy(key => key.Id).ThenBy(key => key.Sequence).ToArray(),
            viewsAndTriggers);
    }

    private static IEnumerable<IndexDefinition> ReadIndexes(SqliteConnection connection, string table)
    {
        var entries = new List<(string Name, int Unique, string Origin, int Partial)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name,\"unique\",origin,partial FROM pragma_index_list($table);";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read()) entries.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3)));
        }

        foreach (var entry in entries)
        {
            string? sql;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name=$name;";
                command.Parameters.AddWithValue("$name", entry.Name);
                sql = command.ExecuteScalar() as string;
            }

            var parsedIndex = ParseIndexSql(sql);
            var terms = new List<IndexTermDefinition>();
            var keyOrdinal = 0;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT seqno,cid,name,\"desc\",coll,\"key\" FROM pragma_index_xinfo($name) ORDER BY seqno;";
                command.Parameters.AddWithValue("$name", entry.Name);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var cid = reader.GetInt32(1);
                    var key = reader.GetInt32(5);
                    var identity = cid switch
                    {
                        -1 => "<rowid>",
                        -2 when key == 1 && keyOrdinal < parsedIndex.Terms.Length => parsedIndex.Terms[keyOrdinal],
                        _ => reader.IsDBNull(2) ? "<null>" : reader.GetString(2),
                    };
                    terms.Add(new IndexTermDefinition(reader.GetInt32(0), cid, identity, reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), key));
                    if (key == 1) keyOrdinal++;
                }
            }

            var internalName = entry.Origin == "c" ? entry.Name : null;
            var termSignature = JsonSerializer.Serialize(terms, JsonOptions);
            yield return new IndexDefinition(table, internalName, entry.Unique, entry.Origin, entry.Partial, parsedIndex.Predicate, termSignature);
        }
    }

    private static IEnumerable<ForeignKeyDefinition> ReadForeignKeys(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,seq,\"table\",\"from\",\"to\",on_update,on_delete,match FROM pragma_foreign_key_list($table) ORDER BY id,seq;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new ForeignKeyDefinition(table, reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7));
        }
    }

    private static void AssertScratchSchemaDifference(string difference)
    {
        var (firstSql, secondSql) = difference switch
        {
            "missing-autoincrement" => ("CREATE TABLE sample(id INTEGER PRIMARY KEY AUTOINCREMENT);", "CREATE TABLE sample(id INTEGER PRIMARY KEY);"),
            "changed-check" => ("CREATE TABLE sample(value INTEGER CHECK(value > 0));", "CREATE TABLE sample(value INTEGER CHECK(value >= 0));"),
            "changed-unique-order" => ("CREATE TABLE sample(a TEXT,b TEXT,UNIQUE(a,b));", "CREATE TABLE sample(a TEXT,b TEXT,UNIQUE(b,a));"),
            "desc-index-term" => ("CREATE TABLE sample(a TEXT); CREATE INDEX ix_sample ON sample(a ASC);", "CREATE TABLE sample(a TEXT); CREATE INDEX ix_sample ON sample(a DESC);"),
            "collation-index-term" => ("CREATE TABLE sample(a TEXT); CREATE INDEX ix_sample ON sample(a COLLATE BINARY);", "CREATE TABLE sample(a TEXT); CREATE INDEX ix_sample ON sample(a COLLATE NOCASE);"),
            "expression-index-identity" => ("CREATE TABLE sample(a INTEGER,b INTEGER); CREATE INDEX ix_sample ON sample(a + b);", "CREATE TABLE sample(a INTEGER,b INTEGER); CREATE INDEX ix_sample ON sample(a - b);"),
            "partial-index-predicate" => ("CREATE TABLE sample(a INTEGER); CREATE INDEX ix_sample ON sample(a) WHERE a > 0;", "CREATE TABLE sample(a INTEGER); CREATE INDEX ix_sample ON sample(a) WHERE a >= 0;"),
            "foreign-key" => ("CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(parent_id INTEGER);", "CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) ON UPDATE CASCADE ON DELETE SET NULL);"),
            _ => throw new ArgumentOutOfRangeException(nameof(difference), difference, null),
        };

        var first = ReadScratchContract(firstSql);
        var second = ReadScratchContract(secondSql);
        Assert.Equal(first.Tables, second.Tables);
        Assert.Equal(first.Columns, second.Columns);
        switch (difference)
        {
            case "desc-index-term":
                AssertNamedIndex(I("sample", "ix_sample", 0, "c", X(0, 0, "a"), X(1, -1, "<rowid>", key: 0)), first);
                AssertNamedIndex(I("sample", "ix_sample", 0, "c", X(0, 0, "a", descending: 1), X(1, -1, "<rowid>", key: 0)), second);
                break;
            case "collation-index-term":
                AssertNamedIndex(I("sample", "ix_sample", 0, "c", X(0, 0, "a"), X(1, -1, "<rowid>", key: 0)), first);
                AssertNamedIndex(I("sample", "ix_sample", 0, "c", X(0, 0, "a", collation: "NOCASE"), X(1, -1, "<rowid>", key: 0)), second);
                break;
            case "expression-index-identity":
                AssertNamedIndex(I("sample", "ix_sample", 0, "c", X(0, -2, "0:a\u001f3:+\u001f0:b"), X(1, -1, "<rowid>", key: 0)), first);
                AssertNamedIndex(I("sample", "ix_sample", 0, "c", X(0, -2, "0:a\u001f3:-\u001f0:b"), X(1, -1, "<rowid>", key: 0)), second);
                break;
            case "partial-index-predicate":
                AssertNamedIndex(IPartial("sample", "ix_sample", "0:a\u001f3:>\u001f0:0", X(0, 0, "a"), X(1, -1, "<rowid>", key: 0)), first);
                AssertNamedIndex(IPartial("sample", "ix_sample", "0:a\u001f3:>=\u001f0:0", X(0, 0, "a"), X(1, -1, "<rowid>", key: 0)), second);
                break;
            case "foreign-key":
                Assert.Empty(first.ForeignKeys);
                Assert.Equal(new[] { new ForeignKeyDefinition("child", 0, 0, "parent", "parent_id", "id", "CASCADE", "SET NULL", "NONE") }, second.ForeignKeys);
                break;
        }

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertSchemaContract(first, second));
    }

    private static void AssertIntrospectionFieldsFromScratchSchemas()
    {
        using var tableListConnection = OpenScratch("ATTACH DATABASE ':memory:' AS aux; CREATE TABLE aux.aux_table(aux_id INTEGER,label TEXT); CREATE TABLE strict_table(value TEXT,count INTEGER,ratio REAL) STRICT; CREATE TABLE wr_table(id INTEGER PRIMARY KEY) WITHOUT ROWID; CREATE VIEW sample_view AS SELECT value,count FROM strict_table;");
        Assert.Equal(
            new[]
            {
                new TableListDefinition("aux", "aux_table", "table", 2, 0, 0),
                new TableListDefinition("main", "sample_view", "view", 2, 0, 0),
                new TableListDefinition("main", "strict_table", "table", 3, 0, 1),
                new TableListDefinition("main", "wr_table", "table", 1, 1, 0),
            },
            ReadSchemaContract(tableListConnection).Tables);

        using var xinfoConnection = OpenScratch("CREATE TABLE sample(id INTEGER,secondary_id INTEGER,required_text TEXT NOT NULL DEFAULT 'sentinel',stored_value INTEGER GENERATED ALWAYS AS (length(required_text)) STORED,virtual_value TEXT GENERATED ALWAYS AS (required_text || '!') VIRTUAL,PRIMARY KEY(id,secondary_id));");
        Assert.Equal(
            new[]
            {
                C("sample", 0, "id", "INTEGER", pk: 1),
                C("sample", 1, "secondary_id", "INTEGER", pk: 2),
                C("sample", 2, "required_text", "TEXT", notNull: 1, defaultValue: "'sentinel'"),
                C("sample", 3, "stored_value", "INTEGER", hidden: 3),
                C("sample", 4, "virtual_value", "TEXT", hidden: 2),
            },
            ReadSchemaContract(xinfoConnection).Columns);
    }

    private static void AssertWithoutRowIdAuxiliaryTerms()
    {
        var contract = ReadScratchContract("CREATE TABLE sample(a TEXT NOT NULL,b TEXT NOT NULL,c TEXT,PRIMARY KEY(a,b)) WITHOUT ROWID; CREATE INDEX ix_sample_c ON sample(c);");
        AssertNamedIndex(
            I("sample", "ix_sample_c", 0, "c", X(0, 2, "c"), X(1, 0, "a", key: 0), X(2, 1, "b", key: 0)),
            contract);
    }

    private static void AssertNamedIndex(IndexDefinition expected, SchemaContract contract) =>
        Assert.Equal(expected, Assert.Single(contract.Indexes, index => index.Name == expected.Name));

    private static void AssertEquivalentAutoindexSchemasCompareEqual()
    {
        using var first = OpenScratch("CREATE TABLE sample(a TEXT,b TEXT,UNIQUE(a),UNIQUE(b));");
        using var second = OpenScratch("CREATE TABLE sample(a TEXT,b TEXT,UNIQUE(b),UNIQUE(a));");
        AssertSchemaContract(ReadSchemaContract(first), ReadSchemaContract(second));
    }

    private static void AssertSqlDecoysAreIgnored()
    {
        using var connection = OpenScratch("CREATE TABLE sample(id INTEGER PRIMARY KEY,note TEXT DEFAULT 'AUTOINCREMENT CHECK(fake)',\"AUTOINCREMENT\" TEXT,\"CHECK\" TEXT,/* AUTOINCREMENT CHECK(commented) */CHECK(note <> 'CHECK(nested) AUTOINCREMENT')); -- CHECK(line) AUTOINCREMENT\n");
        var definition = Assert.Single(ReadSchemaContract(connection).TableSql);
        Assert.False(definition.AutoIncrement);
        Assert.Equal(CheckSignature(new[] { CanonicalExpression("note <> 'CHECK(nested) AUTOINCREMENT'") }), definition.Checks);
    }

    private static SchemaContract ReadScratchContract(string sql)
    {
        using var connection = OpenScratch(sql);
        return ReadSchemaContract(connection);
    }

    private static SqliteConnection OpenScratch(string sql)
    {
        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        return connection;
    }

    private static ParsedIndexSql ParseIndexSql(string? sql)
    {
        if (sql is null) return new ParsedIndexSql(Array.Empty<string>(), null);
        var tokens = TokenizeSql(sql);
        var on = tokens.FindIndex(token => token.Kind == SqlTokenKind.Word && token.Text == "on");
        var open = tokens.FindIndex(on + 1, token => token.Text == "(");
        if (open < 0) return new ParsedIndexSql(Array.Empty<string>(), null);

        var terms = new List<string>();
        var start = open + 1;
        var depth = 1;
        var close = -1;
        for (var index = open + 1; index < tokens.Count; index++)
        {
            if (tokens[index].Text == "(") depth++;
            else if (tokens[index].Text == ")")
            {
                depth--;
                if (depth == 0)
                {
                    if (index > start) terms.Add(CanonicalTokens(tokens.GetRange(start, index - start)));
                    close = index;
                    break;
                }
            }
            else if (tokens[index].Text == "," && depth == 1)
            {
                terms.Add(CanonicalTokens(tokens.GetRange(start, index - start)));
                start = index + 1;
            }
        }

        var where = close < 0 ? -1 : tokens.FindIndex(close + 1, token => token.Kind == SqlTokenKind.Word && token.Text == "where");
        var predicate = where < 0 ? null : CanonicalTokens(tokens.GetRange(where + 1, tokens.Count - where - 1));
        return new ParsedIndexSql(terms.ToArray(), predicate);
    }

    private static string[] ExtractChecks(List<SqlToken> tokens)
    {
        var checks = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != SqlTokenKind.Word || tokens[index].Text != "check" || index + 1 >= tokens.Count || tokens[index + 1].Text != "(") continue;
            var depth = 1;
            var start = index + 2;
            for (var cursor = start; cursor < tokens.Count; cursor++)
            {
                if (tokens[cursor].Text == "(") depth++;
                else if (tokens[cursor].Text == ")" && --depth == 0)
                {
                    checks.Add(CanonicalTokens(tokens.GetRange(start, cursor - start)));
                    index = cursor;
                    break;
                }
            }
        }
        return checks.Order(StringComparer.Ordinal).ToArray();
    }

    private static string CanonicalExpression(string sql) => CanonicalTokens(TokenizeSql(sql));
    private static string CheckSignature(IEnumerable<string> checks) => JsonSerializer.Serialize(checks.Order(StringComparer.Ordinal), JsonOptions);
    private static string CanonicalTokens(IEnumerable<SqlToken> tokens) => string.Join('\u001f', tokens.Select(token => $"{(int)token.Kind}:{token.Text}"));

    private static List<SqlToken> TokenizeSql(string sql)
    {
        var tokens = new List<SqlToken>();
        for (var index = 0; index < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[index])) { index++; continue; }
            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/')) index++;
                index = Math.Min(index + 2, sql.Length);
                continue;
            }
            if (sql[index] == '\'')
            {
                var start = index++;
                while (index < sql.Length)
                {
                    if (sql[index++] != '\'') continue;
                    if (index < sql.Length && sql[index] == '\'') { index++; continue; }
                    break;
                }
                tokens.Add(new SqlToken(SqlTokenKind.StringLiteral, sql[start..index]));
                continue;
            }
            if (sql[index] is '"' or '`' or '[')
            {
                var start = index;
                var opener = sql[index++];
                var closer = opener == '[' ? ']' : opener;
                while (index < sql.Length)
                {
                    if (sql[index++] != closer) continue;
                    if (opener != '[' && index < sql.Length && sql[index] == closer) { index++; continue; }
                    if (opener == '[' && index < sql.Length && sql[index] == ']') { index++; continue; }
                    break;
                }
                tokens.Add(new SqlToken(SqlTokenKind.QuotedIdentifier, sql[start..index].ToLowerInvariant()));
                continue;
            }
            if (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$' or '.')) index++;
                tokens.Add(new SqlToken(SqlTokenKind.Word, sql[start..index].ToLowerInvariant()));
                continue;
            }

            var symbolLength = 1;
            if (index + 1 < sql.Length && (sql[index..(index + 2)] is ">=" or "<=" or "<>" or "!=" or "==" or "||" or "<<" or ">>" or "->")) symbolLength = 2;
            if (index + 2 < sql.Length && sql[index..(index + 3)] == "->>") symbolLength = 3;
            tokens.Add(new SqlToken(SqlTokenKind.Symbol, sql.Substring(index, symbolLength)));
            index += symbolLength;
        }
        return tokens;
    }

    private static string[] ReadRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index)
                    ? "<null>"
                    : reader.GetValue(index) switch
                    {
                        long value => $"i:{value.ToString(CultureInfo.InvariantCulture)}",
                        double value => $"r:{value.ToString("R", CultureInfo.InvariantCulture)}",
                        string value => $"s:{value}",
                        var value => $"{value.GetType().Name}:{Convert.ToString(value, CultureInfo.InvariantCulture)}",
                    };
            }
            rows.Add(string.Join('|', values));
        }
        return rows.ToArray();
    }

    private static string[] ReadStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, object? id = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return connection;
    }

    private static readonly SchemaContract ExpectedV4SchemaContract = CreateExpectedV4SchemaContract();

    private static SchemaContract CreateExpectedV4SchemaContract()
    {
        var tables = new[]
        {
            new TableListDefinition("main", "monitor_ingestions", "table", 9, 0, 0),
            new TableListDefinition("main", "monitor_spans", "table", 29, 0, 0),
            new TableListDefinition("main", "monitor_traces", "table", 27, 0, 0),
            new TableListDefinition("main", "raw_records", "table", 7, 0, 0),
            new TableListDefinition("main", "schema_version", "table", 2, 0, 0),
        };
        var columns = new[]
        {
            C("monitor_ingestions",0,"id","INTEGER",pk:1), C("monitor_ingestions",1,"raw_record_id","INTEGER",notNull:1), C("monitor_ingestions",2,"received_at","TEXT",notNull:1),
            C("monitor_ingestions",3,"source","TEXT",notNull:1), C("monitor_ingestions",4,"trace_id","TEXT"), C("monitor_ingestions",5,"client_kind","TEXT"),
            C("monitor_ingestions",6,"span_count","INTEGER"), C("monitor_ingestions",7,"projected_at","TEXT",notNull:1), C("monitor_ingestions",8,"span_projected_at","TEXT"),

            C("monitor_spans",0,"id","INTEGER",pk:1), C("monitor_spans",1,"raw_record_id","INTEGER",notNull:1), C("monitor_spans",2,"trace_id","TEXT",notNull:1),
            C("monitor_spans",3,"span_id","TEXT"), C("monitor_spans",4,"parent_span_id","TEXT"), C("monitor_spans",5,"span_ordinal","INTEGER",notNull:1),
            C("monitor_spans",6,"operation","TEXT"), C("monitor_spans",7,"category","TEXT"), C("monitor_spans",8,"tool_name","TEXT"), C("monitor_spans",9,"tool_type","TEXT"),
            C("monitor_spans",10,"mcp_tool_name","TEXT"), C("monitor_spans",11,"mcp_server_hash","TEXT"), C("monitor_spans",12,"agent_name","TEXT"),
            C("monitor_spans",13,"request_model","TEXT"), C("monitor_spans",14,"response_model","TEXT"), C("monitor_spans",15,"input_tokens","INTEGER"),
            C("monitor_spans",16,"output_tokens","INTEGER"), C("monitor_spans",17,"total_tokens","INTEGER"), C("monitor_spans",18,"reasoning_tokens","INTEGER"),
            C("monitor_spans",19,"cache_read_tokens","INTEGER"), C("monitor_spans",20,"cache_creation_tokens","INTEGER"), C("monitor_spans",21,"status","TEXT"),
            C("monitor_spans",22,"error_type","TEXT"), C("monitor_spans",23,"finish_reasons","TEXT"), C("monitor_spans",24,"conversation_id","TEXT"),
            C("monitor_spans",25,"duration_ms","REAL"), C("monitor_spans",26,"start_time","TEXT"), C("monitor_spans",27,"end_time","TEXT"),
            C("monitor_spans",28,"projected_at","TEXT",notNull:1),

            C("monitor_traces",0,"id","INTEGER",pk:1), C("monitor_traces",1,"trace_id","TEXT",notNull:1), C("monitor_traces",2,"client_kind","TEXT"),
            C("monitor_traces",3,"experiment_id","TEXT"), C("monitor_traces",4,"task_id","TEXT"), C("monitor_traces",5,"task_category","TEXT"),
            C("monitor_traces",6,"agent_variant","TEXT"), C("monitor_traces",7,"prompt_version","TEXT"), C("monitor_traces",8,"span_count","INTEGER"),
            C("monitor_traces",9,"tool_call_count","INTEGER"), C("monitor_traces",10,"error_count","INTEGER"), C("monitor_traces",11,"first_seen_at","TEXT"),
            C("monitor_traces",12,"last_seen_at","TEXT"), C("monitor_traces",13,"projected_at","TEXT",notNull:1), C("monitor_traces",14,"input_tokens","INTEGER"),
            C("monitor_traces",15,"output_tokens","INTEGER"), C("monitor_traces",16,"total_tokens","INTEGER"), C("monitor_traces",17,"turn_count","INTEGER"),
            C("monitor_traces",18,"agent_invocation_count","INTEGER"), C("monitor_traces",19,"duration_ms","REAL"), C("monitor_traces",20,"primary_model","TEXT"),
            C("monitor_traces",21,"repository_name","TEXT"), C("monitor_traces",22,"workspace_label","TEXT"), C("monitor_traces",23,"repo_snapshot","TEXT"),
            C("monitor_traces",24,"cache_read_tokens","INTEGER"), C("monitor_traces",25,"cache_creation_tokens","INTEGER"), C("monitor_traces",26,"trace_status","TEXT"),

            C("raw_records",0,"id","INTEGER",pk:1), C("raw_records",1,"source","TEXT",notNull:1), C("raw_records",2,"trace_id","TEXT"),
            C("raw_records",3,"received_at","TEXT",notNull:1), C("raw_records",4,"resource_attributes_json","TEXT"), C("raw_records",5,"payload_json","TEXT",notNull:1),
            C("raw_records",6,"schema_version","INTEGER",notNull:1),
            C("schema_version",0,"component","TEXT",pk:1), C("schema_version",1,"version","INTEGER",notNull:1),
        };
        var tableSql = new[]
        {
            new TableSqlDefinition("monitor_ingestions", true, CheckSignature(Array.Empty<string>())),
            new TableSqlDefinition("monitor_spans", true, CheckSignature(Array.Empty<string>())),
            new TableSqlDefinition("monitor_traces", true, CheckSignature(Array.Empty<string>())),
            new TableSqlDefinition("raw_records", true, CheckSignature(new[] { CanonicalExpression("schema_version = 1"), CanonicalExpression("source IN ('raw-otlp','collector-output','langfuse-export')") })),
            new TableSqlDefinition("schema_version", false, CheckSignature(Array.Empty<string>())),
        };
        var indexes = new[]
        {
            I("monitor_ingestions",null,1,"u", X(0,1,"raw_record_id"), X(1,-1,"<rowid>",key:0)),
            I("monitor_spans",null,1,"u", X(0,1,"raw_record_id"), X(1,5,"span_ordinal"), X(2,-1,"<rowid>",key:0)),
            I("monitor_spans","IX_monitor_spans_raw_record_id",0,"c", X(0,1,"raw_record_id"), X(1,-1,"<rowid>",key:0)),
            I("monitor_spans","IX_monitor_spans_trace_id",0,"c", X(0,2,"trace_id"), X(1,-1,"<rowid>",key:0)),
            I("monitor_traces",null,1,"u", X(0,1,"trace_id"), X(1,-1,"<rowid>",key:0)),
            I("raw_records","IX_raw_records_received_at",0,"c", X(0,3,"received_at"), X(1,-1,"<rowid>",key:0)),
            I("raw_records","IX_raw_records_source",0,"c", X(0,1,"source"), X(1,-1,"<rowid>",key:0)),
            I("raw_records","IX_raw_records_trace_id",0,"c", X(0,2,"trace_id"), X(1,-1,"<rowid>",key:0)),
            I("schema_version",null,1,"pk", X(0,0,"component"), X(1,-1,"<rowid>",key:0)),
        };
        return new SchemaContract(
            tables,
            columns,
            tableSql,
            indexes.OrderBy(index => index.Table, StringComparer.Ordinal).ThenBy(index => index.SemanticSortKey, StringComparer.Ordinal).ToArray(),
            Array.Empty<ForeignKeyDefinition>(),
            Array.Empty<string>());
    }

    private static ColumnDefinition C(string table, int cid, string name, string type, int notNull = 0, string? defaultValue = null, int pk = 0, int hidden = 0) => new(table, cid, name, type, notNull, defaultValue, pk, hidden);
    private static IndexTermDefinition X(int sequence, int cid, string identity, int descending = 0, string? collation = "BINARY", int key = 1) => new(sequence, cid, identity, descending, collation, key);
    private static IndexDefinition I(string table, string? name, int unique, string origin, params IndexTermDefinition[] terms) => new(table, name, unique, origin, 0, null, JsonSerializer.Serialize(terms, JsonOptions));
    private static IndexDefinition IPartial(string table, string name, string predicate, params IndexTermDefinition[] terms) => new(table, name, 0, "c", 1, predicate, JsonSerializer.Serialize(terms, JsonOptions));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record FixtureManifest(string Component, string GenerationCommand, string GitStatusCommand, IReadOnlyList<FixtureEntry> Fixtures);
    private sealed record FixtureEntry(int Version, string File, string SourceCommit, string Sha256, string GitStatusBefore, string GitStatusAfter, FixtureSentinels Sentinels);
    private sealed record FixtureSentinels(long RawRecordId, long IngestionId, long TraceRowId, string TraceId, long? SpanRowId, string? SpanId);
    private sealed record SchemaContract(TableListDefinition[] Tables, ColumnDefinition[] Columns, TableSqlDefinition[] TableSql, IndexDefinition[] Indexes, ForeignKeyDefinition[] ForeignKeys, string[] ViewsAndTriggers);
    private sealed record TableListDefinition(string Schema, string Name, string Type, int ColumnCount, int WithoutRowId, int Strict);
    private sealed record ColumnDefinition(string Table, int Cid, string Name, string Type, int NotNull, string? DefaultValue, int PrimaryKeyOrder, int Hidden);
    private sealed record TableSqlDefinition(string Table, bool AutoIncrement, string Checks);
    private sealed record IndexDefinition(string Table, string? Name, int Unique, string Origin, int Partial, string? Predicate, string Terms)
    {
        public string SemanticSortKey => $"{Name ?? string.Empty}|{Unique}|{Origin}|{Partial}|{Predicate}|{Terms}";
    }
    private sealed record IndexTermDefinition(int Sequence, int Cid, string Identity, int Descending, string? Collation, int Key);
    private sealed record ForeignKeyDefinition(string Table, int Id, int Sequence, string ReferencedTable, string From, string? To, string OnUpdate, string OnDelete, string Match);
    private sealed record ParsedIndexSql(string[] Terms, string? Predicate);
    private sealed record SqlToken(SqlTokenKind Kind, string Text);
    private enum SqlTokenKind { Word, StringLiteral, QuotedIdentifier, Symbol }
}

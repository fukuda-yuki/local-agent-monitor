using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;

public static class SanitizedImportSchemaV1
{
    public const string Component = "sanitized_import";
    public const int Version = 1;

    private static readonly IReadOnlyDictionary<string, string> TableSql = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sanitized_import_history"] = HistoryTableSql,
        ["sanitized_import_records"] = RecordsTableSql,
        ["sanitized_import_origins"] = OriginsTableSql,
        ["sanitized_import_graph_nodes"] = GraphNodesTableSql,
        ["sanitized_import_graph_edges"] = GraphEdgesTableSql,
    };

    private static readonly IReadOnlyDictionary<string, string> IndexSql = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sanitized_import_history_order_idx"] = HistoryOrderIndexSql,
        ["sanitized_import_origins_record_idx"] = OriginsRecordIndexSql,
        ["sanitized_import_graph_edges_source_idx"] = GraphEdgesSourceIndexSql,
        ["sanitized_import_graph_edges_target_idx"] = GraphEdgesTargetIndexSql,
    };

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var hasVersionTable = TableExists(connection, transaction, "schema_version");
        if (!hasVersionTable)
        {
            if (AnyImportTableExists(connection, transaction)) throw new InvalidOperationException("Unstamped sanitized import schema.");
            Execute(connection, transaction, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        }

        var version = ReadVersion(connection, transaction);
        if (version is null)
        {
            if (AnyImportTableExists(connection, transaction)) throw new InvalidOperationException("Unstamped sanitized import schema.");
            foreach (var sql in TableSql.Values) Execute(connection, transaction, sql);
            foreach (var sql in IndexSql.Values) Execute(connection, transaction, sql);
            Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('sanitized_import',1);");
        }
        else if (version != Version)
        {
            throw new InvalidOperationException("Unsupported sanitized import schema version.");
        }

        Validate(connection, transaction);
    }

    internal static void Validate(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (ReadVersion(connection, transaction) != Version) throw new InvalidOperationException("Invalid sanitized import schema version.");
        foreach (var table in TableSql)
            if (!DefinitionMatches(connection, transaction, "table", table.Key, table.Value))
                throw new InvalidOperationException("Invalid sanitized import schema shape.");
        foreach (var index in IndexSql)
            if (!DefinitionMatches(connection, transaction, "index", index.Key, index.Value))
                throw new InvalidOperationException("Invalid sanitized import index shape.");
        var expectedObjects = TableSql.Keys.Concat(IndexSql.Keys).Order(StringComparer.Ordinal).ToArray();
        if (!ReadNamespaceObjects(connection, transaction).SequenceEqual(expectedObjects, StringComparer.Ordinal))
            throw new InvalidOperationException("Invalid sanitized import namespace shape.");
        if (HasUnexpectedAttachedObject(connection, transaction))
            throw new InvalidOperationException("Invalid sanitized import attached object.");
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "PRAGMA foreign_key_check;";
        using var reader = check.ExecuteReader();
        if (reader.Read()) throw new InvalidOperationException("Invalid sanitized import foreign keys.");
    }

    private static bool AnyImportTableExists(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name GLOB 'sanitized_import_*' LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    private static int? ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_version WHERE component='sanitized_import';";
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt32(value);
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool DefinitionMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string type,
        string name,
        string expectedSql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type=$type AND name=$name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is string actualSql
            && string.Equals(NormalizeSql(expectedSql), NormalizeSql(actualSql), StringComparison.Ordinal);
    }

    private static string[] ReadNamespaceObjects(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type IN ('table','index') AND name GLOB 'sanitized_import_*' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static bool HasUnexpectedAttachedObject(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM sqlite_schema
            WHERE (name GLOB 'sanitized_import_*' OR tbl_name GLOB 'sanitized_import_*')
              AND (
                type IN ('trigger','view')
                OR (type='index' AND sql IS NOT NULL AND name NOT IN (
                    'sanitized_import_history_order_idx',
                    'sanitized_import_origins_record_idx',
                    'sanitized_import_graph_edges_source_idx',
                    'sanitized_import_graph_edges_target_idx'))
              )
            LIMIT 1;
            """;
        return command.ExecuteScalar() is not null;
    }

    private static string NormalizeSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private const string HistoryTableSql = """
        CREATE TABLE sanitized_import_history (
            import_id TEXT PRIMARY KEY CHECK(length(import_id)=64),
            archive_sha256 TEXT NOT NULL UNIQUE CHECK(length(archive_sha256)=64),
            preview_digest TEXT NOT NULL CHECK(length(preview_digest)=64),
            status TEXT NOT NULL CHECK(status='committed'),
            new_records INTEGER NOT NULL CHECK(new_records>=0),
            duplicate_records INTEGER NOT NULL CHECK(duplicate_records>=0),
            graph_nodes INTEGER NOT NULL CHECK(graph_nodes>=0),
            graph_edges INTEGER NOT NULL CHECK(graph_edges>=0),
            raw_retention_items INTEGER NOT NULL CHECK(raw_retention_items=0),
            migration_version INTEGER NOT NULL CHECK(migration_version=1),
            migration_chain TEXT NOT NULL,
            migration_step TEXT NOT NULL,
            migration_chain_sha256 TEXT NOT NULL CHECK(length(migration_chain_sha256)=64),
            source_snapshot_id TEXT NOT NULL,
            source_local_monitor_version TEXT NOT NULL,
            imported_at TEXT NOT NULL
        );
        """;

    private const string RecordsTableSql = """
        CREATE TABLE sanitized_import_records (
            local_record_id TEXT PRIMARY KEY CHECK(length(local_record_id)=64),
            record_type TEXT NOT NULL CHECK(record_type IN ('repository_metadata_projection','instruction_finding_handoff','alert_receipt')),
            source_record_id TEXT NOT NULL,
            canonical_sha256 TEXT NOT NULL CHECK(length(canonical_sha256)=64),
            canonical_json BLOB NOT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(record_type,source_record_id)
        );
        """;

    private const string OriginsTableSql = """
        CREATE TABLE sanitized_import_origins (
            import_id TEXT NOT NULL REFERENCES sanitized_import_history(import_id) ON DELETE RESTRICT,
            local_record_id TEXT NOT NULL REFERENCES sanitized_import_records(local_record_id) ON DELETE RESTRICT,
            entry_path TEXT NOT NULL,
            source_snapshot_id TEXT NOT NULL,
            source_local_monitor_version TEXT NOT NULL,
            source_created_at TEXT NOT NULL,
            imported_at TEXT NOT NULL,
            PRIMARY KEY(import_id,local_record_id)
        );
        """;

    private const string GraphNodesTableSql = """
        CREATE TABLE sanitized_import_graph_nodes (
            local_node_id TEXT PRIMARY KEY CHECK(length(local_node_id)=64),
            node_kind TEXT NOT NULL,
            source_id TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('defined','missing','external')),
            defining_record_local_id TEXT NULL REFERENCES sanitized_import_records(local_record_id) ON DELETE RESTRICT,
            first_import_id TEXT NOT NULL REFERENCES sanitized_import_history(import_id) ON DELETE RESTRICT,
            UNIQUE(node_kind,source_id)
        );
        """;

    private const string GraphEdgesTableSql = """
        CREATE TABLE sanitized_import_graph_edges (
            local_edge_id TEXT PRIMARY KEY CHECK(length(local_edge_id)=64),
            source_record_local_id TEXT NOT NULL REFERENCES sanitized_import_records(local_record_id) ON DELETE RESTRICT,
            source_node_id TEXT NOT NULL REFERENCES sanitized_import_graph_nodes(local_node_id) ON DELETE RESTRICT,
            target_node_id TEXT NOT NULL REFERENCES sanitized_import_graph_nodes(local_node_id) ON DELETE RESTRICT,
            relation TEXT NOT NULL,
            edge_ordinal INTEGER NOT NULL CHECK(edge_ordinal>=0),
            resolution_state TEXT NOT NULL CHECK(resolution_state IN ('resolved','missing','external')),
            provenance_json TEXT NOT NULL,
            first_import_id TEXT NOT NULL REFERENCES sanitized_import_history(import_id) ON DELETE RESTRICT
        );
        """;

    private const string HistoryOrderIndexSql =
        "CREATE INDEX sanitized_import_history_order_idx ON sanitized_import_history(imported_at DESC,import_id DESC);";
    private const string OriginsRecordIndexSql =
        "CREATE INDEX sanitized_import_origins_record_idx ON sanitized_import_origins(local_record_id,import_id);";
    private const string GraphEdgesSourceIndexSql =
        "CREATE INDEX sanitized_import_graph_edges_source_idx ON sanitized_import_graph_edges(source_node_id,local_edge_id);";
    private const string GraphEdgesTargetIndexSql =
        "CREATE INDEX sanitized_import_graph_edges_target_idx ON sanitized_import_graph_edges(target_node_id,local_edge_id);";
}

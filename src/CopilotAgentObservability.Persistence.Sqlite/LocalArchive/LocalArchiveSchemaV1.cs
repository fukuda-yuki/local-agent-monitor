using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalArchiveSchemaV1
{
    internal const string ComponentName = "local_archive";
    internal const int Version = 1;
    internal static string CanonicalSql { get; } = ReadCanonicalSql();

    private static readonly string[] TableNames = ["local_archive_current", "local_archive_events"];
    private static readonly string[] IndexNames =
    [
        "IX_local_archive_current_archived_page",
        "IX_local_archive_events_target_revision",
    ];
    private static readonly string[] TriggerNames =
    [
        "local_archive_current_identity_update_rejected",
        "local_archive_current_delete_rejected",
        "local_archive_current_insert_replacement_rejected",
        "local_archive_events_update_rejected",
        "local_archive_events_delete_rejected",
        "local_archive_events_insert_replacement_rejected",
    ];
    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> Definitions =
        BuildDefinitions();
    private static readonly IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        ExpectedObjects = SqliteOwnedSchemaAuthority.Compile(Definitions);

    internal static IEnumerable<SqliteOwnedSchemaObject> OwnedObjects =>
        ExpectedObjects.Values;
    internal static IReadOnlyList<(string Name, string Table, string Sql)> TriggerDefinitions { get; } =
        Definitions
            .Where(static item => item.Type == "trigger")
            .Select(static item => (item.Name, item.Table, item.Sql))
            .ToArray();

    internal static void Ensure(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction);
        transaction.Commit();
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var declared = ReadDeclaredVersion(connection, transaction);
        var objects = ReadOwnedObjects(connection, transaction);
        if (declared is not null || objects.Count != 0)
        {
            Validate(connection, transaction);
            return;
        }

        ValidateDependencies(connection, transaction);
        Execute(connection, transaction, CanonicalSql);
        if (!HasExactOwnedSchema(connection, transaction)
            || Count(connection, transaction, "local_archive_current") != 0
            || Count(connection, transaction, "local_archive_events") != 0)
        {
            Reject();
        }
        Execute(
            connection,
            transaction,
            "INSERT INTO schema_version(component,version) VALUES('local_archive',1);");
    }

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (ReadDeclaredVersion(connection, transaction) != Version)
            Reject();
        ValidateDependencies(connection, transaction);
        if (!HasExactOwnedSchema(connection, transaction))
            Reject();
    }

    internal static bool HasExactOwnedSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Equal(ReadOwnedObjects(connection, transaction), ExpectedObjects);

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        ReadOwnedObjects(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(connection, transaction, static (name, table) =>
            name.StartsWith("local_archive_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("IX_local_archive_", StringComparison.OrdinalIgnoreCase)
            || table.StartsWith("local_archive_", StringComparison.OrdinalIgnoreCase));

    private static long? ReadDeclaredVersion(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (!ObjectExists(connection, transaction, "table", "schema_version"))
            return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT component,typeof(component),version,typeof(version) " +
            "FROM schema_version WHERE component='local_archive' COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.GetValue(0) is not string component
            || component != ComponentName
            || reader.GetString(1) != "text"
            || reader.GetString(3) != "integer")
            Reject();
        var version = reader.GetInt64(2);
        if (reader.Read())
            Reject();
        return version;
    }

    private static void ValidateDependencies(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (!SqliteSessionStore.IsCurrentSchemaValid(connection, transaction))
            throw DependencyInvalid();
        try
        {
            LocalRepositoryCatalogSchemaV1.Validate(connection, transaction);
        }
        catch (InvalidOperationException)
        {
            throw DependencyInvalid();
        }
    }

    private static bool ObjectExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string type,
        string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type=$type AND name=$name);";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static long Count(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<SqliteOwnedSchemaDefinition> BuildDefinitions()
    {
        var statements = CanonicalSql.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        if (statements.Length != 10)
            throw new InvalidOperationException("local_archive_schema_artifact_invalid");
        var definitions = new List<SqliteOwnedSchemaDefinition>(10);
        for (var index = 0; index < TableNames.Length; index++)
            definitions.Add(new("table", TableNames[index], TableNames[index], statements[index]));
        definitions.Add(new("index", IndexNames[0], TableNames[0], statements[2]));
        definitions.Add(new("index", IndexNames[1], TableNames[1], statements[3]));
        for (var index = 0; index < TriggerNames.Length; index++)
        {
            definitions.Add(new(
                "trigger",
                TriggerNames[index],
                index < 3 ? TableNames[0] : TableNames[1],
                statements[index + 4]));
        }
        return definitions;
    }

    private static string ReadCanonicalSql()
    {
        using var stream = typeof(LocalArchiveSchemaV1).Assembly.GetManifestResourceStream(
            "local_archive.schema.v1.sql")
            ?? throw new InvalidOperationException("local_archive_schema_artifact_missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }

    private static InvalidOperationException DependencyInvalid() =>
        new("local_archive_component_dependency_invalid");

    private static void Reject() =>
        throw new InvalidOperationException(
            "Unsupported incomplete local_archive schema version 1.");
}

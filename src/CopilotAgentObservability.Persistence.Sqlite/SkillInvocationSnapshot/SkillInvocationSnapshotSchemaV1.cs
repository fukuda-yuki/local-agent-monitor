using System.Globalization;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

internal static class SkillInvocationSnapshotSchemaV1
{
    internal const string ComponentName = "skill_invocation_snapshot";
    internal const int Version = 1;
    internal static string CanonicalSql { get; } = ReadCanonicalSql();

    private static readonly string[] TableNames =
        ["skill_invocation_snapshots", "skill_invocation_snapshot_receipts"];

    // Declared order matches the artifact: three rows-table triggers, three receipts-table
    // triggers, then the two session_events triggers already registered with Session in
    // SessionChildTriggerExtensionRegistry (not duplicated here).
    private static readonly (string Name, string Table)[] TriggerTargets =
    [
        ("skill_invocation_snapshot_rows_update_rejected", "skill_invocation_snapshots"),
        ("skill_invocation_snapshot_rows_delete_rejected", "skill_invocation_snapshots"),
        ("skill_invocation_snapshot_rows_replacement_rejected", "skill_invocation_snapshots"),
        ("skill_invocation_snapshot_receipts_update_rejected", "skill_invocation_snapshot_receipts"),
        ("skill_invocation_snapshot_receipts_delete_rejected", "skill_invocation_snapshot_receipts"),
        ("skill_invocation_snapshot_receipts_replacement_rejected", "skill_invocation_snapshot_receipts"),
        ("skill_invocation_snapshot_session_event_update_rejected", "session_events"),
        ("skill_invocation_snapshot_session_event_delete_rejected", "session_events"),
    ];

    internal static IReadOnlyList<SqliteOwnedSchemaDefinition> Definitions { get; } = BuildDefinitions();

    internal static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> ExpectedObjects
    { get; } = SqliteOwnedSchemaAuthority.Compile(Definitions);

    internal static IEnumerable<SqliteOwnedSchemaObject> OwnedObjects => ExpectedObjects.Values;

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
            SkillInvocationSnapshotSchemaV1Validator.Validate(connection, transaction);
            return;
        }

        ValidateDependencies(connection, transaction);

        Execute(connection, transaction, CanonicalSql);

        // The stamp must already be visible when Session revalidates below: Session's
        // conditional child-trigger registry only activates for the two session_events
        // triggers once it reads this exact stamp from schema_version. Never INSERT OR
        // REPLACE — a second install attempt must fail closed at the guard above, not
        // silently restamp.
        Execute(
            connection,
            transaction,
            "INSERT INTO schema_version(component,version) VALUES('skill_invocation_snapshot',1);");

        if (!SqliteSessionStore.IsCurrentSchemaValid(connection, transaction))
            Reject();

        SkillInvocationSnapshotSchemaV1Validator.Validate(connection, transaction);
        if (Count(connection, transaction, "skill_invocation_snapshots") != 0
            || Count(connection, transaction, "skill_invocation_snapshot_receipts") != 0)
            Reject();
    }

    internal static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        ReadOwnedObjects(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(
            connection,
            transaction,
            // Name-only match: the two session_events triggers are owned by this component
            // even though their tbl_name is the parent table, not skill_invocation_snapshot*.
            // Matching on tbl_name as well would both miss those two triggers and falsely
            // claim unrelated session_events objects.
            static (name, _) => name.StartsWith(ComponentName, StringComparison.OrdinalIgnoreCase));

    internal static long? ReadDeclaredVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!ObjectExists(connection, transaction, "table", "schema_version"))
            return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT component,typeof(component),version,typeof(version) " +
            "FROM schema_version WHERE component='skill_invocation_snapshot' COLLATE NOCASE;";
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

    internal static bool HasExactOwnedSchema(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Equal(ReadOwnedObjects(connection, transaction), ExpectedObjects);

    private static void ValidateDependencies(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!SqliteSessionStore.IsCurrentSchemaValid(connection, transaction))
            throw DependencyInvalid();
        if (!IsRetentionSchemaValid(connection, transaction))
            throw DependencyInvalid();
        try
        {
            CopilotAgentObservability.Persistence.Sqlite.SkillProjectionSchemaV1.Validate(connection, transaction);
        }
        catch (InvalidOperationException)
        {
            throw DependencyInvalid();
        }
    }

    // Retention publishes no public validator: RetentionSchemaMigrator.Apply is a mutating,
    // idempotent installer, not a read-only check. This mirrors SkillProjectionSchemaV1's own
    // Retention dependency check, which reads retention_component_versions directly rather than
    // introducing a second Retention validation contract.
    private static bool IsRetentionSchemaValid(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!ObjectExists(connection, transaction, "table", "retention_component_versions"))
            return false;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT version FROM retention_component_versions WHERE component='retention';";
        var value = command.ExecuteScalar();
        return value is not null
            && Convert.ToInt64(value, CultureInfo.InvariantCulture)
               == CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionV1Constants.CatalogSchemaVersion;
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
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static long Count(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<SqliteOwnedSchemaDefinition> BuildDefinitions()
    {
        var statements = SplitStatements(CanonicalSql);
        if (statements.Count != 10)
            throw new InvalidOperationException("skill_invocation_snapshot_schema_artifact_invalid");

        var definitions = new List<SqliteOwnedSchemaDefinition>(10);
        for (var index = 0; index < TableNames.Length; index++)
            definitions.Add(new("table", TableNames[index], TableNames[index], statements[index]));
        for (var index = 0; index < TriggerTargets.Length; index++)
        {
            var (name, table) = TriggerTargets[index];
            definitions.Add(new("trigger", name, table, statements[TableNames.Length + index]));
        }
        return definitions;
    }

    // The artifact has no blank line between the second CREATE TABLE's closing ");" and the
    // first CREATE TRIGGER, so LocalArchiveSchemaV1's "\n\n" blank-line split would fuse those
    // two statements into one chunk (9 chunks instead of 10). Split on every line-start
    // "CREATE TABLE " / "CREATE TRIGGER " occurrence instead.
    private static List<string> SplitStatements(string sql)
    {
        var starts = new List<int>();
        for (var index = 0; index < sql.Length; index++)
        {
            if (index != 0 && sql[index - 1] != '\n')
                continue;
            var remainder = sql.AsSpan(index);
            if (remainder.StartsWith("CREATE TABLE ", StringComparison.Ordinal)
                || remainder.StartsWith("CREATE TRIGGER ", StringComparison.Ordinal))
                starts.Add(index);
        }

        var statements = new List<string>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1] : sql.Length;
            statements.Add(sql[start..end].TrimEnd());
        }
        return statements;
    }

    private static string ReadCanonicalSql()
    {
        using var stream = typeof(SkillInvocationSnapshotSchemaV1).Assembly.GetManifestResourceStream(
            "skill-invocation-snapshot.schema.v1.sql")
            ?? throw new InvalidOperationException("skill_invocation_snapshot_schema_artifact_missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }

    private static InvalidOperationException DependencyInvalid() =>
        new("skill_invocation_snapshot_component_dependency_invalid");

    private static void Reject() =>
        throw new InvalidOperationException(
            "Unsupported incomplete skill_invocation_snapshot schema version 1.");
}

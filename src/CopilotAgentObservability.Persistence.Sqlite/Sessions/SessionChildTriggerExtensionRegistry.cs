using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

// Each Sql value is the delimiter-free text SQLite stores in sqlite_schema.sql after the
// canonical child DDL runs; the executable DDL source ends "END;" and SQLite drops that
// delimiter. Comparing against the DDL source instead would make every installed database
// look altered. Newlines are written as escapes so the checked-in registry bytes survive
// any source-file line-ending normalization.
internal static class SessionChildTriggerExtensionRegistry
{
    internal const string RegistrySchemaVersion = "session-child-trigger-extension-registry.v1";
    internal const int RegistryRevision = 1;

    internal sealed record ChildTrigger(string Name, string TargetTable, string Sql);

    internal sealed record ChildEntry(
        string Component,
        int Version,
        string ParentComponent,
        int ParentVersion,
        IReadOnlyList<ChildTrigger> Triggers);

    internal enum StampKind
    {
        Inactive,
        Active,
        Incompatible,
    }

    internal readonly record struct StampResolution(
        StampKind Kind,
        IReadOnlyList<ChildEntry> ActiveEntries);

    private static readonly IReadOnlyList<ChildEntry> RegisteredEntries =
    [
        new(
            "skill_invocation_snapshot",
            1,
            "session",
            14,
            [
                new(
                    "skill_invocation_snapshot_session_event_update_rejected",
                    "session_events",
                    "CREATE TRIGGER skill_invocation_snapshot_session_event_update_rejected\n"
                    + "BEFORE UPDATE ON session_events\n"
                    + "WHEN EXISTS(SELECT 1 FROM skill_invocation_snapshots s\n"
                    + "            WHERE s.event_id=OLD.event_id)\n"
                    + "BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_event_immutable'); END"),
                new(
                    "skill_invocation_snapshot_session_event_delete_rejected",
                    "session_events",
                    "CREATE TRIGGER skill_invocation_snapshot_session_event_delete_rejected\n"
                    + "BEFORE DELETE ON session_events\n"
                    + "WHEN EXISTS(SELECT 1 FROM skill_invocation_snapshots s\n"
                    + "            WHERE s.event_id=OLD.event_id)\n"
                    + "BEGIN SELECT RAISE(ABORT,'skill_invocation_snapshot_event_immutable'); END"),
            ]),
    ];

    private static readonly StampResolution InactiveResolution =
        new(StampKind.Inactive, []);
    private static readonly StampResolution IncompatibleResolution =
        new(StampKind.Incompatible, []);

    internal static IReadOnlyList<ChildEntry> Entries => RegisteredEntries;

    internal static StampResolution ResolveStamp(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string parentComponent,
        int parentVersion)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(parentComponent);

        var candidates = RegisteredEntries
            .Where(entry => string.Equals(entry.ParentComponent, parentComponent, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0 || !SchemaVersionTableExists(connection, transaction))
        {
            return InactiveResolution;
        }

        var stamped = ReadStampedRows(connection, transaction, candidates);
        if (stamped is null)
        {
            return IncompatibleResolution;
        }
        if (stamped.Count == 0)
        {
            return InactiveResolution;
        }

        var active = new List<ChildEntry>(stamped.Count);
        foreach (var (component, version) in stamped)
        {
            var entry = candidates.SingleOrDefault(candidate =>
                string.Equals(candidate.Component, component, StringComparison.Ordinal)
                && candidate.Version == version);
            if (entry is null || entry.ParentVersion != parentVersion)
            {
                return IncompatibleResolution;
            }
            active.Add(entry);
        }
        return new(StampKind.Active, active);
    }

    // Returns null for every incompatible stamp shape: a non-text component, a component that
    // matches a registered namespace only case-insensitively, a non-integer version, or a
    // duplicated component row.
    private static List<(string Component, long Version)>? ReadStampedRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<ChildEntry> candidates)
    {
        var rows = new List<(string Component, long Version)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT component,typeof(component),version,typeof(version) FROM schema_version;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.GetString(1).Equals("text", StringComparison.Ordinal))
            {
                continue;
            }
            var component = reader.GetString(0);
            if (!candidates.Any(candidate =>
                    string.Equals(candidate.Component, component, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!candidates.Any(candidate =>
                    string.Equals(candidate.Component, component, StringComparison.Ordinal))
                || !reader.GetString(3).Equals("integer", StringComparison.Ordinal)
                || !seen.Add(component))
            {
                return null;
            }
            rows.Add((component, reader.GetInt64(2)));
        }
        return rows;
    }

    private static bool SchemaVersionTableExists(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='schema_version');";
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }
}

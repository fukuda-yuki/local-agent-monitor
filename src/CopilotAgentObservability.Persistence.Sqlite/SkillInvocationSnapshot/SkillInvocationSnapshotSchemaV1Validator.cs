using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

// Scope for this package: structural proof only (stamp shape, exact owned-object set,
// namespace containment, foreign-key resolution, parent revalidation). Row-level graph
// invariants (receipt reconstruction, content/Retention/claim equality) are a later package.
internal static class SkillInvocationSnapshotSchemaV1Validator
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ExpectedForeignKeyParents =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["skill_invocation_snapshots"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "sessions",
                "session_events",
                "session_runs",
                "skill_projection_sdk_claims",
                "retention_items",
            },
            ["skill_invocation_snapshot_receipts"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "skill_invocation_snapshots",
            },
        };

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (SkillInvocationSnapshotSchemaV1.ReadDeclaredVersion(connection, transaction)
            != SkillInvocationSnapshotSchemaV1.Version)
            Reject();

        // SqliteOwnedSchemaAuthority.Equal compares actual.Count to expected.Count, so any
        // object whose name starts with the component prefix beyond the ten expected ones
        // already fails this check — a separate namespace scan would be redundant.
        if (!SkillInvocationSnapshotSchemaV1.HasExactOwnedSchema(connection, transaction))
            Reject();

        ValidateForeignKeys(connection, transaction);

        if (!SqliteSessionStore.IsCurrentSchemaValid(connection, transaction))
            Reject();
    }

    internal static bool IsValid(SqliteConnection connection, SqliteTransaction? transaction)
    {
        try
        {
            Validate(connection, transaction);
            return true;
        }
        catch (SqliteException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static void ValidateForeignKeys(SqliteConnection connection, SqliteTransaction? transaction)
    {
        foreach (var (table, expectedParents) in ExpectedForeignKeyParents)
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = $"SELECT COUNT(*) FROM pragma_foreign_key_check('{table}');";
                if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    Reject();
            }

            using var list = connection.CreateCommand();
            list.Transaction = transaction;
            list.CommandText = $"PRAGMA foreign_key_list({table});";
            using var reader = list.ExecuteReader();
            var actualParents = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                actualParents.Add(reader.GetString(2));
            if (!actualParents.SetEquals(expectedParents))
                Reject();
        }
    }

    private static void Reject() =>
        throw new InvalidOperationException(
            "Unsupported incomplete skill_invocation_snapshot schema version 1.");
}

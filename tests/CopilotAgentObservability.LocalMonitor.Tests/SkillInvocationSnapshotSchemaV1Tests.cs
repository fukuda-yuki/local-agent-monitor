using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationSnapshotSchemaV1Tests
{
    private const string ExpectedArtifactSha256 =
        "502f787c28b13363826aeccde96979ed22dc89c8ee137593922b106528935d7c";
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-08-17T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly (string Type, string Name, string Table)[] ExpectedDefinitionOrder =
    [
        ("table", "skill_invocation_snapshots", "skill_invocation_snapshots"),
        ("table", "skill_invocation_snapshot_receipts", "skill_invocation_snapshot_receipts"),
        ("trigger", "skill_invocation_snapshot_rows_update_rejected", "skill_invocation_snapshots"),
        ("trigger", "skill_invocation_snapshot_rows_delete_rejected", "skill_invocation_snapshots"),
        ("trigger", "skill_invocation_snapshot_rows_replacement_rejected", "skill_invocation_snapshots"),
        ("trigger", "skill_invocation_snapshot_receipts_update_rejected", "skill_invocation_snapshot_receipts"),
        ("trigger", "skill_invocation_snapshot_receipts_delete_rejected", "skill_invocation_snapshot_receipts"),
        ("trigger", "skill_invocation_snapshot_receipts_replacement_rejected", "skill_invocation_snapshot_receipts"),
        ("trigger", "skill_invocation_snapshot_session_event_update_rejected", "session_events"),
        ("trigger", "skill_invocation_snapshot_session_event_delete_rejected", "session_events"),
    ];

    [Fact]
    public void EmbeddedArtifact_MatchesDocsContractByteForByteAndHash()
    {
        var docsPath = Path.Combine(
            FindRepositoryRoot(),
            "docs", "specifications", "contracts", "skill-invocation-snapshot", "v1",
            "skill-invocation-snapshot.schema.v1.sql");
        var expectedBytes = File.ReadAllBytes(docsPath);

        using var stream = typeof(SkillInvocationSnapshotSchemaV1).Assembly.GetManifestResourceStream(
            "skill-invocation-snapshot.schema.v1.sql")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var actualBytes = memory.ToArray();

        Assert.Equal(9213, actualBytes.Length);
        Assert.Equal(expectedBytes, actualBytes);
        Assert.Equal(ExpectedArtifactSha256, Convert.ToHexStringLower(SHA256.HashData(actualBytes)));
    }

    [Fact]
    public void CanonicalSql_SplitsIntoExactlyTenStatementsInDeclaredOrder()
    {
        Assert.Equal(10, SkillInvocationSnapshotSchemaV1.Definitions.Count);
        Assert.Equal(
            ExpectedDefinitionOrder,
            SkillInvocationSnapshotSchemaV1.Definitions
                .Select(definition => (definition.Type, definition.Name, definition.Table))
                .ToArray());
        Assert.Equal(2, SkillInvocationSnapshotSchemaV1.Definitions.Count(definition => definition.Type == "table"));
        Assert.Equal(8, SkillInvocationSnapshotSchemaV1.Definitions.Count(definition => definition.Type == "trigger"));
    }

    [Fact]
    public void Ensure_OnValidBaseDatabase_InstallsSchemaStampsIntegerVersionAndLeavesSessionValid()
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);

        SkillInvocationSnapshotSchemaV1.Ensure(connection);

        foreach (var table in new[] { "skill_invocation_snapshots", "skill_invocation_snapshot_receipts" })
        {
            Assert.Equal(
                1L,
                Scalar<long>(
                    connection,
                    $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        }
        Assert.Equal(
            8L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'skill_invocation_snapshot%';"));

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT typeof(component),version,typeof(version) FROM schema_version " +
                "WHERE component='skill_invocation_snapshot';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("text", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal("integer", reader.GetString(2));
            Assert.False(reader.Read());
        }

        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    [Fact]
    public void Validator_AfterEnsure_IsValid()
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);
        SkillInvocationSnapshotSchemaV1.Ensure(connection);

        Assert.True(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));
    }

    [Fact]
    public void Ensure_CalledTwice_IsIdempotentAndDoesNotThrow()
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);
        SkillInvocationSnapshotSchemaV1.Ensure(connection);

        SkillInvocationSnapshotSchemaV1.Ensure(connection);

        Assert.Equal(
            1L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM schema_version WHERE component='skill_invocation_snapshot';"));
        Assert.Equal(
            10L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'skill_invocation_snapshot%';"));
        Assert.True(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));
    }

    [Fact]
    public void Ensure_WhenCallerTransactionRollsBackAfterFailure_LeavesNoPartialObjectsOrStamp()
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);

        // Injected failure: a table pre-exists under one of the ten canonical object names.
        // SkillInvocationSnapshotSchemaV1's ownership predicate matches by name prefix, so this
        // trips Ensure's "a declared version row exists OR any owned object exists" branch
        // *before* the canonical DDL or the stamp INSERT ever runs: Ensure delegates straight to
        // SkillInvocationSnapshotSchemaV1Validator.Validate, which rejects the incomplete
        // (1-of-10) owned-object set and throws. Rolling back the caller's transaction below must
        // remove every trace, including the one conflicting object this test itself created.
        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            "CREATE TABLE skill_invocation_snapshot_rows_update_rejected(x INTEGER);",
            transaction);

        Assert.Throws<InvalidOperationException>(
            () => SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction));

        transaction.Rollback();

        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'skill_invocation_snapshot%';"));
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM schema_version WHERE component='skill_invocation_snapshot';"));
    }

    [Fact]
    public void Ensure_WhenSessionSchemaIsInvalid_ThrowsDependencyGateAndInstallsNothing()
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);
        Execute(
            connection,
            "UPDATE session_events SET terminal_outcome='failed'; UPDATE sessions SET status='failed';");
        Assert.False(SqliteSessionStore.IsCurrentSchemaValid(connection, null));

        var error = Assert.Throws<InvalidOperationException>(
            () => SkillInvocationSnapshotSchemaV1.Ensure(connection));

        Assert.Equal("skill_invocation_snapshot_component_dependency_invalid", error.Message);
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'skill_invocation_snapshot%';"));
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM schema_version WHERE component='skill_invocation_snapshot';"));
    }

    [Fact]
    public void Validator_WhenExtraNamespaceObjectExists_BecomesInvalid()
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);
        SkillInvocationSnapshotSchemaV1.Ensure(connection);
        Assert.True(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));

        Execute(
            connection,
            "CREATE INDEX skill_invocation_snapshot_extra_index ON skill_invocation_snapshots(session_id);");

        Assert.False(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));
    }

    // schema_version.version is declared INTEGER, so SQLite affinity silently rewrites the
    // spec's literal text "1" stamp fault into integer 1 on store; that exact shape cannot
    // exist in this database. The typeof(version)='integer' guard is still reachable through
    // any value affinity leaves as text, which is what nonintegral_version exercises.
    [Fact]
    public void StampVersionColumnAffinity_StoresTextOneAsInteger()
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);
        SkillInvocationSnapshotSchemaV1.Ensure(connection);

        Execute(
            connection,
            "UPDATE schema_version SET version='1' WHERE component='skill_invocation_snapshot';");

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT typeof(version) FROM schema_version WHERE component='skill_invocation_snapshot';";
        Assert.Equal("integer", command.ExecuteScalar() as string);
        Assert.True(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));
    }

    [Theory]
    [InlineData("nonintegral_version")]
    [InlineData("wrong_version")]
    [InlineData("duplicate_row")]
    public void Validator_RejectsEachStampFault(string fault)
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);
        SkillInvocationSnapshotSchemaV1.Ensure(connection);
        Assert.True(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));

        Execute(connection, fault switch
        {
            "nonintegral_version" =>
                "UPDATE schema_version SET version='v1' WHERE component='skill_invocation_snapshot';",
            "wrong_version" =>
                "UPDATE schema_version SET version=2 WHERE component='skill_invocation_snapshot';",
            // component has no COLLATE NOCASE primary key, so a case-different component text is
            // a distinct row that still matches the validator's COLLATE NOCASE lookup: a genuine
            // duplicate stamp under case-insensitive component identity.
            "duplicate_row" =>
                "INSERT INTO schema_version(component,version) VALUES('Skill_Invocation_Snapshot',1);",
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        });

        Assert.False(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));
    }

    [Fact]
    public void InstalledTriggers_EnforceRowAppendOnlyAndSessionEventImmutability()
    {
        using var temp = BuildBaseDatabase();
        using var connection = Open(temp.DatabasePath);
        SkillInvocationSnapshotSchemaV1.Ensure(connection);

        var sessionId = Scalar<string>(connection, "SELECT session_id FROM sessions;");
        var eventId = Scalar<string>(connection, "SELECT event_id FROM session_events;");
        var contentItemId = Scalar<string>(
            connection,
            "SELECT item_id FROM retention_items WHERE store_kind='session_event_content';");
        var snapshotId = Guid.CreateVersion7().ToString("D");
        const string timestamp = "2026-08-17T00:00:00.0000000+00:00";

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO skill_invocation_snapshots(
                    snapshot_id,session_id,native_session_id,event_id,claim_id,run_id,trace_id,span_id,
                    name,source,trigger,state,reason,content_item_id,payload_sha256,payload_bytes,
                    content_document_sha256,body_sha256,body_utf8_bytes,definition_path_sha256,
                    definition_path_utf8_bytes,source_parent_event_id,source_ephemeral,
                    source_application_version,adapter_version,normalization_version,payload_schema,
                    schema_fingerprint,captured_at,created_at)
                VALUES(
                    $snapshot,$session,$native,$event,NULL,NULL,NULL,NULL,
                    NULL,NULL,NULL,'missing','body_missing',$content,$payload,2,
                    $document,NULL,NULL,NULL,
                    NULL,NULL,0,
                    '1.0.0','sdk-v1','session-normalization-v1','github-copilot-sdk.skill-invoked.v1',
                    $fingerprint,$captured,$created);
                """;
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$native", "native-session-acceptance");
            command.Parameters.AddWithValue("$event", eventId);
            command.Parameters.AddWithValue("$content", contentItemId);
            command.Parameters.AddWithValue("$payload", new string('a', 64));
            command.Parameters.AddWithValue("$document", new string('b', 64));
            command.Parameters.AddWithValue("$fingerprint", new string('c', 64));
            command.Parameters.AddWithValue("$captured", timestamp);
            command.Parameters.AddWithValue("$created", timestamp);
            command.ExecuteNonQuery();
        }

        var updateRow = Assert.Throws<SqliteException>(() => Execute(
            connection,
            $"UPDATE skill_invocation_snapshots SET native_session_id='changed' WHERE snapshot_id='{snapshotId}';"));
        Assert.Contains("skill_invocation_snapshot_append_only", updateRow.Message, StringComparison.Ordinal);

        var deleteRow = Assert.Throws<SqliteException>(() => Execute(
            connection,
            $"DELETE FROM skill_invocation_snapshots WHERE snapshot_id='{snapshotId}';"));
        Assert.Contains("skill_invocation_snapshot_append_only", deleteRow.Message, StringComparison.Ordinal);

        var updateEvent = Assert.Throws<SqliteException>(() => Execute(
            connection,
            $"UPDATE session_events SET status='changed' WHERE event_id='{eventId}';"));
        Assert.Contains("skill_invocation_snapshot_event_immutable", updateEvent.Message, StringComparison.Ordinal);

        var deleteEvent = Assert.Throws<SqliteException>(() => Execute(
            connection,
            $"DELETE FROM session_events WHERE event_id='{eventId}';"));
        Assert.Contains("skill_invocation_snapshot_event_immutable", deleteEvent.Message, StringComparison.Ordinal);
    }

    private static MonitorTempDirectory BuildBaseDatabase()
    {
        var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(ObservedAt) };
        var store = CreateStore(temp);
        Normalize(store, temp);
        return temp;
    }

    private static SqliteSessionStore CreateStore(MonitorTempDirectory temp)
    {
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        return store;
    }

    private static void Normalize(SqliteSessionStore store, MonitorTempDirectory temp)
    {
        using var document = JsonDocument.Parse("{}");
        var envelope = new SessionIngestEnvelope(
            1,
            "copilot-sdk-stream",
            "copilot-sdk",
            "skill-invocation-snapshot-acceptance-session",
            [
                new(
                    "skill-invocation-snapshot-acceptance-event",
                    "session.task_complete",
                    ObservedAt.ToString("O"),
                    document.RootElement.Clone())
            ],
            SourceApplicationVersion: "1.0.0",
            AdapterVersion: "sdk-v1",
            NormalizationVersion: "session-normalization-v1");
        new SessionEventNormalizer(store, temp.TimeProvider).NormalizeAndWrite(envelope);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Unable to locate repository root.");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}

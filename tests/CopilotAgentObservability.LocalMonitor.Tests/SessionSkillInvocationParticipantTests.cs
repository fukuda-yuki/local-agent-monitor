using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionSkillInvocationParticipantTests
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";
    private const string DefaultAdapter = "copilot-sdk-stream";
    private const string DefaultSurface = "copilot-sdk";
    private const string DefaultPayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private static readonly DateTimeOffset DefaultWriteAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] DefaultPayloadToken = "{\"skill\":\"demo\"}"u8.ToArray();

    private static readonly string[] EightTables =
    [
        "sessions", "session_native_ids", "session_events", "session_event_content",
        "retention_items", "skill_projection_sdk_claims", "skill_invocation_snapshots",
        "skill_invocation_snapshot_receipts",
    ];

    [Fact]
    public void First_available_write_with_no_run_inserts_one_row_per_table_and_zero_runs()
    {
        using var database = new TestDatabase();
        var write = NewWrite(nativeSessionId: "native-p1");

        var outcome = Commit(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        foreach (var table in EightTables)
            Assert.Equal(1, database.Count(table));
        Assert.Equal(0, database.Count("session_runs"));
    }

    [Fact]
    public void Every_mandated_write_at_and_expires_at_equality_holds_after_a_fresh_insert()
    {
        using var database = new TestDatabase();
        var occurredAt = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var writeAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var write = NewWrite(nativeSessionId: "native-p2", occurredAt: occurredAt, writeAt: writeAt);

        var outcome = Commit(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        var writeAtText = FormatTimestamp(writeAt);
        var expiresAtText = FormatTimestamp(write.ExpiresAt);

        Assert.Equal(writeAtText, database.ScalarText("SELECT captured_at FROM session_event_content;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT captured_at FROM retention_items;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT captured_at FROM skill_invocation_snapshots;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT created_at FROM skill_invocation_snapshots;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT created_at FROM skill_invocation_snapshot_receipts;"));
        Assert.Equal(expiresAtText, database.ScalarText("SELECT expires_at FROM session_event_content;"));
        Assert.Equal(expiresAtText, database.ScalarText("SELECT expires_at FROM retention_items;"));

        var sessionCreatedAt = database.ScalarText("SELECT created_at FROM sessions;");
        var sessionUpdatedAt = database.ScalarText("SELECT updated_at FROM sessions;");
        var snapshotCapturedAt = database.ScalarText("SELECT captured_at FROM skill_invocation_snapshots;");
        Assert.True(string.CompareOrdinal(sessionCreatedAt, snapshotCapturedAt) <= 0);
        Assert.True(string.CompareOrdinal(snapshotCapturedAt, sessionUpdatedAt) <= 0);

        Assert.Equal(FormatTimestamp(occurredAt), database.ScalarText("SELECT last_seen_at FROM sessions;"));
        Assert.NotEqual(writeAtText, FormatTimestamp(occurredAt));
    }

    [Theory]
    [InlineData("malformed", "duplicate_property")]
    [InlineData("missing", "body_missing")]
    [InlineData("binary", "body_unicode_invalid")]
    [InlineData("oversized", "body_oversized")]
    public void Each_fault_classification_writes_available_content_state_no_claim_and_a_null_snapshot_projection(
        string state, string reason)
    {
        using var database = new TestDatabase();
        var write = NewWrite(
            nativeSessionId: $"native-fault-{state}",
            runNativeId: $"run-fault-{state}",
            state: state,
            reason: reason);

        var outcome = Commit(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        Assert.Equal("available", database.ScalarText("SELECT content_state FROM session_events;"));
        Assert.Equal(0, database.Count("skill_projection_sdk_claims"));
        Assert.NotNull(database.ScalarText("SELECT run_id FROM session_events;"));

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT claim_id,run_id,trace_id,span_id,name,source,trigger,
                   body_sha256,body_utf8_bytes,definition_path_sha256,definition_path_utf8_bytes
            FROM skill_invocation_snapshots;
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.IsDBNull(ordinal), $"column {ordinal} expected null for state={state}");
    }

    [Fact]
    public void Nonnull_run_native_id_creates_one_run_and_a_replaying_native_id_reuses_it()
    {
        using var database = new TestDatabase();
        var first = NewWrite(nativeSessionId: "native-p4", runNativeId: "run-p4");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, first));
        Assert.Equal(1, database.Count("session_runs"));
        Assert.Equal("unknown", database.ScalarText("SELECT status FROM session_runs;"));

        var second = NewWrite(nativeSessionId: "native-p4", runNativeId: "run-p4");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, second));

        Assert.Equal(1, database.Count("session_runs"));
        Assert.Equal(2, database.Count("session_events"));
        Assert.Equal(1L, database.ScalarLong("SELECT COUNT(DISTINCT run_id) FROM session_events;"));
    }

    [Theory]
    [InlineData("explicit_resume")]
    [InlineData("explicit_handoff")]
    public void An_accepted_existing_binding_kind_is_reused_with_its_kind_and_observed_at_unchanged(string bindingKind)
    {
        using var database = new TestDatabase();
        var sessionId = Guid.CreateVersion7().ToString("D");
        var observedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SeedSession(database, sessionId, "native-p5", bindingKind, observedAt,
            lastSeenAt: new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
            createdAt: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            updatedAt: new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var write = NewWrite(nativeSessionId: "native-p5");
        var outcome = Commit(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        Assert.Equal(1, database.Count("sessions"));
        Assert.Equal(1, database.Count("session_native_ids"));
        Assert.Equal(bindingKind, database.ScalarText("SELECT binding_kind FROM session_native_ids;"));
        Assert.Equal(FormatTimestamp(observedAt), database.ScalarText("SELECT observed_at FROM session_native_ids;"));
        Assert.Equal(sessionId, database.ScalarText("SELECT session_id FROM session_events;"));
    }

    [Fact]
    public void An_unaccepted_trace_context_binding_returns_binding_invalid_and_writes_nothing()
    {
        using var database = new TestDatabase();
        var sessionId = Guid.CreateVersion7().ToString("D");
        var observedAt = DefaultWriteAt;
        SeedSession(database, sessionId, "native-p6", "trace_context", observedAt, observedAt, observedAt, observedAt);
        var baseline = database.CountAll(EightTables);

        var write = NewWrite(nativeSessionId: "native-p6");
        var outcome = RollBack(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.SessionBindingInvalid, outcome);
        Assert.Equal(baseline, database.CountAll(EightTables));
    }

    [Fact]
    public void Session_ambiguous_cannot_be_constructed_because_the_native_binding_key_is_the_table_primary_key()
    {
        using var database = new TestDatabase();
        var sessionA = Guid.CreateVersion7().ToString("D");
        var sessionB = Guid.CreateVersion7().ToString("D");
        SeedSession(database, sessionA, "native-p7", "native", DefaultWriteAt, DefaultWriteAt, DefaultWriteAt, DefaultWriteAt);
        SeedRawSession(database, sessionB, DefaultWriteAt);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
            VALUES($session_id,'copilot-sdk','native-p7','native',$observed_at);
            """;
        command.Parameters.AddWithValue("$session_id", sessionB);
        command.Parameters.AddWithValue("$observed_at", FormatTimestamp(DefaultWriteAt));

        var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(19, exception.SqliteErrorCode);
        transaction.Rollback();

        // A second row for the same (source_surface,native_session_id) is rejected by the table's
        // own primary key, so ResolveSession's SELECT ... LIMIT 2 can never observe two rows for
        // this exact key: SessionAmbiguous is a fail-closed backstop, not a reachable outcome here.
        Assert.Equal(1, database.Count("session_native_ids"));
    }

    [Fact]
    public void Run_ambiguous_returns_zero_writes_when_two_runs_share_the_same_natural_key()
    {
        using var database = new TestDatabase();
        var sessionId = Guid.CreateVersion7().ToString("D");
        SeedSession(database, sessionId, "native-p8", "native", DefaultWriteAt, DefaultWriteAt, DefaultWriteAt, DefaultWriteAt);
        SeedRun(database, sessionId, "run-p8");
        SeedRun(database, sessionId, "run-p8");
        var baseline = database.CountAll(EightTables);
        Assert.Equal(2, database.Count("session_runs"));

        var write = NewWrite(nativeSessionId: "native-p8", runNativeId: "run-p8");
        var outcome = RollBack(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.RunAmbiguous, outcome);
        Assert.Equal(baseline, database.CountAll(EightTables));
        Assert.Equal(2, database.Count("session_runs"));
    }

    [Fact]
    public void Event_conflict_returns_zero_writes_for_a_preexisting_source_key_without_a_receipt()
    {
        using var database = new TestDatabase();
        var sessionId = Guid.CreateVersion7().ToString("D");
        SeedSession(database, sessionId, "native-p9", "native", DefaultWriteAt, DefaultWriteAt, DefaultWriteAt, DefaultWriteAt);
        var conflictingSourceEventId = Guid.NewGuid().ToString("D");
        SeedConflictingEvent(database, sessionId, conflictingSourceEventId);
        var baseline = database.CountAll(EightTables);
        Assert.Equal(1, baseline["session_events"]);

        var write = NewWrite(nativeSessionId: "native-p9", sourceEventId: conflictingSourceEventId);
        var outcome = RollBack(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.EventConflict, outcome);
        Assert.Equal(baseline, database.CountAll(EightTables));
    }

    [Fact]
    public void Receipt_raced_short_circuits_before_any_read_or_write_even_for_an_unknown_native_id()
    {
        using var database = new TestDatabase();
        var raceId = Guid.NewGuid().ToString("D");
        var first = NewWrite(nativeSessionId: "native-p10a", sourceEventId: raceId);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, first));
        var baseline = database.CountAll(EightTables);
        Assert.Equal(1, baseline["sessions"]);

        var second = NewWrite(nativeSessionId: "native-p10b-unknown", sourceEventId: raceId);
        var outcome = RollBack(database, second);

        Assert.Equal(SessionSkillInvocationWriteOutcome.ReceiptRaced, outcome);
        Assert.Equal(baseline, database.CountAll(EightTables));
        Assert.Equal(1, database.Count("sessions"));
    }

    [Fact]
    public void Caller_owned_rollback_after_a_successful_insert_leaves_every_table_empty()
    {
        using var database = new TestDatabase();
        var write = NewWrite(nativeSessionId: "native-p11");

        var outcome = RollBack(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        foreach (var table in EightTables)
            Assert.Equal(0, database.Count(table));
        Assert.Equal(0, database.Count("session_runs"));
    }

    [Fact]
    public void No_clock_is_read_every_persisted_timestamp_derives_from_the_supplied_instants()
    {
        using var database = new TestDatabase();
        var occurredAt = new DateTimeOffset(1901, 1, 1, 2, 3, 4, TimeSpan.Zero).AddTicks(1234567);
        var writeAt = new DateTimeOffset(1901, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(7654321);
        var write = NewWrite(nativeSessionId: "native-p12", runNativeId: "run-p12", occurredAt: occurredAt, writeAt: writeAt);

        var outcome = Commit(database, write);

        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        var writeAtText = FormatTimestamp(writeAt);
        var occurredAtText = FormatTimestamp(occurredAt);
        var expiresAtText = FormatTimestamp(write.ExpiresAt);
        Assert.Equal(33, writeAtText.Length);

        Assert.Equal(writeAtText, database.ScalarText("SELECT created_at FROM sessions;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT updated_at FROM sessions;"));
        Assert.Equal(occurredAtText, database.ScalarText("SELECT last_seen_at FROM sessions;"));
        Assert.Equal(occurredAtText, database.ScalarText("SELECT observed_at FROM session_native_ids;"));
        Assert.Equal(occurredAtText, database.ScalarText("SELECT occurred_at FROM session_events;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT captured_at FROM session_event_content;"));
        Assert.Equal(expiresAtText, database.ScalarText("SELECT expires_at FROM session_event_content;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT captured_at FROM retention_items;"));
        Assert.Equal(expiresAtText, database.ScalarText("SELECT expires_at FROM retention_items;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT created_at FROM skill_projection_sdk_claims;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT captured_at FROM skill_invocation_snapshots;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT created_at FROM skill_invocation_snapshots;"));
        Assert.Equal(writeAtText, database.ScalarText("SELECT created_at FROM skill_invocation_snapshot_receipts;"));
    }

    [Fact]
    public void The_written_graph_passes_backup_validation_after_commit()
    {
        using var database = new TestDatabase();
        var write = NewWrite(nativeSessionId: "native-p13");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.True(SkillInvocationSnapshotBackupValidation.IsValid(connection, transaction));
        transaction.Rollback();
    }

    [Fact]
    public void The_persisted_receipt_fingerprint_equals_an_independent_recomputation_from_the_model()
    {
        using var database = new TestDatabase();
        var write = NewWrite(nativeSessionId: "native-p14");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));

        var persisted = database.ScalarText("SELECT request_fingerprint_sha256 FROM skill_invocation_snapshot_receipts;");

        var document = SkillInvocationSnapshotContentDocumentV1.Build(write.PayloadTokenUtf8.Span);
        var payloadSha256 = SkillInvocationSnapshotContentDocumentV1.PayloadSha256(write.PayloadTokenUtf8.Span);
        var documentSha256 = SkillInvocationSnapshotContentDocumentV1.ContentDocumentSha256(document);
        var expected = SkillInvocationSnapshotReceiptFingerprint.Compute(new SkillInvocationSnapshotReceiptFingerprintInput(
            SourceAdapter: write.SourceAdapter,
            SourceEventId: write.SourceEventId,
            SourceSurface: write.SourceSurface,
            NativeSessionId: write.NativeSessionId,
            RunNativeId: write.RunNativeId,
            SourceParentEventId: write.SourceParentEventId,
            SourceEphemeral: write.SourceEphemeral,
            TraceId: null,
            SpanId: null,
            OccurredAt: write.OccurredAt,
            SourceApplicationVersion: write.SourceApplicationVersion,
            AdapterVersion: write.AdapterVersion,
            NormalizationVersion: write.NormalizationVersion,
            PayloadSchema: write.PayloadSchema,
            SchemaFingerprint: write.SchemaFingerprint,
            PayloadSha256: payloadSha256,
            PayloadBytes: (ulong)write.PayloadTokenUtf8.Length,
            State: write.State,
            Reason: write.Reason,
            Name: write.Name,
            Source: write.Source,
            Trigger: write.Trigger,
            BodySha256: write.BodySha256,
            BodyUtf8Bytes: (ulong?)write.BodyUtf8Bytes,
            DefinitionPathSha256: write.DefinitionPathSha256,
            DefinitionPathUtf8Bytes: (ulong?)write.DefinitionPathUtf8Bytes,
            ContentDocumentSha256: documentSha256));

        Assert.Equal(expected, persisted);
    }

    private static SessionSkillInvocationWrite NewWrite(
        string nativeSessionId,
        string? sourceEventId = null,
        string? runNativeId = null,
        string state = "available",
        string reason = "none",
        DateTimeOffset? occurredAt = null,
        DateTimeOffset? writeAt = null)
    {
        var writeAtValue = writeAt ?? DefaultWriteAt;
        var occurredAtValue = occurredAt ?? writeAtValue;
        var isAvailable = state == "available";

        return new SessionSkillInvocationWrite(
            SourceAdapter: DefaultAdapter,
            SourceSurface: DefaultSurface,
            SourceEventId: sourceEventId ?? Guid.NewGuid().ToString("D"),
            SourceParentEventId: null,
            NativeSessionId: nativeSessionId,
            RunNativeId: runNativeId,
            SourceEphemeral: false,
            OccurredAt: occurredAtValue,
            SourceApplicationVersion: "1.0.65",
            AdapterVersion: "adapter-version-1",
            NormalizationVersion: "normalization-1",
            PayloadSchema: DefaultPayloadSchema,
            SchemaFingerprint: new string('a', 64),
            PayloadTokenUtf8: DefaultPayloadToken,
            State: state,
            Reason: reason,
            Name: isAvailable ? "demo-skill" : null,
            Source: isAvailable ? "project" : null,
            Trigger: isAvailable ? "user-invoked" : null,
            BodySha256: isAvailable ? new string('b', 64) : null,
            BodyUtf8Bytes: isAvailable ? 7L : null,
            DefinitionPathSha256: isAvailable ? new string('c', 64) : null,
            DefinitionPathUtf8Bytes: isAvailable ? 12L : null,
            EventId: Guid.CreateVersion7(),
            SnapshotId: Guid.CreateVersion7(),
            ClaimId: isAvailable ? Guid.CreateVersion7() : null,
            NewSessionId: Guid.CreateVersion7(),
            NewRunId: Guid.CreateVersion7(),
            WriteAt: writeAtValue,
            ExpiresAt: writeAtValue.AddDays(90));
    }

    private static SessionSkillInvocationWriteOutcome Commit(TestDatabase database, SessionSkillInvocationWrite write)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var outcome = SessionSkillInvocationParticipant.InsertOrVerify(connection, transaction, write);
        transaction.Commit();
        return outcome;
    }

    private static SessionSkillInvocationWriteOutcome RollBack(TestDatabase database, SessionSkillInvocationWrite write)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var outcome = SessionSkillInvocationParticipant.InsertOrVerify(connection, transaction, write);
        transaction.Rollback();
        return outcome;
    }

    private static void SeedRawSession(TestDatabase database, string sessionId, DateTimeOffset at)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        InsertSessionRow(connection, transaction, sessionId, at, at, at, at);
        transaction.Commit();
    }

    private static void SeedSession(
        TestDatabase database, string sessionId, string nativeSessionId, string bindingKind,
        DateTimeOffset observedAt, DateTimeOffset lastSeenAt, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        InsertSessionRow(connection, transaction, sessionId, lastSeenAt, createdAt, updatedAt, observedAt);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
                VALUES($session_id,'copilot-sdk',$native,$binding_kind,$observed_at);
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$native", nativeSessionId);
            command.Parameters.AddWithValue("$binding_kind", bindingKind);
            command.Parameters.AddWithValue("$observed_at", FormatTimestamp(observedAt));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void InsertSessionRow(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        DateTimeOffset lastSeenAt, DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset unusedObservedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO sessions(
                session_id,status,completeness,repository,workspace,started_at,ended_at,
                last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($session_id,'active','partial',NULL,NULL,NULL,NULL,$last_seen_at,'expiring',$created_at,$updated_at);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$last_seen_at", FormatTimestamp(lastSeenAt));
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(createdAt));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(updatedAt));
        command.ExecuteNonQuery();
    }

    private static void SeedRun(TestDatabase database, string sessionId, string nativeRunId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO session_runs(
                run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,
                started_at,ended_at,input_tokens,output_tokens,total_tokens,status)
            VALUES($run_id,$session_id,'copilot-sdk',$native_run_id,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');
            """;
        command.Parameters.AddWithValue("$run_id", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$native_run_id", nativeRunId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void SeedConflictingEvent(TestDatabase database, string sessionId, string sourceEventId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,
                source_adapter,source_event_id,type,occurred_at,content_state)
            VALUES($event_id,$session_id,NULL,'copilot-sdk',NULL,NULL,NULL,$source_adapter,$source_event_id,'skill.invoked',$occurred_at,'available');
            """;
        command.Parameters.AddWithValue("$event_id", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$source_adapter", DefaultAdapter);
        command.Parameters.AddWithValue("$source_event_id", sourceEventId);
        command.Parameters.AddWithValue("$occurred_at", FormatTimestamp(DefaultWriteAt));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private sealed class TestDatabase : IDisposable
    {
        internal TestDatabase()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"session-skill-invocation-participant-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "monitor.db");
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
                transaction.Commit();
            }
            InstallComponent();
        }

        internal string Root { get; }
        internal string Path { get; }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        internal long Count(string table)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        internal long ScalarLong(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        internal string ScalarText(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)command.ExecuteScalar()!;
        }

        internal Dictionary<string, long> CountAll(IEnumerable<string> tables)
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in tables)
                result[table] = Count(table);
            return result;
        }

        private void InstallComponent()
        {
            using (var retentionConnection = Open())
            using (var retentionTransaction = retentionConnection.BeginTransaction())
            {
                RetentionSchemaMigrator.Apply(retentionConnection, retentionTransaction);
                retentionTransaction.Commit();
            }
            new SqliteSourceCompatibilityStore(Path).CreateSchema();
            new SqliteSessionStore(Path).CreateSchema();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            SkillProjectionSchemaV1.Ensure(connection, transaction);
            LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
            LocalArchiveSchemaV1.Ensure(connection, transaction);
            SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

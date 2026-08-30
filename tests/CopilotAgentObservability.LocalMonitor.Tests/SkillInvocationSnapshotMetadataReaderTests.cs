using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class SkillInvocationSnapshotMetadataReaderTests
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";
    private const string DefaultAdapter = "copilot-sdk-stream";
    private const string DefaultSurface = "copilot-sdk";
    private const string DefaultPayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private static readonly DateTimeOffset DefaultWriteAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DefaultValidationAt = DefaultWriteAt.AddHours(1);
    private static readonly byte[] DefaultPayloadToken = "{\"skill\":\"demo\"}"u8.ToArray();

    private static readonly string[] AllTables =
    [
        "sessions", "session_native_ids", "session_runs", "session_events", "session_event_content",
        "retention_items", "retention_tombstones", "skill_projection_sdk_claims",
        "skill_invocation_snapshots", "skill_invocation_snapshot_receipts",
    ];

    // N1
    [Fact]
    public void Available_committed_write_returns_found_with_readable_and_every_fact_equals_what_was_written()
    {
        using var database = new TestDatabase();
        var occurredAt = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var writeAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var write = NewWrite(nativeSessionId: "native-n1", runNativeId: "run-n1", occurredAt: occurredAt, writeAt: writeAt);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var runId = Guid.Parse(database.ScalarText("SELECT run_id FROM skill_invocation_snapshots;"));

        AssertBothArms(database, write.NewSessionId, write.SnapshotId, DefaultWriteAt.AddDays(1), result =>
        {
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Found, result.Outcome);
            var facts = result.Facts!;
            Assert.Equal(write.SnapshotId, facts.SnapshotId);
            Assert.Equal(write.NewSessionId, facts.SessionId);
            Assert.Equal(write.EventId, facts.EventId);
            Assert.Equal(occurredAt, facts.InvokedAt);
            Assert.Equal(writeAt, facts.CapturedAt);
            Assert.NotEqual(facts.InvokedAt, facts.CapturedAt);
            Assert.Equal(write.SourceApplicationVersion, facts.SourceApplicationVersion);
            Assert.Equal(write.AdapterVersion, facts.AdapterVersion);
            Assert.Equal(write.PayloadSchema, facts.PayloadSchema);
            Assert.True(facts.IsAvailable);
            Assert.Null(facts.FaultState);
            Assert.Null(facts.FaultReason);
            Assert.Equal(write.ClaimId, facts.ClaimId);
            Assert.Equal(write.Name, facts.Name);
            Assert.Equal(write.Source, facts.Source);
            Assert.Equal(write.Trigger, facts.Trigger);
            Assert.Equal(runId, facts.RunId);
            Assert.Equal(write.BodySha256, facts.BodySha256);
            Assert.Equal((ulong)write.BodyUtf8Bytes!.Value, facts.BodyUtf8Bytes);
            Assert.Equal(write.DefinitionPathSha256, facts.DefinitionPathSha256);
            Assert.Equal((ulong)write.DefinitionPathUtf8Bytes!.Value, facts.DefinitionPathUtf8Bytes);
            Assert.Equal(SkillInvocationSnapshotMetadataRetentionProjection.Readable, facts.RetentionProjection);
        });
    }

    // N2
    [Theory]
    [InlineData("malformed", "duplicate_property")]
    [InlineData("missing", "body_missing")]
    [InlineData("binary", "body_unicode_invalid")]
    [InlineData("oversized", "body_oversized")]
    public void Each_fault_classification_returns_found_with_the_exact_persisted_state_and_reason_and_every_available_field_null(
        string state, string reason)
    {
        using var database = new TestDatabase();
        var write = NewWrite(nativeSessionId: $"native-n2-{state}", state: state, reason: reason);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));

        AssertBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt, result =>
        {
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Found, result.Outcome);
            var facts = result.Facts!;
            Assert.False(facts.IsAvailable);
            Assert.Equal(state, facts.FaultState);
            Assert.Equal(reason, facts.FaultReason);
            Assert.Null(facts.ClaimId);
            Assert.Null(facts.Name);
            Assert.Null(facts.Source);
            Assert.Null(facts.Trigger);
            Assert.Null(facts.RunId);
            Assert.Null(facts.BodySha256);
            Assert.Null(facts.BodyUtf8Bytes);
            Assert.Null(facts.DefinitionPathSha256);
            Assert.Null(facts.DefinitionPathUtf8Bytes);
            Assert.Equal(SkillInvocationSnapshotMetadataRetentionProjection.Readable, facts.RetentionProjection);
        });
    }

    // N3
    [Fact]
    public void Unknown_snapshot_id_returns_not_found()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n3-unknown");

        AssertBothArms(database, write.NewSessionId, Guid.CreateVersion7(), DefaultValidationAt, result =>
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.NotFound, result.Outcome));
    }

    [Fact]
    public void Known_snapshot_id_queried_under_a_different_session_returns_not_found_not_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n3-owner");
        var other = InsertAvailableAndCommit(database, "native-n3-stranger");

        AssertBothArms(database, other.NewSessionId, write.SnapshotId, DefaultValidationAt, result =>
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.NotFound, result.Outcome));
    }

    // N4
    [Fact]
    public void Deleted_graph_returns_found_with_unreadable_or_deleted_and_every_safe_stored_fact_survives()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n4-deleted");
        DeleteContentAndInsertTombstone(database, write, DefaultWriteAt.AddDays(200));

        AssertBothArms(database, write.NewSessionId, write.SnapshotId, DefaultWriteAt.AddDays(300), result =>
        {
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Found, result.Outcome);
            var facts = result.Facts!;
            Assert.Equal(SkillInvocationSnapshotMetadataRetentionProjection.UnreadableOrDeleted, facts.RetentionProjection);
            Assert.True(facts.IsAvailable);
            Assert.Equal(write.ClaimId, facts.ClaimId);
            Assert.Equal(write.Name, facts.Name);
            Assert.Equal(write.BodySha256, facts.BodySha256);
            Assert.Equal((ulong)write.BodyUtf8Bytes!.Value, facts.BodyUtf8Bytes);
            Assert.Equal(write.DefinitionPathSha256, facts.DefinitionPathSha256);
            Assert.Equal((ulong)write.DefinitionPathUtf8Bytes!.Value, facts.DefinitionPathUtf8Bytes);
        });
    }

    // N5
    [Fact]
    public void Read_denied_while_the_content_row_survives_returns_found_with_unreadable_or_deleted()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n5-read-denied");
        DriveIntoExpiredPendingDeletionWithReadDenied(database, write, DefaultWriteAt.AddDays(200));

        AssertBothArms(database, write.NewSessionId, write.SnapshotId, DefaultWriteAt.AddDays(201), result =>
        {
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Found, result.Outcome);
            Assert.Equal(SkillInvocationSnapshotMetadataRetentionProjection.UnreadableOrDeleted, result.Facts!.RetentionProjection);
        });
        Assert.Equal(1, database.Count("session_event_content"));
    }

    [Fact]
    public void At_or_after_the_expiring_boundary_returns_found_with_unreadable_or_deleted()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n5-boundary");

        AssertBothArms(database, write.NewSessionId, write.SnapshotId, write.ExpiresAt, result =>
        {
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Found, result.Outcome);
            Assert.Equal(SkillInvocationSnapshotMetadataRetentionProjection.UnreadableOrDeleted, result.Facts!.RetentionProjection);
        });
    }

    // N6
    [Fact]
    public void Content_row_absent_while_the_item_is_not_deleted_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n6-absent-not-deleted");
        Execute(database, "DELETE FROM session_event_content WHERE event_id=$event;", ("$event", write.EventId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Fact]
    public void Deleted_item_with_surviving_mismatched_content_metadata_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-deleted-surviving-content");
        var at = DefaultValidationAt;
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection, transaction,
                "UPDATE session_event_content SET content_kind='text/plain' WHERE event_id=$event;",
                ("$event", write.EventId.ToString("D")));
            Execute(connection, transaction,
                "UPDATE retention_items SET state='deleted',read_denied_at=$at,deleted_at=$at,revision=revision+1 WHERE source_item_id=$event;",
                ("$at", FormatTimestamp(at)), ("$event", write.EventId.ToString("D")));
            Execute(connection, transaction,
                "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) SELECT item_id,$at,$at FROM retention_items WHERE source_item_id=$event;",
                ("$at", FormatTimestamp(at)), ("$event", write.EventId.ToString("D")));
            transaction.Commit();
        }

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, at);
    }

    [Fact]
    public void Deleted_item_missing_its_tombstone_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n6-no-tombstone");
        DeleteContentAndInsertTombstone(database, write, DefaultWriteAt.AddDays(200));
        RemoveTombstone(database, write);

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultWriteAt.AddDays(300));
    }

    [Fact]
    public void Deleted_item_with_content_restored_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n6-restored");
        DeleteContentAndInsertTombstone(database, write, DefaultWriteAt.AddDays(200));
        RestoreContentRow(database, write);

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultWriteAt.AddDays(300));
    }

    [Fact]
    public void Receipt_deleted_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n6-receipt");
        DropTrigger(database, "skill_invocation_snapshot_receipts_delete_rejected");
        Execute(database, "DELETE FROM skill_invocation_snapshot_receipts WHERE snapshot_id=$snapshot;",
            ("$snapshot", write.SnapshotId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Theory]
    [InlineData("source_adapter")]
    [InlineData("source_event_id")]
    [InlineData("request_fingerprint_sha256")]
    public void Receipt_identity_or_fingerprint_contradiction_is_unavailable(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-receipt-{column}");
        DropTrigger(database, "skill_invocation_snapshot_receipts_update_rejected");
        var value = column switch
        {
            "source_event_id" => Guid.NewGuid().ToString("D"),
            "request_fingerprint_sha256" => new string('f', 64),
            _ => "contradictory-adapter",
        };
        Execute(database,
            $"PRAGMA ignore_check_constraints=ON; UPDATE skill_invocation_snapshot_receipts SET {column}=$value WHERE snapshot_id=$snapshot;",
            ("$value", value), ("$snapshot", write.SnapshotId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Fact]
    public void Event_content_state_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n6-content-state");
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        Execute(database, "UPDATE session_events SET content_state='not_captured' WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Fact]
    public void Event_type_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n6-type");
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        Execute(database, "UPDATE session_events SET type='skill.something_else' WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Theory]
    [InlineData("source_application_version")]
    [InlineData("adapter_version")]
    [InlineData("normalization_version")]
    [InlineData("schema_fingerprint")]
    public void Event_producer_identity_contradiction_is_unavailable(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-event-identity-{column}");
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        var value = column == "schema_fingerprint" ? new string('f', 64) : "contradiction";
        Execute(database, $"UPDATE session_events SET {column}=$value WHERE event_id=$event;",
            ("$value", value),
            ("$event", write.EventId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Theory]
    [InlineData("source_application_version")]
    [InlineData("adapter_version")]
    [InlineData("normalization_version")]
    [InlineData("payload_schema")]
    [InlineData("schema_fingerprint")]
    public void Claim_producer_identity_contradiction_is_unavailable(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-claim-identity-{column}");
        DropTrigger(database, "skill_projection_sdk_claims_update_rejected");
        var value = column == "schema_fingerprint" ? new string('f', 64) : "contradiction";
        Execute(database, $"UPDATE skill_projection_sdk_claims SET {column}=$value WHERE claim_id=$claim;",
            ("$value", value),
            ("$claim", write.ClaimId!.Value.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Theory]
    [InlineData("created_at")]
    [InlineData("invocation_trigger")]
    [InlineData("payload_sha256")]
    public void Claim_remaining_immutable_field_contradiction_is_unavailable(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-claim-complete-{column}");
        DropTrigger(database, "skill_projection_sdk_claims_update_rejected");
        var value = column switch
        {
            "created_at" => FormatTimestamp(write.WriteAt.AddSeconds(1)),
            "payload_sha256" => new string('f', 64),
            _ => "contradictory-trigger",
        };
        Execute(database, $"UPDATE skill_projection_sdk_claims SET {column}=$value WHERE claim_id=$claim;",
            ("$value", value), ("$claim", write.ClaimId!.Value.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Event_and_snapshot_optional_run_mismatch_is_unavailable(bool eventHasRun)
    {
        using var database = new TestDatabase();
        var write = NewWrite($"native-run-link-{eventHasRun}", runNativeId: eventHasRun ? null : "native-run");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        ExecuteWithForeignKeysOff(database,
            "UPDATE session_events SET run_id=$run WHERE event_id=$event;",
            ("$run", eventHasRun ? Guid.CreateVersion7().ToString("D") : null),
            ("$event", write.EventId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Theory]
    [InlineData("source_application_version")]
    [InlineData("adapter_version")]
    [InlineData("normalization_version")]
    [InlineData("schema_fingerprint")]
    public void Snapshot_producer_identity_contradiction_is_unavailable(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-snapshot-identity-{column}");
        DropTrigger(database, "skill_invocation_snapshot_rows_update_rejected");
        var value = column == "schema_fingerprint" ? new string('f', 64) : "contradiction";
        Execute(database, $"UPDATE skill_invocation_snapshots SET {column}=$value WHERE snapshot_id=$snapshot;",
            ("$value", value), ("$snapshot", write.SnapshotId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Fact]
    public void Claim_row_deleted_while_claim_id_is_nonnull_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n6-claim");
        DropTrigger(database, "skill_projection_sdk_claims_delete_rejected");
        ExecuteWithForeignKeysOff(database, "DELETE FROM skill_projection_sdk_claims WHERE claim_id=$claim;",
            ("$claim", write.ClaimId!.Value.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    [Fact]
    public void Retention_item_source_item_id_pointed_at_another_event_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n6-source-item");
        Execute(database,
            """
            UPDATE retention_items SET source_item_id=$other
            WHERE item_id=(SELECT content_item_id FROM skill_invocation_snapshots WHERE event_id=$event);
            """,
            ("$other", Guid.CreateVersion7().ToString("D")), ("$event", write.EventId.ToString("D")));

        AssertUnavailableBothArms(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt);
    }

    // N7
    [Fact]
    public void The_reader_never_opens_the_document_a_corrupt_content_json_leaves_the_outcome_unchanged()
    {
        using var database = new TestDatabase();
        var occurredAt = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var writeAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var write = NewWrite(nativeSessionId: "native-n7", occurredAt: occurredAt, writeAt: writeAt);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        Execute(database, "UPDATE session_event_content SET content_json='not a canonical document' WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));

        AssertBothArms(database, write.NewSessionId, write.SnapshotId, DefaultWriteAt.AddDays(1), result =>
        {
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Found, result.Outcome);
            var facts = result.Facts!;
            Assert.Equal(SkillInvocationSnapshotMetadataRetentionProjection.Readable, facts.RetentionProjection);
            Assert.True(facts.IsAvailable);
            Assert.Equal(write.ClaimId, facts.ClaimId);
            Assert.Equal(write.BodySha256, facts.BodySha256);
            Assert.Equal(occurredAt, facts.InvokedAt);
            Assert.Equal(writeAt, facts.CapturedAt);
        });
    }

    // N8
    [Fact]
    public void Zero_writes_for_found_not_found_and_unavailable_outcomes_on_both_arms()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-n8-found");
        AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt, SkillInvocationSnapshotMetadataOutcome.Found);
        AssertZeroWrites(database, write.NewSessionId, Guid.CreateVersion7(), DefaultValidationAt, SkillInvocationSnapshotMetadataOutcome.NotFound);

        var broken = InsertAvailableAndCommit(database, "native-n8-broken");
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        Execute(database, "UPDATE session_events SET type='skill.something_else' WHERE event_id=$event;",
            ("$event", broken.EventId.ToString("D")));
        AssertZeroWrites(database, broken.NewSessionId, broken.SnapshotId, DefaultValidationAt, SkillInvocationSnapshotMetadataOutcome.Unavailable);
    }

    // N9
    [Fact]
    public void No_clock_value_leaks_into_the_returned_facts_and_the_clock_is_sampled_exactly_once()
    {
        using var database = new TestDatabase();
        var occurredAt = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var writeAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var write = NewWrite(nativeSessionId: "native-n9", occurredAt: occurredAt, writeAt: writeAt);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var injectedInstant = writeAt.AddMinutes(37);
        Assert.NotEqual(injectedInstant, occurredAt);
        Assert.NotEqual(injectedInstant, writeAt);

        var ownedProvider = new CountingTimeProvider(injectedInstant);
        var ownedResult = SkillInvocationSnapshotMetadataReader.ReadOwnedTransaction(database.Path, write.NewSessionId, write.SnapshotId, ownedProvider);
        Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Found, ownedResult.Outcome);
        Assert.Equal(1, ownedProvider.CallCount);
        Assert.NotEqual(injectedInstant, ownedResult.Facts!.InvokedAt);
        Assert.NotEqual(injectedInstant, ownedResult.Facts!.CapturedAt);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var inTxProvider = new CountingTimeProvider(injectedInstant);
        var inTxResult = SkillInvocationSnapshotMetadataReader.ReadInTransaction(connection, transaction, write.NewSessionId, write.SnapshotId, inTxProvider);
        Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Found, inTxResult.Outcome);
        Assert.Equal(1, inTxProvider.CallCount);
        Assert.NotEqual(injectedInstant, inTxResult.Facts!.InvokedAt);
        Assert.NotEqual(injectedInstant, inTxResult.Facts!.CapturedAt);
        transaction.Rollback();
    }

    private static void AssertBothArms(
        TestDatabase database, Guid sessionId, Guid snapshotId, DateTimeOffset at,
        Action<SkillInvocationSnapshotMetadataReadResult> assertion)
    {
        assertion(SkillInvocationSnapshotMetadataReader.ReadOwnedTransaction(database.Path, sessionId, snapshotId, new FixedTimeProvider(at)));

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        assertion(SkillInvocationSnapshotMetadataReader.ReadInTransaction(connection, transaction, sessionId, snapshotId, new FixedTimeProvider(at)));
        transaction.Rollback();
    }

    private static void AssertUnavailableBothArms(TestDatabase database, Guid sessionId, Guid snapshotId, DateTimeOffset at) =>
        AssertBothArms(database, sessionId, snapshotId, at, result =>
            Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Unavailable, result.Outcome));

    private static void AssertZeroWrites(
        TestDatabase database, Guid sessionId, Guid snapshotId, DateTimeOffset at, SkillInvocationSnapshotMetadataOutcome expected)
    {
        string beforeOwned;
        using (var probe = database.Open())
            beforeOwned = DumpAllRows(probe, null);
        var ownedResult = SkillInvocationSnapshotMetadataReader.ReadOwnedTransaction(database.Path, sessionId, snapshotId, new FixedTimeProvider(at));
        Assert.Equal(expected, ownedResult.Outcome);
        string afterOwned;
        using (var probe = database.Open())
            afterOwned = DumpAllRows(probe, null);
        Assert.Equal(beforeOwned, afterOwned);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var beforeInTx = DumpAllRows(connection, transaction);
        var inTxResult = SkillInvocationSnapshotMetadataReader.ReadInTransaction(connection, transaction, sessionId, snapshotId, new FixedTimeProvider(at));
        Assert.Equal(expected, inTxResult.Outcome);
        var afterInTx = DumpAllRows(connection, transaction);
        Assert.Equal(beforeInTx, afterInTx);
        transaction.Rollback();
    }

    private const string FieldSeparator = "<F>";
    private const string RowSeparator = "<R>";
    private const string TableSeparator = "<T>";

    private static string DumpAllRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var builder = new StringBuilder();
        foreach (var table in AllTables)
        {
            builder.Append(table).Append(TableSeparator);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    builder.Append(reader.IsDBNull(i) ? "<null>" : FormatValue(reader.GetValue(i)));
                    builder.Append(FieldSeparator);
                }
                builder.Append(RowSeparator);
            }
        }
        return builder.ToString();
    }

    private static string FormatValue(object value) => value switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static void DeleteContentAndInsertTombstone(TestDatabase database, SessionSkillInvocationWrite write, DateTimeOffset at)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM session_event_content WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));
        Execute(connection, transaction,
            """
            UPDATE retention_items
            SET state='deleted', read_denied_at=COALESCE(read_denied_at,$at), deleted_at=$at, revision=revision+1
            WHERE item_id=(SELECT content_item_id FROM skill_invocation_snapshots WHERE event_id=$event);
            """,
            ("$at", FormatTimestamp(at)), ("$event", write.EventId.ToString("D")));
        Execute(connection, transaction,
            """
            INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
            SELECT content_item_id,$at,$at FROM skill_invocation_snapshots WHERE event_id=$event;
            """,
            ("$at", FormatTimestamp(at)), ("$event", write.EventId.ToString("D")));
        transaction.Commit();
    }

    private static void RemoveTombstone(TestDatabase database, SessionSkillInvocationWrite write) =>
        Execute(database,
            """
            DELETE FROM retention_tombstones
            WHERE item_id=(SELECT content_item_id FROM skill_invocation_snapshots WHERE event_id=$event);
            """,
            ("$event", write.EventId.ToString("D")));

    private static void RestoreContentRow(TestDatabase database, SessionSkillInvocationWrite write)
    {
        var document = SkillInvocationSnapshotContentDocumentV1.Build(write.PayloadTokenUtf8.Span);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction,
            """
            INSERT INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
            VALUES($event,$content_kind,$content_json,$captured_at,$expires_at,$token);
            """,
            ("$event", write.EventId.ToString("D")),
            ("$content_kind", SkillInvocationSnapshotContentDocumentV1.ContentKind),
            ("$content_json", Encoding.UTF8.GetString(document)),
            ("$captured_at", FormatTimestamp(write.WriteAt)),
            ("$expires_at", FormatTimestamp(write.ExpiresAt)),
            ("$token", RandomNumberGenerator.GetBytes(32)));
        transaction.Commit();
    }

    private static void DriveIntoExpiredPendingDeletionWithReadDenied(
        TestDatabase database, SessionSkillInvocationWrite write, DateTimeOffset at) =>
        Execute(database,
            """
            UPDATE retention_items
            SET state='expired_pending_deletion', read_denied_at=$at, revision=revision+1
            WHERE item_id=(SELECT content_item_id FROM skill_invocation_snapshots WHERE event_id=$event);
            """,
            ("$at", FormatTimestamp(at)), ("$event", write.EventId.ToString("D")));

    private static void DropTrigger(TestDatabase database, string triggerName) =>
        Execute(database, $"DROP TRIGGER {triggerName};");

    private static void Execute(TestDatabase database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, sql, parameters);
        transaction.Commit();
    }

    private static void Execute(
        SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // The claim FK is ON DELETE RESTRICT, and the append-only trigger alone would not allow this
    // setup delete either: proving "claim deleted" requires a connection with FK enforcement off,
    // never the reader's own connection, which always runs with foreign keys on.
    private static void ExecuteWithForeignKeysOff(TestDatabase database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=OFF;";
            pragma.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, sql, parameters);
        transaction.Commit();
    }

    private static SessionSkillInvocationWrite InsertAvailableAndCommit(TestDatabase database, string nativeSessionId)
    {
        var write = NewWrite(nativeSessionId);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        return write;
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

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class CountingTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public int CallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            CallCount++;
            return instant;
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        internal TestDatabase()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"skill-invocation-snapshot-metadata-reader-{Guid.NewGuid():N}");
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

        internal string ScalarText(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)command.ExecuteScalar()!;
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

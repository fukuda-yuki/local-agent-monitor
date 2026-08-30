using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class SkillInvocationSnapshotReplayValidatorTests
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";
    private const string DefaultAdapter = "copilot-sdk-stream";
    private const string DefaultSurface = "copilot-sdk";
    private const string DefaultPayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private static readonly DateTimeOffset DefaultWriteAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DefaultValidationAt = DefaultWriteAt.AddHours(1);
    private static readonly byte[] DefaultPayloadToken = "{\"skill\":\"demo\"}"u8.ToArray();

    private static readonly string[] ZeroWriteTables =
    [
        "sessions", "session_native_ids", "session_runs", "session_events", "session_event_content",
        "retention_items", "retention_tombstones", "skill_projection_sdk_claims",
        "skill_invocation_snapshots", "skill_invocation_snapshot_receipts",
    ];

    // R1
    [Fact]
    public void Committed_available_write_replayed_with_same_fingerprint_is_equal_replay_on_both_arms()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r1");
        var request = RequestFor(database, write);

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.EqualReplay);
    }

    // R2
    [Fact]
    public void Different_fingerprint_is_conflict_on_both_arms_with_zero_clock_calls()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r2");
        var request = RequestFor(database, write) with { RequestFingerprintSha256 = Flip(ReadFingerprint(database, write)) };

        var ownedProvider = new CountingTimeProvider(DefaultValidationAt);
        Assert.Equal(
            SkillInvocationSnapshotReplayOutcome.DifferentFingerprint,
            SkillInvocationSnapshotReplayValidator.ValidateOwnedTransaction(database.Path, request, ownedProvider));
        Assert.Equal(0, ownedProvider.CallCount);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var inTxProvider = new CountingTimeProvider(DefaultValidationAt);
        Assert.Equal(
            SkillInvocationSnapshotReplayOutcome.DifferentFingerprint,
            SkillInvocationSnapshotReplayValidator.ValidateInTransaction(connection, transaction, request, inTxProvider));
        Assert.Equal(0, inTxProvider.CallCount);
        transaction.Rollback();
    }

    // R3
    [Fact]
    public void Unknown_source_key_is_receipt_missing_on_both_arms_with_zero_clock_calls()
    {
        using var database = new TestDatabase();
        var request = new SkillInvocationSnapshotReplayRequest(DefaultAdapter, Guid.NewGuid().ToString("D"), new string('a', 64));

        var ownedProvider = new CountingTimeProvider(DefaultValidationAt);
        Assert.Equal(
            SkillInvocationSnapshotReplayOutcome.ReceiptMissing,
            SkillInvocationSnapshotReplayValidator.ValidateOwnedTransaction(database.Path, request, ownedProvider));
        Assert.Equal(0, ownedProvider.CallCount);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var inTxProvider = new CountingTimeProvider(DefaultValidationAt);
        Assert.Equal(
            SkillInvocationSnapshotReplayOutcome.ReceiptMissing,
            SkillInvocationSnapshotReplayValidator.ValidateInTransaction(connection, transaction, request, inTxProvider));
        Assert.Equal(0, inTxProvider.CallCount);
        transaction.Rollback();
    }

    // R4
    [Theory]
    [InlineData("malformed", "duplicate_property")]
    [InlineData("missing", "body_missing")]
    [InlineData("binary", "body_unicode_invalid")]
    [InlineData("oversized", "body_oversized")]
    public void Each_fault_classification_replays_as_equal_replay_on_both_arms(string state, string reason)
    {
        using var database = new TestDatabase();
        var write = NewWrite(nativeSessionId: $"native-r4-{state}", state: state, reason: reason);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var request = RequestFor(database, write);

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.EqualReplay);
    }

    [Fact]
    public void Fault_snapshot_preserves_and_validates_the_event_outer_run_for_equal_replay()
    {
        using var database = new TestDatabase();
        var write = NewWrite("native-fault-outer-run", runNativeId: "outer-run", state: "missing", reason: "body_missing");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var request = RequestFor(database, write);

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.EqualReplay);
    }

    // R5
    [Fact]
    public void Live_readable_content_json_tampered_inside_base64_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r5-json");
        var request = RequestFor(database, write);
        MutateContentJsonOneBase64Character(database, write);

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Fact]
    public void Live_readable_payload_sha256_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r5-payload-sha");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_rows_update_rejected");
        Execute(database, "UPDATE skill_invocation_snapshots SET payload_sha256=$value WHERE event_id=$event;",
            ("$value", new string('f', 64)), ("$event", write.EventId.ToString("D")));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Fact]
    public void Live_readable_content_document_sha256_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r5-document-sha");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_rows_update_rejected");
        Execute(database, "UPDATE skill_invocation_snapshots SET content_document_sha256=$value WHERE event_id=$event;",
            ("$value", new string('e', 64)), ("$event", write.EventId.ToString("D")));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Fact]
    public void Live_readable_payload_bytes_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r5-payload-bytes");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_rows_update_rejected");
        Execute(database, "UPDATE skill_invocation_snapshots SET payload_bytes=payload_bytes+1 WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Fact]
    public void Live_readable_content_kind_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r5-content-kind");
        var request = RequestFor(database, write);
        Execute(database, "UPDATE session_event_content SET content_kind='text/plain' WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    // R6
    [Fact]
    public void Deleted_graph_with_tombstone_and_absent_content_is_equal_replay_on_both_arms()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r6-deleted");
        var request = RequestFor(database, write);
        DeleteContentAndInsertTombstone(database, write, DefaultWriteAt.AddDays(200));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.EqualReplay);
    }

    [Fact]
    public void Deleted_item_without_its_tombstone_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r6-no-tombstone");
        var request = RequestFor(database, write);
        DeleteContentAndInsertTombstone(database, write, DefaultWriteAt.AddDays(200));
        RemoveTombstone(database, write);

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Fact]
    public void Deleted_item_with_content_restored_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r6-restored-content");
        var request = RequestFor(database, write);
        DeleteContentAndInsertTombstone(database, write, DefaultWriteAt.AddDays(200));
        RestoreContentRow(database, write);

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    // R7
    [Fact]
    public void Owner_valid_nonreadable_raw_retained_is_unavailable_without_selecting_the_document()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r7");
        var request = RequestFor(database, write);
        DriveIntoExpiredPendingDeletionWithReadDenied(database, write, DefaultWriteAt.AddDays(200));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);

        Execute(database, "UPDATE session_event_content SET content_json='not a canonical document' WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("readable", "EqualReplay", 1)]
    [InlineData("nonreadable", "Unavailable", 0)]
    [InlineData("deleted", "EqualReplay", 0)]
    public void Equal_replay_materializes_content_json_only_after_retention_authorization(
        string lifecycle, string expectedName, int expectedContentReads)
    {
        var expected = Enum.Parse<SkillInvocationSnapshotReplayOutcome>(expectedName);
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-raw-read-order-{lifecycle}");
        var request = RequestFor(database, write);
        if (lifecycle == "nonreadable")
            DriveIntoExpiredPendingDeletionWithReadDenied(database, write, DefaultWriteAt.AddDays(200));
        else if (lifecycle == "deleted")
            DeleteContentAndInsertTombstone(database, write, DefaultWriteAt.AddDays(200));

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var contentReads = 0;
        strdelegate_authorizer authorizer = (_, action, table, column, _, _) =>
        {
            if (action == raw.SQLITE_READ
                && string.Equals(table, "session_event_content", StringComparison.Ordinal)
                && string.Equals(column, "content_json", StringComparison.Ordinal))
                contentReads++;
            return raw.SQLITE_OK;
        };
        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_set_authorizer(connection.Handle, authorizer, null));
        try
        {
            Assert.Equal(expected, SkillInvocationSnapshotReplayValidator.ValidateInTransaction(
                connection, transaction, request, new FixedTimeProvider(DefaultValidationAt)));
        }
        finally
        {
            raw.sqlite3_set_authorizer(connection.Handle, (strdelegate_authorizer?)null, null);
            transaction.Rollback();
        }
        Assert.Equal(expectedContentReads, contentReads);
    }

    // R8
    [Fact]
    public void Binding_kind_changed_to_trace_context_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r8-binding");
        var request = RequestFor(database, write);
        Execute(database,
            "UPDATE session_native_ids SET binding_kind='trace_context' WHERE source_surface='copilot-sdk' AND native_session_id=$native;",
            ("$native", write.NativeSessionId));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        var different = request with { RequestFingerprintSha256 = Flip(request.RequestFingerprintSha256) };
        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, different));
        AssertZeroWritesOwned(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.False(SkillInvocationSnapshotBackupValidation.IsValid(connection, transaction));
        transaction.Rollback();
    }

    [Fact]
    public void Native_binding_pointed_at_a_different_session_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r8-session");
        var request = RequestFor(database, write);
        var otherSessionId = Guid.CreateVersion7().ToString("D");
        InsertBareSession(database, otherSessionId, write.WriteAt);
        Execute(database,
            "UPDATE session_native_ids SET session_id=$other WHERE source_surface='copilot-sdk' AND native_session_id=$native;",
            ("$other", otherSessionId), ("$native", write.NativeSessionId));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Fact]
    public void Claim_created_at_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r8-claim");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_projection_sdk_claims_update_rejected");
        Execute(database, "UPDATE skill_projection_sdk_claims SET created_at=$value WHERE claim_id=$claim;",
            ("$value", FormatTimestamp(write.WriteAt.AddSeconds(1))), ("$claim", write.ClaimId!.Value.ToString("D")));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        var different = request with { RequestFingerprintSha256 = Flip(request.RequestFingerprintSha256) };
        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, different));
        AssertZeroWritesOwned(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("receipt")]
    [InlineData("retention")]
    public void Write_timestamp_contradiction_precedes_different_fingerprint(string owner)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-write-time-{owner}");
        var request = RequestFor(database, write) with { RequestFingerprintSha256 = new string('f', 64) };
        if (owner != "retention")
            DropTrigger(database, owner == "snapshot"
                ? "skill_invocation_snapshot_rows_update_rejected"
                : "skill_invocation_snapshot_receipts_update_rejected");
        var sql = owner switch
        {
            "snapshot" => "UPDATE skill_invocation_snapshots SET created_at=$value WHERE snapshot_id=$snapshot;",
            "receipt" => "UPDATE skill_invocation_snapshot_receipts SET created_at=$value WHERE snapshot_id=$snapshot;",
            _ => "UPDATE retention_items SET captured_at=$value WHERE item_id=(SELECT content_item_id FROM skill_invocation_snapshots WHERE snapshot_id=$snapshot);",
        };
        Execute(database, sql,
            ("$value", FormatTimestamp(write.WriteAt.AddSeconds(1))), ("$snapshot", write.SnapshotId.ToString("D")));

        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, request));
        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("session_created")]
    [InlineData("session_updated")]
    [InlineData("event_trace")]
    [InlineData("content_kind")]
    [InlineData("content_captured")]
    [InlineData("content_expires")]
    public void Surviving_graph_metadata_contradiction_precedes_equal_and_different_replay_and_invalidates_backup(string mutation)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-graph-metadata-{mutation}");
        var request = RequestFor(database, write);
        if (mutation.StartsWith("event_", StringComparison.Ordinal))
            DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        var sql = mutation switch
        {
            "session_created" => "UPDATE sessions SET created_at=$late WHERE session_id=$session;",
            "session_updated" => "UPDATE sessions SET updated_at=$early WHERE session_id=$session;",
            "event_trace" => "UPDATE session_events SET trace_id=$trace WHERE event_id=$event;",
            "content_kind" => "UPDATE session_event_content SET content_kind='text/plain' WHERE event_id=$event;",
            "content_captured" => "UPDATE session_event_content SET captured_at=$late WHERE event_id=$event;",
            _ => "UPDATE session_event_content SET expires_at=$late WHERE event_id=$event;",
        };
        Execute(database, sql,
            ("$late", FormatTimestamp(write.WriteAt.AddDays(2))),
            ("$early", FormatTimestamp(write.WriteAt.AddDays(-2))),
            ("$trace", new string('d', 32)), ("$span", new string('e', 16)),
            ("$session", write.NewSessionId.ToString("D")), ("$event", write.EventId.ToString("D")));

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        var different = request with { RequestFingerprintSha256 = Flip(request.RequestFingerprintSha256) };
        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, different));
        AssertZeroWritesOwned(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
        Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Unavailable,
            SkillInvocationSnapshotMetadataReader.ReadOwnedTransaction(
                database.Path, write.NewSessionId, write.SnapshotId, new FixedTimeProvider(DefaultValidationAt)).Outcome);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.False(SkillInvocationSnapshotBackupValidation.IsValid(connection, transaction));
        transaction.Rollback();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Event_and_snapshot_optional_run_mismatch_precedes_equal_and_different_replay(bool eventHasRun)
    {
        using var database = new TestDatabase();
        var write = NewWrite($"native-run-link-{eventHasRun}", runNativeId: eventHasRun ? null : "native-run");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        ExecuteWithForeignKeysOff(database, "UPDATE session_events SET run_id=$run WHERE event_id=$event;",
            ("$run", eventHasRun ? Guid.CreateVersion7().ToString("D") : null), ("$event", write.EventId.ToString("D")));

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        var different = request with { RequestFingerprintSha256 = Flip(request.RequestFingerprintSha256) };
        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, different));
        AssertZeroWritesOwned(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.False(SkillInvocationSnapshotBackupValidation.IsValid(connection, transaction));
        transaction.Rollback();
    }

    [Theory]
    [InlineData("wrong_surface", false)]
    [InlineData("null_native", false)]
    [InlineData("changed_native", false)]
    [InlineData("duplicate_natural", false)]
    [InlineData("wrong_surface", true)]
    [InlineData("null_native", true)]
    [InlineData("changed_native", true)]
    [InlineData("duplicate_natural", true)]
    public void Event_outer_run_natural_identity_contradiction_is_unavailable_and_backup_invalid(string mutation, bool fault)
    {
        using var database = new TestDatabase();
        var write = NewWrite($"native-run-natural-{mutation}-{fault}", runNativeId: "outer-run",
            state: fault ? "missing" : "available", reason: fault ? "body_missing" : "none");
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));
        var request = RequestFor(database, write);
        var sql = mutation switch
        {
            "wrong_surface" => "UPDATE session_runs SET source_surface='copilot-cli' WHERE run_id=$run;",
            "null_native" => "UPDATE session_runs SET native_run_id=NULL WHERE run_id=$run;",
            "changed_native" => "UPDATE session_runs SET native_run_id='changed-run' WHERE run_id=$run;",
            _ => "INSERT INTO session_runs(run_id,session_id,source_surface,native_run_id,status) SELECT $other,session_id,source_surface,native_run_id,'unknown' FROM session_runs WHERE run_id=$run;",
        };
        Execute(database, sql, ("$run", write.NewRunId.ToString("D")), ("$other", Guid.CreateVersion7().ToString("D")));

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        Assert.Equal(SkillInvocationSnapshotMetadataOutcome.Unavailable,
            SkillInvocationSnapshotMetadataReader.ReadOwnedTransaction(
                database.Path, write.NewSessionId, write.SnapshotId, new FixedTimeProvider(DefaultValidationAt)).Outcome);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.False(SkillInvocationSnapshotBackupValidation.IsValid(connection, transaction));
        transaction.Rollback();
    }

    [Fact]
    public void Event_content_state_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r8-content-state");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        Execute(database, "UPDATE session_events SET content_state='not_captured' WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Fact]
    public void Event_type_changed_is_unavailable()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r8-type");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        Execute(database, "UPDATE session_events SET type='skill.something_else' WHERE event_id=$event;",
            ("$event", write.EventId.ToString("D")));

        AssertBothArmsOutcome(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("source_application_version")]
    [InlineData("adapter_version")]
    [InlineData("normalization_version")]
    [InlineData("schema_fingerprint")]
    public void Event_producer_identity_contradiction_refuses_equal_replay_without_writes(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-event-identity-{column}");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_session_event_update_rejected");
        var value = column == "schema_fingerprint" ? new string('f', 64) : "contradiction";
        Execute(database, $"UPDATE session_events SET {column}=$value WHERE event_id=$event;",
            ("$value", value),
            ("$event", write.EventId.ToString("D")));

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        var different = request with { RequestFingerprintSha256 = Flip(request.RequestFingerprintSha256) };
        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, different));
        AssertZeroWritesOwned(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("source_application_version")]
    [InlineData("adapter_version")]
    [InlineData("normalization_version")]
    [InlineData("payload_schema")]
    [InlineData("schema_fingerprint")]
    public void Claim_producer_identity_contradiction_refuses_equal_replay_without_writes(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-claim-identity-{column}");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_projection_sdk_claims_update_rejected");
        var value = column == "schema_fingerprint" ? new string('f', 64) : "contradiction";
        Execute(database, $"UPDATE skill_projection_sdk_claims SET {column}=$value WHERE claim_id=$claim;",
            ("$value", value),
            ("$claim", write.ClaimId!.Value.ToString("D")));

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        var different = request with { RequestFingerprintSha256 = Flip(request.RequestFingerprintSha256) };
        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, different));
        AssertZeroWritesOwned(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, different, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("source_application_version")]
    [InlineData("adapter_version")]
    [InlineData("normalization_version")]
    [InlineData("schema_fingerprint")]
    public void Snapshot_producer_identity_contradiction_refuses_equal_replay_without_writes(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-snapshot-identity-{column}");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_rows_update_rejected");
        var value = column == "schema_fingerprint" ? new string('f', 64) : "contradiction";
        Execute(database, $"UPDATE skill_invocation_snapshots SET {column}=$value WHERE snapshot_id=$snapshot;",
            ("$value", value), ("$snapshot", write.SnapshotId.ToString("D")));

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("source_application_version")]
    [InlineData("adapter_version")]
    [InlineData("normalization_version")]
    public void Backup_validation_rejects_each_claim_producer_identity_contradiction(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-backup-claim-{column}");
        DropTrigger(database, "skill_projection_sdk_claims_update_rejected");
        Execute(database, $"UPDATE skill_projection_sdk_claims SET {column}='contradiction' WHERE claim_id=$claim;",
            ("$claim", write.ClaimId!.Value.ToString("D")));

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.False(SkillInvocationSnapshotBackupValidation.IsValid(connection, transaction));
        transaction.Rollback();
    }

    // R9
    [Fact]
    public void Zero_writes_on_equal_replay_for_owned_and_in_transaction_arms()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r9-equal");
        var request = RequestFor(database, write);

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.EqualReplay);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.EqualReplay);
    }

    [Fact]
    public void Zero_writes_on_different_fingerprint_for_owned_and_in_transaction_arms()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r9-different");
        var request = RequestFor(database, write) with { RequestFingerprintSha256 = Flip(ReadFingerprint(database, write)) };

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.DifferentFingerprint);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.DifferentFingerprint);
    }

    [Fact]
    public void Corrupt_stored_fingerprint_is_unavailable_for_original_and_mutated_requests_without_writes()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-corrupt-receipt-fingerprint");
        var original = RequestFor(database, write);
        var mutatedFingerprint = new string('f', 64);
        DropTrigger(database, "skill_invocation_snapshot_receipts_update_rejected");
        Execute(database,
            "UPDATE skill_invocation_snapshot_receipts SET request_fingerprint_sha256=$value WHERE snapshot_id=$snapshot;",
            ("$value", mutatedFingerprint), ("$snapshot", write.SnapshotId.ToString("D")));

        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, original));
        AssertZeroWritesOwned(database, original, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, original, SkillInvocationSnapshotReplayOutcome.Unavailable);

        var mutated = original with { RequestFingerprintSha256 = mutatedFingerprint };
        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, mutated));
        AssertZeroWritesOwned(database, mutated, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, mutated, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("source_adapter")]
    [InlineData("source_event_id")]
    public void Linked_receipt_natural_key_drift_is_unavailable_not_missing_without_writes(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-receipt-key-drift-{column}");
        var request = RequestFor(database, write);
        DropTrigger(database, "skill_invocation_snapshot_receipts_update_rejected");
        var value = column == "source_event_id" ? Guid.NewGuid().ToString("D") : "contradictory-adapter";
        Execute(database,
            $"PRAGMA ignore_check_constraints=ON; UPDATE skill_invocation_snapshot_receipts SET {column}=$value WHERE snapshot_id=$snapshot;",
            ("$value", value), ("$snapshot", write.SnapshotId.ToString("D")));

        Assert.Equal(SkillInvocationSnapshotReceiptProbeOutcome.Unavailable,
            SkillInvocationSnapshotReplayValidator.ProbeReceipt(database.Path, request));
        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    [Theory]
    [InlineData("request_fingerprint_sha256")]
    [InlineData("source_event_id")]
    public void Backup_validation_rejects_receipt_semantic_contradiction(string column)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-backup-receipt-{column}");
        DropTrigger(database, "skill_invocation_snapshot_receipts_update_rejected");
        var value = column == "request_fingerprint_sha256" ? new string('f', 64) : Guid.NewGuid().ToString("D");
        Execute(database, $"UPDATE skill_invocation_snapshot_receipts SET {column}=$value WHERE snapshot_id=$snapshot;",
            ("$value", value), ("$snapshot", write.SnapshotId.ToString("D")));

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.False(SkillInvocationSnapshotBackupValidation.IsValid(connection, transaction));
        transaction.Rollback();
    }

    [Fact]
    public void Zero_writes_on_unavailable_for_owned_and_in_transaction_arms()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r9-unavailable");
        var request = RequestFor(database, write);
        Execute(database,
            "UPDATE session_native_ids SET binding_kind='trace_context' WHERE source_surface='copilot-sdk' AND native_session_id=$native;",
            ("$native", write.NativeSessionId));

        AssertZeroWritesOwned(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
        AssertZeroWritesInTransaction(database, request, SkillInvocationSnapshotReplayOutcome.Unavailable);
    }

    // R10
    [Fact]
    public void ValidateInTransaction_opens_no_nested_transaction_so_the_caller_can_commit_afterward()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r10");
        var request = RequestFor(database, write);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var outcome = SkillInvocationSnapshotReplayValidator.ValidateInTransaction(
            connection, transaction, request, new FixedTimeProvider(DefaultValidationAt));

        Assert.Equal(SkillInvocationSnapshotReplayOutcome.EqualReplay, outcome);
        transaction.Commit();
    }

    // R11
    [Fact]
    public void Exactly_one_clock_sample_on_equal_replay_for_both_arms()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-r11");
        var request = RequestFor(database, write);

        var ownedProvider = new CountingTimeProvider(DefaultValidationAt);
        Assert.Equal(
            SkillInvocationSnapshotReplayOutcome.EqualReplay,
            SkillInvocationSnapshotReplayValidator.ValidateOwnedTransaction(database.Path, request, ownedProvider));
        Assert.Equal(1, ownedProvider.CallCount);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var inTxProvider = new CountingTimeProvider(DefaultValidationAt);
        Assert.Equal(
            SkillInvocationSnapshotReplayOutcome.EqualReplay,
            SkillInvocationSnapshotReplayValidator.ValidateInTransaction(connection, transaction, request, inTxProvider));
        Assert.Equal(1, inTxProvider.CallCount);
        transaction.Rollback();
    }

    private static void AssertBothArmsOutcome(
        TestDatabase database, SkillInvocationSnapshotReplayRequest request, SkillInvocationSnapshotReplayOutcome expected)
    {
        var ownedOutcome = SkillInvocationSnapshotReplayValidator.ValidateOwnedTransaction(
            database.Path, request, new FixedTimeProvider(DefaultValidationAt));
        Assert.Equal(expected, ownedOutcome);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var inTxOutcome = SkillInvocationSnapshotReplayValidator.ValidateInTransaction(
            connection, transaction, request, new FixedTimeProvider(DefaultValidationAt));
        Assert.Equal(expected, inTxOutcome);
        transaction.Rollback();
    }

    private static void AssertZeroWritesOwned(
        TestDatabase database, SkillInvocationSnapshotReplayRequest request, SkillInvocationSnapshotReplayOutcome expected)
    {
        string before;
        using (var probe = database.Open())
            before = DumpAllRows(probe, null);

        var outcome = SkillInvocationSnapshotReplayValidator.ValidateOwnedTransaction(
            database.Path, request, new FixedTimeProvider(DefaultValidationAt));
        Assert.Equal(expected, outcome);

        string after;
        using (var probe = database.Open())
            after = DumpAllRows(probe, null);
        Assert.Equal(before, after);
    }

    private static void AssertZeroWritesInTransaction(
        TestDatabase database, SkillInvocationSnapshotReplayRequest request, SkillInvocationSnapshotReplayOutcome expected)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var before = DumpAllRows(connection, transaction);

        var outcome = SkillInvocationSnapshotReplayValidator.ValidateInTransaction(
            connection, transaction, request, new FixedTimeProvider(DefaultValidationAt));
        Assert.Equal(expected, outcome);

        var after = DumpAllRows(connection, transaction);
        Assert.Equal(before, after);
        transaction.Rollback();
    }

    private const string FieldSeparator = "<F>";
    private const string RowSeparator = "<R>";
    private const string TableSeparator = "<T>";

    private static string DumpAllRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var builder = new StringBuilder();
        foreach (var table in ZeroWriteTables)
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

    private static SkillInvocationSnapshotReplayRequest RequestFor(TestDatabase database, SessionSkillInvocationWrite write) =>
        new(write.SourceAdapter, write.SourceEventId, ReadFingerprint(database, write));

    private static string ReadFingerprint(TestDatabase database, SessionSkillInvocationWrite write)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT request_fingerprint_sha256 FROM skill_invocation_snapshot_receipts WHERE source_adapter=$adapter AND source_event_id=$event;";
        command.Parameters.AddWithValue("$adapter", write.SourceAdapter);
        command.Parameters.AddWithValue("$event", write.SourceEventId);
        return (string)command.ExecuteScalar()!;
    }

    private static string Flip(string hex) => (hex[0] == '0' ? '1' : '0') + hex[1..];

    private static void MutateContentJsonOneBase64Character(TestDatabase database, SessionSkillInvocationWrite write)
    {
        using var connection = database.Open();
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT content_json FROM session_event_content WHERE event_id=$event;";
        readCommand.Parameters.AddWithValue("$event", write.EventId.ToString("D"));
        var original = (string)readCommand.ExecuteScalar()!;

        const string marker = "\"payload_utf8_base64\":\"";
        const string base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var start = original.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var chars = original.ToCharArray();
        var current = chars[start];
        var position = base64Alphabet.IndexOf(current);
        chars[start] = base64Alphabet[(position + 1) % base64Alphabet.Length];
        var mutated = new string(chars);

        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "UPDATE session_event_content SET content_json=$value WHERE event_id=$event;",
            ("$value", mutated), ("$event", write.EventId.ToString("D")));
        transaction.Commit();
    }

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

    private static void InsertBareSession(TestDatabase database, string sessionId, DateTimeOffset at) =>
        Execute(database,
            """
            INSERT INTO sessions(
                session_id,status,completeness,repository,workspace,started_at,ended_at,
                last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($session_id,'active','partial',NULL,NULL,NULL,NULL,$at,'expiring',$at,$at);
            """,
            ("$session_id", sessionId), ("$at", FormatTimestamp(at)));

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

    private static void ExecuteWithForeignKeysOff(
        TestDatabase database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
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
        DateTimeOffset? writeAt = null)
    {
        var writeAtValue = writeAt ?? DefaultWriteAt;
        var isAvailable = state == "available";

        return new SessionSkillInvocationWrite(
            SourceAdapter: DefaultAdapter,
            SourceSurface: DefaultSurface,
            SourceEventId: sourceEventId ?? Guid.NewGuid().ToString("D"),
            SourceParentEventId: null,
            NativeSessionId: nativeSessionId,
            RunNativeId: runNativeId,
            SourceEphemeral: false,
            OccurredAt: writeAtValue,
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
        var outcome = SessionSkillInvocationParticipant.InsertOrVerify(
            connection,
            transaction,
            write,
            new LocalWorkspaceProjectionTransactionParticipant(new StructuralRegistryAuthority()),
            out _);
        transaction.Commit();
        return outcome;
    }

    private sealed class StructuralRegistryAuthority : ISkillRegistryGenerationAuthority
    {
        public ISkillRegistryGenerationCapture? CaptureGeneration() => null;
        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            lease = null;
            return false;
        }
        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease) => false;
        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple) => false;
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
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"skill-invocation-snapshot-replay-validator-{Guid.NewGuid():N}");
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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationSnapshotContentReaderTests
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";
    private const string DefaultAdapter = "copilot-sdk-stream";
    private const string DefaultSurface = "copilot-sdk";
    private const string DefaultPayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private const string DefaultBody = "demo-body";
    private const string DefaultDefinitionPath = "skills/demo.md";
    private static readonly DateTimeOffset DefaultWriteAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DefaultValidationAt = DefaultWriteAt.AddHours(1);
    private static readonly byte[] DefaultPayloadToken = Encoding.UTF8.GetBytes(
        """{"name":"demo-skill","path":"skills/demo.md","content":"demo-body","source":"project","trigger":"user-invoked"}""");
    private static readonly byte[] DefaultBodyUtf8 = Encoding.UTF8.GetBytes(DefaultBody);
    private static readonly byte[] DefaultDefinitionPathUtf8 = Encoding.UTF8.GetBytes(DefaultDefinitionPath);

    private static readonly string[] AllTables =
    [
        "sessions", "session_native_ids", "session_runs", "session_events", "session_event_content",
        "retention_items", "retention_leases", "retention_tombstones", "skill_projection_sdk_claims",
        "skill_invocation_snapshots", "skill_invocation_snapshot_receipts",
    ];

    [Fact]
    public async Task Available_content_under_a_live_grant_returns_granted_with_every_fact_and_seals_raw()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c1");

        var result = await ReadAt(database, write, DefaultValidationAt);

        Assert.True(result.IsGranted);
        var facts = result.Facts!;
        Assert.Equal(write.SnapshotId, facts.SnapshotId);
        Assert.Equal(DefaultBody, facts.Body);
        Assert.Equal(DefaultDefinitionPath, facts.DefinitionPath);
        Assert.Equal(Sha256Hex(DefaultBodyUtf8), facts.BodySha256);
        Assert.Equal(Sha256Hex(DefaultDefinitionPathUtf8), facts.DefinitionPathSha256);
        Assert.Equal(DefaultBodyUtf8.LongLength, facts.BodyUtf8Bytes);
        Assert.Equal(DefaultDefinitionPathUtf8.LongLength, facts.DefinitionPathUtf8Bytes);
        Assert.Equal(write.WriteAt, facts.CapturedAt);

        var lease = result.Lease!;
        Assert.Equal(SkillInvocationSnapshotContentTerminalResult.Sealed, lease.TrySealRawResponse());
        Assert.Equal(SkillInvocationSnapshotContentTerminalResult.Lost, lease.TrySealRawResponse());
        await lease.DisposeAsync();
        Assert.Equal(0, database.Count("retention_leases"));
    }

    [Fact]
    public async Task Complete_without_raw_before_any_seal_completes_and_every_later_terminal_is_lost()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c2");

        var result = await ReadAt(database, write, DefaultValidationAt);
        Assert.True(result.IsGranted);
        var lease = result.Lease!;

        Assert.Equal(SkillInvocationSnapshotContentTerminalResult.CompletedWithoutRaw, lease.TryCompleteWithoutRaw());
        Assert.Equal(SkillInvocationSnapshotContentTerminalResult.Lost, lease.TrySealRawResponse());
        Assert.Equal(SkillInvocationSnapshotContentTerminalResult.Lost, lease.TryCompleteWithoutRaw());
        await lease.DisposeAsync();
        Assert.Equal(0, database.Count("retention_leases"));
    }

    [Fact]
    public async Task Unknown_snapshot_and_foreign_session_owner_return_not_found_with_zero_writes()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c3");
        var stranger = InsertAvailableAndCommit(database, "native-c3-stranger");

        await AssertZeroWrites(database, write.NewSessionId, Guid.CreateVersion7(), DefaultValidationAt,
            SkillInvocationSnapshotContentOutcome.NotFound);
        await AssertZeroWrites(database, stranger.NewSessionId, write.SnapshotId, DefaultValidationAt,
            SkillInvocationSnapshotContentOutcome.NotFound);
    }

    [Fact]
    public async Task One_tick_before_expiry_is_granted_and_the_boundary_and_after_are_expired_with_zero_writes()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c4");
        var expiresAt = write.ExpiresAt;

        var before = await ReadAt(database, write, expiresAt.AddTicks(-1));
        Assert.True(before.IsGranted);
        await before.Lease!.DisposeAsync();

        await AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, expiresAt,
            SkillInvocationSnapshotContentOutcome.Expired);
        await AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, expiresAt.AddTicks(1),
            SkillInvocationSnapshotContentOutcome.Expired);
    }

    [Fact]
    public async Task Deleted_content_with_tombstone_is_expired_with_zero_writes()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c5");
        DeleteContentAndInsertTombstone(database, write, DefaultValidationAt);

        await AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt,
            SkillInvocationSnapshotContentOutcome.Expired);
    }

    [Fact]
    public async Task Expired_pending_deletion_with_read_denied_is_expired_with_zero_writes()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c6");
        DriveIntoExpiredPendingDeletionWithReadDenied(database, write, DefaultValidationAt);

        await AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt,
            SkillInvocationSnapshotContentOutcome.Expired);
    }

    [Theory]
    [InlineData("malformed", "duplicate_property")]
    [InlineData("missing", "body_missing")]
    [InlineData("binary", "body_unicode_invalid")]
    [InlineData("oversized", "body_oversized")]
    public async Task Each_fault_classification_is_content_unavailable_with_zero_writes(string state, string reason)
    {
        using var database = new TestDatabase();
        var write = NewWrite($"native-c7-{state}", state: state, reason: reason);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, Commit(database, write));

        await AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt,
            SkillInvocationSnapshotContentOutcome.ContentUnavailable);
    }

    [Fact]
    public async Task Claim_row_deleted_while_claim_id_is_nonnull_is_unavailable_with_zero_writes()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c8");
        DropTrigger(database, "skill_projection_sdk_claims_delete_rejected");
        ExecuteWithForeignKeysOff(database, "DELETE FROM skill_projection_sdk_claims WHERE claim_id=$claim;",
            ("$claim", write.ClaimId!.Value.ToString("D")));

        await AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt,
            SkillInvocationSnapshotContentOutcome.Unavailable);
    }

    [Theory]
    [InlineData("content_json")]
    [InlineData("payload_sha256")]
    [InlineData("content_document_sha256")]
    [InlineData("snapshot_body_sha256")]
    public async Task Every_post_grant_proof_failure_is_unavailable_releases_the_lease_and_stays_unavailable(string tamper)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-c9-{tamper}");
        ApplyTamper(database, write, tamper);

        await AssertPostGrantFailure(database, write, SkillInvocationSnapshotContentOutcome.Unavailable);
        await AssertPostGrantFailure(database, write, SkillInvocationSnapshotContentOutcome.Unavailable);
    }

    [Theory]
    [InlineData("content_kind")]
    [InlineData("content_captured_at")]
    [InlineData("content_expires_at")]
    public async Task Content_metadata_tamper_is_unavailable_before_retention_admission_with_zero_writes(string tamper)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-c9m-{tamper}");
        ApplyTamper(database, write, tamper);

        await AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt,
            SkillInvocationSnapshotContentOutcome.Unavailable);
        await AssertZeroWrites(database, write.NewSessionId, write.SnapshotId, DefaultValidationAt,
            SkillInvocationSnapshotContentOutcome.Unavailable);
        Assert.Equal(0, database.Count("retention_leases"));
        AssertNoReadDenial(database, write);
    }

    [Fact]
    public async Task Retention_owner_token_tamper_is_denied_at_admission_and_expires()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c9m-retention-owner-token");
        ApplyTamper(database, write, "retention_owner_token");

        var first = await ReadAt(database, write, DefaultValidationAt);
        Assert.Equal(SkillInvocationSnapshotContentOutcome.Expired, first.Outcome);
        Assert.Null(first.Lease);
        Assert.Null(first.Facts);
        AssertReadDenialRecorded(database, write);
        var afterFirst = DumpAllRows(database);

        var second = await ReadAt(database, write, DefaultValidationAt);
        Assert.Equal(SkillInvocationSnapshotContentOutcome.Expired, second.Outcome);
        Assert.Null(second.Lease);
        Assert.Null(second.Facts);
        Assert.Equal(0, database.Count("retention_leases"));
        Assert.Equal(afterFirst, DumpAllRows(database));
    }

    [Fact]
    public async Task Admission_sqlite_busy_is_busy_with_zero_writes()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c10");
        var before = DumpAllRows(database);
        // Clock-sample order: one sample inside the metadata read, one for the read request, then the
        // admission sample inside the immediate transaction.
        var time = new ArmedTimeProvider(DefaultValidationAt, 3, static () => throw new SqliteException("busy", 5));
        var store = new RetentionCatalogStore(database.Path, time);

        var result = await SkillInvocationSnapshotContentReader.ReadAsync(
            database.Path, store, time, write.NewSessionId, write.SnapshotId, CancellationToken.None);

        Assert.Equal(SkillInvocationSnapshotContentOutcome.Busy, result.Outcome);
        Assert.Null(result.Lease);
        Assert.Null(result.Facts);
        Assert.Equal(before, DumpAllRows(database));
    }

    [Fact]
    public async Task Post_grant_sqlite_busy_returns_busy_completes_without_raw_and_leaves_zero_leases()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c11");
        var time = new FixedTimeProvider(DefaultValidationAt);
        var store = new RetentionCatalogStore(database.Path, time,
            new ThrowingBoundaryCheckpoint(RetentionReadBoundaryCheckpoint.BeforeConsumptionTransaction, new SqliteException("busy", 5)));

        var result = await SkillInvocationSnapshotContentReader.ReadAsync(
            database.Path, store, time, write.NewSessionId, write.SnapshotId, CancellationToken.None);

        Assert.Equal(SkillInvocationSnapshotContentOutcome.Busy, result.Outcome);
        Assert.Null(result.Lease);
        Assert.Null(result.Facts);
        Assert.Equal(0, database.Count("retention_leases"));
    }

    [Theory]
    [InlineData("AfterValueSelector")]
    [InlineData("AfterValuePublicationProof")]
    [InlineData("BeforeValuePublicationCommit")]
    [InlineData("AfterValueOwnerAttachedBeforeClaimRelease")]
    public async Task Publication_loss_at_every_fence_is_aborted_with_zero_leases(string checkpointName)
    {
        var checkpoint = Enum.Parse<RetentionReadBoundaryCheckpoint>(checkpointName);
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-c12-{checkpoint}");
        using var cancellation = new CancellationTokenSource();
        var time = new FixedTimeProvider(DefaultValidationAt);
        var store = new RetentionCatalogStore(database.Path, time,
            new CancellingBoundaryCheckpoint(checkpoint, cancellation));

        var result = await SkillInvocationSnapshotContentReader.ReadAsync(
            database.Path, store, time, write.NewSessionId, write.SnapshotId, cancellation.Token);

        Assert.Equal(SkillInvocationSnapshotContentOutcome.Aborted, result.Outcome);
        Assert.Null(result.Lease);
        Assert.Null(result.Facts);
        Assert.Equal(0, database.Count("retention_leases"));
    }

    // Current-file takes one fixed two-minute operation grant and deliberately makes no renewal
    // call at the general one-minute renewal deadline. The deadline is crossed on a clock that
    // never advances by itself, so the three ticks are the exact boundary rather than a sample of
    // it, and every renewal observable is read at that tick.
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Current_file_makes_no_renewal_call_at_the_one_minute_renewal_deadline(int deadlineTickOffset)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-c14-{deadlineTickOffset}");
        var clock = new GenericRouteContentClock(DefaultValidationAt);
        var gate = new SkillCurrentFileHistoricalGateV1(
            database.Path, new RetentionCatalogStore(database.Path, clock), clock);

        var admission = await gate.AdmitAsync(write.NewSessionId, write.SnapshotId, CancellationToken.None);

        Assert.Equal(SkillInvocationSnapshotContentOutcome.Granted, admission.Outcome);
        await using var grant = admission.Grant!;
        var admittedLease = LeaseRow(database);
        Assert.Equal("operation", database.ScalarText("SELECT lease_kind FROM retention_leases;"));
        Assert.Equal(
            FormatTimestamp(DefaultValidationAt + RetentionV1Constants.LeaseDuration),
            database.ScalarText("SELECT expires_at FROM retention_leases;"));
        Assert.Equal(1, clock.LeaseExpiryArmCount);
        Assert.Equal(1, clock.TimerArmCount);

        clock.UtcNow = DefaultValidationAt
            + RetentionV1Constants.LeaseRenewalDeadline
            + TimeSpan.FromTicks(deadlineTickOffset);

        Assert.Equal(1, database.Count("retention_leases"));
        Assert.Equal(admittedLease, LeaseRow(database));
        Assert.Equal(1, clock.LeaseExpiryArmCount);
        Assert.Equal(1, clock.TimerArmCount);

        // The request keeps running on the original grant: its terminal proof still succeeds and
        // stays one-shot, so no replacement grant was substituted underneath it.
        Assert.Equal(SkillInvocationSnapshotContentTerminalResult.Sealed, grant.TrySealRawResponse());
        Assert.Equal(SkillInvocationSnapshotContentTerminalResult.Lost, grant.TrySealRawResponse());
        Assert.Equal(SkillInvocationSnapshotContentTerminalResult.Lost, grant.TryCompleteWithoutRaw());
    }

    // The unrenewed grant's authority still ends at the originally published two-minute expiry. A
    // renewal at the one-minute deadline would have pushed this boundary out, so the exact-expiry
    // tick is what makes the absence of renewal observable rather than merely asserted.
    [Theory]
    [InlineData(-1, (int)SkillInvocationSnapshotContentTerminalResult.Sealed)]
    [InlineData(0, (int)SkillInvocationSnapshotContentTerminalResult.Lost)]
    [InlineData(1, (int)SkillInvocationSnapshotContentTerminalResult.Lost)]
    public async Task The_unrenewed_operation_grant_is_still_governed_by_its_original_expiry(
        int expiryTickOffset,
        int expected)
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, $"native-c15-{expiryTickOffset}");
        var clock = new GenericRouteContentClock(DefaultValidationAt);
        var gate = new SkillCurrentFileHistoricalGateV1(
            database.Path, new RetentionCatalogStore(database.Path, clock), clock);

        var admission = await gate.AdmitAsync(write.NewSessionId, write.SnapshotId, CancellationToken.None);

        Assert.Equal(SkillInvocationSnapshotContentOutcome.Granted, admission.Outcome);
        await using var grant = admission.Grant!;

        clock.UtcNow = DefaultValidationAt + RetentionV1Constants.LeaseRenewalDeadline;
        Assert.Equal(1, clock.LeaseExpiryArmCount);

        clock.UtcNow = DefaultValidationAt
            + RetentionV1Constants.LeaseDuration
            + TimeSpan.FromTicks(expiryTickOffset);

        Assert.Equal((SkillInvocationSnapshotContentTerminalResult)expected, grant.TrySealRawResponse());
        Assert.Equal(1, clock.LeaseExpiryArmCount);
    }

    [Fact]
    public async Task Cancellation_before_the_consumption_transaction_propagates_and_leaves_zero_leases()
    {
        using var database = new TestDatabase();
        var write = InsertAvailableAndCommit(database, "native-c13");
        using var cancellation = new CancellationTokenSource();
        var time = new FixedTimeProvider(DefaultValidationAt);
        var store = new RetentionCatalogStore(database.Path, time,
            new CancellingBoundaryCheckpoint(RetentionReadBoundaryCheckpoint.BeforeConsumptionTransaction, cancellation));

        await Assert.ThrowsAsync<OperationCanceledException>(() => SkillInvocationSnapshotContentReader.ReadAsync(
            database.Path, store, time, write.NewSessionId, write.SnapshotId, cancellation.Token));
        Assert.Equal(0, database.Count("retention_leases"));
    }

    private static Task<SkillInvocationSnapshotContentReadResult> ReadAt(
        TestDatabase database, SessionSkillInvocationWrite write, DateTimeOffset at) =>
        SkillInvocationSnapshotContentReader.ReadAsync(
            database.Path,
            new RetentionCatalogStore(database.Path, new FixedTimeProvider(at)),
            new FixedTimeProvider(at),
            write.NewSessionId,
            write.SnapshotId,
            CancellationToken.None);

    private static async Task AssertZeroWrites(
        TestDatabase database,
        Guid sessionId,
        Guid snapshotId,
        DateTimeOffset at,
        SkillInvocationSnapshotContentOutcome expected)
    {
        var time = new FixedTimeProvider(at);
        var store = new RetentionCatalogStore(database.Path, time);
        var before = DumpAllRows(database);

        var result = await SkillInvocationSnapshotContentReader.ReadAsync(
            database.Path, store, time, sessionId, snapshotId, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Lease);
        Assert.Null(result.Facts);
        Assert.Equal(before, DumpAllRows(database));
    }

    // Post-grant failures legitimately consume the admitted lease: the store retains it and the
    // reader completes it without raw, so only the lease count is stable enough to assert here.
    private static async Task AssertPostGrantFailure(
        TestDatabase database, SessionSkillInvocationWrite write, SkillInvocationSnapshotContentOutcome expected)
    {
        var time = new FixedTimeProvider(DefaultValidationAt);
        var store = new RetentionCatalogStore(database.Path, time);

        var result = await SkillInvocationSnapshotContentReader.ReadAsync(
            database.Path, store, time, write.NewSessionId, write.SnapshotId, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Lease);
        Assert.Null(result.Facts);
        Assert.Equal(0, database.Count("retention_leases"));
    }

    private static void AssertReadDenialRecorded(TestDatabase database, SessionSkillInvocationWrite write)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT state, read_denied_at IS NOT NULL, error_code, revision
            FROM retention_items
            WHERE item_id=(SELECT content_item_id FROM skill_invocation_snapshots WHERE event_id=$event);
            """;
        command.Parameters.AddWithValue("$event", write.EventId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("deletion_failed", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal("retention_ownership_mismatch", reader.GetString(2));
        Assert.Equal(2, reader.GetInt64(3));
    }

    private static void AssertNoReadDenial(TestDatabase database, SessionSkillInvocationWrite write)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT read_denied_at,error_code FROM retention_items WHERE source_item_id=$event;";
        command.Parameters.AddWithValue("$event", write.EventId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    private static void ApplyTamper(TestDatabase database, SessionSkillInvocationWrite write, string tamper)
    {
        var eventId = write.EventId.ToString("D");
        switch (tamper)
        {
            case "content_json":
                Execute(database, "UPDATE session_event_content SET content_json='not the canonical document' WHERE event_id=$event;",
                    ("$event", eventId));
                break;
            case "content_kind":
                Execute(database, "UPDATE session_event_content SET content_kind='application/octet-stream' WHERE event_id=$event;",
                    ("$event", eventId));
                break;
            case "content_captured_at":
                Execute(database, "UPDATE session_event_content SET captured_at=$at WHERE event_id=$event;",
                    ("$at", FormatTimestamp(write.WriteAt.AddSeconds(1))), ("$event", eventId));
                break;
            case "content_expires_at":
                Execute(database, "UPDATE session_event_content SET expires_at=$at WHERE event_id=$event;",
                    ("$at", FormatTimestamp(write.ExpiresAt.AddSeconds(1))), ("$event", eventId));
                break;
            case "retention_owner_token":
                DropTrigger(database, "retention_session_event_content_token_immutable");
                Execute(database, "UPDATE session_event_content SET retention_owner_token=$token WHERE event_id=$event;",
                    ("$token", RandomNumberGenerator.GetBytes(32)), ("$event", eventId));
                break;
            case "payload_sha256":
                ExecuteSnapshotColumn(database, write, "payload_sha256", new string('0', 64));
                break;
            case "content_document_sha256":
                ExecuteSnapshotColumn(database, write, "content_document_sha256", new string('0', 64));
                break;
            case "snapshot_body_sha256":
                ExecuteSnapshotColumn(database, write, "body_sha256", new string('0', 64));
                break;
            default:
                throw new ArgumentException($"Unknown tamper case: {tamper}", nameof(tamper));
        }
    }

    private static void ExecuteSnapshotColumn(TestDatabase database, SessionSkillInvocationWrite write, string column, string value)
    {
        DropTrigger(database, "skill_invocation_snapshot_rows_update_rejected");
        Execute(database, $"UPDATE skill_invocation_snapshots SET {column}=$value WHERE event_id=$event;",
            ("$value", value), ("$event", write.EventId.ToString("D")));
    }

    private const string FieldSeparator = "<F>";
    private const string RowSeparator = "<R>";
    private const string TableSeparator = "<T>";

    private static string DumpAllRows(TestDatabase database)
    {
        using var connection = database.Open();
        var builder = new StringBuilder();
        foreach (var table in AllTables)
        {
            builder.Append(table).Append(TableSeparator);
            using var command = connection.CreateCommand();
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

    // The claim FK is ON DELETE RESTRICT, so proving "claim deleted" requires a connection with
    // FK enforcement off, never the reader's own connection, which always runs with foreign keys on.
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
        string state = "available",
        string reason = "none",
        DateTimeOffset? writeAt = null)
    {
        var writeAtValue = writeAt ?? DefaultWriteAt;
        var isAvailable = state == "available";

        return new SessionSkillInvocationWrite(
            SourceAdapter: DefaultAdapter,
            SourceSurface: DefaultSurface,
            SourceEventId: Guid.NewGuid().ToString("D"),
            SourceParentEventId: null,
            NativeSessionId: nativeSessionId,
            RunNativeId: null,
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
            BodySha256: isAvailable ? Sha256Hex(DefaultBodyUtf8) : null,
            BodyUtf8Bytes: isAvailable ? DefaultBodyUtf8.LongLength : null,
            DefinitionPathSha256: isAvailable ? Sha256Hex(DefaultDefinitionPathUtf8) : null,
            DefinitionPathUtf8Bytes: isAvailable ? DefaultDefinitionPathUtf8.LongLength : null,
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

    private static string Sha256Hex(byte[] utf8) =>
        Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static string LeaseRow(TestDatabase database) =>
        database.ScalarText(
            "SELECT item_id||'|'||lease_kind||'|'||owner||'|'||generation||'|'||expires_at FROM retention_leases;");

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class ArmedTimeProvider(DateTimeOffset instant, int armOrdinal, Action armed) : TimeProvider
    {
        private int calls;

        public override DateTimeOffset GetUtcNow()
        {
            calls++;
            if (calls == armOrdinal)
                armed();
            return instant;
        }
    }

    private sealed class ThrowingBoundaryCheckpoint(RetentionReadBoundaryCheckpoint target, SqliteException exception) : IRetentionReadBoundaryCheckpoint
    {
        public void Reached(RetentionReadBoundaryCheckpoint checkpoint)
        {
            if (checkpoint == target)
                throw exception;
        }
    }

    private sealed class CancellingBoundaryCheckpoint(RetentionReadBoundaryCheckpoint target, CancellationTokenSource source) : IRetentionReadBoundaryCheckpoint
    {
        public void Reached(RetentionReadBoundaryCheckpoint checkpoint)
        {
            if (checkpoint == target)
                source.Cancel();
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        internal TestDatabase()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"skill-invocation-snapshot-content-reader-{Guid.NewGuid():N}");
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
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
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
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                SkillProjectionSchemaV1.Ensure(connection, transaction);
                LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
                LocalArchiveSchemaV1.Ensure(connection, transaction);
                SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }
            new RetentionCatalogStore(Path).CreateSchema();
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

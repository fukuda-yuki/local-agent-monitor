using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationPreviewApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatePreview_PersistsTypedDigestConflictSnapshotAndExactlyFiveMinuteExpiry()
    {
        using var fixture = Fixture.Create();
        fixture.InsertAllFourActiveConflicts();

        var result = fixture.Application.CreatePreview(fixture.Request(), fixture.WorkflowKey(1));

        var preview = Assert.IsType<RetentionMutationPreviewResponse>(result.Preview);
        Assert.Null(result.ErrorCode);
        Assert.StartsWith("rpv1_", preview.PreviewId, StringComparison.Ordinal);
        Assert.StartsWith("sha256-", preview.PreviewDigest, StringComparison.Ordinal);
        Assert.Equal(Now.Add(RetentionMutationConstants.ConfirmationLifetime), preview.ConfirmationExpiresAt);
        Assert.Equal(
            RetentionMutationConflictCodes.All,
            preview.ActiveCleanupExclusionConflicts.Select(static item => item.ConflictCode));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_previews;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='preview';"));

        var storedSnapshot = fixture.ScalarText(
            "SELECT active_conflict_snapshot FROM retention_mutation_previews WHERE preview_id=$preview;",
            ("$preview", preview.PreviewId));
        Assert.Contains("active_read_lease", storedSnapshot, StringComparison.Ordinal);
        Assert.Contains("active_operation_lease", storedSnapshot, StringComparison.Ordinal);
        Assert.Contains("active_deletion_lease", storedSnapshot, StringComparison.Ordinal);
        Assert.Contains("active_delete_intent", storedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("rt90v1_", storedSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePreview_RepeatedSameCatalogMaterializationHasByteIdenticalDigest()
    {
        using var fixture = Fixture.Create();

        var first = Assert.IsType<RetentionMutationPreviewResponse>(
            fixture.Application.CreatePreview(fixture.Request(), fixture.WorkflowKey(1)).Preview);
        var second = Assert.IsType<RetentionMutationPreviewResponse>(
            fixture.Application.CreatePreview(fixture.Request(), fixture.WorkflowKey(2)).Preview);

        Assert.Equal(first.PreviewDigest, second.PreviewDigest);
        Assert.Equal(first.ExpectedStateVersion, second.ExpectedStateVersion);
        Assert.Equal(first.TargetItemSetDigest, second.TargetItemSetDigest);
        Assert.Equal(first.ActiveCleanupExclusionConflicts, second.ActiveCleanupExclusionConflicts);
        Assert.Equal(2L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_previews;"));
    }

    [Fact]
    public void CreatePreview_DigestChangesWhenRejectionCodeOrPinnedFieldChanges()
    {
        using var fixture = Fixture.Create();
        var actionable = Assert.IsType<RetentionMutationPreviewResponse>(
            fixture.Application.CreatePreview(fixture.Request(), fixture.WorkflowKey(10)).Preview);

        fixture.SetState("deleting");
        var rejected = Assert.IsType<RetentionMutationPreviewResponse>(
            fixture.Application.CreatePreview(fixture.Request() with { Operation = RetentionMutationOperation.Pin }, fixture.WorkflowKey(11)).Preview);
        Assert.NotEqual(actionable.RejectionCode, rejected.RejectionCode);
        Assert.NotEqual(actionable.PreviewDigest, rejected.PreviewDigest);

        using var pinFixture = Fixture.Create();
        var unpinned = Assert.IsType<RetentionMutationPreviewResponse>(
            pinFixture.Application.CreatePreview(pinFixture.Request(), pinFixture.WorkflowKey(12)).Preview);
        pinFixture.SetState("retained_by_policy");
        var pinned = Assert.IsType<RetentionMutationPreviewResponse>(
            pinFixture.Application.CreatePreview(pinFixture.Request(), pinFixture.WorkflowKey(13)).Preview);
        Assert.NotEqual(unpinned.TargetItems[0].PinState, pinned.TargetItems[0].PinState);
        Assert.NotEqual(unpinned.PreviewDigest, pinned.PreviewDigest);
    }

    [Fact]
    public void CreatePreview_SameKeyByteIdenticalReplayReturnsStoredPreviewWithoutSecondRecord()
    {
        using var fixture = Fixture.Create();
        var request = fixture.Request();
        var key = fixture.WorkflowKey(3);

        var first = Assert.IsType<RetentionMutationPreviewResponse>(fixture.Application.CreatePreview(request, key).Preview);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        var replay = fixture.Application.CreatePreview(request, key);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay.Preview));
        Assert.True(replay.IsReplay);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_previews;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='preview';"));
    }

    [Fact]
    public void CreatePreview_DifferentRequestWithSameKeyReturnsIdempotencyConflictWithoutSecondRecord()
    {
        using var fixture = Fixture.Create();
        var key = fixture.WorkflowKey(4);

        fixture.Application.CreatePreview(fixture.Request(), key);
        var conflict = fixture.Application.CreatePreview(
            fixture.Request() with { Operation = RetentionMutationOperation.Unpin },
            key);

        Assert.Null(conflict.Preview);
        Assert.Equal(RetentionMutationErrorCodes.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_previews;"));
    }

    [Fact]
    public void ReadPreview_UnknownAndExactlyExpiredIdsReturnFixedOutcomesWithoutExtendingExpiry()
    {
        using var fixture = Fixture.Create();
        var created = Assert.IsType<RetentionMutationPreviewResponse>(
            fixture.Application.CreatePreview(fixture.Request(), fixture.WorkflowKey(5)).Preview);

        var unknown = fixture.Application.ReadPreview(RetentionMutationIdentifiers.CreatePreviewId(Enumerable.Repeat((byte)99, 16).ToArray()));
        Assert.Null(unknown.Preview);
        Assert.Equal(RetentionMutationErrorCodes.PreviewNotFound, unknown.ErrorCode);

        fixture.Time.Advance(RetentionMutationConstants.ConfirmationLifetime);
        var expired = fixture.Application.ReadPreview(created.PreviewId);
        var expiredAgain = fixture.Application.ReadPreview(created.PreviewId);

        Assert.Null(expired.Preview);
        Assert.Equal(RetentionMutationErrorCodes.PreviewExpired, expired.ErrorCode);
        Assert.Equal(expired.ErrorCode, expiredAgain.ErrorCode);
        Assert.Equal(created.ConfirmationExpiresAt, fixture.ScalarTimestamp(
            "SELECT expires_at FROM retention_mutation_previews WHERE preview_id=$preview;",
            ("$preview", created.PreviewId)));
    }

    [Fact]
    public void CreatePreview_EmptySessionPersistsNullExpiryAndRemainsReadableAfterFiveMinutes()
    {
        using var fixture = Fixture.Create(itemCount: 0);

        var created = Assert.IsType<RetentionMutationPreviewResponse>(
            fixture.Application.CreatePreview(
                fixture.Request() with
                {
                    Target = new(RetentionMutationTargetKind.Session, fixture.SessionId),
                    Scope = RetentionMutationScope.SessionItems
                },
                fixture.WorkflowKey(6)).Preview);

        Assert.Equal(RetentionMutationPreviewResult.EmptyNotApplicable, created.Result);
        Assert.Null(created.ConfirmationExpiresAt);
        Assert.Equal(DBNull.Value, fixture.ScalarObject(
            "SELECT expires_at FROM retention_mutation_previews WHERE preview_id=$preview;",
            ("$preview", created.PreviewId)));

        fixture.Time.Advance(RetentionMutationConstants.ConfirmationLifetime);
        var reread = fixture.Application.ReadPreview(created.PreviewId);
        Assert.Equal(JsonSerializer.Serialize(created), JsonSerializer.Serialize(reread.Preview));
        Assert.Null(reread.ErrorCode);
    }

    [Fact]
    public void CreatePreview_RequestStageFailuresProduceNoPreviewRecord()
    {
        using var fixture = Fixture.Create();
        var invalidRequest = fixture.Application.CreatePreview(
            fixture.Request() with { Comment = "password must never be accepted" },
            fixture.WorkflowKey(7));
        var invalidKey = fixture.Application.CreatePreview(fixture.Request(), "rid1_invalid");
        var missingTarget = fixture.Application.CreatePreview(
            fixture.Request() with
            {
                Target = new(RetentionMutationTargetKind.Item, "missing-item")
            },
            fixture.WorkflowKey(8));

        Assert.Equal(RetentionMutationErrorCodes.RequestInvalid, invalidRequest.ErrorCode);
        Assert.Equal(RetentionMutationErrorCodes.IdempotencyKeyInvalid, invalidKey.ErrorCode);
        Assert.Equal(RetentionMutationErrorCodes.TargetNotFound, missingTarget.ErrorCode);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_previews;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency;"));

        using var large = Fixture.Create(itemCount: 101);
        var limit = large.Application.CreatePreview(
            large.Request() with
            {
                Target = new(RetentionMutationTargetKind.Session, large.SessionId),
                Scope = RetentionMutationScope.SessionItems
            },
            large.WorkflowKey(9));
        Assert.Equal(RetentionMutationErrorCodes.TargetLimitExceeded, limit.ErrorCode);
        Assert.Equal(0L, large.Scalar("SELECT COUNT(*) FROM retention_mutation_previews;"));
        Assert.Equal(0L, large.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency;"));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, MutableTimeProvider time, RetentionCatalogStore store, string sessionId, string itemId)
            => (Path, Time, Store, SessionId, ItemId, Application) =
                (path, time, store, sessionId, itemId, new RetentionMutationApplicationService(store, time));

        internal string Path { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionCatalogStore Store { get; }
        internal string SessionId { get; }
        internal string ItemId { get; }
        internal RetentionMutationApplicationService Application { get; }

        internal static Fixture Create(int itemCount = 1)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-preview-application-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(Now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var sessionStore = new SqliteSessionStore(path, context, time);
            sessionStore.CreateSchema();
            var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
            var session = new ObservedSession(
                sessionId,
                ObservedSessionStatus.Completed,
                SessionCompleteness.Full,
                null,
                null,
                Now.AddMinutes(-1),
                Now,
                Now,
                SessionRawRetentionState.Expiring,
                Now.AddMinutes(-1),
                Now);
            var events = Enumerable.Range(0, itemCount)
                .Select(index => new ObservedSessionEvent(
                    Guid.Parse($"018f2b4e-7c1a-7f1a-8a2b-{index + 0x6072:X12}"),
                    sessionId,
                    null,
                    SessionSourceSurface.CopilotSdk,
                    null,
                    $"trace-{index}",
                    "received",
                    "copilot-sdk-stream",
                    $"event-{index}",
                    "user.message",
                    Now.AddSeconds(index),
                    SessionContentState.Available))
                .ToArray();
            var content = events
                .Select((item, index) => new SessionEventContent(
                    item.EventId,
                    "application/json",
                    $"{{\"index\":{index}}}",
                    Now.AddSeconds(index),
                    Now.AddDays(90).AddSeconds(index)))
                .ToArray();
            sessionStore.Write(new(new(session, [], [], events), content));
            var store = new RetentionCatalogStore(context, time);
            var itemId = itemCount == 0
                ? "missing-item"
                : Scalar<string>(path, "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' ORDER BY item_id LIMIT 1;");
            return new(path, time, store, sessionId.ToString("D"), itemId);
        }

        internal RetentionMutationPreviewRequest Request() => new(
            new(RetentionMutationTargetKind.Item, ItemId),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SingleItem,
            RetentionMutationReasonCodes.ResearchNeeded,
            null);

        internal string WorkflowKey(byte value) => RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat(value, 32).ToArray());

        internal void InsertAllFourActiveConflicts()
        {
            Execute("""
                INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation)
                VALUES
                    ($item,'access','read-owner',$expires,11),
                    ($item,'operation','operation-owner',$expires,12),
                    ($item,'deletion','deletion-owner',$expires,13);
                INSERT INTO retention_delete_journal(item_id,durable_cursor,intent_at,expected_revision)
                VALUES($item,NULL,$intent_at,1);
                """,
                ("$item", ItemId),
                ("$expires", Now.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture)),
                ("$intent_at", Now.ToString("O", CultureInfo.InvariantCulture)));
        }

        internal void SetState(string state) => Execute(
            "UPDATE retention_items SET state=$state WHERE item_id=$item;",
            ("$state", state),
            ("$item", ItemId));

        internal long Scalar(string sql, params (string Name, object Value)[] parameters) =>
            Convert.ToInt64(ScalarObject(sql, parameters), CultureInfo.InvariantCulture);

        internal string ScalarText(string sql, params (string Name, object Value)[] parameters) => (string)ScalarObject(sql, parameters)!;

        internal DateTimeOffset? ScalarTimestamp(string sql, params (string Name, object Value)[] parameters) =>
            ScalarObject(sql, parameters) is string value
                ? DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None)
                : null;

        internal object? ScalarObject(string sql, params (string Name, object Value)[] parameters) =>
            ScalarObject(Path, sql, parameters);

        internal void Execute(string sql, params (string Name, object Value)[] parameters) => Execute(Path, sql, parameters);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }

        private static T Scalar<T>(string path, string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return (T)command.ExecuteScalar()!;
        }

        private static object? ScalarObject(string path, string sql, IReadOnlyList<(string Name, object Value)> parameters)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command.ExecuteScalar();
        }

        private static void Execute(string path, string sql, IReadOnlyList<(string Name, object Value)> parameters)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
    }
}

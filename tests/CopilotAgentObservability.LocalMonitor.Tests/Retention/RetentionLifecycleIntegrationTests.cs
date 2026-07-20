using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionLifecycleIntegrationTests
{
    [Fact]
    public async Task PinSuppressesExpiryUntilUnpinQueuesAndExistingWorkerPhysicallyDeletesAfterRestart()
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();

        var (pinPreview, pin) = Execute(
            fixture,
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SingleItem,
            RetentionMutationTargetKind.Item,
            fixture.ItemId,
            80);
        Assert.Equal([fixture.ItemId], pinPreview.TargetItems.Select(item => item.ItemId));
        Assert.Equal(RetentionMutationCompletionCodes.PinApplied, pin.ResultCode);
        Assert.Equal(2L, Revision(fixture, fixture.ItemId));

        fixture.Time.Advance(TimeSpan.FromDays(91));
        await RunExistingWorkerAsync(fixture.Path, fixture.Time);

        Assert.Equal("retained_by_policy", State(fixture, fixture.ItemId));
        Assert.Equal(1L, SourceRowCount(fixture, fixture.ItemId));
        Assert.Equal(2L, Revision(fixture, fixture.ItemId));

        var (unpinPreview, unpin) = Execute(
            fixture,
            RetentionMutationOperation.Unpin,
            RetentionMutationScope.SingleItem,
            RetentionMutationTargetKind.Item,
            fixture.ItemId,
            81);
        Assert.Equal([fixture.ItemId], unpinPreview.TargetItems.Select(item => item.ItemId));
        Assert.Equal(RetentionMutationCompletionCodes.UnpinExpiredQueued, unpin.ResultCode);
        Assert.Equal("deletion_queued", State(fixture, fixture.ItemId));
        Assert.Equal(5L, Revision(fixture, fixture.ItemId));
        Assert.Equal(RetentionMutationDigests.ExpectedStateVersion([
            new RetentionMutationExpectedStateItem(
                fixture.ItemId,
                5,
                RetentionPinState.Unpinned,
                RetentionItemLifecycle.DeletionQueued)
        ]), unpin.ResultVersion);
        Assert.Equal(1L, SourceRowCount(fixture, fixture.ItemId));

        var audit = fixture.Store.ReadAuditEvents(new(RetentionMutationTargetKind.Item, fixture.ItemId));
        Assert.Equal([pin.OperationId, unpin.OperationId], audit.Reverse().Select(item => item.OperationId));
        Assert.Equal(pin.AuditEventId, audit[1].EventId);
        Assert.Equal(unpin.AuditEventId, audit[0].EventId);
        Assert.Equal(unpin.OperationId, fixture.Application.ReadOperationStatus(unpin.OperationId).Status!.OperationId);

        await RunExistingWorkerWithMidpointAsync(fixture.Path, fixture.Time, 1, () =>
        {
            Assert.Equal("deleting", State(fixture, fixture.ItemId));
            Assert.Equal(6L, Revision(fixture, fixture.ItemId));
            Assert.Equal(1L, SourceRowCount(fixture, fixture.ItemId));
            Assert.Equal(6L, fixture.Scalar("SELECT expected_revision FROM retention_delete_journal WHERE item_id=$item;", ("$item", fixture.ItemId)));
        });

        Assert.Equal("deleted", State(fixture, fixture.ItemId));
        Assert.Equal(7L, Revision(fixture, fixture.ItemId));
        Assert.Equal(0L, SourceRowCount(fixture, fixture.ItemId));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;", ("$item", fixture.ItemId)));
    }

    [Fact]
    public async Task SessionDeleteNowQueuesExactTargetsAndExistingWorkerAlonePhysicallyDeletesThemAfterRestart()
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create(itemCount: 2);

        var (pinPreview, pin) = Execute(
            fixture,
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SessionItems,
            RetentionMutationTargetKind.Session,
            fixture.SessionId,
            82);
        Assert.Equal(fixture.ItemIds, pinPreview.TargetItems.Select(item => item.ItemId).Order(StringComparer.Ordinal));
        Assert.Equal(RetentionMutationCompletionCodes.PinApplied, pin.ResultCode);
        Assert.Equal(RetentionPinState.Pinned, pin.PinState);
        Assert.All(fixture.ItemIds, item =>
        {
            Assert.Equal("retained_by_policy", State(fixture, item));
            Assert.Equal(2L, Revision(fixture, item));
        });
        Assert.Equal(RetentionMutationDigests.ExpectedStateVersion(fixture.ItemIds.Select(item =>
            new RetentionMutationExpectedStateItem(item, 2, RetentionPinState.Pinned, RetentionItemLifecycle.RetainedByPolicy))), pin.ResultVersion);

        var (preview, result) = Execute(
            fixture,
            RetentionMutationOperation.DeleteNow,
            RetentionMutationScope.SessionItems,
            RetentionMutationTargetKind.Session,
            fixture.SessionId,
            83);

        Assert.Equal(fixture.ItemIds, preview.TargetItems.Select(item => item.ItemId).Order(StringComparer.Ordinal));
        Assert.Equal(fixture.ItemIds.Count, result.TargetItemCount);
        Assert.Equal(RetentionMutationCompletionCodes.DeleteNowSupersededPin, result.ResultCode);
        Assert.Equal(fixture.SessionId, result.TargetId);
        Assert.Equal(RetentionMutationTargetKind.Session, result.TargetKind);
        Assert.Equal(2L, fixture.Scalar("SELECT COUNT(*) FROM session_event_content;"));
        Assert.All(fixture.ItemIds, item =>
        {
            Assert.Equal("deletion_queued", State(fixture, item));
            Assert.Equal(5L, Revision(fixture, item));
        });
        Assert.Equal(RetentionMutationDigests.ExpectedStateVersion(fixture.ItemIds.Select(item =>
            new RetentionMutationExpectedStateItem(item, 5, RetentionPinState.Unpinned, RetentionItemLifecycle.DeletionQueued))), result.ResultVersion);

        foreach (var item in fixture.ItemIds)
        {
            var selectorCalls = 0;
            var read = await fixture.Store.ReadAsync(
                new RetentionReadRequest(OwnershipKey(fixture, item), RetentionReadKind.Access, fixture.Time.GetUtcNow(), 5),
                (_, _, _, _) =>
                {
                    selectorCalls++;
                    return ValueTask.FromResult<string?>("must-not-be-materialized");
                },
                CancellationToken.None);
            Assert.Equal(RetentionReadDisposition.Denied, read.Disposition);
            Assert.Null(read.Lease);
            Assert.Equal(0, selectorCalls);
        }

        var audit = fixture.Store.ReadAuditEvents(new(RetentionMutationTargetKind.Session, fixture.SessionId));
        Assert.Equal(2, audit.Count);
        Assert.Equal(pin.AuditEventId, Assert.Single(audit, item => item.OperationId == pin.OperationId).EventId);
        Assert.Equal(result.AuditEventId, Assert.Single(audit, item => item.OperationId == result.OperationId).EventId);
        var status = Assert.IsType<RetentionMutationStatusResponse>(fixture.Application.ReadOperationStatus(result.OperationId).Status);
        Assert.Equal(result.OperationId, status.OperationId);
        Assert.Equal(result.ResultCode, status.ResultCode);

        await RunExistingWorkerWithMidpointAsync(fixture.Path, fixture.Time, 2, () =>
        {
            Assert.Equal(2L, fixture.Scalar("SELECT COUNT(*) FROM session_event_content;"));
            Assert.All(fixture.ItemIds, item =>
            {
                Assert.Equal("deleting", State(fixture, item));
                Assert.Equal(6L, Revision(fixture, item));
                Assert.Equal(6L, fixture.Scalar("SELECT expected_revision FROM retention_delete_journal WHERE item_id=$item;", ("$item", item)));
            });
        });

        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM session_event_content;"));
        Assert.All(fixture.ItemIds, item =>
        {
            Assert.Equal("deleted", State(fixture, item));
            Assert.Equal(7L, Revision(fixture, item));
            Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;", ("$item", item)));
        });

        var durableBeforeLateWrite = DurableSessionSnapshot(fixture);
        var reopenedContext = RetentionCatalogContext.AdoptExistingCatalogV1(fixture.Path);
        var lateWritePhases = new List<string>();
        var sessionStore = new SqliteSessionStore(fixture.Path, reopenedContext, fixture.Time, lateWritePhases.Add);
        var lateReplay = CreateLateSessionReplay(sessionStore, Guid.Parse(fixture.SessionId), fixture.Time.GetUtcNow());
        Assert.Throws<RetentionMigrationBlockedException>(() => sessionStore.Write(lateReplay));
        Assert.Equal(["before-session-write", "after-session-content-source"], lateWritePhases);
        Assert.Equal(durableBeforeLateWrite, DurableSessionSnapshot(fixture));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM session_event_content;"));
        Assert.All(fixture.ItemIds, item =>
        {
            Assert.Equal("deleted", State(fixture, item));
            Assert.Equal(7L, Revision(fixture, item));
            Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;", ("$item", item)));
        });

        var freshSessionId = Guid.CreateVersion7();
        var freshEventId = Guid.CreateVersion7();
        sessionStore.Write(CreateFreshSessionWrite(freshSessionId, freshEventId, fixture.Time.GetUtcNow()));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM sessions WHERE session_id=$session;", ("$session", freshSessionId.ToString("D"))));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM session_event_content WHERE event_id=$event;", ("$event", freshEventId.ToString("D"))));
        Assert.Equal("expiring", fixture.ScalarText("SELECT state FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$event;", ("$event", freshEventId.ToString("D"))));
        Assert.All(fixture.ItemIds, item =>
        {
            Assert.Equal(0L, SourceRowCount(fixture, item));
            Assert.Equal("deleted", State(fixture, item));
            Assert.Equal(7L, Revision(fixture, item));
            Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;", ("$item", item)));
        });
    }

    [Fact]
    public async Task CancelledMaintenance_RecordsOnlyFixedRetryWithoutLifecycleMutation()
    {
        var path=Path.Combine(Path.GetTempPath(),$"retention-maintenance-{Guid.NewGuid():N}.db");
        try
        {
            var now=new DateTimeOffset(2026,7,19,0,0,0,TimeSpan.Zero);var time=new MutableTimeProvider(now);var store=new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(path,time);store.CreateSchema();
            using(var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")){c.Open();using var q=c.CreateCommand();q.CommandText="INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,adapter_coverage_version) VALUES('item',(SELECT store_instance_id FROM retention_store_instances WHERE id=1),'raw_record','source',1,zeroblob(32),$now,$now,'raw-default-90d',1,'deleted',2,$now,$now,1); INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES('item',$now,$now);";q.Parameters.AddWithValue("$now",now.ToString("O"));q.ExecuteNonQuery();}
            await store.RecordCleanCycleAsync(true,now,CancellationToken.None);var before=Snapshot(path);using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            Assert.False(await store.TryRunMaintenanceAsync(now,cancelled.Token));
            Assert.Equal(before,Snapshot(path));
            using var inspect=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");inspect.Open();Assert.Equal("retention_maintenance_busy",Text(inspect,"SELECT maintenance_error_code FROM retention_worker_state WHERE id=1"));Assert.Equal(now+TimeSpan.FromMinutes(1),DateTimeOffset.Parse(Text(inspect,"SELECT maintenance_due_at FROM retention_worker_state WHERE id=1")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();foreach(var file in new[]{path,path+"-wal",path+"-shm"}) if(File.Exists(file))File.Delete(file); }
    }

    [Fact]
    public void Migration_AddsWorkerFieldsAndKeepsV1AcrossReopens()
    {
        var path=Path.Combine(Path.GetTempPath(),$"retention-migration-{Guid.NewGuid():N}.db");
        try
        {
            var store=new CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionCatalogStore(path);store.CreateSchema();store.CreateSchema();
            using var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");c.Open();
            Assert.Equal(1L,Scalar(c,"SELECT version FROM retention_component_versions WHERE component='retention'"));
            Assert.Equal(1L,Scalar(c,"SELECT COUNT(*) FROM pragma_table_info('retention_items') WHERE name='deletion_started_at'"));
            Assert.Equal(1L,Scalar(c,"SELECT COUNT(*) FROM retention_worker_state WHERE id=1"));
            Assert.Equal(1L,Scalar(c,"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_retention_items_worker_order'"));
            Assert.Equal(1L,Scalar(c,"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_retention_leases_kind_expiry'"));
        }
        finally { foreach(var file in new[]{path,path+"-wal",path+"-shm"}) if(File.Exists(file))File.Delete(file); }
    }
    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToInt64(q.ExecuteScalar());}
    private static string Text(Microsoft.Data.Sqlite.SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;return (string)q.ExecuteScalar()!;}
    private static string Snapshot(string path){using var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText="SELECT state || ':' || revision || ':' || attempt_count || ':' || COALESCE(error_code,'') || ':' || COALESCE(next_retry_at,'') FROM retention_items WHERE item_id='item';";return (string)q.ExecuteScalar()!;}

    private static (RetentionMutationPreviewResponse Preview, RetentionMutationResult Result) Execute(
        RetentionMutationConfirmationApplicationTests.Fixture fixture,
        RetentionMutationOperation operation,
        RetentionMutationScope scope,
        RetentionMutationTargetKind targetKind,
        string targetId,
        byte workflowKey)
    {
        var key = fixture.WorkflowKey(workflowKey);
        var preview = Assert.IsType<RetentionMutationPreviewResponse>(fixture.Application.CreatePreview(
            new(new(targetKind, targetId), operation, scope, RetentionMutationReasonCodes.TestCleanup, null),
            key).Preview);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest),
            key).Confirmation);
        var result = Assert.IsType<RetentionMutationResult>(fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, operation, scope, targetKind, targetId),
            key).Result);
        return (preview, result);
    }

    private static RetentionOwnershipKey OwnershipKey(
        RetentionMutationConfirmationApplicationTests.Fixture fixture,
        string itemId) =>
        new(
            fixture.Store.StoreInstanceId,
            RetentionStoreKind.SessionEventContent,
            fixture.ScalarText("SELECT source_item_id FROM retention_items WHERE item_id=$item;", ("$item", itemId))!);

    private static SessionWriteBatch CreateLateSessionReplay(
        SqliteSessionStore sessionStore,
        Guid sessionId,
        DateTimeOffset now)
    {
        var detail = Assert.IsType<SessionDetail>(sessionStore.GetDetail(sessionId));
        var content = detail.Events.Select((item, index) => new SessionEventContent(
            item.EventId,
            "application/json",
            $"{{\"index\":{index}}}",
            now.AddSeconds(index),
            now.AddDays(90).AddSeconds(index))).ToArray();
        return new(detail, content);
    }

    private static SessionWriteBatch CreateFreshSessionWrite(Guid sessionId, Guid eventId, DateTimeOffset now)
    {
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Completed,
            SessionCompleteness.Full,
            null,
            null,
            now,
            now,
            now,
            SessionRawRetentionState.Expiring,
            now,
            now);
        var observedEvent = new ObservedSessionEvent(
            eventId,
            sessionId,
            null,
            SessionSourceSurface.CopilotSdk,
            null,
            "fresh-trace",
            "received",
            "copilot-sdk-stream",
            $"fresh-{eventId:D}",
            "user.message",
            now,
            SessionContentState.Available);
        return new(
            new(session, [], [], [observedEvent]),
            [new(eventId, "application/json", "{}", now, now.AddDays(90))]);
    }

    private static async Task RunExistingWorkerAsync(string path, MutableTimeProvider time)
    {
        var (worker, _) = CreateExistingWorker(path, time, expectedSessionDeletions: 0);
        await worker.RunOnceAsync(CancellationToken.None);
    }

    private static async Task RunExistingWorkerWithMidpointAsync(
        string path,
        MutableTimeProvider time,
        int expectedSessionDeletions,
        Action assertMidpoint)
    {
        var (worker, adapter) = CreateExistingWorker(path, time, expectedSessionDeletions);
        var run = worker.RunOnceAsync(CancellationToken.None).AsTask();
        await adapter!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            assertMidpoint();
        }
        finally
        {
            adapter.Release.TrySetResult();
            await run;
        }
    }

    private static (RetentionCleanupWorker Worker, GatedDelegatingAdapter? Adapter) CreateExistingWorker(
        string path,
        MutableTimeProvider time,
        int expectedSessionDeletions)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(path), time);
        var sessionAdapter = new SessionEventContentRetentionAdapter(reopened);
        var gatedSessionAdapter = expectedSessionDeletions > 0
            ? new GatedDelegatingAdapter(sessionAdapter, expectedSessionDeletions)
            : null;
        IRetentionDeletionAdapter registeredSessionAdapter = gatedSessionAdapter ?? (IRetentionDeletionAdapter)sessionAdapter;
        var registry = new RetentionAdapterRegistry([
            registeredSessionAdapter,
            new UnreachableAdapter(RetentionStoreKind.RawRecord),
            new UnreachableAdapter(RetentionStoreKind.AnalysisRunRaw),
            new UnreachableAdapter(RetentionStoreKind.SensitiveBundle),
            new UnreachableAdapter(RetentionStoreKind.AnalysisSdkDirectory)
        ]);
        reopened.RegisterAdapterCoverage(registry);
        return (new RetentionCleanupWorker(new RetentionCleanupCoordinator(reopened, registry, time), time), gatedSessionAdapter);
    }

    private static long SourceRowCount(RetentionMutationConfirmationApplicationTests.Fixture fixture, string itemId) =>
        fixture.Scalar(
            "SELECT COUNT(*) FROM session_event_content WHERE event_id=(SELECT source_item_id FROM retention_items WHERE item_id=$item);",
            ("$item", itemId));

    private static long Revision(RetentionMutationConfirmationApplicationTests.Fixture fixture, string itemId) =>
        fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", itemId));

    private static string? DurableSessionSnapshot(RetentionMutationConfirmationApplicationTests.Fixture fixture) =>
        fixture.ScalarText("""
            SELECT
                (SELECT COUNT(*) FROM sessions) || ':' ||
                (SELECT COUNT(*) FROM session_events) || ':' ||
                (SELECT COUNT(*) FROM session_event_content) || ':' ||
                (SELECT group_concat(item_id || '=' || state || '@' || revision, ',') FROM (SELECT item_id,state,revision FROM retention_items ORDER BY item_id)) || ':' ||
                (SELECT group_concat(item_id, ',') FROM (SELECT item_id FROM retention_tombstones ORDER BY item_id));
            """);

    private static string? State(RetentionMutationConfirmationApplicationTests.Fixture fixture, string itemId) =>
        fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", itemId));

    private sealed class UnreachableAdapter(RetentionStoreKind storeKind) : IRetentionDeletionAdapter
    {
        public RetentionStoreKind StoreKind => storeKind;

        public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context) =>
            throw new Xunit.Sdk.XunitException($"Unexpected adapter invocation: {storeKind}");
    }

    private sealed class GatedDelegatingAdapter(IRetentionDeletionAdapter inner, int expectedCalls) : IRetentionDeletionAdapter
    {
        private int calls;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RetentionStoreKind StoreKind => inner.StoreKind;

        public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
        {
            if (Interlocked.Increment(ref calls) == expectedCalls) Entered.TrySetResult();
            await Release.Task.WaitAsync(context.CancellationToken);
            return await inner.DeleteAsync(context);
        }
    }
}

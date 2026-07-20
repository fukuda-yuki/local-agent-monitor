using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationTargetResolutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SessionResolution_UsesOnlyExactEventLinkageAndOwnsOnlySessionContent()
    {
        using var fixture = Fixture.Create();

        var result = fixture.Store.ResolveMutationTarget(new(RetentionMutationTargetKind.Session, fixture.SessionId));

        Assert.Equal(RetentionMutationTargetResolutionOutcome.Resolved, result.Outcome);
        Assert.Equal([fixture.OwnedEventItemId], result.Items.Select(static item => item.ItemId));
        Assert.Empty(result.ExcludedItemsByReason);
    }

    [Fact]
    public void SessionResolution_CountsOnlyFailedOwnershipProofAndReturnsAllExcludedShape()
    {
        using var fixture = Fixture.Create();
        fixture.Execute("UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE item_id=$item;", ("$item", fixture.OwnedEventItemId));

        var result = fixture.Store.ResolveMutationTarget(new(RetentionMutationTargetKind.Session, fixture.SessionId));

        Assert.Equal(RetentionMutationTargetResolutionOutcome.EmptyNotApplicable, result.Outcome);
        Assert.Equal(RetentionMutationEmptyReason.AllCandidatesExcluded, result.EmptyReason);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.ExcludedItemCount);
        Assert.Equal(new RetentionExclusionSummary(RetentionMutationExclusionCodes.MissingOwnershipProof, 1), Assert.Single(result.ExcludedItemsByReason));
    }

    [Fact]
    public void SessionResolution_ReturnsExplicitEmptyForExistingSessionWithoutExactOwnedItems()
    {
        using var fixture = Fixture.Create(includeOwnedContent: false);

        var result = fixture.Store.ResolveMutationTarget(new(RetentionMutationTargetKind.Session, fixture.SessionId));

        Assert.Equal(RetentionMutationTargetResolutionOutcome.EmptyNotApplicable, result.Outcome);
        Assert.Equal(RetentionMutationEmptyReason.NoExactOwnedItems, result.EmptyReason);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.ExcludedItemCount);
        Assert.False(result.MutationAllowed);
    }

    [Fact]
    public void Resolution_ReturnsNotFoundForUnknownSessionAndItem()
    {
        using var fixture = Fixture.Create();

        var unknownSession = fixture.Store.ResolveMutationTarget(new(RetentionMutationTargetKind.Session, "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6099"));
        var unknownItem = fixture.Store.ResolveMutationTarget(new(RetentionMutationTargetKind.Item, "opaque-item-that-is-not-present"));

        Assert.Equal(RetentionMutationTargetResolutionOutcome.NotFound, unknownSession.Outcome);
        Assert.Equal(RetentionMutationErrorCodes.TargetNotFound, unknownSession.ErrorCode);
        Assert.Equal(RetentionMutationTargetResolutionOutcome.NotFound, unknownItem.Outcome);
        Assert.Equal(RetentionMutationErrorCodes.TargetNotFound, unknownItem.ErrorCode);
    }

    [Fact]
    public void ItemResolution_IsByteExactAndDoesNotExpandToSessionSiblings()
    {
        using var fixture = Fixture.Create();
        const string opaqueId = "Opaque/ID?retention-item";
        fixture.Execute("UPDATE retention_items SET item_id=$opaque WHERE item_id=$item;", ("$opaque", opaqueId), ("$item", fixture.OwnedEventItemId));

        var exact = fixture.Store.ResolveMutationTarget(new(RetentionMutationTargetKind.Item, opaqueId));
        var changedByte = fixture.Store.ResolveMutationTarget(new(RetentionMutationTargetKind.Item, opaqueId.ToUpperInvariant()));

        Assert.Equal(RetentionMutationTargetResolutionOutcome.Resolved, exact.Outcome);
        Assert.Equal([opaqueId], exact.Items.Select(static item => item.ItemId));
        Assert.Equal(RetentionMutationTargetResolutionOutcome.NotFound, changedByte.Outcome);
        Assert.DoesNotContain(fixture.SiblingEventItemId, exact.Items.Select(static item => item.ItemId));
    }

    [Fact]
    public void ItemResolution_RejectsAStoredNonV1StoreKind()
    {
        using var fixture = Fixture.Create();
        fixture.Execute("PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql=replace(sql, 'store_kind TEXT NOT NULL CHECK (store_kind IN (''session_event_content'',''raw_record'',''analysis_run_raw'',''sensitive_bundle'',''analysis_sdk_directory''))', 'store_kind TEXT NOT NULL') WHERE type='table' AND name='retention_items'; PRAGMA writable_schema=OFF;");
        fixture.Execute("INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) SELECT 'future-item',(SELECT store_instance_id FROM retention_store_instances WHERE id=1),'future_store','future-source',1,zeroblob(32),$captured,$expires,'raw-default-90d',1,'expiring',1,1;", ("$captured", Now.ToString("O")), ("$expires", Now.AddDays(90).ToString("O")));

        var result = fixture.Store.ResolveMutationTarget(new(RetentionMutationTargetKind.Item, "future-item"));

        Assert.Equal(RetentionMutationTargetResolutionOutcome.NotApplicable, result.Outcome);
        Assert.Equal(RetentionMutationErrorCodes.TargetNotApplicable, result.ErrorCode);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, RetentionCatalogStore store, string sessionId, string ownedEventItemId, string siblingEventItemId)
            => (Path, Store, SessionId, OwnedEventItemId, SiblingEventItemId) = (path, store, sessionId, ownedEventItemId, siblingEventItemId);

        internal string Path { get; }
        internal RetentionCatalogStore Store { get; }
        internal string SessionId { get; }
        internal string OwnedEventItemId { get; }
        internal string SiblingEventItemId { get; }

        internal static Fixture Create(bool includeOwnedContent = true)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-mutation-resolution-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(Now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var sessionStore = new SqliteSessionStore(path, context, time);
            sessionStore.CreateSchema();

            var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
            var ownedEvent = new ObservedSessionEvent(Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6072"), sessionId, null, SessionSourceSurface.CopilotSdk, null, "trace-near", "received", "copilot-sdk-stream", "owned", "user.message", Now, SessionContentState.Available);
            var foreignSessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6081");
            var siblingEvent = new ObservedSessionEvent(Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6073"), foreignSessionId, null, SessionSourceSurface.CopilotSdk, null, "trace-near", "received", "copilot-sdk-stream", "sibling", "assistant.message", Now.AddSeconds(1), SessionContentState.Available);
            var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, "repository-marker", "workspace-marker", Now.AddMinutes(-1), Now, Now, SessionRawRetentionState.Expiring, Now.AddMinutes(-1), Now);
            var content = includeOwnedContent
                ? [new SessionEventContent(ownedEvent.EventId, "application/json", "{\"content\":\"synthetic\"}", Now, Now.AddDays(90))]
                : Array.Empty<SessionEventContent>();
            sessionStore.Write(new(new(session, [], [], [ownedEvent]), content));
            var foreignSession = new ObservedSession(foreignSessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, "repository-marker", "workspace-marker", Now.AddMinutes(-1), Now, Now, SessionRawRetentionState.Expiring, Now.AddMinutes(-1), Now);
            sessionStore.Write(new(new(foreignSession, [], [], [siblingEvent]), [new SessionEventContent(siblingEvent.EventId, "application/json", "{\"content\":\"foreign\"}", Now.AddSeconds(1), Now.AddDays(90))]));

            var store = new RetentionCatalogStore(context, time);
            var ownedItem = Scalar<string>(path, "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$event;", ("$event", ownedEvent.EventId.ToString("D")));
            var siblingItem = Scalar<string>(path, "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$event;", ("$event", siblingEvent.EventId.ToString("D")));
            Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);", []);
            Execute(path, "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) SELECT 'run-like-item',store_instance_id,'raw_record',$source,1,zeroblob(32),$captured,$expires,'raw-default-90d',1,'expiring',1,1 FROM retention_store_instances WHERE id=1;", [("$source", "run-id-near"), ("$captured", Now.ToString("O")), ("$expires", Now.AddDays(90).ToString("O"))]);
            Execute(path, "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) SELECT 'trace-like-item',store_instance_id,'analysis_run_raw',$source,1,zeroblob(32),$captured,$expires,'raw-default-90d',1,'expiring',1,1 FROM retention_store_instances WHERE id=1;", [("$source", "trace-near"), ("$captured", Now.ToString("O")), ("$expires", Now.AddDays(90).ToString("O"))]);
            return new(path, store, sessionId.ToString("D"), ownedItem, siblingItem);
        }

        internal void Execute(string sql, params (string Name, object Value)[] values) => Execute(Path, sql, values);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }

        private static void Execute(string path, string sql, IReadOnlyList<(string Name, object Value)> values)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        private static T Scalar<T>(string path, string sql, params (string Name, object Value)[] values)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            return (T)command.ExecuteScalar()!;
        }
    }
}

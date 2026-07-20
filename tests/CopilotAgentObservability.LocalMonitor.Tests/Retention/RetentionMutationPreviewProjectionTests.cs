using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationPreviewProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Projection_OrdersNullExpiryLastAndIncludesAllSevenLifecycleCounts()
    {
        var items = CreateLifecycleItems();
        var resolution = new RetentionMutationTargetResolution(RetentionMutationTargetResolutionOutcome.Resolved, null, null, items, 0, []);
        var version = VersionVector(items);

        var projection = RetentionMutationPreviewProjector.Project(
            new(RetentionMutationTargetKind.Item, "item-expiring"),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SingleItem,
            resolution,
            version,
            null,
            [],
            Now);

        Assert.Equal(["item-deleted", "item-deleting", "item-queued", "item-expired", "item-expiring", "item-pinned", "item-null-expiry"], projection.TargetItems.Select(static item => item.ItemId));
        Assert.Equal(new RetentionMutationLifecycleCounts(1, 1, 1, 1, 1, 1, 1), projection.CurrentState.LifecycleCounts);
        Assert.Equal(2, projection.CurrentState.ReadableItemCount);
        Assert.Equal(5, projection.CurrentState.ReadDeniedItemCount);
        Assert.Equal(1, projection.CurrentState.PinnedItemCount);
        Assert.Equal(6, projection.CurrentState.UnpinnedItemCount);
        Assert.Equal(2, projection.StoreKindSummary.Count);
        Assert.Equal(2, projection.CaptureExpiryPolicySummary.Count);
        var rawPolicy = Assert.Single(projection.CaptureExpiryPolicySummary, static summary => summary.PolicyId == "raw-default-90d");
        Assert.Equal(Now.AddDays(83), rawPolicy.OriginalExpiresAtMin);
        Assert.Equal(Now.AddDays(89), rawPolicy.OriginalExpiresAtMax);
        Assert.Equal(RetentionMutationErrorCodes.PinDeleted, projection.RejectionCode);
        Assert.Equal(RetentionMutationConstants.BackupWarningCode, projection.BackupNonPurgeWarningCode);
        Assert.DoesNotContain("source_item_id", Serialize(projection), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_PATH_MARKER", Serialize(projection), StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_EmptySessionUsesExplicitEmptyShapeAndZeroCounts()
    {
        var resolution = new RetentionMutationTargetResolution(
            RetentionMutationTargetResolutionOutcome.EmptyNotApplicable,
            null,
            RetentionMutationEmptyReason.NoExactOwnedItems,
            [],
            0,
            []);

        var projection = RetentionMutationPreviewProjector.Project(
            new(RetentionMutationTargetKind.Session, "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071"),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SessionItems,
            resolution,
            VersionVector([]),
            new(RetentionMutationSourceState.NotCaptured, RetentionMutationSessionCompleteness.Full, "not_captured"),
            [],
            Now);

        Assert.Equal(RetentionMutationPreviewResult.EmptyNotApplicable, projection.Result);
        Assert.Equal(RetentionMutationEmptyReason.NoExactOwnedItems, projection.EmptyReason);
        Assert.False(projection.MutationAllowed);
        Assert.Empty(projection.TargetItems);
        Assert.Equal(0, projection.TargetItemCount);
        Assert.Equal(new RetentionMutationLifecycleCounts(0, 0, 0, 0, 0, 0, 0), projection.CurrentState.LifecycleCounts);
        Assert.Equal(0, projection.CurrentState.ReadableItemCount);
        Assert.Equal(0, projection.CurrentState.ReadDeniedItemCount);
        Assert.Null(projection.BackupNonPurgeWarningCode);
        Assert.Null(projection.RejectionCode);
    }

    [Theory]
    [InlineData(RetentionMutationOperation.Pin, false)]
    [InlineData(RetentionMutationOperation.Unpin, false)]
    [InlineData(RetentionMutationOperation.DeleteNow, true)]
    public void Projection_RetainedImpactFollowsOperation(RetentionMutationOperation operation, bool rawContentWillBeDeleted)
    {
        var items = CreateFutureReadableItems();
        var resolution = new RetentionMutationTargetResolution(RetentionMutationTargetResolutionOutcome.Resolved, null, null, items, 0, []);

        var projection = RetentionMutationPreviewProjector.Project(
            new(RetentionMutationTargetKind.Session, "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071"),
            operation,
            RetentionMutationScope.SessionItems,
            resolution,
            VersionVector(items),
            new(RetentionMutationSourceState.Available, RetentionMutationSessionCompleteness.Full, "available"),
            [],
            Now);

        Assert.Equal(rawContentWillBeDeleted, projection.RetainedMetadataImpact.RawContentWillBeDeleted);
        Assert.True(projection.RetainedMetadataImpact.SessionMetadataRetained);
        Assert.Equal(items.Count, projection.RetainedMetadataImpact.EventMetadataRetainedCount);
    }

    [Fact]
    public void Projection_RetainedImpactMarksExpiredOriginalUnpinAsDeletingRawContent()
    {
        var items = new[]
        {
            Item("item-expired-unpin", RetentionItemLifecycle.RetainedByPolicy, Now.AddDays(-91), Now.AddDays(30), RetentionStoreKind.SessionEventContent, null, "raw-default-90d")
        };
        var resolution = new RetentionMutationTargetResolution(RetentionMutationTargetResolutionOutcome.Resolved, null, null, items, 0, []);

        var projection = RetentionMutationPreviewProjector.Project(
            new(RetentionMutationTargetKind.Item, items[0].ItemId),
            RetentionMutationOperation.Unpin,
            RetentionMutationScope.SingleItem,
            resolution,
            VersionVector(items),
            null,
            [],
            Now);

        Assert.True(projection.RetainedMetadataImpact.RawContentWillBeDeleted);
        Assert.True(projection.MutationAllowed);
        Assert.Null(projection.RejectionCode);
    }

    [Fact]
    public void Collection_FailsClosedAt101WithoutAProjection()
    {
        using var fixture = LargeFixture.Create(101);

        var result = fixture.Store.CollectMutationPreviewProjection(
            new(RetentionMutationTargetKind.Session, fixture.SessionId),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SessionItems,
            Now);

        Assert.Equal(RetentionMutationPreviewProjectionOutcome.TargetLimitExceeded, result.Outcome);
        Assert.Equal(RetentionMutationErrorCodes.TargetLimitExceeded, result.ErrorCode);
        Assert.Null(result.Projection);
    }

    [Fact]
    public void CollectionAcceptsExactly100ItemsAndProvidesVersionVectorDigests()
    {
        using var fixture = LargeFixture.Create(100);

        var result = fixture.Store.CollectMutationPreviewProjection(
            new(RetentionMutationTargetKind.Session, fixture.SessionId),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SessionItems,
            Now);

        var projection = Assert.IsType<RetentionMutationPreviewProjection>(result.Projection);
        Assert.Equal(RetentionMutationPreviewProjectionOutcome.Ready, result.Outcome);
        Assert.Equal(100, projection.TargetItemCount);
        Assert.StartsWith("v1-", projection.ExpectedStateVersion, StringComparison.Ordinal);
        Assert.StartsWith("sha256-", projection.TargetItemSetDigest, StringComparison.Ordinal);
    }

    private static IReadOnlyList<RetentionMutationResolvedItem> CreateLifecycleItems() =>
    [
        Item("item-expiring", RetentionItemLifecycle.Expiring, Now.AddDays(-1), Now.AddDays(1), RetentionStoreKind.SessionEventContent, null),
        Item("item-pinned", RetentionItemLifecycle.RetainedByPolicy, Now.AddDays(-2), Now.AddDays(2), RetentionStoreKind.SessionEventContent, null),
        Item("item-expired", RetentionItemLifecycle.ExpiredPendingDeletion, Now.AddDays(-3), Now.AddDays(-1), RetentionStoreKind.RawRecord, Now),
        Item("item-queued", RetentionItemLifecycle.DeletionQueued, Now.AddDays(-4), Now.AddDays(-2), RetentionStoreKind.RawRecord, Now),
        Item("item-deleting", RetentionItemLifecycle.Deleting, Now.AddDays(-5), Now.AddDays(-3), RetentionStoreKind.RawRecord, Now),
        Item("item-deleted", RetentionItemLifecycle.Deleted, Now.AddDays(-6), Now.AddDays(-4), RetentionStoreKind.RawRecord, Now),
        Item("item-null-expiry", RetentionItemLifecycle.DeletionFailed, Now.AddDays(-7), null, RetentionStoreKind.RawRecord, Now)
    ];

    private static IReadOnlyList<RetentionMutationResolvedItem> CreateFutureReadableItems() =>
    [
        Item("item-expiring", RetentionItemLifecycle.Expiring, Now.AddDays(-1), Now.AddDays(1), RetentionStoreKind.SessionEventContent, null),
        Item("item-pinned", RetentionItemLifecycle.RetainedByPolicy, Now.AddDays(-2), Now.AddDays(2), RetentionStoreKind.SessionEventContent, null)
    ];

    private static RetentionMutationResolvedItem Item(string id, RetentionItemLifecycle state, DateTimeOffset capturedAt, DateTimeOffset? expiresAt, RetentionStoreKind kind, DateTimeOffset? deniedAt, string? policyId = null) =>
        new(id, kind, state, capturedAt, expiresAt, policyId ?? (state == RetentionItemLifecycle.RetainedByPolicy ? "sensitive-bundle-7d" : "raw-default-90d"), 1, deniedAt, deniedAt, 1, state == RetentionItemLifecycle.DeletionFailed, state == RetentionItemLifecycle.DeletionFailed ? RetentionErrorCode.DeleteIoFailed : null);

    private static RetentionMutationVersionVector VersionVector(IReadOnlyList<RetentionMutationResolvedItem> items)
    {
        var expected = items.Select(static item => new RetentionMutationExpectedStateItem(item.ItemId, item.Revision, RetentionMutationStateProjection.PinState(item.State), item.State)).ToArray();
        var targets = items.Select(static item => new RetentionMutationDigestItem(item.ItemId, item.StoreKind)).ToArray();
        return new(expected, targets, RetentionMutationDigests.ExpectedStateVersion(expected), RetentionMutationDigests.TargetItemSetDigest(targets));
    }

    private static string Serialize(RetentionMutationPreviewProjection projection) =>
        System.Text.Json.JsonSerializer.Serialize(projection);

    private sealed class LargeFixture : IDisposable
    {
        private LargeFixture(string path, RetentionCatalogStore store, string sessionId) => (Path, Store, SessionId) = (path, store, sessionId);

        internal string Path { get; }
        internal RetentionCatalogStore Store { get; }
        internal string SessionId { get; }

        internal static LargeFixture Create(int count)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-mutation-preview-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(Now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var sessionStore = new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(path, context, time);
            sessionStore.CreateSchema();
            var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
            var session = new CopilotAgentObservability.Telemetry.Sessions.ObservedSession(sessionId, CopilotAgentObservability.Telemetry.Sessions.ObservedSessionStatus.Completed, CopilotAgentObservability.Telemetry.Sessions.SessionCompleteness.Full, null, null, Now.AddMinutes(-1), Now, Now, CopilotAgentObservability.Telemetry.Sessions.SessionRawRetentionState.Expiring, Now.AddMinutes(-1), Now);
            var events = Enumerable.Range(0, count).Select(index => new CopilotAgentObservability.Telemetry.Sessions.ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, CopilotAgentObservability.Telemetry.Sessions.SessionSourceSurface.CopilotSdk, null, $"trace-{index}", "received", "copilot-sdk-stream", $"event-{index}", "user.message", Now.AddSeconds(index), CopilotAgentObservability.Telemetry.Sessions.SessionContentState.Available)).ToArray();
            var content = events.Select((item, index) => new CopilotAgentObservability.Telemetry.Sessions.SessionEventContent(item.EventId, "application/json", $"{{\"index\":{index}}}", Now.AddSeconds(index), Now.AddDays(90).AddSeconds(index))).ToArray();
            sessionStore.Write(new(new(session, [], [], events), content));
            Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            return new(path, new RetentionCatalogStore(context, time), sessionId.ToString("D"));
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }

        private static void Execute(string path, string sql)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}

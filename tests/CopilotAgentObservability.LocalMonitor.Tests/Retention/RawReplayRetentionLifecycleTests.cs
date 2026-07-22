using System.Globalization;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RawReplayRetentionLifecycleTests
{
    [Fact]
    public async Task Raw_replay_expires_at_exactly_seven_days_and_cleanup_deletes_only_the_owned_child()
    {
        using var fixture = await Fixture.CreateAsync();
        var unlinked = new List<int>();
        var registry = Registry(fixture.Catalog, fixture.Time, checkpoint =>
        {
            if (checkpoint.Phase == SensitiveBundleRetentionCheckpointPhase.AfterUnlink)
                unlinked.Add(checkpoint.Ordinal);
        });
        fixture.Catalog.RegisterAdapterCoverage(registry);
        var coordinator = new RetentionCleanupCoordinator(fixture.Catalog, registry, fixture.Time);
        var (capturedAt, expiresAt) = fixture.CaptureWindow();

        Assert.Equal(capturedAt.AddDays(7), expiresAt);
        fixture.Time.Advance(RetentionV1Constants.SensitiveBundleTtl - TimeSpan.FromTicks(1));
        var early = await coordinator.RunOneCycleAsync(CancellationToken.None, CancellationToken.None);
        Assert.Equal(0, early.Completed);
        var readable = await fixture.Store.ReadAsync(Fixture.ReplayId, CancellationToken.None);
        Assert.Equal(RetainedRawReplayReadDisposition.Granted, readable.Disposition);
        await Assert.IsType<RetainedRawReplayLease>(readable.Lease).DisposeAsync();

        fixture.Time.Advance(TimeSpan.FromTicks(1));
        var expired = await coordinator.RunOneCycleAsync(CancellationToken.None, CancellationToken.None);

        Assert.Equal(1, expired.Completed);
        Assert.Equal([3, 4, 5, 6, 7, 1, 2, 8, 0, 9], unlinked);
        fixture.AssertOnlyOwnedChildWasDeleted();
        Assert.Equal("deleted", fixture.Scalar<string>("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.NotNull(fixture.Scalar<string>("SELECT read_denied_at FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(RetainedRawReplayReadDisposition.Denied,
            (await fixture.Store.ReadAsync(Fixture.ReplayId, CancellationToken.None)).Disposition);
        Assert.Equal("{\"resourceSpans\":[]}", fixture.Scalar<string>(
            "SELECT payload_json FROM raw_records WHERE id=$raw;", ("$raw", fixture.LiveRawId)));
    }

    [Fact]
    public async Task Active_raw_replay_operation_lease_excludes_cleanup_until_release()
    {
        using var fixture = await Fixture.CreateAsync();
        var registry = Registry(fixture.Catalog, fixture.Time);
        fixture.Catalog.RegisterAdapterCoverage(registry);
        var (_, expiresAt) = fixture.CaptureWindow();
        var overlap = TimeSpan.FromTicks(RetentionV1Constants.LeaseDuration.Ticks / 2);
        fixture.Time.Advance(RetentionV1Constants.SensitiveBundleTtl - overlap);
        var readAt = fixture.Time.GetUtcNow();
        var retained = await fixture.Store.ReadAsync(Fixture.ReplayId, CancellationToken.None);
        var operation = Assert.IsType<RetainedRawReplayLease>(retained.Lease);
        var leaseExpiresAt = DateTimeOffset.Parse(fixture.Scalar<string>(
            "SELECT expires_at FROM retention_leases WHERE item_id=$item AND lease_kind='operation';",
            ("$item", fixture.ItemId)), CultureInfo.InvariantCulture);
        Assert.Equal(readAt.Add(RetentionV1Constants.LeaseDuration), leaseExpiresAt);
        Assert.True(leaseExpiresAt > expiresAt);
        fixture.Time.Advance(overlap);
        var prepared = await fixture.Catalog.PrepareCleanupBatchAsync(
            fixture.Time.GetUtcNow(),
            RetentionV1Constants.ExpiryScanItemLimit,
            RetentionV1Constants.ClaimBatchLimit,
            RetentionV1Constants.ScanElapsedBudget,
            CancellationToken.None);
        var work = Assert.Single(prepared.Work);
        var blocked = await fixture.Catalog.TryClaimDeletionAsync(
            work,
            "active-operation-probe",
            fixture.Time.GetUtcNow(),
            CancellationToken.None);

        Assert.Equal(RetentionClaimDisposition.Quiescing, blocked.Disposition);
        Assert.Equal(leaseExpiresAt, blocked.QuiescenceRetryAt);
        Assert.Equal("deletion_queued", fixture.Scalar<string>(
            "SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.NotNull(fixture.Scalar<string>(
            "SELECT read_denied_at FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='operation';", ("$item", fixture.ItemId)));
        Assert.Equal(0L, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='deletion';", ("$item", fixture.ItemId)));
        Assert.Equal(0L, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM retention_delete_journal WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.True(Directory.Exists(fixture.FinalChild));
        var denied = await fixture.Store.ReadAsync(Fixture.ReplayId, CancellationToken.None);
        Assert.Equal(RetainedRawReplayReadDisposition.Denied, denied.Disposition);
        Assert.Null(denied.Lease);

        await operation.DisposeAsync();
        var result = await new RetentionCleanupCoordinator(fixture.Catalog, registry, fixture.Time)
            .RunOneCycleAsync(CancellationToken.None, CancellationToken.None);

        Assert.Equal(1, result.Completed);
        fixture.AssertOnlyOwnedChildWasDeleted();
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;", ("$item", fixture.ItemId)));
    }

    [Fact]
    public async Task Partial_member_unlink_recovers_forward_after_restart_without_recreating_the_member()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Time.Advance(RetentionV1Constants.SensitiveBundleTtl);
        var initialRegistry = Registry(fixture.Catalog, fixture.Time);
        fixture.Catalog.RegisterAdapterCoverage(initialRegistry);
        var prepared = await fixture.Catalog.PrepareCleanupBatchAsync(
            fixture.Time.GetUtcNow(),
            RetentionV1Constants.ExpiryScanItemLimit,
            RetentionV1Constants.ClaimBatchLimit,
            RetentionV1Constants.ScanElapsedBudget,
            CancellationToken.None);
        var work = Assert.Single(prepared.Work);
        var claim = (await fixture.Catalog.TryClaimDeletionAsync(
            work,
            "crashing-cleanup",
            fixture.Time.GetUtcNow(),
            CancellationToken.None)).Claim!;
        var intent = await fixture.Catalog.EnsureDeleteIntentAsync(
            claim.Fence,
            claim.IntentCursor,
            fixture.Time.GetUtcNow(),
            CancellationToken.None);
        Assert.Equal(RetentionIntentDisposition.Committed, intent.Disposition);
        var crashing = new SensitiveBundleRetentionAdapter(fixture.Catalog, fixture.Time, checkpoint =>
        {
            if (checkpoint is { Ordinal: 3, Phase: SensitiveBundleRetentionCheckpointPhase.AfterUnlink })
                throw new SimulatedProcessCrashException();
        });
        var context = new RetentionDeleteContext(
            claim.Fence.ItemId,
            claim.StoreInstanceId,
            claim.StoreKind,
            claim.Fence.ExpectedRevision,
            claim.Fence.LeaseOwner,
            claim.Fence.LeaseGeneration,
            claim.SourceIdentity,
            claim.PrivateLocator,
            intent.IntentCursor,
            CancellationToken.None);

        await Assert.ThrowsAsync<SimulatedProcessCrashException>(() => crashing.DeleteAsync(context).AsTask());

        var retainedArchive = Path.Combine(fixture.FinalChild, "input", "archive.zip");
        Assert.False(File.Exists(retainedArchive));
        Assert.Equal(0L, fixture.Scalar<long>(
            "SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal("deleting", fixture.Scalar<string>("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='deletion';", ("$item", fixture.ItemId)));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;", ("$item", fixture.ItemId)));

        fixture.Time.Advance(RetentionV1Constants.LeaseDuration);
        SqliteConnection.ClearAllPools();
        var reopened = new RetentionCatalogStore(
            RetentionCatalogContext.AdoptExistingCatalogV1(fixture.DatabasePath),
            fixture.Time);
        var restartedRegistry = Registry(reopened, fixture.Time);
        reopened.RegisterAdapterCoverage(restartedRegistry);
        var recovered = await new RetentionCleanupCoordinator(reopened, restartedRegistry, fixture.Time)
            .RunOneCycleAsync(CancellationToken.None, CancellationToken.None);

        Assert.Equal(1, recovered.Completed);
        fixture.AssertOnlyOwnedChildWasDeleted();
        Assert.False(File.Exists(retainedArchive));
        Assert.Equal("deleted", fixture.Scalar<string>("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;", ("$item", fixture.ItemId)));
    }

    private static RetentionAdapterRegistry Registry(
        RetentionCatalogStore catalog,
        TimeProvider time,
        Action<SensitiveBundleRetentionCheckpoint>? checkpoint = null) => new([
            new SessionEventContentRetentionAdapter(catalog),
            new RawRecordRetentionAdapter(catalog),
            new MonitorAnalysisRetentionAdapter(catalog),
            new SensitiveBundleRetentionAdapter(catalog, time, checkpoint),
            new AnalysisSdkDirectoryRetentionAdapter(catalog, time),
        ]);

    private sealed class Fixture : IDisposable
    {
        internal const string ReplayId = "cleanup-replay";

        private Fixture(
            string root,
            string databasePath,
            string bundleParent,
            string callerArchive,
            byte[] callerBytes,
            string unrelatedSibling,
            MutableTimeProvider time,
            RetentionCatalogStore catalog,
            RetentionRawReplayStore store,
            string itemId,
            string finalChild,
            long liveRawId)
        {
            Root = root;
            DatabasePath = databasePath;
            BundleParent = bundleParent;
            CallerArchive = callerArchive;
            CallerBytes = callerBytes;
            UnrelatedSibling = unrelatedSibling;
            Time = time;
            Catalog = catalog;
            Store = store;
            ItemId = itemId;
            FinalChild = finalChild;
            LiveRawId = liveRawId;
        }

        internal string Root { get; }
        internal string DatabasePath { get; }
        internal string BundleParent { get; }
        internal string CallerArchive { get; }
        internal byte[] CallerBytes { get; }
        internal string UnrelatedSibling { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal RetentionRawReplayStore Store { get; }
        internal string ItemId { get; }
        internal string FinalChild { get; }
        internal long LiveRawId { get; }
        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"raw-replay-cleanup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "retention.db");
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 4, 5, 6, TimeSpan.Zero));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, time);
            var catalog = new RetentionCatalogStore(context, time);
            var rawStore = new RawTelemetryStore(databasePath, context, time);
            rawStore.CreateMonitorSchema();
            var liveRawId = rawStore.Insert(new RawTelemetryRecord(
                null,
                RawTelemetrySources.RawOtlp,
                "live-trace",
                time.GetUtcNow(),
                null,
                "{\"resourceSpans\":[]}"));
            var bundleParent = Path.Combine(root, "raw-replays");
            Directory.CreateDirectory(bundleParent);
            var unrelatedSibling = Path.Combine(bundleParent, "unrelated-sibling.txt");
            File.WriteAllText(unrelatedSibling, "preserve");
            var callerDirectory = Path.Combine(root, "caller-owned");
            Directory.CreateDirectory(callerDirectory);
            var callerArchive = Path.Combine(callerDirectory, "raw-local-replay.zip");
            var callerBytes = Archive(time.GetUtcNow());
            File.WriteAllBytes(callerArchive, callerBytes);
            var store = new RetentionRawReplayStore(catalog, bundleParent, time);
            var replay = await store.ReplayAsync(ReplayId, File.ReadAllBytes(callerArchive), CancellationToken.None);
            Assert.True(replay.Success, replay.ErrorCode);
            var captureId = RetentionRawReplayStore.CaptureId(ReplayId);
            var itemId = RawReplayRetentionLifecycleTests.Scalar<string>(databasePath,
                "SELECT item_id FROM retention_items WHERE store_kind='sensitive_bundle' AND source_item_id=$capture;",
                ("$capture", captureId));
            return new(root, databasePath, bundleParent, callerArchive, callerBytes, unrelatedSibling, time, catalog, store,
                itemId, Path.Combine(bundleParent, captureId), liveRawId);
        }

        internal (DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt) CaptureWindow() => (
            DateTimeOffset.Parse(Scalar<string>("SELECT captured_at FROM retention_items WHERE item_id=$item;", ("$item", ItemId)), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(Scalar<string>("SELECT expires_at FROM retention_items WHERE item_id=$item;", ("$item", ItemId)), CultureInfo.InvariantCulture));

        internal void AssertOnlyOwnedChildWasDeleted()
        {
            Assert.False(Directory.Exists(FinalChild));
            Assert.True(Directory.Exists(BundleParent));
            Assert.True(File.Exists(UnrelatedSibling));
            Assert.Equal("preserve", File.ReadAllText(UnrelatedSibling));
            Assert.True(File.Exists(CallerArchive));
            Assert.Equal(CallerBytes, File.ReadAllBytes(CallerArchive));
        }

        internal T Scalar<T>(string sql, params (string Name, object Value)[] parameters) =>
            RawReplayRetentionLifecycleTests.Scalar<T>(DatabasePath, sql, parameters);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static byte[] Archive(DateTimeOffset now)
    {
        var service = new RawReplayArchiveService();
        var snapshot = new RawReplaySnapshot(
            "cleanup-snapshot",
            now,
            "monitor-v1",
            [new RawReplayRecord(1, "raw-otlp", "trace-one", now, null,
                "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"trace-one\",\"spanId\":\"span-one\"}]}]}]}",
                1,
                new("copilot-cli", "1", "otlp-json", "adapter-v1", "schema-v1", new string('a', 64),
                    "supported", "available", "not_applied_raw_capture", RawReplayContractVersions.CredentialScanner))],
            [],
            ["session_content_not_requested"]);
        var control = new RawReplayExportControl(
            RawReplayContractVersions.ExportControl,
            RawReplayContractVersions.BundleProfile,
            now,
            new(RawRecordIds: [1]),
            false,
            false,
            null,
            null);
        var preview = service.Preview(snapshot, control);
        var created = service.Create(snapshot, control with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        });
        Assert.True(created.Success, created.ErrorCode);
        return created.ArchiveBytes!;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private sealed class SimulatedProcessCrashException : Exception;
}

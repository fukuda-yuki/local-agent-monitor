using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class AnalysisSdkDirectoryRetentionTests
{
    [Fact]
    public async Task OpenAsync_ReservesBeforeCreatingTheConfiguredParentAndCreatesOnlyTheOwnedChildMarker()
    {
        using var fixture = Fixture.Create();
        Assert.False(Directory.Exists(fixture.Parent));

        await using var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        Assert.True(Directory.Exists(fixture.Parent));
        Assert.True(Directory.Exists(scope.ChildDirectory));
        Assert.Equal(scope.ChildDirectory, Path.Combine(fixture.Parent, fixture.CaptureId()));
        Assert.Equal(new[] { AnalysisSdkDirectoryOwner.MarkerFileName }, Directory.EnumerateFileSystemEntries(scope.ChildDirectory).Select(Path.GetFileName));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_PreservesAnExistingGeneratedChildAndAbandonsTheReservation()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        File.WriteAllText(Path.Combine(reservation.ChildLocator, "unrelated.txt"), "keep");

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(reservation.ChildLocator, "unrelated.txt")));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_CancellationAfterReservationAbandonsWithoutCreatingTheParent()
    {
        using var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var owner = new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time, cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await owner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, cancellation.Token));

        Assert.False(Directory.Exists(fixture.Parent));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_RecoversOnlyTheReservationBoundMarkerOnlyChild()
    {
        using var fixture = Fixture.Create();
        var reservation = fixture.Catalog.ReserveAnalysisSdkDirectory(7, fixture.Parent);
        Directory.CreateDirectory(reservation.ChildLocator);
        await using (var stream = new FileStream(Path.Combine(reservation.ChildLocator, AnalysisSdkDirectoryOwner.MarkerFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(reservation.OwnershipMarker);
            stream.Flush(flushToDisk: true);
        }

        await using var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        Assert.Equal(reservation.ChildLocator, scope.ChildDirectory);
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_ActivationFailureDeletesTheExactOwnedChildBeforeAbandoning()
    {
        using var fixture = Fixture.Create();
        fixture.Time.Advance(TimeSpan.FromDays(91));

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(Directory.Exists(fixture.Parent));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Parent));
        Assert.Null(fixture.Scalar<object?>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_UnexpectedEntryDuringOwnedChildCleanupRetainsTheReservation()
    {
        using var fixture = Fixture.Create();
        var owner = new AnalysisSdkDirectoryOwner(
            fixture.Catalog,
            fixture.Time,
            childCreatedCheckpoint: child => File.WriteAllText(Path.Combine(child, "replacement.txt"), "keep"));

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await owner.OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(fixture.Parent, fixture.CaptureId(), "replacement.txt")));
        Assert.Equal("reserved", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
    }

    [Fact]
    public async Task OpenAsync_TimerConstructionFailurePreservesTheActiveOwnedChildAndReleasesTheOperationLease()
    {
        using var fixture = Fixture.Create();
        var time = new ThrowingTimerTimeProvider(fixture.Time.GetUtcNow());

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(Directory.Exists(Path.Combine(fixture.Parent, fixture.CaptureId())));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task OpenAsync_ActiveDuplicatePreservesTheLiveMarkerOnlyChildAndOperationLease()
    {
        using var fixture = Fixture.Create();
        await using var first = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var marker = Path.Combine(first.ChildDirectory, AnalysisSdkDirectoryOwner.MarkerFileName);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () =>
            await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
                .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None));

        Assert.True(File.Exists(marker));
        Assert.Equal("active", fixture.Scalar<string>("SELECT phase FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7"));
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task Scope_ThrowingTimerDisposalStillReleasesTheOperationLease()
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, new ThrowingTimerDisposeTimeProvider(fixture.Time.GetUtcNow()))
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);

        await Assert.ThrowsAsync<AnalysisOwnershipException>(async () => await scope.DisposeAsync());

        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task Scope_RenewsOnTheFixedTimerAndReleasesTheOperationLeaseOnce()
    {
        using var fixture = Fixture.Create();
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, fixture.Time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        var initialExpiry = fixture.Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'");

        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.NotEqual(initialExpiry, fixture.Scalar<string>("SELECT expires_at FROM retention_leases WHERE lease_kind='operation'"));
        await scope.DisposeAsync();
        await scope.DisposeAsync();
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    [Fact]
    public async Task Scope_DisposeWaitsForAnInFlightRenewalBeforeReleasing()
    {
        using var fixture = Fixture.Create();
        var time = new GatedTimerTimeProvider(fixture.RequestedAt.AddDays(1));
        var scope = await new AnalysisSdkDirectoryOwner(fixture.Catalog, time)
            .OpenAsync(7, fixture.RequestedAt, fixture.Parent, CancellationToken.None);
        time.BeginCallback();
        await time.CallbackStarted.Task;

        var dispose = scope.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.Equal(1L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));

        time.AllowCallback.SetResult();
        await dispose;
        Assert.Equal(0L, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation'"));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string databasePath, string parent, RetentionCatalogStore catalog, MutableTimeProvider time)
            => (DatabasePath, Parent, Catalog, Time) = (databasePath, parent, catalog, time);

        internal string DatabasePath { get; }
        internal string Parent { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal MutableTimeProvider Time { get; }
        internal DateTimeOffset RequestedAt => new(2026, 7, 19, 1, 2, 3, TimeSpan.Zero);

        internal static Fixture Create()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"analysis-sdk-directory-owner-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, time);
            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE monitor_analysis_runs(id INTEGER PRIMARY KEY, requested_at TEXT NOT NULL, retention_owner_token BLOB NOT NULL); INSERT INTO monitor_analysis_runs(id,requested_at,retention_owner_token) VALUES(7,'2026-07-19T01:02:03.0000000+00:00',zeroblob(32));";
                command.ExecuteNonQuery();
            }
            return new Fixture(databasePath, Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"sdk-owner-parent-{Guid.NewGuid():N}")), new RetentionCatalogStore(context, time), time);
        }

        internal string CaptureId() => Scalar<string>("SELECT capture_id FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=7");
        internal T Scalar<T>(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value is null || value is DBNull ? default! : (T)Convert.ChangeType(value, typeof(T));
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Parent)) Directory.Delete(Parent, recursive: true);
            foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" }) if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class GatedTimerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly GatedTimer timer = new();
        public override DateTimeOffset GetUtcNow() => now;
        public TaskCompletionSource CallbackStarted => timer.CallbackStarted;
        public TaskCompletionSource AllowCallback => timer.AllowCallback;
        public void BeginCallback() => timer.BeginCallback();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer.SetCallback(callback, state);
            return timer;
        }

        private sealed class GatedTimer : ITimer
        {
            private TimerCallback? callback;
            private object? state;
            private Task? callbackTask;
            internal TaskCompletionSource CallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource AllowCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void SetCallback(TimerCallback value, object? valueState) => (callback, state) = (value, valueState);
            internal void BeginCallback() => callbackTask = Task.Run(async () =>
            {
                CallbackStarted.SetResult();
                await AllowCallback.Task;
                callback!(state);
            });
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public async ValueTask DisposeAsync() { if (callbackTask is not null) await callbackTask; }
        }
    }

    private sealed class ThrowingTimerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => throw new InvalidOperationException();
    }

    private sealed class ThrowingTimerDisposeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new ThrowingTimer();

        private sealed class ThrowingTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException());
        }
    }
}

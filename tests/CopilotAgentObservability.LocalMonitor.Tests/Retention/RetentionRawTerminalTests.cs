using CopilotAgentObservability.LocalMonitor.Tests;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionRawTerminalTests
{
    [Fact]
    public async Task OnlyTerminalCasWinnerCanPublish()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        using var claimed = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        fixture.Checkpoint.On(RetentionRawTerminalCheckpoint.AfterClaimBeforeTransaction, () =>
        {
            claimed.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        });

        var winner = BlockingTestActor.Start(fixture.SingleLease.TrySealRawResponse);
        await winner.Entered;
        Assert.True(claimed.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(RetentionRawTerminalResult.Lost, fixture.SingleLease.TryCompleteWithoutRaw());

        resume.Set();
        Assert.Equal(RetentionRawTerminalResult.Sealed, await winner.Completion);
        Assert.Equal(RetentionRawTerminalState.Sealed, fixture.SingleLease.TerminalState);
    }

    [Fact]
    public async Task RawReplayTransientSealUsesTheExistingOneShotTerminalClaim()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();

        Assert.Equal(
            RetentionRawTerminalResult.Sealed,
            fixture.SingleLease.TrySealRawReplayTransientPublication());
        Assert.Equal(RetentionRawTerminalState.Sealed, fixture.SingleLease.TerminalState);
        Assert.Equal(1L, fixture.LeaseCount());
        Assert.Equal(
            RetentionRawTerminalResult.Lost,
            fixture.SingleLease.TrySealRawReplayTransientPublication());
        Assert.Equal(
            RetentionRawTerminalResult.Lost,
            fixture.SingleLease.TryCompleteWithoutRaw());
    }

    [Fact]
    public async Task RawReplayFileSealUsesTheExistingOneShotTerminalClaim()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();

        Assert.Equal(
            RetentionRawTerminalResult.Sealed,
            fixture.SingleLease.TrySealRawReplayFilePublication());
        Assert.Equal(RetentionRawTerminalState.Sealed, fixture.SingleLease.TerminalState);
        Assert.Equal(1L, fixture.LeaseCount());
        Assert.Equal(
            RetentionRawTerminalResult.Lost,
            fixture.SingleLease.TrySealRawReplayFilePublication());
        Assert.Equal(
            RetentionRawTerminalResult.Lost,
            fixture.SingleLease.TrySealRawResponse());
    }

    [Fact]
    public async Task TerminalClaimPrecedesTransactionAndPublicationScopes()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        fixture.Checkpoint.On(RetentionRawTerminalCheckpoint.AfterClaimBeforeTransaction, () =>
        {
            AssertImmediateTransactionCanBegin(fixture.Path);
            AssertPublicationScopeCanBeEntered(fixture.SingleLease.Grant);
        });

        Assert.Equal(RetentionRawTerminalResult.Sealed, fixture.SingleLease.TrySealRawResponse());
    }

    [Fact]
    public async Task TerminalOrdersTransactionScopesClockProofPendingCommitAndPublish()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        fixture.Time.ResetReadCount();

        Assert.Equal(RetentionRawTerminalResult.Sealed, fixture.SingleLease.TrySealRawResponse());

        Assert.Equal(
            [
                RetentionRawTerminalCheckpoint.AfterClaimBeforeTransaction,
                RetentionRawTerminalCheckpoint.AfterTransactionBeganBeforePublicationScopes,
                RetentionRawTerminalCheckpoint.AfterPublicationScopesAcquiredBeforeClockSample,
                RetentionRawTerminalCheckpoint.AfterClockSampleBeforeProof,
                RetentionRawTerminalCheckpoint.AfterProofBeforeStateMove,
                RetentionRawTerminalCheckpoint.AfterStateMoveBeforeCommit,
                RetentionRawTerminalCheckpoint.AfterCommitBeforePublish,
                RetentionRawTerminalCheckpoint.AfterPublish,
            ],
            fixture.Checkpoint.Observed);
        Assert.Equal(1, fixture.Time.ReadCount);
    }

    [Fact]
    public async Task TerminalAtPublishedExpiryLosesAndPublishesNothing()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        fixture.Time.Set(fixture.SingleLease.Grant.LeaseExpiresAt);

        Assert.Equal(RetentionRawTerminalResult.Lost, fixture.SingleLease.TrySealRawResponse());
        Assert.Equal(RetentionRawTerminalState.Lost, fixture.SingleLease.TerminalState);
        Assert.Equal(1L, fixture.LeaseCount());
        Assert.True(fixture.SingleLease.IsValueBufferCleared);
    }

    [Fact]
    public async Task CancellationAfterHandoverLosesAndDrainsValueBuffer()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await TerminalFixture.CreateSingleAsync(cancellation.Token);
        using (fixture.SingleLease.AcquireValueReference()) { }

        var cancellationActor = BlockingTestActor.Start(() =>
        {
            cancellation.Cancel();
            Assert.True(SpinWait.SpinUntil(
                () => fixture.SingleLease.TerminalState == RetentionRawTerminalState.Lost
                    && fixture.SingleLease.IsValueBufferCleared,
                TimeSpan.FromSeconds(5)));
        });
        await cancellationActor.Entered;
        await cancellationActor.Completion;
        Assert.Throws<InvalidOperationException>(() => fixture.SingleLease.AcquireValueReference());
        Assert.Equal(RetentionRawTerminalResult.Lost, fixture.SingleLease.TrySealRawResponse());
    }

    [Fact]
    public async Task MandatoryCleanupDeletesExactLeaseWhileReferenceRemainsOutstanding()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await TerminalFixture.CreateSingleAsync(cancellation.Token);
        var use = fixture.SingleLease.AcquireValueReference();
        try
        {
            var cancellationActor = BlockingTestActor.Start(() =>
            {
                cancellation.Cancel();
                Assert.True(SpinWait.SpinUntil(
                    () => fixture.LeaseCount() == 0,
                    TimeSpan.FromSeconds(5)));
            });
            await cancellationActor.Entered;
            await cancellationActor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.SingleLease.IsValueBufferCleared);
            Assert.Equal("buffered", use.Value);
        }
        finally
        {
            use.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalClaimWhileReferenceRemainsOutstandingFailsLost(bool completeWithoutRaw)
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        var use = fixture.SingleLease.AcquireValueReference();
        try
        {
            Func<RetentionRawTerminalResult> terminal = completeWithoutRaw
                ? fixture.SingleLease.TryCompleteWithoutRaw
                : fixture.SingleLease.TrySealRawResponse;
            Assert.Equal(
                RetentionRawTerminalResult.Lost,
                await Task.Run(terminal).WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Throws<InvalidOperationException>(() => fixture.SingleLease.AcquireValueReference());
            Assert.Equal("buffered", use.Value);
        }
        finally
        {
            use.Dispose();
        }
    }

    [Fact]
    public async Task OutstandingValueReferenceKeepsBufferAliveAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await TerminalFixture.CreateSingleAsync(cancellation.Token);
        using var reference = fixture.SingleLease.AcquireValueReference();

        Assert.Equal("buffered", reference.Value);
        var cancellationActor = BlockingTestActor.Start(() =>
        {
            cancellation.Cancel();
            Assert.True(SpinWait.SpinUntil(
                () => fixture.SingleLease.TerminalState == RetentionRawTerminalState.Lost
                    && fixture.LeaseCount() == 0,
                TimeSpan.FromSeconds(5)));
        });
        await cancellationActor.Entered;
        await cancellationActor.Completion;

        Assert.Equal("buffered", reference.Value);
        Assert.False(fixture.SingleLease.IsValueBufferCleared);
    }

    [Fact]
    public async Task CancellationAfterSealCannotReleaseBeforeFinalDispose()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await TerminalFixture.CreateSingleAsync(cancellation.Token);

        Assert.Equal(RetentionRawTerminalResult.Sealed, fixture.SingleLease.TrySealRawResponse());
        cancellation.Cancel();

        await Task.Delay(100);
        Assert.Equal(RetentionRawTerminalState.Sealed, fixture.SingleLease.TerminalState);
        Assert.Equal(1L, fixture.LeaseCount());
        await fixture.SingleLease.DisposeAsync();
        Assert.Equal(0L, fixture.LeaseCount());
    }

    [Fact]
    public async Task CancellationBeforeTerminalPublishWinsLost()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await TerminalFixture.CreateSingleAsync(cancellation.Token);
        using var pending = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        fixture.Checkpoint.On(RetentionRawTerminalCheckpoint.AfterStateMoveBeforeCommit, () =>
        {
            pending.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        });

        var terminal = BlockingTestActor.Start(fixture.SingleLease.TrySealRawResponse);
        await terminal.Entered;
        Assert.True(pending.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        resume.Set();

        Assert.Equal(RetentionRawTerminalResult.Lost, await terminal.Completion);
        Assert.Equal(RetentionRawTerminalState.Lost, fixture.SingleLease.TerminalState);
    }

    [Theory]
    [InlineData((int)RetentionRawTerminalCheckpoint.AfterClaimBeforeTransaction)]
    [InlineData((int)RetentionRawTerminalCheckpoint.AfterClockSampleBeforeProof)]
    [InlineData((int)RetentionRawTerminalCheckpoint.AfterStateMoveBeforeCommit)]
    public async Task TransactionFailureIsBusyAndIrreversiblyFailed(
        int failurePointValue)
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        var failurePoint = (RetentionRawTerminalCheckpoint)failurePointValue;
        fixture.Checkpoint.FailAt(failurePoint);

        Assert.Equal(RetentionRawTerminalResult.Busy, fixture.SingleLease.TryCompleteWithoutRaw());
        Assert.Equal(RetentionRawTerminalState.Failed, fixture.SingleLease.TerminalState);
        Assert.Equal(1L, fixture.LeaseCount());
        Assert.Equal(RetentionRawTerminalResult.Busy, fixture.SingleLease.TrySealRawResponse());
        Assert.Equal(
            RetentionOperationRenewalDisposition.LeaseLost,
            fixture.Store.RenewOperationLease(fixture.SingleLease.Grant));
        Assert.Throws<InvalidOperationException>(() => fixture.SingleLease.AcquireValueReference());
    }

    [Theory]
    [InlineData((int)RetentionRawTerminalCheckpoint.AfterClaimBeforeTransaction)]
    [InlineData((int)RetentionRawTerminalCheckpoint.AfterStateMoveBeforeCommit)]
    public async Task NonSqliteTransactionFailureIsBusyAndIrreversiblyFailed(
        int failurePointValue)
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        var failurePoint = (RetentionRawTerminalCheckpoint)failurePointValue;
        fixture.Checkpoint.ThrowAt(failurePoint, new InvalidOperationException("injected terminal failure"));

        Assert.Equal(RetentionRawTerminalResult.Busy, fixture.SingleLease.TryCompleteWithoutRaw());
        Assert.Equal(RetentionRawTerminalState.Failed, fixture.SingleLease.TerminalState);
        Assert.Equal(1L, fixture.LeaseCount());
        Assert.Equal(RetentionRawTerminalResult.Busy, fixture.SingleLease.TrySealRawResponse());
        Assert.Equal(
            RetentionOperationRenewalDisposition.LeaseLost,
            fixture.Store.RenewOperationLease(fixture.SingleLease.Grant));
        Assert.Throws<InvalidOperationException>(() => fixture.SingleLease.AcquireValueReference());
    }

    [Fact]
    public async Task CancellationInsideTerminalTransactionIsLostRatherThanBusy()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await TerminalFixture.CreateSingleAsync(cancellation.Token);
        fixture.Checkpoint.On(
            RetentionRawTerminalCheckpoint.AfterStateMoveBeforeCommit,
            cancellation.Cancel);

        Assert.Equal(RetentionRawTerminalResult.Lost, fixture.SingleLease.TrySealRawResponse());
        Assert.Equal(RetentionRawTerminalState.Lost, fixture.SingleLease.TerminalState);
    }

    [Fact]
    public async Task RenewalCommittedBeforeClaimIsVisibleToTerminalProof()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        fixture.Time.Set(fixture.SingleLease.Grant.LeaseExpiresAt - TimeSpan.FromSeconds(30));
        using var renewalCommitted = new ManualResetEventSlim();
        using var publishRenewal = new ManualResetEventSlim();
        fixture.Checkpoint.On(RetentionRawTerminalCheckpoint.RenewalCommittedBeforePublication, () =>
        {
            renewalCommitted.Set();
            Assert.True(publishRenewal.Wait(TimeSpan.FromSeconds(5)));
        });

        var renewal = BlockingTestActor.Start(() => fixture.Store.RenewOperationLease(fixture.SingleLease.Grant));
        await renewal.Entered;
        Assert.True(renewalCommitted.Wait(TimeSpan.FromSeconds(5)));
        var terminal = BlockingTestActor.Start(fixture.SingleLease.TrySealRawResponse);
        await terminal.Entered;
        Assert.True(SpinWait.SpinUntil(
            () => fixture.Checkpoint.Contains(RetentionRawTerminalCheckpoint.AfterClaimBeforeTransaction),
            TimeSpan.FromSeconds(5)));
        publishRenewal.Set();

        Assert.Equal(RetentionOperationRenewalDisposition.Renewed, await renewal.Completion);
        var renewedExpiry = fixture.DatabaseExpiry(0);
        Assert.True(renewedExpiry > fixture.SingleLease.Grant.LeaseExpiresAt);

        Assert.Equal(RetentionRawTerminalResult.Sealed, await terminal.Completion);
        Assert.Equal(renewedExpiry, fixture.DatabaseExpiry(0));
    }

    [Fact]
    public async Task RenewalAfterClaimIsDeniedWithoutPublication()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        fixture.Time.Set(fixture.SingleLease.Grant.LeaseExpiresAt - TimeSpan.FromSeconds(30));
        var originalExpiry = fixture.DatabaseExpiry(0);
        using var claimed = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        fixture.Checkpoint.On(RetentionRawTerminalCheckpoint.AfterClaimBeforeTransaction, () =>
        {
            claimed.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        });

        var terminal = BlockingTestActor.Start(fixture.SingleLease.TrySealRawResponse);
        await terminal.Entered;
        Assert.True(claimed.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            RetentionOperationRenewalDisposition.LeaseLost,
            fixture.Store.RenewOperationLease(fixture.SingleLease.Grant));
        Assert.Equal(originalExpiry, fixture.DatabaseExpiry(0));
        resume.Set();
        Assert.Equal(RetentionRawTerminalResult.Sealed, await terminal.Completion);
    }

    [Fact]
    public async Task SealRetainsLeaseUntilExactlyOnceFinalRelease()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        fixture.InstallDeleteJournal();

        Assert.Equal(RetentionRawTerminalResult.Sealed, fixture.SingleLease.TrySealRawResponse());
        Assert.Equal(1L, fixture.LeaseCount());
        Assert.Throws<InvalidOperationException>(() => fixture.SingleLease.AcquireValueReference());

        await fixture.SingleLease.DisposeAsync();
        await fixture.SingleLease.DisposeAsync();
        Assert.Equal(0L, fixture.LeaseCount());
        Assert.Equal([fixture.SingleLease.Grant.ItemId], fixture.DeleteJournal());
    }

    [Fact]
    public async Task CompleteDeletesLeaseInsideTerminalTransaction()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();

        Assert.Equal(
            RetentionRawTerminalResult.CompletedWithoutRaw,
            fixture.SingleLease.TryCompleteWithoutRaw());
        Assert.Equal(RetentionRawTerminalState.CompletedWithoutRaw, fixture.SingleLease.TerminalState);
        Assert.Equal(0L, fixture.LeaseCount());
    }

    [Fact]
    public async Task LastReferenceReleaseZeroesBufferAndPostDrainAccessIsContradiction()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        var use = fixture.SingleLease.AcquireValueReference();
        Assert.Equal("buffered", use.Value);
        Assert.False(fixture.SingleLease.IsValueBufferCleared);
        use.Dispose();
        Assert.Equal(
            RetentionRawTerminalResult.Sealed,
            await Task.Run(fixture.SingleLease.TrySealRawResponse)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(SpinWait.SpinUntil(
            () => fixture.SingleLease.IsValueBufferCleared,
            TimeSpan.FromSeconds(5)));
        Assert.Throws<ObjectDisposedException>(() => _ = use.Value);
        Assert.Throws<InvalidOperationException>(() => fixture.SingleLease.AcquireValueReference());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulTerminalRefusesPreviouslyAcquiredReference(bool completeWithoutRaw)
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        var reference = fixture.SingleLease.AcquireValueReference();
        reference.Dispose();

        var terminal = completeWithoutRaw
            ? fixture.SingleLease.TryCompleteWithoutRaw()
            : fixture.SingleLease.TrySealRawResponse();

        Assert.Equal(
            completeWithoutRaw
                ? RetentionRawTerminalResult.CompletedWithoutRaw
                : RetentionRawTerminalResult.Sealed,
            terminal);
        Assert.Throws<ObjectDisposedException>(() => _ = reference.Value);
        Assert.Throws<InvalidOperationException>(() => fixture.SingleLease.AcquireValueReference());
    }

    [Fact]
    public async Task ExpiryCallbackDoesNotDrainOutstandingReference()
    {
        await using var fixture = await TerminalFixture.CreateSingleAsync();
        var use = fixture.SingleLease.AcquireValueReference();
        try
        {
            fixture.Time.Set(fixture.SingleLease.Grant.LeaseExpiresAt);

            var expiryActor = BlockingTestActor.Start(() =>
            {
                fixture.Time.FireScheduled();
                Assert.True(SpinWait.SpinUntil(
                    () => fixture.LeaseCount() == 0,
                    TimeSpan.FromSeconds(5)));
            });
            await expiryActor.Entered;
            await expiryActor.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(fixture.SingleLease.IsValueBufferCleared);
            Assert.Equal("buffered", use.Value);
        }
        finally
        {
            use.Dispose();
        }
    }

    [Fact]
    public async Task CompositeCompletionPublishesAndDeletesEveryMemberAllOrNone()
    {
        await using var fixture = await TerminalFixture.CreateBatchAsync();

        Assert.Equal(
            RetentionRawTerminalResult.CompletedWithoutRaw,
            fixture.BatchLease.TryCompleteWithoutRaw());
        Assert.Equal(0L, fixture.LeaseCount());
        Assert.Equal(RetentionRawTerminalState.CompletedWithoutRaw, fixture.BatchLease.TerminalState);
    }

    [Fact]
    public async Task CompositeMemberLossLosesWholeEntityAndFinalReleaseUsesSemanticOrderOnce()
    {
        await using var fixture = await TerminalFixture.CreateBatchAsync();
        fixture.DeleteLease(fixture.BatchLease.Grants[1]);
        fixture.InstallDeleteJournal();

        Assert.Equal(RetentionRawTerminalResult.Lost, fixture.BatchLease.TrySealRawResponse());
        Assert.Equal(RetentionRawTerminalState.Lost, fixture.BatchLease.TerminalState);
        Assert.True(fixture.BatchLease.IsValueBufferCleared);

        fixture.RestoreLease(fixture.BatchLease.Grants[1]);
        await fixture.BatchLease.DisposeAsync();
        await fixture.BatchLease.DisposeAsync();
        Assert.Equal(0L, fixture.LeaseCount());
        Assert.Equal(
            fixture.BatchLease.Grants.Select(grant => grant.ItemId),
            fixture.DeleteJournal());
    }

    private static void AssertImmediateTransactionCanBegin(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction(deferred: false);
        transaction.Rollback();
    }

    private static void AssertPublicationScopeCanBeEntered(RetentionReadGrant grant)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { using var publication = grant.EnterLeasePublication(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=0");
        connection.Open();
        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=0;";
        timeout.ExecuteNonQuery();
        return connection;
    }

    private sealed class TerminalFixture : IAsyncDisposable
    {
        private TerminalFixture(
            string path,
            MutableTerminalTimeProvider time,
            RecordingTerminalCheckpoint checkpoint,
            RetentionCatalogStore store,
            RetentionReadLease<string>? singleLease,
            RetentionBatchReadLease<string>? batchLease)
        {
            Path = path;
            Time = time;
            Checkpoint = checkpoint;
            Store = store;
            SingleLease = singleLease!;
            BatchLease = batchLease!;
        }

        internal string Path { get; }
        internal MutableTerminalTimeProvider Time { get; }
        internal RecordingTerminalCheckpoint Checkpoint { get; }
        internal RetentionCatalogStore Store { get; }
        internal RetentionReadLease<string> SingleLease { get; }
        internal RetentionBatchReadLease<string> BatchLease { get; }

        internal static async Task<TerminalFixture> CreateSingleAsync(CancellationToken cancellationToken = default)
        {
            var (path, time, checkpoint, store, keys) = Create(1);
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(keys[0]));
            var result = await store.ReadAsync(
                new RetentionReadRequest(keys[0], RetentionReadKind.Operation, time.GetUtcNow(), item.Revision),
                (_, _, _, _) => ValueTask.FromResult<string?>("buffered"),
                cancellationToken);
            checkpoint.Clear();
            return new(path, time, checkpoint, store, Assert.IsType<RetentionReadLease<string>>(result.Lease), null);
        }

        internal static async Task<TerminalFixture> CreateBatchAsync()
        {
            var (path, time, checkpoint, store, keys) = Create(2);
            var requests = keys.Select(key =>
            {
                var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
                return new RetentionReadRequest(key, RetentionReadKind.Operation, time.GetUtcNow(), item.Revision);
            }).ToArray();
            var result = await store.ReadBatchAsync(
                requests,
                (_, _, _, _) => ValueTask.FromResult<string?>("batch-buffered"),
                CancellationToken.None);
            checkpoint.Clear();
            return new(path, time, checkpoint, store, null, Assert.IsType<RetentionBatchReadLease<string>>(result.Lease));
        }

        private static (string Path, MutableTerminalTimeProvider Time, RecordingTerminalCheckpoint Checkpoint, RetentionCatalogStore Store, RetentionOwnershipKey[] Keys) Create(int count)
        {
            var source = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "monitor", "monitor-v5.sqlite");
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-terminal-{Guid.NewGuid():N}.sqlite");
            File.Copy(source, path);
            if (count > 1)
            {
                using var connection = Open(path);
                using var clone = connection.CreateCommand();
                clone.CommandText =
                    "INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version) " +
                    "SELECT (SELECT MAX(id)+1 FROM raw_records),source,trace_id || '-terminal-member',received_at,resource_attributes_json,payload_json,schema_version FROM raw_records ORDER BY id LIMIT 1;";
                clone.ExecuteNonQuery();
            }
            var now = ReadCapturedAt(path);
            var time = new MutableTerminalTimeProvider(now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            using (var connection = Open(path))
            using (var coverage = connection.CreateCommand())
            {
                coverage.CommandText =
                    "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES " +
                    "('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);";
                coverage.ExecuteNonQuery();
            }
            var checkpoint = new RecordingTerminalCheckpoint();
            var store = new RetentionCatalogStore(context, time, checkpoint);
            var ids = ReadIds(path).Take(count).ToArray();
            var keys = ids.Select(id => new RetentionOwnershipKey(context.StoreInstanceId, RetentionStoreKind.RawRecord, id.ToString())).ToArray();
            return (path, time, checkpoint, store, keys);
        }

        internal long LeaseCount()
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM retention_leases;";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal DateTimeOffset DatabaseExpiry(int semanticIndex)
        {
            var grant = SingleLease is not null ? SingleLease.Grant : BatchLease.Grants[semanticIndex];
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT expires_at FROM retention_leases WHERE item_id=$item_id AND lease_kind=$kind AND owner=$owner AND generation=$generation;";
            command.Parameters.AddWithValue("$item_id", grant.ItemId);
            command.Parameters.AddWithValue("$kind", grant.LeaseKind.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$owner", grant.LeaseOwner);
            command.Parameters.AddWithValue("$generation", grant.LeaseGeneration);
            return DateTimeOffset.Parse((string)command.ExecuteScalar()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        }

        internal void DeleteLease(RetentionReadGrant grant)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM retention_leases WHERE item_id=$item_id AND lease_kind=$kind AND owner=$owner AND generation=$generation;";
            command.Parameters.AddWithValue("$item_id", grant.ItemId);
            command.Parameters.AddWithValue("$kind", grant.LeaseKind.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$owner", grant.LeaseOwner);
            command.Parameters.AddWithValue("$generation", grant.LeaseGeneration);
            command.ExecuteNonQuery();
        }

        internal void RestoreLease(RetentionReadGrant grant)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item_id,$kind,$owner,$expiry,$generation);";
            command.Parameters.AddWithValue("$item_id", grant.ItemId);
            command.Parameters.AddWithValue("$kind", grant.LeaseKind.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$owner", grant.LeaseOwner);
            command.Parameters.AddWithValue("$expiry", grant.LeaseExpiresAt.ToString("O"));
            command.Parameters.AddWithValue("$generation", grant.LeaseGeneration);
            command.ExecuteNonQuery();
        }

        internal void InstallDeleteJournal()
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE terminal_delete_journal(ordinal INTEGER PRIMARY KEY AUTOINCREMENT, item_id TEXT NOT NULL); " +
                "CREATE TRIGGER terminal_delete_observer AFTER DELETE ON retention_leases BEGIN INSERT INTO terminal_delete_journal(item_id) VALUES(OLD.item_id); END;";
            command.ExecuteNonQuery();
        }

        internal string[] DeleteJournal()
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_id FROM terminal_delete_journal ORDER BY ordinal;";
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (SingleLease is not null) await SingleLease.DisposeAsync();
            if (BatchLease is not null) await BatchLease.DisposeAsync();
            foreach (var candidate in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }

        private static DateTimeOffset ReadCapturedAt(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT received_at FROM raw_records ORDER BY id LIMIT 1;";
            return DateTimeOffset.Parse((string)command.ExecuteScalar()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        }

        private static long[] ReadIds(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM raw_records ORDER BY id;";
            using var reader = command.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            return ids.ToArray();
        }
    }

    private sealed class RecordingTerminalCheckpoint : IRetentionRawTerminalCheckpoint
    {
        private readonly object gate = new();
        private readonly Dictionary<RetentionRawTerminalCheckpoint, Action> actions = [];
        private readonly Dictionary<RetentionRawTerminalCheckpoint, Exception> failures = [];

        private List<RetentionRawTerminalCheckpoint> observed = [];
        internal IReadOnlyList<RetentionRawTerminalCheckpoint> Observed { get { lock (gate) return observed.ToArray(); } }
        internal bool Contains(RetentionRawTerminalCheckpoint checkpoint) { lock (gate) return observed.Contains(checkpoint); }
        internal void Clear() { lock (gate) observed.Clear(); }
        internal void On(RetentionRawTerminalCheckpoint checkpoint, Action action) { lock (gate) actions[checkpoint] = action; }
        internal void FailAt(RetentionRawTerminalCheckpoint checkpoint) =>
            ThrowAt(checkpoint, new RetentionRawTerminalBusyException());
        internal void ThrowAt(RetentionRawTerminalCheckpoint checkpoint, Exception exception)
        {
            lock (gate) failures[checkpoint] = exception;
        }

        public void Reached(RetentionRawTerminalCheckpoint checkpoint)
        {
            Action? action;
            Exception? failure;
            bool fail;
            lock (gate)
            {
                observed.Add(checkpoint);
                fail = failures.TryGetValue(checkpoint, out failure);
                actions.TryGetValue(checkpoint, out action);
            }
            if (fail) throw failure!;
            action?.Invoke();
        }
    }

    private sealed class MutableTerminalTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private int readCount;
        internal int ReadCount => Volatile.Read(ref readCount);
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref readCount);
            return now;
        }
        internal void Set(DateTimeOffset value) => now = value;
        internal void ResetReadCount() => Volatile.Write(ref readCount, 0);
        internal void FireScheduled()
        {
            ManualTimer timer;
            lock (gate) timer = Assert.Single(timers, candidate => candidate.IsScheduled);
            timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            timer.Change(dueTime, period);
            lock (gate) timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int disposed;
            internal bool IsScheduled { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref disposed) != 0) return false;
                IsScheduled = dueTime != Timeout.InfiniteTimeSpan;
                return true;
            }
            internal void Fire()
            {
                IsScheduled = false;
                callback(state);
            }
            public void Dispose()
            {
                Volatile.Write(ref disposed, 1);
                IsScheduled = false;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

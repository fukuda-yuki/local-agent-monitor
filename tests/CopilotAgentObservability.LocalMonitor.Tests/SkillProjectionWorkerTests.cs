using System.Collections;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProjectionWorkerTests
{
    private const string TraceId = "44444444444444444444444444444444";
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ClaimedAt = ObservedAt.AddSeconds(1);

    [Fact]
    public async Task RejectedHeartbeat_CancelsProjectionAndRetriesWithoutPublishingRows()
    {
        using var database = CreateDatabaseWithTwoInputs();
        var store = CreateStore(database.Path);
        var projectionStarted = NewSignal();
        var releaseProjection = NewSignal();
        var time = new MutableTimeProvider(ClaimedAt);
        CapturedFrontier? capturedFrontier = null;
        var publishBoundaryReached = 0;
        var worker = new SkillProjectionWorker(
            store,
            beforePublish: _ => Interlocked.Exchange(ref publishBoundaryReached, 1),
            timeProvider: time,
            readFrontier: (lease, cancellationToken) => ReadBlockedFrontierAsync(
                store,
                lease,
                cancellationToken,
                projectionStarted,
                releaseProjection,
                retained => MakeOperationLeaseRenewalDue(
                    database.Path,
                    retained,
                    ClaimedAt.AddSeconds(20)),
                frontier => capturedFrontier = frontier));

        var work = worker.RunNextAsync(ClaimedAt);
        var projectionCancelled = false;
        try
        {
            await projectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertClaimedQueueAndOperationLeasesPresent(
                database.Path,
                Assert.IsType<CapturedFrontier>(capturedFrontier),
                ClaimedAt.AddSeconds(20));
            DriftRetentionItemRevision(database.Path);
            time.Advance(TimeSpan.FromSeconds(11));
            projectionCancelled = await Assert.IsType<CapturedFrontier>(capturedFrontier)
                .Records
                .WaitForCancellationAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!projectionCancelled)
                releaseProjection.TrySetResult();
        }

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(5));
        var frontier = Assert.IsType<CapturedFrontier>(capturedFrontier);

        Assert.True(projectionCancelled);
        Assert.Equal(SkillProjectionWorkOutcome.Retrying, outcome);
        Assert.False(frontier.Records.SecondRecordRead);
        Assert.Equal(0, Volatile.Read(ref publishBoundaryReached));
        AssertRetriedWithoutPublication(database.Path, frontier);
    }

    [Fact]
    public async Task SqliteBusyHeartbeat_CancelsProjectionAndRetriesWithoutFaultingOrPublishingRows()
    {
        using var database = CreateDatabaseWithTwoInputs();
        var busyCheckpoint = new SqliteBusyRenewalCheckpoint();
        var store = CreateStore(database.Path, busyCheckpoint);
        var projectionStarted = NewSignal();
        var releaseProjection = NewSignal();
        var time = new MutableTimeProvider(ClaimedAt);
        CapturedFrontier? capturedFrontier = null;
        var publishBoundaryReached = 0;
        var worker = new SkillProjectionWorker(
            store,
            beforePublish: _ => Interlocked.Exchange(ref publishBoundaryReached, 1),
            timeProvider: time,
            readFrontier: (lease, cancellationToken) => ReadBlockedFrontierAsync(
                store,
                lease,
                cancellationToken,
                projectionStarted,
                releaseProjection,
                retained => MakeOperationLeaseRenewalDue(
                    database.Path,
                    retained,
                    ClaimedAt.AddSeconds(20)),
                frontier => capturedFrontier = frontier));

        var work = worker.RunNextAsync(ClaimedAt);
        var projectionCancelled = false;
        try
        {
            await projectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertClaimedQueueAndOperationLeasesPresent(
                database.Path,
                Assert.IsType<CapturedFrontier>(capturedFrontier),
                ClaimedAt.AddSeconds(20));
            time.Advance(TimeSpan.FromSeconds(11));
            await busyCheckpoint.WasReached.WaitAsync(TimeSpan.FromSeconds(5));
            projectionCancelled = await Assert.IsType<CapturedFrontier>(capturedFrontier)
                .Records
                .WaitForCancellationAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!projectionCancelled)
                releaseProjection.TrySetResult();
        }

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(5));
        var frontier = Assert.IsType<CapturedFrontier>(capturedFrontier);

        Assert.True(projectionCancelled);
        Assert.Equal(SkillProjectionWorkOutcome.Retrying, outcome);
        Assert.False(frontier.Records.SecondRecordRead);
        Assert.Equal(0, Volatile.Read(ref publishBoundaryReached));
        AssertRetriedWithoutPublication(database.Path, frontier);
    }

    private static async ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>>
        ReadBlockedFrontierAsync(
            SqliteSkillProjectionStore store,
            SkillProjectionQueueLease queueLease,
            CancellationToken cancellationToken,
            TaskCompletionSource projectionStarted,
            TaskCompletionSource releaseProjection,
            Action<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>? configureLease,
            Action<CapturedFrontier> captureFrontier)
    {
        await Task.Yield();
        var read = await store.ReadFrontierAsync(queueLease, cancellationToken);
        var retained = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(
            read.Lease);
        Assert.Equal(2, retained.Value.Count);
        configureLease?.Invoke(retained);
        var records = new BlockingRawRecordList(
            retained.Value,
            projectionStarted,
            releaseProjection,
            cancellationToken);
        captureFrontier(new(queueLease, retained.Grants, records));
        return new(
            RetentionReadDisposition.Granted,
            new RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>(
                records,
                retained.RevisionFence,
                retained.Grants,
                _ => retained.DisposeAsync()));
    }

    private static TestDatabase CreateDatabaseWithTwoInputs()
    {
        var database = new TestDatabase();
        try
        {
            new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
            var ingestion = new SqliteIngestionCommitStore(database.Path);
            ingestion.Commit(CreateBatch("worker-heartbeat-1", ObservedAt));
            ingestion.Commit(CreateBatch(
                "worker-heartbeat-2",
                ObservedAt.AddMilliseconds(1)));
            SeedExactAdapterCoverage(database.Path);
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static SqliteSkillProjectionStore CreateStore(
        string databasePath,
        ISkillProjectionCheckpoint? checkpoint = null)
    {
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(databasePath);
        return new(
            databasePath,
            new RawTelemetryStore(
                databasePath,
                retention,
                new MutableTimeProvider(ClaimedAt)),
            checkpoint);
    }

    private static ValidatedIngestionBatch CreateBatch(
        string batchId,
        DateTimeOffset at)
    {
        var inventory = OtlpJsonStructuralWalker.Build(SkillPayload, at);
        var decision = SourceCompatibilityEvaluator.Assess(
            "github-copilot-cli",
            "1.0.74",
            inventory,
            observedRecognizedCount: 1,
            VerifiedSourceFingerprintRegistry.Create([], [], []));
        var observation = SourceObservationBatchDraft.Create(
            batchId,
            "github-copilot-cli",
            "1.0.74",
            "github-copilot-otel",
            "adapter-1",
            inventory,
            decision,
            SourceCaptureContentState.Available,
            at,
            [TraceSourceVersionResolutionDraft.Create(
                TraceId,
                TraceSourceVersionResolutionState.Resolved,
                "1.0.74")]);
        return ValidatedIngestionBatch.Create(
            new RawTelemetryRecord(
                null,
                RawTelemetrySources.RawOtlp,
                TraceId,
                at,
                ResourceAttributesJson: null,
                PayloadJson: SkillPayload),
            observation);
    }

    private static void DriftRetentionItemRevision(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE retention_items SET revision=revision+1 WHERE store_kind='raw_record';";
        Assert.Equal(2, command.ExecuteNonQuery());
    }

    private static void SeedExactAdapterCoverage(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO retention_adapter_coverage(store_kind,coverage_version)
            VALUES
                ('session_event_content',1),
                ('raw_record',1),
                ('analysis_run_raw',1),
                ('sensitive_bundle',1),
                ('analysis_sdk_directory',1);
            """;
        Assert.Equal(5, command.ExecuteNonQuery());
    }

    private static void MakeOperationLeaseRenewalDue(
        string databasePath,
        RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> retained,
        DateTimeOffset expiresAt)
    {
        foreach (var grant in retained.Grants)
            grant.AdvanceExpiry(expiresAt);
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE retention_leases SET expires_at=$expires_at WHERE lease_kind='operation';";
        command.Parameters.AddWithValue("$expires_at", expiresAt.ToString("O"));
        Assert.Equal(2, command.ExecuteNonQuery());
    }

    private static void AssertRetriedWithoutPublication(
        string databasePath,
        CapturedFrontier frontier)
    {
        using var connection = Open(databasePath);
        Assert.Equal(
            1,
            ScalarLong(
                connection,
                """
                SELECT COUNT(*)
                FROM skill_projection_generations AS generation
                JOIN skill_projection_queue AS queue
                  ON queue.generation_id=generation.generation_id
                JOIN skill_projection_trace_heads AS head
                  ON head.trace_id=generation.trace_id
                WHERE generation.generation_id=$generation_id
                  AND generation.trace_id=$trace_id
                  AND generation.compatibility_revision=$compatibility_revision
                  AND generation.input_frontier_sha256=$input_frontier_sha256
                  AND generation.projector_version=$projector_version
                  AND generation.lifecycle='retry_pending'
                  AND queue.trace_id=$trace_id
                  AND queue.compatibility_revision=$compatibility_revision
                  AND queue.input_frontier_sha256=$input_frontier_sha256
                  AND queue.projector_version=$projector_version
                  AND queue.state='pending'
                  AND queue.attempt_count=$attempt_count
                  AND queue.lease_owner IS NULL
                  AND queue.lease_generation=$lease_generation
                  AND queue.lease_expires_at IS NULL
                  AND queue.next_attempt_at=$next_attempt_at
                  AND queue.error_code='retention_lease_lost'
                  AND head.desired_generation_id=generation.generation_id
                  AND head.current_generation_id IS NULL;
                """,
                ("$generation_id", frontier.QueueLease.GenerationId),
                ("$trace_id", frontier.QueueLease.TraceId),
                ("$compatibility_revision", frontier.QueueLease.CompatibilityRevision),
                ("$input_frontier_sha256", frontier.QueueLease.InputFrontierSha256),
                ("$projector_version", frontier.QueueLease.ProjectorVersion),
                ("$attempt_count", frontier.QueueLease.AttemptCount),
                ("$lease_generation", frontier.QueueLease.LeaseGeneration),
                ("$next_attempt_at", ClaimedAt.AddSeconds(1).ToString("O"))));
        Assert.Equal(
            0,
            ScalarLong(
                connection,
                """
                SELECT
                    (SELECT COUNT(*) FROM skill_projection_invocations) +
                    (SELECT COUNT(*) FROM skill_projection_inventories) +
                    (SELECT COUNT(*) FROM skill_projection_inventory_names) +
                    (SELECT COUNT(*) FROM skill_projection_sdk_claims);
                """));
        Assert.Equal(
            0,
            ScalarLong(
                connection,
                """
                SELECT COUNT(*)
                FROM skill_projection_trace_heads
                WHERE current_generation_id IS NOT NULL;
                """));
        AssertExactOperationLeasesReleased(connection, frontier.RetentionGrants);
    }

    private static void AssertClaimedQueueAndOperationLeasesPresent(
        string databasePath,
        CapturedFrontier frontier,
        DateTimeOffset operationLeaseExpiresAt)
    {
        using var connection = Open(databasePath);
        Assert.Equal(
            1,
            ScalarLong(
                connection,
                """
                SELECT COUNT(*)
                FROM skill_projection_queue
                WHERE generation_id=$generation_id
                  AND state='leased'
                  AND lease_owner=$owner
                  AND lease_generation=$lease_generation
                  AND lease_expires_at=$lease_expires_at;
                """,
                ("$generation_id", frontier.QueueLease.GenerationId),
                ("$owner", frontier.QueueLease.LeaseOwner),
                ("$lease_generation", frontier.QueueLease.LeaseGeneration),
                ("$lease_expires_at", frontier.QueueLease.LeaseExpiresAt.ToString("O"))));
        Assert.Equal(2, frontier.RetentionGrants.Count);
        foreach (var grant in frontier.RetentionGrants)
        {
            Assert.Equal(RetentionLeaseKind.Operation, grant.LeaseKind);
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM retention_leases
                    WHERE item_id=$item_id
                      AND lease_kind='operation'
                      AND owner=$owner
                      AND generation=$generation
                      AND expires_at=$expires_at;
                    """,
                    ("$item_id", grant.ItemId),
                    ("$owner", grant.LeaseOwner),
                    ("$generation", grant.LeaseGeneration),
                    ("$expires_at", operationLeaseExpiresAt.ToString("O"))));
        }
    }

    private static void AssertExactOperationLeasesReleased(
        SqliteConnection connection,
        IReadOnlyList<RetentionReadGrant> grants)
    {
        Assert.Equal(2, grants.Count);
        foreach (var grant in grants)
        {
            Assert.Equal(RetentionLeaseKind.Operation, grant.LeaseKind);
            Assert.Equal(
                0,
                ScalarLong(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM retention_leases
                    WHERE item_id=$item_id
                      AND lease_kind='operation'
                      AND owner=$owner
                      AND generation=$generation;
                    """,
                    ("$item_id", grant.ItemId),
                    ("$owner", grant.LeaseOwner),
                    ("$generation", grant.LeaseGeneration)));
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static long ScalarLong(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private const string SkillPayload =
        """
        {"resourceSpans":[{
          "resource":{"attributes":[
            {"key":"service.version","value":{"stringValue":"1.0.74"}},
            {"key":"client.kind","value":{"stringValue":"copilot-cli"}}
          ]},
          "scopeSpans":[{"spans":[{
            "traceId":"44444444444444444444444444444444",
            "spanId":"5555555555555555",
            "attributes":[
              {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
              {"key":"gen_ai.tool.name","value":{"stringValue":"skill"}},
              {"key":"github.copilot.skill.name","value":{"stringValue":"safe-skill"}},
              {"key":"github.copilot.context.skills","value":{"arrayValue":{"values":[
                {"stringValue":"safe-skill"}
              ]}}}
            ]
          }]}]
        }]}
        """;

    private sealed record CapturedFrontier(
        SkillProjectionQueueLease QueueLease,
        IReadOnlyList<RetentionReadGrant> RetentionGrants,
        BlockingRawRecordList Records);

    private sealed class BlockingRawRecordList : IReadOnlyList<RawTelemetryRecord>
    {
        private readonly IReadOnlyList<RawTelemetryRecord> source;
        private readonly TaskCompletionSource projectionStarted;
        private readonly TaskCompletionSource releaseProjection;
        private readonly TaskCompletionSource cancellationObserved = NewSignal();
        private int secondRecordRead;

        public BlockingRawRecordList(
            IReadOnlyList<RawTelemetryRecord> source,
            TaskCompletionSource projectionStarted,
            TaskCompletionSource releaseProjection,
            CancellationToken projectionCancellation)
        {
            this.source = source;
            this.projectionStarted = projectionStarted;
            this.releaseProjection = releaseProjection;
            projectionCancellation.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                cancellationObserved);
        }

        public RawTelemetryRecord this[int index]
        {
            get
            {
                if (index == 0)
                {
                    projectionStarted.TrySetResult();
                    Task.WhenAny(cancellationObserved.Task, releaseProjection.Task)
                        .GetAwaiter()
                        .GetResult();
                }
                else if (index == 1)
                {
                    Interlocked.Exchange(ref secondRecordRead, 1);
                }
                return source[index];
            }
        }

        public int Count => source.Count;
        internal bool SecondRecordRead => Volatile.Read(ref secondRecordRead) == 1;
        internal async Task<bool> WaitForCancellationAsync(TimeSpan timeout)
        {
            try
            {
                await cancellationObserved.Task.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        public IEnumerator<RawTelemetryRecord> GetEnumerator() => source.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SqliteBusyRenewalCheckpoint : ISkillProjectionCheckpoint
    {
        private readonly TaskCompletionSource reached = NewSignal();

        internal Task WasReached => reached.Task;

        public void Reached(SkillProjectionCheckpoint checkpoint)
        {
            if (checkpoint != SkillProjectionCheckpoint.BeforeRetentionRenewalPublication)
                return;
            reached.TrySetResult();
            throw new SqliteException("database is busy", 5);
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"skill-worker-{Guid.NewGuid():N}");

        internal TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        internal string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

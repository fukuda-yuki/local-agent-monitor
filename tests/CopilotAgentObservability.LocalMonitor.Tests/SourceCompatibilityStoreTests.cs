using System.Globalization;
using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SourceCompatibilityStoreTests
{
    [Fact]
    public void ValidatedBatch_RejectsCallerSuppliedRawRecordIdentity()
    {
        var valid = CreateBatch("batch-validation", BuildOverflowInventory());

        var exception = Assert.Throws<ArgumentException>(() =>
            ValidatedIngestionBatch.Create(valid.RawRecord with { Id = 42 }, valid.Observation));

        Assert.Equal("rawRecord", exception.ParamName);
    }

    [Fact]
    public void Commit_PersistsRawObservationAndCanonicalUnknownsAtomically()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var batch = CreateBatch("batch-atomic", BuildOverflowInventory());

        var result = new SqliteIngestionCommitStore(database.Path).Commit(batch);

        var raw = Assert.Single(database.CreateRawStore().ListRecords());
        Assert.Equal(result.RawRecordId, raw.Id);

        var observation = Assert.Single(new SqliteSourceCompatibilityStore(database.Path).List(after: null, limit: 200));
        Assert.Equal(result.ObservationId, observation.Id);
        Assert.Equal("batch-atomic", observation.ObservationId);
        Assert.Equal("batch-atomic", observation.IngestBatchId);
        Assert.Equal(result.RawRecordId, observation.RawRecordId);
        Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.SchemaDriftDetected], observation.ReasonCodes);
        Assert.Equal(SourceCompatibilityNextActions.CaptureFixtureAndReviewMapping, observation.NextAction);
        Assert.Equal(SourceCaptureContentState.Available, observation.CaptureContentState);
        Assert.Equal(257, observation.UnknownAttributeCount);
        Assert.Equal(3, observation.UnknownSpanCount);
        Assert.Equal(4, observation.UnknownEventCount);
        Assert.Equal(3, observation.OverflowDistinctCount);
        Assert.Equal(3, observation.OverflowOccurrenceCount);
        Assert.Equal(256, observation.UnknownObservations.Count);
        Assert.Equal(254, observation.UnknownObservations.Count(child => child.Kind == SourceUnknownKind.Attribute));
        Assert.Equal(1, observation.UnknownObservations.Count(child => child.Kind == SourceUnknownKind.Span));
        Assert.Equal(1, observation.UnknownObservations.Count(child => child.Kind == SourceUnknownKind.Event));
        Assert.All(observation.UnknownObservations, child =>
        {
            Assert.Equal("unverified", child.SourceVersionLabel);
            Assert.DoesNotContain("@example.test", child.Name, StringComparison.Ordinal);
            Assert.StartsWith("sample:v1:", child.OpaqueSampleReference, StringComparison.Ordinal);
        });
        Assert.Equal(3, Assert.Single(observation.UnknownObservations, child => child.Kind == SourceUnknownKind.Span).Count);
        Assert.Equal(4, Assert.Single(observation.UnknownObservations, child => child.Kind == SourceUnknownKind.Event).Count);
    }

    [Fact]
    public void Commit_WhenSecondUnknownInsertFails_RollsBackRawParentAndFirstChild()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using (var connection = Open(database.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TRIGGER inject_unknown_failure BEFORE INSERT ON source_unknown_observations " +
                "WHEN (SELECT COUNT(*) FROM source_unknown_observations WHERE source_observation_id = NEW.source_observation_id) >= 1 " +
                "BEGIN SELECT RAISE(ABORT, 'injected unknown failure after first child'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() =>
            new SqliteIngestionCommitStore(database.Path).Commit(CreateBatch("batch-rollback", BuildOverflowInventory())));

        using var verification = Open(database.Path);
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM source_unknown_observations;"));
    }

    [Fact]
    public void Commit_DuplicateBatchIsIdempotentAndReturnsExactIds()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path);
        var batch = CreateBatch(
            "batch-duplicate",
            BuildOverflowInventory(),
            [TraceSourceResolutionDraft.FromEvidence(
                "11111111111111111111111111111111",
                cliCandidateObserved: true,
                vsCodeCandidateObserved: false,
                unknownCandidateObserved: false,
                relevantEvidenceObserved: true)]);

        var first = store.Commit(batch);
        var second = store.Commit(batch);

        Assert.Equal(first, second);
        using var connection = Open(database.Path);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM source_trace_attribution_observations;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
        Assert.Equal(256L, Scalar(connection, "SELECT COUNT(*) FROM source_unknown_observations;"));
    }

    [Fact]
    public void GetTraceSourceVersionResolution_MissingAndResolvedObservationsDoesNotReturnResolved()
    {
        const string traceId = "11111111111111111111111111111111";
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var commitStore = new SqliteIngestionCommitStore(database.Path);
        var missing = commitStore.Commit(CreateBatch("batch-missing", BuildOverflowInventory()));
        var resolved = commitStore.Commit(CreateBatch("batch-resolved", BuildOverflowInventory()));
        using (var connection = Open(database.Path))
        {
            Execute(
                connection,
                $"INSERT INTO source_trace_version_observations VALUES ({missing.ObservationId}, '{traceId}', 'missing', NULL);");
            Execute(
                connection,
                $"INSERT INTO source_trace_version_observations VALUES ({resolved.ObservationId}, '{traceId}', 'resolved', '1.0.74');");
        }

        var resolution = new SqliteSourceCompatibilityStore(database.Path)
            .GetTraceSourceVersionResolution(traceId);

        Assert.Equal(TraceSourceVersionResolutionState.Missing, Assert.IsType<TraceSourceVersionResolutionRow>(resolution).State);
    }

    [Fact]
    public void TraceVersionObservation_RejectsUpdateDeleteAndParentDelete()
    {
        const string traceId = "11111111111111111111111111111111";
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("batch-immutable-trace-version", BuildOverflowInventory()));
        using var connection = Open(database.Path);
        Execute(
            connection,
            $"INSERT INTO source_trace_version_observations VALUES ({committed.ObservationId}, '{traceId}', 'missing', NULL);");

        Assert.Contains(
            "source_trace_version_observation_immutable",
            Assert.Throws<SqliteException>(() => Execute(
                connection,
                $"UPDATE source_trace_version_observations SET resolution_state='conflicting' WHERE source_observation_id={committed.ObservationId} AND trace_id='{traceId}';")).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_trace_version_observation_immutable",
            Assert.Throws<SqliteException>(() => Execute(
                connection,
                $"DELETE FROM source_trace_version_observations WHERE source_observation_id={committed.ObservationId} AND trace_id='{traceId}';")).Message,
            StringComparison.Ordinal);
        Assert.Throws<SqliteException>(() => Execute(
            connection,
            $"DELETE FROM source_schema_observations WHERE id={committed.ObservationId};"));
    }

    [Fact]
    public void TraceVersionObservation_InsertOrReplaceCannotReplaceExistingEvidenceWhenRecursiveTriggersAreOff()
    {
        const string traceId = "11111111111111111111111111111111";
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("batch-no-replace-trace-version", BuildOverflowInventory()));
        using var connection = Open(database.Path);
        Execute(
            connection,
            $"INSERT INTO source_trace_version_observations VALUES ({committed.ObservationId}, '{traceId}', 'missing', NULL);");
        Execute(connection, "PRAGMA recursive_triggers=OFF;");
        var before = SnapshotTraceVersionObservation(
            connection,
            committed.ObservationId,
            traceId);

        Assert.Contains(
            "source_trace_version_observation_no_replace",
            Assert.Throws<SqliteException>(() => Execute(
                connection,
                $"INSERT OR REPLACE INTO source_trace_version_observations VALUES ({committed.ObservationId}, '{traceId}', 'resolved', '1.0.74');")).Message,
            StringComparison.Ordinal);
        Assert.Equal(
            "missing",
            ScalarText(
                connection,
                $"SELECT resolution_state FROM source_trace_version_observations WHERE source_observation_id={committed.ObservationId} AND trace_id='{traceId}';"));
        Assert.Equal(
            before,
            SnapshotTraceVersionObservation(
                connection,
                committed.ObservationId,
                traceId));
    }

    [Fact]
    public void TriggerDefinitions_MatchInstalledSourceCompatibilityTriggers()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);

        foreach (var trigger in SourceCompatibilitySchemaV11.TriggerDefinitions)
        {
            var actual = ScalarText(
                connection,
                $"SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='{trigger.Name}';");
            Assert.Equal(NormalizeTriggerSql(trigger.Sql), NormalizeTriggerSql(actual));
        }
    }

    [Fact]
    public void Commit_PersistsTraceSourceEvidenceAtomicallyAndReturnsAggregateResolution()
    {
        const string traceId = "11111111111111111111111111111111";
        using var database = new TestDatabase();
        var sourceStore = new SqliteSourceCompatibilityStore(database.Path);
        sourceStore.CreateSchema();
        var batch = CreateBatch(
            "batch-source-attribution",
            BuildOverflowInventory(),
            [TraceSourceResolutionDraft.FromEvidence(
                traceId,
                cliCandidateObserved: true,
                vsCodeCandidateObserved: false,
                unknownCandidateObserved: false,
                relevantEvidenceObserved: true)]);

        var committed = new SqliteIngestionCommitStore(database.Path).Commit(batch);

        Assert.Equal(
            new TraceSourceResolutionRow(traceId, TraceSourceResolutionState.Resolved, "copilot-cli"),
            sourceStore.GetTraceSourceResolution(traceId));
        using var connection = Open(database.Path);
        Assert.Equal(1L, Scalar(
            connection,
            $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE raw_record_id={committed.RawRecordId.ToString(CultureInfo.InvariantCulture)} AND trace_id='{traceId}' AND cli_candidate_observed=1 AND vscode_candidate_observed=0 AND unknown_candidate_observed=0 AND relevant_evidence_observed=1;"));
        Assert.Equal(1L, Scalar(
            connection,
            $"SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue WHERE trace_id='{traceId}';"));
        Execute(
            connection,
            $"DELETE FROM raw_records WHERE id={committed.RawRecordId.ToString(CultureInfo.InvariantCulture)};");
        Assert.Equal(
            new TraceSourceResolutionRow(traceId, TraceSourceResolutionState.Resolved, "copilot-cli"),
            sourceStore.GetTraceSourceResolution(traceId));
    }

    [Fact]
    public void GetTraceSourceResolution_OrAggregatesRecordsWithDeterministicPrecedence()
    {
        const string traceId = "11111111111111111111111111111111";
        using var database = new TestDatabase();
        var sourceStore = new SqliteSourceCompatibilityStore(database.Path);
        sourceStore.CreateSchema();
        var commitStore = new SqliteIngestionCommitStore(database.Path);
        commitStore.Commit(CreateBatch(
            "batch-source-cli",
            BuildOverflowInventory(),
            [TraceSourceResolutionDraft.FromEvidence(traceId, true, false, false, true)]));
        commitStore.Commit(CreateBatch(
            "batch-source-unknown",
            BuildOverflowInventory(),
            [TraceSourceResolutionDraft.FromEvidence(traceId, false, false, true, true)]));

        Assert.Equal(
            new TraceSourceResolutionRow(traceId, TraceSourceResolutionState.Unrecognised, null),
            sourceStore.GetTraceSourceResolution(traceId));

        commitStore.Commit(CreateBatch(
            "batch-source-vscode",
            BuildOverflowInventory(),
            [TraceSourceResolutionDraft.FromEvidence(traceId, false, true, false, true)]));

        Assert.Equal(
            new TraceSourceResolutionRow(traceId, TraceSourceResolutionState.Conflicting, null),
            sourceStore.GetTraceSourceResolution(traceId));
    }

    [Fact]
    public void ReconcileTraceSourceAttribution_UpdatesOnlyQueuedOwnersAndIdleRetryDoesNotRewrite()
    {
        const string affectedTrace = "11111111111111111111111111111111";
        const string unrelatedTrace = "22222222222222222222222222222222";
        using var database = new TestDatabase();
        var store = new SqliteSourceCompatibilityStore(database.Path);
        store.CreateSchema();
        using (var connection = Open(database.Path))
        {
            Execute(
                connection,
                $"""
                INSERT INTO monitor_traces(trace_id,client_kind,projected_at)
                VALUES('{affectedTrace}',NULL,'2026-07-30T00:00:00.0000000+00:00'),
                      ('{unrelatedTrace}','vscode-copilot-chat','2026-07-30T00:00:00.0000000+00:00');
                INSERT INTO monitor_ingestions(
                    raw_record_id,received_at,source,trace_id,client_kind,projected_at)
                VALUES(101,'2026-07-30T00:00:00.0000000+00:00','raw-otlp',
                       '{affectedTrace}',NULL,'2026-07-30T00:00:00.0000000+00:00'),
                      (202,'2026-07-30T00:00:00.0000000+00:00','raw-otlp',
                       '{unrelatedTrace}','vscode-copilot-chat','2026-07-30T00:00:00.0000000+00:00');
                INSERT INTO source_trace_attribution_observations
                VALUES(101,'{affectedTrace}',1,0,0,1),
                      (202,'{unrelatedTrace}',0,1,0,1);
                INSERT INTO source_trace_attribution_reconciliation_queue
                VALUES('{affectedTrace}');
                CREATE TABLE source_reconciliation_update_audit(
                    table_name TEXT NOT NULL,
                    identity TEXT NOT NULL
                );
                CREATE TRIGGER audit_trace_source_update
                AFTER UPDATE OF client_kind ON monitor_traces
                BEGIN
                    INSERT INTO source_reconciliation_update_audit VALUES('trace',NEW.trace_id);
                END;
                CREATE TRIGGER audit_ingestion_source_update
                AFTER UPDATE OF client_kind ON monitor_ingestions
                BEGIN
                    INSERT INTO source_reconciliation_update_audit
                    VALUES('ingestion',CAST(NEW.raw_record_id AS TEXT));
                END;
                """);
        }

        Assert.True(store.ReconcileProjectedTraceSourceAttribution());
        Assert.False(store.ReconcileProjectedTraceSourceAttribution());

        using var verification = Open(database.Path);
        Assert.Equal("copilot-cli", ScalarText(
            verification,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{affectedTrace}';"));
        Assert.Equal("copilot-cli", ScalarText(
            verification,
            "SELECT client_kind FROM monitor_ingestions WHERE raw_record_id=101;"));
        Assert.Equal("vscode-copilot-chat", ScalarText(
            verification,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{unrelatedTrace}';"));
        Assert.Equal("vscode-copilot-chat", ScalarText(
            verification,
            "SELECT client_kind FROM monitor_ingestions WHERE raw_record_id=202;"));
        Assert.Equal(
            "ingestion|101\ntrace|11111111111111111111111111111111",
            ScalarText(
                verification,
                """
                SELECT group_concat(value, char(10))
                FROM (
                    SELECT table_name || '|' || identity AS value
                    FROM source_reconciliation_update_audit
                    ORDER BY table_name,identity
                );
                """));
        Assert.Equal(0L, Scalar(
            verification,
            "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
    }

    [Fact]
    public void ReconcileTraceSourceAttribution_FailureRollsBackProjectionAndRetainsDurableRetry()
    {
        const string traceId = "11111111111111111111111111111111";
        using var database = new TestDatabase();
        var store = new SqliteSourceCompatibilityStore(database.Path);
        store.CreateSchema();
        using (var connection = Open(database.Path))
        {
            Execute(
                connection,
                $"""
                INSERT INTO monitor_traces(trace_id,client_kind,projected_at)
                VALUES('{traceId}',NULL,'2026-07-30T00:00:00.0000000+00:00');
                INSERT INTO monitor_ingestions(
                    raw_record_id,received_at,source,trace_id,client_kind,projected_at)
                VALUES(101,'2026-07-30T00:00:00.0000000+00:00','raw-otlp',
                       '{traceId}',NULL,'2026-07-30T00:00:00.0000000+00:00');
                INSERT INTO source_trace_attribution_observations
                VALUES(101,'{traceId}',1,0,0,1);
                INSERT INTO source_trace_attribution_reconciliation_queue
                VALUES('{traceId}');
                CREATE TRIGGER reject_ingestion_source_reconciliation
                BEFORE UPDATE OF client_kind ON monitor_ingestions
                BEGIN
                    SELECT RAISE(ABORT,'injected source reconciliation failure');
                END;
                """);
        }

        Assert.Throws<SqliteException>(
            () => store.ReconcileProjectedTraceSourceAttribution());

        using (var verification = Open(database.Path))
        {
            Assert.Equal(DBNull.Value, ScalarObject(
                verification,
                $"SELECT client_kind FROM monitor_traces WHERE trace_id='{traceId}';"));
            Assert.Equal(DBNull.Value, ScalarObject(
                verification,
                "SELECT client_kind FROM monitor_ingestions WHERE raw_record_id=101;"));
            Assert.Equal(1L, Scalar(
                verification,
                "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
            Execute(
                verification,
                "DROP TRIGGER reject_ingestion_source_reconciliation;");
        }

        Assert.True(store.ReconcileProjectedTraceSourceAttribution());

        using var completed = Open(database.Path);
        Assert.Equal("copilot-cli", ScalarText(
            completed,
            $"SELECT client_kind FROM monitor_traces WHERE trace_id='{traceId}';"));
        Assert.Equal("copilot-cli", ScalarText(
            completed,
            "SELECT client_kind FROM monitor_ingestions WHERE raw_record_id=101;"));
        Assert.Equal(0L, Scalar(
            completed,
            "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
    }

    [Fact]
    public void RecordAdapterFailure_PersistsNullableMetadataAndListUsesBoundedCursor()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var store = new SqliteSourceCompatibilityStore(database.Path);
        var at = new DateTimeOffset(2026, 7, 12, 3, 4, 5, TimeSpan.FromHours(9));

        var firstId = store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateParseFailure(
            "failure-1", null, null, null, null, null, null, at));
        var secondId = store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateAdapterException(
            "failure-2", "batch-failed", "claude-code", "1.2.3", "claude-code-otel", "adapter-v1",
            SourceCaptureContentState.NotCaptured, at.AddMinutes(1)));
        var thirdId = store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateParseFailure(
            "failure-3", null, null, null, null, null, null, at.AddMinutes(2)));

        var firstPage = store.List(after: null, limit: 2);
        Assert.Equal([firstId, secondId], firstPage.Select(row => row.Id));
        var first = firstPage[0];
        Assert.Equal("failure-1", first.ObservationId);
        Assert.Null(first.RawRecordId);
        Assert.Null(first.IngestBatchId);
        Assert.Null(first.SourceSurface);
        Assert.Null(first.SourceApplicationVersion);
        Assert.Null(first.SourceAdapter);
        Assert.Null(first.AdapterVersion);
        Assert.Null(first.SchemaFingerprint);
        Assert.Null(first.InventoryHash);
        Assert.Null(first.CaptureContentState);
        Assert.Empty(first.UnknownObservations);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, first.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], first.ReasonCodes);
        Assert.Equal(at.ToUniversalTime(), first.ObservedAt);

        var secondPage = store.List(after: secondId, limit: 2);
        Assert.Equal([thirdId], secondPage.Select(row => row.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(after: null, limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(after: null, limit: 201));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(after: -1, limit: 1));
    }

    [Fact]
    public void List_UsesStableAscendingCursorsForLessThanExactAndMultiplePages()
    {
        using var database = new TestDatabase();
        var store = new SqliteSourceCompatibilityStore(database.Path);
        store.CreateSchema();
        var observedAt = DateTimeOffset.UnixEpoch;
        for (var index = 1; index <= 51; index++)
        {
            store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateParseFailure(
                $"cursor-{index}", null, null, null, null, null, null, observedAt.AddMinutes(index)));
        }

        var firstPage = store.List(after: null, limit: 50);
        var secondPage = store.List(after: firstPage[^1].Id, limit: 50);
        var finalPage = store.List(after: secondPage[^1].Id, limit: 50);
        var underLimitPage = store.List(after: null, limit: 200);

        Assert.Equal(Enumerable.Range(1, 50).Select(value => (long)value), firstPage.Select(row => row.Id));
        Assert.Equal([51L], secondPage.Select(row => row.Id));
        Assert.Empty(finalPage);
        Assert.Equal(51, underLimitPage.Count);
        Assert.Equal(Enumerable.Range(1, 51).Select(value => (long)value), underLimitPage.Select(row => row.Id));
    }

    [Fact]
    public void List_MapsConcreteSqliteReadLockToPersistenceBusyException()
    {
        using var database = new TestDatabase();
        var connectionOptions = new RawTelemetryStoreConnectionOptions(EnableWriteAheadLog: false, BusyTimeoutMilliseconds: 0);
        var store = new SqliteSourceCompatibilityStore(database.Path, connectionOptions);
        store.CreateSchema();
        using var lockConnection = Open(database.Path);
        Execute(lockConnection, "PRAGMA locking_mode = EXCLUSIVE;");
        Execute(lockConnection, "BEGIN EXCLUSIVE;");
        try
        {
            Assert.Throws<PersistenceBusyException>(() => store.List(after: null, limit: 1));
        }
        finally
        {
            Execute(lockConnection, "ROLLBACK;");
        }
    }

    [Fact]
    public void CreateSchema_RejectsNewerVersionWithoutRewritingStamp()
    {
        using var database = new TestDatabase();
        var compatibilityStore = new SqliteSourceCompatibilityStore(database.Path);
        compatibilityStore.CreateSchema();
        using (var connection = Open(database.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE schema_version SET version = 12 WHERE component = 'monitor';";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(compatibilityStore.CreateSchema);

        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
        using var verification = Open(database.Path);
        Assert.Equal(12L, Scalar(verification, "SELECT version FROM schema_version WHERE component = 'monitor';"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("raw")]
    [InlineData("raw-monitor")]
    [InlineData("runtime-state")]
    [InlineData("retention")]
    public void MonitorInitializers_RejectFutureSchemaWithoutAnyByteOrStateMutation(
        string initializer)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using (var connection = Open(database.Path))
        {
            Execute(connection, """
                DROP INDEX IX_raw_records_source;
                UPDATE schema_version SET version = 12 WHERE component = 'monitor';
                CREATE TABLE future_monitor_sentinel(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO future_monitor_sentinel(id,value) VALUES(1,'future-state');
                PRAGMA journal_mode=DELETE;
                """);
        }
        var before = SHA256.HashData(File.ReadAllBytes(database.Path));

        Action initialize = initializer switch
        {
            "source" => new SqliteSourceCompatibilityStore(
                database.Path,
                RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema,
            "raw" => new RawTelemetryStore(
                database.Path,
                RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema,
            "raw-monitor" => new RawTelemetryStore(
                database.Path,
                RawTelemetryStoreConnectionOptions.MonitorWriter).CreateMonitorSchema,
            "runtime-state" => new SqliteMonitorRuntimeStateStore(
                database.Path,
                timeProvider: null,
                RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema,
            "retention" => new RetentionCatalogStore(database.Path).CreateSchema,
            _ => throw new ArgumentOutOfRangeException(nameof(initializer)),
        };

        Assert.NotNull(Record.Exception(initialize));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
        Assert.False(File.Exists(database.Path + "-wal"));
        Assert.False(File.Exists(database.Path + "-shm"));
        using var verification = Open(database.Path);
        Assert.Equal(12L, Scalar(
            verification,
            "SELECT version FROM schema_version WHERE component = 'monitor';"));
        Assert.Equal(0L, Scalar(
            verification,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_raw_records_source';"));
        Assert.Equal("future-state", ScalarText(
            verification,
            "SELECT value FROM future_monitor_sentinel WHERE id=1;"));
    }

    [Theory]
    [InlineData("source", "missing")]
    [InlineData("source", "wrong-pk")]
    [InlineData("source", "wrong-check")]
    [InlineData("source", "missing-index")]
    [InlineData("source", "missing-queue")]
    [InlineData("source", "wrong-queue-pk")]
    [InlineData("host", "missing")]
    [InlineData("host", "wrong-pk")]
    [InlineData("host", "wrong-check")]
    [InlineData("host", "missing-index")]
    [InlineData("host", "missing-queue")]
    [InlineData("host", "wrong-queue-pk")]
    public void CurrentV10Initializers_RejectMalformedAttributionAuthorityWithoutMutation(
        string initializer,
        string corruption)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using (var connection = Open(database.Path))
        {
            Execute(connection, """
                CREATE TABLE current_monitor_sentinel(
                    id INTEGER PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT INTO current_monitor_sentinel VALUES(1,'current-state');
                """);
            if (corruption is "missing" or "wrong-pk" or "wrong-check")
            {
                Execute(
                    connection,
                    """
                    DROP INDEX IX_source_trace_attribution_observations_trace_id;
                    DROP TABLE source_trace_attribution_observations;
                    """);
            }
            if (corruption is "wrong-pk" or "wrong-check")
            {
                var tableSql = SqliteSourceCompatibilityStore.TraceSourceAttributionTableSql;
                tableSql = corruption switch
                {
                    "wrong-pk" => tableSql.Replace(
                        "PRIMARY KEY (raw_record_id, trace_id)",
                        "PRIMARY KEY (raw_record_id)",
                        StringComparison.Ordinal),
                    "wrong-check" => tableSql.Replace(
                        "cli_candidate_observed IN (0, 1)",
                        "cli_candidate_observed IN (0, 1, 2)",
                        StringComparison.Ordinal),
                    _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
                };
                Execute(connection, tableSql);
                Execute(
                    connection,
                    SqliteSourceCompatibilityStore.TraceSourceAttributionIndexSql);
            }
            else if (corruption == "missing-index")
            {
                Execute(
                    connection,
                    "DROP INDEX IX_source_trace_attribution_observations_trace_id;");
            }
            else if (corruption is "missing-queue" or "wrong-queue-pk")
            {
                Execute(
                    connection,
                    "DROP TABLE source_trace_attribution_reconciliation_queue;");
                if (corruption == "wrong-queue-pk")
                {
                    Execute(
                        connection,
                        SqliteSourceCompatibilityStore.TraceSourceReconciliationQueueTableSql.Replace(
                            "trace_id TEXT NOT NULL PRIMARY KEY",
                            "trace_id TEXT NOT NULL",
                            StringComparison.Ordinal));
                }
            }
            Execute(connection, "PRAGMA journal_mode=DELETE;");
        }
        var before = SHA256.HashData(File.ReadAllBytes(database.Path));

        Action initialize = initializer switch
        {
            "source" => new SqliteSourceCompatibilityStore(
                database.Path,
                RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema,
            "host" => () =>
            {
                using var app = MonitorHost.Build(
                    new MonitorOptions(
                        database.Path,
                        "http://127.0.0.1:0",
                        SanitizedOnly: false,
                        MonitorOptions.DefaultMaxRequestBodyBytes),
                    new MonitorHostTestOptions
                    {
                        StartWriter = false,
                        StartProjectionWorker = false,
                        StartSessionWriter = false,
                        StartSessionOtelEnrichment = false,
                        UseUserSecrets = false,
                    });
            },
            _ => throw new ArgumentOutOfRangeException(nameof(initializer)),
        };

        var exception = Record.Exception(initialize);
        Assert.NotNull(exception);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
        Assert.False(File.Exists(database.Path + "-wal"));
        Assert.False(File.Exists(database.Path + "-shm"));
        Assert.False(File.Exists(database.Path + "-journal"));
        using var verification = Open(database.Path);
        Assert.Equal(11L, Scalar(
            verification,
            "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal("current-state", ScalarText(
            verification,
            "SELECT value FROM current_monitor_sentinel WHERE id=1;"));
        Assert.Equal(
            corruption == "missing" ? 0L : 1L,
            Scalar(
                verification,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type='table'
                  AND name='source_trace_attribution_observations';
                """));
    }

    [Theory]
    [InlineData("exact-empty")]
    [InlineData("exact-populated")]
    [InlineData("wrong-pk")]
    [InlineData("wrong-index")]
    [InlineData("wrong-queue-pk")]
    public void MonitorWriterV9Migration_RejectsCollidingAttributionAuthorityBeforeWalOrVersionMutation(
        string corruption)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using (var connection = Open(database.Path))
        {
            switch (corruption)
            {
                case "exact-empty":
                    break;
                case "exact-populated":
                    Execute(
                        connection,
                        """
                        INSERT INTO source_trace_attribution_observations(
                            raw_record_id,trace_id,cli_candidate_observed,
                            vscode_candidate_observed,unknown_candidate_observed,
                            relevant_evidence_observed)
                        VALUES(
                            999,'undeclared-v10-trace',1,0,0,1);
                        INSERT INTO source_trace_attribution_reconciliation_queue(trace_id)
                        VALUES('undeclared-v10-trace');
                        """);
                    break;
                case "wrong-pk":
                    Execute(
                        connection,
                        """
                        DROP INDEX IX_source_trace_attribution_observations_trace_id;
                        DROP TABLE source_trace_attribution_observations;
                        """);
                    Execute(
                        connection,
                        SqliteSourceCompatibilityStore.TraceSourceAttributionTableSql.Replace(
                            "PRIMARY KEY (raw_record_id, trace_id)",
                            "PRIMARY KEY (raw_record_id)",
                            StringComparison.Ordinal));
                    Execute(
                        connection,
                        SqliteSourceCompatibilityStore.TraceSourceAttributionIndexSql);
                    break;
                case "wrong-index":
                    Execute(
                        connection,
                        """
                        DROP INDEX IX_source_trace_attribution_observations_trace_id;
                        CREATE INDEX IX_source_trace_attribution_observations_trace_id
                        ON source_trace_attribution_observations(raw_record_id, trace_id);
                        """);
                    break;
                case "wrong-queue-pk":
                    Execute(
                        connection,
                        "DROP TABLE source_trace_attribution_reconciliation_queue;");
                    Execute(
                        connection,
                        SqliteSourceCompatibilityStore.TraceSourceReconciliationQueueTableSql.Replace(
                            "trace_id TEXT NOT NULL PRIMARY KEY",
                            "trace_id TEXT NOT NULL",
                            StringComparison.Ordinal));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption));
            }
            Execute(
                connection,
                """
                UPDATE schema_version SET version=9 WHERE component='monitor';
                CREATE TABLE v9_migration_sentinel(
                    id INTEGER PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT INTO v9_migration_sentinel VALUES(1,'v9-state');
                PRAGMA journal_mode=DELETE;
                """);
        }
        var before = SHA256.HashData(File.ReadAllBytes(database.Path));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SqliteSourceCompatibilityStore(
                database.Path,
                RawTelemetryStoreConnectionOptions.MonitorWriter)
            .CreateSchema());

        Assert.Equal(
            "Unsupported incomplete monitor schema version 11.",
            exception.Message);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
        Assert.False(File.Exists(database.Path + "-wal"));
        Assert.False(File.Exists(database.Path + "-shm"));
        Assert.False(File.Exists(database.Path + "-journal"));
        using var verification = Open(database.Path);
        Assert.Equal(9L, Scalar(
            verification,
            "SELECT version FROM schema_version WHERE component='monitor';"));
        Assert.Equal("v9-state", ScalarText(
            verification,
            "SELECT value FROM v9_migration_sentinel WHERE id=1;"));
    }

    [Fact]
    public void CreateSchema_WhenSecondSourceObjectFails_RollsBackFirstObjectAndVersionStamp()
    {
        using var database = new TestDatabase();
        database.CreateRawStore().CreateMonitorSchema();
        using (var connection = Open(database.Path))
        {
            Execute(
                connection,
                """
                DROP TABLE source_unknown_observations;
                CREATE VIEW source_unknown_observations AS SELECT 1 AS conflict;
                """);
        }

        Assert.Throws<SqliteException>(() => new SqliteSourceCompatibilityStore(database.Path).CreateSchema());

        using var verification = Open(database.Path);
        Assert.NotEmpty(Columns(verification, "source_schema_observations"));
        Assert.Equal(RawTelemetryStore.MonitorSchemaVersion, Scalar(verification, "SELECT version FROM schema_version WHERE component = 'monitor';"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'source_unknown_observations';"));
    }

    [Fact]
    public void CreateSchema_FocusedStoreOwnsSanitizedSourceTablesAndV10Stamp()
    {
        using var database = new TestDatabase();
        database.CreateRawStore().CreateMonitorSchema();
        using var connection = Open(database.Path);

        Assert.NotEmpty(Columns(connection, "source_schema_observations"));
        Assert.NotEmpty(Columns(connection, "source_unknown_observations"));
        Assert.NotEmpty(Columns(connection, "source_trace_version_observations"));
        Assert.Equal(
            [
                "raw_record_id", "trace_id", "cli_candidate_observed", "vscode_candidate_observed",
                "unknown_candidate_observed", "relevant_evidence_observed",
            ],
            Columns(connection, "source_trace_attribution_observations"));
        Assert.Equal(
            ["trace_id"],
            Columns(connection, "source_trace_attribution_reconciliation_queue"));
        Assert.Equal(RawTelemetryStore.MonitorSchemaVersion, Scalar(connection, "SELECT version FROM schema_version WHERE component = 'monitor';"));
        connection.Close();

        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        connection.Open();
        Assert.Equal(SqliteSourceCompatibilityStore.MonitorSchemaVersion, Scalar(connection, "SELECT version FROM schema_version WHERE component = 'monitor';"));

        Assert.Equal(
            [
                "id", "observation_id", "raw_record_id", "raw_payload_sha256",
                "input_evidence_kind", "ingest_batch_id", "source_surface",
                "source_application_version", "source_adapter", "adapter_version", "schema_fingerprint",
                "inventory_hash", "compatibility_state", "reason_code", "next_action", "capture_content_state",
                "unknown_span_count", "unknown_event_count", "unknown_attribute_count", "overflow_distinct_count",
                "overflow_occurrence_count", "observed_at",
            ],
            Columns(connection, "source_schema_observations"));
        Assert.Equal(
            [
                "id", "source_observation_id", "kind", "name", "occurrence_count", "source_version_label",
                "first_observed_at", "last_observed_at", "opaque_sample_reference",
            ],
            Columns(connection, "source_unknown_observations"));
        Assert.Equal(
            [
                "source_observation_id", "trace_id", "resolution_state", "source_application_version",
            ],
            Columns(connection, "source_trace_version_observations"));

        string[] forbidden = ["payload_json", "resource_attributes_json", "raw_value", "value", "user_id", "user_email", "path"];
        foreach (var table in new[]
        {
            "source_schema_observations",
            "source_unknown_observations",
            "source_trace_version_observations",
            "source_trace_attribution_observations",
            "source_trace_attribution_reconciliation_queue",
        })
        {
            Assert.DoesNotContain(Columns(connection, table), forbidden.Contains);
        }

        Assert.Equal(
            ["IX_source_schema_observations_cursor", "IX_source_unknown_observations_cursor"],
            Indexes(connection).Where(name => name.EndsWith("_cursor", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Contains(
            "IX_source_trace_attribution_observations_trace_id",
            Indexes(connection));
    }

    [Fact]
    public void CreateSchema_MigratesExistingV7FixtureDatabaseAdditively()
    {
        using var database = new TestDatabase();
        var store = new SqliteSourceCompatibilityStore(database.Path);
        store.CreateSchema();
        store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateParseFailure(
            "fixture-observation", null, null, null, null, null, null, DateTimeOffset.UnixEpoch));
        using (var connection = Open(database.Path))
        {
            Execute(
                connection,
                """
                DROP TABLE IF EXISTS skill_projection_sdk_claims;
                DROP TABLE IF EXISTS skill_projection_inventory_names;
                DROP TABLE IF EXISTS skill_projection_inventories;
                DROP TABLE IF EXISTS skill_projection_invocations;
                DROP TABLE IF EXISTS skill_projection_operation_receipts;
                DROP TABLE IF EXISTS skill_projection_queue;
                DROP TABLE IF EXISTS skill_projection_trace_heads;
                DROP TABLE IF EXISTS skill_projection_generation_inputs;
                DROP TABLE IF EXISTS skill_projection_generations;
                DELETE FROM schema_version WHERE component='skill_projection';
                DROP TABLE source_compatibility_reconciliation_receipts;
                DROP TABLE source_trace_version_interpretation_heads;
                DROP TABLE source_trace_version_interpretation_supersessions;
                DROP TABLE source_trace_compatibility_revisions;
                DROP TRIGGER source_schema_observations_insert_no_replace;
                DROP TRIGGER source_schema_observations_trace_version_child_delete_rejected;
                DROP TRIGGER source_schema_observations_projection_input_update_rejected;
                ALTER TABLE source_schema_observations
                DROP COLUMN input_evidence_kind;
                ALTER TABLE source_schema_observations
                DROP COLUMN raw_payload_sha256;
                DROP TABLE source_trace_version_observations;
                DROP INDEX IX_source_trace_attribution_observations_trace_id;
                DROP TABLE source_trace_attribution_observations;
                DROP TABLE source_trace_attribution_reconciliation_queue;
                """);
            Execute(connection, "UPDATE schema_version SET version = 7 WHERE component = 'monitor';");
        }

        store.CreateSchema();

        using var verification = Open(database.Path);
        Assert.Equal(11L, Scalar(verification, "SELECT version FROM schema_version WHERE component = 'monitor';"));
        Assert.Equal(
            ["source_observation_id", "trace_id", "resolution_state", "source_application_version"],
            Columns(verification, "source_trace_version_observations"));
        Assert.Equal(
            1L,
            Scalar(verification, "SELECT COUNT(*) FROM source_schema_observations WHERE observation_id = 'fixture-observation';"));
    }

    [Fact]
    public void SourceObservationSchema_RejectsArbitraryNextActionViaDirectSql()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO source_schema_observations (
                observation_id, compatibility_state, reason_code, next_action, capture_content_state,
                unknown_span_count, unknown_event_count, unknown_attribute_count,
                overflow_distinct_count, overflow_occurrence_count, observed_at
            ) VALUES (
                'invalid-action', 'schema_drift_detected', 'schema_drift_detected', 'arbitrary_action', 'available',
                0, 0, 0, 0, 0, '2026-07-12T00:00:00.0000000+00:00'
            );
            """;

        var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM source_schema_observations;"));
    }

    [Fact]
    public void Commit_WriteLockReturnsTypedBusyWithoutRowsOrCursor_ExplicitReplaySucceedsOnce()
    {
        using var database = new TestDatabase();
        var options = new RawTelemetryStoreConnectionOptions(EnableWriteAheadLog: true, BusyTimeoutMilliseconds: 0);
        new SqliteSourceCompatibilityStore(database.Path, options).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path, options);
        var batch = CreateBatch("batch-busy", BuildOverflowInventory());
        using var lockConnection = Open(database.Path);
        Execute(lockConnection, "BEGIN IMMEDIATE;");
        try
        {
            Assert.Throws<IngestionCommitBusyException>(() => store.Commit(batch));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COUNT(*) FROM raw_records;"));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COUNT(*) FROM source_schema_observations;"));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COUNT(*) FROM source_unknown_observations;"));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'raw_records'), 0);"));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'source_schema_observations'), 0);"));
        }
        finally
        {
            Execute(lockConnection, "ROLLBACK;");
        }

        var committed = store.Commit(batch);
        var replayed = store.Commit(batch);

        Assert.Equal(committed, replayed);
        using var verification = Open(database.Path);
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(256L, Scalar(verification, "SELECT COUNT(*) FROM source_unknown_observations;"));
    }

    [Theory]
    [InlineData("AfterRawRecordInsert")]
    [InlineData("AfterCatalogRegistration")]
    [InlineData("BeforeCommit")]
    public void Commit_CheckpointFailureRollsBackRawCatalogAndSourceTogether(string phaseName)
    {
        var phase = Enum.Parse<IngestionCommitWritePhase>(phaseName, ignoreCase: false);
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new RetentionCatalogStore(database.Path).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path, connectionOptions: null, actual =>
        {
            if (actual == phase) throw new InvalidOperationException("injected direct-ingestion failure");
        });

        Assert.Throws<InvalidOperationException>(() => store.Commit(CreateBatch(
            "batch-atomic-" + phase,
            BuildOverflowInventory(),
            [TraceSourceResolutionDraft.FromEvidence(
                "11111111111111111111111111111111",
                cliCandidateObserved: true,
                vsCodeCandidateObserved: false,
                unknownCandidateObserved: false,
                relevantEvidenceObserved: true)])));

        using var connection = Open(database.Path);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM source_trace_attribution_observations;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM monitor_projection_dispositions;"));
    }

    [Fact]
    public void Commit_RegistersExactRawReceiptAndReplayPreservesIds()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path);
        var batch = CreateBatch("batch-catalog-receipt", BuildOverflowInventory());

        var committed = store.Commit(batch);
        Assert.Equal(committed, store.Commit(batch));

        using var connection = Open(database.Path);
        Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM raw_records WHERE id={committed.RawRecordId.ToString(CultureInfo.InvariantCulture)} AND received_at='2026-07-12T00:00:00.0000000+00:00' AND length(retention_owner_token)=32;"));
        Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId.ToString(CultureInfo.InvariantCulture)}' AND captured_at='2026-07-12T00:00:00.0000000+00:00';"));
    }

    [Fact]
    public async Task Commit_DuplicateDeliveryReleasedAtBarrier_ProducesOneIdentityOrTypedBusyThenReplaysExactIds()
    {
        using var database = new TestDatabase();
        var options = new RawTelemetryStoreConnectionOptions(EnableWriteAheadLog: true, BusyTimeoutMilliseconds: 0);
        new SqliteSourceCompatibilityStore(database.Path, options).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path, options);
        var batch = CreateBatch("batch-race", BuildOverflowInventory());
        using var barrier = new Barrier(participantCount: 3);

        Task<CommitAttempt> StartAttempt() => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                return new CommitAttempt(store.Commit(batch), Busy: false);
            }
            catch (IngestionCommitBusyException)
            {
                return new CommitAttempt(null, Busy: true);
            }
        });

        var firstTask = StartAttempt();
        var secondTask = StartAttempt();
        barrier.SignalAndWait();
        var attempts = await Task.WhenAll(firstTask, secondTask);
        var committedIds = attempts.Where(attempt => attempt.Ids is not null).Select(attempt => attempt.Ids!).ToArray();
        Assert.NotEmpty(committedIds);
        var committed = committedIds[0];
        Assert.All(committedIds, ids => Assert.Equal(committed, ids));
        Assert.All(attempts, attempt => Assert.True(attempt.Busy || attempt.Ids == committed));

        var replayed = store.Commit(batch);

        Assert.Equal(committed, replayed);
        using var verification = Open(database.Path);
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(256L, Scalar(verification, "SELECT COUNT(*) FROM source_unknown_observations;"));
    }

    private static ValidatedIngestionBatch CreateBatch(
        string ingestBatchId,
        SourceStructuralInventory inventory,
        IReadOnlyList<TraceSourceResolutionDraft>? traceSourceResolutions = null)
    {
        var decision = SourceCompatibilityEvaluator.Assess(
            "claude-code",
            "unverified",
            inventory,
            observedRecognizedCount: 1,
            VerifiedSourceFingerprintRegistry.Create([], [], []));
        var observedAt = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var observation = traceSourceResolutions is null
            ? SourceObservationBatchDraft.Create(
                ingestBatchId,
                "claude-code",
                "unverified",
                "claude-code-otel",
                "adapter-v1",
                inventory,
                decision,
                SourceCaptureContentState.Available,
                observedAt)
            : SourceObservationBatchDraft.CreateWithTraceSources(
                ingestBatchId,
                "claude-code",
                "unverified",
                "claude-code-otel",
                "adapter-v1",
                inventory,
                decision,
                SourceCaptureContentState.Available,
                observedAt,
                traceSourceResolutions: traceSourceResolutions);
        return ValidatedIngestionBatch.Create(
            new RawTelemetryRecord(
                Id: null,
                Source: RawTelemetrySources.RawOtlp,
                TraceId: "11111111111111111111111111111111",
                ReceivedAt: new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
                ResourceAttributesJson: null,
                PayloadJson: "{}"),
            observation);
    }

    private static SourceStructuralInventory BuildOverflowInventory()
    {
        var json = new StringBuilder("{");
        for (var index = 0; index < 257; index++)
        {
            if (index != 0)
            {
                json.Append(',');
            }
            json.Append('"').Append("unknown-").Append(index.ToString("D3", CultureInfo.InvariantCulture)).Append("@example.test\":\"secret-value\"");
        }
        json.Append(",\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{}]}]}]}");
        var walked = OtlpJsonStructuralWalker.Build(json.ToString(), DateTimeOffset.UnixEpoch);
        var occurrences = walked.StructuralOccurrences.ToList();
        occurrences.Add(UnknownProducerOccurrence(
            SourceUnknownKind.Span,
            SourceStructuralEnvelope.Span,
            SourceStructuralRole.SpanName,
            "unrecognized-span@example.test",
            count: 3,
            sampleHex: 'c'));
        occurrences.Add(UnknownProducerOccurrence(
            SourceUnknownKind.Event,
            SourceStructuralEnvelope.Event,
            SourceStructuralRole.EventName,
            "unrecognized-event@example.test",
            count: 4,
            sampleHex: 'd'));
        return SourceStructuralInventory.Create(occurrences, hasRequiredTraceSignal: true);
    }

    private static SourceStructuralOccurrence UnknownProducerOccurrence(
        SourceUnknownKind kind,
        SourceStructuralEnvelope envelope,
        SourceStructuralRole role,
        string rawName,
        int count,
        char sampleHex)
    {
        var name = SourceStructuralNameToken.FromProducerName(role, rawName);
        var occurrenceCount = SourceOccurrenceCount.Create(count);
        var identity = SourceUnknownIdentity.Create(
            kind,
            name,
            occurrenceCount,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            $"sample:v1:{new string(sampleHex, 64)}");
        return SourceStructuralOccurrence.Create(
            envelope,
            role,
            name,
            SourceStructuralType.String,
            occurrenceCount,
            identity);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string NormalizeTriggerSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd(';')
            .Replace("CREATE TRIGGER IF NOT EXISTS ", "CREATE TRIGGER ", StringComparison.Ordinal);

    private static string SnapshotTraceVersionObservation(
        SqliteConnection connection,
        long sourceObservationId,
        string traceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_observation_id,trace_id,resolution_state,source_application_version
            FROM source_trace_version_observations
            WHERE source_observation_id=$source_observation_id AND trace_id=$trace_id;
            """;
        command.Parameters.AddWithValue("$source_observation_id", sourceObservationId);
        command.Parameters.AddWithValue("$trace_id", traceId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var snapshot = string.Join(
            '\0',
            reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? "<null>" : reader.GetString(3));
        Assert.False(reader.Read());
        return snapshot;
    }

    private static object? ScalarObject(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_xinfo($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static string[] Indexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"source-compatibility-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        public string Path { get; }

        public TimeProvider TimeProvider { get; } = global::System.TimeProvider.System;

        public RetentionCatalogContext RetentionContext =>
            retentionContext ??= RetentionCatalogContext.InitializeNewOwnedDatabase(Path, TimeProvider);

        private RetentionCatalogContext? retentionContext;

        public RawTelemetryStore CreateRawStore(RawTelemetryStoreConnectionOptions? connectionOptions = null) =>
            new(Path, RetentionContext, TimeProvider, connectionOptions);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record CommitAttempt(CommittedIngestionIds? Ids, bool Busy);
}

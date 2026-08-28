using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RawRecordRetentionAdapterTests
{
    [Theory]
    [InlineData("$retention_read_source_token")]
    [InlineData("$retention_read_item_id")]
    [InlineData("$retention_read_revision")]
    [InlineData("$retention_read_lease_kind")]
    [InlineData("$retention_read_lease_owner")]
    [InlineData("$retention_read_lease_generation")]
    [InlineData("$retention_read_lease_expires_at")]
    public async Task RawMaterialization_RejectsEachPerturbedAdmissionParameter(string parameterName)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"raw-materialization-capability-{Guid.NewGuid():N}.db");
        try
        {
            var now = new DateTimeOffset(2026, 8, 13, 1, 2, 3, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var store = new RawTelemetryStore(path, context, time);
            store.CreateMonitorSchema();
            var rawRecordId = store.Insert(new RawTelemetryRecord(
                null,
                RawTelemetrySources.RawOtlp,
                "admitted-trace",
                now,
                null,
                "{\"resourceSpans\":[]}"));
            var admitted = await store.GetRawRecordByIdAsync(rawRecordId, RetentionReadKind.Access, CancellationToken.None);
            Assert.Null(admitted.Disposition);
            await using var lease = Assert.IsType<RetentionReadLease<RawTelemetryRecord>>(admitted.Lease);
            using var connection = OpenDatabase(path);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            RawTelemetryStore.ConfigureExactRawRecordMaterializationCommand(command, rawRecordId, lease.Grant);

            Assert.Equal(1, CountRows(command));
            var parameter = command.Parameters[parameterName];
            var boundValue = parameter.Value;
            Assert.NotNull(boundValue);
            parameter.Value = Perturb(parameterName, boundValue);

            Assert.Equal(0, CountRows(command));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task RawAdapter_DeletesOwnedRowAndRetainsProjection()
    {
        using var fixture = await Fixture.CreateAsync();
        var before = fixture.ProjectionSnapshot();
        Assert.All(before, value => Assert.NotEmpty(value));

        var result = await new RawRecordRetentionAdapter(fixture.Catalog).DeleteAsync(fixture.Context);

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM raw_records WHERE id=$target;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM raw_records WHERE id=$sibling;"));
        Assert.Equal(before, fixture.ProjectionSnapshot());
        Assert.Equal(1L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;"));
        Assert.Equal("deleted", fixture.Text("SELECT state FROM retention_items WHERE item_id=$item;"));
    }

    [Fact]
    public async Task RawAdapter_ForgedKindOrSourceContextReturnsLeaseLostWithoutMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var adapter = new RawRecordRetentionAdapter(fixture.Catalog);
        var forgedKind = fixture.Context with { StoreKind = RetentionStoreKind.SessionEventContent };
        var forgedSource = fixture.Context with { SourceIdentity = fixture.Context.SourceIdentity with { SourceItemId = "999" } };

        Assert.Same(RetentionAdapterResult.LeaseLost, await adapter.DeleteAsync(forgedKind));
        Assert.Same(RetentionAdapterResult.LeaseLost, await adapter.DeleteAsync(forgedSource));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM raw_records WHERE id=$target;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM raw_records WHERE id=$sibling;"));
        Assert.Equal(0L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item;"));
        Assert.Equal("deleting", fixture.Text("SELECT state FROM retention_items WHERE item_id=$item;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item;"));
    }

    [Fact]
    public async Task RawAdapter_RemovesToolAndOtelSkillFactsInOwningDeletionTransaction()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.AddWorkspaceFactsForTargetRawRecord();
        var participant = new RemovingWorkspaceFactsParticipant();

        var result = await new RawRecordRetentionAdapter(
            fixture.Catalog,
            participant: participant).DeleteAsync(fixture.Context);

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.Equal(new[] { "skill", "tool" }, participant.RemovedKinds);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM local_workspace_session_search_facts;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM raw_records WHERE id=$target;"));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("v4")]
    [InlineData("future")]
    public async Task RawAdapter_UnboundDeletionRejectsUnsupportedWorkspaceBeforeAnyMutation(string state)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.InstallUnsupportedWorkspace(state);
        var before = fixture.RawAndWorkspaceMutationSnapshot();

        var result = await new RawRecordRetentionAdapter(fixture.Catalog).DeleteAsync(fixture.Context);

        Assert.Equal(RetentionAdapterDisposition.TransientFailure, result.Disposition);
        Assert.Equal(RetentionErrorCode.DeleteIoFailed, result.ErrorCode);
        Assert.Equal(before, fixture.RawAndWorkspaceMutationSnapshot());
    }

    [Fact]
    public async Task RawAdapter_BatchesTwoHundredOneAffectedSessionsInBinaryOrder()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.AddAffectedSessions(201);
        var participant = new RecordingBatchParticipant();

        var result = await new RawRecordRetentionAdapter(fixture.Catalog, participant: participant)
            .DeleteAsync(fixture.Context);

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.Equal([200, 1], participant.BatchSizes);
        Assert.Equal(1, participant.PreflightCount);
        Assert.Equal(201, participant.SessionIds.Count);
        Assert.Equal(participant.SessionIds.Order(StringComparer.Ordinal), participant.SessionIds);
        Assert.Equal(201, participant.SessionIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RawAdapter_RefreshesTheCanonicalSessionWhenDeletingACaseVariantOtelOwner()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.AddNormalizedOtelOwnerConflict();
        var participant = new RecordingBatchParticipant();

        var result = await new RawRecordRetentionAdapter(fixture.Catalog, participant: participant)
            .DeleteAsync(fixture.Context);

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.Equal(["session-canonical"], participant.SessionIds);
    }

    private static int CountRows(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read()) count++;
        return count;
    }

    private static object Perturb(string parameterName, object value) => parameterName switch
    {
        "$retention_read_source_token" => PerturbToken(Assert.IsType<byte[]>(value)),
        "$retention_read_item_id" or "$retention_read_lease_owner" => Assert.IsType<string>(value) + "-different",
        "$retention_read_revision" or "$retention_read_lease_generation" => Convert.ToInt64(value, CultureInfo.InvariantCulture) + 1,
        "$retention_read_lease_kind" => Assert.IsType<string>(value) == "access" ? "operation" : "access",
        "$retention_read_lease_expires_at" => DateTimeOffset.Parse(Assert.IsType<string>(value), CultureInfo.InvariantCulture).AddTicks(1).ToString("O", CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(parameterName), parameterName, null),
    };

    private static byte[] PerturbToken(byte[] value)
    {
        var perturbed = value.ToArray();
        perturbed[0] ^= 0xff;
        return perturbed;
    }

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, RetentionCatalogStore catalog, RetentionDeleteContext context, long target, long sibling) =>
            (Path, Catalog, Context, Target, Sibling) = (path, catalog, context, target, sibling);

        private string Path { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal RetentionDeleteContext Context { get; }
        private long Target { get; }
        private long Sibling { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"raw-retention-adapter-{Guid.NewGuid():N}.db");
            var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var catalogContext = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var source = new RawTelemetryStore(path, catalogContext, time);
            source.CreateMonitorSchema();
            var target = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "target-trace", now, "{\"resource\":\"target\"}", "{\"payload\":\"target\"}"));
            var sibling = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "sibling-trace", now, "{\"resource\":\"sibling\"}", "{\"payload\":\"sibling\"}"));
            InsertProjections(path, target, now);

            var catalog = new RetentionCatalogStore(catalogContext, time);
            Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            var item = Text(path, "SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$source;", ("$source", target.ToString()));
            Execute(path, "UPDATE retention_items SET state='deletion_queued', revision=1, read_denied_at=$now, queued_at=$now WHERE item_id=$item;", ("$now", now.ToString("O")), ("$item", item));
            var claimResult = await catalog.TryClaimDeletionAsync(new RetentionWorkReference(item, 1, RetentionWorkKind.Queued), "raw-adapter", now, CancellationToken.None);
            Assert.Equal(RetentionClaimDisposition.Claimed, claimResult.Disposition);
            var claim = Assert.IsType<RetentionDeletionClaim>(claimResult.Claim);
            var intent = await catalog.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);
            Assert.Equal(RetentionIntentDisposition.Committed, intent.Disposition);
            return new Fixture(path, catalog, new RetentionDeleteContext(claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration, claim.SourceIdentity, null, intent.IntentCursor, CancellationToken.None), target, sibling);
        }

        internal IReadOnlyList<string> ProjectionSnapshot() =>
        [
            Snapshot("SELECT * FROM monitor_ingestions WHERE raw_record_id=$target;"),
            Snapshot("SELECT * FROM monitor_traces WHERE trace_id='target-trace';"),
            Snapshot("SELECT * FROM monitor_spans WHERE raw_record_id=$target;")
        ];

        internal void AddWorkspaceFactsForTargetRawRecord()
        {
            Execute(Path,
                "CREATE TABLE session_events(session_id TEXT NOT NULL,trace_id TEXT NULL,source_adapter TEXT NOT NULL,source_event_id TEXT NOT NULL);" +
                "CREATE TABLE local_workspace_session_search_facts(session_id TEXT NOT NULL,kind TEXT NOT NULL);" +
                "INSERT INTO session_events(session_id,trace_id,source_adapter,source_event_id) VALUES('session-1','target-trace','otel-exact','target-trace/span-1');" +
                "INSERT INTO local_workspace_session_search_facts(session_id,kind) VALUES('session-1','skill'),('session-1','tool');");
        }

        internal void AddAffectedSessions(int count)
        {
            using var connection = Open(Path);
            using (var transaction = connection.BeginTransaction())
            {
                CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore.InitializeSchema(
                    connection, transaction, DateTimeOffset.UnixEpoch);
                transaction.Commit();
            }
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<{count})
                INSERT INTO sessions
                SELECT printf('session-%03d',value),'active','partial',NULL,NULL,NULL,NULL,
                       '2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00'
                FROM n;
                WITH RECURSIVE n(value) AS (SELECT 2 UNION ALL SELECT value+1 FROM n WHERE value<{count})
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,total_tokens,projected_at)
                SELECT $target,'target-trace',printf('span-%03d',value),value-1,'tool.call',17,'2026-08-24T00:00:00.0000000+00:00' FROM n;
                WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<{count})
                INSERT INTO session_events(event_id,session_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
                SELECT printf('event-%03d',value),printf('session-%03d',value),'copilot-sdk','target-trace','otel-exact',
                       CASE value WHEN 1 THEN 'target-trace/span-1' ELSE 'target-trace/'||printf('span-%03d',value) END,
                       'otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured'
                FROM n;
                """;
            command.Parameters.AddWithValue("$target", Target);
            command.ExecuteNonQuery();
        }

        internal void AddNormalizedOtelOwnerConflict()
        {
            const string trace = "0123456789abcdef0123456789abcdef";
            const string span = "0123456789abcdef";
            using var connection = Open(Path);
            using (var transaction = connection.BeginTransaction())
            {
                CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore.InitializeSchema(
                    connection, transaction, DateTimeOffset.UnixEpoch);
                transaction.Commit();
            }
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE monitor_spans SET trace_id='{trace.ToUpperInvariant()}',span_id='{span.ToUpperInvariant()}',operation='execute_tool',category='tool_call'
                  WHERE raw_record_id=$target;
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,projected_at)
                  VALUES($sibling,'{trace}','{span}',0,'execute_tool','tool_call','2026-08-24T00:00:00.0000000+00:00');
                INSERT INTO sessions VALUES('session-canonical','active','partial',NULL,NULL,NULL,NULL,
                  '2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
                INSERT INTO session_runs VALUES('run-canonical','session-canonical','claude-code','native-run','{trace}',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
                  VALUES('event-canonical','session-canonical','run-canonical','claude-code','{trace}','otel-exact','{trace}/{span}',
                    'otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured');
                """;
            command.Parameters.AddWithValue("$target", Target);
            command.Parameters.AddWithValue("$sibling", Sibling);
            command.ExecuteNonQuery();
        }

        internal void InstallUnsupportedWorkspace(string state)
        {
            using var connection = Open(Path);
            using (var transaction = connection.BeginTransaction())
            {
                CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore.InitializeSchema(
                    connection, transaction, DateTimeOffset.UnixEpoch);
                transaction.Commit();
            }
            if (state == "v4")
            {
                foreach (var sql in LocalWorkspaceProjectionSchemaV1.ExactV4SchemaSql)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
                using var stamp = connection.CreateCommand();
                stamp.CommandText = "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',4);";
                stamp.ExecuteNonQuery();
            }
            else
            {
                LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
                using var mutation = connection.CreateCommand();
                mutation.CommandText = state == "partial"
                    ? "DROP TABLE local_workspace_node_edges;"
                    : "UPDATE schema_version SET version=6 WHERE component='local_workspace_projection';";
                mutation.ExecuteNonQuery();
            }
            using var fact = connection.CreateCommand();
            fact.CommandText = "INSERT OR REPLACE INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens) VALUES($target,0,7,17);";
            fact.Parameters.AddWithValue("$target", Target);
            fact.ExecuteNonQuery();
        }

        internal string[] RawAndWorkspaceMutationSnapshot() =>
        [
            Snapshot("SELECT * FROM raw_records WHERE id=$target;"),
            Snapshot("SELECT * FROM local_workspace_span_facts WHERE raw_record_id=$target;"),
            Snapshot("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item;"),
            Snapshot("SELECT state,revision,error_code FROM retention_items WHERE item_id=$item;")
        ];

        internal long Scalar(string sql) => Convert.ToInt64(ScalarValue(sql));
        internal string Text(string sql) => (string)ScalarValue(sql)!;
        private object? ScalarValue(string sql)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$target", Target);
            command.Parameters.AddWithValue("$sibling", Sibling);
            command.Parameters.AddWithValue("$item", Context.ItemId);
            return command.ExecuteScalar();
        }

        private string Snapshot(string sql)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$target", Target);
            command.Parameters.AddWithValue("$item", Context.ItemId);
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
            {
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    values.Add(reader.IsDBNull(ordinal)
                        ? $"{ordinal}:null"
                        : $"{ordinal}:{SnapshotValue(reader.GetValue(ordinal))}");
                }
            }

            return string.Join("|", values);
        }

        private static string SnapshotValue(object value) => value switch
        {
            byte[] bytes => Convert.ToHexString(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!
        };

        private static void InsertProjections(string path, long target, DateTimeOffset now)
        {
            Execute(path,
                "INSERT INTO monitor_ingestions(raw_record_id,received_at,source,trace_id,client_kind,span_count,projected_at,span_projected_at) VALUES($target,$now,'raw-otlp','target-trace','copilot',1,$now,$now);" +
                "INSERT INTO monitor_traces(trace_id,client_kind,span_count,projected_at,total_tokens,trace_status) VALUES('target-trace','copilot',1,$now,17,'ok');" +
                "INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,total_tokens,projected_at) VALUES($target,'target-trace','span-1',0,'tool.call',17,$now);",
                ("$target", target), ("$now", now.ToString("O")));
        }

        private static void Execute(string path, string sql, params (string Name, object Value)[] values)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        private static string Text(string path, string sql, params (string Name, object Value)[] values)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            return (string)command.ExecuteScalar()!;
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }

    private sealed class RemovingWorkspaceFactsParticipant : ILocalWorkspaceProjectionTransactionParticipant
    {
        internal string[] RemovedKinds { get; private set; } = [];

        public void RefreshSessions(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyCollection<string> sessionIds,
            DateTimeOffset now)
        {
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT kind FROM local_workspace_session_search_facts WHERE session_id='session-1' ORDER BY kind;";
            using (var reader = read.ExecuteReader())
            {
                var kinds = new List<string>();
                while (reader.Read()) kinds.Add(reader.GetString(0));
                RemovedKinds = kinds.ToArray();
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM local_workspace_session_search_facts WHERE session_id='session-1';";
            delete.ExecuteNonQuery();
        }

        public void CompleteSessionEventContentDeletion(SqliteConnection connection, SqliteTransaction transaction, string sourceItemId, DateTimeOffset now) { }
    }

    private sealed class RecordingBatchParticipant : ILocalWorkspaceProjectionTransactionParticipant
    {
        internal int PreflightCount { get; private set; }
        internal List<int> BatchSizes { get; } = [];
        internal List<string> SessionIds { get; } = [];

        public void ValidateInstallationState(SqliteConnection connection, SqliteTransaction transaction) => PreflightCount++;

        public void RefreshSessions(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyCollection<string> sessionIds,
            DateTimeOffset now)
        {
            BatchSizes.Add(sessionIds.Count);
            SessionIds.AddRange(sessionIds);
        }

        public void CompleteSessionEventContentDeletion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceItemId,
            DateTimeOffset now) { }
    }
}

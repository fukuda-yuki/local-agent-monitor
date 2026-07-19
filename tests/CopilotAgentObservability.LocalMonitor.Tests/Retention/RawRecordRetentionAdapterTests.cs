using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RawRecordRetentionAdapterTests
{
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
}

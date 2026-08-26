using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceSessionDetailSnapshotTests
{
    [Theory]
    [InlineData("Summary")]
    [InlineData("Timeline")]
    [InlineData("Node")]
    public async Task DetailCommandsAndFullScanWorkAreIndependentOfUnrelatedSessionCardinality(string kindName)
    {
        var one = await ObserveDetailRead(kindName, 1);
        var tenThousand = await ObserveDetailRead(kindName, 10_000);

        Assert.Equal(one.Sql, tenThousand.Sql);
        Assert.Equal(one.FullScanSteps, tenThousand.FullScanSteps);
        Assert.NotEmpty(one.Sql);
        Assert.Equal(1, one.PublicationLeaseCount);
        Assert.Equal(1, one.ConnectionCount);
        Assert.Equal(1, one.ReadTransactionCount);
        Assert.Equal(one.PublicationLeaseCount, tenThousand.PublicationLeaseCount);
        Assert.Equal(one.ConnectionCount, tenThousand.ConnectionCount);
        Assert.Equal(one.ReadTransactionCount, tenThousand.ReadTransactionCount);
    }

    [Fact]
    public async Task NodeReadRejectsAProjectionWithFourThousandNinetySevenTotalNodes()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('018f0000-0000-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-1','018f0000-0000-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        var rootId = LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT node_id FROM local_workspace_nodes;").Single();
        CloneUnrelatedNodes(connection, 4096);
        using var transaction = connection.BeginTransaction();
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load());

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: rootId), CancellationToken.None));

        Assert.Equal("workspace_too_large", error.Error);
    }

    [Fact]
    public async Task NodeReadRejectsAnAncestryCycleAsUnavailableInsteadOfTooLarge()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('018f0000-0000-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','not_captured','2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:00.0000000+00:00'); INSERT INTO session_runs VALUES('run-1','018f0000-0000-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'unknown');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        var rootId = LocalWorkspaceProjectionSchemaTests.Strings(connection, "SELECT node_id FROM local_workspace_nodes;").Single();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE local_workspace_nodes SET parent_node_id=node_id WHERE node_id=$node_id;";
            command.Parameters.AddWithValue("$node_id", rootId);
            command.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: FixedSkillRegistryGenerationAuthority.Load());

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Node, SessionId, NodeId: rootId), CancellationToken.None));

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public void RecordedTimelineCursorPermitsTheFullSignedTickDomainIncludingZero()
    {
        var key = new byte[32];
        var filter = new LocalMonitorV1TimelineFilter(SessionId, new string('1', 64), ExecutionId, null, 100);
        foreach (var ticks in new[] { long.MinValue, 0L, long.MaxValue })
        {
            var expected = new LocalMonitorV1TimelinePosition(0, ticks, 0, "node-00000000000000000000000000000001");
            var cursor = LocalMonitorV1TimelineCursor.Encode(key, filter, expected);
            Assert.True(LocalMonitorV1TimelineCursor.TryDecode(cursor, key, filter, out var actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TimelineExposesUnknownGroupAndRejectsAValidCursorForAnAbsentSibling()
    {
        var snapshot = Snapshot();
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

        var bytes = LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, null, 100, null, key);
        using var json = JsonDocument.Parse(bytes);
        var items = json.RootElement.GetProperty("items");
        Assert.Single(items.EnumerateArray());
        Assert.Equal("unknown_relation_group", items[0].GetProperty("kind").GetString());
        Assert.Equal(1, items[0].GetProperty("child_count").GetInt32());

        var filter = new LocalMonitorV1TimelineFilter(SessionId, snapshot.WorkspaceRevision, ExecutionId, null, 100);
        var absent = LocalMonitorV1TimelineCursor.Encode(key, filter,
            new LocalMonitorV1TimelinePosition(1, 0, 99, "node-ffffffffffffffffffffffffffffffff"));
        var error = Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, null, 100, absent, key));
        Assert.Equal("invalid_cursor", error.Error);
    }

    [Fact]
    public void SummaryUsesExactNativeSessionAndInstructionContentBindings()
    {
        var bytes = LocalMonitorV1SessionDetailApplication.SerializeSummary(Snapshot());
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"native_session_ids\":[\"native-session\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"native_session_ids\":[\"run-1\"]", text, StringComparison.Ordinal);
        Assert.Contains("\"instruction\":{\"state\":\"recorded\",\"label\":\"instruction\",\"additional_count\":0,\"content_available\":true}", text, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"copilot-sdk\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryTimelineAndNodeUsePersistedExactChildCountsWhenChildrenAreNotLoaded()
    {
        var snapshot = Snapshot();
        var root = snapshot.Detail.Nodes.Single(static node => node.SourceKind == "execution_root");
        var group = snapshot.Detail.Nodes.Single(static node => node.Kind == "unknown_relation_group");
        var exact = snapshot with
        {
            Detail = snapshot.Detail with
            {
                Executions = [snapshot.Detail.Executions.Single() with { ChildCount = 301 }],
                Nodes = [root with { ChildCount = 301 }, group with { ChildCount = 300 }],
            },
        };

        using var summary = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeSummary(exact));
        using var timeline = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(
            exact, ExecutionId, null, 100, null, new byte[32]));
        using var node = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeNode(exact, group.NodeId));

        Assert.Equal(301, summary.RootElement.GetProperty("executions")[0].GetProperty("child_count").GetInt32());
        Assert.Equal(300, timeline.RootElement.GetProperty("items")[0].GetProperty("child_count").GetInt32());
        Assert.Equal(300, node.RootElement.GetProperty("node").GetProperty("child_count").GetInt32());
    }

    private static LocalRepositorySessionDetailSnapshot Snapshot()
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null);
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var activity = new LocalWorkspaceActivityFacts(zero, zero, zero, zero, zero);
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none);
        var row = new LocalWorkspaceProjectionRow(SessionId, 0, 0, "recorded", "instruction", "completed", "full",
            new("recorded", ["copilot-sdk"]), new("recorded", ["model"]), activity, tokens,
            "recorded", "2026-08-26T00:00:00.0000000+00:00", "2026-08-26T00:00:01.0000000+00:00", "2026-08-26T00:00:01.0000000+00:00", 1000, [], "revision");
        var scope = new LocalRepositoryScopeSessionSnapshot(SessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned,
            LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null);
        var execution = new LocalWorkspaceExecutionDetail(ExecutionId, SessionId, "session_run", "run-1", 0, "completed", "completed", "model", null,
            "recorded", 638917344000000000, 638917344010000000, 1000, activity, tokens, "copilot-sdk", "1.0", 1);
        var root = Node("node-00000000000000000000000000000001", "execution_root", "execution", null, 0, activity, tokens);
        var group = Node("node-00000000000000000000000000000002", "unknown_relation_group", "unknown_relation_group", null, 1, activity, tokens) with { ChildCount = 1 };
        var child = Node("node-00000000000000000000000000000003", "session_event", "event", group.NodeId, 2, activity, tokens);
        var detail = new LocalWorkspaceSessionDetailContribution([execution], [root, group, child], [],
            [new(root.NodeId, "instruction", "available", "instruction-event", "revision")], ["native-session"], ["1.0"], "instruction-event", 0, "canonical");
        return new(scope, detail, new string('1', 64));
    }

    private static LocalWorkspaceNodeDetail Node(string id, string sourceKind, string kind, string? parent, long ordinal,
        LocalWorkspaceActivityFacts activity, LocalWorkspaceTokenFacts tokens) =>
        new(id, SessionId, ExecutionId, sourceKind, sourceKind == "execution_root" ? "run-1" : id, ordinal, parent,
            parent is null && kind == "unknown_relation_group" ? "unknown" : "exact", kind, "not_observed", null,
            "completed", "completed", "missing", null, null, null, activity, tokens, null, null, null);

    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string ExecutionId = "018f0000-0000-7000-8000-000000000003";

    private static void CloneUnrelatedNodes(SqliteConnection connection, int count)
    {
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(local_workspace_nodes);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        var expressions = columns.Select(static column => column switch
        {
            "node_id" => "$node_id",
            "source_kind" => "'session_event'",
            "source_identity" => "$source_identity",
            "source_ordinal" => "$source_ordinal",
            "parent_node_id" => "NULL",
            "relationship_authority" => "'unknown'",
            "kind" => "'event'",
            _ => column,
        });
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO local_workspace_nodes({string.Join(',', columns)}) SELECT {string.Join(',', expressions)} FROM local_workspace_nodes WHERE source_kind='execution_root';";
        var nodeId = command.Parameters.Add("$node_id", Microsoft.Data.Sqlite.SqliteType.Text);
        var sourceIdentity = command.Parameters.Add("$source_identity", Microsoft.Data.Sqlite.SqliteType.Text);
        var sourceOrdinal = command.Parameters.Add("$source_ordinal", Microsoft.Data.Sqlite.SqliteType.Integer);
        for (var index = 0; index < count; index++)
        {
            var identity = $"unrelated-{index:D4}";
            nodeId.Value = LocalWorkspaceProjectionStore.StableNodeId("session_event", identity);
            sourceIdentity.Value = identity;
            sourceOrdinal.Value = index + 1;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static async Task<ObservedRead> ObserveDetailRead(string kindName, int sessionCount)
    {
        using var temp = new MonitorTempDirectory();
        var targetSession = AlertCenterRouteTests.SeedPersistedTraceAndSession(
            temp, "00000000000000000000000000000001", authoritativeToolStatus: true);
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        var sessionColumns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(sessions);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) sessionColumns.Add(reader.GetString(1));
        }
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO sessions({string.Join(',', sessionColumns)}) SELECT $id,{string.Join(',', sessionColumns.Skip(1))} FROM sessions WHERE session_id=$target;";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            command.Parameters.AddWithValue("$target", targetSession.ToString("D"));
            for (var index = 1; index < sessionCount; index++)
            {
                id.Value = $"01990000-0000-7000-8000-{index:D12}";
                command.ExecuteNonQuery();
            }
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, DateTimeOffset.UnixEpoch,
                FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
        }
        using var rootCommand = connection.CreateCommand();
        rootCommand.CommandText = "SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND source_kind='execution_root';";
        rootCommand.Parameters.AddWithValue("$session", targetSession.ToString("D"));
        var rootId = (string)rootCommand.ExecuteScalar()!;
        connection.Close();
        var kind = Enum.Parse<LocalRepositorySessionDetailRequestKind>(kindName);
        var request = new LocalRepositorySessionDetailRequest(kind, targetSession.ToString("D"),
            NodeId: kind == LocalRepositorySessionDetailRequestKind.Node ? rootId : null);
        var gate = new CountingPublicationGate();
        var connectionCount = 0;
        NativeDetailObserver? observer = null;
        IReadOnlyList<string>? sql = null;
        IReadOnlyList<int>? fullScanSteps = null;
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            connectionOpenedObserver: opened => { connectionCount++; observer = new NativeDetailObserver(opened); },
            finalReturnObserver: () =>
            {
                sql = observer!.Sql.ToArray();
                fullScanSteps = observer.FullScanSteps.ToArray();
                observer.Dispose();
            },
            publicationGate: gate,
            skillRegistryAuthority: FixedSkillRegistryGenerationAuthority.Load());
        await service.ReadDetailAsync(request, CancellationToken.None);
        Assert.NotNull(sql);
        Assert.NotNull(fullScanSteps);
        return new(sql, fullScanSteps, gate.ReadCount, connectionCount,
            sql.Count(statement => statement.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record ObservedRead(
        IReadOnlyList<string> Sql,
        IReadOnlyList<int> FullScanSteps,
        int PublicationLeaseCount,
        int ConnectionCount,
        int ReadTransactionCount);

    private sealed class CountingPublicationGate : ILocalWorkspacePublicationGate
    {
        internal int ReadCount { get; private set; }
        public ValueTask<IAsyncDisposable> AcquireReadAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult<IAsyncDisposable>(new Lease());
        }
        public ValueTask<IAsyncDisposable> AcquireWriteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class NativeDetailObserver : IDisposable
    {
        private const uint Statement = 0x01;
        private const uint Profile = 0x02;
        private const int FullScanStep = 1;
        private readonly IntPtr database;
        private readonly TraceCallback callback;
        private readonly HashSet<IntPtr> topLevel = [];
        internal List<string> Sql { get; } = [];
        internal List<int> FullScanSteps { get; } = [];

        internal NativeDetailObserver(SqliteConnection connection)
        {
            database = connection.Handle.DangerousGetHandle();
            callback = Observe;
            Assert.Equal(0, sqlite3_trace_v2(database, Statement | Profile, callback, IntPtr.Zero));
        }

        private int Observe(uint kind, IntPtr context, IntPtr statement, IntPtr detail)
        {
            if (kind == Statement)
            {
                var sql = Marshal.PtrToStringUTF8(detail) ?? string.Empty;
                if (!sql.TrimStart().StartsWith("-- ", StringComparison.Ordinal))
                {
                    topLevel.Add(statement);
                    Sql.Add(string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
                }
            }
            else if (kind == Profile && topLevel.Remove(statement))
                FullScanSteps.Add(sqlite3_stmt_status(statement, FullScanStep, 0));
            return 0;
        }

        public void Dispose()
        {
            sqlite3_trace_v2(database, 0, null, IntPtr.Zero);
            GC.KeepAlive(callback);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TraceCallback(uint kind, IntPtr context, IntPtr statement, IntPtr detail);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_trace_v2(IntPtr database, uint mask, TraceCallback? callback, IntPtr context);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_stmt_status(IntPtr statement, int operation, int resetFlag);
    }

    private sealed class DirectReadTransaction(SqliteConnection connection, SqliteTransaction transaction) : ILocalRepositoryReadTransaction
    {
        public ValueTask<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read,
            CancellationToken cancellationToken) => read(connection, transaction, cancellationToken);
    }
}

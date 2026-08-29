using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonApplicationIntegrationTests
{
    [Fact]
    public async Task ArchivedInclusionEvidencePreservesEachFrozenMembershipIndicatorAcrossRestart()
    {
        using var db = new Database(); db.Initialize();
        const string active = "018f0000-0000-7000-8000-000000000001", archived = "018f0000-0000-7000-8000-000000000002";
        var store = new SqliteLocalComparisonStore(db.Path, new FixedClock());
        var application = new LocalMonitorV1ComparisonProductionApplication(new FakeInput([Input(active), Input(archived, archived: true)]), store, new FixedClock(), cursorKey: new byte[32]);
        var previewBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{active}\"],\"b\":[\"{archived}\"]}},\"include_archived\":true}}");
        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        using var previewJson = JsonDocument.Parse(preview.Entity);
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{active}\"],\"b\":[\"{archived}\"]}},\"include_archived\":true,\"selection_sha256\":\"{previewJson.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{previewJson.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var created = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        using var createdJson = JsonDocument.Parse(created.Entity);
        var comparisonId = createdJson.RootElement.GetProperty("comparison_id").GetString()!;
        var frozen = store.Read(LocalComparisonInputProjectionTests.RepositoryId, comparisonId, default).Snapshot!;
        var archivedResult = frozen.Results.Single(result => result.RowKey == "archived_inclusion");
        var restarted = new LocalMonitorV1ComparisonProductionApplication(new ThrowingInput(), new SqliteLocalComparisonStore(db.Path, new FixedClock()), new FixedClock(), cursorKey: new byte[32]);

        var evidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, comparisonId, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={archivedResult.ResultOrdinal}&field_key=condition", default);
        using var evidenceJson = JsonDocument.Parse(evidence.Entity);
        var items = evidenceJson.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal([active, archived], items.Select(item => item.GetProperty("session_id").GetString()));
        Assert.Equal(["0", "1"], items.Select(item => item.GetProperty("consumed_value").GetString()));
        Assert.All(items, item => Assert.Equal("included", item.GetProperty("state").GetString()));
        Assert.Equal([new string('1', 64), new string('2', 64)], items.Select(item => item.GetProperty("consumed_revision").GetString()));
    }

    [Fact]
    public async Task PreviewKeepsUnavailableSourceAndModelMetadataNull()
    {
        using var db = new Database(); db.Initialize();
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002";
        var first = Input(a);
        var row = (LocalWorkspaceProjectionRow)first.Session.Session;
        first = first with { Session = first.Session with { Session = row with { Sources = new("source_unsupported", []), Models = new("not_observed", []) } } };
        var application = new LocalMonitorV1ComparisonProductionApplication(new FakeInput([first, Input(b)]), new SqliteLocalComparisonStore(db.Path, new FixedClock()), new FixedClock(), cursorKey: new byte[32]);
        var body = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false}}");

        var response = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, body, "", default);
        using var json = JsonDocument.Parse(response.Entity);
        var metadata = json.RootElement.GetProperty("included")[0].GetProperty("metadata");

        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("source").ValueKind);
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("model").ValueKind);
    }

    [Fact]
    public async Task CreatePersistsDistinctSnapshotsAndRestartReadsFrozenState()
    {
        using var db = new Database(); db.Initialize();
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002";
        var authority = new FakeInput([Input(a), Input(b)]); var store = new SqliteLocalComparisonStore(db.Path, new FixedClock());
        var application = new LocalMonitorV1ComparisonProductionApplication(authority, store, new FixedClock(), cursorKey: new byte[32]);
        var previewBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false}}");
        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        using var p = JsonDocument.Parse(preview.Entity); var selection = p.RootElement.GetProperty("selection_sha256").GetString(); var revision = p.RootElement.GetProperty("preview_revision").GetString();
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false,\"selection_sha256\":\"{selection}\",\"preview_revision\":\"{revision}\"}}");

        var first = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        var second = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        using var one = JsonDocument.Parse(first.Entity); using var two = JsonDocument.Parse(second.Entity); var id = one.RootElement.GetProperty("comparison_id").GetString()!;
        Assert.Equal(201, first.StatusCode); Assert.Equal(201, second.StatusCode); Assert.NotEqual(id, two.RootElement.GetProperty("comparison_id").GetString());

        var restarted = new LocalMonitorV1ComparisonProductionApplication(new ThrowingInput(), new SqliteLocalComparisonStore(db.Path, new FixedClock()), new FixedClock(), cursorKey: new byte[32]);
        var read = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Read, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "", default);
        Assert.Equal(200, read.StatusCode); Assert.Contains(a, Encoding.UTF8.GetString(read.Entity), StringComparison.Ordinal); Assert.Contains(b, Encoding.UTF8.GetString(read.Entity), StringComparison.Ordinal);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Read, "018f0000-0000-7000-8000-000000000099", id, ReadOnlyMemory<byte>.Empty, "", default)).StatusCode);
        var rows = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "?family=subagent&q=helper", default);
        Assert.Equal(200, rows.StatusCode); Assert.Contains("helper", Encoding.UTF8.GetString(rows.Entity), StringComparison.Ordinal);
        var frozen = store.Read(LocalComparisonInputProjectionTests.RepositoryId, id, default).Snapshot!;
        var storedRow = frozen.Results.Single(result => result.RowKind == "subagent");
        using (var rowsJson = JsonDocument.Parse(rows.Entity))
        {
            var serialized = rowsJson.RootElement.GetProperty("items")[0].GetProperty("values")
                .EnumerateArray()
                .Select(value => new KeyValuePair<string, string>(
                    value.GetProperty("key").GetString()!,
                    value.GetProperty("value").GetString()!))
                .ToArray();
            Assert.Equal(storedRow.Values, serialized);
        }
        var inputTokens = frozen.Results.Single(result => result.RowKey == "input_tokens");
        var scalarEvidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={inputTokens.ResultOrdinal}&field_key=median", default);
        using (var scalarJson = JsonDocument.Parse(scalarEvidence.Entity))
        {
            var items = scalarJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.Equal("10", item.GetProperty("consumed_value").GetString()));
            Assert.All(items, item => Assert.Equal(new string(item.GetProperty("session_id").GetString()![^1], 64), item.GetProperty("consumed_revision").GetString()));
        }
        var unavailableResult = frozen.Results.Single(result => result.RowKey == "model_turn_count");
        var unavailableEvidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={unavailableResult.ResultOrdinal}&field_key=median", default);
        using (var unavailableJson = JsonDocument.Parse(unavailableEvidence.Entity))
        {
            var items = unavailableJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.Equal("unavailable", item.GetProperty("state").GetString()));
            Assert.All(items, item => Assert.Equal("source_unsupported", item.GetProperty("unavailable_reason").GetString()));
            Assert.All(items, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("consumed_value").ValueKind));
        }
        var namedEvidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={storedRow.ResultOrdinal}&field_key=count", default);
        using (var namedJson = JsonDocument.Parse(namedEvidence.Entity))
        {
            Assert.All(namedJson.RootElement.GetProperty("items").EnumerateArray(), item =>
            {
                Assert.NotEqual(JsonValueKind.Null, item.GetProperty("consumed_value").ValueKind);
                var execution = item.GetProperty("execution_id").GetString();
                var node = item.GetProperty("node_id").GetString();
                Assert.NotNull(execution);
                Assert.NotNull(node);
                Assert.Equal($"/sessions/{item.GetProperty("session_id").GetString()}?execution={execution}&node={node}", item.GetProperty("session_location").GetString());
            });
        }
        var acceptedFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["value"] = "input_tokens",
            ["available_count"] = "input_tokens",
            ["median"] = "input_tokens",
            ["minimum"] = "input_tokens",
            ["maximum"] = "input_tokens",
            ["total"] = "input_tokens",
            ["absolute_difference"] = "input_tokens",
            ["relative_difference_percent"] = "input_tokens",
            ["condition"] = "period",
            ["count"] = storedRow.RowKey,
            ["duration_ms"] = "session_duration",
            ["input_tokens"] = "input_tokens",
            ["output_tokens"] = "output_tokens",
            ["total_tokens"] = "total_tokens",
            ["cache_read"] = "cache_read_tokens",
            ["cache_creation"] = "cache_creation_tokens",
            ["new_input"] = "new_input_tokens",
            ["error_count"] = "error_count",
            ["retry_count"] = "retry_count",
        };
        foreach (var accepted in acceptedFields)
        {
            var acceptedResult = accepted.Key == "count"
                ? storedRow
                : frozen.Results.First(result => result.RowKey == accepted.Value);
            var response = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={acceptedResult.ResultOrdinal}&field_key={accepted.Key}", default);
            Assert.Equal(200, response.StatusCode);
            using var responseJson = JsonDocument.Parse(response.Entity);
            Assert.True(responseJson.RootElement.GetProperty("items").GetArrayLength() > 0, accepted.Key);
        }
        var period = frozen.Results.Single(result => result.RowKey == "period");
        var includedCount = frozen.Results.Single(result => result.RowKey == "included_session_count");
        var targetCount = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={includedCount.ResultOrdinal}&field_key=count", default);
        using (var targetJson = JsonDocument.Parse(targetCount.Entity))
        {
            Assert.Equal(200, targetCount.StatusCode);
            Assert.All(targetJson.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal("1", item.GetProperty("consumed_value").GetString()));
        }
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={includedCount.ResultOrdinal}&field_key=median", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={period.ResultOrdinal}&field_key=median", default)).StatusCode);
        var skillRow = frozen.Results.Single(result => result.RowKind == "skill");
        var toolRow = frozen.Results.Single(result => result.RowKind == "tool");
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={skillRow.ResultOrdinal}&field_key=count", default)).StatusCode);
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={toolRow.ResultOrdinal}&field_key=error_count", default)).StatusCode);
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={storedRow.ResultOrdinal}&field_key=total_tokens", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={skillRow.ResultOrdinal}&field_key=error_count", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={toolRow.ResultOrdinal}&field_key=total_tokens", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={storedRow.ResultOrdinal}&field_key=retry_count", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={inputTokens.ResultOrdinal}&field_key=count", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "?result_ordinal=999999&field_key=value", default)).StatusCode);
        var evidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "?result_ordinal=1&limit=1", default);
        using var evidenceJson = JsonDocument.Parse(evidence.Entity); var cursor = evidenceJson.RootElement.GetProperty("next_cursor").GetString(); Assert.NotNull(cursor);
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal=1&after={cursor}&limit=1", default)).StatusCode);
        Assert.Equal(400, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal=1&after={cursor}x&limit=1", default)).StatusCode);
    }

    private static LocalRepositoryComparisonSessionInput Input(string id, bool archived = false) { var s = LocalComparisonInputProjectionTests.ScopeSession(id, archived); return new(s, LocalComparisonInputProjectionTests.Detail(id, false), new string(id[^1], 64)); }
    private sealed class FakeInput(IReadOnlyList<LocalRepositoryComparisonSessionInput> sessions) : ILocalRepositoryComparisonInputSnapshotService { public ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) { var repo = new LocalRepositoryCatalogSnapshot(LocalComparisonInputProjectionTests.RepositoryId, "Repository", 1, null, 0, LocalArchiveState.Active, 1); return ValueTask.FromResult(new LocalRepositoryComparisonInputSnapshot(new(request, [repo], sessions.Select(x => x.Session).ToArray()), sessions)); } }
    private sealed class ThrowingInput : ILocalRepositoryComparisonInputSnapshotService { public ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("current_session_state_was_queried"); }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Database : IDisposable { private readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"comparison-app-{Guid.NewGuid():N}"); internal Database() { Directory.CreateDirectory(dir); Path = System.IO.Path.Combine(dir, "db.sqlite"); } internal string Path { get; } internal void Initialize() { new SqliteSessionStore(Path).CreateSchema(); using var c = Open(); LocalRepositoryCatalogSchemaV1.Ensure(c); LocalArchiveSchemaV1.Ensure(c); LocalWorkspaceProjectionSchemaV1.Ensure(c, DateTimeOffset.UnixEpoch); LocalComparisonSchemaV1.Ensure(c); using var cmd = c.CreateCommand(); cmd.CommandText = $"INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES('{LocalComparisonInputProjectionTests.RepositoryId}','Repository',1,'2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');"; cmd.ExecuteNonQuery(); } private SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); c.Open(); return c; } public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); } }
}

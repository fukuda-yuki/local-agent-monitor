using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonApplicationIntegrationTests
{
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
        var evidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "?result_ordinal=1&limit=1", default);
        using var evidenceJson = JsonDocument.Parse(evidence.Entity); var cursor = evidenceJson.RootElement.GetProperty("next_cursor").GetString(); Assert.NotNull(cursor);
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal=1&after={cursor}&limit=1", default)).StatusCode);
        Assert.Equal(400, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal=1&after={cursor}x&limit=1", default)).StatusCode);
    }

    private static LocalRepositoryComparisonSessionInput Input(string id) { var s = LocalComparisonInputProjectionTests.ScopeSession(id, false); return new(s, LocalComparisonInputProjectionTests.Detail(id, false), new string(id[^1], 64)); }
    private sealed class FakeInput(IReadOnlyList<LocalRepositoryComparisonSessionInput> sessions) : ILocalRepositoryComparisonInputSnapshotService { public ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) { var repo = new LocalRepositoryCatalogSnapshot(LocalComparisonInputProjectionTests.RepositoryId, "Repository", 1, null, 0, LocalArchiveState.Active, 1); return ValueTask.FromResult(new LocalRepositoryComparisonInputSnapshot(new(request, [repo], sessions.Select(x => x.Session).ToArray()), sessions)); } }
    private sealed class ThrowingInput : ILocalRepositoryComparisonInputSnapshotService { public ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("current_session_state_was_queried"); }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Database : IDisposable { private readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"comparison-app-{Guid.NewGuid():N}"); internal Database() { Directory.CreateDirectory(dir); Path = System.IO.Path.Combine(dir, "db.sqlite"); } internal string Path { get; } internal void Initialize() { new SqliteSessionStore(Path).CreateSchema(); using var c = Open(); LocalRepositoryCatalogSchemaV1.Ensure(c); LocalArchiveSchemaV1.Ensure(c); LocalWorkspaceProjectionSchemaV1.Ensure(c, DateTimeOffset.UnixEpoch); LocalComparisonSchemaV1.Ensure(c); using var cmd = c.CreateCommand(); cmd.CommandText = $"INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES('{LocalComparisonInputProjectionTests.RepositoryId}','Repository',1,'2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');"; cmd.ExecuteNonQuery(); } private SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); c.Open(); return c; } public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); } }
}

using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed class SqliteLocalAiRunRepositoryV1(string databasePath, string model,
    string configurationSha256, TimeProvider? timeProvider = null, RetentionCatalogStore? retentionCatalog = null) : ILocalAiRunRepositoryV1
{
    private readonly LocalAiAnalysisStoreV1 store = new(databasePath, retentionCatalog, timeProvider);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    internal static SqliteLocalAiRunRepositoryV1 Create(string databasePath, string model,
        string configurationSha256, TimeProvider? timeProvider = null, RetentionCatalogStore? retentionCatalog = null)
    {
        using var connection = Open(databasePath); LocalAiAnalysisSchemaV1.Ensure(connection);
        return new(databasePath, model, configurationSha256, timeProvider, retentionCatalog);
    }

    internal int CleanupExpiredNodes() => store.DeleteExpiredNodeRuns(clock.GetUtcNow());

    public LocalAiRunStatusV1 Create(LocalAiSnapshotProjectionV1 snapshot, int timeout)
    {
        store.InsertSnapshot(new(snapshot.SnapshotId, snapshot.ScopeKind, snapshot.SessionId, snapshot.NodeId,
            snapshot.AnchorId, snapshot.PayloadCanonicalJson, snapshot.EvidenceIndexCanonicalJson));
        var run = store.CreateRun(new(snapshot.SnapshotId, snapshot.ScopeKind, snapshot.SessionId, snapshot.NodeId,
            "github_copilot_sdk", model, configurationSha256, "local-ai-session-node-v1", clock.GetUtcNow(), timeout));
        return Read(run.RunId);
    }

    public void Start(string runId) => store.TransitionRun(runId, LocalAiRunStateV1.Running, occurredAt: clock.GetUtcNow());

    public LocalAiRunStatusV1 Complete(string runId, LocalAiProviderOutcomeV1 outcome, DateTimeOffset completedAt)
    {
        if (outcome.Kind == LocalAiProviderOutcomeKindV1.Partial)
            store.TransitionRun(runId, LocalAiRunStateV1.ProviderPartial, "provider_partial", clock.GetUtcNow());
        else if (outcome.Kind == LocalAiProviderOutcomeKindV1.Failed)
            store.TransitionRun(runId, LocalAiRunStateV1.ProviderFailed, "provider_failed", clock.GetUtcNow());
        else store.Complete(runId, outcome.ResultJson ?? [], completedAt);
        return Read(runId);
    }

    public LocalAiRunStatusV1 Fail(string runId, string errorCode)
    {
        var state = errorCode switch
        {
            "stale_snapshot" => LocalAiRunStateV1.StaleSnapshot,
            "scope_too_large" => LocalAiRunStateV1.ScopeTooLarge,
            "timed_out" => LocalAiRunStateV1.TimedOut,
            "provider_partial" => LocalAiRunStateV1.ProviderPartial,
            _ => LocalAiRunStateV1.ProviderFailed,
        };
        try { store.TransitionRun(runId, state, errorCode == "provider_failed" ? "provider_failed" : errorCode, clock.GetUtcNow()); }
        catch (InvalidOperationException) { }
        return Read(runId);
    }

    public bool Cancel(string runId)
    {
        try { store.TransitionRun(runId, LocalAiRunStateV1.Canceled, "canceled", clock.GetUtcNow()); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public LocalAiRunStatusV1 Read(string runId)
    {
        using var connection = Open(databasePath); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.run_id,r.state,r.scope_kind,r.session_id,r.node_id,r.error_code,x.result_json,
              r.requested_at,r.started_at,r.model,r.configuration_sha256,r.prompt_template_version
            FROM local_ai_runs r LEFT JOIN local_ai_results x ON x.result_id=r.result_id WHERE r.run_id=$run;
            """; command.Parameters.AddWithValue("$run", runId); using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("local_ai_run_missing");
        var scope=reader.GetString(2); var direct=reader.IsDBNull(6)?null:(byte[])reader[6]; var resultId=scope=="session"?ReadResultId(runId):null; var content=scope=="session"?(resultId is null?null:store.ReadRetainedResult(resultId)):direct;
        return new(reader.GetString(0), reader.GetString(1), scope, reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            content, reader.GetString(7), reader.IsDBNull(8)?null:reader.GetString(8),
            reader.GetString(9),reader.GetString(10),reader.GetString(11));
    }

    private string? ReadResultId(string runId){using var connection=Open(databasePath);using var command=connection.CreateCommand();command.CommandText="SELECT result_id FROM local_ai_runs WHERE run_id=$run;";command.Parameters.AddWithValue("$run",runId);return command.ExecuteScalar() as string;}

    public LocalAiReportPageResponseV1 Reports(string sessionId, int? limit, string? cursor, string currentPayloadSha256)
    {
        var page = store.GetSessionReports(sessionId, limit, cursor);
        using var connection = Open(databasePath); var rows = new List<LocalAiReportItemResponseV1>(page.Items.Count);
        foreach (var report in page.Items)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT s.payload_sha256 FROM local_ai_runs r JOIN local_ai_snapshots s ON s.snapshot_id=r.snapshot_id WHERE r.run_id=$run;";
            command.Parameters.AddWithValue("$run", report.RunId); var payloadHash = (string)command.ExecuteScalar()!;
            rows.Add(new(report.RunId, Wire(report.State), report.CanonicalResult, report.ContentState,
                !string.Equals(payloadHash, currentPayloadSha256, StringComparison.Ordinal)));
        }
        return new(rows, page.NextCursor);
    }

    private static string Wire(LocalAiRunStateV1 state) => state switch
    { LocalAiRunStateV1.Succeeded=>"succeeded", LocalAiRunStateV1.ZeroFindings=>"zero_findings", _=>throw new ArgumentOutOfRangeException(nameof(state)) };
    private static SqliteConnection Open(string path) { var connection = new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString()); connection.Open(); return connection; }
}

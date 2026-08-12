using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionTotalTokensAdmissionTests
{
    [Fact]
    public async Task Ingest_PersistsOnlyCanonicalBoundedCompletedTotalTokensWithoutProjectionChanges()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        const string json = """
            {
              "schema_version":1,
              "source_adapter":"copilot-sdk-stream",
              "source_surface":"copilot-sdk",
              "native_session_id":"total-tokens-session",
              "events":[
                {"source_event_id":"01-valid-zero","type":"subagent.completed","occurred_at":"2026-07-11T00:00:01Z","run_native_id":"run-1","payload":{"safe":"zero","totalTokens":0,"token":"remove-me","nested":{"password":"remove-me"},"text":"Bearer abc.def.ghi"}},
                {"source_event_id":"02-valid-max","type":"subagent.completed","occurred_at":"2026-07-11T00:00:02Z","payload":{"totalTokens":2147483647,"safe":"max"}},
                {"source_event_id":"03-overflow","type":"subagent.completed","occurred_at":"2026-07-11T00:00:03Z","payload":{"safe":"overflow","totalTokens":2147483648}},
                {"source_event_id":"04-negative","type":"subagent.completed","occurred_at":"2026-07-11T00:00:04Z","payload":{"safe":"negative","totalTokens":-1}},
                {"source_event_id":"05-negative-zero","type":"subagent.completed","occurred_at":"2026-07-11T00:00:05Z","payload":{"safe":"negative-zero","totalTokens":-0}},
                {"source_event_id":"06-fraction","type":"subagent.completed","occurred_at":"2026-07-11T00:00:06Z","payload":{"safe":"fraction","totalTokens":1.0}},
                {"source_event_id":"07-exponent","type":"subagent.completed","occurred_at":"2026-07-11T00:00:07Z","payload":{"safe":"exponent","totalTokens":1e3}},
                {"source_event_id":"08-string","type":"subagent.completed","occurred_at":"2026-07-11T00:00:08Z","payload":{"safe":"string","totalTokens":"1"}},
                {"source_event_id":"09-null","type":"subagent.completed","occurred_at":"2026-07-11T00:00:09Z","payload":{"safe":"null","totalTokens":null}},
                {"source_event_id":"10-boolean","type":"subagent.completed","occurred_at":"2026-07-11T00:00:10Z","payload":{"safe":"boolean","totalTokens":true}},
                {"source_event_id":"11-object","type":"subagent.completed","occurred_at":"2026-07-11T00:00:11Z","payload":{"safe":"object","totalTokens":{"value":1}}},
                {"source_event_id":"12-array","type":"subagent.completed","occurred_at":"2026-07-11T00:00:12Z","payload":{"safe":"array","totalTokens":[1]}},
                {"source_event_id":"13-wrong-event","type":"subagent.failed","occurred_at":"2026-07-11T00:00:13Z","payload":{"safe":"wrong-event","totalTokens":7}},
                {"source_event_id":"14-wrong-names","type":"subagent.completed","occurred_at":"2026-07-11T00:00:14Z","payload":{"TotalTokens":1,"totaltokens":2,"total_tokens":3,"totalToken":4,"totalTokens2":5,"myTotalTokens":6,"safe":"wrong-names"}},
                {"source_event_id":"15-nested","type":"subagent.completed","occurred_at":"2026-07-11T00:00:15Z","payload":{"nested":{"totalTokens":7},"array":[{"totalTokens":8}],"safe":"nested"}},
                {"source_event_id":"16-duplicate-valid","type":"subagent.completed","occurred_at":"2026-07-11T00:00:16Z","payload":{"totalTokens":1,"safe":"duplicate-valid","totalTokens":2}},
                {"source_event_id":"17-duplicate-mixed","type":"subagent.completed","occurred_at":"2026-07-11T00:00:17Z","payload":{"totalTokens":1,"safe":"duplicate-mixed","totalTokens":"2"}}
              ]
            }
            """;

        using var response = await host.Client.SendAsync(IngestRequest(json));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(
            [
                ("01-valid-zero", "{\"safe\":\"zero\",\"totalTokens\":0,\"nested\":{},\"text\":\"[REDACTED]\"}"),
                ("02-valid-max", "{\"totalTokens\":2147483647,\"safe\":\"max\"}"),
                ("03-overflow", "{\"safe\":\"overflow\"}"),
                ("04-negative", "{\"safe\":\"negative\"}"),
                ("05-negative-zero", "{\"safe\":\"negative-zero\"}"),
                ("06-fraction", "{\"safe\":\"fraction\"}"),
                ("07-exponent", "{\"safe\":\"exponent\"}"),
                ("08-string", "{\"safe\":\"string\"}"),
                ("09-null", "{\"safe\":\"null\"}"),
                ("10-boolean", "{\"safe\":\"boolean\"}"),
                ("11-object", "{\"safe\":\"object\"}"),
                ("12-array", "{\"safe\":\"array\"}"),
                ("13-wrong-event", "{\"safe\":\"wrong-event\"}"),
                ("14-wrong-names", "{\"safe\":\"wrong-names\"}"),
                ("15-nested", "{\"nested\":{},\"array\":[{}],\"safe\":\"nested\"}"),
                ("16-duplicate-valid", "{\"safe\":\"duplicate-valid\"}"),
                ("17-duplicate-mixed", "{\"safe\":\"duplicate-mixed\"}"),
            ],
            ReadContentRows(temp.DatabasePath));

        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        var sessionId = Assert.Single(list!.RootElement.GetProperty("items").EnumerateArray())
            .GetProperty("session_id").GetString();
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        var run = Assert.Single(detail!.RootElement.GetProperty("runs").EnumerateArray());
        Assert.Equal(
            ["run_id", "source_surface", "native_run_id", "trace_id", "parent_run_id", "model", "status", "started_at", "ended_at", "input_tokens", "output_tokens", "total_tokens"],
            run.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, run.GetProperty("total_tokens").ValueKind);
        Assert.All(detail.RootElement.GetProperty("events").EnumerateArray(), item =>
        {
            Assert.Equal(
                ["event_id", "run_id", "source_surface", "parent_event_id", "status", "type", "occurred_at", "content_state"],
                item.EnumerateObject().Select(property => property.Name));
            Assert.False(item.TryGetProperty("totalTokens", out _));
            Assert.False(item.TryGetProperty("payload", out _));
        });
    }

    [Fact]
    public async Task RuntimeBackup_RoundTripsAcceptedAndHistoricalMissingContentWithoutReplayBackfill()
    {
        using var temp = new MonitorTempDirectory();
        await using (var host = await MonitorTestHost.StartAsync(temp))
        {
            using var first = await host.Client.SendAsync(IngestRequest("""
                {"schema_version":1,"source_adapter":"copilot-sdk-stream","source_surface":"copilot-sdk","native_session_id":"backup-session","events":[
                  {"source_event_id":"accepted-zero","type":"subagent.completed","occurred_at":"2026-07-11T00:00:01Z","payload":{"totalTokens":0,"safe":"zero"}},
                  {"source_event_id":"historical-missing","type":"subagent.completed","occurred_at":"2026-07-11T00:00:02Z","payload":{"safe":"historical"}}
                ]}
                """));
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

            using var replay = await host.Client.SendAsync(IngestRequest("""
                {"schema_version":1,"source_adapter":"copilot-sdk-stream","source_surface":"copilot-sdk","native_session_id":"backup-session","events":[
                  {"source_event_id":"historical-missing","type":"subagent.completed","occurred_at":"2026-07-11T00:00:02Z","payload":{"totalTokens":1,"safe":"historical"}}
                ]}
                """));
            // D079 clause F: replaying an existing event with divergent captured content
            // aborts the whole batch fail-closed; the writer keeps the frozen 503
            // session_store_busy bytes instead of backfilling or overwriting content.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
            Assert.Equal(
                """{"error":"session_store_busy"}""",
                await replay.Content.ReadAsStringAsync());
        }

        Assert.Equal(
            [("accepted-zero", "{\"totalTokens\":0,\"safe\":\"zero\"}"), ("historical-missing", "{\"safe\":\"historical\"}")],
            ReadContentRows(temp.DatabasePath));
        var bundle = Path.Combine(temp.Path, "session-content.backup.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        var created = service.CreateAndPublish(temp.DatabasePath, bundle);
        Assert.True(created.Success, created.ErrorCode);
        var restoreDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "restored")).FullName;
        var restoredDatabase = Path.Combine(restoreDirectory, "raw-store.db");

        var restored = service.Restore(bundle, restoredDatabase, new RuntimeRestoreOptions());

        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(
            [("accepted-zero", "{\"totalTokens\":0,\"safe\":\"zero\"}"), ("historical-missing", "{\"safe\":\"historical\"}")],
            ReadContentRows(restoredDatabase));
        Assert.Equal(ReadSessionVersion(temp.DatabasePath), ReadSessionVersion(restoredDatabase));
    }

    private static HttpRequestMessage IngestRequest(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CAO-Session-Event-Version", "1");
        return request;
    }

    private static (string SourceEventId, string ContentJson)[] ReadContentRows(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event.source_event_id, content.content_json
            FROM session_events AS event
            INNER JOIN session_event_content AS content ON content.event_id=event.event_id
            ORDER BY event.source_event_id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<(string SourceEventId, string ContentJson)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return [.. rows];
    }

    private static long ReadSessionVersion(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version WHERE component='session';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}

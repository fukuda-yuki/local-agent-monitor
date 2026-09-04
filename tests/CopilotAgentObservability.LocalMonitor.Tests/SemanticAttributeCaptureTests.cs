using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SemanticAttributeCaptureTests
{
    [Fact]
    public void CurrentSchema_RejectsUnexpectedObjectOnCaptureTablesBeforeMutation()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE INDEX unrelated_name ON source_semantic_capture_keys(occurrence_count);";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(store.CreateSchema);
    }

    [Fact]
    public void GenuineV11Fixture_MigratesReopensAndBacksUpWithoutLosingCapture()
    {
        using var temp = new MonitorTempDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "SemanticCapture", "monitor-v11.sqlite");
        Assert.Equal("39e1d08c1c796c0f7d3c7006a0d49763ea0df750e92ea453b68679209b9d4e91", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture))));
        File.Copy(fixture, temp.DatabasePath);
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.UtcNow;
        var id = store.StartSemanticCapture("copilot-cli", now);
        Observe(temp.DatabasePath, "new.key", now);
        store.CompleteSemanticCapture("copilot-cli", id, now);
        store.CreateSchema();
        Assert.Single(store.ListSemanticCaptures(now));
        var archive = Path.Combine(temp.Path, "capture.zip");
        var service = new SqliteRuntimeBackupService();
        var result = service.CreateAndPublish(temp.DatabasePath, archive);
        Assert.True(result.Success, result.ErrorCode);
        var restoredPath = Path.Combine(temp.Path, "restored.sqlite");
        var restore = service.Restore(archive, restoredPath, new RuntimeRestoreOptions());
        Assert.True(restore.Success, restore.ErrorCode);
        Assert.Contains(Assert.Single(new SqliteSourceCompatibilityStore(restoredPath).ListSemanticCaptures(now)).AddedKeys,
            key => key.KeyHash == Hash("new.key"));
    }

    [Fact]
    public async Task Diagnostics_OffersAntiforgeryProtectedExplicitCaptureControls()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var response = await host.Client.GetAsync("/diagnostics");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("semantic-attribute-captures", html);
        Assert.Contains("__RequestVerificationToken", html);
        var rejected = await host.Client.PostAsync("/diagnostics?handler=StartSemanticCapture",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["sourceFamily"] = "copilot-cli" }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        var token = System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        var started = await host.Client.PostAsync("/diagnostics?handler=StartSemanticCapture",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["sourceFamily"] = "copilot-cli", ["__RequestVerificationToken"] = token }));
        Assert.True(started.IsSuccessStatusCode);
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        var capture = Assert.Single(store.ListSemanticCaptures(temp.TimeProvider.GetUtcNow()));
        const string payload = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"github-copilot"}},{"key":"service.version","value":{"stringValue":"SECRET_VALUE"}}]},"scopeSpans":[{"spans":[{"traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef","name":"chat","attributes":[{"key":"new.key","value":{"stringValue":"SECRET_VALUE"}}]}]}]}]}
        """;
        var ingested = await host.Client.PostAsync("/v1/traces", new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.True(ingested.IsSuccessStatusCode);
        var completed = await host.Client.PostAsync("/diagnostics?handler=CompleteSemanticCapture",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["sourceFamily"] = "copilot-cli", ["captureId"] = capture.CaptureId, ["__RequestVerificationToken"] = token }));
        Assert.True(completed.IsSuccessStatusCode);
        var finalHtml = await completed.Content.ReadAsStringAsync();
        Assert.Contains(Hash("new.key"), finalHtml);
        Assert.DoesNotContain("SECRET_VALUE", finalHtml);
        var boundedCapture = store.StartSemanticCapture("copilot-cli", temp.TimeProvider.GetUtcNow());
        var manyKeys = JsonSerializer.Serialize(new { resourceSpans = new[] { new {
            resource = new { attributes = new[] { new { key = "service.name", value = new { stringValue = "github-copilot" } } } },
            scopeSpans = new[] { new { spans = new[] { new { traceId = "0123456789abcdef0123456789abcdef", spanId = "fedcba9876543210",
                attributes = Enumerable.Range(0, 257).Select(index => new { key = $"addition.{index}", value = new { stringValue = "SECRET_VALUE" } }).ToArray() } } } }
        } } });
        var accepted = await host.Client.PostAsync("/v1/traces", new StringContent(manyKeys, Encoding.UTF8, "application/json"));
        Assert.True(accepted.IsSuccessStatusCode);
        store.CompleteSemanticCapture("copilot-cli", boundedCapture, temp.TimeProvider.GetUtcNow());
        var bounded = Assert.Single(store.ListSemanticCaptures(temp.TimeProvider.GetUtcNow()));
        Assert.True(bounded.Incomplete);
        Assert.Equal(256, bounded.Keys.Count);
        Assert.Empty(bounded.NotObservedKeys);
    }

    [Fact]
    public void Capture_CompletedComparisonIsBoundedAndNeverRetainsValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"semantic-{Guid.NewGuid():N}.sqlite");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var store = new SqliteSourceCompatibilityStore(path);
            store.CreateSchema();
            var id = store.StartSemanticCapture("copilot-cli", now);
            Assert.Equal(id, store.StartSemanticCapture("copilot-cli", now));
            Observe(path, "new.key", now);
            Assert.True(store.CompleteSemanticCapture("copilot-cli", id, now.AddMinutes(1)));
            var completed = Assert.Single(store.ListSemanticCaptures(now.AddMinutes(1)));
            Assert.False(completed.Incomplete);
            Assert.Contains(completed.AddedKeys, key => key.KeyHash == Hash("new.key"));
            Assert.Contains(Hash("gen_ai.tool.name"), completed.NotObservedKeys);
            Assert.DoesNotContain("SECRET_VALUE", JsonSerializer.Serialize(completed));
            new SqliteSourceCompatibilityStore(path).CreateSchema();
            Assert.Single(store.ListSemanticCaptures(now.AddMinutes(2)));
            Assert.Empty(store.ListSemanticCaptures(now.AddHours(25)));
            store.ExpireSemanticCaptures(now.AddHours(25));
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM source_semantic_capture_keys";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TruncatedOrInterruptedCapture_DoesNotEvaluateMissingKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"semantic-{Guid.NewGuid():N}.sqlite");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var store = new SqliteSourceCompatibilityStore(path);
            store.CreateSchema();
            var id = store.StartSemanticCapture("copilot-cli", now);
            for (var index = 0; index < 257; index++) Observe(path, $"key.{index}", now);
            Assert.True(store.CompleteSemanticCapture("copilot-cli", id, now));
            var completed = Assert.Single(store.ListSemanticCaptures(now));
            Assert.True(completed.Incomplete);
            Assert.Empty(completed.NotObservedKeys);
            Assert.Equal(256, completed.Keys.Count);
            var next = store.StartSemanticCapture("copilot-cli", now);
            Assert.False(store.CompleteSemanticCapture("copilot-cli", id, now));
            store.MarkSemanticCaptureGap();
            Observe(path, "known.key", now);
            Assert.True(store.CompleteSemanticCapture("copilot-cli", next, now));
            Assert.Empty(Assert.Single(store.ListSemanticCaptures(now)).NotObservedKeys);
        }
        finally { File.Delete(path); }
    }

    private static void Observe(string path, string key, DateTimeOffset now)
    {
        var payload = JsonSerializer.Serialize(new { resourceSpans = new[] { new {
            resource = new { attributes = new[] { new { key = "service.name", value = new { stringValue = "github-copilot" } } } },
            scopeSpans = new[] { new { spans = new[] { new { traceId = "cli", attributes = new[] { new { key, value = new { stringValue = "SECRET_VALUE" } } } } } } }
        } } });
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        SqliteSourceCompatibilityStore.ObserveSemanticKeys(connection, transaction,
            OtlpTraceSourceResolver.Resolve(payload), false, now);
        transaction.Commit();
    }

    [Fact]
    public void CompletionDuringIngestion_IsIncompleteAndDoesNotPoisonNextCapture()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.UtcNow;
        var id = store.StartSemanticCapture("copilot-cli", now);
        Observe(temp.DatabasePath, "new.key", now);
        store.BeginSemanticIngestion();
        store.CompleteSemanticCapture("copilot-cli", id, now);
        store.EndSemanticIngestion(succeeded: true);
        Assert.True(Assert.Single(store.ListSemanticCaptures(now)).Incomplete);
        var next = store.StartSemanticCapture("copilot-cli", now);
        Observe(temp.DatabasePath, "new.key", now);
        store.CompleteSemanticCapture("copilot-cli", next, now);
        Assert.False(Assert.Single(store.ListSemanticCaptures(now)).Incomplete);
    }

    [Fact]
    public void ProducerDroppedAttributes_MakesCaptureIncomplete()
    {
        const string payload = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"github-copilot"}}]},"scopeSpans":[{"spans":[{"traceId":"cli","droppedAttributesCount":1}]}]}]}
        """;
        Assert.True(Assert.Single(OtlpTraceSourceResolver.Resolve(payload)).AttributeInventoryIncomplete);
    }

    [Fact]
    public void MissingTraceIdentitySibling_MakesAttributeInventoryIncomplete()
    {
        const string payload = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"github-copilot"}}]},"scopeSpans":[{"spans":[{"traceId":"cli"},{"attributes":[{"key":"unattributable.key","value":{"stringValue":"SECRET"}}]}]}]}]}
        """;
        var resolution = Assert.Single(OtlpTraceSourceResolver.Resolve(payload));
        Assert.True(resolution.AttributeInventoryIncomplete);
        Assert.DoesNotContain(Hash("unattributable.key"), resolution.AttributeKeys.Keys);
    }

    [Fact]
    public void AttributeInventory_DoesNotPromoteMapValueKeysToAttributeKeys()
    {
        const string payload = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"github-copilot"}}]},"scopeSpans":[{"spans":[{"traceId":"cli","attributes":[{"key":"actual.attribute","value":{"kvlistValue":{"values":[{"key":"user.value.field","value":{"stringValue":"SECRET"}}]}}}]}]}]}]}
        """;
        var keys = Assert.Single(OtlpTraceSourceResolver.Resolve(payload)).AttributeKeys;
        Assert.Contains(Hash("actual.attribute"), keys.Keys);
        Assert.DoesNotContain(Hash("user.value.field"), keys.Keys);
    }

    [Fact]
    public void SourceResolution_CarriesOnlyHashedKeysWithoutCrossSourceContamination()
    {
        var payload = """
        {"resourceSpans":[
          {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"github-copilot"}}]},
           "scopeSpans":[{"spans":[{"traceId":"cli","attributes":[{"key":"CLI.only","value":{"stringValue":"SECRET_VALUE"}}]}]}]},
          {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"copilot-chat"}}]},
           "scopeSpans":[{"spans":[{"traceId":"vscode","events":[{"attributes":[{"key":"vscode.only","value":{"stringValue":"OTHER_SECRET"}}]}]}]}]}
        ]}
        """;
        var resolutions = OtlpTraceSourceResolver.Resolve(payload);
        var cli = JsonSerializer.Serialize(Assert.Single(resolutions, item => item.TraceId == "cli"));
        var vscode = JsonSerializer.Serialize(Assert.Single(resolutions, item => item.TraceId == "vscode"));
        Assert.Contains(Hash("CLI.only"), cli);
        Assert.DoesNotContain(Hash("vscode.only"), cli);
        Assert.Contains(Hash("vscode.only"), vscode);
        Assert.DoesNotContain(Hash("CLI.only"), vscode);
        Assert.DoesNotContain("SECRET", cli + vscode);
        Assert.DoesNotContain("CLI.only", cli + vscode);
    }

    private static string Hash(string key) => "sha256:" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes("source-structure-v1\0attribute_key\0" + key)));

    [Fact]
    public void Initialization_CreatesBoundedSemanticCaptureAuthority()
    {
        var path = Path.Combine(Path.GetTempPath(), $"semantic-{Guid.NewGuid():N}.sqlite");
        try
        {
            new SqliteSourceCompatibilityStore(path).CreateSchema();
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('source_semantic_captures','source_semantic_capture_keys');";
            Assert.Equal(2L, command.ExecuteScalar());
        }
        finally { File.Delete(path); }
    }
}

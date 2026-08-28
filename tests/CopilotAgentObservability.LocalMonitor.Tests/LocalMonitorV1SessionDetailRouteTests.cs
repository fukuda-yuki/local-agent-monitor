using System.Net;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionDetailRouteTests
{
    private const string SessionId="018f0000-0000-7000-8000-000000000001";

    [Fact]
    public async Task ProductionInstructionContentGetAndHeadReturnExactSelectedUtf8Bytes()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);
        RefreshDeterministicFullProjection(temp, stabilizeGolden: false);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        using var summaryResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsByteArrayAsync());
        var revision = summary.RootElement.GetProperty("workspace_revision").GetString();
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction";

        using var get = await host.Client.GetAsync(path);
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));

        const string expected = "Review the retained instruction";
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(expected, (await ContentEntity(get)).GetProperty("text").GetString());
        Assert.Equal("application/json; charset=utf-8", get.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], get.Headers.GetValues("Cache-Control"));
        Assert.Equal("local-monitor-node-content.response.v2", get.Headers.GetValues("X-Local-Monitor-Schema-Version").Single());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("instruction", "UserPromptSubmit", "{\"prompt\":\"こんにちは🌏\"}", "こんにちは🌏")]
    [InlineData("tool_input", "PreToolUse", "{\"tool_input\":{\"task\":\"inspect\"}}", "{\"task\":\"inspect\"}")]
    [InlineData("tool_input", "PreToolUse", "{\"tool_input\": { \"task\" : [ 1, true ] }}", "{ \"task\" : [ 1, true ] }")]
    [InlineData("tool_result", "PostToolUse", "{\"tool_response\":[1,true,null]}", "[1,true,null]")]
    [InlineData("tool_result", "PostToolUse", "{\"tool_response\":1.2300}", "1.2300")]
    [InlineData("instruction", "UserPromptSubmit", "{\"prompt\":\"line\\nquote: \\\"ok\\\"\"}", "line\nquote: \"ok\"")]
    [InlineData("error_message", "PostToolUseFailure", "{\"error\":\"failed\"}", "failed")]
    [InlineData("event_content", "tool.completed", "{\"number\":1,\"boolean\":true,\"nil\":null}", "{\"number\":1,\"boolean\":true,\"nil\":null}")]
    public async Task ProductionContentFixturePublishesAllSixPartsAsExactInertUtf8(
        string part, string eventType, string contentJson, string expectedText)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, contentJson: contentJson, eventType: eventType,
            sourceAdapter: part == "event_content" ? "github-copilot-vscode-otel" : "claude-code-hook",
            schemaFingerprint: part == "event_content" ? null : new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);
        string nodeId; using (var connection = Open(temp))
        {
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part=$execution AND source_item_id='018f0000-0000-7000-8000-000000000004';", SessionId, part);
            Assert.Equal("available", Scalar(connection, "SELECT availability_state FROM local_workspace_node_content_refs WHERE part=$execution AND source_item_id='018f0000-0000-7000-8000-000000000004';", SessionId, part));
        }
        using var summaryResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsByteArrayAsync()); var revision = summary.RootElement.GetProperty("workspace_revision").GetString();

        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part={part}");

        var actual = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {System.Text.Encoding.UTF8.GetString(actual)}");
        using var entity = JsonDocument.Parse(actual);
        Assert.Equal(expectedText, entity.RootElement.GetProperty("text").GetString());
        using var verification = Open(temp);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(expectedText), long.Parse(Scalar(verification,
            "SELECT CAST(selected_utf8_bytes AS TEXT) FROM local_workspace_node_content_refs WHERE part=$execution AND source_item_id='018f0000-0000-7000-8000-000000000004';", SessionId, part), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("{\"other\":\"safe\"}")]
    [InlineData("{\"prompt\":{}}")]
    public async Task SupportedClaudeCarrierWithoutTheExactStringPropertyServesOnlyTheWholeEvent(string contentJson)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, contentJson: contentJson, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        using var host = await StartProductionDetailRouteAsync(temp, null, null, ensureSchemas: false);
        string nodeId;
        using (var connection = Open(temp))
        {
            nodeId = Scalar(connection,
                "SELECT node_id FROM local_workspace_node_content_refs WHERE source_item_id='018f0000-0000-7000-8000-000000000004' AND part='event_content';", SessionId);
            Assert.Equal("whole_event", Scalar(connection,
                "SELECT locator_kind FROM local_workspace_node_content_refs WHERE node_id=$session AND part='event_content';", nodeId));
        }

        using var response = await host.Client.GetAsync(
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=event_content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentJson, (await ContentEntity(response)).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ExactPointerCarrierCannotBeDowngradedToWholeEventAfterRetentionAdmission()
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "pointer-downgrade-must-not-publish";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\",\"secret\":\"hidden\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
        using (var command = connection.CreateCommand())
        {
            nodeId = Scalar(connection,
                "SELECT node_id FROM local_workspace_node_content_refs WHERE source_item_id='018f0000-0000-7000-8000-000000000004';", SessionId);
            command.CommandText = """
                UPDATE local_workspace_node_content_refs
                SET part='event_content',locator_kind='whole_event',json_pointer=NULL,
                    selected_utf8_bytes=(SELECT length(CAST(content_json AS BLOB)) FROM session_event_content WHERE event_id=source_item_id)
                WHERE node_id=$node AND part='instruction';
                """;
            command.Parameters.AddWithValue("$node", nodeId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var service = CreateProductionDetailService(temp);
        var snapshot = await service.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Content, SessionId, NodeId: nodeId, ContentPart: "event_content"),
            CancellationToken.None);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var postGrantFailureObserved = false;
        LocalMonitorV1SessionDetailRoutes.Map(app, new FixedDetailService(snapshot), new byte[32],
            new LocalWorkspaceNodeContentReader(temp.RetentionContext, temp.TimeProvider,
                () => postGrantFailureObserved = true));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.GetAsync(
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={snapshot.WorkspaceRevision}&part=event_content");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.True(postGrantFailureObserved);
        using var proof = Open(temp);
        Assert.Equal("0", Scalar(proof,
            "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId));
    }

    [Theory]
    [InlineData("{\"prompt\":\"selected-marker\",\"other\":\"\\q\"}")]
    [InlineData("{\"prompt\":\"selected-marker\",\"other\":01}")]
    [InlineData("{\"prompt\":\"selected-marker\",\"other\":{\"nested\":[true,]}}")]
    public async Task PointerReadRejectsMalformedUnrelatedJsonAfterCommittedAdmission(string malformedJson)
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "selected-marker";
        SeedDeterministicSession(temp, full: true,
            contentJson: "{\"prompt\":\"" + marker + "\",\"other\":\"valid\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection,
                "SELECT node_id FROM local_workspace_node_content_refs WHERE source_item_id='018f0000-0000-7000-8000-000000000004' AND part='instruction';", SessionId);
        var postGrantFailureObserved = false;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, CreateProductionDetailService(temp), new byte[32],
            new LocalWorkspaceNodeContentReader(temp.RetentionContext, temp.TimeProvider,
                () => postGrantFailureObserved = true));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var revision = await Revision(client);
        using (var connection = Open(temp))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE session_event_content SET content_json=$content WHERE event_id='018f0000-0000-7000-8000-000000000004';";
            command.Parameters.AddWithValue("$content", malformedJson);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        using var response = await client.GetAsync(
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(postGrantFailureObserved);
        Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        using var proof = Open(temp);
        Assert.Equal("0", Scalar(proof,
            "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId));
    }

    [Theory]
    [InlineData("GET", "whole_event", 1_048_576, HttpStatusCode.OK)]
    [InlineData("HEAD", "whole_event", 1_048_576, HttpStatusCode.OK)]
    [InlineData("GET", "whole_event", 1_048_577, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("HEAD", "whole_event", 1_048_577, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("GET", "json_pointer", 1_048_576, HttpStatusCode.OK)]
    [InlineData("HEAD", "json_pointer", 1_048_576, HttpStatusCode.OK)]
    [InlineData("GET", "json_pointer", 1_048_577, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("HEAD", "json_pointer", 1_048_577, HttpStatusCode.RequestEntityTooLarge)]
    public async Task ProductionContentRouteEnforcesCompleteOneMiBBoundary(
        string method, string locatorKind, int size, HttpStatusCode expectedStatus)
    {
        using var temp = new MonitorTempDirectory();
        var wholeEvent = locatorKind == "whole_event";
        var contentJson = wholeEvent
            ? "\"" + new string('x', size - 2) + "\""
            : "{\"prompt\":\"" + new string('x', size) + "\"}";
        Assert.Equal(wholeEvent ? size : size + 13, System.Text.Encoding.UTF8.GetByteCount(contentJson));
        SeedDeterministicSession(temp, full: true, contentJson: contentJson,
            eventType: wholeEvent ? "tool.completed" : "UserPromptSubmit",
            sourceAdapter: wholeEvent ? "github-copilot-vscode-otel" : "claude-code-hook",
            schemaFingerprint: wholeEvent ? null : new string('0',64));
        StabilizeDeterministicContentOwner(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);
        RefreshDeterministicFullProjection(temp, stabilizeGolden: false);
        var part = wholeEvent ? "event_content" : "instruction";
        string nodeId;using(var connection=Open(temp))nodeId=Scalar(connection,"SELECT node_id FROM local_workspace_node_content_refs WHERE part=$execution AND source_item_id='018f0000-0000-7000-8000-000000000004';",SessionId,part);
        using var summaryResponse=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");using var summary=JsonDocument.Parse(await summaryResponse.Content.ReadAsByteArrayAsync());var revision=summary.RootElement.GetProperty("workspace_revision").GetString();
        using var response=await host.Client.SendAsync(new(new HttpMethod(method), $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part={part}"));
        Assert.Equal(expectedStatus,response.StatusCode);
        if(expectedStatus==HttpStatusCode.OK)
        {
            if(method=="HEAD") Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            else Assert.Equal(size,System.Text.Encoding.UTF8.GetByteCount((await ContentEntity(response)).GetProperty("text").GetString()!));
        }
        else Assert.Equal(method == "HEAD" ? "" : "{\"error\":\"raw_content_too_large\"}",await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task RealCatalogBusyBeforeGrantReturnsExactPersistenceBusyWithoutLeaseOrRaw(string method)
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "catalog-busy-raw-must-not-publish";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        using var host = await StartProductionDetailRouteAsync(temp);
        RefreshDeterministicFullProjection(temp, stabilizeGolden: false);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=instruction";
        using var blocker = Open(temp);
        using var blockingTransaction = blocker.BeginTransaction(deferred: false);

        using var response = await host.Client.SendAsync(new(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(28, response.Content.Headers.ContentLength);
        Assert.Equal(method == "HEAD" ? [] : "{\"error\":\"persistence_busy\"}"u8.ToArray(), await response.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain(marker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("0", Scalar(blocker, "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId));
    }

    [Theory]
    [InlineData("GET", (int)LocalMonitorNodeContentRoutePhase.DuringResponseWrite)]
    [InlineData("HEAD", (int)LocalMonitorNodeContentRoutePhase.AfterSuccessfulSealBeforeWrite)]
    public async Task SuccessfulResponseHoldsAccessLeaseThroughPublicationAndReleasesAtCompletion(
        string method, int publicationPhaseValue)
    {
        using var temp = new MonitorTempDirectory();
        temp.TimeProvider = new GenericRouteContentClock(new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero));
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        var observedPublicationLease = false;
        var options = new MonitorHostTestOptions
        {
            StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false,
            StartSessionOtelEnrichment = false, StartLocalRepositoryCatalogHostedService = false,
            UseUserSecrets = false,
            LocalMonitorNodeContentRouteCheckpoint = phase =>
            {
                if (phase != (LocalMonitorNodeContentRoutePhase)publicationPhaseValue) return;
                using var proof = Open(temp);
                observedPublicationLease = Scalar(proof,
                    "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId) == "1";
            },
        };
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: options);
        var revision = await Revision(host.Client);

        using var response = await host.Client.SendAsync(new(new HttpMethod(method),
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction"));
        _ = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(observedPublicationLease);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var completedProof = Open(temp);
            return Scalar(completedProof,
                "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId) == "0";
        }, TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task PostSealExpiryCannotDeleteRealContentUntilResponseAndLeaseComplete(string method)
    {
        using var temp = new MonitorTempDirectory();
        var clock = new GenericRouteContentClock(new DateTimeOffset(2026, 8, 27, 1, 2, 3, TimeSpan.Zero));
        temp.TimeProvider = clock;
        const string marker = "post-seal-retention-race-marker";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        using var sealedResponse = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        var options = new MonitorHostTestOptions
        {
            StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false,
            StartSessionOtelEnrichment = false, StartLocalRepositoryCatalogHostedService = false,
            StartRetentionCleanupWorker = false, UseUserSecrets = false,
            LocalMonitorNodeContentRouteCheckpoint = phase =>
            {
                if (phase != LocalMonitorNodeContentRoutePhase.AfterSuccessfulSealBeforeWrite) return;
                sealedResponse.Set();
                Assert.True(releaseWrite.Wait(TimeSpan.FromSeconds(30)));
            },
        };
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: options);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=instruction";

        var responseTask = Task.Run(() => host.Client.SendAsync(new(new HttpMethod(method), path)));
        var sealTask = Task.Run(() => sealedResponse.Wait(TimeSpan.FromSeconds(30)));
        var first = await Task.WhenAny(sealTask, responseTask);
        if (first == responseTask)
        {
            using var early = await responseTask;
            Assert.Fail($"Response completed before post-seal checkpoint: {(int)early.StatusCode} {await early.Content.ReadAsStringAsync()}");
        }
        Assert.True(await sealTask);
        var mutationError = TryExecuteDeleteNow(temp, 41);
        Assert.Null(mutationError);
        var worker = new RetentionCleanupWorker(new RetentionCleanupCoordinator(
            host.Services.GetRequiredService<RetentionCatalogStore>(),
            host.Services.GetRequiredService<RetentionAdapterRegistry>(), temp.TimeProvider), temp.TimeProvider);

        using (var proof = Open(temp))
        {
            Assert.Equal("deletion_queued", Scalar(proof, "SELECT state FROM retention_items WHERE source_item_id=$execution;", SessionId, "018f0000-0000-7000-8000-000000000004"));
            Assert.Equal("1", Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM session_event_content WHERE event_id=$execution;", SessionId, "018f0000-0000-7000-8000-000000000004"));
            Assert.Equal("1", Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId));
        }

        releaseWrite.Set();
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if(method == "HEAD") Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        else Assert.Equal(marker, (await ContentEntity(response)).GetProperty("text").GetString());
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var proof = Open(temp);
            return Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId) == "0";
        }, TimeSpan.FromSeconds(5)));
        await worker.RunOnceAsync(CancellationToken.None);
        RefreshContentProjection(temp);
        using (var proof = Open(temp))
        {
            Assert.Equal("deleted", Scalar(proof, "SELECT state FROM retention_items WHERE source_item_id=$execution;", SessionId, "018f0000-0000-7000-8000-000000000004"));
            Assert.Equal("0", Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM session_event_content WHERE event_id=$execution;", SessionId, "018f0000-0000-7000-8000-000000000004"));
            Assert.Equal("0", Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId));
        }
        var terminalPath = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=instruction";
        await AssertContentErrorParity(host.Client, terminalPath, 410, "raw_content_deleted");
    }

    [Fact]
    public async Task ClientCancellationAtFirstLargeBodyWriteReleasesLeaseAndWaitingDeleteCompletesWithoutPublishingRaw()
    {
        using var temp = new MonitorTempDirectory();
        temp.TimeProvider = new GenericRouteContentClock(new DateTimeOffset(2026, 8, 27, 1, 2, 3, TimeSpan.Zero));
        const string marker = "disconnect-write-raw-marker";
        var selected = marker + new string('x', 1_048_576 - marker.Length);
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + selected + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp); EnsureProductionProjectionSchemas(temp); RefreshContentProjection(temp);
        string nodeId; using (var connection = Open(temp)) nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        using var cancellation = new CancellationTokenSource();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new MonitorHostTestOptions
        {
            StartWriter=false,StartProjectionWorker=false,StartSessionWriter=false,StartSessionOtelEnrichment=false,
            StartLocalRepositoryCatalogHostedService=false,StartRetentionCleanupWorker=false,UseUserSecrets=false,
            LocalMonitorNodeContentRouteCheckpoint = phase =>
            {
                if (phase != LocalMonitorNodeContentRoutePhase.DuringResponseWrite) return;
                writeEntered.TrySetResult(); cancellation.Cancel();
            },
        };
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: options);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=instruction";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client.GetAsync(path, cancellation.Token));
        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var proof = Open(temp);
            return Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId) == "0";
        }, TimeSpan.FromSeconds(5)));
        Assert.Null(TryExecuteDeleteNow(temp, 51));
        var worker = new RetentionCleanupWorker(new RetentionCleanupCoordinator(
            host.Services.GetRequiredService<RetentionCatalogStore>(), host.Services.GetRequiredService<RetentionAdapterRegistry>(), temp.TimeProvider), temp.TimeProvider);
        await worker.RunOnceAsync(CancellationToken.None);
        using var proof = Open(temp);
        Assert.Equal("deleted", Scalar(proof, "SELECT state FROM retention_items WHERE source_item_id=$execution;", SessionId, "018f0000-0000-7000-8000-000000000004"));
        Assert.Equal("0", Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM session_event_content WHERE event_id=$execution;", SessionId, "018f0000-0000-7000-8000-000000000004"));
    }

    [Theory]
    [InlineData("GET", (int)LocalMonitorNodeContentRoutePhase.AfterCommittedGrantBeforeReference)]
    [InlineData("HEAD", (int)LocalMonitorNodeContentRoutePhase.AfterCommittedGrantBeforeReference)]
    [InlineData("GET", (int)LocalMonitorNodeContentRoutePhase.WhileContentReferenceHeld)]
    [InlineData("HEAD", (int)LocalMonitorNodeContentRoutePhase.WhileContentReferenceHeld)]
    [InlineData("GET", (int)LocalMonitorNodeContentRoutePhase.AfterBytesSelectedBeforeSeal)]
    [InlineData("HEAD", (int)LocalMonitorNodeContentRoutePhase.AfterBytesSelectedBeforeSeal)]
    public async Task RealCommittedLeaseExpiryBeforeSealReturnsExactLostWithoutRaw(string method, int phaseValue)
    {
        using var temp = new MonitorTempDirectory();
        var clock = new GenericRouteContentClock(new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero));
        temp.TimeProvider = clock;
        const string marker = "task5c-live-retention-marker";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        var options = new MonitorHostTestOptions
        {
            StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false,
            StartSessionOtelEnrichment = false, StartLocalRepositoryCatalogHostedService = false,
            UseUserSecrets = false,
            LocalMonitorNodeContentRouteCheckpoint = phase =>
            {
                if (phase == (LocalMonitorNodeContentRoutePhase)phaseValue)
                    clock.UtcNow += RetentionV1Constants.LeaseDuration;
            },
        };
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: options);
        RefreshDeterministicFullProjection(temp, stabilizeGolden: false);
        string nodeId; using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=instruction";

        using var response = await host.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(34, response.Content.Headers.ContentLength);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(method == "HEAD" ? [] : "{\"error\":\"raw_content_lease_lost\"}"u8.ToArray(), bytes);
        Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "expired_pending_deletion", 410, "raw_content_expired")]
    [InlineData("HEAD", "expired_pending_deletion", 410, "raw_content_expired")]
    [InlineData("GET", "deletion_queued", 403, "raw_content_read_denied")]
    [InlineData("HEAD", "deletion_queued", 403, "raw_content_read_denied")]
    [InlineData("GET", "deleted", 410, "raw_content_deleted")]
    [InlineData("HEAD", "deleted", 410, "raw_content_deleted")]
    public async Task RealPreGrantRetentionWinnerReturnsItsExactFixedResponseWithoutLeaseOrRaw(
        string method, string state, int status, string error)
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "pre-grant-raw-must-not-publish";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        string revision;
        using (var revisionHost = await StartProductionDetailRouteAsync(temp))
            revision = await Revision(revisionHost.Client);
        var checkpointCalls = 0;
        using var host = await StartProductionDetailRouteAsync(temp, phase =>
        {
            if (phase != LocalMonitorNodeContentRoutePhase.BeforeRetentionGrant) return;
            Interlocked.Increment(ref checkpointCalls);
            using var connection = Open(temp);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE retention_items SET state=$state,read_denied_at=$at,queued_at=$at,deleted_at=CASE WHEN $state='deleted' THEN $at ELSE deleted_at END,revision=revision+1 WHERE store_kind='session_event_content' AND source_item_id=$event;";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$at", "2026-08-26T01:02:06.0000000+00:00");
            command.Parameters.AddWithValue("$event", "018f0000-0000-7000-8000-000000000004");
            Assert.Equal(1, command.ExecuteNonQuery());
        });
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction";

        using var response = await host.Client.SendAsync(new(new HttpMethod(method), path));
        var expected = System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{error}\"}}");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
        Assert.Equal(method == "HEAD" ? [] : expected, await response.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain(marker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(1, checkpointCalls);
        using var proof = Open(temp);
        Assert.Equal("0", Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId));
    }

    [Fact]
    public async Task ProductionWholeEventMalformedJsonFailsClosedWithoutPartialBytes()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, contentJson: "not-json", eventType: "tool.completed",
            sourceAdapter: "github-copilot-vscode-otel");
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='event_content' AND source_item_id='018f0000-0000-7000-8000-000000000004';", SessionId);
        using var summaryResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsByteArrayAsync());
        var revision = summary.RootElement.GetProperty("workspace_revision").GetString();

        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=event_content");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("not_captured", 404, "raw_content_not_captured")]
    [InlineData("expired", 410, "raw_content_expired")]
    [InlineData("deleted", 410, "raw_content_deleted")]
    [InlineData("read_denied", 403, "raw_content_read_denied")]
    [InlineData("owner_mismatch", 503, "local_monitor_ui_unavailable")]
    [InlineData("store_mismatch", 503, "local_monitor_ui_unavailable")]
    [InlineData("captured_mismatch", 503, "local_monitor_ui_unavailable")]
    [InlineData("expiry_mismatch", 503, "local_monitor_ui_unavailable")]
    [InlineData("receipt_mismatch", 503, "local_monitor_ui_unavailable")]
    [InlineData("oversized", 413, "raw_content_too_large")]
    public async Task ProductionContentStatesHaveExactGetAndHeadParity(
        string state, int expectedStatus, string expectedError)
    {
        using var temp = new MonitorTempDirectory();
        var captured = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero);
        var json = state == "oversized" ? "{\"prompt\":\"" + new string('x', 1_048_577) + "\"}" : "{\"prompt\":\"retained\"}";
        SeedDeterministicSession(temp, full: state != "not_captured", contentJson: json,
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        if (state != "not_captured") StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);
        var revision = await Revision(host.Client);
        if (state is not ("not_captured" or "oversized"))
        {
            using var connection = Open(temp);
            using var command = connection.CreateCommand();
            command.CommandText = state switch
            {
                "expired" => "UPDATE session_event_content SET expires_at=$at WHERE event_id=$event; UPDATE retention_items SET expires_at=$at WHERE source_item_id=$event;",
                "deleted" => "UPDATE retention_items SET state='deleted',deleted_at=$at WHERE source_item_id=$event; INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) SELECT item_id,$at,$at FROM retention_items WHERE source_item_id=$event;",
                "read_denied" => "UPDATE retention_items SET read_denied_at=$at WHERE source_item_id=$event;",
                "owner_mismatch" => "DROP TRIGGER retention_session_event_content_token_immutable; UPDATE session_event_content SET retention_owner_token=zeroblob(32) WHERE event_id=$event;",
                "store_mismatch" => "PRAGMA foreign_keys=OFF; UPDATE retention_items SET store_instance_id='wrong-store' WHERE source_item_id=$event; PRAGMA foreign_keys=ON;",
                "captured_mismatch" => "UPDATE session_event_content SET captured_at='2026-08-26T01:02:04.0000000+00:00' WHERE event_id=$event;",
                "expiry_mismatch" => "UPDATE session_event_content SET expires_at='2026-09-26T01:02:03.0000000+00:00' WHERE event_id=$event;",
                "receipt_mismatch" => "UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE source_item_id=$event;",
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            };
            command.Parameters.AddWithValue("$event", "018f0000-0000-7000-8000-000000000004");
            command.Parameters.AddWithValue("$at", captured.ToString("O"));
            command.ExecuteNonQuery();
            if (state == "expired")
                RefreshDeterministicContentReceipt(connection, captured);
            if (state == "deleted")
            {
                using var deletion = connection.BeginTransaction();
                LocalWorkspaceProjectionStore.CompleteSessionEventContentDeletion(connection, deletion,
                    "018f0000-0000-7000-8000-000000000004", captured);
                deletion.Commit();
            }
        }
        if (state is not ("not_captured" or "oversized")) RefreshContentProjection(temp);
        if (state == "expired")
        {
            using var proof = Open(temp);
            Assert.Equal("expired", Scalar(proof,
                "SELECT availability_state FROM local_workspace_node_content_refs WHERE source_item_id=$session LIMIT 1;",
                "018f0000-0000-7000-8000-000000000004"));
            LocalWorkspaceProjectionStore.RegisterProjectionFunctions(proof);
            Assert.Equal("1", Scalar(proof, """
                SELECT CAST(local_workspace_retention_receipt_matches(i.store_instance_id,e.event_id,c.content_kind,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token,i.ownership_receipt) AS TEXT)
                FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
                JOIN retention_items i ON i.source_item_id=e.event_id WHERE e.event_id=$session;
                """, "018f0000-0000-7000-8000-000000000004"));
        }
        if (state is "deleted" or "expired")
            revision = await Revision(host.Client);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE source_item_id='018f0000-0000-7000-8000-000000000004';", SessionId);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction";

        await AssertContentErrorParity(host.Client, path, expectedStatus, expectedError);
    }

    [Fact]
    public async Task ProductionSummaryGetAndHeadMatchTheNonemptyGoldenExactly()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        using var host = await StartProductionDetailRouteAsync(temp);
        RefreshDeterministicFullProjection(temp);
        SeedDeterministicRepositoryAssignment(temp);
        var expected = File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(), "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData",
            "LocalMonitorV1SessionDetail", "summary-full.json"));

        using var get = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var head = await host.Client.SendAsync(new(HttpMethod.Head,
            $"/api/local-monitor/v1/sessions/{SessionId}/summary"));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var actual = await get.Content.ReadAsByteArrayAsync();
        Assert.True(expected.AsSpan().SequenceEqual(actual), System.Text.Encoding.UTF8.GetString(actual));
        using (var summary = JsonDocument.Parse(actual))
        {
            var session = summary.RootElement.GetProperty("session");
            Assert.Equal("completed", session.GetProperty("status").GetString());
            Assert.Equal("full", session.GetProperty("completeness").GetString());
            Assert.Equal("manual", session.GetProperty("assignment").GetProperty("authority").GetString());
            Assert.Equal("recorded", session.GetProperty("instruction").GetProperty("state").GetString());
            Assert.True(session.GetProperty("instruction").GetProperty("content_available").GetBoolean());
            Assert.Equal("complete", session.GetProperty("capture").GetProperty("state").GetString());
            foreach (var fact in new[] { "tool", "subagent", "error", "retry" })
                Assert.Equal("recorded", session.GetProperty("activity").GetProperty(fact).GetProperty("state").GetString());
        }
        Assert.Equal(expected.Length, get.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", get.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], get.Headers.GetValues("Cache-Control"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ProductionNonrecordedSummaryGetAndHeadMatchTheGoldenExactly()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicNonrecordedSession(temp);
        using var host = await StartProductionDetailRouteAsync(temp);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/summary";
        using var get = await host.Client.GetAsync(path);
        var actual = await get.Content.ReadAsByteArrayAsync();
        var expected = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "LocalMonitorV1SessionDetail",
            "summary-nonrecorded-evidence.json"));

        Assert.True(expected.AsSpan().SequenceEqual(actual), System.Text.Encoding.UTF8.GetString(actual));
        Assert.Equal(expected.Length, get.Content.Headers.ContentLength);
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("timeline-page.json", "timeline")]
    [InlineData("node-full.json", "root")]
    [InlineData("node-nested.json", "child")]
    public async Task ProductionDetailGetMatchesTheNonemptyGoldenExactly(string goldenName, string target)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp);
        using var host = await StartProductionDetailRouteAsync(temp);
        using var summaryResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsByteArrayAsync());
        var revision = summary.RootElement.GetProperty("workspace_revision").GetString()!;
        var execution = summary.RootElement.GetProperty("executions")[0];
        var executionId = execution.GetProperty("execution_id").GetString()!;
        var rootNodeId = execution.GetProperty("node_id").GetString()!;
        var timelinePath = $"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={revision}&execution_id={executionId}&limit=1";
        using var timelineResponse = await host.Client.GetAsync(timelinePath);
        var timelineBytes = await timelineResponse.Content.ReadAsByteArrayAsync();
        using var timeline = JsonDocument.Parse(timelineBytes);
        var childNodeId = timeline.RootElement.GetProperty("items")[0].GetProperty("node_id").GetString()!;
        var path = target switch
        {
            "timeline" => timelinePath,
            "root" => $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{rootNodeId}?workspace_revision={revision}",
            _ => $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{childNodeId}?workspace_revision={revision}",
        };
        using var get = target == "timeline" ? timelineResponse : await host.Client.GetAsync(path);
        var actual = target == "timeline" ? timelineBytes : await get.Content.ReadAsByteArrayAsync();
        var expected = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "LocalMonitorV1SessionDetail", goldenName));

        Assert.True(expected.AsSpan().SequenceEqual(actual), System.Text.Encoding.UTF8.GetString(actual));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(expected.Length, get.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", get.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], get.Headers.GetValues("Cache-Control"));
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", head.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], head.Headers.GetValues("Cache-Control"));
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryHttpPipelineAcceptsExactlyEightMiBForGetAndHead(bool head)
    {
        var baseline = Snapshot(SessionId, new([], [], [], [], [], [], null, null, "canonical", "generation"), new string('1', 64), string.Empty);
        var baselineLength = LocalMonitorV1SessionDetailApplication.SerializeSummary(baseline).Length;
        var snapshot = Snapshot(SessionId, baseline.Detail, baseline.WorkspaceRevision, new string('a', 8_388_608 - baselineLength));
        using var running = await StartDetailRouteAsync(new FixedDetailService(snapshot));

        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get,
            $"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var response = await running.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(8_388_608, response.Content.Headers.ContentLength);
        Assert.Equal(head ? 0 : 8_388_608, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryHttpPipelineRejectsEightMiBPlusOneWithFixedBody(bool head)
    {
        var baseline = Snapshot(SessionId, new([], [], [], [], [], [], null, null, "canonical", "generation"), new string('1', 64), string.Empty);
        var baselineLength = LocalMonitorV1SessionDetailApplication.SerializeSummary(baseline).Length;
        var snapshot = Snapshot(SessionId, baseline.Detail, baseline.WorkspaceRevision, new string('a', 8_388_609 - baselineLength));
        using var running = await StartDetailRouteAsync(new FixedDetailService(snapshot));

        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get,
            $"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var response = await running.Client.SendAsync(request);

        var expected = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"workspace_too_large\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
        Assert.Equal(head ? [] : expected, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
    }

    [Theory]
    [InlineData("GET", "executions")]
    [InlineData("HEAD", "executions")]
    [InlineData("GET", "nodes")]
    [InlineData("HEAD", "nodes")]
    public async Task ProductionContentRouteRejectsTwoHundredFiftySevenExecutionsOrFourThousandNinetySevenNodes(
        string method, string overflow)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        SeedWorkspaceCardinalityOverflow(temp, overflow);
        using var host = await StartProductionDetailRouteAsync(temp, null, null, ensureSchemas: false);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/node-00000000000000000000000000000001/content?workspace_revision={new string('1', 64)}&part=instruction";

        using var response = await host.Client.SendAsync(new(new HttpMethod(method), path));

        var expected = "{\"error\":\"workspace_too_large\"}"u8.ToArray();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Equal(method == "HEAD" ? [] : expected, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RetentionExpiryAtReadTimeChangesSummaryNodeAndRevisionWithoutProjectionRefresh()
    {
        using var temp = new MonitorTempDirectory();
        var clock = new GenericRouteContentClock(new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero));
        temp.TimeProvider = clock;
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        using var host = await StartProductionDetailRouteAsync(temp, null, null, ensureSchemas: false);

        using var beforeResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var before = JsonDocument.Parse(await beforeResponse.Content.ReadAsByteArrayAsync());
        var oldRevision = before.RootElement.GetProperty("workspace_revision").GetString()!;
        Assert.True(before.RootElement.GetProperty("session").GetProperty("instruction").GetProperty("content_available").GetBoolean());

        clock.UtcNow = new DateTimeOffset(2026, 9, 25, 1, 2, 3, TimeSpan.Zero);

        using var afterResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var after = JsonDocument.Parse(await afterResponse.Content.ReadAsByteArrayAsync());
        var newRevision = after.RootElement.GetProperty("workspace_revision").GetString()!;
        Assert.NotEqual(oldRevision, newRevision);
        Assert.False(after.RootElement.GetProperty("session").GetProperty("instruction").GetProperty("content_available").GetBoolean());
        using var nodeResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}?workspace_revision={newRevision}");
        using var node = JsonDocument.Parse(await nodeResponse.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain(node.RootElement.GetProperty("node").GetProperty("content_parts").EnumerateArray(),
            static part => part.GetString() == "instruction");
        await AssertContentErrorParity(host.Client,
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={oldRevision}&part=instruction",
            409, "workspace_snapshot_stale");
    }

    [Fact]
    public async Task SummaryReadsASeededSessionThroughTheProductionCoordinator()
    {
        using var temp=new MonitorTempDirectory();
        var session=AlertCenterRouteTests.SeedPersistedTraceAndSession(temp,"00000000000000000000000000000001",authoritativeToolStatus:true);
        await using var host=await MonitorTestHost.StartAsync(temp);

        _=await host.Services.GetRequiredService<ILocalRepositorySessionDetailSnapshotService>()
            .ReadDetailAsync(new(LocalRepositorySessionDetailRequestKind.Summary,session.ToString("D")),CancellationToken.None);

        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{session:D}/summary");

        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal("application/json; charset=utf-8",response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"],response.Headers.GetValues("Cache-Control"));
        var bytes=await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length,response.Content.Headers.ContentLength);
        Assert.Contains("\"schema_version\":\"local-monitor-session-summary.response.v2\"",System.Text.Encoding.UTF8.GetString(bytes),StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionSummaryDeduplicatesOneNativeSessionIdAcrossSourceSurfaces()
    {
        using var temp = new MonitorTempDirectory();
        var session = AlertCenterRouteTests.SeedPersistedTraceAndSession(temp, "00000000000000000000000000000001", authoritativeToolStatus: true);
        using (var connection = Open(temp))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at) VALUES($session,'claude-code','shared-native','native','2026-08-26T00:00:00.0000000+00:00'),($session,'copilot-sdk','shared-native','native','2026-08-26T00:00:00.0000000+00:00');";
            command.Parameters.AddWithValue("$session", session.ToString("D"));
            command.ExecuteNonQuery();
        }
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{session:D}/summary");
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(json.RootElement.GetProperty("technical_references").GetProperty("native_session_ids")
            .EnumerateArray(), static value => value.GetString() == "shared-native");
    }

    [Fact]
    public async Task ProductionTimelinePagesThreeHundredChildrenWhenParentSortsAfterThem()
    {
        using var temp = new MonitorTempDirectory();
        var session = AlertCenterRouteTests.SeedPersistedTraceAndSession(temp, "00000000000000000000000000000001", authoritativeToolStatus: true);
        string executionId;
        string parentId;
        using (var connection = Open(temp))
        {
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
            executionId = Scalar(connection, "SELECT execution_id FROM local_workspace_execution_headers WHERE session_id=$session LIMIT 1;", session.ToString("D"));
            var rootId = Scalar(connection, "SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND execution_id=$execution AND source_kind='execution_root';", session.ToString("D"), executionId);
            parentId = InsertClonedNodes(connection, session.ToString("D"), executionId, rootId, 301);
        }
        var key = new byte[32];
        var revision = new string('1', 64);
        var seen = new List<string>();
        string? after = null;
        using (var connection = Open(temp))
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
            using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = "CREATE TABLE IF NOT EXISTS skill_invocation_snapshots(snapshot_id TEXT,session_id TEXT); CREATE TABLE IF NOT EXISTS skill_invocation_snapshot_receipts(snapshot_id TEXT);";
                schema.ExecuteNonQuery();
            }
            var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: FixedSkillRegistryGenerationAuthority.Load());
            var summaryDetail = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Summary, session.ToString("D")), CancellationToken.None);
            using (var summary = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeSummary(Snapshot(session.ToString("D"), summaryDetail, revision))))
                Assert.True(summary.RootElement.GetProperty("executions")[0].GetProperty("child_count").GetInt32() > 0);
            var topLevelDetail = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Timeline, session.ToString("D"), executionId, Limit: 100), CancellationToken.None);
            using (var topLevel = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(
                Snapshot(session.ToString("D"), topLevelDetail, revision), executionId, null, 100, null, key)))
                Assert.Equal(301, topLevel.RootElement.GetProperty("items").EnumerateArray()
                    .Single(item => item.GetProperty("node_id").GetString() == parentId).GetProperty("child_count").GetInt32());
            do
            {
                LocalRepositoryTimelinePosition? position = null;
                if (after is not null)
                {
                    Assert.True(LocalMonitorV1TimelineCursor.TryDecode(after, key,
                        new(session.ToString("D"), revision, executionId, parentId, 100), out var decoded));
                    position = new(decoded.TimeGroup, decoded.UtcTicks, decoded.SourceOrdinal, decoded.NodeId);
                }
                var detail = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                    new(LocalRepositorySessionDetailRequestKind.Timeline, session.ToString("D"), executionId, parentId, position, 100), CancellationToken.None);
                using var page = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(
                    Snapshot(session.ToString("D"), detail, revision), executionId, parentId, 100, after, key));
                seen.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("node_id").GetString()!));
                after = page.RootElement.GetProperty("next_cursor").GetString();
            }
            while (after is not null);
        }

        Assert.Equal(301, seen.Count);
        Assert.Equal(301, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ProductionTimelineSyntheticSkillParentGetAndHeadReturnExactEmptyPage()
    {
        using var temp = new MonitorTempDirectory();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        temp.TimeProvider = clock;
        _ = temp.RetentionContext;
        using var fixture = new CurrentInvocationProjectionFixture(databasePath: temp.DatabasePath);
        fixture.SeedMismatchedPair("route-crossing", "Needle-Skill", "91", "a1", "92", "a2");
        fixture.RefreshWorkspace();
        clock.Advance(TimeSpan.FromDays(91));
        fixture.AdvancePastLatestSdkExpiry("route-crossing");
        fixture.RefreshWorkspace();
        using var host = await StartProductionDetailRouteAsync(
            temp, null, null, ensureSchemas: false, registryAuthority: fixture.RegistryAuthority);
        var sessionId = fixture.SessionId("route-crossing");
        using var summaryResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{sessionId}/summary");
        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsByteArrayAsync());
        var revision = summary.RootElement.GetProperty("workspace_revision").GetString()!;
        var executionId = summary.RootElement.GetProperty("executions")[0].GetProperty("execution_id").GetString()!;
        using var timelineResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{sessionId}/timeline?workspace_revision={revision}&execution_id={executionId}&limit=100");
        using var timeline = JsonDocument.Parse(await timelineResponse.Content.ReadAsByteArrayAsync());
        var synthetic = timeline.RootElement.GetProperty("items").EnumerateArray()
            .Single(static item => item.GetProperty("kind").GetString() == "skill").GetProperty("node_id").GetString()!;
        var path = $"/api/local-monitor/v1/sessions/{sessionId}/timeline?workspace_revision={revision}&execution_id={executionId}&parent_node_id={synthetic}&limit=100";

        using var get = await host.Client.GetAsync(path);
        var bytes = await get.Content.ReadAsByteArrayAsync();
        using var repeat = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(bytes, await repeat.Content.ReadAsByteArrayAsync());
        using (var page = JsonDocument.Parse(bytes))
        {
            Assert.Empty(page.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, page.RootElement.GetProperty("next_cursor").ValueKind);
        }
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentType?.ToString(), head.Content.Headers.ContentType?.ToString());
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ProductionRootTimelinePaginatesAntiCorrelatedExecutionsByFrozenTuple()
    {
        using var temp = new MonitorTempDirectory();
        var session = AlertCenterRouteTests.SeedPersistedTraceAndSession(temp, "00000000000000000000000000000001", authoritativeToolStatus: true);
        EnsureProductionProjectionSchemas(temp);
        using (var connection = Open(temp))
        {
            SeedAntiCorrelatedExecutionRoots(connection, session.ToString("D"), 201);
        }
        var revision = new string('1', 64);
        using var host = await StartDetailRouteAsync(new SqliteContributorDetailService(temp.DatabasePath, session.ToString("D"), revision));
        var seen = new List<(string NodeId, DateTimeOffset Start)>();
        string? after = null;
        do
        {
            var firstPage = after is null;
            var path = $"/api/local-monitor/v1/sessions/{session:D}/timeline?workspace_revision={revision}&limit=100" +
                (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var pageBytes = await response.Content.ReadAsByteArrayAsync();
            if (firstPage)
            {
                using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                Assert.Equal(pageBytes.Length, head.Content.Headers.ContentLength);
                Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            }
            using var page = JsonDocument.Parse(pageBytes);
            seen.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(item => (
                item.GetProperty("node_id").GetString()!,
                item.GetProperty("timing").GetProperty("started_at").GetDateTimeOffset())));
            after = page.RootElement.GetProperty("next_cursor").GetString();
        }
        while (after is not null);

        Assert.Equal(201, seen.Count);
        Assert.Equal(201, seen.Select(static item => item.NodeId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(seen.OrderBy(static item => item.Start).ThenBy(static item => item.NodeId, StringComparer.Ordinal), seen);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task WrongMethodWinsBeforeIdentifiersAndQuery(string method)
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var request=new HttpRequestMessage(new HttpMethod(method),"/api/local-monitor/v1/sessions/not-an-id/summary?unknown=secret");
        using var response=await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,response.StatusCode);Assert.Equal(["GET","HEAD"],response.Content.Headers.Allow);
        Assert.Equal("{\"error\":\"method_not_allowed\"}",await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SummaryRejectsQueryBeforeSessionLookup()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary?unknown=secret");
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal("{\"error\":\"invalid_request\"}",await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidAbsentSessionReturnsFixedNotFound()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);Assert.Equal("{\"error\":\"session_not_found\"}",await response.Content.ReadAsStringAsync());
        Assert.Equal(["no-store"],response.Headers.GetValues("Cache-Control"));
    }

    [Fact]
    public async Task HeadUsesGetErrorLengthWithoutEntity()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.SendAsync(new(HttpMethod.Head,$"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision=bad"));
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(27,response.Content.Headers.ContentLength);Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("GET", "Origin", "https://example.test")]
    [InlineData("HEAD", "Origin", "https://example.test")]
    [InlineData("GET", "Sec-Fetch-Site", "cross-site")]
    [InlineData("HEAD", "Sec-Fetch-Site", "same-site")]
    public async Task ContentCrossSiteGetAndHeadReturnExactCsrfRejectionWithoutCors(
        string method,
        string headerName,
        string headerValue)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var request = new HttpRequestMessage(new HttpMethod(method),
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/node-00000000000000000000000000000000/content?workspace_revision={new string('0', 64)}&part=instruction");
        request.Headers.TryAddWithoutValidation(headerName, headerValue);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(25, response.Content.Headers.ContentLength);
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Equal(method == "HEAD" ? [] : "{\"error\":\"csrf_rejected\"}"u8.ToArray(),
            await response.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("Access-Control-Allow-Origin", response.Headers.Select(header => header.Key));
        Assert.DoesNotContain("ETag", response.Headers.Select(header => header.Key));
        Assert.DoesNotContain("Location", response.Headers.Select(header => header.Key));
        Assert.DoesNotContain("Set-Cookie", response.Headers.Select(header => header.Key));
    }

    [Fact]
    public async Task SanitizedOnlyProductionCompositionDoesNotRegisterDetailOrContentEndpoints()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true);
        var patterns = host.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern?.StartsWith("/api/local-monitor/v1/sessions/", StringComparison.Ordinal) == true)
            .ToArray();

        using var summary = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var content = await host.Client.GetAsync(
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/node-00000000000000000000000000000000/content?workspace_revision={new string('0', 64)}&part=instruction");

        Assert.Empty(patterns);
        Assert.Equal(HttpStatusCode.NotFound, summary.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
        Assert.Empty(await summary.Content.ReadAsByteArrayAsync());
        Assert.Empty(await content.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RetainedRawBackupRestoreServesActualContentGetAndHead()
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "restored-retained-content-marker";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        RestoreRuntimeBackupOverSource(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=instruction";

        using var get = await host.Client.GetAsync(path);
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(marker, (await ContentEntity(get)).GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DeletedTombstoneBackupRestoreReturnsExactGoneWithoutResurrectionOrRaw()
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "deleted-restored-raw-must-not-publish";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        Assert.Null(TryExecuteDeleteNow(temp, 61));
        await using (var deletionHost = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false,
            StartSessionOtelEnrichment = false, StartLocalRepositoryCatalogHostedService = false,
            StartRetentionCleanupWorker = false, UseUserSecrets = false,
        }))
        {
            var worker = new RetentionCleanupWorker(new RetentionCleanupCoordinator(
                deletionHost.Services.GetRequiredService<RetentionCatalogStore>(),
                deletionHost.Services.GetRequiredService<RetentionAdapterRegistry>(), temp.TimeProvider), temp.TimeProvider);
            await worker.RunOnceAsync(CancellationToken.None);
        }
        RestoreRuntimeBackupOverSource(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);
        string nodeId;
        using (var connection = Open(temp))
        {
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
            Assert.Equal("0", Scalar(connection, "SELECT CAST(COUNT(*) AS TEXT) FROM session_event_content WHERE event_id=$execution;", SessionId,
                "018f0000-0000-7000-8000-000000000004"));
        }
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=instruction";

        using var get = await host.Client.GetAsync(path);
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));
        var getBytes = await get.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.Gone, get.StatusCode);
        Assert.Equal("{\"error\":\"raw_content_deleted\"}"u8.ToArray(), getBytes);
        Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(getBytes), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Gone, head.StatusCode);
        Assert.Equal(getBytes.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RawMarkerAndInjectedExceptionContentNeverEnterLogsOrActivityTelemetry()
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "raw-log-telemetry-secret-marker";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        var logs = new List<string>();
        var activities = new List<string>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(string.Join('|',
                new[] { activity.DisplayName, activity.OperationName }
                    .Concat(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")))),
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var options = new MonitorHostTestOptions
        {
            StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false,
            StartSessionOtelEnrichment = false, StartLocalRepositoryCatalogHostedService = false,
            UseUserSecrets = false,
            LocalMonitorNodeContentRouteCheckpoint = phase =>
            {
                if (phase == LocalMonitorNodeContentRoutePhase.BeforeRetentionGrant)
                    throw new InvalidOperationException("injected-exception-" + marker);
            },
            AdditionalServices = services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
                new CollectingLoggerProvider(logs)),
        };
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: options);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={await Revision(host.Client)}&part=instruction";

        using var response = await host.Client.GetAsync(path);
        var error = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{response.StatusCode}: {error}");
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", error);
        Assert.DoesNotContain(marker, string.Join('\n', logs), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, string.Join('\n', activities), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentOuterHostGuardWinsBeforeMethodIdentifiersAndQuery()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/local-monitor/v1/sessions/not-an-id/nodes/not-a-node/content?bad=query");
        request.Headers.Host = "example.com";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_host\"}", await response.Content.ReadAsStringAsync());
        Assert.Empty(response.Content.Headers.Allow);
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/sessions/not-an-id/nodes/not-a-node/content?bad=query", 405, "method_not_allowed")]
    [InlineData("/api/local-monitor/v1/sessions/not-an-id/nodes/not-a-node/content?bad=query", 400, "invalid_request", "GET")]
    public async Task ContentMethodAndIdentifierPrecedenceIsExact(string path, int status, string error, string method = "POST")
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var response = await host.Client.SendAsync(new(new HttpMethod(method), path));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal($"{{\"error\":\"{error}\"}}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("part=instruction&workspace_revision={0}")]
    [InlineData("workspace_revision={0}&part=instruction&part=instruction")]
    [InlineData("workspace_revision={0}&unknown=x")]
    [InlineData("workspace_revision={0}&part=Instruction")]
    public async Task ContentClosedQueryRejectsOrderDuplicatesUnknownAndInvalidPart(string queryTemplate)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var query = string.Format(System.Globalization.CultureInfo.InvariantCulture, queryTemplate, new string('0', 64));
        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/nodes/node-00000000000000000000000000000000/content?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ContentSessionRevisionNodeAndPartPrecedenceIsExact()
    {
        const string secondSession = "018f0000-0000-7000-8000-000000000021";
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        var observed = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero);
        var sessionStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider,
            new LocalWorkspacePublicationGate(),
            new LocalWorkspaceProjectionTransactionParticipant(FixedSkillRegistryGenerationAuthority.Load()));
        sessionStore.Write(new SessionWriteBatch(new SessionDetail(
            new ObservedSession(Guid.Parse(secondSession), ObservedSessionStatus.Active, SessionCompleteness.Partial,
                null, null, null, null, observed, SessionRawRetentionState.NotCaptured, observed, observed),
            [new SessionNativeId(Guid.Parse(secondSession), SessionSourceSurface.VisualStudioCode,
                "native-second-content-session", SessionBindingKind.Native, observed)], [], []), []));
        RefreshContentProjection(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE source_item_id='018f0000-0000-7000-8000-000000000004';", SessionId);
        var revision = await Revision(host.Client);
        var secondRevision = await Revision(host.Client, secondSession);
        var basePath = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content";

        await AssertContentErrorParity(host.Client,
            $"/api/local-monitor/v1/sessions/018f0000-0000-7000-8000-000000000099/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction",
            404, "session_not_found");
        await AssertContentErrorParity(host.Client,
            $"{basePath}?workspace_revision={new string('f', 64)}&part=instruction", 409, "workspace_snapshot_stale");
        await AssertContentErrorParity(host.Client,
            $"/api/local-monitor/v1/sessions/{secondSession}/nodes/{nodeId}/content?workspace_revision={secondRevision}&part=instruction",
            404, "node_not_found");
        await AssertContentErrorParity(host.Client,
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/node-00000000000000000000000000000000/content?workspace_revision={revision}&part=instruction",
            404, "node_not_found");
        await AssertContentErrorParity(host.Client,
            $"{basePath}?workspace_revision={revision}&part=tool_input", 404, "raw_content_not_captured");
    }

    [Fact]
    public async Task StaleTimelineAndNodeRevisionPrecedesMissingExecutionParentNodeAndCursorPosition()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string executionId;
        using (var connection = Open(temp))
            executionId = Scalar(connection,
                "SELECT execution_id FROM local_workspace_execution_headers WHERE session_id=$session LIMIT 1;", SessionId);
        using var host = await StartProductionDetailRouteAsync(temp, null, null, ensureSchemas: false);
        var staleRevision = new string('f', 64);
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var cursor = LocalMonitorV1TimelineCursor.Encode(key,
            new(SessionId, staleRevision, null, null, 100),
            new(0, 0, 1, "node-00000000000000000000000000000000"));
        var paths = new[]
        {
            $"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={staleRevision}&execution_id=018f0000-0000-7000-8000-000000000099&limit=100",
            $"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={staleRevision}&execution_id={executionId}&parent_node_id=node-00000000000000000000000000000000&limit=100",
            $"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={staleRevision}&after={cursor}&limit=100",
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/node-00000000000000000000000000000000?workspace_revision={staleRevision}",
        };

        foreach (var path in paths)
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("{\"error\":\"workspace_snapshot_stale\"}", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task StaleContentRevisionIsRejectedBeforeRealStoreReadOrLease(string method)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        using (var connection = Open(temp))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE local_workspace_node_content_refs SET part='instruction',locator_kind='json_pointer',json_pointer='/prompt',selected_utf8_bytes=31 WHERE source_item_id='018f0000-0000-7000-8000-000000000004' AND availability_state='available';";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        string staleRevision;
        using (var initial = await StartProductionDetailRouteAsync(temp))
            staleRevision = await Revision(initial.Client);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        using (var connection = Open(temp))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE local_workspace_node_content_refs SET availability_state='deleted',retention_owner_token=NULL WHERE node_id=$node AND part='instruction';";
            command.Parameters.AddWithValue("$node", nodeId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var realStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        var countingStore = System.Reflection.DispatchProxy.Create<ISessionStore, CountingSessionStoreProxy>();
        var proxy = (CountingSessionStoreProxy)(object)countingStore;
        proxy.Inner = realStore;
        using var host = await StartProductionDetailRouteAsync(temp, null, countingStore, ensureSchemas: false);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={staleRevision}&part=instruction";

        using var response = await host.Client.SendAsync(new(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(method == "HEAD" ? [] : System.Text.Encoding.UTF8.GetBytes("{\"error\":\"workspace_snapshot_stale\"}"), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, proxy.ReadContentCount);
    }

    [Fact]
    public async Task TimelinePassesTheClosedRequestShapeIntoTheSharedCoordinator()
    {
        var service = new CapturingDetailService();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, service, new byte[32]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={new string('1',64)}&execution_id=018f0000-0000-7000-8000-000000000003&parent_node_id=node-00000000000000000000000000000001&limit=17");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var request = Assert.IsType<LocalRepositorySessionDetailRequest>(service.Request);
        Assert.Equal(LocalRepositorySessionDetailRequestKind.Timeline, request.Kind);
        Assert.Equal(SessionId, request.SessionId);
        Assert.Equal("018f0000-0000-7000-8000-000000000003", request.ExecutionId);
        Assert.Equal("node-00000000000000000000000000000001", request.ParentNodeId);
        Assert.Equal(17, request.Limit);
        Assert.Null(request.After);
        Assert.Equal(new string('1', 64), request.ExpectedWorkspaceRevision);
    }

    private static void RestoreRuntimeBackupOverSource(MonitorTempDirectory temp)
    {
        var bundle = Path.Combine(temp.Path, "session-detail-backup.zip");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        var initialization = service.InitializeForMonitor(temp.DatabasePath);
        initialization.Lease?.Dispose();
        Assert.True(initialization.Result.Success, initialization.Result.ErrorCode);
        var created = service.CreateAndPublish(temp.DatabasePath, bundle);
        Assert.True(created.Success, created.ErrorCode);
        File.Delete(temp.DatabasePath);
        File.Delete(temp.DatabasePath + "-wal");
        File.Delete(temp.DatabasePath + "-shm");
        var restored = service.Restore(bundle, temp.DatabasePath, new RuntimeRestoreOptions());
        Assert.True(restored.Success, restored.ErrorCode);
    }

    private sealed class CollectingLoggerProvider(List<string> entries) : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, entries);
        public void Dispose() { }

        private sealed class CollectingLogger(string category, List<string> entries) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                    entries.Add($"{category}|{logLevel}|{eventId.Id}|{formatter(state, exception)}|{exception?.GetType().Name}");
            }
        }
    }

    private sealed class CapturingDetailService : ILocalRepositorySessionDetailSnapshotService
    {
        internal LocalRepositorySessionDetailRequest? Request { get; private set; }
        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(LocalRepositorySessionDetailRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            throw new LocalWorkspaceSessionDetailException("session_not_found");
        }
    }

    private sealed class FixedDetailService(LocalRepositorySessionDetailSnapshot snapshot) : ILocalRepositorySessionDetailSnapshotService
    {
        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(LocalRepositorySessionDetailRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);
    }

    private sealed class SqliteContributorDetailService(string databasePath, string sessionId, string revision)
        : ILocalRepositorySessionDetailSnapshotService
    {
        public async ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(
            LocalRepositorySessionDetailRequest request, CancellationToken cancellationToken)
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);
            LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
            using var transaction = connection.BeginTransaction(deferred: true);
            var contributorRequest = request with { ExpectedWorkspaceRevision = null };
            var detail = await new LocalWorkspaceSessionDetailSnapshotContributor(
                registryAuthority: FixedSkillRegistryGenerationAuthority.Load()).ReadAsync(
                    new DirectReadTransaction(connection, transaction), contributorRequest, cancellationToken);
            return Snapshot(sessionId, detail, revision);
        }
    }

    private static async Task<RunningDetailRoute> StartDetailRouteAsync(ILocalRepositorySessionDetailSnapshotService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, service, new byte[32]);
        await app.StartAsync();
        return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }

    private static async Task<RunningDetailRoute> StartProductionDetailRouteAsync(MonitorTempDirectory temp)
        => await StartProductionDetailRouteAsync(temp, null);

    private static async Task<RunningDetailRoute> StartProductionDetailRouteAsync(
        MonitorTempDirectory temp,
        Action<LocalMonitorNodeContentRoutePhase>? contentCheckpoint)
        => await StartProductionDetailRouteAsync(temp, contentCheckpoint, null);

    private static async Task<RunningDetailRoute> StartProductionDetailRouteAsync(
        MonitorTempDirectory temp,
        Action<LocalMonitorNodeContentRoutePhase>? contentCheckpoint,
        ISessionStore? sessionStore,
        bool ensureSchemas = true,
        ISkillRegistryGenerationAuthority? registryAuthority = null)
    {
        if (ensureSchemas) EnsureProductionProjectionSchemas(temp);
        var service = CreateProductionDetailService(temp, registryAuthority);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, service, Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray(),
            new LocalWorkspaceNodeContentReader(temp.RetentionContext, temp.TimeProvider), contentCheckpoint);
        await app.StartAsync();
        return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }

    private static SqliteLocalRepositoryScopeSnapshotService CreateProductionDetailService(
        MonitorTempDirectory temp,
        ISkillRegistryGenerationAuthority? registryAuthority = null)
    {
        registryAuthority ??= FixedSkillRegistryGenerationAuthority.Load();
        return
        new(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(
                registryAuthority: registryAuthority, timeProvider: temp.TimeProvider),
            skillRegistryAuthority: registryAuthority);
    }

    public class CountingSessionStoreProxy : System.Reflection.DispatchProxy
    {
        internal ISessionStore Inner { get; set; } = null!;
        internal int ReadContentCount { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISessionStore.ReadContentAsync)) ReadContentCount++;
            try { return targetMethod!.Invoke(Inner, args); }
            catch (System.Reflection.TargetInvocationException exception) { throw exception.InnerException!; }
        }
    }

    private static void EnsureProductionProjectionSchemas(MonitorTempDirectory temp)
    {
        using (var connection = Open(temp))
        {
            using (var transaction = connection.BeginTransaction())
            {
                SkillProjectionSchemaV1.Ensure(connection, transaction);
                SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
                LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
                LocalArchiveSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch, FixedSkillRegistryGenerationAuthority.Load());
        }
    }

    private static void SeedWorkspaceCardinalityOverflow(MonitorTempDirectory temp, string overflow)
    {
        using var connection = Open(temp);
        var table = overflow == "executions" ? "local_workspace_execution_headers" : "local_workspace_nodes";
        var target = overflow == "executions" ? 257 : 4097;
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        var expressions = columns.Select(column => column switch
        {
            "execution_id" when table == "local_workspace_execution_headers" => "$execution_id",
            "node_id" => "$node_id",
            "source_identity" => "$source_identity",
            "source_ordinal" => "$source_ordinal",
            "source_kind" when table == "local_workspace_nodes" => "'session_event'",
            "parent_node_id" => "NULL",
            "relationship_authority" => "'unknown'",
            "kind" => "'event'",
            _ => column,
        }).ToArray();
        using var transaction = connection.BeginTransaction();
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"SELECT COUNT(*) FROM {table} WHERE session_id=$session;";
        count.Parameters.AddWithValue("$session", SessionId);
        var existing = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table}({string.Join(',', columns)}) SELECT {string.Join(',', expressions)} FROM {table} WHERE session_id=$session LIMIT 1;";
        command.Parameters.AddWithValue("$session", SessionId);
        var executionId = command.Parameters.Add("$execution_id", SqliteType.Text);
        var nodeId = command.Parameters.Add("$node_id", SqliteType.Text);
        var sourceIdentity = command.Parameters.Add("$source_identity", SqliteType.Text);
        var sourceOrdinal = command.Parameters.Add("$source_ordinal", SqliteType.Integer);
        for (var index = existing; index < target; index++)
        {
            var identity = $"overflow-{overflow}-{index:D4}";
            executionId.Value = LocalWorkspaceProjectionStore.StableExecutionId(SessionId, "session_run", identity);
            nodeId.Value = LocalWorkspaceProjectionStore.StableNodeId("session_event", identity);
            sourceIdentity.Value = identity;
            sourceOrdinal.Value = index;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void SeedAntiCorrelatedExecutionRoots(SqliteConnection connection, string sessionId, int total)
    {
        LocalWorkspaceProjectionStore.RegisterProjectionFunctions(connection);
        using var transaction = connection.BeginTransaction();
        for (var index = 1; index < total; index++)
        {
            var reverse = total - index;
            var sourceIdentity = $"anti-{reverse:D4}";
            var startedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index).ToString("O");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO session_runs(run_id,session_id,source_surface,started_at,status)
                VALUES($source,$session,'copilot-sdk',$started_at,'active');
                """;
            command.Parameters.AddWithValue("$source", sourceIdentity);
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$started_at", startedAt);
            command.ExecuteNonQuery();
        }
        LocalWorkspaceProjectionStore.RefreshSessions(
            connection, transaction, [sessionId], new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
            FixedSkillRegistryGenerationAuthority.Load());
        transaction.Commit();
    }

    private sealed class RunningDetailRoute(WebApplication app, HttpClient client) : IDisposable
    {
        internal HttpClient Client { get; } = client;
        public void Dispose()
        {
            Client.Dispose();
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string Scalar(SqliteConnection connection, string sql, string sessionId, string? executionId = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$execution", (object?)executionId ?? DBNull.Value);
        return (string)command.ExecuteScalar()!;
    }

    private static async Task<string> Revision(HttpClient client, string sessionId = SessionId)
    {
        using var response = await client.GetAsync($"/api/local-monitor/v1/sessions/{sessionId}/summary");
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return document.RootElement.GetProperty("workspace_revision").GetString()!;
    }

    private static async Task<JsonElement> ContentEntity(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var root = document.RootElement.Clone();
        Assert.Equal("local-monitor-node-content.response.v2", root.GetProperty("schema_version").GetString());
        Assert.False(root.GetProperty("truncation").GetBoolean());
        return root;
    }

    private static async Task AssertContentErrorParity(HttpClient client, string path, int status, string error)
    {
        var expected = System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{error}\"}}");
        using var get = await client.GetAsync(path);
        using var head = await client.SendAsync(new(HttpMethod.Head, path));
        foreach (var response in new[] { get, head })
        {
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
            Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
            foreach (var forbidden in new[] { "Access-Control-Allow-Origin", "ETag", "Location", "Set-Cookie", "X-Local-Monitor-Schema-Version" })
                Assert.False(response.Headers.Contains(forbidden));
        }
        Assert.Equal(expected, await get.Content.ReadAsByteArrayAsync());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    private static LocalRepositorySessionDetailSnapshot Snapshot(string sessionId, LocalWorkspaceSessionDetailContribution detail, string revision, string? label = null)
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null); var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var activity = new LocalWorkspaceActivityFacts(zero, zero, zero, zero, zero);
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none);
        var row = new LocalWorkspaceProjectionRow(sessionId, 0, 0, label is null ? "not_observed" : "recorded", label, "completed", "full", new("not_observed", []), new("not_observed", []), activity, tokens, "not_observed", null, null, null, null, [], "revision");
        return new(new(sessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned, LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null), detail, revision);
    }

    private static SqliteConnection Open(MonitorTempDirectory temp)
    {
        var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void SeedDeterministicSession(MonitorTempDirectory temp, bool full = false,
        string contentJson = "{\"value\":\"Review the retained instruction\",\"prompt\":\"Review the retained instruction\"}",
        string eventType = "user.message", string sourceAdapter = "github-copilot-vscode-otel", string? schemaFingerprint = null)
    {
        var observed = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero);
        var sessionId = Guid.Parse(SessionId);
        var runId = Guid.Parse("018f0000-0000-7000-8000-000000000003");
        var eventId = Guid.Parse("018f0000-0000-7000-8000-000000000004");
        var secondEventId = Guid.Parse("018f0000-0000-7000-8000-000000000005");
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        if (full)
        {
            using var connection = Open(temp);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE retention_store_instances SET store_instance_id='000102030405060708090a0b0c0d0e0f' WHERE id=1;";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        store.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId, ObservedSessionStatus.Completed, full ? SessionCompleteness.Full : SessionCompleteness.Rich,
                    Repository: null, Workspace: null, StartedAt: observed,
                    EndedAt: observed.AddSeconds(1), LastSeenAt: observed.AddSeconds(1),
                    full ? SessionRawRetentionState.Expiring : SessionRawRetentionState.NotCaptured, CreatedAt: observed, UpdatedAt: observed.AddSeconds(1)),
                [new SessionNativeId(sessionId, SessionSourceSurface.VisualStudioCode, "native-session-detail-golden", SessionBindingKind.Native, observed)],
                [new ObservedSessionRun(
                    runId, sessionId, SessionSourceSurface.VisualStudioCode, NativeRunId: "run-detail-golden",
                    TraceId: "00000000000000000000000000000001", ParentRunId: null, Model: "gpt-5.6-sol",
                    ObservedSessionStatus.Completed, StartedAt: observed, EndedAt: observed.AddSeconds(1),
                    InputTokens: 10, OutputTokens: 5, TotalTokens: 15)],
                [new ObservedSessionEvent(
                    eventId, sessionId, runId, SessionSourceSurface.VisualStudioCode, ParentEventId: null,
                    TraceId: "00000000000000000000000000000001", Status: "completed",
                    SourceAdapter: sourceAdapter, SourceEventId: "event-detail-golden",
                    Type: eventType, OccurredAt: observed, full ? SessionContentState.Available : SessionContentState.NotCaptured,
                    SourceApplicationVersion: "1.0", AdapterVersion: "monitor-projection-v1",
                    SchemaFingerprint: schemaFingerprint, NormalizationVersion: "session-normalization-v1"),
                 new ObservedSessionEvent(
                    secondEventId, sessionId, runId, SessionSourceSurface.VisualStudioCode, ParentEventId: null,
                    TraceId: "00000000000000000000000000000001", Status: "completed",
                    SourceAdapter: "github-copilot-vscode-otel", SourceEventId: "event-detail-golden-2",
                    Type: "tool.completed", OccurredAt: observed.AddMilliseconds(500), SessionContentState.NotCaptured,
                    SourceApplicationVersion: "1.0", AdapterVersion: "monitor-projection-v1",
                    SchemaFingerprint: null, NormalizationVersion: "session-normalization-v1")]),
            full
                ? [new SessionEventContent(eventId, "application/json", contentJson, observed, observed.AddDays(30))]
                : []));
    }

    private static void SeedDeterministicRepositoryAssignment(MonitorTempDirectory temp)
    {
        using var connection = Open(temp);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES('018f0000-0000-7000-8000-000000000002','Golden repository',1,$at,$at);
            INSERT INTO session_repository_assignment_revisions(session_id,revision,updated_at) VALUES($session,1,$at);
            INSERT INTO session_repository_manual_overrides(session_id,state,repository_id,revision,updated_at) VALUES($session,'assigned','018f0000-0000-7000-8000-000000000002',1,$at);
            """;
        command.Parameters.AddWithValue("$session", SessionId);
        command.Parameters.AddWithValue("$at", "2026-08-26T01:02:05.0000000+00:00");
        command.ExecuteNonQuery();
    }

    private static void StabilizeDeterministicContentOwner(MonitorTempDirectory temp)
    {
        using var connection = Open(temp);
        var token = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        using var store = connection.CreateCommand();
        store.CommandText = "SELECT store_instance_id FROM retention_store_instances WHERE id=1;";
        var storeId = (string)store.ExecuteScalar()!;
        var captured = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero);
        var expires = captured.AddDays(30);
        var receipt = RetentionOwnershipReceipt.CreateSession(new(storeId,
            "018f0000-0000-7000-8000-000000000004", "application/json", captured.ToString("O"), captured.UtcTicks,
            expires.ToString("O"), expires.UtcTicks, SessionId, "018f0000-0000-7000-8000-000000000003",
            Scalar(connection, "SELECT source_adapter FROM session_events WHERE event_id=$execution;", SessionId, "018f0000-0000-7000-8000-000000000004"), "event-detail-golden", token));
        using var update = connection.CreateCommand();
        update.CommandText = """
            DROP TRIGGER retention_session_event_content_token_immutable;
            UPDATE session_event_content SET retention_owner_token=$token WHERE event_id=$event;
            CREATE TRIGGER retention_session_event_content_token_immutable
            BEFORE UPDATE OF retention_owner_token ON session_event_content
            WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token
            BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;
            UPDATE retention_items
            SET item_id='018f0000-0000-7000-8000-000000000006',ownership_receipt=$receipt
            WHERE store_kind='session_event_content' AND source_item_id=$event;
            """;
        update.Parameters.AddWithValue("$token", token);
        update.Parameters.AddWithValue("$receipt", receipt);
        update.Parameters.AddWithValue("$event", "018f0000-0000-7000-8000-000000000004");
        update.ExecuteNonQuery();
    }

    private static void RefreshDeterministicContentReceipt(SqliteConnection connection, DateTimeOffset expires)
    {
        var token = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var captured = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero);
        var storeId = Scalar(connection,
            "SELECT store_instance_id FROM retention_store_instances WHERE id=1;", SessionId);
        var receipt = RetentionOwnershipReceipt.CreateSession(new(storeId,
            "018f0000-0000-7000-8000-000000000004", "application/json", captured.ToString("O"), captured.UtcTicks,
            expires.ToString("O"), expires.UtcTicks, SessionId, "018f0000-0000-7000-8000-000000000003",
            Scalar(connection, "SELECT source_adapter FROM session_events WHERE event_id=$execution;", SessionId,
                "018f0000-0000-7000-8000-000000000004"), "event-detail-golden", token));
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE retention_items SET ownership_receipt=$receipt WHERE source_item_id=$event;";
        command.Parameters.AddWithValue("$receipt", receipt);
        command.Parameters.AddWithValue("$event", "018f0000-0000-7000-8000-000000000004");
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void RefreshDeterministicFullProjection(MonitorTempDirectory temp, bool stabilizeGolden = true)
    {
        using var connection = Open(temp);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions SET status='completed',completeness='full',ended_at='2026-08-26T01:02:04.0000000+00:00' WHERE session_id=$session;
            UPDATE session_events SET source_adapter='otel-exact',source_event_id='00000000000000000000000000000001/0000000000000001' WHERE event_id='018f0000-0000-7000-8000-000000000005';
            INSERT INTO raw_records(source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token)
            VALUES('raw-otlp','00000000000000000000000000000001','2026-08-26T01:02:03.0000000+00:00',NULL,'{}',1,zeroblob(32));
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens,status,projected_at)
            VALUES(last_insert_rowid(),'00000000000000000000000000000001','0000000000000001',0,'chat','llm_call',10,5,15,2,4,1,'OK','2026-08-26T01:02:04.0000000+00:00');
            INSERT INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens)
            SELECT raw_record_id,span_ordinal,0,15 FROM monitor_spans WHERE trace_id='00000000000000000000000000000001' AND span_id='0000000000000001';
            """;
        command.Parameters.AddWithValue("$session", SessionId);
        Assert.True(command.ExecuteNonQuery() > 0);
        LocalWorkspaceProjectionStore.Refresh(connection, transaction, new DateTimeOffset(2026, 8, 26, 1, 2, 5, TimeSpan.Zero),
            FixedSkillRegistryGenerationAuthority.Load());
        if (stabilizeGolden)
        {
            command.CommandText = "UPDATE local_workspace_node_content_refs SET part='instruction',locator_kind='json_pointer',json_pointer='/prompt',selected_utf8_bytes=31 WHERE source_item_id='018f0000-0000-7000-8000-000000000004' AND availability_state='available';";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        transaction.Commit();
    }

    private static void RefreshContentProjection(MonitorTempDirectory temp)
    {
        using var connection = Open(temp);
        using var transaction = connection.BeginTransaction();
        LocalWorkspaceProjectionStore.Refresh(connection, transaction,
            new DateTimeOffset(2026, 8, 26, 1, 2, 5, TimeSpan.Zero),
            FixedSkillRegistryGenerationAuthority.Load());
        transaction.Commit();
    }

    private static string? TryExecuteDeleteNow(MonitorTempDirectory temp, byte workflowByte)
    {
        string itemId;
        using (var connection = Open(temp))
            itemId = Scalar(connection, "SELECT item_id FROM retention_items WHERE source_item_id=$execution;", SessionId, "018f0000-0000-7000-8000-000000000004");
        var application = new RetentionMutationApplicationService(
            new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider), temp.TimeProvider,
            workspaceParticipant: new LocalWorkspaceProjectionTransactionParticipant(FixedSkillRegistryGenerationAuthority.Load()));
        var key = RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat(workflowByte, 32).ToArray());
        var previewResult = application.CreatePreview(
            new(new(RetentionMutationTargetKind.Item, itemId), RetentionMutationOperation.DeleteNow,
                RetentionMutationScope.SingleItem, RetentionMutationReasonCodes.ResearchNeeded, null),
            key);
        if (previewResult.Preview is null) return previewResult.ErrorCode;
        var confirmationResult = application.IssueConfirmation(
            new(previewResult.Preview.PreviewId, previewResult.Preview.PreviewDigest), key);
        if (confirmationResult.Confirmation is null) return confirmationResult.ErrorCode;
        var result = application.ExecuteMutation(
            new(confirmationResult.Confirmation.ConfirmationToken, RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, itemId),
            key);
        if (result.ErrorCode is not null) return result.ErrorCode;
        Assert.Equal(RetentionMutationCompletionCodes.DeleteQueued, result.Result?.ResultCode);
        return null;
    }

    private static void SeedDeterministicNonrecordedSession(MonitorTempDirectory temp)
    {
        var observed = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero);
        var sessionId = Guid.Parse(SessionId);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        store.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId, ObservedSessionStatus.Active, SessionCompleteness.Unbound,
                    Repository: null, Workspace: null, StartedAt: null, EndedAt: null, LastSeenAt: observed,
                    SessionRawRetentionState.NotCaptured, CreatedAt: observed, UpdatedAt: observed),
                [new SessionNativeId(sessionId, SessionSourceSurface.VisualStudioCode,
                    "native-nonrecorded-evidence", SessionBindingKind.Native, observed)],
                [], []),
            []));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class DirectReadTransaction(SqliteConnection connection, SqliteTransaction transaction) : ILocalRepositoryReadTransaction
    {
        public ValueTask<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken) =>
            read(connection, transaction, cancellationToken);
    }

    private static string InsertClonedNodes(SqliteConnection connection, string sessionId, string executionId, string rootId, int childCount)
    {
        var runId = Scalar(connection,
            "SELECT source_identity FROM local_workspace_execution_headers WHERE session_id=$session AND execution_id=$execution;",
            sessionId, executionId);
        var parentEventId = "late-parent";
        var parentId = LocalWorkspaceProjectionStore.StableNodeId("session_event", parentEventId);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state)
            VALUES($event,$session,$run,'copilot-sdk',$parent,'synthetic',$event,'event',$at,'not_captured');
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$run", runId);
        var eventId = command.Parameters.Add("$event", SqliteType.Text);
        var parent = command.Parameters.Add("$parent", SqliteType.Text);
        var occurredAt = command.Parameters.Add("$at", SqliteType.Text);
        eventId.Value = parentEventId; parent.Value = DBNull.Value; occurredAt.Value = "invalid"; command.ExecuteNonQuery();
        for (var index = 0; index < childCount; index++)
        {
            eventId.Value = $"child-{index:D3}";
            parent.Value = parentEventId;
            occurredAt.Value = index < 200 ? "" : "invalid";
            command.ExecuteNonQuery();
        }
        LocalWorkspaceProjectionStore.RefreshSessions(
            connection, transaction, [sessionId], DateTimeOffset.UnixEpoch,
            FixedSkillRegistryGenerationAuthority.Load());
        transaction.Commit();
        return parentId;
    }

    [Fact]
    public void ProjectionDoesNotPublishNodeContentReferencesForSkillInvokedEvents()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "skill.invoked",
            sourceAdapter: "copilot-sdk-stream", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);

        RefreshContentProjection(temp);

        using var connection = Open(temp);
        Assert.Equal("1", Scalar(connection,
            "SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_nodes WHERE session_id=$session AND event_id='018f0000-0000-7000-8000-000000000004';", SessionId));
        Assert.Equal("0", Scalar(connection,
            "SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.session_id=$session AND c.source_item_id='018f0000-0000-7000-8000-000000000004';", SessionId));
    }

    [Fact]
    public void ProjectionDoesNotPublishClaudeAgentIdAsSubagentInput()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, contentJson: "{\"agent_id\":\"agent-1\"}", eventType: "SubagentStart",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);

        RefreshContentProjection(temp);

        using var connection = Open(temp);
        Assert.Equal("0", Scalar(connection,
            "SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_node_content_refs WHERE source_item_id='018f0000-0000-7000-8000-000000000004' AND part='subagent_input';", SessionId));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task SkillInvokedNodeContentIsNotCapturedWithoutRetentionAdmission(string method)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "skill.invoked",
            sourceAdapter: "copilot-sdk-stream", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND event_id='018f0000-0000-7000-8000-000000000004' ORDER BY source_kind LIMIT 1;", SessionId);
        using var host = await StartProductionDetailRouteAsync(temp, null, null, ensureSchemas: false);
        var revision = await Revision(host.Client);

        using var response = await host.Client.SendAsync(new(new HttpMethod(method),
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=event_content"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(method == "HEAD" ? [] : System.Text.Encoding.UTF8.GetBytes("{\"error\":\"raw_content_not_captured\"}"), await response.Content.ReadAsByteArrayAsync());
        using var proof = Open(temp);
        Assert.Equal("0", Scalar(proof, "SELECT CAST(COUNT(*) AS TEXT) FROM retention_leases WHERE lease_kind='access';", SessionId));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task EqualLengthRawAndOwnerReplacementAfterSnapshotIsRejectedAsStale(string method)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"original\"}", eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        using var host = await StartProductionDetailRouteAsync(temp, phase =>
        {
            if (phase != LocalMonitorNodeContentRoutePhase.BeforeRetentionGrant) return;
            using var connection = Open(temp);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER retention_session_event_content_token_immutable; UPDATE session_event_content SET content_json='{\"prompt\":\"replaced\"}',retention_owner_token=randomblob(32) WHERE event_id='018f0000-0000-7000-8000-000000000004';";
            command.ExecuteNonQuery();
        });
        var revision = await Revision(host.Client);

        using var response = await host.Client.SendAsync(new(new HttpMethod(method),
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(method == "HEAD" ? [] : System.Text.Encoding.UTF8.GetBytes("{\"error\":\"workspace_snapshot_stale\"}"), bytes);
        Assert.DoesNotContain("replaced", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("retention_item_id")]
    [InlineData("source_item_id")]
    [InlineData("retention_revision")]
    [InlineData("revision_input")]
    [InlineData("locator")]
    [InlineData("selected_utf8_bytes")]
    [InlineData("store_kind")]
    [InlineData("retention_store_instance_id")]
    [InlineData("source_captured_at")]
    [InlineData("source_expires_at")]
    [InlineData("retention_ownership_receipt")]
    [InlineData("retention_owner_token")]
    public async Task PersistedLocatorTupleDriftAfterSnapshotReturnsFixedStaleWithoutRaw(string field)
    {
        using var temp = new MonitorTempDirectory();
        const string marker = "locator-drift-raw-marker";
        SeedDeterministicSession(temp, full: true, contentJson: "{\"prompt\":\"" + marker + "\"}",
            eventType: "UserPromptSubmit", sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        var mutated = false;
        using var host = await StartProductionDetailRouteAsync(temp, phase =>
        {
            if (phase != LocalMonitorNodeContentRoutePhase.BeforeRetentionGrant || mutated) return;
            mutated = true;
            using var connection = Open(temp);
            using var command = connection.CreateCommand();
            command.CommandText = field switch
            {
                "retention_item_id" => "UPDATE local_workspace_node_content_refs SET retention_item_id='missing-item' WHERE node_id=$node AND part='instruction';",
                "source_item_id" => "UPDATE local_workspace_node_content_refs SET source_item_id='018f0000-0000-7000-8000-000000000099' WHERE node_id=$node AND part='instruction';",
                "retention_revision" => "UPDATE local_workspace_node_content_refs SET retention_revision=retention_revision+1 WHERE node_id=$node AND part='instruction';",
                "revision_input" => "UPDATE local_workspace_node_content_refs SET revision_input='drifted' WHERE node_id=$node AND part='instruction';",
                "locator" => "UPDATE local_workspace_node_content_refs SET json_pointer='/error' WHERE node_id=$node AND part='instruction';",
                "selected_utf8_bytes" => "UPDATE local_workspace_node_content_refs SET selected_utf8_bytes=selected_utf8_bytes+1 WHERE node_id=$node AND part='instruction';",
                "store_kind" => "UPDATE local_workspace_node_content_refs SET store_kind='wrong_store' WHERE node_id=$node AND part='instruction';",
                "retention_store_instance_id" => "UPDATE local_workspace_node_content_refs SET retention_store_instance_id='wrong-store' WHERE node_id=$node AND part='instruction';",
                "source_captured_at" => "UPDATE local_workspace_node_content_refs SET source_captured_at='2026-08-26T01:02:04.0000000+00:00' WHERE node_id=$node AND part='instruction';",
                "source_expires_at" => "UPDATE local_workspace_node_content_refs SET source_expires_at='2026-09-26T01:02:04.0000000+00:00' WHERE node_id=$node AND part='instruction';",
                "retention_ownership_receipt" => "UPDATE local_workspace_node_content_refs SET retention_ownership_receipt=zeroblob(32) WHERE node_id=$node AND part='instruction';",
                "retention_owner_token" => "UPDATE local_workspace_node_content_refs SET retention_owner_token=zeroblob(32) WHERE node_id=$node AND part='instruction';",
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
            command.Parameters.AddWithValue("$node", nodeId);
            command.ExecuteNonQuery();
        });
        var revision = await Revision(host.Client);

        using var response = await host.Client.GetAsync(
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.True(mutated);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("{\"error\":\"workspace_snapshot_stale\"}"u8.ToArray(), bytes);
        Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task InvalidUtf8AfterSnapshotFailsClosedWithoutRawEcho(string method)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp, full: true, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        using var host = await StartProductionDetailRouteAsync(temp, phase =>
        {
            if (phase != LocalMonitorNodeContentRoutePhase.BeforeRetentionGrant) return;
            using var connection = Open(temp);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE session_event_content SET content_json=CAST(X'7B2270726F6D7074223AFF7D' AS TEXT) WHERE event_id='018f0000-0000-7000-8000-000000000004';";
            command.ExecuteNonQuery();
        });
        var revision = await Revision(host.Client);

        using var response = await host.Client.SendAsync(new(new HttpMethod(method),
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(method == "HEAD" ? [] : System.Text.Encoding.UTF8.GetBytes("{\"error\":\"local_monitor_ui_unavailable\"}"), await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task SmallSelectedPartSucceedsWithoutPublishingHugeSibling(string method)
    {
        using var temp = new MonitorTempDirectory();
        const string selected = "bounded-selection";
        var json = "{\"unused\":\"" + new string('x', 2_097_152) + "\",\"prompt\":\"" + selected + "\"}";
        SeedDeterministicSession(temp, full: true, contentJson: json, eventType: "UserPromptSubmit",
            sourceAdapter: "claude-code-hook", schemaFingerprint: new string('0', 64));
        StabilizeDeterministicContentOwner(temp);
        EnsureProductionProjectionSchemas(temp);
        RefreshContentProjection(temp);
        string nodeId;
        using (var connection = Open(temp))
            nodeId = Scalar(connection, "SELECT node_id FROM local_workspace_node_content_refs WHERE part='instruction';", SessionId);
        using var host = await StartProductionDetailRouteAsync(temp, null, null, ensureSchemas: false);
        var revision = await Revision(host.Client);

        using var response = await host.Client.SendAsync(new(new HttpMethod(method),
            $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{nodeId}/content?workspace_revision={revision}&part=instruction"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if(method == "HEAD") Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        else Assert.Equal(selected, (await ContentEntity(response)).GetProperty("text").GetString());
    }
}

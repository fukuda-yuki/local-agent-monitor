using CopilotAgentObservability.LocalMonitor.SourceCompatibility;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class SessionOtelEnrichmentTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-12T00:00:00Z");

    [Fact]
    public async Task ProcessNextBatch_AgentExecutionPreservesCrossRunToolParentAndExcludesStartup()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        const string trace = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        IngestCopilot(temp, trace, "copilot-chat", "agent-session", "question", "answer", spanId: "0000000000000001", operation: "invoke_agent");
        IngestCopilot(temp, trace, "copilot-chat", "agent-session", "question", "answer", spanId: "0000000000000002", parentSpanId: "0000000000000001");
        IngestCopilot(temp, trace, "copilot-chat", "agent-session", "question", "answer", spanId: "0000000000000003", parentSpanId: "0000000000000002", operation: "execute_tool", statusCode: 2);
        IngestCopilot(temp, trace, "copilot-chat", "agent-session", "question", "answer", spanId: "0000000000000004", operation: "startup");
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock);
        Assert.Equal(4, enricher.ProcessNextBatch());
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, ObservedAt, FixedSkillRegistryGenerationAuthority.Load());
        }
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
            LocalArchiveSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }
        var service = new SqliteLocalRepositoryScopeSnapshotService(temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority), SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock), skillRegistryAuthority: authority);
        var sessionId = Assert.Single(store.ListMostRecent(10)).SessionId.ToString();
        var summary = await service.ReadDetailAsync(new(LocalRepositorySessionDetailRequestKind.Summary, sessionId), CancellationToken.None);
        var execution = Assert.Single(summary.Detail.Executions);
        Assert.Equal(1, execution.Activity.Error.Value);
        var agent = Assert.Single(summary.Detail.Nodes, value => value.Kind == "agent");
        Assert.Equal("completed", agent.Status);
        Assert.Equal(1000, agent.DurationMilliseconds);
        var tool = Assert.Single(summary.Detail.Nodes, value => value.Kind == "tool");
        Assert.Equal(execution.ExecutionId, tool.ExecutionId);
        Assert.Equal("exact", tool.RelationshipAuthority);
        Assert.Equal("0000000000000002", Assert.Single(summary.Detail.Nodes, value => value.NodeId == tool.ParentNodeId).SpanId);
        var inspector = await service.ReadDetailAsync(new(LocalRepositorySessionDetailRequestKind.Node, sessionId, NodeId: tool.NodeId), CancellationToken.None);
        Assert.Equal(tool.ParentNodeId, Assert.Single(inspector.Detail.Nodes, value => value.NodeId == tool.NodeId).ParentNodeId);
    }

    [Fact]
    public void ProcessNextBatch_RetainedToolPartsReachTheirExactSemanticNode()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, new FixedTimeProvider(ObservedAt));
        store.CreateSchema();
        IngestCopilot(temp, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "copilot-chat", "tool-session", "question", "answer",
            spanId: "0000000000000001", operation: "execute_tool");
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, new FixedTimeProvider(ObservedAt));
        Assert.Equal(1, enricher.ProcessNextBatch());
        Assert.Equal(0, enricher.ProcessNextBatch());
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, ObservedAt, FixedSkillRegistryGenerationAuthority.Load());
        var sessionId = Assert.Single(store.ListMostRecent(10)).SessionId.ToString();
        using var transaction = connection.BeginTransaction();
        Assert.True(LocalWorkspaceContentAuthority.ValidateSessionGraph(connection, transaction, sessionId, ObservedAt));
        transaction.Commit();
        Assert.Equal(["tool_input|available", "tool_result|available"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT c.part||'|'||c.availability_state FROM local_workspace_node_content_refs c JOIN local_workspace_nodes n ON n.node_id=c.node_id WHERE n.kind='tool' ORDER BY c.part;"));
        Assert.Equal(2, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_events WHERE type IN ('otel.tool.input','otel.tool.result');"));
    }

    [Fact]
    public void ProcessNextBatch_RecordedOtelFailuresReachSessionAndExecutionActivity()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        for (var index = 1; index <= 6; index++)
            IngestCopilot(temp, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "copilot-chat", "failure-session", "question", "answer",
                spanId: index.ToString("x16"), statusCode: index <= 5 ? 2 : 1);
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock);
        Assert.Equal(6, enricher.ProcessNextBatch());
        Assert.Equal(0, enricher.ProcessNextBatch());
        Assert.Single(store.ListMostRecent(10));

        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, ObservedAt, authority);
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, ObservedAt, authority);
            transaction.Commit();
        }
        Assert.Equal(["recorded|5"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT state||'|'||COALESCE(CAST(count AS TEXT),'missing') FROM local_workspace_session_activity WHERE kind='error';"));
        Assert.Equal(5, Count(temp.DatabasePath,
            "SELECT SUM(error_activity_count) FROM local_workspace_execution_headers;"));
        Assert.Equal(1, Count(temp.DatabasePath,
            "SELECT COUNT(*) FROM local_workspace_execution_headers WHERE error_activity_state='not_observed' AND error_activity_count IS NULL;"));
    }

    [Fact]
    public void ProcessNextBatch_CopilotFirstObservationPreservesContentAndSourceScopedContinuity()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        IngestCopilot(temp, "trace-first", "github-copilot", "same-native", "first instruction", "first answer");
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock);
        Assert.Equal(1, enricher.ProcessNextBatch());
        var first = Assert.Single(store.ListMostRecent(10));
        Assert.Equal(SessionCompleteness.Partial, first.Completeness);
        Assert.Equal(first.SessionId, store.Resolve(SessionSourceSurface.CopilotCli, "same-native")!.SessionId);
        IngestCopilot(temp, "trace-later", "github-copilot", "same-native", "second instruction", "second answer");
        IngestCopilot(temp, "trace-vscode", "copilot-chat", "same-native", "other instruction", "other answer");
        Assert.Equal(2, enricher.ProcessNextBatch());
        Assert.Equal(2, store.ListMostRecent(10).Count);
        Assert.Equal(2, store.GetDetail(first.SessionId)!.Runs.Count);
        Assert.NotEqual(first.SessionId, store.Resolve(SessionSourceSurface.VisualStudioCode, "same-native")!.SessionId);
        Assert.Equal(6, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Equal(1, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content WHERE json_extract(content_json,'$.value')='first instruction';"));
        Assert.Equal(1, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content WHERE json_extract(content_json,'$')='second answer';"));
        Assert.Equal(0, enricher.ProcessNextBatch());
        Assert.Equal(6, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Fact]
    public void ProcessNextBatch_CopilotContentRepairKeepsExistingOwnersAndIsIdempotent()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        IngestCopilot(temp, "trace-repair", "github-copilot", "repair-native", "instruction Bearer synthetic-secret", "answer");
        var interrupted = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock,
            checkpoint => { if (checkpoint == "before-copilot-content-write") throw new InvalidOperationException("synthetic stop"); });
        Assert.Throws<InvalidOperationException>(() => interrupted.ProcessNextBatch());
        var before = Assert.Single(store.ListMostRecent(10));
        Assert.Single(store.GetDetail(before.SessionId)!.Runs);
        Assert.Equal(0, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
        var resumed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock);
        Assert.Equal(0, resumed.ProcessNextBatch());
        Assert.Equal(before.SessionId, Assert.Single(store.ListMostRecent(10)).SessionId);
        Assert.Single(store.GetDetail(before.SessionId)!.Runs);
        Assert.Equal(2, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Equal(1, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content WHERE json_extract(content_json,'$.value')='instruction [REDACTED]';"));
        store.UpsertProjectionState(new(SqliteSessionOtelEnricher.ContentProjectorKey, 0, 0, ObservedAt));
        Assert.Equal(0, resumed.ProcessNextBatch());
        Assert.Equal(2, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Fact]
    public void ProcessNextBatch_CopilotContentLostGrantWritesNothingAndKeepsRepairCursor()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        IngestCopilot(temp, "trace-lost-grant", "github-copilot", "native-lost-grant", "instruction", "answer");
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock,
            checkpoint => { if (checkpoint == "before-copilot-content-write") Execute(temp.DatabasePath, "DELETE FROM retention_leases WHERE lease_kind='operation';"); });
        Assert.Equal(1, enricher.ProcessNextBatch());
        Assert.Equal(0, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Null(store.GetProjectionState(SqliteSessionOtelEnricher.ContentProjectorKey));
    }

    [Fact]
    public void ProcessNextBatch_CopilotConfirmationCannotBridgeSourcesThroughUnknownHook()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        var sessionId = Guid.CreateVersion7();
        store.Write(new(new(new(sessionId, ObservedSessionStatus.Unknown, SessionCompleteness.Partial,
            null, null, null, null, ObservedAt, SessionRawRetentionState.NotCaptured, ObservedAt, ObservedAt),
            [new(sessionId, SessionSourceSurface.HookUnknown, "shared-hook-native", SessionBindingKind.Native, ObservedAt)], [],
            [Event(sessionId, "unknown-hook", "UserPromptSubmit", ObservedAt)]), []));
        IngestCopilot(temp, "trace-cli-confirm", "github-copilot", "shared-hook-native", "CLI input", "CLI answer");
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock);
        Assert.Equal(1, enricher.ProcessNextBatch());
        Assert.Equal(sessionId, store.Resolve(SessionSourceSurface.CopilotCli, "shared-hook-native")!.SessionId);
        IngestCopilot(temp, "trace-vscode-separate", "copilot-chat", "shared-hook-native", "VSCode input", "VSCode answer");
        Assert.Equal(1, enricher.ProcessNextBatch());
        Assert.NotEqual(sessionId, store.Resolve(SessionSourceSurface.VisualStudioCode, "shared-hook-native")!.SessionId);
        Assert.Equal(2, store.ListMostRecent(10).Count);
    }

    [Fact]
    public void ProcessNextBatch_CopilotDifferentNativeIdsDoNotJoinBySharedTrace()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock);
        IngestCopilot(temp, "trace-shared", "github-copilot", "native-first", "first input", "first answer");
        Assert.Equal(1, enricher.ProcessNextBatch());
        var firstOwner = store.Resolve(SessionSourceSurface.CopilotCli, "native-first")!.SessionId;
        IngestCopilot(temp, "trace-shared", "github-copilot", "native-second", "second input", "second answer", spanId: "span-2");
        Assert.Equal(1, enricher.ProcessNextBatch());
        Assert.NotEqual(firstOwner, store.Resolve(SessionSourceSurface.CopilotCli, "native-second")!.SessionId);
        Assert.Equal(2, store.ListMostRecent(10).Count);
        Assert.Single(store.GetDetail(firstOwner)!.Runs);
    }

    [Fact]
    public void ProcessNextBatch_CopilotExactLegacyMixedOwnerIsNotRebound()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        var sessionId = Guid.CreateVersion7();
        store.Write(new(new(new(sessionId, ObservedSessionStatus.Unknown, SessionCompleteness.Partial,
            null, null, null, null, ObservedAt, SessionRawRetentionState.NotCaptured, ObservedAt, ObservedAt),
            [new(sessionId, SessionSourceSurface.CopilotCli, "legacy-mixed-native", SessionBindingKind.Native, ObservedAt),
             new(sessionId, SessionSourceSurface.VisualStudioCode, "legacy-mixed-native", SessionBindingKind.Native, ObservedAt)], [], []), []));
        var unknownOwner = Guid.CreateVersion7();
        store.Write(new(new(new(unknownOwner, ObservedSessionStatus.Unknown, SessionCompleteness.Partial,
            null, null, null, null, ObservedAt, SessionRawRetentionState.NotCaptured, ObservedAt, ObservedAt),
            [new(unknownOwner, SessionSourceSurface.HookUnknown, "legacy-mixed-native", SessionBindingKind.Native, ObservedAt)], [], []), []));
        IngestCopilot(temp, "trace-legacy-mixed", "github-copilot", "legacy-mixed-native", "input", "answer");
        Assert.Equal(1, new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock).ProcessNextBatch());
        Assert.Equal(2, store.ListMostRecent(10).Count);
        Assert.Empty(store.GetDetail(unknownOwner)!.Runs);
        Assert.Equal(2, store.GetDetail(sessionId)!.NativeIds.Count);
        Assert.Single(store.GetDetail(sessionId)!.Runs);
    }

    [Theory]
    [InlineData("[{\"parts\":[]}]")]
    [InlineData("[{\"role\":\"user\",\"parts\":[{\"type\":\"image\"}]}]")]
    public void ProcessNextBatch_CopilotPresentUnsupportedInputIsNotNotCaptured(string input)
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        IngestCopilot(temp, "trace-unsupported", "github-copilot", "unsupported-native", "unused", "answer", inputMessages: input);
        Assert.Equal(1, new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock).ProcessNextBatch());
        var detail = store.GetDetail(Assert.Single(store.ListMostRecent(10)).SessionId)!;
        Assert.Single(detail.Events, item => item.SourceAdapter == "copilot-otel" && item.ContentState == SessionContentState.Unsupported);
        Assert.Equal(1, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Fact]
    public void ProcessNextBatch_CopilotConflictingTraceSourcesDoNotBootstrapOrCopyContent()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var clock = new FixedTimeProvider(ObservedAt);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        IngestCopilot(temp, "trace-conflicting", "github-copilot", "conflicting-native", "CLI input", "CLI answer");
        IngestCopilot(temp, "trace-conflicting", "copilot-chat", "conflicting-native", "VSCode input", "VSCode answer", spanId: "span-2");
        Assert.Equal(2, new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock).ProcessNextBatch());
        Assert.Equal(0, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_native_ids;"));
        Assert.Equal(0, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.All(store.ListMostRecent(10), item => Assert.Equal(SessionCompleteness.Unbound, item.Completeness));
    }

    [Fact]
    public async Task ProcessNextBatch_UnsupportedCopilotOutputKeepsProductionSummaryAvailable()
    {
        using var temp = new MonitorTempDirectory();
        var clock = new FixedTimeProvider(ObservedAt);
        temp.TimeProvider = clock;
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, clock);
        store.CreateSchema();
        IngestCopilot(temp, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "github-copilot", "unsupported-output", "recorded question", "unused",
            spanId: "bbbbbbbbbbbbbbbb", outputMessages: """[{"role":"assistant","parts":[{"type":"tool_call","name":"read_file"}]}]""");
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock);
        Assert.Equal(1, enricher.ProcessNextBatch());
        var session = Assert.Single(store.ListMostRecent(10));
        var detail = store.GetDetail(session.SessionId)!;
        var unsupported = Assert.Single(detail.Events, item => item.ContentState == SessionContentState.Unsupported);
        Assert.Equal("assistant.message", unsupported.Type);
        using var host = await LocalMonitorV1SessionDetailRouteTests.StartProductionDetailRouteAsync(temp);
        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{session.SessionId:D}/summary");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var summary = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(summary.RootElement.GetProperty("session").GetProperty("capture").GetProperty("notes").EnumerateArray(),
            note => note.GetString() == "source_unsupported");
        Assert.DoesNotContain(summary.RootElement.GetProperty("session").GetProperty("capture").GetProperty("notes").EnumerateArray(),
            note => note.GetString() == "raw_content_not_captured");
        Assert.Equal(1, Count(temp.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
    }

    private static void IngestCopilot(MonitorTempDirectory temp, string traceId, string service, string nativeId, string prompt, string answer, string? inputMessages = null, string spanId = "span-1", string? outputMessages = null, int statusCode = 1, string operation = "chat", string? parentSpanId = null)
    {
        static object Attribute(string key, string value) => new { key, value = new { stringValue = value } };
        var input = inputMessages ?? System.Text.Json.JsonSerializer.Serialize(new[] { new { role = "user", parts = new[] { new { type = "text", content = prompt } } } });
        var output = outputMessages ?? System.Text.Json.JsonSerializer.Serialize(new[] { new { role = "assistant", parts = new[] { new { type = "text", content = answer } } } });
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            resourceSpans = new[] { new
            {
                resource = new { attributes = new[] { Attribute("service.name", service) } },
                scopeSpans = new[] { new { spans = new[] { new
                {
                    traceId, spanId, parentSpanId, name = "chat", startTimeUnixNano = "1783814400000000000", endTimeUnixNano = "1783814401000000000",
                    status = new { code = statusCode },
                    attributes = new[] { Attribute("gen_ai.operation.name", operation), Attribute("gen_ai.conversation.id", nativeId) }
                        .Concat(operation is "chat" or "invoke_agent" ? [Attribute("gen_ai.input.messages", input), Attribute("gen_ai.output.messages", output)] : [])
                        .Concat(operation == "execute_tool" ? [Attribute("gen_ai.tool.name", "read_file"), Attribute("gen_ai.tool.call.arguments", "{\"path\":\"example.txt\"}"), Attribute("gen_ai.tool.call.result", "example content")] : []).ToArray(),
                } } } },
            } },
        });
        var raw = RawOtlpIngestor.CreateRecordFromPayloadJson(payload, ObservedAt);
        var rawStore = temp.CreateRawStore();
        var rawId = rawStore.Insert(raw);
        raw = raw with { Id = rawId };
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM raw_records ORDER BY id;";
        var payloads = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) payloads.Add(reader.GetString(0));
        var sources = OtlpTraceSourceResolver.Resolve(payloads);
        rawStore.ApplyProjection(rawId, raw.Source, raw.ReceivedAt,
            MonitorProjectionBuilder.Build(raw, id => sources.Single(item => item.TraceId == id).SourceFamily), ObservedAt);
        rawStore.ApplySpanProjection(rawId, MonitorSpanProjectionBuilder.Build(raw), ObservedAt);
    }

    [Fact]
    public void ProcessNextBatch_UsesOnlyExactLinksAndCreatesUnboundForUnmatchedRows()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var sessionId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var events = new[]
        {
            Event(sessionId, "start", "SessionStart", now),
            Event(sessionId, "prompt", "UserPromptSubmit", now.AddSeconds(1)),
            Event(sessionId, "end", "SessionEnd", now.AddSeconds(2)),
            Event(sessionId, "trace-link", "tool.execution_complete", now.AddSeconds(2)) with { TraceId = "trace-by-context" },
        };
        var seed = new SessionWriteBatch(
            new(
                new(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Rich, "same-repo", null, now, now.AddSeconds(2), now.AddSeconds(2), SessionRawRetentionState.NotCaptured, now, now),
                [new(sessionId, SessionSourceSurface.HookUnknown, "native-exact", SessionBindingKind.Native, now)],
                [],
                events),
            []);
        ((IClassifiedSessionStore)store).WriteClassified(
            seed,
            [new SessionTerminalFact(events[2].EventId, SessionTerminalOutcome.Clean)]);
        InsertProjectedSpan(temp.DatabasePath, temp.RetentionContext, "trace-exact", "span-1", "native-exact", "vscode-copilot-chat", "same-repo", now.AddSeconds(3));
        InsertProjectedSpan(temp.DatabasePath, temp.RetentionContext, "trace-unmatched", "span-2", null, "vscode-copilot-chat", "same-repo", now.AddSeconds(4));
        InsertProjectedSpan(temp.DatabasePath, temp.RetentionContext, "trace-by-context", "span-3", null, "unrecognized-client", "same-repo", now.AddSeconds(5));

        var processor = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, TimeProvider.System);
        var processed = processor.ProcessNextBatch(100);

        Assert.Equal(3, processed);
        var confirmed = store.Resolve(SessionSourceSurface.VisualStudioCode, "native-exact");
        Assert.NotNull(confirmed);
        Assert.Equal(sessionId, confirmed.SessionId);
        Assert.Equal(SessionCompleteness.Full, confirmed.Completeness);
        var detail = store.GetDetail(sessionId)!;
        Assert.Contains(detail.Events, item => item.SourceAdapter == "otel-exact" && item.SourceEventId == "trace-exact/span-1");
        Assert.Contains(detail.Events, item => item.SourceAdapter == "otel-exact" && item.SourceEventId == "trace-by-context/span-3");

        var sessions = store.ListMostRecent(10);
        var unbound = Assert.Single(sessions, item => item.SessionId != sessionId);
        Assert.Equal(SessionCompleteness.Unbound, unbound.Completeness);
        Assert.Equal("same-repo", unbound.Repository);
        Assert.Null(store.Resolve(SessionSourceSurface.VisualStudioCode, "trace-unmatched"));
        Assert.Equal(3, store.GetProjectionState("session-otel-enrichment")!.ProjectionCursor);
    }

    // Issue #108: the exact native-session-ID resolver binds on its own
    // session.id evidence in the generic (non-promoted) path, without
    // requiring claude-code-otel adapter promotion (D058).
    [Fact]
    public void ProcessNextBatch_GenericPathBindsOnOwnSessionIdEvidenceWithoutAdapterPromotion()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var hookSessionId = SeedClaudeSession(store, "GENERIC_NATIVE_001", SessionBindingKind.Native);
        var payload = BuildOtelPayload("generic-trace-1", "generic-span-1", "GENERIC_NATIVE_001");
        InsertProjectedSpanWithPayload(temp.DatabasePath, temp.RetentionContext, "generic-trace-1", "generic-span-1", "unrecognized-client", "generic-repo", now.AddSeconds(1), payload);

        var processed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, TimeProvider.System).ProcessNextBatch(100);

        Assert.Equal(1, processed);
        var detail = store.GetDetail(hookSessionId)!;
        Assert.Contains(detail.Events, item => item.SourceAdapter == "otel-exact" && item.SourceEventId == "generic-trace-1/generic-span-1");
        Assert.DoesNotContain(detail.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Single(detail.NativeIds);
        Assert.Single(store.ListMostRecent(10), item => item.SessionId == hookSessionId);

        var projection = SourceProjectionStateBuilder.Build([], detail);
        Assert.Equal("exact_linked", projection.BindingState);
    }

    [Fact]
    public void ProcessNextBatch_GenericCopilotCliPathPreservesNormalizedPerSpanRunFacts()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var startedAt = DateTimeOffset.Parse("2026-07-16T00:00:01Z");
        var endedAt = startedAt.AddSeconds(7);
        InsertProjectedSpan(
            temp.DatabasePath,
            temp.RetentionContext,
            "generic-facts-trace",
            "generic-facts-span",
            null,
            "copilot-cli",
            "generic-repo",
            startedAt);
        CreateSpanFactsTable(temp.DatabasePath);
        Execute(
            temp.DatabasePath,
            $"""
            UPDATE monitor_spans
            SET category='llm_call',
                operation='chat',
                request_model='requested-model',
                response_model='response-model',
                status='ok',
                end_time='{endedAt:O}',
                input_tokens=10840,
                output_tokens=77,
                total_tokens=10917
            WHERE trace_id='generic-facts-trace'
              AND span_id='generic-facts-span';
            INSERT INTO local_workspace_span_facts(
                raw_record_id,span_ordinal,retry_count,producer_total_tokens)
            SELECT raw_record_id,span_ordinal,NULL,10917
            FROM monitor_spans
            WHERE trace_id='generic-facts-trace'
              AND span_id='generic-facts-span';
            """);

        Assert.Equal(
            1,
            new SqliteSessionOtelEnricher(
                temp.DatabasePath,
                store,
                temp.RetentionContext,
                new FixedTimeProvider(startedAt),
                checkpoint =>
                {
                    if (checkpoint == "before_raw_terminal")
                        Execute(temp.DatabasePath, "DROP TABLE local_workspace_span_facts;");
                }).ProcessNextBatch(1));

        var session = Assert.Single(store.ListMostRecent(10));
        var run = Assert.Single(store.GetDetail(session.SessionId)!.Runs);
        Assert.Equal(SessionSourceSurface.CopilotCli, run.SourceSurface);
        Assert.Equal("response-model", run.Model);
        Assert.Equal(ObservedSessionStatus.Completed, run.Status);
        Assert.Equal(startedAt, run.StartedAt);
        Assert.Equal(endedAt, run.EndedAt);
        Assert.Equal(10840, run.InputTokens);
        Assert.Equal(77, run.OutputTokens);
        Assert.Equal(10917, run.TotalTokens);
    }

    [Fact]
    public void ProcessNextBatch_GenericPathUsesRequestModelWhenResponseModelWasNotObserved()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var startedAt = DateTimeOffset.Parse("2026-07-16T00:00:01Z");
        InsertProjectedSpan(
            temp.DatabasePath,
            temp.RetentionContext,
            "generic-request-model-trace",
            "generic-request-model-span",
            null,
            "copilot-cli",
            "generic-repo",
            startedAt);
        Execute(
            temp.DatabasePath,
            """
            UPDATE monitor_spans
            SET request_model='request-only-model',
                response_model=NULL
            WHERE trace_id='generic-request-model-trace'
              AND span_id='generic-request-model-span';
            """);

        Assert.Equal(
            1,
            new SqliteSessionOtelEnricher(
                temp.DatabasePath,
                store,
                temp.RetentionContext).ProcessNextBatch(1));

        var session = Assert.Single(store.ListMostRecent(10));
        var run = Assert.Single(store.GetDetail(session.SessionId)!.Runs);
        Assert.Equal("request-only-model", run.Model);
    }

    [Fact]
    public void ProcessNextBatch_RealGenericNormalizationProjectsExactWorkspaceRunFactsWithoutTokenDuplication()
    {
        const string traceId = "11111111111111111111111111111111";
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var payload = $$$"""
            {"resourceSpans":[{"resource":{"attributes":[
              {"key":"client.kind","value":{"stringValue":"github-copilot"}}
            ]},"scopeSpans":[{"spans":[
              {"traceId":"{{{traceId}}}","spanId":"1111111111111111","name":"invoke_agent",
               "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000003000000000",
               "status":{"code":"1"},"attributes":[
                 {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
                 {"key":"gen_ai.request.model","value":{"stringValue":"parent-model"}},
                 {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
                 {"key":"gen_ai.usage.output_tokens","value":{"intValue":"20"}}
               ]},
              {"traceId":"{{{traceId}}}","spanId":"2222222222222222","parentSpanId":"1111111111111111","name":"chat",
               "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
               "status":{"code":"1"},"attributes":[
                 {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                 {"key":"gen_ai.request.model","value":{"stringValue":"requested-model"}},
                 {"key":"gen_ai.response.model","value":{"stringValue":"response-model"}},
                 {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
                 {"key":"gen_ai.usage.output_tokens","value":{"intValue":"20"}}
               ]}
            ]}]}]}
            """;
        var rawStore = new RawTelemetryStore(
            temp.DatabasePath,
            temp.RetentionContext,
            connectionOptions: RawTelemetryStoreConnectionOptions.MonitorWriter);
        var rawRecordId = rawStore.Insert(new RawTelemetryRecord(
            null, "raw-otlp", traceId, ObservedAt, null, payload));
        var persisted = new RawTelemetryRecord(
            rawRecordId, "raw-otlp", traceId, ObservedAt, null, payload);
        Assert.True(rawStore.ApplyProjection(
            rawRecordId,
            persisted.Source,
            persisted.ReceivedAt,
            MonitorProjectionBuilder.Build(persisted),
            ObservedAt));
        Assert.True(rawStore.ApplySpanProjection(
            rawRecordId,
            MonitorSpanProjectionBuilder.Build(persisted),
            ObservedAt));

        Assert.Equal(
            2,
            new SqliteSessionOtelEnricher(
                temp.DatabasePath,
                store,
                temp.RetentionContext,
                new FixedTimeProvider(ObservedAt.AddMinutes(1))).ProcessNextBatch(2));

        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, ObservedAt, authority);
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, ObservedAt.AddMinutes(1), authority);
            transaction.Commit();
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model,time_authority,duration_ms,token_authority,input_tokens,output_tokens,total_token_state,total_tokens
            FROM local_workspace_execution_headers
            ORDER BY source_ordinal;
            SELECT SUM(input_tokens),SUM(output_tokens),SUM(total_tokens)
            FROM local_workspace_execution_headers;
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("parent-model", reader.GetString(0));
        Assert.Equal("recorded", reader.GetString(1));
        Assert.Equal(3000, reader.GetInt64(2));
        Assert.Equal("none", reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        Assert.True(reader.IsDBNull(5));
        Assert.Equal("not_observed", reader.GetString(6));
        Assert.True(reader.IsDBNull(7));
        Assert.True(reader.Read());
        Assert.Equal("response-model", reader.GetString(0));
        Assert.Equal("recorded", reader.GetString(1));
        Assert.Equal(1000, reader.GetInt64(2));
        Assert.Equal("session_run", reader.GetString(3));
        Assert.Equal(100, reader.GetInt64(4));
        Assert.Equal(20, reader.GetInt64(5));
        Assert.Equal("not_observed", reader.GetString(6));
        Assert.True(reader.IsDBNull(7));
        Assert.False(reader.Read());
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal(100, reader.GetInt64(0));
        Assert.Equal(20, reader.GetInt64(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public void ProcessNextBatch_RechecksSourceInsideWriteAfterConflictConsumesPendingRetry()
    {
        const string traceId = "11111111111111111111111111111111";
        const string spanId = "1111111111111111";
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var sourceStore = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        sourceStore.CreateSchema();
        var seedStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext);
        seedStore.CreateSchema();
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        InsertProjectedSpanWithPayload(
            temp.DatabasePath,
            temp.RetentionContext,
            traceId,
            spanId,
            "copilot-cli",
            "generic-repo",
            now,
            BuildOtelPayload(traceId, spanId));
        long rawRecordId;
        using (var connection = new SqliteConnection(
                   $"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT raw_record_id FROM monitor_spans WHERE trace_id='{traceId}';";
            rawRecordId = (long)command.ExecuteScalar()!;
        }
        Execute(
            temp.DatabasePath,
            $"""
            UPDATE source_trace_attribution_observations
            SET cli_candidate_observed=1,
                relevant_evidence_observed=1
            WHERE raw_record_id={rawRecordId}
              AND trace_id='{traceId}';
            """);
        sourceStore.ReconcileProjectedTraceSourceAttribution();
        var checkpointCalls = 0;
        var raceStore = new SqliteSessionStore(
            temp.DatabasePath,
            temp.RetentionContext,
            new FixedTimeProvider(now),
            phase =>
            {
                if (phase != "before-session-write"
                    || Interlocked.Increment(ref checkpointCalls) != 1)
                {
                    return;
                }
                Execute(
                    temp.DatabasePath,
                    $"""
                    INSERT INTO source_trace_attribution_observations
                    VALUES({rawRecordId + 1000},'{traceId}',0,1,0,1);
                    INSERT INTO source_trace_attribution_reconciliation_queue
                    VALUES('{traceId}');
                    """);
                Assert.True(sourceStore.ReconcileProjectedTraceSourceAttribution());
            });

        var processed = new SqliteSessionOtelEnricher(
            temp.DatabasePath,
            raceStore,
            temp.RetentionContext,
            new FixedTimeProvider(now)).ProcessNextBatch(100);

        Assert.Equal(1, processed);
        Assert.Equal(1, checkpointCalls);
        var session = Assert.Single(raceStore.ListMostRecent(10));
        var detail = Assert.IsType<SessionDetail>(
            raceStore.GetDetail(session.SessionId));
        var @event = Assert.Single(
            detail.Events,
            item => item.SourceAdapter == "otel-exact");
        var run = Assert.Single(detail.Runs);
        Assert.Null(@event.SourceSurface);
        Assert.Null(run.SourceSurface);
        Assert.Equal(SessionMatchKind.None, @event.MatchKind);
        Assert.Equal($"{traceId}/{spanId}", @event.SourceEventId);
        Assert.Equal(traceId, @event.TraceId);
        Assert.Equal(@event.RunId, run.RunId);
        Assert.Empty(detail.NativeIds);
        Assert.Equal(
            0L,
            Count(
                temp.DatabasePath,
                "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
        Assert.Equal(
            1L,
            raceStore.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!
                .ProjectionCursor);
    }

    [Fact]
    public void ProcessNextBatch_GenericPathPreservesReachabilityCountsAndAdapterLabels()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var exactSessionId = SeedClaudeSession(store, "GENERIC_EXACT_001", SessionBindingKind.Native);
        var traceSessionId = Guid.CreateVersion7();
        store.Write(new(new(
            new ObservedSession(
                traceSessionId, ObservedSessionStatus.Unknown, SessionCompleteness.Unbound,
                null, null, null, null, now, SessionRawRetentionState.NotCaptured, now, now),
            [],
            [],
            [Event(traceSessionId, "trace-context", "UserPromptSubmit", now) with { TraceId = "generic-shared-trace" }]),
            []));
        var conversationSessionId = SeedClaudeSession(store, "GENERIC_CONVERSATION_001", SessionBindingKind.Native);

        InsertProjectedSpanWithPayload(
            temp.DatabasePath, temp.RetentionContext,
            "generic-exact-trace",
            "generic-span-1",
            "vscode-copilot-chat",
            "generic-repo",
            now.AddSeconds(1),
            BuildOtelPayload("generic-exact-trace", "generic-span-1", "GENERIC_EXACT_001"));
        InsertProjectedSpan(temp.DatabasePath, temp.RetentionContext, "generic-shared-trace", "generic-span-2", null, "vscode-copilot-chat", "generic-repo", now.AddSeconds(2));
        InsertProjectedSpan(temp.DatabasePath, temp.RetentionContext, "generic-conversation-trace", "generic-span-3", "GENERIC_CONVERSATION_001", "vscode-copilot-chat", "generic-repo", now.AddSeconds(3));

        var processed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, TimeProvider.System).ProcessNextBatch(100);

        Assert.Equal(3, processed);
        Assert.Equal(SessionMatchKind.ExactNative, Assert.Single(store.GetDetail(exactSessionId)!.Events, item => item.SourceAdapter == "otel-exact").MatchKind);
        Assert.Equal(SessionMatchKind.TraceContinuity, Assert.Single(store.GetDetail(traceSessionId)!.Events, item => item.SourceAdapter == "otel-exact").MatchKind);
        Assert.Equal(SessionMatchKind.ConversationId, Assert.Single(store.GetDetail(conversationSessionId)!.Events, item => item.SourceAdapter == "otel-exact").MatchKind);
        Assert.Single(store.GetDetail(exactSessionId)!.Events, item => item.SourceAdapter == "otel-exact" && item.SourceEventId == "generic-exact-trace/generic-span-1");
        Assert.Single(store.GetDetail(traceSessionId)!.Events, item => item.SourceAdapter == "otel-exact" && item.SourceEventId == "generic-shared-trace/generic-span-2");
        Assert.Single(store.GetDetail(conversationSessionId)!.Events, item => item.SourceAdapter == "otel-exact" && item.SourceEventId == "generic-conversation-trace/generic-span-3");
        Assert.Equal(3, store.ListMostRecent(10).SelectMany(session => store.GetDetail(session.SessionId)!.Events).Count(item => item.SourceAdapter == "otel-exact"));
    }

    [Theory]
    [InlineData(SessionBindingKind.ExplicitResume)]
    [InlineData(SessionBindingKind.ExplicitHandoff)]
    public void ProcessNextBatch_ClaudeExplicitResumeAndHandoffRemainExactBindings(SessionBindingKind bindingKind)
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var sessionId = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", bindingKind);

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext).ProcessNextBatch(1);

        var detail = store.GetDetail(sessionId)!;
        Assert.Single(detail.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Equal(bindingKind is SessionBindingKind.Native ? SessionMatchKind.ExactNative : SessionMatchKind.ExplicitLink, Assert.Single(detail.Events, item => item.SourceAdapter == "claude-code-otel").MatchKind);
        Assert.Equal(bindingKind, Assert.Single(detail.NativeIds).BindingKind);
        Assert.Equal(SessionCompleteness.Full, detail.Session.Completeness);
    }

    [Fact]
    public void ProcessNextBatch_GenericPathStillUsesSharedTraceIdContinuity()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var sessionId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        store.Write(new(new(
            new ObservedSession(
                sessionId, ObservedSessionStatus.Unknown, SessionCompleteness.Unbound,
                null, null, null, null, now, SessionRawRetentionState.NotCaptured, now, now),
            [],
            [],
            [Event(sessionId, "trace-context", "UserPromptSubmit", now) with { TraceId = "shared-trace-id" }]),
            []));
        InsertProjectedSpan(temp.DatabasePath, temp.RetentionContext, "shared-trace-id", "shared-span-1", null, "unrecognized-client", "generic-repo", now.AddSeconds(1));

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext).ProcessNextBatch(1);

        Assert.Single(store.GetDetail(sessionId)!.Events, item => item.SourceEventId == "shared-trace-id/shared-span-1");
    }

    [Fact]
    public void ProcessNextBatch_GenericPathWithoutHookSessionCreatesFreshUnboundSession()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var payload = BuildOtelPayload("generic-trace-2", "generic-span-2", "NO_MATCHING_HOOK_SESSION");
        InsertProjectedSpanWithPayload(temp.DatabasePath, temp.RetentionContext, "generic-trace-2", "generic-span-2", "unrecognized-client", "generic-repo", now, payload);

        var processed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, TimeProvider.System).ProcessNextBatch(100);

        Assert.Equal(1, processed);
        var unbound = Assert.Single(store.ListMostRecent(10));
        Assert.Equal(SessionCompleteness.Unbound, unbound.Completeness);
    }

    [Fact]
    public void ProcessNextBatch_GenericPathAmbiguousSessionIdAttributesRemainUnbound()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var hookSessionId = SeedClaudeSession(store, "AMBIGUOUS_NATIVE_001", SessionBindingKind.Native);
        var payload = BuildOtelPayload("generic-trace-3", "generic-span-3", "AMBIGUOUS_NATIVE_001", "OTHER_VALUE");
        InsertProjectedSpanWithPayload(temp.DatabasePath, temp.RetentionContext, "generic-trace-3", "generic-span-3", "unrecognized-client", "generic-repo", now, payload);

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, TimeProvider.System).ProcessNextBatch(100);

        Assert.DoesNotContain(store.GetDetail(hookSessionId)!.Events, item => item.SourceEventId == "generic-trace-3/generic-span-3");
        Assert.Contains(store.ListMostRecent(10), item => item.SessionId != hookSessionId && item.Completeness == SessionCompleteness.Unbound);
    }

    [Theory]
    [InlineData("byte_native_001")]
    [InlineData("BYTE_NATIVE_001 ")]
    public void ProcessNextBatch_GenericPathByteMismatchDoesNotBind(string nearNativeId)
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var hookSessionId = SeedClaudeSession(store, "BYTE_NATIVE_001", SessionBindingKind.Native);
        var payload = BuildOtelPayload("generic-trace-4", "generic-span-4", nearNativeId);
        InsertProjectedSpanWithPayload(temp.DatabasePath, temp.RetentionContext, "generic-trace-4", "generic-span-4", "unrecognized-client", "generic-repo", now, payload);

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, TimeProvider.System).ProcessNextBatch(100);

        Assert.DoesNotContain(store.GetDetail(hookSessionId)!.Events, item => item.SourceEventId == "generic-trace-4/generic-span-4");
        Assert.Contains(store.ListMostRecent(10), item => item.SessionId != hookSessionId && item.Completeness == SessionCompleteness.Unbound);
    }

    private static string BuildOtelPayload(string traceId, string spanId, params string[] sessionIdValues)
    {
        var attributes = string.Join(",", sessionIdValues.Select(value =>
            $$$"""{"key":"session.id","value":{"stringValue":"{{{value}}}"}}"""));
        return $$$"""
            {"resourceSpans":[{"scopeSpans":[{"spans":[
              {"traceId":"{{{traceId}}}","spanId":"{{{spanId}}}","attributes":[{{{attributes}}}]}
            ]}]}]}
            """;
    }

    private static void InsertProjectedSpanWithPayload(
        string databasePath, RetentionCatalogContext retentionContext, string traceId, string spanId, string clientKind, string repository, DateTimeOffset time, string payloadJson)
    {
        var rawRecordId = new RawTelemetryStore(
            databasePath,
            retentionContext,
            connectionOptions: RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Insert(new RawTelemetryRecord(null, "raw-otlp", traceId, time, null, payloadJson));
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var trace = connection.CreateCommand();
        trace.Transaction = transaction;
        trace.CommandText = """
            INSERT INTO monitor_traces(trace_id,client_kind,first_seen_at,last_seen_at,span_count,projected_at,repository_name)
            VALUES($trace_id,$client_kind,$time,$time,1,$time,$repository);
            """;
        trace.Parameters.AddWithValue("$trace_id", traceId);
        trace.Parameters.AddWithValue("$client_kind", clientKind);
        trace.Parameters.AddWithValue("$time", time.ToString("O"));
        trace.Parameters.AddWithValue("$repository", repository);
        trace.ExecuteNonQuery();
        using var span = connection.CreateCommand();
        span.Transaction = transaction;
        span.CommandText = """
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,start_time,projected_at)
            VALUES($raw_record_id,$trace_id,$span_id,0,$time,$time);
            """;
        span.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        span.Parameters.AddWithValue("$trace_id", traceId);
        span.Parameters.AddWithValue("$span_id", spanId);
        span.Parameters.AddWithValue("$time", time.ToString("O"));
        span.ExecuteNonQuery();
        transaction.Commit();
    }

    [Theory]
    [InlineData(SessionBindingKind.Native)]
    [InlineData(SessionBindingKind.ExplicitResume)]
    [InlineData(SessionBindingKind.ExplicitHandoff)]
    public void ProcessNextBatch_ClaudeFixtureBindsOnlyExactNativeOrExplicitIdentity(SessionBindingKind bindingKind)
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var sessionId = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", bindingKind);

        var processed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, new FixedTimeProvider(ObservedAt.AddMinutes(1)))
            .ProcessNextBatch(100);

        Assert.Equal(6, processed);
        Assert.Single(store.ListMostRecent(10));
        var detail = Assert.IsType<SessionDetail>(store.GetDetail(sessionId));
        Assert.Equal(SessionCompleteness.Full, detail.Session.Completeness);
        Assert.Equal(bindingKind, Assert.Single(detail.NativeIds).BindingKind);
        var otelEvents = detail.Events.Where(item => item.SourceAdapter == "claude-code-otel").ToArray();
        Assert.Equal(6, otelEvents.Length);
        Assert.All(otelEvents, item =>
        {
            Assert.Equal(SessionSourceSurface.ClaudeCode, item.SourceSurface);
            Assert.Equal("claude-otel-v1", item.AdapterVersion);
            Assert.NotNull(item.SchemaFingerprint);
            Assert.Null(item.NormalizationVersion);
            Assert.Equal("ok", item.Status);
            Assert.Equal(SessionContentState.NotCaptured, item.ContentState);
        });
        Assert.Equal(6, detail.Runs.Count);
        var llmRun = Assert.Single(detail.Runs, run => run.Model == "SYNTHETIC_MODEL");
        Assert.Equal(12, llmRun.InputTokens);
        Assert.Equal(7, llmRun.OutputTokens);
        Assert.Null(llmRun.TotalTokens);
        Assert.Equal(ObservedSessionStatus.Completed, llmRun.Status);
        Assert.Equal(6, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    [Theory]
    [InlineData("synthetic_session_001")]
    [InlineData("SYNTHETIC_SESSION_001 ")]
    public void ProcessNextBatch_ClaudeNearMatchesAndForbiddenHeuristicsRemainUnbound(string nearNativeId)
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var existingSessionId = SeedClaudeSession(
            store,
            nearNativeId,
            SessionBindingKind.Native,
            traceId: "11111111111111111111111111111111",
            repository: "same-repository",
            workspace: "same-workspace");
        Execute(
            temp.DatabasePath,
            "UPDATE monitor_traces SET repository_name='same-repository',workspace_label='same-workspace';");

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext).ProcessNextBatch(100);

        var existing = Assert.IsType<SessionDetail>(store.GetDetail(existingSessionId));
        Assert.DoesNotContain(existing.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Equal(SessionCompleteness.Rich, existing.Session.Completeness);
        Assert.Contains(store.ListMostRecent(20), item => item.SessionId != existingSessionId && item.Completeness == SessionCompleteness.Unbound);
    }

    [Fact]
    public void ProcessNextBatch_DuplicateClaudeSessionIdsOverlapAndRemainUnbound()
    {
        using var temp = new MonitorTempDirectory();
        var payload = JsonNode.Parse(ReadClaudeFixture())!.AsObject();
        var firstSpan = payload["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!.AsObject();
        firstSpan["attributes"]!.AsArray().Add(JsonNode.Parse(
            """{"key":"session.id","value":{"stringValue":"SECOND_SESSION"}}"""));
        var store = PrepareClaudeFixture(temp.DatabasePath, payload.ToJsonString());
        var first = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", SessionBindingKind.Native);
        var second = SeedClaudeSession(store, "SECOND_SESSION", SessionBindingKind.Native);

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext).ProcessNextBatch(1);

        Assert.DoesNotContain(store.GetDetail(first)!.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.DoesNotContain(store.GetDetail(second)!.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Contains(store.ListMostRecent(10), item => item.SessionId != first && item.SessionId != second && item.Completeness == SessionCompleteness.Unbound);
    }

    [Fact]
    public void ProcessNextBatch_IncompleteTraceContextCannotUseTraceContextBindingKind()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var sessionId = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", SessionBindingKind.TraceContext);

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext).ProcessNextBatch(1);

        Assert.DoesNotContain(store.GetDetail(sessionId)!.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Contains(store.ListMostRecent(10), item => item.SessionId != sessionId && item.Completeness == SessionCompleteness.Unbound);
    }

    [Fact]
    public void ProcessNextBatch_StaleClaudeSourceIdentityDoesNotMoveOrDuplicate()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var originalOwner = SeedClaudeSession(store, "ORIGINAL_NATIVE", SessionBindingKind.Native);
        var original = store.GetDetail(originalOwner)!;
        var sourceIdentity = "11111111111111111111111111111111/1111111111111111";
        store.Write(new(new(
            original.Session,
            [],
            [],
            [new ObservedSessionEvent(
                Guid.CreateVersion7(), originalOwner, null, SessionSourceSurface.ClaudeCode, null,
                "11111111111111111111111111111111", "ok", "claude-code-otel", sourceIdentity,
                "otel.span", ObservedAt, SessionContentState.NotCaptured)]), []));
        var competingOwner = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", SessionBindingKind.Native);

        Assert.Throws<SessionIdentityConflictException>(() =>
            new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext).ProcessNextBatch(1));

        Assert.Single(store.GetDetail(originalOwner)!.Events, item => item.SourceAdapter == "claude-code-otel" && item.SourceEventId == sourceIdentity);
        Assert.DoesNotContain(store.GetDetail(competingOwner)!.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Null(store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey));
    }

    [Fact]
    public void ProcessNextBatch_ExactClaudeReplayUsesCanonicalIdentityAndCompleteComparator()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var sessionId = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", SessionBindingKind.Native);
        var firstClock = new FixedTimeProvider(ObservedAt.AddMinutes(1));
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, firstClock);
        Assert.Equal(1, enricher.ProcessNextBatch(1));
        var detailBefore = store.GetDetail(sessionId)!;
        var databaseBefore = ReadSessionPersistenceSnapshot(temp.DatabasePath);
        store.UpsertProjectionState(new(SqliteSessionOtelEnricher.ProjectorKey, 0, 0, firstClock.GetUtcNow()));
        var replay = new SqliteSessionOtelEnricher(
            temp.DatabasePath,
            store,
            temp.RetentionContext,
            new FixedTimeProvider(ObservedAt.AddDays(1)));

        Assert.Equal(1, replay.ProcessNextBatch(1));

        var detailAfter = store.GetDetail(sessionId)!;
        Assert.Equal(detailBefore.Session, detailAfter.Session);
        Assert.Equal(detailBefore.NativeIds, detailAfter.NativeIds);
        Assert.Equal(detailBefore.Runs, detailAfter.Runs);
        Assert.Equal(detailBefore.Events, detailAfter.Events);
        Assert.Equal(databaseBefore, ReadSessionPersistenceSnapshot(temp.DatabasePath));
        Assert.Equal(1, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    [Theory]
    [InlineData("UPDATE monitor_spans SET status='error' WHERE id=1;")]
    [InlineData("UPDATE monitor_spans SET start_time='2026-07-12T00:00:01.0000000+00:00' WHERE id=1;")]
    [InlineData("UPDATE monitor_spans SET end_time='2026-07-12T00:00:03.0000000+00:00' WHERE id=1;")]
    [InlineData("UPDATE source_schema_observations SET source_application_version='changed-version' WHERE raw_record_id=(SELECT raw_record_id FROM monitor_spans WHERE id=1);")]
    [InlineData("UPDATE source_schema_observations SET adapter_version='changed-adapter' WHERE raw_record_id=(SELECT raw_record_id FROM monitor_spans WHERE id=1);")]
    [InlineData("UPDATE source_schema_observations SET schema_fingerprint='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' WHERE raw_record_id=(SELECT raw_record_id FROM monitor_spans WHERE id=1);")]
    public void ProcessNextBatch_ClaudeReplayFieldMismatchRollsBackAndDoesNotAdvanceCursor(string mutation)
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var sessionId = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", SessionBindingKind.Native);
        var clock = new FixedTimeProvider(ObservedAt.AddMinutes(1));
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, clock);
        Assert.Equal(1, enricher.ProcessNextBatch(1));
        var detailBefore = store.GetDetail(sessionId)!;
        var databaseBefore = ReadSessionPersistenceSnapshot(temp.DatabasePath);
        store.UpsertProjectionState(new(SqliteSessionOtelEnricher.ProjectorKey, 0, 0, clock.GetUtcNow()));
        Execute(temp.DatabasePath, mutation);

        Assert.Throws<InvalidOperationException>(() => enricher.ProcessNextBatch(1));

        var detailAfter = store.GetDetail(sessionId)!;
        Assert.Equal(detailBefore.Session, detailAfter.Session);
        Assert.Equal(detailBefore.NativeIds, detailAfter.NativeIds);
        Assert.Equal(detailBefore.Runs, detailAfter.Runs);
        Assert.Equal(detailBefore.Events, detailAfter.Events);
        Assert.Equal(databaseBefore, ReadSessionPersistenceSnapshot(temp.DatabasePath));
        Assert.Equal(0, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    [Fact]
    public void ProcessNextBatch_ClaudeWriteFailureRollsBackAggregateAndDoesNotAdvanceCursor()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var sessionId = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", SessionBindingKind.Native);
        var retentionContext = temp.RetentionContext;
        Execute(temp.DatabasePath, """
            CREATE TRIGGER fail_claude_enrichment BEFORE INSERT ON session_events
            WHEN NEW.source_adapter IN ('otel-exact','claude-code-otel')
            BEGIN SELECT RAISE(ABORT,'synthetic enrichment failure'); END;
            """);

        Assert.Throws<SqliteException>(() => new SqliteSessionOtelEnricher(temp.DatabasePath, store, retentionContext).ProcessNextBatch(1));

        var afterFailure = store.GetDetail(sessionId)!;
        Assert.Equal(SessionCompleteness.Rich, afterFailure.Session.Completeness);
        Assert.Empty(afterFailure.Runs);
        Assert.DoesNotContain(afterFailure.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Null(store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey));

        Execute(temp.DatabasePath, "DROP TRIGGER fail_claude_enrichment;");
        Assert.Equal(1, new SqliteSessionOtelEnricher(temp.DatabasePath, store, retentionContext).ProcessNextBatch(1));
        var afterRetry = store.GetDetail(sessionId)!;
        Assert.Single(afterRetry.Runs);
        Assert.Single(afterRetry.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Equal(1, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    [Fact]
    public void ProcessNextBatch_RawGrantLostAfterMaterialization_WritesNothingAndDoesNotAdvanceCursor()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        _ = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", SessionBindingKind.Native);
        var enricher = new SqliteSessionOtelEnricher(
            temp.DatabasePath,
            store,
            temp.RetentionContext,
            new FixedTimeProvider(ObservedAt.AddMinutes(1)),
            checkpoint =>
            {
                if (checkpoint == "before_raw_terminal")
                    Execute(temp.DatabasePath, "DELETE FROM retention_leases WHERE lease_kind='operation';");
            });

        Assert.Equal(0, enricher.ProcessNextBatch(1));
        Assert.Null(store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey));
    }

    private static ObservedSessionEvent Event(Guid sessionId, string sourceId, string type, DateTimeOffset occurredAt) =>
        new(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.HookUnknown, null, null, null, "copilot-compatible-hook", sourceId, type, occurredAt, SessionContentState.NotCaptured);

    private static SqliteSessionStore PrepareClaudeFixture(string databasePath, string payload)
    {
        var compatibilityStore = new SqliteSourceCompatibilityStore(
            databasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        compatibilityStore.CreateSchema();
        var store = new SqliteSessionStore(databasePath);
        store.CreateSchema();
        var inventory = OtlpJsonStructuralWalker.Build(payload, ObservedAt);
        var decision = SourceCompatibilityEvaluator.Assess(
            "claude-code",
            sourceApplicationVersion: null,
            inventory,
            6,
            VerifiedSourceFingerprintRegistry.Create([], [], []));
        var raw = RawOtlpIngestor.CreateRecordFromPayloadJson(payload, ObservedAt);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"),
            "claude-code",
            sourceApplicationVersion: null,
            "claude-code-otel",
            "claude-otel-v1",
            inventory,
            decision,
            SourceCaptureContentState.NotCaptured,
            ObservedAt);
        var committed = new SqliteIngestionCommitStore(
            databasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(raw, observation));
        var persisted = raw with { Id = committed.RawRecordId };
        var projectionStore = new RawTelemetryStore(
            databasePath,
            RetentionCatalogContext.AdoptExistingCatalogV1(databasePath),
            connectionOptions: RawTelemetryStoreConnectionOptions.MonitorWriter);
        projectionStore.ApplyProjection(
            committed.RawRecordId,
            persisted.Source,
            persisted.ReceivedAt,
            MonitorProjectionBuilder.Build(persisted),
            ObservedAt);
        projectionStore.ApplySpanProjection(
            committed.RawRecordId,
            MonitorSpanProjectionBuilder.Build(persisted),
            ObservedAt);
        return store;
    }

    private static Guid SeedClaudeSession(
        SqliteSessionStore store,
        string nativeSessionId,
        SessionBindingKind bindingKind,
        string? traceId = null,
        string? repository = null,
        string? workspace = null)
    {
        var sessionId = Guid.CreateVersion7();
        var start = Event(sessionId, $"{nativeSessionId}-start", "SessionStart", ObservedAt) with
        {
            SourceSurface = SessionSourceSurface.ClaudeCode,
            SourceAdapter = "claude-code-hook",
            SourceApplicationVersion = "synthetic-version",
            AdapterVersion = "claude-hook-v1",
            NormalizationVersion = "session-normalization-v1",
        };
        var prompt = Event(sessionId, $"{nativeSessionId}-prompt", "UserPromptSubmit", ObservedAt.AddSeconds(1)) with
        {
            SourceSurface = SessionSourceSurface.ClaudeCode,
            SourceAdapter = "claude-code-hook",
            TraceId = traceId,
            SourceApplicationVersion = "synthetic-version",
            AdapterVersion = "claude-hook-v1",
            NormalizationVersion = "session-normalization-v1",
        };
        var end = Event(sessionId, $"{nativeSessionId}-end", "SessionEnd", ObservedAt.AddSeconds(2)) with
        {
            SourceSurface = SessionSourceSurface.ClaudeCode,
            SourceAdapter = "claude-code-hook",
            SourceApplicationVersion = "synthetic-version",
            AdapterVersion = "claude-hook-v1",
            NormalizationVersion = "session-normalization-v1",
        };
        var batch = new SessionWriteBatch(new(
            new ObservedSession(
                sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Rich,
                repository, workspace, ObservedAt, ObservedAt.AddSeconds(2), ObservedAt.AddSeconds(2),
                SessionRawRetentionState.NotCaptured, ObservedAt, ObservedAt),
            [new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, nativeSessionId, bindingKind, ObservedAt)],
            [],
            [start, prompt, end]), []);
        ((IClassifiedSessionStore)store).WriteClassified(
            batch,
            [new SessionTerminalFact(end.EventId, SessionTerminalOutcome.Clean)]);
        return sessionId;
    }

    private static string ReadClaudeFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Claude", "otel", "content-disabled.json"));

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CreateSpanFactsTable(string databasePath) => Execute(
        databasePath,
        """
        CREATE TABLE local_workspace_span_facts (
            raw_record_id INTEGER NOT NULL,
            span_ordinal INTEGER NOT NULL,
            retry_count INTEGER NULL,
            producer_total_tokens INTEGER NULL,
            PRIMARY KEY(raw_record_id,span_ordinal));
        """);

    private static long Count(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static IReadOnlyList<string> ReadSessionPersistenceSnapshot(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'sessions|'||quote(session_id)||'|'||quote(status)||'|'||quote(completeness)||'|'||quote(repository)||'|'||quote(workspace)||'|'||quote(started_at)||'|'||quote(ended_at)||'|'||quote(last_seen_at)||'|'||quote(raw_retention_state)||'|'||quote(created_at)||'|'||quote(updated_at) FROM sessions
            UNION ALL
            SELECT 'session_native_ids|'||quote(session_id)||'|'||quote(source_surface)||'|'||quote(native_session_id)||'|'||quote(binding_kind)||'|'||quote(observed_at) FROM session_native_ids
            UNION ALL
            SELECT 'session_runs|'||quote(run_id)||'|'||quote(session_id)||'|'||quote(source_surface)||'|'||quote(native_run_id)||'|'||quote(trace_id)||'|'||quote(parent_run_id)||'|'||quote(model)||'|'||quote(started_at)||'|'||quote(ended_at)||'|'||quote(input_tokens)||'|'||quote(output_tokens)||'|'||quote(total_tokens)||'|'||quote(status) FROM session_runs
            UNION ALL
            SELECT 'session_events|'||quote(event_id)||'|'||quote(session_id)||'|'||quote(run_id)||'|'||quote(source_surface)||'|'||quote(parent_event_id)||'|'||quote(trace_id)||'|'||quote(status)||'|'||quote(source_adapter)||'|'||quote(source_event_id)||'|'||quote(type)||'|'||quote(occurred_at)||'|'||quote(content_state)||'|'||quote(source_application_version)||'|'||quote(adapter_version)||'|'||quote(schema_fingerprint)||'|'||quote(normalization_version)||'|'||quote(match_kind)||'|'||quote(terminal_outcome)||'|'||quote(terminal_policy_version) FROM session_events
            UNION ALL
            SELECT 'session_event_content|'||quote(event_id)||'|'||quote(content_kind)||'|'||quote(content_json)||'|'||quote(captured_at)||'|'||quote(expires_at)||'|'||quote(retention_owner_token) FROM session_event_content
            ORDER BY 1;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static void InsertProjectedSpan(string databasePath, RetentionCatalogContext retentionContext, string traceId, string spanId, string? conversationId, string clientKind, string repository, DateTimeOffset time)
    {
        var rawRecordId = new RawTelemetryStore(
            databasePath,
            retentionContext,
            connectionOptions: RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Insert(new RawTelemetryRecord(null, "raw-otlp", traceId, time, null, "{\"resourceSpans\":[]}"));
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var trace = connection.CreateCommand();
        trace.Transaction = transaction;
        trace.CommandText = """
            INSERT INTO monitor_traces(trace_id,client_kind,first_seen_at,last_seen_at,span_count,projected_at,repository_name)
            VALUES($trace_id,$client_kind,$time,$time,1,$time,$repository);
            """;
        trace.Parameters.AddWithValue("$trace_id", traceId);
        trace.Parameters.AddWithValue("$client_kind", clientKind);
        trace.Parameters.AddWithValue("$time", time.ToString("O"));
        trace.Parameters.AddWithValue("$repository", repository);
        trace.ExecuteNonQuery();
        using var span = connection.CreateCommand();
        span.Transaction = transaction;
        span.CommandText = """
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,conversation_id,start_time,projected_at)
            VALUES($raw_record_id,$trace_id,$span_id,0,$conversation_id,$time,$time);
            """;
        span.Parameters.AddWithValue("$trace_id", traceId);
        span.Parameters.AddWithValue("$span_id", spanId);
        span.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        span.Parameters.AddWithValue("$conversation_id", (object?)conversationId ?? DBNull.Value);
        span.Parameters.AddWithValue("$time", time.ToString("O"));
        span.ExecuteNonQuery();
        transaction.Commit();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

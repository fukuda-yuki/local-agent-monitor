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
        Execute(
            temp.DatabasePath,
            $"""
            UPDATE monitor_spans
            SET request_model='requested-model',
                response_model='response-model',
                status='ok',
                end_time='{endedAt:O}',
                input_tokens=10840,
                output_tokens=77,
                total_tokens=10917
            WHERE trace_id='generic-facts-trace'
              AND span_id='generic-facts-span';
            """);

        Assert.Equal(
            1,
            new SqliteSessionOtelEnricher(
                temp.DatabasePath,
                store,
                temp.RetentionContext).ProcessNextBatch(1));

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

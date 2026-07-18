using CopilotAgentObservability.LocalMonitor.SourceCompatibility;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionOtelEnrichmentTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-12T00:00:00Z");

    [Fact]
    public void ProcessNextBatch_UsesOnlyExactLinksAndCreatesUnboundForUnmatchedRows()
    {
        using var temp = new MonitorTempDirectory();
        new RawTelemetryStore(temp.DatabasePath).CreateMonitorSchema();
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
        store.Write(new(
            new(
                new(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Rich, "same-repo", null, now, now.AddSeconds(2), now.AddSeconds(2), SessionRawRetentionState.NotCaptured, now, now),
                [new(sessionId, SessionSourceSurface.HookUnknown, "native-exact", SessionBindingKind.Native, now)],
                [],
                events),
            []));
        InsertProjectedSpan(temp.DatabasePath, "trace-exact", "span-1", "native-exact", "vscode-copilot-chat", "same-repo", now.AddSeconds(3));
        InsertProjectedSpan(temp.DatabasePath, "trace-unmatched", "span-2", null, "vscode-copilot-chat", "same-repo", now.AddSeconds(4));
        InsertProjectedSpan(temp.DatabasePath, "trace-by-context", "span-3", null, "unrecognized-client", "same-repo", now.AddSeconds(5));

        var processor = new SqliteSessionOtelEnricher(temp.DatabasePath, store, TimeProvider.System);
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
        new RawTelemetryStore(temp.DatabasePath).CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var hookSessionId = SeedClaudeSession(store, "GENERIC_NATIVE_001", SessionBindingKind.Native);
        var payload = BuildOtelPayload("generic-trace-1", "generic-span-1", "GENERIC_NATIVE_001");
        InsertProjectedSpanWithPayload(temp.DatabasePath, "generic-trace-1", "generic-span-1", "unrecognized-client", "generic-repo", now.AddSeconds(1), payload);

        var processed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, TimeProvider.System).ProcessNextBatch(100);

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
    public void ProcessNextBatch_GenericPathPreservesReachabilityCountsAndAdapterLabels()
    {
        using var temp = new MonitorTempDirectory();
        new RawTelemetryStore(temp.DatabasePath).CreateMonitorSchema();
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
            temp.DatabasePath,
            "generic-exact-trace",
            "generic-span-1",
            "vscode-copilot-chat",
            "generic-repo",
            now.AddSeconds(1),
            BuildOtelPayload("generic-exact-trace", "generic-span-1", "GENERIC_EXACT_001"));
        InsertProjectedSpan(temp.DatabasePath, "generic-shared-trace", "generic-span-2", null, "vscode-copilot-chat", "generic-repo", now.AddSeconds(2));
        InsertProjectedSpan(temp.DatabasePath, "generic-conversation-trace", "generic-span-3", "GENERIC_CONVERSATION_001", "vscode-copilot-chat", "generic-repo", now.AddSeconds(3));

        var processed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, TimeProvider.System).ProcessNextBatch(100);

        Assert.Equal(3, processed);
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

        new SqliteSessionOtelEnricher(temp.DatabasePath, store).ProcessNextBatch(1);

        var detail = store.GetDetail(sessionId)!;
        Assert.Single(detail.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Equal(bindingKind, Assert.Single(detail.NativeIds).BindingKind);
        Assert.Equal(SessionCompleteness.Full, detail.Session.Completeness);
    }

    [Fact]
    public void ProcessNextBatch_GenericPathStillUsesSharedTraceIdContinuity()
    {
        using var temp = new MonitorTempDirectory();
        new RawTelemetryStore(temp.DatabasePath).CreateMonitorSchema();
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
        InsertProjectedSpan(temp.DatabasePath, "shared-trace-id", "shared-span-1", null, "unrecognized-client", "generic-repo", now.AddSeconds(1));

        new SqliteSessionOtelEnricher(temp.DatabasePath, store).ProcessNextBatch(1);

        Assert.Single(store.GetDetail(sessionId)!.Events, item => item.SourceEventId == "shared-trace-id/shared-span-1");
    }

    [Fact]
    public void ProcessNextBatch_GenericPathWithoutHookSessionCreatesFreshUnboundSession()
    {
        using var temp = new MonitorTempDirectory();
        new RawTelemetryStore(temp.DatabasePath).CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var payload = BuildOtelPayload("generic-trace-2", "generic-span-2", "NO_MATCHING_HOOK_SESSION");
        InsertProjectedSpanWithPayload(temp.DatabasePath, "generic-trace-2", "generic-span-2", "unrecognized-client", "generic-repo", now, payload);

        var processed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, TimeProvider.System).ProcessNextBatch(100);

        Assert.Equal(1, processed);
        var unbound = Assert.Single(store.ListMostRecent(10));
        Assert.Equal(SessionCompleteness.Unbound, unbound.Completeness);
    }

    [Fact]
    public void ProcessNextBatch_GenericPathAmbiguousSessionIdAttributesRemainUnbound()
    {
        using var temp = new MonitorTempDirectory();
        new RawTelemetryStore(temp.DatabasePath).CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var hookSessionId = SeedClaudeSession(store, "AMBIGUOUS_NATIVE_001", SessionBindingKind.Native);
        var payload = BuildOtelPayload("generic-trace-3", "generic-span-3", "AMBIGUOUS_NATIVE_001", "OTHER_VALUE");
        InsertProjectedSpanWithPayload(temp.DatabasePath, "generic-trace-3", "generic-span-3", "unrecognized-client", "generic-repo", now, payload);

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, TimeProvider.System).ProcessNextBatch(100);

        Assert.DoesNotContain(store.GetDetail(hookSessionId)!.Events, item => item.SourceEventId == "generic-trace-3/generic-span-3");
        Assert.Contains(store.ListMostRecent(10), item => item.SessionId != hookSessionId && item.Completeness == SessionCompleteness.Unbound);
    }

    [Theory]
    [InlineData("byte_native_001")]
    [InlineData("BYTE_NATIVE_001 ")]
    public void ProcessNextBatch_GenericPathByteMismatchDoesNotBind(string nearNativeId)
    {
        using var temp = new MonitorTempDirectory();
        new RawTelemetryStore(temp.DatabasePath).CreateMonitorSchema();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var hookSessionId = SeedClaudeSession(store, "BYTE_NATIVE_001", SessionBindingKind.Native);
        var payload = BuildOtelPayload("generic-trace-4", "generic-span-4", nearNativeId);
        InsertProjectedSpanWithPayload(temp.DatabasePath, "generic-trace-4", "generic-span-4", "unrecognized-client", "generic-repo", now, payload);

        new SqliteSessionOtelEnricher(temp.DatabasePath, store, TimeProvider.System).ProcessNextBatch(100);

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
        string databasePath, string traceId, string spanId, string clientKind, string repository, DateTimeOffset time, string payloadJson)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var raw = connection.CreateCommand();
        raw.Transaction = transaction;
        raw.CommandText = """
            INSERT INTO raw_records(source,trace_id,received_at,payload_json,schema_version)
            VALUES('raw-otlp',$trace_id,$time,$payload,1);
            """;
        raw.Parameters.AddWithValue("$trace_id", traceId);
        raw.Parameters.AddWithValue("$time", time.ToString("O"));
        raw.Parameters.AddWithValue("$payload", payloadJson);
        raw.ExecuteNonQuery();
        using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        var rawRecordId = (long)idCommand.ExecuteScalar()!;
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

        var processed = new SqliteSessionOtelEnricher(temp.DatabasePath, store, new FixedTimeProvider(ObservedAt.AddMinutes(1)))
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

        new SqliteSessionOtelEnricher(temp.DatabasePath, store).ProcessNextBatch(100);

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

        new SqliteSessionOtelEnricher(temp.DatabasePath, store).ProcessNextBatch(1);

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

        new SqliteSessionOtelEnricher(temp.DatabasePath, store).ProcessNextBatch(1);

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

        new SqliteSessionOtelEnricher(temp.DatabasePath, store).ProcessNextBatch(1);

        Assert.Single(store.GetDetail(originalOwner)!.Events, item => item.SourceAdapter == "claude-code-otel" && item.SourceEventId == sourceIdentity);
        Assert.DoesNotContain(store.GetDetail(competingOwner)!.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Equal(1, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    [Fact]
    public void ProcessNextBatch_ClaudeWriteFailureRollsBackAggregateAndDoesNotAdvanceCursor()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeFixture(temp.DatabasePath, ReadClaudeFixture());
        var sessionId = SeedClaudeSession(store, "SYNTHETIC_SESSION_001", SessionBindingKind.Native);
        Execute(temp.DatabasePath, """
            CREATE TRIGGER fail_claude_enrichment BEFORE INSERT ON session_events
            WHEN NEW.source_adapter IN ('otel-exact','claude-code-otel')
            BEGIN SELECT RAISE(ABORT,'synthetic enrichment failure'); END;
            """);

        Assert.Throws<SqliteException>(() => new SqliteSessionOtelEnricher(temp.DatabasePath, store).ProcessNextBatch(1));

        var afterFailure = store.GetDetail(sessionId)!;
        Assert.Equal(SessionCompleteness.Rich, afterFailure.Session.Completeness);
        Assert.Empty(afterFailure.Runs);
        Assert.DoesNotContain(afterFailure.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Null(store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey));

        Execute(temp.DatabasePath, "DROP TRIGGER fail_claude_enrichment;");
        Assert.Equal(1, new SqliteSessionOtelEnricher(temp.DatabasePath, store).ProcessNextBatch(1));
        var afterRetry = store.GetDetail(sessionId)!;
        Assert.Single(afterRetry.Runs);
        Assert.Single(afterRetry.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Equal(1, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
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
            RawTelemetryStoreConnectionOptions.MonitorWriter);
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
        store.Write(new(new(
            new ObservedSession(
                sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Rich,
                repository, workspace, ObservedAt, ObservedAt.AddSeconds(2), ObservedAt.AddSeconds(2),
                SessionRawRetentionState.NotCaptured, ObservedAt, ObservedAt),
            [new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, nativeSessionId, bindingKind, ObservedAt)],
            [],
            [start, prompt, end]), []));
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

    private static void InsertProjectedSpan(string databasePath, string traceId, string spanId, string? conversationId, string clientKind, string repository, DateTimeOffset time)
    {
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
        span.Parameters.AddWithValue("$raw_record_id", int.Parse(spanId.AsSpan(spanId.Length - 1), System.Globalization.CultureInfo.InvariantCulture));
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

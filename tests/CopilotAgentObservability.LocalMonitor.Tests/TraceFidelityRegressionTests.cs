using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class TraceFidelityRegressionTests
{
    private const string FixtureName = "github-copilot-vscode-v1";
    private const string FixtureClassification = "repository_safe_conformance";
    private const string MappingEvidence = "docs/sprints/sprint9-monitor-agent-execution-view/milestones/M6-security-and-live-validation/live-validation.md#part-b--vs-code-github-copilot-chat-pending-user-confirmation";
    private const string LiveProducerFixtureBlocker = "no_repository_safe_raw_vscode_copilot_export";
    private const string SourceSurface = "github-copilot-vscode";
    private const string SourceAdapter = "otel-http";
    private const string AdapterVersion = "1";
    private const string PinnedSchemaFingerprint = "dc755b625c7d947e05d2265b607baee26166dee9f55c748e05cba0b6dac5b40d";
    private const string PinnedInventoryHash = "3631858e493d2adad9bc7cf05989750e52ae42bd626ebf8ebd1b5df331cbc965";
    private static readonly string[] RawOnlyMarkers =
    [
        "SYNTHETIC_RAW_PROMPT_MARKER_62",
        "SYNTHETIC_RAW_RESPONSE_MARKER_62",
        "SYNTHETIC_TOOL_ARGUMENTS_MARKER_62",
        "SYNTHETIC_TOOL_RESULT_MARKER_62",
        "fidelity-user@example.invalid",
        "SYNTHETIC_USER_ID_MARKER_62",
        "SYNTHETIC_TOKEN_MARKER_62",
        "SYNTHETIC_CREDENTIAL_MARKER_62",
        "SYNTHETIC_JWT_MARKER_62",
        "C:\\synthetic-sensitive\\issue-62",
    ];
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse(
        "2026-07-12T00:00:00.0000000+00:00",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task GithubCopilotVscodeConformanceFixture_LfAndCrlfProduceIdenticalPinnedSanitizedGolden()
    {
        var checkedInFixture = File.ReadAllText(FidelityPath($"{FixtureName}.otlp.json"), Encoding.UTF8);
        var lfInput = Encoding.UTF8.GetBytes(checkedInFixture.ReplaceLineEndings("\n"));
        var crlfInput = Encoding.UTF8.GetBytes(checkedInFixture.ReplaceLineEndings("\r\n"));
        Assert.NotEqual(lfInput, crlfInput);

        var lf = await RunPipeline(lfInput);
        var crlf = await RunPipeline(crlfInput);

        Assert.Equal(lf.GoldenBytes, crlf.GoldenBytes);
        Assert.Equal(File.ReadAllBytes(FidelityPath($"{FixtureName}.golden.json")), lf.GoldenBytes);
        Assert.Equal(lfInput, Encoding.UTF8.GetBytes(lf.Raw.PayloadJson));
        Assert.Equal(crlfInput, Encoding.UTF8.GetBytes(crlf.Raw.PayloadJson));
        AssertRawSanitizedBoundary(lf);
        AssertRawSanitizedBoundary(crlf);
    }

    private static async Task<PipelineResult> RunPipeline(byte[] fixtureBytes)
    {
        var decoded = OtlpTracePayloadDecoder.DecodeTracePayload("application/json", fixtureBytes);
        OtlpTracePayloadDecoder.EnsurePayloadContainsSpan(decoded.PayloadJson);
        Assert.Equal(PinnedSchemaFingerprint, decoded.StructuralInventory.SchemaFingerprint);
        Assert.Equal(PinnedInventoryHash, decoded.StructuralInventory.InventoryHash);
        var recognizedSpanCount = CountRecognizedSpans(decoded.StructuralInventory);
        var decision = SourceCompatibilityEvaluator.Assess(
            SourceSurface,
            sourceApplicationVersion: null,
            decoded.StructuralInventory,
            recognizedSpanCount,
            VerifiedSourceFingerprintRegistry.Create([], [], []));
        Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, decision.State);
        var rawRecord = RawOtlpIngestor.CreateRecordFromPayloadJson(decoded.PayloadJson, ObservedAt);
        var observation = SourceObservationBatchDraft.Create(
            "00000000-0000-7000-8000-000000000062",
            SourceSurface,
            sourceApplicationVersion: null,
            SourceAdapter,
            AdapterVersion,
            decoded.StructuralInventory,
            decision,
            SourceCaptureContentState.Available,
            ObservedAt);

        using var temp = new MonitorTempDirectory();
        _ = temp.RetentionContext;
        var compatibilityStore = new SqliteSourceCompatibilityStore(
            temp.DatabasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        compatibilityStore.CreateSchema();
        var committed = new SqliteIngestionCommitStore(
            temp.DatabasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(rawRecord, observation));
        var projectionStore = new RawTelemetryStoreProjectionStore(new RawTelemetryStore(
            temp.DatabasePath,
            temp.RetentionContext,
            connectionOptions: RawTelemetryStoreConnectionOptions.MonitorWriter));

        var rawResult = await projectionStore.GetRawRecordByIdAsync(committed.RawRecordId, RetentionReadKind.Access, CancellationToken.None);
        RawTelemetryRecord persistedRaw;
        await using (var rawLease = Assert.IsType<RetentionReadLease<RawTelemetryRecord>>(rawResult.Lease))
        {
            persistedRaw = rawLease.Value;
        }
        var persistedObservation = Assert.IsType<SourceCompatibilityRow>(
            compatibilityStore.GetByRawRecordId(committed.RawRecordId));
        Assert.Equal(committed.ObservationId, persistedObservation.Id);
        Assert.Equal(committed.RawRecordId, persistedObservation.RawRecordId);
        Assert.Single(compatibilityStore.List(after: null, limit: 10));

        var fixedTime = new FixedTimeProvider(ObservedAt.AddMinutes(1));
        var health = new MonitorHealthState(fixedTime);
        health.MarkMigrationComplete();
        await new ProjectionWorker(projectionStore, health, compatibilityStore, fixedTime)
            .RunProjectionPassAsync();

        var traceId = Assert.IsType<string>(persistedRaw.TraceId);
        var traceRawResult = await projectionStore.ListRawRecordsByTraceIdAsync(traceId, limit: 10, RetentionReadKind.Access, CancellationToken.None);
        await using var traceRawLease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(traceRawResult.Lease);
        Assert.Single(traceRawLease.Value);
        var trace = Assert.IsType<MonitorTraceRow>(projectionStore.GetMonitorTrace(traceId));
        var spans = projectionStore.GetSpansForTrace(traceId);
        var graph = AgentExecutionGraphBuilder.Build(spans);
        var goldenBytes = BuildGolden(
            persistedObservation,
            recognizedSpanCount,
            trace,
            spans,
            graph);
        return new PipelineResult(persistedRaw, persistedObservation, trace, spans, graph, goldenBytes);
    }

    private static void AssertRawSanitizedBoundary(PipelineResult result)
    {
        var goldenJson = Encoding.UTF8.GetString(result.GoldenBytes);
        using var rawDocument = JsonDocument.Parse(result.Raw.PayloadJson);
        var rawStringValues = EnumerateStringValues(rawDocument.RootElement).ToHashSet(StringComparer.Ordinal);
        foreach (var marker in RawOnlyMarkers)
        {
            Assert.Contains(marker, rawStringValues);
            Assert.DoesNotContain(marker, goldenJson, StringComparison.Ordinal);
        }

        var sanitizedProjection = JsonSerializer.SerializeToUtf8Bytes(new
        {
            result.Observation.CompatibilityState,
            result.Observation.ReasonCodes,
            result.Trace,
            result.Spans,
            result.Graph,
        });
        var sanitizedProjectionJson = Encoding.UTF8.GetString(sanitizedProjection);
        foreach (var marker in RawOnlyMarkers)
        {
            Assert.DoesNotContain(marker, sanitizedProjectionJson, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return element.GetString()!;
            yield break;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var value in EnumerateStringValues(property.Value)) yield return value;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var value in EnumerateStringValues(item)) yield return value;
            }
        }
    }

    private static byte[] BuildGolden(
        SourceCompatibilityRow observation,
        int recognizedSpanCount,
        MonitorTraceRow trace,
        IReadOnlyList<MonitorSpanRow> spans,
        AgentExecutionGraph graph)
    {
        var snapshot = new
        {
            fixture_contract = FixtureName,
            fixture_provenance = new
            {
                classification = FixtureClassification,
                producer_envelope = "opentelemetry.proto.collector.trace.v1.ExportTraceServiceRequest JSON",
                mapping_evidence = MappingEvidence,
                mapping_evidence_status = "candidate_not_closure",
                live_producer_fixture_status = "blocked",
                live_producer_fixture_blocker = LiveProducerFixtureBlocker,
            },
            source = new
            {
                source_surface = observation.SourceSurface,
                source_application_version = observation.SourceApplicationVersion,
                source_adapter = observation.SourceAdapter,
                adapter_version = observation.AdapterVersion,
                schema_fingerprint = observation.SchemaFingerprint,
                inventory_hash = observation.InventoryHash,
                compatibility_state = observation.CompatibilityState,
                reason_codes = observation.ReasonCodes,
                next_action = observation.NextAction,
                capture_content_state = observation.CaptureContentState,
                recognized_span_count = recognizedSpanCount,
                unknown_span_count = observation.UnknownSpanCount,
                unknown_event_count = observation.UnknownEventCount,
                unknown_attribute_count = observation.UnknownAttributeCount,
            },
            trace = new
            {
                trace_id = trace.TraceId,
                span_count = trace.SpanCount,
                tool_call_count = trace.ToolCallCount,
                error_count = trace.ErrorCount,
                input_tokens = trace.InputTokens,
                output_tokens = trace.OutputTokens,
                total_tokens = trace.TotalTokens,
                cache_read_tokens = trace.CacheReadTokens,
                cache_creation_tokens = trace.CacheCreationTokens,
                duration_ms = trace.DurationMs,
                primary_model = trace.PrimaryModel,
                trace_status = trace.TraceStatus,
            },
            spans = spans.Select(span => new
            {
                span_ordinal = span.SpanOrdinal,
                trace_id = span.TraceId,
                span_id = span.SpanId,
                parent_span_id = span.ParentSpanId,
                operation = span.Operation,
                category = span.Category,
                agent_name = span.AgentName,
                tool_name = span.ToolName,
                request_model = span.RequestModel,
                response_model = span.ResponseModel,
                input_tokens = span.InputTokens,
                output_tokens = span.OutputTokens,
                total_tokens = span.TotalTokens,
                cache_read_tokens = span.CacheReadTokens,
                cache_creation_tokens = span.CacheCreationTokens,
                duration_ms = span.DurationMs,
                start_time = span.StartTime,
                end_time = span.EndTime,
                status = span.Status,
                error_type = span.ErrorType,
                finish_reasons = span.FinishReasons,
            }),
            evidence_resolution = new
            {
                graph.Summary.MainAgentName,
                graph.Summary.RootAgentCount,
                graph.Summary.SubagentInvocationCount,
                graph.Summary.UniqueSubagentCount,
                graph.Summary.MaxAgentDepth,
                graph.Summary.ParallelAgentGroupCount,
                graph.Summary.RelationshipQuality,
                graph.Summary.AgentPresence,
                span_edges = graph.Spans.Select(span => new
                {
                    span_id = span.SpanId,
                    owning_agent_span_id = span.OwningAgentSpanId,
                    parent_agent_span_id = span.ParentAgentSpanId,
                    agent_depth = span.AgentDepth,
                    agent_role = span.AgentRole,
                    relationship_source = span.RelationshipSource,
                    relationship_confidence = span.RelationshipConfidence,
                }),
                graph_warnings = graph.GraphWarnings,
            },
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, GoldenJsonOptions).ReplaceLineEndings("\n") + "\n");
    }

    private static int CountRecognizedSpans(SourceStructuralInventory inventory) =>
        inventory.StructuralOccurrences
            .Where(item => item.Envelope == SourceStructuralEnvelope.Span && item.Role == SourceStructuralRole.Envelope)
            .Sum(item => item.Count.Value);

    private static string FidelityPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Fidelity", fileName);

    private static JsonSerializerOptions GoldenJsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private sealed record PipelineResult(
        RawTelemetryRecord Raw,
        SourceCompatibilityRow Observation,
        MonitorTraceRow Trace,
        IReadOnlyList<MonitorSpanRow> Spans,
        AgentExecutionGraph Graph,
        byte[] GoldenBytes);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Projection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SourceCompatibilityIngestionTests
{
    [Fact]
    public async Task PostTraces_DefaultReceiverCommitsRawAndObservationBeforeAcknowledging()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rawRecordId = responseJson.RootElement.GetProperty("rawRecordId").GetInt64();
        var observationId = responseJson.RootElement.GetProperty("observationId").GetInt64();
        Assert.True(rawRecordId > 0);
        Assert.True(observationId > 0);

        var raw = Assert.Single(temp.CreateRawStore().ListRecords());
        var compatibilityStore = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        var observation = Assert.Single(compatibilityStore.List(after: null, limit: 200));
        Assert.Equal(rawRecordId, raw.Id);
        Assert.Equal(observationId, observation.Id);
        Assert.Equal(rawRecordId, observation.RawRecordId);
        Assert.Equal("raw-otlp", observation.SourceSurface);
        Assert.Null(observation.SourceApplicationVersion);
        Assert.Equal("raw-otlp", observation.SourceAdapter);
        Assert.Equal("1", observation.AdapterVersion);
        Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, observation.CompatibilityState);
        var lookedUpObservation = Assert.IsType<SourceCompatibilityRow>(compatibilityStore.GetByRawRecordId(rawRecordId));
        Assert.Equal(observation.Id, lookedUpObservation.Id);
        Assert.Equal(observation.CompatibilityState, lookedUpObservation.CompatibilityState);
        Assert.Null(compatibilityStore.GetByRawRecordId(rawRecordId + 1));
    }

    [Fact]
    public async Task PostTraces_EquivalentJsonAndProtobufUseOneKnownFingerprint()
    {
        var jsonInventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(EquivalentJson())).StructuralInventory;
        var protobufInventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/x-protobuf", OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest()).StructuralInventory;
        Assert.Equal(jsonInventory.SchemaFingerprint, protobufInventory.SchemaFingerprint);

        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("raw-otlp", "fixture-v1", jsonInventory.SchemaFingerprint)],
            [],
            []);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceFingerprintRegistry = registry,
        });

        var jsonResponse = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));
        using var protobufContent = new ByteArrayContent(OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest());
        protobufContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var protobufResponse = await host.Client.PostAsync("/v1/traces", protobufContent);

        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, protobufResponse.StatusCode);
        var observations = new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200);
        Assert.Equal(2, observations.Count);
        Assert.All(observations, item =>
        {
            Assert.Equal(jsonInventory.SchemaFingerprint, item.SchemaFingerprint);
            Assert.Equal(SourceCompatibilityState.Supported, item.CompatibilityState);
        });
    }

    [Fact]
    public async Task PostTraces_UnknownProtobufFieldDoesNotPoisonRecognizedProjection()
    {
        const string marker = "unknown-protobuf-value-marker";
        var payload = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest(),
            OtlpProtobufTestPayload.StringField(100, marker));
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var response = await host.Client.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var raw = Assert.Single(rawStore.ListRecords());
        Assert.DoesNotContain(marker, raw.PayloadJson, StringComparison.Ordinal);
        var observation = Assert.Single(
            new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.True(observation.UnknownAttributeCount > 0);

        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            projectionStore,
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));

        await worker.RunProjectionPassAsync();

        Assert.Single(projectionStore.GetSpansForTrace("11111111111111111111111111111111"));
        Assert.Equal(0, projectionStore.GetProjectionStatus().Backlog);
        Assert.Equal(0, projectionStore.GetSpanProjectionStatus().Backlog);
    }

    [Theory]
    [InlineData(ClaudeInteractionWithUserPrompt, "available")]
    [InlineData(ClaudeInteractionWithRedactedUserPrompt, "not_captured")]
    [InlineData(ClaudeInteractionWithoutGatedField, "not_captured")]
    [InlineData(ForeignSpanOnly, "unsupported")]
    public async Task PostTraces_DerivesTraceContentStateFromClaudeSpanEvidence(string payload, string expectedContentState)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            projectionStore,
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));
        await worker.RunProjectionPassAsync();

        var tracesResponse = await host.Client.GetAsync("/api/monitor/traces");
        Assert.Equal(HttpStatusCode.OK, tracesResponse.StatusCode);
        using var tracesJson = JsonDocument.Parse(await tracesResponse.Content.ReadAsStringAsync());
        var item = Assert.Single(tracesJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(expectedContentState, item.GetProperty("content_state").GetString());
    }

    private const string ClaudeInteractionWithUserPrompt = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "spanId":"1111111111111111",
          "name":"claude_code.interaction",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[{"key":"user_prompt","value":{"stringValue":"synthetic-marker"}}]
        }]}]}]}
        """;

    private const string ClaudeInteractionWithRedactedUserPrompt = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "spanId":"1111111111111111",
          "name":"claude_code.interaction",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[
            {"key":"user_prompt","value":{"stringValue":"<REDACTED>"}},
            {"key":"user_prompt_length","value":{"intValue":"16"}}
          ]
        }]}]}]}
        """;

    private const string ClaudeInteractionWithoutGatedField = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "spanId":"2222222222222222",
          "name":"claude_code.interaction",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[{"key":"session.id","value":{"stringValue":"synthetic-marker"}}]
        }]}]}]}
        """;

    private const string ForeignSpanOnly = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"cccccccccccccccccccccccccccccccc",
          "spanId":"3333333333333333",
          "name":"chat gpt-4o",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[]
        }]}]}]}
        """;

    [Fact]
    public async Task PostTraces_NewFingerprintIsCommittedAsDrift()
    {
        var known = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(EquivalentJson())).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("raw-otlp", "fixture-v1", known.SchemaFingerprint)],
            [],
            []);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceFingerprintRegistry = registry,
        });

        var response = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(EquivalentJson().Replace("\"resourceSpans\"", "\"futureEnvelope\":{},\"resourceSpans\"", StringComparison.Ordinal)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observation = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.SchemaDriftDetected], observation.ReasonCodes);
    }

    [Fact]
    public async Task PostTraces_RecognitionProfileDeficitIsPersistedAsRecognizedRecordDrop()
    {
        const string sourceVersion = "fixture-v1";
        var inventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(EquivalentJson())).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("raw-otlp", sourceVersion, inventory.SchemaFingerprint)],
            [],
            [SourceRecognitionProfileEvidence.Create(
                "raw-otlp",
                sourceVersion,
                inventory.SchemaFingerprint,
                SourceOccurrenceCount.Create(2))]);
        var metadata = OtlpTraceSourceMetadata.Create(
            "raw-otlp",
            sourceVersion,
            "raw-otlp",
            "1",
            SourceCaptureContentState.NotCaptured);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceFingerprintRegistry = registry,
            SourceMetadataProvider = new FixedOtlpTraceSourceMetadataProvider(metadata),
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observation = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(SourceCompatibilityState.RecognizedRecordDropDetected, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.RecognizedRecordDropDetected], observation.ReasonCodes);
    }

    [Fact]
    public async Task Projection_WrongRepresentationsStayRawOnlyAndBatchCompletes()
    {
        const string marker = "wrong-representation-raw-marker";
        var payload = """
        {
          "resourceSpans": [{
            "scopeSpans": [{
              "spans": [{
                "traceId":"11111111111111111111111111111111",
                "spanId":{"marker":"wrong-representation-raw-marker"},
                "parentSpanId":["wrong-representation-raw-marker"],
                "attributes":[{
                  "key":"gen_ai.request.model",
                  "value":{"stringValue":{"marker":"wrong-representation-raw-marker"}}
                }]
              },{
                "traceId":{"marker":"wrong-representation-raw-marker"},
                "spanId":"valid-shape-id",
                "attributes":[]
              }]
            }]
          }]
        }
        """;
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });
        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            projectionStore,
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));

        await worker.RunProjectionPassAsync();

        var span = Assert.Single(projectionStore.GetSpansForTrace("11111111111111111111111111111111"));
        Assert.Null(span.SpanId);
        Assert.Null(span.ParentSpanId);
        Assert.Null(span.RequestModel);
        Assert.Single(projectionStore.ListMonitorSpans("11111111111111111111111111111111", 0, 200).Items);
        Assert.Equal(0, projectionStore.GetProjectionStatus().Backlog);
        Assert.Equal(0, projectionStore.GetSpanProjectionStatus().Backlog);
        Assert.Contains(marker, Assert.Single(rawStore.ListRecords()).PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTraces_UsesOnlyTrustedInjectedSourceMetadata()
    {
        using var temp = new MonitorTempDirectory();
        var metadata = OtlpTraceSourceMetadata.Create(
            "trusted-fixture-source",
            "2.1.207",
            "trusted-fixture-adapter",
            "7",
            SourceCaptureContentState.NotCaptured);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceMetadataProvider = new FixedOtlpTraceSourceMetadataProvider(metadata),
        });

        var response = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(EquivalentJson().Replace(
                "\"client.kind\"",
                "\"untrusted.source_surface\"",
                StringComparison.Ordinal)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observation = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal("trusted-fixture-source", observation.SourceSurface);
        Assert.Equal("2.1.207", observation.SourceApplicationVersion);
        Assert.Equal("trusted-fixture-adapter", observation.SourceAdapter);
        Assert.Equal("7", observation.AdapterVersion);
        Assert.Equal(SourceCaptureContentState.NotCaptured, observation.CaptureContentState);
    }

    [Fact]
    public async Task PostTraces_MissingRequiredSpanSignalIsCommittedAsUnsupported()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent("""{"resourceSpans":[]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(temp.CreateRawStore().ListRecords());
        var observation = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(SourceCompatibilityState.UnsupportedSourceVersion, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.UnsupportedSourceVersion], observation.ReasonCodes);
    }

    [Fact]
    public async Task PostTraces_WrongHierarchyCommitsOriginalRawAndUnsupportedObservation()
    {
        const string marker = "wrong-hierarchy-raw-only-marker";
        var payload = $$"""
        {
          "resourceSpans": {
            "marker": "{{marker}}",
            "scopeSpans": [{"spans": [{"traceId":"must-not-project"}]}]
          }
        }
        """;
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = Assert.Single(temp.CreateRawStore().ListRecords());
        Assert.Equal(payload, raw.PayloadJson);
        Assert.Contains(marker, raw.PayloadJson, StringComparison.Ordinal);
        Assert.Null(raw.TraceId);
        Assert.Null(raw.ResourceAttributesJson);
        var observation = Assert.Single(
            new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(raw.Id, observation.RawRecordId);
        Assert.Equal(SourceCompatibilityState.UnsupportedSourceVersion, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.UnsupportedSourceVersion], observation.ReasonCodes);
    }

    [Fact]
    public async Task PostTraces_NonObjectRootRecordsParseFailureWithoutRaw()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent("[]"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(temp.CreateRawStore().ListRecords());
        var failure = Assert.Single(
            new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Null(failure.RawRecordId);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, failure.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], failure.ReasonCodes);
    }

    [Fact]
    public async Task PostTraces_ParseFailureRecordsSanitizedNullableDiagnosticWithoutRaw()
    {
        const string marker = "RAW_PARSE_FAILURE_MARKER_7cefb";
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent($$"""{"resourceSpans":[{"marker":"{{marker}}"}"""));

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_payload", responseBody);
        Assert.DoesNotContain(marker, responseBody, StringComparison.Ordinal);
        Assert.Empty(temp.CreateRawStore().ListRecords());
        var failure = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Null(failure.RawRecordId);
        Assert.Null(failure.IngestBatchId);
        Assert.Null(failure.SourceSurface);
        Assert.Null(failure.SourceApplicationVersion);
        Assert.Null(failure.SourceAdapter);
        Assert.Null(failure.AdapterVersion);
        Assert.Null(failure.SchemaFingerprint);
        Assert.Null(failure.InventoryHash);
        Assert.Null(failure.CaptureContentState);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, failure.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], failure.ReasonCodes);
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(File.ReadAllBytes(temp.DatabasePath)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTraces_MalformedProtobufRecordsSanitizedParseFailureWithoutRaw()
    {
        const string marker = "MALFORMED_PROTOBUF_EXCEPTION_BYTES_9fd31";
        var payload = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.StringField(100, marker),
            [0x0a, 0x80]);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var response = await host.Client.PostAsync("/v1/traces", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_payload", responseBody);
        Assert.DoesNotContain(marker, responseBody, StringComparison.Ordinal);
        Assert.Empty(temp.CreateRawStore().ListRecords());
        var failure = Assert.Single(
            new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Null(failure.RawRecordId);
        Assert.Null(failure.IngestBatchId);
        Assert.Null(failure.SchemaFingerprint);
        Assert.Null(failure.InventoryHash);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, failure.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], failure.ReasonCodes);
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(File.ReadAllBytes(temp.DatabasePath)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTraces_AdapterExceptionRecordsSanitizedNullableDiagnosticWithoutRaw()
    {
        const string marker = "ADAPTER_EXCEPTION_MARKER_931fd";
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceMetadataProvider = new ThrowingSourceMetadataProvider(marker),
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("internal_error", responseBody);
        Assert.DoesNotContain(marker, responseBody, StringComparison.Ordinal);
        Assert.Empty(temp.CreateRawStore().ListRecords());
        var failure = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Null(failure.RawRecordId);
        Assert.Null(failure.IngestBatchId);
        Assert.Null(failure.SourceSurface);
        Assert.Null(failure.SourceApplicationVersion);
        Assert.Null(failure.SourceAdapter);
        Assert.Null(failure.AdapterVersion);
        Assert.Null(failure.SchemaFingerprint);
        Assert.Null(failure.InventoryHash);
        Assert.Null(failure.CaptureContentState);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, failure.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterException], failure.ReasonCodes);
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(File.ReadAllBytes(temp.DatabasePath)), StringComparison.Ordinal);
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed class ThrowingSourceMetadataProvider(string marker) : IOtlpTraceSourceMetadataProvider
    {
        public OtlpTraceSourceMetadata GetMetadata() => throw new InvalidOperationException(marker);
    }

    private static string EquivalentJson() =>
        """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"experiment.id","value":{"stringValue":"baseline"}}
        ]},"scopeSpans":[{"spans":[{
          "traceId":"11111111111111111111111111111111",
          "spanId":"2222222222222222",
          "name":"chat gpt-4o",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
            {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
          ]
        }]}]}]}
        """;
}

using CopilotAgentObservability.LocalMonitor.Events;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class ProjectionWorkerTests
{
    public static TheoryData<SourceCompatibilityState> RawBackedCompatibilityStates => new()
    {
        SourceCompatibilityState.Supported,
        SourceCompatibilityState.SupportedWithUnknownFields,
        SourceCompatibilityState.SchemaDriftDetected,
        SourceCompatibilityState.UnsupportedSourceVersion,
        SourceCompatibilityState.RecognizedRecordDropDetected,
    };

    [Theory]
    [MemberData(nameof(RawBackedCompatibilityStates))]
    public async Task Pass_ProjectsRecognizedSanitizedOutputForEveryRawBackedCompatibilityState(
        SourceCompatibilityState state)
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), OneSpanPayload("recognized-operation"));
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, state));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();
        await worker.RunProjectionPassAsync();

        Assert.True(store.IsProjected(1));
        Assert.Equal(1, store.AppliedProjections[1].SpanCount);
        Assert.Equal(1, store.ApplyCalls[1]);
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
        Assert.Equal([1L], compatibility.LookupRawRecordIds);
    }

    [Fact]
    public async Task Pass_UnknownValuesNeverEnterSanitizedProjection()
    {
        const string sensitiveUnknownValue = "must-not-be-projected";
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), OneSpanPayload("recognized-operation", sensitiveUnknownValue));
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.SupportedWithUnknownFields));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        var projectionJson = JsonSerializer.Serialize(store.AppliedProjections[1]);
        Assert.DoesNotContain(sensitiveUnknownValue, projectionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pass_UnknownRecordIsExcludedWhileRecognizedSpanStillProjectsAndBacklogCompletes()
    {
        const string unknownRecordMarker = "unknown-event-record";
        var payload = OneSpanPayload("recognized-operation").Replace(
            "\"attributes\": [",
            $"\"events\": [\"{unknownRecordMarker}\"],\n                \"attributes\": [",
            StringComparison.Ordinal);
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), payload);
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.SupportedWithUnknownFields));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        Assert.True(store.IsProjected(1));
        Assert.Equal(1, store.AppliedProjections[1].SpanCount);
        Assert.DoesNotContain(
            unknownRecordMarker,
            JsonSerializer.Serialize(store.AppliedProjections[1]),
            StringComparison.Ordinal);
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
        Assert.Equal(0, store.GetSpanProjectionStatus().Backlog);
    }

    [Fact]
    public async Task Pass_EmptyRecognizedViewCompletesBothBacklogsInOnePass()
    {
        const string marker = "no-valid-span-raw-marker";
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), JsonSerializer.Serialize(new { resourceSpans = new { marker } }));
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.UnsupportedSourceVersion));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        Assert.True(store.IsProjected(1));
        Assert.Equal(0, store.AppliedProjections[1].SpanCount);
        Assert.Empty(store.AppliedSpanProjections[1]);
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
        Assert.Equal(0, store.GetSpanProjectionStatus().Backlog);
        Assert.Contains(marker, store.GetPayload(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pass_DescriptorValidNumericStatusRemainsAnError()
    {
        var payload = OneSpanPayload("chat").Replace(
            "\"spanId\": \"s1\",",
            "\"spanId\": \"s1\",\n                \"kind\": 3,\n                \"status\": {\"code\": 2},",
            StringComparison.Ordinal);
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), payload);
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.Supported));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        Assert.Equal(1, store.AppliedProjections[1].TraceContributions.Single().ErrorCount);
        Assert.Equal("error", Assert.Single(store.AppliedSpanProjections[1]).Status);
    }

    [Fact]
    public void RecognizedView_DescriptorValidNumericKindMapsExactly()
    {
        var payload = OneSpanPayload("unknown-operation").Replace(
            "\"spanId\": \"s1\",",
            "\"spanId\": \"s1\",\n                \"kind\": 3,",
            StringComparison.Ordinal);

        using var document = JsonDocument.Parse(OtlpJsonRecognizedPayloadBuilder.Build(payload));
        var resourceSpan = Assert.Single(document.RootElement.GetProperty("resourceSpans").EnumerateArray());
        var scopeSpan = Assert.Single(resourceSpan.GetProperty("scopeSpans").EnumerateArray());
        var span = Assert.Single(scopeSpan.GetProperty("spans").EnumerateArray());

        Assert.Equal("3", OtlpSpanReader.CreateSpan(span).Kind);
    }

    [Fact]
    public async Task Pass_WrongTypedTraceIdDoesNotPersistAsAStringAndBacklogCompletes()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), WrongRepresentationPayload(
            traceId: "42",
            spanId: "\"span-1\"",
            parentSpanId: "\"parent-1\""));
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.SupportedWithUnknownFields));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        var span = Assert.Single(store.AppliedSpanProjections[1]);
        Assert.Null(span.TraceId);
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
        Assert.Equal(0, store.GetSpanProjectionStatus().Backlog);
    }

    [Fact]
    public async Task Pass_WrongTypedSpanAndParentIdsRemainNullInSpanProjection()
    {
        const string marker = "wrong-id-marker";
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), WrongRepresentationPayload(
            traceId: "\"t1\"",
            spanId: JsonSerializer.Serialize(new { unexpected = marker }),
            parentSpanId: JsonSerializer.Serialize(new[] { marker })));
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.SupportedWithUnknownFields));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        var span = Assert.Single(store.AppliedSpanProjections[1]);
        Assert.Equal("t1", span.TraceId);
        Assert.Null(span.SpanId);
        Assert.Null(span.ParentSpanId);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pass_WrongTypedStringValueDoesNotThrowOrEnterSpanProjection()
    {
        const string marker = "wrong-string-value-marker";
        var payload = WrongRepresentationPayload(
            traceId: "\"t1\"",
            spanId: "\"span-1\"",
            parentSpanId: "\"parent-1\"",
            attribute: JsonSerializer.Serialize(new
            {
                key = "gen_ai.request.model",
                value = new { stringValue = new { marker } },
            }));
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), payload);
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.SupportedWithUnknownFields));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        var span = Assert.Single(store.AppliedSpanProjections[1]);
        Assert.Null(span.RequestModel);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(span), StringComparison.Ordinal);
        Assert.Equal(payload, store.GetPayload(1));
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
        Assert.Equal(0, store.GetSpanProjectionStatus().Backlog);
    }

    [Fact]
    public async Task Pass_NameAttributesWithNestedAnyValuesDoNotLeakOrPoisonBacklog()
    {
        const string marker = "nested-any-value-name-marker";
        const string payload = """
        {
          "resourceSpans": [{
            "scopeSpans": [{
              "spans": [{
                "traceId": "t1",
                "spanId": "s1",
                "attributes": [{
                  "key": "gen_ai.request.model",
                  "value": {"arrayValue": {"values": [
                    42,
                    {"stringValue": "nested-any-value-name-marker"}
                  ]}}
                },{
                  "key": "gen_ai.tool.name",
                  "value": {"kvlistValue": {"values": [{
                    "key": "nested",
                    "value": {"stringValue": "nested-any-value-name-marker"}
                  }]}}
                }]
              }]
            }]
          }]
        }
        """;
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), payload);
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.SupportedWithUnknownFields));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        var span = Assert.Single(store.AppliedSpanProjections[1]);
        Assert.Null(span.RequestModel);
        Assert.Null(span.ToolName);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(span), StringComparison.Ordinal);
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
        Assert.Equal(0, store.GetSpanProjectionStatus().Backlog);
    }

    [Fact]
    public async Task Pass_ValidCopilotProjectionMatchesPinnedBytes()
    {
        var payload = CopilotPayload();
        var expectedTraceProjection = Encoding.UTF8.GetBytes(
            """{"TraceId":"t1","ClientKind":"vscode-copilot-chat","SpanCount":1,"TraceContributions":[{"TraceId":"t1","ClientKind":"vscode-copilot-chat","ExperimentId":null,"TaskId":null,"TaskCategory":null,"AgentVariant":null,"PromptVersion":null,"SpanCount":1,"ToolCallCount":0,"ErrorCount":0,"RepositoryName":"copilot-agent-observability","WorkspaceLabel":null,"RepoSnapshot":null}]}""");
        var expectedSpanProjection = Encoding.UTF8.GetBytes(
            """[{"TraceId":"t1","SpanId":"span-1","ParentSpanId":"parent-1","SpanOrdinal":0,"Operation":"chat","Category":"llm_call","ToolName":null,"ToolType":null,"McpToolName":null,"McpServerHash":null,"AgentName":null,"RequestModel":"gpt-4o","ResponseModel":null,"InputTokens":10,"OutputTokens":5,"TotalTokens":15,"ReasoningTokens":null,"CacheReadTokens":null,"CacheCreationTokens":null,"Status":"ok","ErrorType":null,"FinishReasons":null,"ConversationId":null,"DurationMs":500,"StartTime":"1970-01-01T00:00:01.0000000\u002B00:00","EndTime":"1970-01-01T00:00:01.5000000\u002B00:00"}]""");
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), payload);
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.Supported));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        Assert.Equal(expectedTraceProjection, JsonSerializer.SerializeToUtf8Bytes(store.AppliedProjections[1]));
        Assert.Equal(expectedSpanProjection, JsonSerializer.SerializeToUtf8Bytes(store.AppliedSpanProjections[1]));
    }

    [Fact]
    public async Task Pass_MissingObservationPreservesLegacyProjectionWithoutInventingCompatibilityState()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), OneSpanPayload("legacy-operation"));
        var compatibility = new FakeCompatibilityStore();
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        Assert.True(store.IsProjected(1));
        Assert.Equal([1L], compatibility.LookupRawRecordIds);
    }

    [Fact]
    public async Task Pass_CorruptPersistedLegacyRawLeavesBothBacklogsAndCreatesNoCompatibilityDiagnostic()
    {
        const string marker = "corrupt-legacy-raw-only-marker";
        const string corruptPayload = "{\"raw_only_marker\":\"corrupt-legacy-raw-only-marker\"";
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), corruptPayload);
        store.SeedProjected(2, T(2), corruptPayload);
        var compatibility = new FakeCompatibilityStore();
        var health = ReadyHealth();
        var broker = new MonitorEventBroker();
        using var subscription = broker.Subscribe();
        var worker = new ProjectionWorker(
            store,
            health,
            compatibilityStore: compatibility,
            eventBroker: broker);

        await worker.RunProjectionPassAsync();

        Assert.Equal(corruptPayload, store.GetPayload(1));
        Assert.Equal(corruptPayload, store.GetPayload(2));
        Assert.Contains(marker, store.GetPayload(1), StringComparison.Ordinal);
        Assert.Contains(marker, store.GetPayload(2), StringComparison.Ordinal);
        Assert.False(store.IsProjected(1));
        Assert.True(store.IsProjected(2));
        Assert.False(store.IsSpanProjected(2));
        Assert.Empty(store.ApplyCalls);
        Assert.Empty(store.SpanApplyCalls);
        Assert.Empty(store.AppliedProjections);
        Assert.Empty(store.AppliedSpanProjections);

        var traceStatus = store.GetProjectionStatus();
        Assert.Equal(1, traceStatus.Backlog);
        Assert.Equal(T(1), traceStatus.OldestUnprocessedReceivedAt);
        var spanStatus = store.GetSpanProjectionStatus();
        Assert.Equal(1, spanStatus.Backlog);
        Assert.Equal(T(2), spanStatus.OldestUnprocessedReceivedAt);

        var snapshot = health.Snapshot();
        Assert.Equal(1, snapshot.ProjectionBacklog);
        Assert.Equal(T(1), snapshot.OldestUnprocessedReceivedAt);
        Assert.Equal(1, snapshot.SpanProjectionBacklog);
        Assert.Equal(T(2), snapshot.OldestUnprocessedSpanReceivedAt);
        Assert.Equal(2, snapshot.ProjectionFailureCount);
        Assert.False(subscription.Reader.TryRead(out _));
        Assert.Equal(0, compatibility.CreateSchemaCallCount);
        Assert.Empty(compatibility.RecordAdapterFailureCalls);

        var visibleOutputs = JsonSerializer.Serialize(new
        {
            store.ApplyCalls,
            store.SpanApplyCalls,
            store.AppliedProjections,
            store.AppliedSpanProjections,
            compatibility.RecordAdapterFailureCalls,
        });
        Assert.DoesNotContain(marker, visibleOutputs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pass_AdapterFailureWithoutRawRecordHasNoProjectionWork()
    {
        var store = new FakeProjectionStore();
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(rawRecordId: null, SourceCompatibilityState.AdapterFailure));
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        Assert.Empty(store.ApplyCalls);
        Assert.Empty(compatibility.LookupRawRecordIds);
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
    }

    [Fact]
    public async Task Pass_PublishesOneProjectionEventWhenRecordsNewlyProjected()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        store.Seed(2, T(2));
        var broker = new MonitorEventBroker();
        using var subscription = broker.Subscribe();
        var worker = new ProjectionWorker(store, ReadyHealth(), eventBroker: broker);

        await worker.RunProjectionPassAsync();

        Assert.True(subscription.Reader.TryRead(out _));
        // Exactly one notification per pass, regardless of how many rows projected.
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Pass_DoesNotPublishWhenNothingNewlyProjected()
    {
        var store = new FakeProjectionStore();
        var broker = new MonitorEventBroker();
        using var subscription = broker.Subscribe();
        var worker = new ProjectionWorker(store, ReadyHealth(), eventBroker: broker);

        await worker.RunProjectionPassAsync();

        Assert.False(subscription.Reader.TryRead(out _));
    }
    [Fact]
    public async Task Pass_ProjectsPreExistingUnprocessedRows()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        store.Seed(2, T(2));
        store.Seed(3, T(3));
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        Assert.True(store.IsProjected(1));
        Assert.True(store.IsProjected(2));
        Assert.True(store.IsProjected(3));
        var snapshot = health.Snapshot();
        Assert.Equal(0, snapshot.ProjectionBacklog);
        Assert.Null(snapshot.OldestUnprocessedReceivedAt);
    }

    [Fact]
    public async Task Pass_IsNoOpUntilMigrationComplete()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        var health = new MonitorHealthState(); // migration not complete
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        Assert.False(store.IsProjected(1));
    }

    [Fact]
    public async Task Pass_ProjectsNewRowsAndDoesNotReprocess()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();
        store.Seed(2, T(2));
        await worker.RunProjectionPassAsync();

        Assert.Equal(1, store.ApplyCalls[1]);
        Assert.Equal(1, store.ApplyCalls[2]);
    }

    [Fact]
    public async Task Pass_BusyResultIsRetriedAndRawNotLost()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        var busy = true;
        store.OnApply = _ => busy ? ApplyOutcome.Busy : ApplyOutcome.Success;
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();
        Assert.False(store.IsProjected(1));
        Assert.Contains(1L, store.AllIds);

        busy = false;
        await worker.RunProjectionPassAsync();
        Assert.True(store.IsProjected(1));
    }

    [Fact]
    public async Task Pass_DispositionFailureLeavesBacklogForExplicitIdempotentReplay()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1), OneSpanPayload("recognized-operation"));
        var compatibility = new FakeCompatibilityStore();
        compatibility.Seed(Observation(1, SourceCompatibilityState.Supported));
        var fail = true;
        store.OnApply = _ => fail ? ApplyOutcome.Fail : ApplyOutcome.Success;
        var worker = new ProjectionWorker(store, ReadyHealth(), compatibilityStore: compatibility);

        await worker.RunProjectionPassAsync();

        Assert.False(store.IsProjected(1));
        Assert.Equal(1, store.GetProjectionStatus().Backlog);

        fail = false;
        await worker.RunProjectionPassAsync();

        Assert.True(store.IsProjected(1));
        Assert.Equal(0, store.GetProjectionStatus().Backlog);
        Assert.Equal(2, store.ApplyCalls[1]);
    }

    [Fact]
    public async Task Pass_NonBusyFailureIsolatesRowAndContinues()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        store.Seed(2, T(2));
        store.OnApply = id => id == 1 ? ApplyOutcome.Fail : ApplyOutcome.Success;
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        Assert.False(store.IsProjected(1));
        Assert.True(store.IsProjected(2));
        Assert.Contains(1L, store.AllIds);
        Assert.True(health.Snapshot().ProjectionFailureCount >= 1);
    }

    [Fact]
    public async Task Pass_UpdatesHealthBacklogAndOldest()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        store.Seed(2, T(2));
        store.OnApply = id => id == 1 ? ApplyOutcome.Success : ApplyOutcome.Busy;
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        var snapshot = health.Snapshot();
        Assert.Equal(1, snapshot.ProjectionBacklog);
        Assert.Equal(T(2), snapshot.OldestUnprocessedReceivedAt);
    }

    [Fact]
    public async Task Pass_UpdatesHealthWithRemainingSpanProjectionBacklog()
    {
        var store = new FakeProjectionStore();
        for (var i = 1; i <= 101; i++)
        {
            store.SeedProjected(i, T(i));
        }

        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        var snapshot = health.Snapshot();
        Assert.Equal(0, snapshot.ProjectionBacklog);
        Assert.Equal(1, snapshot.SpanProjectionBacklog);
        Assert.Equal(T(101), snapshot.OldestUnprocessedSpanReceivedAt);
    }

    [Fact]
    public async Task Pass_StatusReadBusy_MarksProjectionStatusUnknownSoReadinessIsNotReady()
    {
        var store = new FakeProjectionStore { StatusThrowsBusy = true };
        var health = new MonitorHealthState();
        health.SetLoopbackBound(true);
        health.MarkMigrationComplete();
        health.SetWriterRunning(true);
        health.SetProjectionWorkerRunning(true);
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        var readiness = health.Evaluate(ingestionStallThresholdSeconds: 10, projectionLagThresholdSeconds: 60);
        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("projection_status_unknown", readiness.DegradedReasons);
    }

    [Fact]
    public async Task StartStop_TogglesProjectionWorkerRunning()
    {
        var store = new FakeProjectionStore();
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health, pollInterval: TimeSpan.FromMilliseconds(20));

        await worker.StartAsync(CancellationToken.None);
        Assert.True(health.Snapshot().ProjectionWorkerRunning);

        await worker.StopAsync(CancellationToken.None);
        Assert.False(health.Snapshot().ProjectionWorkerRunning);
    }

    private static MonitorHealthState ReadyHealth()
    {
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        return health;
    }

    private static DateTimeOffset T(int minutes) => DateTimeOffset.UnixEpoch.AddMinutes(minutes);

    private static string OneSpanPayload(string operation, string? unknownValue = null) =>
        $$"""
        {
          "resourceSpans": [{
            "scopeSpans": [{
              "spans": [{
                "traceId": "t1",
                "spanId": "s1",
                "attributes": [
                  { "key": "gen_ai.operation.name", "value": { "stringValue": "{{operation}}" } }{{(unknownValue is null ? "" : $",\n                  {{ \"key\": \"future.unknown.key\", \"value\": {{ \"stringValue\": \"{unknownValue}\" }} }}")}}
                ]
              }]
            }]
          }]
        }
        """;

    private static string WrongRepresentationPayload(
        string traceId,
        string spanId,
        string parentSpanId,
        string? attribute = null) =>
        $$"""
        {
          "resourceSpans": [{
            "scopeSpans": [{
              "spans": [{
                "traceId": {{traceId}},
                "spanId": {{spanId}},
                "parentSpanId": {{parentSpanId}},
                "attributes": [{{attribute ?? ""}}]
              }]
            }]
          }]
        }
        """;

    private static string CopilotPayload() =>
        """
        {
          "resourceSpans": [{
            "resource": {"attributes": [
              {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
              {"key":"vcs.repository.name","value":{"stringValue":"copilot-agent-observability"}}
            ]},
            "scopeSpans": [{
              "spans": [{
                "traceId":"t1",
                "spanId":"span-1",
                "parentSpanId":"parent-1",
                "name":"chat gpt-4o",
                "startTimeUnixNano":"1000000000",
                "endTimeUnixNano":"1500000000",
                "attributes": [
                  {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                  {"key":"gen_ai.request.model","value":{"stringValue":"gpt-4o"}},
                  {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
                  {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
                ]
              }]
            }]
          }]
        }
        """;

    private static SourceCompatibilityRow Observation(long? rawRecordId, SourceCompatibilityState state)
    {
        var decision = state == SourceCompatibilityState.AdapterFailure
            ? SourceCompatibilityDecision.ForAdapterFailure(SourceCompatibilityReasonCodes.AdapterParseFailure)
            : SourceCompatibilityDecision.ForState(state);
        return new SourceCompatibilityRow(
            Id: 1,
            ObservationId: "observation-1",
            RawRecordId: rawRecordId,
            IngestBatchId: rawRecordId is null ? null : "batch-1",
            SourceSurface: rawRecordId is null ? null : "github-copilot",
            SourceApplicationVersion: null,
            SourceAdapter: rawRecordId is null ? null : "otlp",
            AdapterVersion: rawRecordId is null ? null : "1",
            SchemaFingerprint: rawRecordId is null ? null : $"sha256:{new string('1', 64)}",
            InventoryHash: rawRecordId is null ? null : $"sha256:{new string('2', 64)}",
            CompatibilityState: state,
            ReasonCodes: decision.ReasonCodes,
            NextAction: decision.NextAction,
            CaptureContentState: rawRecordId is null ? null : SourceCaptureContentState.NotCaptured,
            UnknownSpanCount: 0,
            UnknownEventCount: 0,
            UnknownAttributeCount: 0,
            OverflowDistinctCount: 0,
            OverflowOccurrenceCount: 0,
            ObservedAt: T(1),
            UnknownObservations: []);
    }

    private enum ApplyOutcome
    {
        Success,
        Busy,
        Fail,
    }

    private sealed class FakeProjectionStore : IMonitorProjectionStore
    {
        private readonly List<RawTelemetryRecord> records = new();
        private readonly HashSet<long> projected = new();
        private readonly HashSet<long> spanProjected = new();

        public Func<long, ApplyOutcome> OnApply { get; set; } = _ => ApplyOutcome.Success;

        public bool StatusThrowsBusy { get; set; }

        public Dictionary<long, int> ApplyCalls { get; } = new();

        public Dictionary<long, int> SpanApplyCalls { get; } = new();

        public Dictionary<long, MonitorRecordProjection> AppliedProjections { get; } = new();

        public Dictionary<long, IReadOnlyList<MonitorSpanProjection>> AppliedSpanProjections { get; } = new();

        public IReadOnlyCollection<long> AllIds => records.Select(r => r.Id!.Value).ToList();

        public void Seed(long id, DateTimeOffset receivedAt, string payloadJson = """{"resourceSpans":[]}""") =>
            records.Add(new RawTelemetryRecord(
                Id: id,
                Source: "raw-otlp",
                TraceId: $"t{id}",
                ReceivedAt: receivedAt,
                ResourceAttributesJson: null,
                PayloadJson: payloadJson));

        public void SeedProjected(
            long id,
            DateTimeOffset receivedAt,
            string payloadJson = """{"resourceSpans":[]}""")
        {
            Seed(id, receivedAt, payloadJson);
            projected.Add(id);
        }

        public bool IsProjected(long id) => projected.Contains(id);

        public bool IsSpanProjected(long id) => spanProjected.Contains(id);

        public string GetPayload(long id) => records.Single(record => record.Id == id).PayloadJson;

        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit) =>
            records.Where(r => !projected.Contains(r.Id!.Value)).Take(limit).ToList();

        public bool ApplyProjection(
            long rawRecordId,
            string source,
            DateTimeOffset receivedAt,
            MonitorRecordProjection projection,
            DateTimeOffset projectedAt)
        {
            ApplyCalls[rawRecordId] = ApplyCalls.GetValueOrDefault(rawRecordId) + 1;
            switch (OnApply(rawRecordId))
            {
                case ApplyOutcome.Busy:
                    throw new PersistenceBusyException();
                case ApplyOutcome.Fail:
                    throw new InvalidOperationException("projection boom");
                default:
                    AppliedProjections[rawRecordId] = projection;
                    projected.Add(rawRecordId);
                    return true;
            }
        }

        public MonitorProjectionStatus GetProjectionStatus()
        {
            if (StatusThrowsBusy)
            {
                throw new PersistenceBusyException();
            }

            var unprocessed = records.Where(r => !projected.Contains(r.Id!.Value)).ToList();
            var oldest = unprocessed.Count == 0 ? (DateTimeOffset?)null : unprocessed.Min(r => r.ReceivedAt);
            return new MonitorProjectionStatus(unprocessed.Count, oldest);
        }

        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(int limit) =>
            records.Where(r => projected.Contains(r.Id!.Value) && !spanProjected.Contains(r.Id!.Value)).Take(limit).ToList();

        public bool ApplySpanProjection(long rawRecordId, IReadOnlyList<MonitorSpanProjection> spans, DateTimeOffset projectedAt)
        {
            SpanApplyCalls[rawRecordId] = SpanApplyCalls.GetValueOrDefault(rawRecordId) + 1;
            if (!projected.Contains(rawRecordId) || spanProjected.Contains(rawRecordId))
            {
                return false;
            }

            AppliedSpanProjections[rawRecordId] = spans;
            spanProjected.Add(rawRecordId);
            return true;
        }

        public MonitorProjectionStatus GetSpanProjectionStatus()
        {
            var pending = records.Where(r => projected.Contains(r.Id!.Value) && !spanProjected.Contains(r.Id!.Value)).ToList();
            var oldest = pending.Count == 0 ? (DateTimeOffset?)null : pending.Min(r => r.ReceivedAt);
            return new MonitorProjectionStatus(pending.Count, oldest);
        }

        public MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) =>
            throw new NotSupportedException();

        public MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) =>
            throw new NotSupportedException();

        public MonitorTraceRow? GetMonitorTrace(string traceId) =>
            throw new NotSupportedException();

        public MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) =>
            throw new NotSupportedException();

        public RawTelemetryRecord? GetRawRecordById(long id) =>
            throw new NotSupportedException();

        public IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(string traceId, int limit) =>
            throw new NotSupportedException();

        public MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) =>
            throw new NotSupportedException();

        public MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) =>
            throw new NotSupportedException();

        public MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) =>
            throw new NotSupportedException();

        public IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCompatibilityStore : ISourceCompatibilityStore
    {
        private readonly Dictionary<long, SourceCompatibilityRow> observationsByRawRecordId = new();
        private readonly List<SourceCompatibilityRow> observations = new();

        public List<long> LookupRawRecordIds { get; } = new();

        public int CreateSchemaCallCount { get; private set; }

        public List<SourceAdapterFailureDraft> RecordAdapterFailureCalls { get; } = [];

        public void Seed(SourceCompatibilityRow observation)
        {
            observations.Add(observation);
            if (observation.RawRecordId is { } rawRecordId)
            {
                observationsByRawRecordId.Add(rawRecordId, observation);
            }
        }

        public void CreateSchema() => CreateSchemaCallCount++;

        public long RecordAdapterFailure(SourceAdapterFailureDraft failure)
        {
            RecordAdapterFailureCalls.Add(failure);
            return 73;
        }

        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) => observations;

        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId)
        {
            LookupRawRecordIds.Add(rawRecordId);
            return observationsByRawRecordId.GetValueOrDefault(rawRecordId);
        }
    }
}

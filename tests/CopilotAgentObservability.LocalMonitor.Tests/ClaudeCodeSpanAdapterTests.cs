using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ClaudeCodeSpanAdapterTests
{
    [Fact]
    public void Build_ContentDisabledFixture_ProjectsOnlyApprovedNormalizedFields()
    {
        var spans = BuildFixture("content-disabled.json");

        Assert.Equal(6, spans.Count);
        Assert.Equal(
            [
                ("1111111111111111", (string?)null),
                ("2222222222222222", "1111111111111111"),
                ("3333333333333333", "1111111111111111"),
                ("4444444444444444", "3333333333333333"),
                ("5555555555555555", "3333333333333333"),
                ("6666666666666666", "1111111111111111"),
            ],
            spans.Select(span => (span.SpanId, span.ParentSpanId)));

        foreach (var span in spans)
        {
            Assert.Equal("11111111111111111111111111111111", span.TraceId);
            Assert.Null(span.ToolType);
            Assert.Null(span.McpToolName);
            Assert.Null(span.McpServerHash);
            Assert.Null(span.AgentName);
            Assert.Null(span.ResponseModel);
            Assert.Null(span.TotalTokens);
            Assert.Null(span.ReasoningTokens);
            Assert.Null(span.ErrorType);
            Assert.Null(span.FinishReasons);
            Assert.Null(span.ConversationId);
            Assert.Equal("ok", span.Status);
        }

        Assert.Equal(
            [null, "chat", "execute_tool", null, null, null],
            spans.Select(span => span.Operation));
        Assert.Equal(
            ["unknown", "llm_call", "tool_call", "unknown", "unknown", "hook"],
            spans.Select(span => span.Category));
        Assert.Equal(Enumerable.Range(0, 6), spans.Select(span => span.SpanOrdinal));

        Assert.Null(spans[0].RequestModel);
        Assert.Null(spans[0].ToolName);
        Assert.Equal(600d, spans[0].DurationMs);
        Assert.Equal("1970-01-01T00:00:01.0000000+00:00", spans[0].StartTime);
        Assert.Equal("1970-01-01T00:00:01.6000000+00:00", spans[0].EndTime);

        Assert.Equal("SYNTHETIC_MODEL", spans[1].RequestModel);
        Assert.Equal(12, spans[1].InputTokens);
        Assert.Equal(7, spans[1].OutputTokens);
        Assert.Equal(3, spans[1].CacheReadTokens);
        Assert.Equal(2, spans[1].CacheCreationTokens);
        Assert.Equal(250d, spans[1].DurationMs);

        Assert.Equal("SYNTHETIC_TOOL", spans[2].ToolName);
        Assert.Null(spans[2].RequestModel);
        Assert.Null(spans[2].InputTokens);
        Assert.Null(spans[2].OutputTokens);
        Assert.Null(spans[2].CacheReadTokens);
        Assert.Null(spans[2].CacheCreationTokens);

        Assert.Null(spans[3].ToolName);
        Assert.Equal(40d, spans[3].DurationMs);
        Assert.Null(spans[4].ToolName);
        Assert.Equal(120d, spans[4].DurationMs);
        Assert.Null(spans[5].ToolName);
    }

    [Fact]
    public void Build_ContentGateChangesOnlyRawPayloadNotSanitizedProjection()
    {
        var disabled = BuildFixture("content-disabled.json");
        var enabled = BuildFixture("content-enabled.json");

        Assert.Equal(JsonSerializer.Serialize(disabled), JsonSerializer.Serialize(enabled));

        var serialized = JsonSerializer.Serialize(enabled);
        foreach (var rawOnlyMarker in new[]
        {
            "SYNTHETIC_USER_PROMPT",
            "SYNTHETIC_MODEL_OUTPUT",
            "SYNTHETIC_RELATIVE_PATH",
            "SYNTHETIC_SUBAGENT_TYPE",
            "SYNTHETIC_TOOL_OUTPUT",
            "SYNTHETIC_HOOK_DEFINITIONS",
        })
        {
            Assert.DoesNotContain(rawOnlyMarker, serialized, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("content-disabled")]
    [InlineData("content-enabled")]
    public void Build_Task12JsonAndProtobufFixturesProduceIdenticalCanonicalProjectionBytes(string fixtureName)
    {
        var jsonProjection = BuildFixture($"{fixtureName}.json");
        var protobufPayload = OtlpProtobufTraceConverter.ConvertTraceRequestToRawOtlpJson(
            File.ReadAllBytes(FixturePath($"{fixtureName}.bin")));
        var protobufProjection = MonitorSpanProjectionBuilder.Build(Record(protobufPayload));

        AssertProducerProjectionContract(jsonProjection);
        AssertProducerProjectionContract(protobufProjection);

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(jsonProjection, CanonicalProjectionJsonOptions);
        var protobufBytes = JsonSerializer.SerializeToUtf8Bytes(protobufProjection, CanonicalProjectionJsonOptions);
        Assert.Equal(jsonBytes, protobufBytes);

        using var canonical = JsonDocument.Parse(protobufBytes);
        var expectedPropertyOrder = new[]
        {
            "trace_id", "span_id", "parent_span_id", "span_ordinal", "operation", "category",
            "tool_name", "tool_type", "mcp_tool_name", "mcp_server_hash", "agent_name",
            "request_model", "response_model", "input_tokens", "output_tokens", "total_tokens",
            "reasoning_tokens", "cache_read_tokens", "cache_creation_tokens", "status", "error_type",
            "finish_reasons", "conversation_id", "duration_ms", "start_time", "end_time",
        };
        Assert.All(
            canonical.RootElement.EnumerateArray(),
            span => Assert.Equal(expectedPropertyOrder, span.EnumerateObject().Select(property => property.Name)));
    }

    [Theory]
    [InlineData("0", "false", "ok", "unknown")]
    [InlineData("1", "false", "ok", "unknown")]
    [InlineData("2", "true", "error", "error")]
    [InlineData(null, "false", null, "unknown")]
    [InlineData("9", "true", null, "unknown")]
    public void Build_StatusCodeIsSoleAuthorityAndToolSuccessNeverFillsIt(
        string? statusCode,
        string success,
        string? expectedStatus,
        string expectedCategory)
    {
        var status = statusCode is null ? string.Empty : $",\"status\":{{\"code\":{statusCode}}}";
        var payload =
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{" +
            "\"traceId\":\"AaBb\",\"spanId\":\"CcDd\",\"parentSpanId\":\"EeFf\"," +
            "\"name\":\"claude_code.tool.execution\"" + status + "," +
            "\"attributes\":[{\"key\":\"success\",\"value\":{\"boolValue\":" + success + "}}]" +
            "}]}]}]}";

        var span = Assert.Single(MonitorSpanProjectionBuilder.Build(Record(payload)));

        Assert.Equal("AaBb", span.TraceId);
        Assert.Equal("CcDd", span.SpanId);
        Assert.Equal("EeFf", span.ParentSpanId);
        Assert.Equal(expectedStatus, span.Status);
        Assert.Equal(expectedCategory, span.Category);
    }

    [Fact]
    public void Build_DocumentedUnmappedAndRawOnlyFieldsNeverEnterSanitizedProjection()
    {
        var payload = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[
          {
            "traceId":"trace","spanId":"llm","name":"claude_code.llm_request",
            "status":{"code":2},
            "attributes":[
              {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
              {"key":"gen_ai.agent.name","value":{"stringValue":"LEAK_AGENT_NAME"}},
              {"key":"gen_ai.response.model","value":{"stringValue":"LEAK_RESPONSE_MODEL"}},
              {"key":"gen_ai.usage.total_tokens","value":{"intValue":"99"}},
              {"key":"gen_ai.usage.reasoning_tokens","value":{"intValue":"11"}},
              {"key":"reasoning_tokens","value":{"intValue":"12"}},
              {"key":"ttft_ms","value":{"doubleValue":13.5}},
              {"key":"attempt","value":{"intValue":"2"}},
              {"key":"agent_id","value":{"stringValue":"LEAK_AGENT_ID"}},
              {"key":"parent_agent_id","value":{"stringValue":"LEAK_PARENT_AGENT_ID"}},
              {"key":"response.model_output","value":{"stringValue":"LEAK_MODEL_OUTPUT"}},
              {"key":"error.type","value":{"stringValue":"LEAK_GENERIC_ERROR"}}
            ],
            "events":[{"name":"gen_ai.request.attempt","attributes":[
              {"key":"attempt","value":{"intValue":"3"}}
            ]}]
          },
          {
            "traceId":"trace","spanId":"permission","parentSpanId":"llm",
            "name":"claude_code.tool.blocked_on_user",
            "attributes":[
              {"key":"duration_ms","value":{"doubleValue":40}},
              {"key":"decision","value":{"stringValue":"accept"}}
            ]
          },
          {
            "traceId":"trace","spanId":"tool","parentSpanId":"llm",
            "name":"claude_code.tool",
            "attributes":[
              {"key":"tool_name","value":{"stringValue":"safe_tool"}},
              {"key":"tool_use_id","value":{"stringValue":"LEAK_TOOL_USE_ID"}},
              {"key":"file_path","value":{"stringValue":"LEAK_FILE_PATH"}},
              {"key":"subagent_type","value":{"stringValue":"LEAK_SUBAGENT_TYPE"}}
            ],
            "events":[{"name":"tool.output","attributes":[
              {"key":"content","value":{"stringValue":"LEAK_TOOL_OUTPUT"}}
            ]}]
          },
          {
            "traceId":"trace","spanId":"execution","parentSpanId":"tool",
            "name":"claude_code.tool.execution","status":{"code":2},
            "attributes":[
              {"key":"success","value":{"boolValue":false}},
              {"key":"error","value":{"stringValue":"LEAK_ERROR_CATEGORY_OR_DETAIL"}}
            ]
          }
        ]}]}]}
        """;

        var spans = MonitorSpanProjectionBuilder.Build(Record(payload));

        Assert.Equal(4, spans.Count);
        Assert.Equal("chat", spans[0].Operation);
        Assert.Equal("error", spans[0].Category);
        Assert.Null(spans[0].AgentName);
        Assert.Null(spans[0].ResponseModel);
        Assert.Null(spans[0].TotalTokens);
        Assert.Null(spans[0].ReasoningTokens);
        Assert.Null(spans[0].ErrorType);
        Assert.Null(spans[1].ToolName);
        Assert.Null(spans[1].DurationMs);
        Assert.Equal("safe_tool", spans[2].ToolName);
        Assert.Null(spans[2].ToolType);
        Assert.Equal("error", spans[3].Status);
        Assert.Null(spans[3].ErrorType);

        var serialized = JsonSerializer.Serialize(spans);
        foreach (var marker in new[]
        {
            "LEAK_AGENT_NAME",
            "LEAK_RESPONSE_MODEL",
            "LEAK_AGENT_ID",
            "LEAK_PARENT_AGENT_ID",
            "LEAK_MODEL_OUTPUT",
            "LEAK_GENERIC_ERROR",
            "LEAK_TOOL_USE_ID",
            "LEAK_FILE_PATH",
            "LEAK_SUBAGENT_TYPE",
            "LEAK_TOOL_OUTPUT",
            "LEAK_ERROR_CATEGORY_OR_DETAIL",
        })
        {
            Assert.DoesNotContain(marker, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Build_MissingNormalizedFieldsRemainNullWithoutZeroFill()
    {
        const string payload = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[
          {"name":"claude_code.llm_request"}
        ]}]}]}
        """;

        var span = Assert.Single(MonitorSpanProjectionBuilder.Build(Record(payload)));

        Assert.Null(span.TraceId);
        Assert.Null(span.SpanId);
        Assert.Null(span.ParentSpanId);
        Assert.Equal(0, span.SpanOrdinal);
        Assert.Null(span.RequestModel);
        Assert.Null(span.InputTokens);
        Assert.Null(span.OutputTokens);
        Assert.Null(span.TotalTokens);
        Assert.Null(span.ReasoningTokens);
        Assert.Null(span.CacheReadTokens);
        Assert.Null(span.CacheCreationTokens);
        Assert.Null(span.Status);
        Assert.Null(span.DurationMs);
        Assert.Null(span.StartTime);
        Assert.Null(span.EndTime);
    }

    [Theory]
    [InlineData("read_file", "read_file")]
    [InlineData("user@example.invalid", null)]
    [InlineData("C:\\\\sensitive\\\\path", null)]
    public void Build_ToolNameUsesExistingBoundedSanitizer(string sourceName, string? expectedName)
    {
        var payload =
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{" +
            "\"name\":\"claude_code.tool\"," +
            "\"attributes\":[{\"key\":\"tool_name\",\"value\":{\"stringValue\":" +
            JsonSerializer.Serialize(sourceName) + "}}]}]}]}]}";

        var span = Assert.Single(MonitorSpanProjectionBuilder.Build(Record(payload)));

        Assert.Equal(expectedName, span.ToolName);
    }

    [Fact]
    public void Build_NonExactClaudeSpanNameRetainsExistingProjectionPath()
    {
        const string payload = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"copilot-trace","spanId":"copilot-span",
          "name":"claude_code.llm_request.future",
          "attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
            {"key":"gen_ai.request.model","value":{"stringValue":"copilot-model"}},
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"4"}},
            {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
          ]
        }]}]}]}
        """;

        var span = Assert.Single(MonitorSpanProjectionBuilder.Build(Record(payload)));

        Assert.Equal("chat", span.Operation);
        Assert.Equal("llm_call", span.Category);
        Assert.Equal("copilot-model", span.RequestModel);
        Assert.Equal(4, span.InputTokens);
        Assert.Equal(5, span.OutputTokens);
        Assert.Equal(9, span.TotalTokens);
        Assert.Equal("ok", span.Status);
    }

    [Fact]
    public void Build_ObservedToolFailureShapeClassifiesAndRollsUpWithoutDoubleCounting()
    {
        var spans = MonitorSpanProjectionBuilder.Build(Record(ObservedToolFailurePayload));
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(6, spans.Count);
        Assert.Equal(
            ["unknown", "llm_call", "tool_call", "unknown", "error", "llm_call"],
            spans.Select(span => span.Category));
        Assert.Equal(2, rollup.TurnCount);
        Assert.Equal(0, rollup.AgentInvocationCount);
        Assert.Equal(20, rollup.InputTokens);
        Assert.Equal(6, rollup.OutputTokens);
        Assert.Equal(MonitorTraceStatus.Recovered, rollup.TraceStatus);

        var measurement = Assert.Single(RawMeasurementNormalizer.Normalize(ObservedToolFailurePayload));
        Assert.Equal(2, measurement.TurnCount);
        Assert.Equal(1, measurement.ToolCallCount);
        Assert.Equal(1, measurement.ErrorCount);
    }

    [Fact]
    public void Build_ObservedSubAgentDelegationShapeClassifiesAndRollsUpWithoutDoubleCounting()
    {
        var spans = MonitorSpanProjectionBuilder.Build(Record(ObservedSubAgentPayload));
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(11, spans.Count);
        Assert.Equal(4, spans.Count(span => span.Category == "llm_call"));
        Assert.Equal(2, spans.Count(span => span.Category == "tool_call"));
        Assert.Equal(5, spans.Count(span => span.Category == "unknown"));
        Assert.Equal(4, rollup.TurnCount);
        Assert.Equal(0, rollup.AgentInvocationCount);
        Assert.Equal(40, rollup.InputTokens);
        Assert.Equal(MonitorTraceStatus.Ok, rollup.TraceStatus);

        var measurement = Assert.Single(RawMeasurementNormalizer.Normalize(ObservedSubAgentPayload));
        Assert.Equal(4, measurement.TurnCount);
        Assert.Equal(2, measurement.ToolCallCount);
        Assert.Equal(0, measurement.ErrorCount);
    }

    private static string ClaudeSpan(
        string name,
        string spanId,
        string? parentSpanId,
        long startNano,
        long endNano,
        string? statusCode = "0",
        string extraAttributes = "")
    {
        var parent = parentSpanId is null ? string.Empty : $"\"parentSpanId\":\"{parentSpanId}\",";
        var status = statusCode is null ? string.Empty : $",\"status\":{{\"code\":{statusCode}}}";
        return
            $"{{\"traceId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"spanId\":\"{spanId}\",{parent}" +
            $"\"name\":\"{name}\",\"startTimeUnixNano\":\"{startNano}\",\"endTimeUnixNano\":\"{endNano}\"," +
            $"\"attributes\":[{extraAttributes}]{status}}}";
    }

    private const string LlmTokens =
        "{\"key\":\"input_tokens\",\"value\":{\"intValue\":\"10\"}}," +
        "{\"key\":\"output_tokens\",\"value\":{\"intValue\":\"3\"}}";

    private const string ToolNameAttribute =
        "{\"key\":\"tool_name\",\"value\":{\"stringValue\":\"SYNTHETIC_TOOL\"}}";

    // Mirrors the 2026-07-13 observed print-mode tool-failure trace:
    // interaction root, llm x2, tool with blocked_on_user + failing execution.
    private static readonly string ObservedToolFailurePayload =
        "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[" +
        ClaudeSpan("claude_code.interaction", "a000000000000001", null, 1_000_000_000, 9_000_000_000) + "," +
        ClaudeSpan("claude_code.llm_request", "a000000000000002", "a000000000000001", 1_100_000_000, 2_000_000_000, extraAttributes: LlmTokens) + "," +
        ClaudeSpan("claude_code.tool", "a000000000000003", "a000000000000001", 2_100_000_000, 4_000_000_000, extraAttributes: ToolNameAttribute) + "," +
        ClaudeSpan("claude_code.tool.blocked_on_user", "a000000000000004", "a000000000000003", 2_200_000_000, 2_400_000_000) + "," +
        ClaudeSpan("claude_code.tool.execution", "a000000000000005", "a000000000000003", 2_500_000_000, 3_900_000_000, statusCode: "2") + "," +
        ClaudeSpan("claude_code.llm_request", "a000000000000006", "a000000000000001", 4_100_000_000, 8_900_000_000, extraAttributes: LlmTokens) +
        "]}]}]}";

    // Mirrors the 2026-07-13 observed print-mode sub-agent delegation trace:
    // the Task tool's claude_code.tool.execution parents the sub-agent's own
    // llm_request and tool spans (11 spans total).
    private static readonly string ObservedSubAgentPayload =
        "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[" +
        ClaudeSpan("claude_code.interaction", "b000000000000001", null, 1_000_000_000, 20_000_000_000) + "," +
        ClaudeSpan("claude_code.llm_request", "b000000000000002", "b000000000000001", 1_100_000_000, 2_000_000_000, extraAttributes: LlmTokens) + "," +
        ClaudeSpan("claude_code.tool", "b000000000000003", "b000000000000001", 2_100_000_000, 15_000_000_000, extraAttributes: ToolNameAttribute) + "," +
        ClaudeSpan("claude_code.tool.blocked_on_user", "b000000000000004", "b000000000000003", 2_200_000_000, 2_400_000_000) + "," +
        ClaudeSpan("claude_code.tool.execution", "b000000000000005", "b000000000000003", 2_500_000_000, 14_900_000_000) + "," +
        ClaudeSpan("claude_code.llm_request", "b000000000000006", "b000000000000005", 3_000_000_000, 4_000_000_000, extraAttributes: LlmTokens) + "," +
        ClaudeSpan("claude_code.tool", "b000000000000007", "b000000000000005", 4_100_000_000, 6_000_000_000, extraAttributes: ToolNameAttribute) + "," +
        ClaudeSpan("claude_code.tool.blocked_on_user", "b000000000000008", "b000000000000007", 4_200_000_000, 4_400_000_000) + "," +
        ClaudeSpan("claude_code.tool.execution", "b000000000000009", "b000000000000007", 4_500_000_000, 5_900_000_000) + "," +
        ClaudeSpan("claude_code.llm_request", "b00000000000000a", "b000000000000005", 6_100_000_000, 7_000_000_000, extraAttributes: LlmTokens) + "," +
        ClaudeSpan("claude_code.llm_request", "b00000000000000b", "b000000000000001", 15_100_000_000, 19_900_000_000, extraAttributes: LlmTokens) +
        "]}]}]}";

    private static IReadOnlyList<MonitorSpanProjection> BuildFixture(string fileName) =>
        MonitorSpanProjectionBuilder.Build(Record(File.ReadAllText(FixturePath(fileName))));

    private static void AssertProducerProjectionContract(IReadOnlyList<MonitorSpanProjection> spans)
    {
        Assert.Equal(6, spans.Count);
        Assert.Equal(
            [
                ("1111111111111111", (string?)null),
                ("2222222222222222", "1111111111111111"),
                ("3333333333333333", "1111111111111111"),
                ("4444444444444444", "3333333333333333"),
                ("5555555555555555", "3333333333333333"),
                ("6666666666666666", "1111111111111111"),
            ],
            spans.Select(span => (span.SpanId, span.ParentSpanId)));
        Assert.Equal(
            [null, "chat", "execute_tool", null, null, null],
            spans.Select(span => span.Operation));
        Assert.Equal(
            ["unknown", "llm_call", "tool_call", "unknown", "unknown", "hook"],
            spans.Select(span => span.Category));
        Assert.All(spans, span =>
        {
            Assert.Equal("11111111111111111111111111111111", span.TraceId);
            Assert.Null(span.ToolType);
            Assert.Null(span.McpToolName);
            Assert.Null(span.McpServerHash);
            Assert.Null(span.AgentName);
            Assert.Null(span.ResponseModel);
            Assert.Null(span.TotalTokens);
            Assert.Null(span.ReasoningTokens);
            Assert.Equal("ok", span.Status);
            Assert.Null(span.ErrorType);
            Assert.Null(span.FinishReasons);
            Assert.Null(span.ConversationId);
        });
        Assert.Equal(Enumerable.Range(0, 6), spans.Select(span => span.SpanOrdinal));
        Assert.Equal(
            [
                ("1970-01-01T00:00:01.0000000+00:00", "1970-01-01T00:00:01.6000000+00:00", 600d),
                ("1970-01-01T00:00:01.0500000+00:00", "1970-01-01T00:00:01.3000000+00:00", 250d),
                ("1970-01-01T00:00:01.3100000+00:00", "1970-01-01T00:00:01.5000000+00:00", 190d),
                ("1970-01-01T00:00:01.3200000+00:00", "1970-01-01T00:00:01.3600000+00:00", 40d),
                ("1970-01-01T00:00:01.3700000+00:00", "1970-01-01T00:00:01.4900000+00:00", 120d),
                ("1970-01-01T00:00:01.5100000+00:00", "1970-01-01T00:00:01.5500000+00:00", 40d),
            ],
            spans.Select(span => (span.StartTime, span.EndTime, span.DurationMs)));

        Assert.Equal("SYNTHETIC_MODEL", spans[1].RequestModel);
        Assert.Equal(12, spans[1].InputTokens);
        Assert.Equal(7, spans[1].OutputTokens);
        Assert.Equal(3, spans[1].CacheReadTokens);
        Assert.Equal(2, spans[1].CacheCreationTokens);
        Assert.Equal("SYNTHETIC_TOOL", spans[2].ToolName);
        Assert.All(spans.Where((_, index) => index != 1), span =>
        {
            Assert.Null(span.RequestModel);
            Assert.Null(span.InputTokens);
            Assert.Null(span.OutputTokens);
            Assert.Null(span.CacheReadTokens);
            Assert.Null(span.CacheCreationTokens);
        });
        Assert.All(spans.Where((_, index) => index != 2), span => Assert.Null(span.ToolName));

        var serialized = JsonSerializer.Serialize(spans, CanonicalProjectionJsonOptions);
        foreach (var rawOnlyMarker in new[]
        {
            "SYNTHETIC_USER_PROMPT",
            "SYNTHETIC_MODEL_OUTPUT",
            "SYNTHETIC_RELATIVE_PATH",
            "SYNTHETIC_SUBAGENT_TYPE",
            "SYNTHETIC_TOOL_OUTPUT",
            "SYNTHETIC_HOOK_DEFINITIONS",
        })
        {
            Assert.DoesNotContain(rawOnlyMarker, serialized, StringComparison.Ordinal);
        }
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Claude", "otel", fileName);

    private static RawTelemetryRecord Record(string payloadJson) =>
        new(
            Id: 1,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: null,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);

    private static JsonSerializerOptions CanonicalProjectionJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

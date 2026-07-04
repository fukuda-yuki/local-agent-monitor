using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorSpanProjectionBuilderTests
{
    // --- Basic projection ---

    [Fact]
    public void Build_ChatSpan_ExtractsAllFields()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"aaaa","spanId":"1111","parentSpanId":"0000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "status":{"code":"1"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.request.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o-2024-05-13"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"500"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.usage.reasoning.output_tokens","value":{"intValue":"50"}},
             {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"30"}},
             {"key":"gen_ai.response.finish_reasons","value":{"stringValue":"[\"stop\"]"}},
             {"key":"gen_ai.conversation.id","value":{"stringValue":"conv-1"}}
           ]}
        ]}]}]}
        """;
        var record = Record("aaaa", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("aaaa", span.TraceId);
        Assert.Equal("1111", span.SpanId);
        Assert.Equal("0000", span.ParentSpanId);
        Assert.Equal(0, span.SpanOrdinal);
        Assert.Equal("chat", span.Operation);
        Assert.Equal("llm_call", span.Category);
        Assert.Equal("gpt-4o", span.RequestModel);
        Assert.Equal("gpt-4o-2024-05-13", span.ResponseModel);
        Assert.Equal(500, span.InputTokens);
        Assert.Equal(200, span.OutputTokens);
        Assert.Equal(700, span.TotalTokens);
        Assert.Equal(50, span.ReasoningTokens);
        Assert.Equal(100, span.CacheReadTokens);
        Assert.Equal(30, span.CacheCreationTokens);
        Assert.Equal("ok", span.Status);
        Assert.Null(span.ErrorType);
        Assert.Equal("stop", span.FinishReasons);
        Assert.Equal("conv-1", span.ConversationId);
        Assert.NotNull(span.StartTime);
        Assert.NotNull(span.EndTime);
        Assert.True(span.DurationMs > 0);
    }

    [Fact]
    public void Build_ToolSpan_ExtractsToolFields()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"bbbb","spanId":"2222","name":"execute_tool",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000000500000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"function"}}
           ]}
        ]}]}]}
        """;
        var record = Record("bbbb", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("execute_tool", span.Operation);
        Assert.Equal("tool_call", span.Category);
        Assert.Equal("read_file", span.ToolName);
        Assert.Equal("function", span.ToolType);
    }

    [Fact]
    public void Build_InvokeAgentSpan_ExtractsAgentFields()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"cccc","spanId":"3333","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000005000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"copilot"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"500"}}
           ]}
        ]}]}]}
        """;
        var record = Record("cccc", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("invoke_agent", span.Operation);
        Assert.Equal("agent_invocation", span.Category);
        Assert.Equal("copilot", span.AgentName);
        Assert.Equal(1000, span.InputTokens);
        Assert.Equal(500, span.OutputTokens);
    }

    [Fact]
    public void Build_HookSpan_ClassifiedCorrectly()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"dddd","spanId":"4444","name":"execute_hook post-commit",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000000100000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_hook"}}
           ]}
        ]}]}]}
        """;
        var record = Record("dddd", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("execute_hook", span.Operation);
        Assert.Equal("hook", span.Category);
    }

    [Fact]
    public void Build_UnknownSpanName_ProducesUnknownCategory()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"eeee","spanId":"5555","name":"totally.new.span.kind",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000000100000000"}
        ]}]}]}
        """;
        var record = Record("eeee", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.Operation);
        Assert.Equal("unknown", span.Category);
    }

    // --- MCP tool attributes ---

    [Fact]
    public void Build_McpTool_ExtractsMcpFields()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"ffff","spanId":"6666","name":"execute_tool",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000000500000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"mcp_read_resource"}},
             {"key":"github.copilot.tool.parameters.tool_type","value":{"stringValue":"extension"}},
             {"key":"github.copilot.tool.parameters.mcp_tool_name","value":{"stringValue":"read_resource"}},
             {"key":"github.copilot.tool.parameters.mcp_server_name_hash","value":{"stringValue":"a1b2c3d4e5f6"}}
           ]}
        ]}]}]}
        """;
        var record = Record("ffff", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("mcp_read_resource", span.ToolName);
        Assert.Equal("extension", span.ToolType);
        Assert.Equal("read_resource", span.McpToolName);
        Assert.Equal("a1b2c3d4e5f6", span.McpServerHash);
    }

    // --- Error classification ---

    [Fact]
    public void Build_ErrorSpan_ExtractsErrorTypeAsClassToken()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"1111","spanId":"7777","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"error.type","value":{"stringValue":"timeout"}}
           ]}
        ]}]}]}
        """;
        var record = Record("1111", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("error", span.Status);
        Assert.Equal("error", span.Category);
        Assert.Equal("timeout", span.ErrorType);
    }

    // --- Sub-agent hierarchy ---

    [Fact]
    public void Build_SubAgentHierarchy_PreservesParentSpanIds()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"aaaa","spanId":"1000","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}}]},
          {"traceId":"aaaa","spanId":"2000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"aaaa","spanId":"3000","parentSpanId":"1000","name":"execute_tool",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]},
          {"traceId":"aaaa","spanId":"4000","parentSpanId":"3000","name":"invoke_agent sub",
           "startTimeUnixNano":"1710000002500000000","endTimeUnixNano":"1710000002900000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}}]},
          {"traceId":"aaaa","spanId":"5000","parentSpanId":"4000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002600000000","endTimeUnixNano":"1710000002800000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]}
        ]}]}]}
        """;
        var record = Record("aaaa", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        Assert.Equal(5, spans.Count);
        Assert.Null(spans[0].ParentSpanId);
        Assert.Equal("1000", spans[1].ParentSpanId);
        Assert.Equal("1000", spans[2].ParentSpanId);
        Assert.Equal("3000", spans[3].ParentSpanId);
        Assert.Equal("4000", spans[4].ParentSpanId);
        Assert.Equal(0, spans[0].SpanOrdinal);
        Assert.Equal(4, spans[4].SpanOrdinal);
    }

    // --- parent_span_id-absent fallback ---

    [Fact]
    public void Build_MissingParentSpanId_ProducesFlatList()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"bbbb","spanId":"1111","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"bbbb","spanId":"2222","name":"execute_tool",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]}
        ]}]}]}
        """;
        var record = Record("bbbb", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        Assert.Equal(2, spans.Count);
        Assert.Null(spans[0].ParentSpanId);
        Assert.Null(spans[1].ParentSpanId);
    }

    // --- Multi-trace fan-out ---

    [Fact]
    public void Build_MultiTracePayload_ProjectsAllSpans()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"trace-1","spanId":"1111","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]},
          {"traceId":"trace-2","spanId":"2222","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]}
        ]}]}]}
        """;
        var record = Record("trace-1", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        Assert.Equal(2, spans.Count);
        Assert.Equal("trace-1", spans[0].TraceId);
        Assert.Equal("trace-2", spans[1].TraceId);
    }

    // --- Per-attribute negative tests (acceptance criteria) ---

    [Fact]
    public void Build_EmailInToolName_GuardedOut()
    {
        var payload = SpanWithNameField("gen_ai.tool.name", "user@example.com");
        var record = Record("neg-1", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.ToolName);
        Assert.Equal("execute_tool", span.Operation);
    }

    [Fact]
    public void Build_PathInToolName_GuardedOut()
    {
        var payload = SpanWithNameField("gen_ai.tool.name", @"C:\\Users\\test\\file.txt");
        var record = Record("neg-2", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.ToolName);
    }

    [Fact]
    public void Build_SecretInToolName_GuardedOut()
    {
        var payload = SpanWithNameField("gen_ai.tool.name", "my_secret_function");
        var record = Record("neg-3", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.ToolName);
    }

    [Fact]
    public void Build_EmailInMcpToolName_GuardedOut()
    {
        var payload = SpanWithNameField("github.copilot.tool.parameters.mcp_tool_name", "admin@corp.internal");
        var record = Record("neg-4", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.McpToolName);
    }

    [Fact]
    public void Build_PathInAgentName_GuardedOut()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"neg-5","spanId":"1111","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"/home/user/.config/agent"}}
           ]}
        ]}]}]}
        """;
        var record = Record("neg-5", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.AgentName);
        Assert.Equal("invoke_agent", span.Operation);
    }

    [Fact]
    public void Build_SecretInAgentName_GuardedOut()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"neg-6","spanId":"1111","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"agent_with_secret_key"}}
           ]}
        ]}]}]}
        """;
        var record = Record("neg-6", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.AgentName);
    }

    [Fact]
    public void Build_EmailInErrorType_GuardedOut()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"neg-7","spanId":"1111","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"error.type","value":{"stringValue":"admin@corp.internal"}}
           ]}
        ]}]}]}
        """;
        var record = Record("neg-7", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.ErrorType);
        Assert.Equal("error", span.Status);
    }

    [Fact]
    public void Build_ClassTokenErrorType_Passes()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"neg-8","spanId":"1111","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"error.type","value":{"stringValue":"ECONNREFUSED"}}
           ]}
        ]}]}]}
        """;
        var record = Record("neg-8", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("ECONNREFUSED", span.ErrorType);
    }

    [Fact]
    public void Build_SecretNamedErrorClassToken_Passes()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"err-token","spanId":"1111","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"error.type","value":{"stringValue":"TokenExpiredError"}}
           ]}
        ]}]}]}
        """;
        var record = Record("err-token", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("TokenExpiredError", span.ErrorType);
    }

    [Fact]
    public void Build_UnsafeFinishReason_GuardedOut()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"neg-9","spanId":"1111","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.finish_reasons","value":{"stringValue":"[\"stop\",\"/home/user/leak\"]"}}
           ]}
        ]}]}]}
        """;
        var record = Record("neg-9", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Equal("stop", span.FinishReasons);
    }

    [Fact]
    public void Build_MalformedFinishReasons_DropsRawText()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"finish-bad","spanId":"1111","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.finish_reasons","value":{"stringValue":"[not json]"}}
           ]}
        ]}]}]}
        """;
        var record = Record("finish-bad", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.Null(span.FinishReasons);
    }

    [Fact]
    public void Build_MaxLengthTruncation_TruncatesLongName()
    {
        var longName = new string('x', 300);
        var payload = SpanWithNameField("gen_ai.tool.name", longName);
        var record = Record("trunc-1", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);

        var span = Assert.Single(spans);
        Assert.NotNull(span.ToolName);
        Assert.Equal(256, span.ToolName!.Length);
    }

    [Fact]
    public void Build_SerializedProjectionDoesNotContainInjectedMarkers()
    {
        const string emailMarker = "leak@example.com";
        const string secretMarker = "my_secret_tool";

        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"marker-1","spanId":"1111","name":"execute_tool",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"leak@example.com"}},
             {"key":"github.copilot.tool.parameters.mcp_tool_name","value":{"stringValue":"/home/user/leak"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"my_secret_tool"}}
           ]}
        ]}]}]}
        """;
        var record = Record("marker-1", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var serialized = JsonSerializer.Serialize(spans);

        Assert.DoesNotContain(emailMarker, serialized);
        Assert.DoesNotContain("/home/user/leak", serialized);
        Assert.DoesNotContain(secretMarker, serialized);
    }

    // --- Token rollup (no double count) ---

    [Fact]
    public void Rollup_InvokeAgentWithTokens_UsesInvokeAgentTokens()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-1","spanId":"1000","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"500"}}
           ]},
          {"traceId":"roll-1","spanId":"2000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"400"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}}
           ]},
          {"traceId":"roll-1","spanId":"3000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000003000000000","endTimeUnixNano":"1710000004000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"600"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"300"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}}
           ]}
        ]}]}]}
        """;
        var record = Record("roll-1", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(1000, rollup.InputTokens);
        Assert.Equal(500, rollup.OutputTokens);
        Assert.Equal(1500, rollup.TotalTokens);
        Assert.Equal(2, rollup.TurnCount);
        Assert.Equal(1, rollup.AgentInvocationCount);
        Assert.Equal("gpt-4o", rollup.PrimaryModel);
    }

    [Fact]
    public void Rollup_ChatOnlyNoInvokeAgent_SumsChatTokens()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-2","spanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"300"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}}
           ]},
          {"traceId":"roll-2","spanId":"2000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"150"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}}
           ]}
        ]}]}]}
        """;
        var record = Record("roll-2", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(500, rollup.InputTokens);
        Assert.Equal(250, rollup.OutputTokens);
        Assert.Equal(750, rollup.TotalTokens);
        Assert.Equal(2, rollup.TurnCount);
        Assert.Equal(0, rollup.AgentInvocationCount);
    }

    [Fact]
    public void Rollup_SubAgentTokens_AttributedToSubAgent()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-3","spanId":"1000","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"2000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"1000"}}
           ]},
          {"traceId":"roll-3","spanId":"2000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"800"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"400"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}}
           ]},
          {"traceId":"roll-3","spanId":"3000","parentSpanId":"1000","name":"execute_tool",
           "startTimeUnixNano":"1710000003000000000","endTimeUnixNano":"1710000004000000000",
           "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}}]},
          {"traceId":"roll-3","spanId":"4000","parentSpanId":"3000","name":"invoke_agent sub",
           "startTimeUnixNano":"1710000003100000000","endTimeUnixNano":"1710000003900000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"500"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}}
           ]},
          {"traceId":"roll-3","spanId":"5000","parentSpanId":"4000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000003200000000","endTimeUnixNano":"1710000003800000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"500"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}}
           ]}
        ]}]}]}
        """;
        var record = Record("roll-3", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        // Root invoke_agent carries 2000/1000 (includes sub-agent).
        // Rollup should use root invoke_agent tokens, not re-sum chat spans.
        Assert.Equal(2000, rollup.InputTokens);
        Assert.Equal(1000, rollup.OutputTokens);
        Assert.Equal(3000, rollup.TotalTokens);
    }

    [Fact]
    public void Rollup_ChildInvokeAgentBeforeRoot_UsesRootAgentTokens()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-child-first","spanId":"child","parentSpanId":"root","name":"invoke_agent sub",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
           ]},
          {"traceId":"roll-child-first","spanId":"root","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}}
           ]}
        ]}]}]}
        """;
        var record = Record("roll-child-first", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(100, rollup.InputTokens);
        Assert.Equal(50, rollup.OutputTokens);
        Assert.Equal(150, rollup.TotalTokens);
    }

    [Fact]
    public void Rollup_MultipleRootInvokeAgents_SumsRootAgentTokens()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-multi-root","spanId":"root-1","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}}
           ]},
          {"traceId":"roll-multi-root","spanId":"root-2","name":"invoke_agent",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"75"}}
           ]}
        ]}]}]}
        """;
        var record = Record("roll-multi-root", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(300, rollup.InputTokens);
        Assert.Equal(125, rollup.OutputTokens);
        Assert.Equal(425, rollup.TotalTokens);
        Assert.Equal(2, rollup.AgentInvocationCount);
    }

    [Fact]
    public void Rollup_MultipleRootInvokeAgentsWithPartialTotals_DerivesMissingRootTotals()
    {
        var spans = new[]
        {
            Projection("root-1", parentSpanId: null, inputTokens: 100, outputTokens: 50, totalTokens: 150),
            Projection("root-2", parentSpanId: null, inputTokens: 200, outputTokens: 75, totalTokens: null),
        };

        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(300, rollup.InputTokens);
        Assert.Equal(125, rollup.OutputTokens);
        Assert.Equal(425, rollup.TotalTokens);
    }

    [Fact]
    public void Rollup_RootInvokeAgentWithoutUsage_IgnoresChildAgentUsageAndFallsBackToChat()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-root-no-usage","spanId":"root","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000004000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}}
           ]},
          {"traceId":"roll-root-no-usage","spanId":"child-agent","parentSpanId":"root","name":"invoke_agent sub",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
           ]},
          {"traceId":"roll-root-no-usage","spanId":"chat","parentSpanId":"root","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"300"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"100"}}
           ]}
        ]}]}]}
        """;
        var record = Record("roll-root-no-usage", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Equal(300, rollup.InputTokens);
        Assert.Equal(100, rollup.OutputTokens);
        Assert.Equal(400, rollup.TotalTokens);
        Assert.Equal(2, rollup.AgentInvocationCount);
    }

    [Fact]
    public void Rollup_ChatFallbackTokenOverflow_DropsOverflowedField()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-overflow","spanId":"chat-1","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"2000000000"}}
           ]},
          {"traceId":"roll-overflow","spanId":"chat-2","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"2000000000"}}
           ]}
        ]}]}]}
        """;
        var record = Record("roll-overflow", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Null(rollup.InputTokens);
        Assert.Null(rollup.OutputTokens);
        Assert.Null(rollup.TotalTokens);
    }

    [Fact]
    public void Rollup_InvokeAgentWithOnlyTotalTokens_UsesAgentTotal()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"roll-total-only","spanId":"root","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.total_tokens","value":{"intValue":"900"}}
           ]},
          {"traceId":"roll-total-only","spanId":"chat","parentSpanId":"root","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}}
           ]}
        ]}]}]}
        """;
        var record = Record("roll-total-only", payload);
        var spans = MonitorSpanProjectionBuilder.Build(record);
        var rollup = MonitorTraceRollupBuilder.ComputeRollup(spans);

        Assert.Null(rollup.InputTokens);
        Assert.Null(rollup.OutputTokens);
        Assert.Equal(900, rollup.TotalTokens);
    }

    // --- Normalizer over-count fix ---

    [Fact]
    public void Normalizer_InvokeAgentAndChat_NoDoubleCount()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"norm-1","spanId":"1000","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"500"}}
           ]},
          {"traceId":"norm-1","spanId":"2000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"400"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"200"}}
           ]},
          {"traceId":"norm-1","spanId":"3000","parentSpanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000003000000000","endTimeUnixNano":"1710000004000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"600"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"300"}}
           ]}
        ]}]}]}
        """;
        var rows = RawMeasurementNormalizer.Normalize(payload);

        var row = Assert.Single(rows);
        // Should use invoke_agent tokens (1000/500), not sum all (2000/1000)
        Assert.Equal(1000, row.InputTokens);
        Assert.Equal(500, row.OutputTokens);
        Assert.Equal(1500, row.TotalTokens);
    }

    [Fact]
    public void Normalizer_ChatOnlyNoInvokeAgent_SumsChatTokens()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"norm-2","spanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"300"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"100"}}
           ]},
          {"traceId":"norm-2","spanId":"2000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"150"}}
           ]}
        ]}]}]}
        """;
        var rows = RawMeasurementNormalizer.Normalize(payload);

        var row = Assert.Single(rows);
        Assert.Equal(500, row.InputTokens);
        Assert.Equal(250, row.OutputTokens);
        Assert.Equal(750, row.TotalTokens);
    }

    [Fact]
    public void Normalizer_ChildInvokeAgentBeforeRoot_UsesRootAgentTokens()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"norm-child-first","spanId":"child","parentSpanId":"root","name":"invoke_agent sub",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
           ]},
          {"traceId":"norm-child-first","spanId":"root","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}}
           ]}
        ]}]}]}
        """;

        var row = Assert.Single(RawMeasurementNormalizer.Normalize(payload));
        Assert.Equal(100, row.InputTokens);
        Assert.Equal(50, row.OutputTokens);
        Assert.Equal(150, row.TotalTokens);
    }

    [Fact]
    public void Normalizer_MultipleRootInvokeAgents_SumsRootAgentTokens()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"norm-multi-root","spanId":"root-1","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}}
           ]},
          {"traceId":"norm-multi-root","spanId":"root-2","name":"invoke_agent",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"200"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"75"}}
           ]}
        ]}]}]}
        """;

        var row = Assert.Single(RawMeasurementNormalizer.Normalize(payload));
        Assert.Equal(300, row.InputTokens);
        Assert.Equal(125, row.OutputTokens);
        Assert.Equal(425, row.TotalTokens);
    }

    [Fact]
    public void Normalizer_RootInvokeAgentWithoutUsage_IgnoresChildAgentUsageAndFallsBackToChat()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"norm-root-no-usage","spanId":"root","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000004000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}}
           ]},
          {"traceId":"norm-root-no-usage","spanId":"child-agent","parentSpanId":"root","name":"invoke_agent sub",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
           ]},
          {"traceId":"norm-root-no-usage","spanId":"chat","parentSpanId":"root","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"300"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"100"}}
           ]}
        ]}]}]}
        """;

        var row = Assert.Single(RawMeasurementNormalizer.Normalize(payload));
        Assert.Equal(300, row.InputTokens);
        Assert.Equal(100, row.OutputTokens);
        Assert.Equal(400, row.TotalTokens);
    }

    [Fact]
    public void Normalizer_ChatFallbackTokenOverflow_DropsOverflowedField()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"norm-overflow","spanId":"chat-1","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"2000000000"}}
           ]},
          {"traceId":"norm-overflow","spanId":"chat-2","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000002000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"2000000000"}}
           ]}
        ]}]}]}
        """;

        var row = Assert.Single(RawMeasurementNormalizer.Normalize(payload));
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.TotalTokens);
    }

    [Fact]
    public void Normalizer_InvokeAgentWithOnlyTotalTokens_UsesAgentTotal()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"norm-total-only","spanId":"root","name":"invoke_agent",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000003000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.usage.total_tokens","value":{"intValue":"900"}}
           ]},
          {"traceId":"norm-total-only","spanId":"chat","parentSpanId":"root","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000001000000000","endTimeUnixNano":"1710000002000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}}
           ]}
        ]}]}]}
        """;

        var row = Assert.Single(RawMeasurementNormalizer.Normalize(payload));
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Equal(900, row.TotalTokens);
    }

    // --- Path detection in IsUnsafeStringValue ---

    [Theory]
    [InlineData(@"C:\Users\test\file.txt")]
    [InlineData(@"D:\projects\src")]
    [InlineData(@"\\server\share\path")]
    [InlineData("../../../etc/passwd")]
    [InlineData(@"..\..\..\windows\system32")]
    [InlineData("/home/user/.ssh/id_rsa")]
    [InlineData("/usr/local/bin/tool")]
    [InlineData("/var/log/syslog")]
    [InlineData("/tmp/scratch")]
    [InlineData("/etc/passwd")]
    [InlineData("/opt/tools/binary")]
    public void IsUnsafeStringValue_DetectsFilePaths(string value)
    {
        Assert.True(MeasurementSanitizer.IsUnsafeStringValue(value));
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("copilot")]
    [InlineData("gpt-4o")]
    [InlineData("my_tool_v2")]
    [InlineData("timeout")]
    [InlineData("ECONNREFUSED")]
    public void IsUnsafeStringValue_AllowsLegitimateNames(string value)
    {
        Assert.False(MeasurementSanitizer.IsUnsafeStringValue(value));
    }

    // --- Helpers ---

    private static RawTelemetryRecord Record(string? traceId, string payloadJson) =>
        new(
            Id: 1,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);

    private static MonitorSpanProjection Projection(
        string spanId,
        string? parentSpanId,
        int? inputTokens,
        int? outputTokens,
        int? totalTokens) =>
        new(
            TraceId: "roll-direct",
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            SpanOrdinal: 0,
            Operation: "invoke_agent",
            Category: "agent_invocation",
            ToolName: null,
            ToolType: null,
            McpToolName: null,
            McpServerHash: null,
            AgentName: null,
            RequestModel: null,
            ResponseModel: null,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            ReasoningTokens: null,
            CacheReadTokens: null,
            CacheCreationTokens: null,
            Status: "ok",
            ErrorType: null,
            FinishReasons: null,
            ConversationId: null,
            DurationMs: null,
            StartTime: null,
            EndTime: null);

    private static string SpanWithNameField(string attributeKey, string attributeValue) =>
        "{\"resourceSpans\":[{\"resource\":{\"attributes\":[]},\"scopeSpans\":[{\"spans\":[" +
        "{\"traceId\":\"neg\",\"spanId\":\"1111\",\"name\":\"execute_tool\"," +
        "\"startTimeUnixNano\":\"1710000000000000000\",\"endTimeUnixNano\":\"1710000001000000000\"," +
        "\"attributes\":[" +
        "{\"key\":\"gen_ai.operation.name\",\"value\":{\"stringValue\":\"execute_tool\"}}," +
        "{\"key\":\"" + attributeKey + "\",\"value\":{\"stringValue\":\"" + attributeValue + "\"}}" +
        "]}]}]}]}";
}

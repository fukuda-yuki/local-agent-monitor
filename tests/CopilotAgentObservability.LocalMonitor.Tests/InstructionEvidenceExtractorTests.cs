using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class InstructionEvidenceExtractorTests
{
    private const string TraceId = "trace-1";

    [Fact]
    public void Extract_ErrorSpans_ListsErrorStatusSpansInOrdinalOrder()
    {
        var evidence = Extract(
            Span(0, operation: "chat", category: "llm_call"),
            Span(1, toolName: "shell", status: "error", errorType: "timeout"),
            Span(2, toolName: "shell"),
            Span(3, toolName: "web", status: "error", errorType: null));

        Assert.Equal(new[] { "span-1", "span-3" }, evidence.ErrorSpans.Select(e => e.SpanId).ToArray());
        Assert.Equal("timeout", evidence.ErrorSpans[0].ErrorKind);
        Assert.Equal("unknown", evidence.ErrorSpans[1].ErrorKind);
        Assert.Contains("shell", evidence.ErrorSpans[0].Descriptor);
        Assert.Contains("timeout", evidence.ErrorSpans[0].Descriptor);
        // Descriptor is built from allowlist columns only — never payload text.
        Assert.DoesNotContain("payload", evidence.ErrorSpans[0].Descriptor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_RetryChains_FailureThenSameToolRetry_Recovered()
    {
        var evidence = Extract(
            Span(0, operation: "chat", category: "llm_call"),
            Span(1, toolName: "shell", status: "error", errorType: "timeout"),
            Span(2, toolName: "shell", status: "ok"));

        var chain = Assert.Single(evidence.RetryChains);
        Assert.Equal("shell", chain.ToolName);
        Assert.Equal(new[] { "span-1", "span-2" }, chain.SpanIds.ToArray());
        Assert.Equal("recovered", chain.FinalOutcome);
    }

    [Fact]
    public void Extract_RetryChains_FailureWithoutRecovery_Unrecovered()
    {
        var evidence = Extract(
            Span(0, toolName: "shell", status: "error", errorType: "timeout"),
            Span(1, toolName: "shell", status: "error", errorType: "timeout"));

        var chain = Assert.Single(evidence.RetryChains);
        Assert.Equal(new[] { "span-0", "span-1" }, chain.SpanIds.ToArray());
        Assert.Equal("unrecovered", chain.FinalOutcome);
    }

    [Fact]
    public void Extract_RetryChains_SingleFailureNoRetry_NotEmitted()
    {
        var evidence = Extract(
            Span(0, toolName: "shell", status: "error", errorType: "timeout"),
            Span(1, toolName: "web", status: "ok"));

        Assert.Empty(evidence.RetryChains);
        Assert.Single(evidence.ErrorSpans);
    }

    [Fact]
    public void Extract_TurnTokens_UsesChatAndLlmCallSpansWithNullTokensAsZero()
    {
        var evidence = Extract(
            Span(0, operation: "chat", category: "llm_call", inputTokens: 100, outputTokens: 50),
            Span(1, toolName: "shell"),
            Span(2, operation: null, category: "llm_call", inputTokens: null, outputTokens: null));

        Assert.Equal(2, evidence.TurnTokens.Count);
        Assert.Equal(1, evidence.TurnTokens[0].TurnIndex);
        Assert.Equal("span-0", evidence.TurnTokens[0].SpanId);
        Assert.Equal(100, evidence.TurnTokens[0].InputTokens);
        Assert.Equal(50, evidence.TurnTokens[0].OutputTokens);
        Assert.Equal(2, evidence.TurnTokens[1].TurnIndex);
        Assert.Equal(0, evidence.TurnTokens[1].InputTokens);
        Assert.Equal(0, evidence.TurnTokens[1].OutputTokens);
    }

    [Fact]
    public void Extract_EmptyTrace_ReturnsEmptyCollectionsAndNulls()
    {
        var evidence = InstructionEvidenceExtractor.Extract(TraceId, [], [], []);

        Assert.Empty(evidence.ErrorSpans);
        Assert.Empty(evidence.RetryChains);
        Assert.Empty(evidence.TurnTokens);
        Assert.Null(evidence.UserInstruction);
        Assert.Null(evidence.Conversation);
    }

    [Fact]
    public void Extract_IsDeterministic_SameInputTwiceGivesEqualSerializedOutput()
    {
        static MonitorSpanRow[] Spans() =>
        [
            Span(0, operation: "chat", category: "llm_call", inputTokens: 100, outputTokens: 50),
            Span(1, toolName: "shell", status: "error", errorType: "timeout"),
            Span(2, toolName: "shell", status: "ok"),
            Span(3, toolName: "web", status: "error", errorType: null),
        ];

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var first = JsonSerializer.Serialize(InstructionEvidenceExtractor.Extract(TraceId, Spans(), [], []), options);
        var second = JsonSerializer.Serialize(InstructionEvidenceExtractor.Extract(TraceId, Spans(), [], []), options);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Extract_UserInstruction_ResolvesFirstChatSpanAndDescriptor()
    {
        var longPrompt = new string('a', 200);
        var spans = new[] { Span(0, operation: "chat", category: "llm_call", rawRecordId: 7) };
        var records = new[] { PromptRecord(7, "span-0", longPrompt) };

        var evidence = InstructionEvidenceExtractor.Extract(TraceId, spans, records, []);

        Assert.NotNull(evidence.UserInstruction);
        Assert.Equal("span-0", evidence.UserInstruction!.SpanId);
        Assert.Equal(7, evidence.UserInstruction.RawRecordId);
        Assert.Equal(new string('a', 160) + "...", evidence.UserInstruction.Descriptor);
    }

    [Fact]
    public void Extract_UserInstruction_TakesFirstLineOnly()
    {
        var spans = new[] { Span(0, operation: "chat", rawRecordId: 7) };
        // JSON-escaped newline: descriptor is the first line only.
        var records = new[] { PromptRecord(7, "span-0", "first line\\nsecond line") };

        var evidence = InstructionEvidenceExtractor.Extract(TraceId, spans, records, []);

        Assert.Equal("first line", evidence.UserInstruction!.Descriptor);
    }

    [Fact]
    public void Extract_UserInstruction_NoChatSpanOrNoPrompt_ReturnsNull()
    {
        // No chat span at all.
        var noChat = InstructionEvidenceExtractor.Extract(TraceId, [Span(0, toolName: "shell")], [], []);
        Assert.Null(noChat.UserInstruction);

        // Chat span, but its raw record carries no gen_ai.prompt attribute.
        var noPromptRecord = Record(7, """{"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"trace-1","spanId":"span-0","attributes":[]}]}]}]}""");
        var noPrompt = InstructionEvidenceExtractor.Extract(
            TraceId, [Span(0, operation: "chat", rawRecordId: 7)], [noPromptRecord], []);
        Assert.Null(noPrompt.UserInstruction);

        // Chat span, but no raw record joins on its RawRecordId.
        var missingRecord = InstructionEvidenceExtractor.Extract(
            TraceId, [Span(0, operation: "chat", rawRecordId: 7)], [], []);
        Assert.Null(missingRecord.UserInstruction);

        // Malformed payload JSON must not throw.
        var malformed = InstructionEvidenceExtractor.Extract(
            TraceId, [Span(0, operation: "chat", rawRecordId: 7)], [Record(7, "{ not json")], []);
        Assert.Null(malformed.UserInstruction);
    }

    [Fact]
    public void Extract_Conversation_OrdersSiblingsAndLocatesAnalyzedTrace()
    {
        var siblings = new[]
        {
            new MonitorConversationTraceRow("trace-0", "2026-07-01T00:01:00.000+00:00"),
            new MonitorConversationTraceRow(TraceId, "2026-07-01T00:02:00.000+00:00"),
            new MonitorConversationTraceRow("trace-2", "2026-07-01T00:03:00.000+00:00"),
        };

        var evidence = InstructionEvidenceExtractor.Extract(
            TraceId, [Span(0, operation: "chat", conversationId: "conv-1")], [], siblings);

        Assert.NotNull(evidence.Conversation);
        Assert.Equal("conv-1", evidence.Conversation!.ConversationId);
        Assert.Equal(new[] { "trace-0", "trace-1", "trace-2" }, evidence.Conversation.TraceIds.ToArray());
        Assert.Equal(3, evidence.Conversation.TraceCount);
        Assert.Equal(2, evidence.Conversation.AnalyzedTraceIndex);
    }

    [Fact]
    public void Extract_Conversation_MissingConversationId_ReturnsNull()
    {
        var evidence = InstructionEvidenceExtractor.Extract(
            TraceId, [Span(0, operation: "chat", conversationId: null)], [], []);

        Assert.Null(evidence.Conversation);
    }

    [Fact]
    public void Extract_SingleSpanTrace_ProducesConsistentOutput()
    {
        var spans = new[] { Span(0, operation: "chat", category: "llm_call", conversationId: "conv-1", rawRecordId: 7) };
        var records = new[] { PromptRecord(7, "span-0", "hello world") };
        var siblings = new[] { new MonitorConversationTraceRow(TraceId, "2026-07-01T00:01:00.000+00:00") };

        var evidence = InstructionEvidenceExtractor.Extract(TraceId, spans, records, siblings);

        Assert.Empty(evidence.ErrorSpans);
        Assert.Empty(evidence.RetryChains);
        Assert.Single(evidence.TurnTokens);
        Assert.Equal("hello world", evidence.UserInstruction!.Descriptor);
        Assert.Equal(1, evidence.Conversation!.AnalyzedTraceIndex);
    }

    private static InstructionEvidence Extract(params MonitorSpanRow[] spans) =>
        InstructionEvidenceExtractor.Extract(TraceId, spans, [], []);

    private static RawTelemetryRecord Record(long id, string payloadJson) =>
        new(
            Id: id,
            Source: "raw-otlp",
            TraceId: TraceId,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);

    /// <summary>A raw OTLP record whose single chat span carries a gen_ai.prompt string value.</summary>
    private static RawTelemetryRecord PromptRecord(long id, string spanId, string promptTextJson) =>
        Record(id, PromptPayloadTemplate
            .Replace("TRACE_ID_PLACEHOLDER", TraceId)
            .Replace("SPAN_ID_PLACEHOLDER", spanId)
            .Replace("PROMPT_TEXT_PLACEHOLDER", promptTextJson));

    private const string PromptPayloadTemplate = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
          {"traceId":"TRACE_ID_PLACEHOLDER","spanId":"SPAN_ID_PLACEHOLDER","name":"chat gpt-4o",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"PROMPT_TEXT_PLACEHOLDER"}}
           ]}
        ]}]}]}
        """;

    /// <summary>Synthetic span-row factory: defaults for every allowlist column, override per test.</summary>
    private static MonitorSpanRow Span(
        int ordinal,
        string? operation = "execute_tool",
        string? category = "tool_call",
        string? toolName = null,
        string? status = "ok",
        string? errorType = null,
        int? inputTokens = null,
        int? outputTokens = null,
        string? conversationId = null,
        long rawRecordId = 1) =>
        new(
            Id: ordinal + 1,
            RawRecordId: rawRecordId,
            TraceId: TraceId,
            SpanId: $"span-{ordinal}",
            ParentSpanId: null,
            SpanOrdinal: ordinal,
            Operation: operation,
            Category: category,
            ToolName: toolName,
            ToolType: null,
            McpToolName: null,
            McpServerHash: null,
            AgentName: null,
            RequestModel: null,
            ResponseModel: null,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: null,
            ReasoningTokens: null,
            CacheReadTokens: null,
            CacheCreationTokens: null,
            Status: status,
            ErrorType: errorType,
            FinishReasons: null,
            ConversationId: conversationId,
            DurationMs: null,
            StartTime: null,
            EndTime: null,
            ProjectedAt: "2026-07-01T00:00:00.000+00:00");
}

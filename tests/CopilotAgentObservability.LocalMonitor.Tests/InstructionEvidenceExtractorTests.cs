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

    private static InstructionEvidence Extract(params MonitorSpanRow[] spans) =>
        InstructionEvidenceExtractor.Extract(TraceId, spans, [], []);

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

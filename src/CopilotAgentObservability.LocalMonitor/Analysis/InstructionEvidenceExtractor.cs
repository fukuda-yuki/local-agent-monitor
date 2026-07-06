using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

/// <summary>
/// Deterministic instruction-evidence pre-extraction for the
/// instruction-diagnosis focus (Sprint20, D047). Pure function over the
/// projection rows / raw records already loaded for an analysis run plus
/// sibling-trace metadata: no I/O, no clock, no randomness — the same inputs
/// produce byte-identical serialized output. The output carries no long raw
/// bodies; the only raw-derived content is the capped user-instruction
/// descriptor, which stays inside the raw analysis surface
/// (removed by <c>--sanitized-only</c>). Contract:
/// docs/specifications/interfaces/instruction-diagnosis-analysis.md.
/// </summary>
internal static class InstructionEvidenceExtractor
{
    private const string ErrorStatus = "error";
    private const string UnknownErrorKind = "unknown";

    public static InstructionEvidence Extract(
        string traceId,
        IReadOnlyList<MonitorSpanRow> spans,
        IReadOnlyList<RawTelemetryRecord> rawRecords,
        IReadOnlyList<MonitorConversationTraceRow> conversationTraces)
    {
        // Store read order (raw_record_id, span_ordinal) — deterministic even
        // when a trace spans multiple raw records with colliding ordinals.
        var ordered = spans
            .OrderBy(span => span.RawRecordId)
            .ThenBy(span => span.SpanOrdinal)
            .ToList();

        return new InstructionEvidence(
            ErrorSpans: ExtractErrorSpans(ordered),
            RetryChains: ExtractRetryChains(ordered),
            TurnTokens: ExtractTurnTokens(ordered),
            UserInstruction: null,
            Conversation: null);
    }

    private static IReadOnlyList<InstructionEvidenceErrorSpan> ExtractErrorSpans(
        IReadOnlyList<MonitorSpanRow> ordered) =>
        ordered
            .Where(IsError)
            .Select(span => new InstructionEvidenceErrorSpan(
                SpanId: span.SpanId,
                ToolName: span.ToolName,
                ErrorKind: span.ErrorType ?? UnknownErrorKind,
                Descriptor: BuildErrorDescriptor(span)))
            .ToList();

    private static IReadOnlyList<InstructionEvidenceRetryChain> ExtractRetryChains(
        IReadOnlyList<MonitorSpanRow> ordered)
    {
        var chains = new List<(int FirstIndex, InstructionEvidenceRetryChain Chain)>();
        var toolSpans = ordered
            .Select((span, index) => (span, index))
            .Where(entry => entry.span.ToolName is not null)
            .GroupBy(entry => entry.span.ToolName);

        foreach (var group in toolSpans)
        {
            List<(MonitorSpanRow Span, int Index)>? open = null;
            foreach (var (span, index) in group)
            {
                if (open is null)
                {
                    if (IsError(span))
                    {
                        open = [(span, index)];
                    }

                    continue;
                }

                open.Add((span, index));
                if (!IsError(span))
                {
                    AddChain(chains, group.Key!, open, "recovered");
                    open = null;
                }
            }

            if (open is not null)
            {
                AddChain(chains, group.Key!, open, "unrecovered");
            }
        }

        return chains
            .OrderBy(entry => entry.FirstIndex)
            .Select(entry => entry.Chain)
            .ToList();
    }

    private static void AddChain(
        List<(int FirstIndex, InstructionEvidenceRetryChain Chain)> chains,
        string toolName,
        List<(MonitorSpanRow Span, int Index)> members,
        string finalOutcome)
    {
        if (members.Count < 2)
        {
            return;
        }

        chains.Add((
            members[0].Index,
            new InstructionEvidenceRetryChain(
                ToolName: toolName,
                SpanIds: members.Select(member => member.Span.SpanId).ToList(),
                FinalOutcome: finalOutcome)));
    }

    private static IReadOnlyList<InstructionEvidenceTurnTokens> ExtractTurnTokens(
        IReadOnlyList<MonitorSpanRow> ordered) =>
        ordered
            .Where(IsTurn)
            .Select((span, index) => new InstructionEvidenceTurnTokens(
                TurnIndex: index + 1,
                SpanId: span.SpanId,
                InputTokens: span.InputTokens ?? 0,
                OutputTokens: span.OutputTokens ?? 0))
            .ToList();

    private static bool IsError(MonitorSpanRow span) =>
        string.Equals(span.Status, ErrorStatus, StringComparison.Ordinal);

    // Same turn filter as the existing cache summary in the analysis runner.
    private static bool IsTurn(MonitorSpanRow span) =>
        span.Operation == "chat" || span.Category == "llm_call";

    // Allowlist columns only (operation / tool / error kind) — never payload text.
    private static string BuildErrorDescriptor(MonitorSpanRow span)
    {
        var subject = string.Join(' ', new[] { span.Operation, span.ToolName }
            .Where(part => !string.IsNullOrEmpty(part)));
        if (subject.Length == 0)
        {
            subject = span.Category ?? "span";
        }

        return $"{subject} failed ({span.ErrorType ?? UnknownErrorKind})";
    }
}

/// <summary>
/// Structured evidence for one analyzed trace (D047). Serialized as the
/// <c>get_instruction_evidence</c> tool result.
/// </summary>
internal sealed record InstructionEvidence(
    IReadOnlyList<InstructionEvidenceErrorSpan> ErrorSpans,
    IReadOnlyList<InstructionEvidenceRetryChain> RetryChains,
    IReadOnlyList<InstructionEvidenceTurnTokens> TurnTokens,
    InstructionEvidenceUserInstruction? UserInstruction,
    InstructionEvidenceConversation? Conversation);

/// <summary>An error-status span, in span-ordinal order.</summary>
internal sealed record InstructionEvidenceErrorSpan(
    string? SpanId,
    string? ToolName,
    string ErrorKind,
    string Descriptor);

/// <summary>
/// Same-tool failed-then-retried span chain (length >= 2), ordered by first
/// span ordinal. <see cref="FinalOutcome"/> is <c>recovered</c> or <c>unrecovered</c>.
/// </summary>
internal sealed record InstructionEvidenceRetryChain(
    string? ToolName,
    IReadOnlyList<string?> SpanIds,
    string FinalOutcome);

/// <summary>Per chat-turn token distribution (1-based turn index; null tokens read as 0).</summary>
internal sealed record InstructionEvidenceTurnTokens(
    int TurnIndex,
    string? SpanId,
    int InputTokens,
    int OutputTokens);

/// <summary>
/// The first chat-operation span plus a capped descriptor of the user
/// instruction (first line, 160 chars max) — the only raw-derived field.
/// </summary>
internal sealed record InstructionEvidenceUserInstruction(
    string? SpanId,
    long RawRecordId,
    string Descriptor);

/// <summary>Sibling-trace metadata for the analyzed trace's conversation id. Metadata only, no bodies.</summary>
internal sealed record InstructionEvidenceConversation(
    string ConversationId,
    IReadOnlyList<string> TraceIds,
    int TraceCount,
    int AnalyzedTraceIndex);

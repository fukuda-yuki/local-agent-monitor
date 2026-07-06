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
    private const string ChatOperation = "chat";
    private const string PromptAttributeKey = "gen_ai.prompt";
    private const int UserInstructionMaxChars = 160;
    private const string TruncationMarker = "...";

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
            UserInstruction: ExtractUserInstruction(ordered, rawRecords),
            Conversation: ExtractConversation(traceId, ordered, conversationTraces));
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

    private static InstructionEvidenceUserInstruction? ExtractUserInstruction(
        IReadOnlyList<MonitorSpanRow> ordered,
        IReadOnlyList<RawTelemetryRecord> rawRecords)
    {
        var chatSpan = ordered.FirstOrDefault(span => span.Operation == ChatOperation);
        if (chatSpan is null)
        {
            return null;
        }

        var record = rawRecords.FirstOrDefault(raw => raw.Id == chatSpan.RawRecordId);
        if (record is null)
        {
            return null;
        }

        var descriptor = BuildInstructionDescriptor(record.PayloadJson, chatSpan.TraceId, chatSpan.SpanId);
        if (descriptor is null)
        {
            return null;
        }

        return new InstructionEvidenceUserInstruction(
            SpanId: chatSpan.SpanId,
            RawRecordId: chatSpan.RawRecordId,
            Descriptor: descriptor);
    }

    private static string? BuildInstructionDescriptor(string? payloadJson, string traceId, string? spanId)
    {
        var prompt = ReadPromptText(payloadJson, traceId, spanId);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var firstLine = prompt.Split('\n', 2)[0].Trim();
        if (firstLine.Length == 0)
        {
            return null;
        }

        return firstLine.Length <= UserInstructionMaxChars
            ? firstLine
            : firstLine[..UserInstructionMaxChars] + TruncationMarker;
    }

    private static InstructionEvidenceConversation? ExtractConversation(
        string traceId,
        IReadOnlyList<MonitorSpanRow> ordered,
        IReadOnlyList<MonitorConversationTraceRow> conversationTraces)
    {
        var conversationId = ordered
            .Select(span => span.ConversationId)
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));
        if (string.IsNullOrEmpty(conversationId))
        {
            return null;
        }

        var traceIds = conversationTraces.Select(trace => trace.TraceId).ToList();

        return new InstructionEvidenceConversation(
            ConversationId: conversationId,
            TraceIds: traceIds,
            TraceCount: traceIds.Count,
            AnalyzedTraceIndex: traceIds.IndexOf(traceId) + 1);
    }

    // Span-targeted gen_ai.prompt lookup over the raw OTLP payload, modeled on
    // SpanDetailExtractor: pure, exception-safe. When the span row carries a span
    // id, only that span node matches (trace-guarded); otherwise the first
    // prompt-bearing span for the trace is used.
    private static string? ReadPromptText(string? payloadJson, string traceId, string? spanId)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            foreach (var span in EnumerateSpans(document.RootElement))
            {
                if (!MatchesSpan(span, traceId, spanId))
                {
                    continue;
                }

                var prompt = ReadPromptAttribute(span);
                if (prompt is not null)
                {
                    return prompt;
                }

                // A specific span id targets exactly one node; do not scan others.
                if (spanId is not null)
                {
                    return null;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> EnumerateSpans(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            if (resourceSpan.ValueKind != JsonValueKind.Object
                || !resourceSpan.TryGetProperty("scopeSpans", out var scopeSpans)
                || scopeSpans.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var scopeSpan in scopeSpans.EnumerateArray())
            {
                if (scopeSpan.ValueKind != JsonValueKind.Object
                    || !scopeSpan.TryGetProperty("spans", out var spans)
                    || spans.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var span in spans.EnumerateArray())
                {
                    if (span.ValueKind == JsonValueKind.Object)
                    {
                        yield return span;
                    }
                }
            }
        }
    }

    private static bool MatchesSpan(JsonElement span, string traceId, string? spanId)
    {
        // A payload can hold spans from multiple traces; a span that names a
        // different trace never matches. Spans without a trace id still match.
        if (span.TryGetProperty("traceId", out var traceIdElement)
            && traceIdElement.ValueKind == JsonValueKind.String
            && !string.Equals(traceIdElement.GetString(), traceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (spanId is null)
        {
            return true;
        }

        return span.TryGetProperty("spanId", out var spanIdElement)
            && spanIdElement.ValueKind == JsonValueKind.String
            && string.Equals(spanIdElement.GetString(), spanId, StringComparison.Ordinal);
    }

    private static string? ReadPromptAttribute(JsonElement span)
    {
        if (!span.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (attribute.ValueKind == JsonValueKind.Object
                && attribute.TryGetProperty("key", out var keyElement)
                && keyElement.ValueKind == JsonValueKind.String
                && string.Equals(keyElement.GetString(), PromptAttributeKey, StringComparison.Ordinal)
                && attribute.TryGetProperty("value", out var valueElement)
                && valueElement.ValueKind == JsonValueKind.Object
                && valueElement.TryGetProperty("stringValue", out var stringValue)
                && stringValue.ValueKind == JsonValueKind.String)
            {
                var text = stringValue.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

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

# Sprint21 M2: Bounded Conversation Context Extraction

M2 implements deterministic bounded conversation context before any prompt
change. This keeps the data contract testable without a live model.

## Target Files

- `src/CopilotAgentObservability.LocalMonitor/Analysis/InstructionEvidenceExtractor.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/InstructionEvidenceExtractorTests.cs`
- `src/CopilotAgentObservability.LocalMonitor/Projection/IMonitorProjectionStore.cs` only if existing reads are insufficient
- `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore*.cs` only if a narrow read helper is required
- `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionStoreTests.cs` only if store reads change

## Design

Prefer a pure extractor shape that receives preloaded sibling trace data:

```csharp
internal sealed record InstructionEvidenceConversationTraceInput(
    string TraceId,
    int RelativePosition,
    bool IsAnalyzedTrace,
    string? FirstStartTime,
    IReadOnlyList<MonitorSpanRow> Spans,
    IReadOnlyList<RawTelemetryRecord> RawRecords);
```

`MonitorAnalysisToolData.Create` should own I/O in M3. M2 should focus on the
pure transformation and tests.

Add records equivalent to the M1 contract:

```csharp
internal sealed record InstructionEvidenceConversationContext(
    string ConversationId,
    int TraceCount,
    int AnalyzedTraceIndex,
    int WindowStartIndex,
    int WindowEndIndex,
    bool TruncatedBefore,
    bool TruncatedAfter,
    IReadOnlyList<InstructionEvidenceConversationTrace> Traces);

internal sealed record InstructionEvidenceConversationTrace(
    string TraceId,
    int RelativePosition,
    bool IsAnalyzedTrace,
    string? FirstStartTime,
    string? UserInstructionDescriptor,
    int TurnCount,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    int ErrorSpanCount,
    int RetryChainCount,
    IReadOnlyList<string?> ErrorSpanIds,
    IReadOnlyList<string> RetryToolNames);
```

Keep caps explicit in constants:

```csharp
private const int ConversationContextSiblingRadius = 2;
private const int ConversationContextMaxTraces = 5;
private const int ConversationContextMaxErrorSpanIds = 5;
private const int ConversationContextMaxRetryToolNames = 5;
```

## Tasks

- [ ] Write failing extractor tests.
  - Middle of a seven-trace conversation emits positions `-2, -1, 0, +1, +2`,
    `TruncatedBefore == true`, `TruncatedAfter == true`.
  - First trace emits current plus following traces only, `TruncatedBefore ==
    false`.
  - Last trace emits previous traces plus current only, `TruncatedAfter ==
    false`.
  - Missing `conversation_id` omits `ConversationContext`.
  - Single-trace conversation emits one trace and no truncation.
  - Missing or malformed sibling raw payload omits only that trace's descriptor
    and does not throw.
  - Error span ids and retry tool names are capped.
  - Same input serialized twice with `JsonSerializerDefaults.Web` is
    byte-identical.

- [ ] Implement pure conversation-context extraction.
  - Do not perform I/O in `InstructionEvidenceExtractor`.
  - Reuse the existing user-instruction descriptor reader where possible.
  - Compute turn tokens using the existing turn filter (`operation == "chat" ||
    category == "llm_call"`).
  - Compute sibling retry summaries with the same retry-chain logic used for
    the analyzed trace.

- [ ] Preserve existing Sprint20 fields.
  - `error_spans`, `retry_chains`, `turn_tokens`, `user_instruction`, and
    `conversation` must remain deterministic and backward-compatible for
    existing tests.

- [ ] Add or update store tests only if a new store method is needed.
  - Prefer existing `ListConversationTraces`, `GetSpansForTrace`, and
    `ListRawRecordsByTraceId` reads before adding a method.
  - If a new method is unavoidable, it must be read-only over existing columns
    and documented in D048.

## Targeted Verification

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~InstructionEvidenceExtractorTests
```

Expected: all extractor tests pass.

If store reads changed:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionStoreTests
```

Expected: projection-store tests pass.

## Commit

```powershell
git add src tests
git commit -m "Instruction Diagnosis Conversation Scope: feat: add bounded conversation evidence extraction (Sprint21 M2)"
```

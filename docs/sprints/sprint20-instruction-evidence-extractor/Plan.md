# Sprint20 Instruction Evidence Extractor - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Subagent dispatch is allowed only when the user explicitly asks for it (AGENTS.md); otherwise execute inline.

**Goal:** Add a deterministic instruction-evidence extractor that runs in code at analysis start and feeds the LLM structured, verifiable evidence through a new process-internal tool `get_instruction_evidence`, plus prompt template v3 with per-category required-evidence rules (Issue #46 Phase 2, step 1).

**Architecture:** A pure static class `InstructionEvidenceExtractor` computes five structured evidence fields from the projection rows and raw records that `MonitorAnalysisToolData.Create` already loads, plus sibling-trace metadata resolved through one new read-only projection-store query keyed on `conversation_id`. The result is exposed as a seventh process-internal SDK tool next to the existing six in `DotNetCopilotRawAnalysisRunner`, and `InstructionDiagnosisPromptBlock` is revised to v3 so each taxonomy category must cite extractor fields (with an explicit raw-verified-citation escape hatch).

**Tech Stack:** .NET (existing Local Monitor project), xUnit, SQLite read query via existing `RawTelemetryStore` partial classes. No new dependencies.

## Global Constraints

- Additive only: one new class, one new process-internal tool, one prompt template revision. No new routes, no schema change, no new API fields, no projection migration (Sprint20 README Boundary).
- The one new read-only store query (`ListConversationTraces`) is an internal read over existing `monitor_spans` columns; it must be recorded in the M1 decision so it is not a silent boundary stretch.
- The existing six analysis tools stay available and unchanged (verification path).
- `--sanitized-only` continues to remove the whole raw analysis surface; no change to that switch.
- Keep the 4-part finding format, no-evidence-no-finding rule, Japanese final-report output rule, and D045 history blocks unchanged in prompt v3.
- Canvas focus set (D036) unchanged; `CanvasExtensionContractTests.cs` stays green without modification.
- No raw bodies or PII in extractor output committed anywhere; sprint evidence stays sanitized (trace/span references, verdicts, iteration log only).
- Test baseline: 700 passing after Sprint19; the pinned validation suite is `dotnet build CopilotAgentObservability.slnx`, `pwsh scripts\test\install-playwright-chromium.ps1`, `dotnet test CopilotAgentObservability.slnx` from the repository root.
- Commit messages start with the work item name: `Instruction Evidence Extractor: <type>: <subject>` (Conventional Commits after the prefix).
- Update source-of-truth documents (M1) before implementation (M2/M3), per AGENTS.md.

## File Map

| File | Action | Responsibility |
| --- | --- | --- |
| `docs/decisions.md` | Modify (append D047) | Decision record for deterministic pre-extraction |
| `docs/specifications/interfaces/instruction-diagnosis-analysis.md` | Modify | Extractor output contract + per-category required-evidence rules (prompt v3 source of truth) |
| `docs/specifications/layers/telemetry-ingestion.md` | Modify | Add `get_instruction_evidence` to the process-internal tool list |
| `docs/requirements.md`, `docs/spec.md` | Modify only if M1 review finds stale shipped-behavior wording | Raw analysis bullets |
| `src/CopilotAgentObservability.LocalMonitor/Analysis/InstructionEvidenceExtractor.cs` | Create | Pure deterministic extractor + output record types |
| `src/CopilotAgentObservability.LocalMonitor/Projection/IMonitorProjectionStore.cs` | Modify | Add `ListConversationTraces` |
| `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.Overview.cs` (or the partial that owns span reads) | Modify | SQL for `ListConversationTraces` |
| `src/CopilotAgentObservability.Persistence.Sqlite/MonitorProjectionRows.cs` | Modify | `MonitorConversationTraceRow` DTO |
| `src/CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs` | Modify | Wire extractor into `MonitorAnalysisToolData`, define the 7th tool, prompt v3 block |
| `tests/CopilotAgentObservability.LocalMonitor.Tests/InstructionEvidenceExtractorTests.cs` | Create | Extractor unit tests (synthetic fixtures) |
| `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionStoreTests.cs` | Modify | `ListConversationTraces` store tests |
| `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorAnalysisRouteTests.cs` | Modify | Prompt v3 content tests, tool data shape, existing focus prompts unchanged |
| `docs/sprints/sprint20-instruction-evidence-extractor/milestones/M4-regression-validation.md` | Create | M4 evidence |
| `docs/sprints/sprint20-instruction-evidence-extractor/milestones/M5-ab-validation/` | Create | Sanitized A/B evidence |

---

## M1 - Specs and decision record

**Purpose:** Pin the extractor field contract, the coupling rules, and the additive boundary in the sources of truth before any code exists, so M2/M3 implement a decided contract instead of improvising one (AGENTS.md: spec first; Sprint19 lesson: category=evidence coupling was the weakest contract element).

**Goal (exit criteria):**
- D047 appended to `docs/decisions.md` with Status: Accepted, covering: extractor field set, the additive `get_instruction_evidence` tool, the one read-only sibling-trace query, per-category coupling rules, the raw-verified-citation escape hatch, and the M5 A/B gate.
- `instruction-diagnosis-analysis.md` specifies the extractor output contract (field names, per-field content rules, deterministic ordering, no long raw bodies, user-instruction descriptor derivation rule) and the per-category required-evidence table.
- `telemetry-ingestion.md` lists seven process-internal tools.
- `docs/requirements.md` / `docs/spec.md` raw-analysis bullets reviewed; updated only if they pin six-tool or prompt-v2 wording as shipped behavior.
- Docs-only commit created after recorded self-review.

### Task 1.1: Confirm the user-instruction source field

**Files:**
- Read only: `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorSpanProjectionBuilder.cs`, `src/CopilotAgentObservability.Telemetry/Monitoring/OtlpSpanReader.cs`, one real chat-span raw payload fixture from existing tests.

**Interfaces:**
- Produces: the decided payload attribute path for the user instruction text and the descriptor rule, written into the M1 spec update (Task 1.2). M2 Task 2.3 consumes this decision verbatim.

- [x] **Step 1:** Read how `MonitorSpanProjectionBuilder` identifies `operation == "chat"` spans and which payload attributes carry prompt content; note the exact attribute key(s). Result: prompt text lives only in the raw payload attribute `gen_ai.prompt` (stringValue); it is deliberately never projected, so the extractor resolves the first projected `operation == "chat"` span by ordinal, then reads `gen_ai.prompt` from that span's raw record `payload_json`.
- [x] **Step 2:** Decide and write down the descriptor rule (proposed: first line of the user instruction, truncated to 160 chars, marker `"..."` when truncated; empty/missing prompt -> `user_instruction` omitted). Record the chosen attribute path and rule for Task 1.2. Result: proposal adopted as-is; malformed payload JSON also omits the field (no throw).

### Task 1.2: Write D047 and the spec updates

**Files:**
- Modify: `docs/decisions.md` (append after D046)
- Modify: `docs/specifications/interfaces/instruction-diagnosis-analysis.md`
- Modify: `docs/specifications/layers/telemetry-ingestion.md`

- [x] **Step 1:** Append D047 to `docs/decisions.md` (match D046's Japanese style and bullet structure). Required content:
  - Deterministic pre-extraction step for the instruction-diagnosis focus (Issue #46 Phase 2 step 1, Sprint19 M5 GO verdict + two design inputs as motivation).
  - Extractor field set: `error_spans[]`, `retry_chains[]`, `turn_tokens[]`, `user_instruction`, `conversation` — exactly as defined in the Extractor Output Contract (Task 1.2 Step 2).
  - Additive tool `get_instruction_evidence` alongside the existing six; existing tools unchanged.
  - One new read-only projection-store query `ListConversationTraces(conversationId)` over existing `monitor_spans.conversation_id`; no schema change, no route, no API field.
  - Per-category required-evidence coupling rules + raw-verified-citation escape hatch (discovery stays possible).
  - M5 A/B gate: Sprint19 three criteria + "every finding grounded in extractor fields or an explicitly raw-verified span citation"; Sprint19 B1 findings 3/4 must not recur in equivalent form.
- [x] **Step 2:** In `instruction-diagnosis-analysis.md`, add two sections:
  - **Evidence Extractor Output Contract** — for each field: source columns (allowlist projection columns only), inclusion rule, ordering rule, and the no-long-raw-bodies rule. Use this contract (field semantics also drive M2 tests):
    - `error_spans[]`: spans with `status == "error"`, ordered by span ordinal; each entry = span id, tool name, error kind (`error_type`, `"unknown"` when null), short factual descriptor built only from allowlist columns (operation/tool/error kind — never payload text).
    - `retry_chains[]`: per tool name, spans ordered by span ordinal; a chain starts at an error span of that tool and extends through subsequent spans of the same tool; emitted only when length >= 2; final outcome `recovered` (last span ok) or `unrecovered` (last span error). Ordered by first span ordinal.
    - `turn_tokens[]`: spans with `operation == "chat"` or `category == "llm_call"` (same filter as the existing cache summary), ordered by span ordinal; each entry = 1-based turn index, span id, input/output tokens (null -> 0).
    - `user_instruction`: the first chat-operation span by span ordinal; entry = span id, raw record id, descriptor per the Task 1.1 rule; omitted when no chat span or no resolvable prompt text.
    - `conversation`: the analyzed trace's `conversation_id` (from its spans), sibling trace ids ordered by earliest span start time (tie-break: trace id), trace count, and the analyzed trace's position; metadata only, no bodies; omitted when no conversation id.
    - Determinism rule: same inputs -> byte-identical serialized output; all ordering rules explicit.
  - **Per-Category Required Evidence (prompt v3)** — the coupling table:
    - `missing-context-constraints`: must cite at least one `error_spans` or `retry_chains` entry by span id.
    - `task-size-split`: must cite both a multi-goal `user_instruction` descriptor and a `turn_tokens` concentration (named turns).
    - `ambiguity`: must cite user rephrase evidence — `conversation` sibling metadata plus the corrective wording inside the analyzed trace.
    - `goal-clarity`, `missing-acceptance-criteria`: must cite turn-level evidence of the analyzed trace (`turn_tokens` entries and/or spans verified through the raw tools).
    - Escape hatch: a finding grounded outside the extractor output is allowed only with a span id citation explicitly verified through the raw tools, stated as such in the finding.
- [x] **Step 3:** In `telemetry-ingestion.md`, add `get_instruction_evidence` to the process-internal tool list (six -> seven) with a one-line description mirroring the other entries.

### Task 1.3: Review shipped-behavior wording and commit

**Files:**
- Read: `docs/requirements.md`, `docs/spec.md` (raw analysis bullets)
- Modify: only if wording pins "six tools" or prompt-v2-only behavior.

- [x] **Step 1:** Search both files for the raw-analysis / instruction-diagnosis bullets (`get_raw_trace`, `instruction-diagnosis`, tool counts). Update only stale shipped-behavior wording; otherwise leave untouched and note "no change needed" in the commit body. Result: `docs/spec.md` enumerated the six tool names (stale) — `get_instruction_evidence` added; `docs/requirements.md` wording is generic — no change needed.
- [x] **Step 2:** Recorded self-review (docs-only): scope, files checked, consistency of field names across D047 / interface spec / layer spec, no product behavior invented outside the Sprint20 README scope.
- [x] **Step 3:** Commit:

```powershell
git add docs/decisions.md docs/specifications/interfaces/instruction-diagnosis-analysis.md docs/specifications/layers/telemetry-ingestion.md docs/requirements.md docs/spec.md
git commit -m "Instruction Evidence Extractor: docs: add D047 and extractor output contract (Sprint20 M1)"
```

---

## M2 - Extractor and unit tests

**Purpose:** Implement the deterministic evidence computation as a pure, fully unit-tested function before any SDK/prompt wiring, so evidence correctness is provable without a live model and the field design is exercised against shapes beyond the six Sprint19 traces (README risk 1).

**Goal (exit criteria):**
- `ListConversationTraces` exists on `IMonitorProjectionStore` + `RawTelemetryStore` with store-level tests (sibling ordering, no-conversation case).
- `InstructionEvidenceExtractor.Extract` implements the M1 contract; unit tests cover: error-span extraction, retry-chain detection (recovered and unrecovered), turn token distribution, user-instruction resolution, conversation sibling metadata, and edge fixtures (zero errors, missing conversation id, single-span trace, empty trace).
- Deterministic ordering asserted (same input twice -> equal serialized output).
- All new tests green; committed in small steps.

### Task 2.1: Sibling-trace read query

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/MonitorProjectionRows.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.Overview.cs` (keep beside the other span reads)
- Modify: `src/CopilotAgentObservability.LocalMonitor/Projection/IMonitorProjectionStore.cs` (interface + `RawTelemetryStoreProjectionStore` delegate with `Guard`)
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionStoreTests.cs`

**Interfaces:**
- Produces: `IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId)`; `MonitorConversationTraceRow(string TraceId, string? FirstStartTime)` ordered by earliest span `start_time` then `TraceId`. Consumed by Task 3.1.

- [x] **Step 1: Write the failing store test** (follow the existing seeding helpers in `MonitorProjectionStoreTests.cs`):

```csharp
[Fact]
public void ListConversationTraces_ReturnsSiblingsOrderedByFirstStartTime()
{
    // seed spans for trace-b (start 2026-07-01T00:05Z) and trace-a (start 2026-07-01T00:01Z)
    // sharing conversation_id "conv-1", plus trace-c without a conversation id
    var rows = store.ListConversationTraces("conv-1");
    Assert.Equal(new[] { "trace-a", "trace-b" }, rows.Select(r => r.TraceId).ToArray());
}

[Fact]
public void ListConversationTraces_UnknownConversation_ReturnsEmpty()
{
    Assert.Empty(store.ListConversationTraces("missing"));
}
```

- [x] **Step 2: Run to verify failure** — `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~ListConversationTraces` — expect compile error (method missing).
- [x] **Step 3: Implement.** DTO in `MonitorProjectionRows.cs`:

```csharp
/// <summary>Sanitized sibling-trace metadata for one conversation_id (Sprint20, D047). Metadata only.</summary>
internal sealed record MonitorConversationTraceRow(
    string TraceId,
    string? FirstStartTime);
```

SQL in the store partial (read-only over existing columns):

```sql
SELECT trace_id, MIN(start_time) AS first_start_time
FROM monitor_spans
WHERE conversation_id = @conversationId
GROUP BY trace_id
ORDER BY MIN(start_time), trace_id;
```

Add the interface member and the `Guard(...)` delegating implementation in `RawTelemetryStoreProjectionStore`.
- [x] **Step 4: Run the filter above** — expect PASS. Result: 2/2 pass. Note: the interface addition also required the three test fake stores (`MonitorSummaryEndpointTests`, `MonitorTraceDetailTests`, `ProjectionWorkerTests`) to implement the new member.
- [x] **Step 5: Commit** — `Instruction Evidence Extractor: feat: add ListConversationTraces read query (Sprint20 M2)`.

### Task 2.2: Extractor skeleton + error spans, retry chains, turn tokens

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/Analysis/InstructionEvidenceExtractor.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/InstructionEvidenceExtractorTests.cs`

**Interfaces:**
- Produces (consumed by Tasks 2.3 and 3.1):

```csharp
internal static class InstructionEvidenceExtractor
{
    public static InstructionEvidence Extract(
        string traceId,
        IReadOnlyList<MonitorSpanRow> spans,
        IReadOnlyList<RawTelemetryRecord> rawRecords,
        IReadOnlyList<MonitorConversationTraceRow> conversationTraces);
}

internal sealed record InstructionEvidence(
    IReadOnlyList<InstructionEvidenceErrorSpan> ErrorSpans,
    IReadOnlyList<InstructionEvidenceRetryChain> RetryChains,
    IReadOnlyList<InstructionEvidenceTurnTokens> TurnTokens,
    InstructionEvidenceUserInstruction? UserInstruction,
    InstructionEvidenceConversation? Conversation);

internal sealed record InstructionEvidenceErrorSpan(
    string? SpanId, string? ToolName, string ErrorKind, string Descriptor);

internal sealed record InstructionEvidenceRetryChain(
    string? ToolName, IReadOnlyList<string?> SpanIds, string FinalOutcome); // "recovered" | "unrecovered"

internal sealed record InstructionEvidenceTurnTokens(
    int TurnIndex, string? SpanId, int InputTokens, int OutputTokens);

internal sealed record InstructionEvidenceUserInstruction(
    string? SpanId, long RawRecordId, string Descriptor);

internal sealed record InstructionEvidenceConversation(
    string ConversationId, IReadOnlyList<string> TraceIds, int TraceCount, int AnalyzedTraceIndex);
```

- [x] **Step 1: Write failing tests** using a fixture builder (synthetic `MonitorSpanRow` factory with defaults; only override what each test needs). Required cases, all per the M1 contract:
  - `Extract_ErrorSpans_ListsErrorStatusSpansInOrdinalOrder` (mixed ok/error spans; asserts span ids, `ErrorKind == "unknown"` for null `ErrorType`, descriptor contains no payload text).
  - `Extract_RetryChains_FailureThenSameToolRetry_Recovered` (tool X: error then ok -> one chain, outcome `recovered`).
  - `Extract_RetryChains_FailureWithoutRecovery_Unrecovered` (tool X: error, error -> outcome `unrecovered`).
  - `Extract_RetryChains_SingleFailureNoRetry_NotEmitted` (chain length 1 -> no chain; still present in `error_spans`).
  - `Extract_TurnTokens_UsesChatAndLlmCallSpansWithNullTokensAsZero`.
  - `Extract_EmptyTrace_ReturnsEmptyCollectionsAndNulls` (no spans, no raw records).
  - `Extract_IsDeterministic_SameInputTwiceGivesEqualSerializedOutput` (serialize both with `JsonSerializerDefaults.Web`, assert string equality).
- [x] **Step 2: Run** `dotnet test ... --filter FullyQualifiedName~InstructionEvidenceExtractorTests` — expect FAIL (type missing).
- [x] **Step 3: Implement** the three collection fields as pure LINQ over `spans` ordered by `(RawRecordId, SpanOrdinal)` (matches the store's deterministic read order and disambiguates ordinals across multi-record traces); no I/O, no clock, no randomness. Retry-chain algorithm: group spans with non-null `ToolName` by tool; walk each group in ordinal order; open a chain at an error span, append subsequent same-tool spans, close at first ok (recovered) or group end (unrecovered); emit chains with >= 2 spans, ordered by first span ordinal.
- [x] **Step 4: Run the filter** — expect PASS. Result: 7/7 pass.
- [x] **Step 5: Commit** — `Instruction Evidence Extractor: feat: add extractor error/retry/turn fields (Sprint20 M2)`.

### Task 2.3: User instruction and conversation fields

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/Analysis/InstructionEvidenceExtractor.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/InstructionEvidenceExtractorTests.cs`

- [x] **Step 1: Write failing tests:**
  - `Extract_UserInstruction_ResolvesFirstChatSpanAndDescriptor` (synthetic raw payload containing the attribute path decided in Task 1.1; asserts span id, raw record id, descriptor truncation at 160 chars with `"..."`).
  - `Extract_UserInstruction_TakesFirstLineOnly` (JSON-escaped newline → first line only). Added beyond the plan list to pin the first-line rule.
  - `Extract_UserInstruction_NoChatSpanOrNoPrompt_ReturnsNull` (also covers missing-record and malformed-JSON → null, no throw).
  - `Extract_Conversation_OrdersSiblingsAndLocatesAnalyzedTrace` (pass three `MonitorConversationTraceRow`s; asserts order preserved, `TraceCount == 3`, `AnalyzedTraceIndex` is the 1-based position of `traceId`).
  - `Extract_Conversation_MissingConversationId_ReturnsNull` (spans without `ConversationId` and empty sibling list).
  - `Extract_SingleSpanTrace_ProducesConsistentOutput` (one chat span only — README risk fixture).
- [x] **Step 2: Run** — expect FAIL.
- [x] **Step 3: Implement** per the M1 contract: user-instruction parsing reads only the analyzed chat span's raw record (`RawRecordId` join into `rawRecords`), tolerates malformed JSON by returning null (no throw), and never emits more than the descriptor cap. Note: a private OTLP parser mirroring `SpanDetailExtractor` was added (its `Truncate` uses `…`, but D047 requires a literal `"..."`, so the marker was not reused).
- [x] **Step 4: Run** — expect PASS. Result: 13/13 in the class (incl. determinism).
- [x] **Step 5: Commit** — `Instruction Evidence Extractor: feat: add user-instruction and conversation evidence fields (Sprint20 M2)`.

---

## M3 - Tool exposure and prompt template v3

**Purpose:** Make the extractor output reachable by the model (`get_instruction_evidence`) and make the prompt require it per category, converting the Sprint19 free-coupling prompt into the enforced coupling contract — while leaving every existing tool, focus, and prompt path byte-compatible.

**Goal (exit criteria):**
- `MonitorAnalysisToolData` carries the extractor output; `Create` resolves siblings via `ListConversationTraces` using the analyzed trace's `conversation_id`.
- Seventh tool `get_instruction_evidence` defined alongside the existing six; six existing tools unchanged.
- `InstructionDiagnosisPromptBlock` v3 contains the per-category required-evidence rules and escape hatch from the M1 spec; 4-part format, no-evidence-no-finding, Japanese rule, D045 history blocks unchanged.
- Unit tests green: prompt v3 content, tool data shape, existing focus prompts unchanged (`BuildPrompt_ExistingFocuses_KeepGenericPromptWithoutTaxonomy` still passes unmodified).

### Task 3.1: Wire extractor into tool data and define the tool

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorAnalysisRouteTests.cs`

**Interfaces:**
- Consumes: `InstructionEvidenceExtractor.Extract(...)` (Task 2.2/2.3), `ListConversationTraces` (Task 2.1).
- Produces: `MonitorAnalysisToolData.InstructionEvidence` (type `InstructionEvidence`) and tool name `get_instruction_evidence`.

- [x] **Step 1: Write failing tests** (same style as the existing tool-data tests in `MonitorAnalysisRouteTests.cs`): both seeded through a real `RawTelemetryStore` wrapped in `RawTelemetryStoreProjectionStore`, so the new `Create` → `ListConversationTraces` wiring is exercised end-to-end (not a hand-rolled fake).
  - `MonitorAnalysisToolData_Create_PopulatesInstructionEvidence` (fake/seeded projection store; asserts error spans and conversation metadata appear for a seeded trace).
  - `MonitorAnalysisToolData_Create_NoConversationId_ProducesNullConversation`.
- [x] **Step 2: Run** `--filter FullyQualifiedName~MonitorAnalysisToolData_Create` — expect FAIL. Result: CS1061 (`MonitorAnalysisToolData` has no `InstructionEvidence`).
- [x] **Step 3: Implement:**
  - Extend the record: `internal sealed record MonitorAnalysisToolData(..., object CacheSummary, InstructionEvidence InstructionEvidence)`.
  - In `Create`: take the first non-null `ConversationId` from `spans`; call `projectionStore.ListConversationTraces(...)` when present (else empty list); call `InstructionEvidenceExtractor.Extract(context.TraceId, spans, rawRecords, conversationTraces)`. Note: added `using CopilotAgentObservability.Persistence.Sqlite;` for the `Array.Empty<MonitorConversationTraceRow>()` no-conversation branch.
  - Add the tool in `RunCopilotSessionAsync`, after the existing six:

```csharp
DefineTool(
    "get_instruction_evidence",
    "Return deterministic, code-extracted instruction evidence (error spans, retry chains, turn tokens, user instruction, conversation metadata) for this Local Monitor analysis run.",
    () => Serialize(data.InstructionEvidence)),
```

- [x] **Step 4: Run the filter** — expect PASS (including all pre-existing tests in the class). Result: 12/12 pass in `MonitorAnalysisRouteTests`.
- [x] **Step 5: Commit** — `Instruction Evidence Extractor: feat: expose get_instruction_evidence tool (Sprint20 M3)` (1f32ad0).

### Task 3.2: Prompt template v3

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs` (`InstructionDiagnosisPromptBlock`)
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorAnalysisRouteTests.cs`

- [x] **Step 1: Write failing tests:**
  - `BuildPrompt_InstructionDiagnosis_EmbedsEvidenceGroundingRules` — asserts the v3 block names `get_instruction_evidence` and contains the per-category rule lines (assert on stable substrings: `error_spans`, `retry_chains`, `turn_tokens`, `raw-verified`). Also asserts `user_instruction` and `conversation`.
  - Keep `BuildPrompt_InstructionDiagnosis_EmbedsTaxonomyAndFindingContract`, `BuildPrompt_InstructionDiagnosis_KeepsHistoryAndFollowUpBlocks`, and `BuildPrompt_ExistingFocuses_KeepGenericPromptWithoutTaxonomy` green without weakening their assertions.
- [x] **Step 2: Run** — expect the new test to FAIL. Result: FAIL (`get_instruction_evidence` not found in prompt).
- [x] **Step 3: Implement** — extend `InstructionDiagnosisPromptBlock` (keep taxonomy v1 list, 4-part format, no-evidence-no-finding rule, Japanese rule, final markdown-only rule) by inserting an evidence-grounding section after the taxonomy list, transcribed from the M1 spec table. Escape-hatch line uses the `raw-verified` token (D047 gate vocabulary). Draft wording (final wording = M1 spec):

```text
Evidence grounding rules (v3): call get_instruction_evidence first. It returns deterministic, code-extracted evidence: error_spans, retry_chains, turn_tokens, user_instruction, conversation.
- missing-context-constraints: cite at least one error_spans or retry_chains entry by span id.
- task-size-split: cite both a multi-goal user_instruction descriptor and a turn_tokens concentration (name the turns).
- ambiguity: cite user rephrase evidence - conversation sibling metadata plus the corrective wording inside the analyzed trace.
- goal-clarity and missing-acceptance-criteria: cite turn-level evidence of the analyzed trace (turn_tokens entries and/or spans you verified through the raw tools).
- A finding grounded outside the extractor output is allowed only with a span id you explicitly verified through the raw tools in this session; state that verification in the finding.
```

- [x] **Step 4: Run the full test class** — expect PASS. Result: 13/13 pass in `MonitorAnalysisRouteTests`.
- [x] **Step 5: Update the XML doc comment** on the constant (D046 -> D046+D047, prompt v3) and **commit** — `Instruction Evidence Extractor: feat: prompt template v3 with per-category evidence rules (Sprint20 M3)` (254f3d2).

---

## M4 - Regression validation

**Purpose:** Prove the additive boundary held — the whole pinned suite (including Playwright smoke, canvas contract, security boundary, and sanitized-only tests) passes with the new code, and the diff is consistent with the updated sources of truth.

**Goal (exit criteria):**
- Pinned suite green from the repository root: build, Playwright install script, full `dotnet test` (>= 700 baseline + all new tests; no skipped-as-substitute).
- Spec-consistency self-review recorded per `docs/agent-guides/review-workflow.md` (implementation change -> deeper review: spec compliance, tests/edge cases, maintainability, data safety — extractor output must contain no raw bodies beyond the capped descriptor, docs consistency).
- Evidence file `milestones/M4-regression-validation.md` committed (commands, counts, findings, residual risks), matching the Sprint19 M4 format.

### Task 4.1: Run the pinned suite and record evidence

- [ ] **Step 1:**

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Expected: build succeeds; test run reports 0 failed, total >= 700 + new tests. If any command fails, stop and fix; do not substitute a different command (AGENTS.md).
- [ ] **Step 2:** Self-review the full sprint diff against `docs/requirements.md`, `docs/spec.md`, the two updated specification files, and D047. Confirm: no route/schema/API-field change, six existing tools untouched, `CanvasExtensionContractTests.cs` unmodified and green, no raw/PII in any committed file.
- [ ] **Step 3:** Write `docs/sprints/sprint20-instruction-evidence-extractor/milestones/M4-regression-validation.md` with: commands run + result counts, review scope and findings (or "no blocking issues"), residual risks (M5 pending; extractor over-fit risk covered by edge fixtures), unverified scope (live BYOK path — M5).
- [ ] **Step 4:** Update the Sprint20 README milestone table (M1-M4 -> Done) and **commit** — `Instruction Evidence Extractor: docs: record Sprint20 M4 regression validation`.

---

## M5 - A/B live validation and Issue #46 update (human-gated)

**Purpose:** Measure whether deterministic pre-extraction actually tightens category=evidence coupling on the same six real traces Sprint19 M5 used, without losing recall — the decision input for the next Phase 2 step.

**Goal (exit criteria, human judgment required):**
- Instruction-diagnosis focus re-run over the same six traces from the preserved DB (`artifacts\sprint19-live-validation\monitor-live.db`) via the validated BYOK path (glm-5.2 on the OpenCode Go endpoint, `CopilotAnalysis:TimeoutSeconds` 600).
- Gate: (1) every citation exists in the analyzed trace; (2) findings are trace-specific; (3) no finding without evidence; (4) **new** — every finding grounded in extractor fields or an explicitly raw-verified span citation. Sprint19 B1 findings 3 and 4 must not recur in equivalent form.
- Recall check: not materially fewer valid findings than Sprint19 on the same traces (fewer -> signal to loosen coupling, recorded, iterate).
- Each iteration (prompt v3 or extractor field change) and verdict recorded; sanitized A/B evidence under `milestones/M5-ab-validation/`; outcome appended to Issue #46; README milestone table and `docs/task.md` updated.

### Task 5.1: Precondition check

- [ ] **Step 1:** Verify `artifacts\sprint19-live-validation\monitor-live.db` exists. If lost, fall back to generating fresh traces and record the deviation explicitly in the M5 evidence (README risk 3 — never a silent switch).
- [ ] **Step 2:** Confirm with the user that the BYOK provider secrets are configured and they are ready to gate the run (human-gated milestone; do not start it unattended).

### Task 5.2: A/B run and comparison

- [ ] **Step 1:** For each of the six trace ids, run the instruction-diagnosis analysis through the monitor against the preserved DB; save each report (local runtime data — reports themselves stay out of the repository).
- [ ] **Step 2:** Build the A/B comparison table (per trace: finding count, categories, citation validity, grounding source extractor/raw-verified, coupling verdict) against the Sprint19 M5 results in `../sprint19-instruction-diagnosis-analysis/milestones/M5-live-validation/live-validation.md`.
- [ ] **Step 3:** Apply the gate with the user (criteria 2 and coupling equivalence are human judgment). On failure: iterate prompt v3 wording first, extractor field definitions second; append every iteration + verdict to the evidence file; re-run only the affected traces.

### Task 5.3: Evidence, Issue #46, closeout

- [ ] **Step 1:** Write `milestones/M5-ab-validation/ab-validation.md` — sanitized only: trace/span id references, verdict table, iteration log, gate outcome. No prompts, no report bodies, no PII.
- [ ] **Step 2:** Draft the Issue #46 comment (A/B outcome, gate verdict, next-step recommendation) and post it after user confirmation.
- [ ] **Step 3:** Update the Sprint20 README status/milestone table and `docs/task.md` (Sprint20 outcome, Phase 2 next-step status). **Commit** — `Instruction Evidence Extractor: docs: record Sprint20 M5 A/B validation and Issue #46 update`.

---

## Validation Commands (reference)

```powershell
# targeted, while iterating
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~InstructionEvidenceExtractorTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorAnalysisRouteTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionStoreTests

# pinned suite (M4)
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

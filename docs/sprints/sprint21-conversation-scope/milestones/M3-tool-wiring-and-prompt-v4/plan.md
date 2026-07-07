# Sprint21 M3: Tool Wiring And Prompt Template v4

M3 wires the bounded context into analysis tool data and updates the
instruction-diagnosis prompt so the model can use sibling traces safely.

## Target Files

- `src/CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorAnalysisRouteTests.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorTraceDetailTests.cs` or other fake stores only if interface changes require test doubles to compile

## Tool Data Wiring

`MonitorAnalysisToolData.Create` should:

1. Load the analyzed trace's spans and raw records as today.
2. Read `conversation_id` from the analyzed trace's spans.
3. Use `ListConversationTraces(conversationId)` to get ordered siblings.
4. Compute the bounded window around the analyzed trace.
5. Load spans and raw records only for trace ids inside the window.
6. Pass those preloaded inputs to `InstructionEvidenceExtractor`.

Do not load every trace in a large conversation.

## Prompt v4 Rules

The instruction-diagnosis prompt block should keep the Sprint20 taxonomy and
four-part finding contract, then add v4 conversation rules:

- Call `get_instruction_evidence` first.
- Treat the analyzed trace as the anchor.
- Use `conversation_context.traces[]` only as bounded supporting evidence.
- Sibling citations must include the sibling `trace_id` and relative position.
- Do not cite traces outside `conversation_context.traces[]`.
- If the only evidence is outside the bounded window, say the bounded evidence
  is insufficient.
- Do not copy sibling instruction descriptors verbatim into the final report
  beyond short factual references.

## Tasks

- [ ] Write failing tool-data tests.
  - Seed a conversation with at least five traces.
  - Analyze the middle trace and assert `ConversationContext.Traces` includes
    previous/current/next entries in order.
  - Assert a large conversation does not load or emit out-of-window traces.
  - Assert no `conversation_id` produces null/empty bounded context.

- [ ] Implement bounded sibling loading.
  - Keep the I/O in `MonitorAnalysisToolData.Create`.
  - Keep pure summarization in `InstructionEvidenceExtractor`.
  - Avoid new public methods if existing projection-store methods are enough.

- [ ] Write failing prompt tests.
  - `BuildPrompt_InstructionDiagnosis_EmbedsConversationScopeRules`
    should assert stable substrings:
    - `conversation_context`
    - `analyzed trace`
    - `sibling trace`
    - `trace_id`
    - `bounded window`
    - `outside the bounded window`
  - Existing `BuildPrompt_ExistingFocuses_KeepGenericPromptWithoutTaxonomy`
    must remain green.
  - Existing history/follow-up tests must remain green.

- [ ] Implement prompt template v4.
  - Update the XML doc comment from prompt v3 to v4.
  - Keep Japanese output rule unchanged.
  - Keep raw-verified escape hatch, but require out-of-extractor evidence to
    still be inside the bounded context when it is sibling evidence.

- [ ] Confirm tool name stability.
  - Prefer no new tool name: `get_instruction_evidence` returns the expanded
    structured evidence.
  - If M1 chose a new process-internal tool, add tests that the old tool still
    exists and the new tool is instruction-diagnosis specific.

## Targeted Verification

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorAnalysisToolData_Create
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~BuildPrompt_InstructionDiagnosis
```

Expected: all selected tests pass.

## Commit

```powershell
git add src tests
git commit -m "Instruction Diagnosis Conversation Scope: feat: wire bounded context into analysis prompt (Sprint21 M3)"
```

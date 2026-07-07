# Sprint21 M4 - Regression Validation Record

Date: 2026-07-08

## Commands

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | Passed. Build succeeded with 0 warnings and 0 errors. |
| `pwsh scripts\test\install-playwright-chromium.ps1` | Passed. Command exited 0. |
| `dotnet test CopilotAgentObservability.slnx` | Passed. 301 ConfigCli tests and 427 LocalMonitor tests passed; 0 failed, 0 skipped. |

Additional targeted iteration checks:

| Command | Result |
| --- | --- |
| `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~InstructionEvidenceExtractorTests` | Passed. 20 tests passed. |
| `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorAnalysisToolData_Create\|FullyQualifiedName~BuildPrompt_InstructionDiagnosis"` | Passed. 8 tests passed. |
| `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorAnalysisToolData_Create_ExistingFocusDoesNotLoadConversationContext` | Initially failed before the review fix, then passed after scoping bounded context to `instruction-diagnosis`. |

Note: one earlier attempt ran two targeted `dotnet test` commands in parallel
and one process failed with a build output file lock on
`CopilotAgentObservability.LocalMonitor.dll`. The failed command was rerun
sequentially and passed; it is not counted as successful evidence.

## Self-Review

- Source-of-truth alignment: D048, requirements, spec index, telemetry
  ingestion spec, and the instruction-diagnosis interface spec define the
  bounded `conversation_context` contract before implementation.
- Boundary checks: no new public route, `/api/monitor/*` field, SSE change,
  projection migration, Canvas focus, dependency, compatibility shim, or public
  export was added.
- Data safety: bounded sibling summaries contain trace ids, relative position,
  timing metadata, capped first-line descriptors, token/error/retry counts, and
  capped span/tool references only. No repository evidence includes raw
  prompts, raw responses, tool arguments/results, PII, credentials, provider
  URLs, local sensitive paths, or full analysis markdown.
- Bounded loading: `MonitorAnalysisToolData.Create` keeps the analyzed trace as
  the anchor and loads spans/raw records only for traces in the emitted window
  and only for `instruction-diagnosis`. Tests cover
  middle/start/end/single/missing-conversation cases, verify large
  conversations do not read or emit out-of-window traces, and verify existing
  focuses do not load `conversation_context`.
- Prompt contract: prompt template v4 requires `get_instruction_evidence`,
  distinguishes analyzed-trace and sibling-trace evidence, requires sibling
  `trace_id` / relative position citations, and forbids claims outside the
  bounded window.
- `--sanitized-only`: unchanged. The raw analysis route surface is still
  removed wholesale in sanitized-only mode; this change only extends the
  process-internal raw-analysis evidence.

## Findings

- Independent review found that the first M3 implementation built
  `conversation_context` for non-`instruction-diagnosis` focuses. That would
  have widened raw-derived sibling descriptor reachability through the internal
  SDK tool. Fixed by scoping conversation metadata/window resolution to
  `instruction-diagnosis` and adding
  `MonitorAnalysisToolData_Create_ExistingFocusDoesNotLoadConversationContext`.
- No remaining blocking issues found after the fix and rerun validation.

## Residual Risks

- M5 live validation is still required with preserved Sprint19/Sprint20 traces
  and at least two bounded conversation-window cases.
- Live validation must judge trace specificity and category coupling from the
  generated analysis result without committing raw analysis markdown.

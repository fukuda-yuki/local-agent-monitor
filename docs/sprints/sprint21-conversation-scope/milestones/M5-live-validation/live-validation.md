# Sprint21 M5 - Live Validation Record

Status: Conditional pass / Phase 2 step 2 GO with category-shift caveat.

This file holds the repository-safe live-validation procedure for Issue #46
Phase 2 step 2. Live validation was not run during M1-M4 implementation because
it requires preserved/local trace data and provider execution. Do not paste raw
prompts, raw responses, tool arguments/results, PII, credentials, provider
URLs, local sensitive paths, or full analysis markdown here.

## Environment

- Date: 2026-07-08
- Repository branch: `feat/sprint21`
- Local Monitor DB:
  `src\CopilotAgentObservability.LocalMonitor\artifacts\sprint19-live-validation\monitor-live.db`
- Analysis focus: `instruction-diagnosis`
- Provider label: BYOK local user-secrets provider used by the Local Monitor
  Copilot SDK runner; no provider URL, key, credential, or account identifier
  recorded.
- Timeout: 900 seconds for BYOK live runs after A1 smoke; A1 used 600 seconds.

## Execution Notes

- The initial GitHub Copilot Chat inspection could not run provider-backed
  analysis and is treated as pre-flight evidence only, not as the M5 gate.
- Codex could not keep the Local Monitor HTTP server running through
  `Start-Process` because the local command policy rejected long-lived process
  launch. Instead, validation used a temporary local .NET runner that invoked
  the same `DotNetCopilotRawAnalysisRunner`, `SqliteMonitorAnalysisStore`, and
  `RawTelemetryStoreProjectionStore` from
  `src\CopilotAgentObservability.LocalMonitor\bin\Debug\net10.0`, with the
  working directory set there so `runtimes\win-x64\native\copilot.exe` was
  available. This bypassed the HTTP route but exercised the same raw-default
  analysis runner and persisted runs in the preserved validation DB.
- A first A1 smoke accidentally used a relative DB path after changing working
  directory and wrote to a build-output runtime artifact; it is discarded as
  non-evidence. The recorded runs below all used the preserved DB absolute
  path.
- A no-user-secrets A2 attempt failed with a monthly quota error and is not
  validation evidence. User-secrets loading was then aligned with
  `MonitorHost`, and the recorded BYOK run succeeded.
- Full analysis markdown remains local runtime data in the validation DB and
  is not copied into this repository evidence.

## Preconditions

- Branch includes M1-M4 commits for Sprint21 bounded conversation scope.
- Local Monitor runs in raw-default mode, not `--sanitized-only`.
- Preserved Sprint19/Sprint20 validation DB is available, or a deviation is
  recorded below.
- Provider configuration is available locally. Record provider labels only; do
  not record provider URLs, keys, credentials, or account identifiers.
- At least two same-`conversation_id` validation cases exist where the relevant
  clarification, acceptance criteria, or missing context appears in a previous
  or following sibling trace within the bounded window.

## User-Run Procedure

1. Start Local Monitor against the preserved validation DB in raw-default mode.
2. For the regression set, run `instruction-diagnosis` on the preserved
   Sprint19/Sprint20 traces that were used for Sprint20 extractor grounding.
3. For the conversation-window set, run `instruction-diagnosis` on at least two
   analyzed traces where relevant sibling evidence appears within
   `conversation_context.traces[]`.
4. Keep full analysis results as local runtime data only.
5. For each run, fill the table below with sanitized observations only:
   trace id, conversation position, run status, finding count, category ids,
   and bounded-context verdict.
6. Apply every gate in the Gate Assessment table. Human judgment is required
   for trace specificity, category coupling, and whether sibling evidence is
   explained clearly.
7. If a gate fails, iterate only prompt wording first. Change extractor fields
   only if the model cannot cite needed in-window evidence from the bounded
   summaries.

## Validation Set

- Regression set: preserved Sprint19/Sprint20 traces previously used to check
  citation existence, trace specificity, no-evidence-no-finding, and
  extractor/raw grounding.
- Conversation-window set: at least two same-conversation traces where a
  clarification, acceptance criterion, or missing context appears in an
  emitted previous/following sibling trace.
- Deviation rule: if the preserved DB lacks enough conversation-window cases,
  capture fresh local traces and record that deviation here without committing
  raw payloads or full analysis markdown.

## Validation Runs

| Ref | Trace ID | Conversation position | Run | Status | Finding count | Categories | Bounded-context verdict |
| --- | --- | --- | --- | --- | ---: | --- | --- |
| A1 | `3de778a823c30e19a55f32189b9d9983` | 1/2; following sibling A2 (`+1`) emitted | 25 | Succeeded | 2 | `missing-context-constraints`, `missing-acceptance-criteria` | Pass: bounded context available; no invalid trace refs; 6/6 span refs resolved within analyzed/window traces. |
| A2 | `3a9a195df2ea4a3390db205ac79e3929` | 2/2; previous sibling A1 (`-1`) emitted | 27 | Succeeded | 0 | none | Pass: model emitted no finding and treated bounded evidence as insufficient for a strict category finding; 7/7 span refs in non-finding evaluation resolved within the window. |
| B1 | `d646a7c4b389fd22d5b79741149eea45` | 1/2; following sibling B2 (`+1`) emitted | 28 | Succeeded | 1 | `missing-context-constraints` | Pass: sibling trace was cited from the emitted window with relationship context; 5/5 span refs resolved within analyzed/window traces. |
| B2 | `079b1d0a0e2a238d57f205a21320c229` | 2/2; previous sibling B1 (`-1`) emitted | 29 | Succeeded | 2 | `ambiguity`, `goal-clarity` | Pass: previous sibling B1 was cited from the emitted window; 5/5 span refs resolved within analyzed/window traces. |
| C1 | `432da7a06c1115e51f32bf42f65de9b6` | 1/2; following sibling C2 (`+1`) emitted | 30 | Succeeded | 1 | `task-size-split` | Pass: following sibling C2 was cited from the emitted window; 13/13 span refs resolved within analyzed/window traces. |
| C2 | `06803b4012e9904cc927ebce99088282` | 2/2; previous sibling C1 (`-1`) emitted | 31 | Succeeded | 1 | `task-size-split` | Pass: previous sibling C1 was cited from the emitted window; 3/3 span refs resolved within analyzed/window traces. |

## Gate Assessment

| Gate | Verdict | Evidence |
| --- | --- | --- |
| Citation existence | Pass | Six completed runs; 39/39 detected span refs resolved within the analyzed trace or emitted bounded sibling window. All detected trace refs were either the analyzed trace or an emitted sibling trace. |
| Trace specificity | Pass | Findings cite run-specific span ids, trace ids, and conversation positions. A2 emitted zero findings rather than generic prompt advice. |
| No-evidence-no-finding | Pass | Seven findings were emitted across six runs, and each finding-bearing run cited concrete trace/window evidence. A2 correctly emitted zero findings when the strict category evidence was insufficient. |
| Extractor/raw grounding | Pass | Prompt v4 runs used the Local Monitor raw analysis runner and `get_instruction_evidence` path; result refs were checked against the preserved projection/raw store without copying raw bodies. |
| Bounded-window compliance | Pass | Every sibling trace ref was one of the emitted same-`conversation_id` window entries: A1/A2, B1/B2, or C1/C2. No out-of-window trace ref was detected. |
| Sibling relationship clarity | Pass | Runs using sibling evidence named the sibling trace and previous/following relationship. A1 did not rely on sibling evidence for its findings. |
| Sprint20 regression | Conditional pass / category-shift caveat | B1 remained `missing-context-constraints`; B2 retained `ambiguity` and added `goal-clarity`; C2 changed from zero findings to `task-size-split` because bounded C1 evidence is now available; A2 changed from two findings to zero under stricter prompt-v4 evidence rules; C1 changed from Sprint20 `missing-context-constraints` to `task-size-split`. The shifts are recorded as prompt-v4/bounded-context behavior, not citation or safety regressions. |
| Data safety | Pass | This record includes only trace ids, run ids, counts, category ids, bounded-window positions, and gate verdicts. Raw prompts/responses, tool arguments/results, PII, credentials, provider URLs, local sensitive paths, and full analysis markdown are omitted. |

## Issue #46 Comment Draft

```markdown
Sprint21 / Issue #46 Phase 2 step 2 status update:

- Implemented bounded same-conversation context for `instruction-diagnosis`.
- The selected trace remains the anchor. The extractor emits at most two
  previous and two following same-`conversation_id` sibling traces, max five
  entries total, ordered by earliest span start time then trace id.
- The existing `get_instruction_evidence` tool now includes bounded
  `conversation_context` for `instruction-diagnosis`; no new public route,
  `/api/monitor/*` field, SSE change, projection migration, Canvas focus,
  dependency, or repository-safe raw export was added.
- Prompt template v4 requires sibling citations to name `trace_id` and
  relative position, explain the relationship to the analyzed trace, and avoid
  claims outside the bounded window.
- Regression validation passed:
  - `dotnet build CopilotAgentObservability.slnx`
  - `pwsh scripts\test\install-playwright-chromium.ps1`
  - `dotnet test CopilotAgentObservability.slnx`
- Full solution test result: 301 ConfigCli tests and 427 LocalMonitor tests
  passed, 0 failed.
- Independent review found one regression risk: bounded context was initially
  available to non-`instruction-diagnosis` focuses through the internal tool.
  Fixed by scoping conversation metadata/window loading to
  `instruction-diagnosis` and adding a regression test.
- M5 live validation completed against the preserved Sprint19/Sprint20 DB using
  six prompt-v4 `instruction-diagnosis` runs: A1 run 25, A2 run 27, B1 run 28,
  B2 run 29, C1 run 30, and C2 run 31.
- All six runs succeeded under the local BYOK provider path. Across the six
  runs, 39/39 detected span references resolved within the analyzed trace or
  emitted bounded sibling window, and every detected trace reference was either
  the analyzed trace or an emitted same-`conversation_id` sibling.
- Conversation-window behavior was exercised on all three preserved pairs:
  A1/A2, B1/B2, and C1/C2.
- Gate verdict: conditional pass / Phase 2 step 2 GO. Safety and grounding
  gates passed. Category distribution shifted versus Sprint20 as expected from
  stricter prompt-v4 evidence rules and newly available bounded sibling
  context: A2 emitted zero findings, C2 now emits `task-size-split`, and C1
  moved from `missing-context-constraints` to `task-size-split`. This is
  recorded as a category-shift caveat, not a citation or raw-boundary
  regression.
- No raw prompts/responses, tool arguments/results, credentials, provider URLs,
  PII, local sensitive paths, or full analysis markdown are included in the
  repository evidence.
```

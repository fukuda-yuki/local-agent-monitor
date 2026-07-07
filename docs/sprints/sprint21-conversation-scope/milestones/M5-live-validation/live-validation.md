# Sprint21 M5 - Live Validation Record

Status: Prepared; not run.

This file holds the repository-safe live-validation procedure for Issue #46
Phase 2 step 2. Live validation was not run during M1-M4 implementation because
it requires preserved/local trace data and provider execution. Do not paste raw
prompts, raw responses, tool arguments/results, PII, credentials, provider
URLs, local sensitive paths, or full analysis markdown here.

## Environment

- Date:
- Repository branch:
- Local Monitor DB:
- Analysis focus: `instruction-diagnosis`
- Provider label:
- Timeout:

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

## Gate Assessment

| Gate | Verdict | Evidence |
| --- | --- | --- |
| Citation existence | Not run |  |
| Trace specificity | Not run |  |
| No-evidence-no-finding | Not run |  |
| Extractor/raw grounding | Not run |  |
| Bounded-window compliance | Not run |  |
| Sibling relationship clarity | Not run |  |
| Sprint20 regression | Not run |  |
| Data safety | Not run |  |

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

Live validation remains user/provider gated. Required next check: run
`instruction-diagnosis` on the preserved Sprint19/Sprint20 regression traces
plus at least two bounded conversation-window cases, then apply the gates for
citation existence, trace specificity, no-evidence-no-finding,
extractor/raw grounding, bounded-window compliance, sibling relationship
clarity, Sprint20 regression, and data safety.
```

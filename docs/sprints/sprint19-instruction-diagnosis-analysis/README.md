# Sprint19 - Instruction Diagnosis Analysis (Issue #46 Phase 1)

Status: In progress (M1-M4 done).

Sprint19 implements Phase 1 of
[Issue #46](https://github.com/fukuda-yuki/copilot-agent-observability/issues/46):
add one **instruction diagnosis** analysis focus to the existing Local Monitor
Copilot raw analysis so a local user can get a trace-backed diagnosis of the
implementation instructions they gave the agent. The sprint exists to validate
the Phase 1 value hypothesis — that trace-backed instruction feedback exceeds
generic prompt advice — before any later phase (memory candidates, adoption
workflow) is considered.

## Scope (from Issue #46 Phase 1 refinement, 2026-07-05)

- Evidence is **trace-internal only**: multi-turn follow-up / rephrased
  instructions, error spans, failed or retried tool calls, token waste. No
  GitHub issue / commit / test-evidence correlation (follows the D037
  decision to keep traces manually selected).
- **Diagnosis display only**: results appear as a normal raw analysis result
  in the existing Copilot analysis drawer. No memory candidate generation, no
  adoption workflow, no new repository-safe export.
- **Built-in analysis focus**: a fixed prompt template embedding taxonomy v1,
  reproducible in one click. Wire value candidate: `instruction-diagnosis`
  (final name decided in M1).
- **Taxonomy v1 starts narrow** (4-6 categories: goal clarity, ambiguity,
  missing acceptance criteria, task size / split). A category may only be
  added together with a corresponding trace-internal evidence pattern.
- **Prompt-only first**: raw trace is fed through the existing runner; the
  prompt requires span/turn citations. Migration of proven evidence patterns
  to deterministic pre-extractors is a later phase, not this sprint.
- **Fixed per-finding output format**: category / trace evidence citation
  (span, turn) / gap explanation / improved next-time instruction. Findings
  without citable evidence are forbidden.

## Boundary

- Additive change only: one new `MonitorAnalysisFocus` value plus its prompt
  template branch. No new routes, no schema change, no new API fields.
- `/api/monitor/*`, SSE, and Canvas bounded DTOs stay sanitized and unchanged.
  The Canvas helper focus set (`latency` / `tokens` / `cache` / `errors`,
  D036) is not extended in this sprint; the new focus is Local Monitor drawer
  only. `CanvasExtensionContractTests.cs` must stay green unmodified.
- `--sanitized-only` continues to disable raw analysis wholesale (existing
  requirements.md rule); the new focus inherits that behavior.
- Drawer follow-up chat (history resend, D045) must keep working with the new
  focus.
- Sprint evidence must not contain raw prompt/response bodies, tool
  arguments/results, PII, credentials, or local sensitive paths — sanitized
  trace/span references and verdicts only.

## Milestones

| Milestone | Summary | Status |
| --- | --- | --- |
| M1 | Specs and decision record | Done |
| M2 | Focus value and prompt template | Done |
| M3 | Drawer UI exposure | Done |
| M4 | Regression validation | Done ([record](milestones/M4-regression-validation.md)) |
| M5 | BYOK live validation and Phase 2 gate (human-gated) | Done ([record](milestones/M5-live-validation/live-validation.md)) |

### M1 - Specs and decision record

- Add a decision to `docs/decisions.md`: additive focus extension for
  instruction diagnosis; trace-internal evidence only; drawer-only exposure
  (Canvas focus set unchanged); taxonomy v1 list and the category=evidence
  coupling rule; prompt-only start with planned deterministic-extractor
  migration as a later phase; fixed per-finding output contract.
- Update the Local Monitor Copilot raw analysis bullet in
  `docs/requirements.md` (section 3) to include the instruction diagnosis
  focus and its boundaries.
- Record the output contract and taxonomy v1 in a specification file (new
  `docs/specifications/interfaces/instruction-diagnosis-analysis.md`, or an
  existing spec file if M1 review finds a better home). Product behavior must
  not live only in this sprint folder.
- Decide the final wire value and the Japanese drawer label.

### M2 - Focus value and prompt template

- Extend `MonitorAnalysisFocus`, `ToWireValue`, and `TryParseFocus`
  (additive, following the `tool-usage` / `agent-flow` precedent).
- Add the instruction-diagnosis branch to
  `DotNetCopilotRawAnalysisRunner.BuildPrompt`: taxonomy v1, the
  evidence-citation requirement (trace/span/turn ids, no long raw bodies),
  the fixed 4-part finding format, and the no-evidence-no-finding rule.
- Keep the D045 history / follow-up question blocks compatible.
- Unit tests: prompt template content for the new focus, focus parse
  round-trip, existing focus prompts unchanged.

### M3 - Drawer UI exposure

- Add the focus option to the analysis drawer focus selector with the
  Japanese label decided in M1.
- Verify the Canvas helper focus set and bounded DTOs are untouched
  (contract tests green with no edits).
- Verify `--sanitized-only` still removes the entire raw analysis surface
  including the new focus (existing tests or one added assertion).
- Update the Playwright smoke test only if it asserts the drawer focus list.
- Update the drawer focus list in `docs/user-guide/local-monitor.md` (the
  guide documents shipped behavior, so this lands with the UI change, not
  in M1).

### M4 - Regression validation

- Run the pinned suite from the repository root:
  `dotnet build CopilotAgentObservability.slnx`,
  `pwsh scripts\test\install-playwright-chromium.ps1`,
  `dotnet test CopilotAgentObservability.slnx`.
- Spec-consistency self-review; record the review per
  `docs/agent-guides/review-workflow.md`.
- Commit in small steps per milestone with the work item prefix.

### M5 - BYOK live validation and Phase 2 gate (human-gated)

- Run the new focus against at least 3 real traces using the validated BYOK
  provider path (Sprint13 / Sprint18 practice).
- Gate criteria (from Issue #46 Phase 1):
  - Every finding cites a span/turn that actually exists in the trace.
  - Findings are trace-specific: a finding that still reads correct when
    swapped onto a different trace (generic advice) fails.
  - No finding appears without evidence.
- If the gate fails, iterate the prompt template (return to M2) and record
  each iteration and its verdict.
- Save sanitized evidence (trace/span references, verdicts, iteration log)
  under `milestones/M5-live-validation/`; no raw bodies.
- Append the Phase 2 go / no-go verdict to Issue #46.

## User-provided live validation inputs (M5)

- Running raw-default Local Monitor with BYOK provider configuration
  (validated on this machine 2026-07-04).
- Real local traces containing multi-turn implementation instructions
  acceptable for raw analysis.
- Human judgment on the trace-specificity criterion.

## Risks

- The dominant risk is output quality, not code: the diagnosis may degrade
  into generic prompt advice. The fixed output format and the
  no-evidence-no-finding rule exist to make that failure visible, and M5 is
  the explicit kill switch — if the gate fails after reasonable prompt
  iteration, close Phase 1 as "not validated" and record that on Issue #46
  instead of expanding scope.
- LLM-cited span/turn ids may be hallucinated; M5 must check citations
  against the actual trace, and persistent hallucination is a gate failure
  (it is also the signal that deterministic pre-extraction is required
  before any Phase 2).

# Sprint19 M4 - Regression Validation Record

Date: 2026-07-06. Branch: `feat/sprint19` (M1-M3 commits, working tree clean
before this record). Recorded per `docs/agent-guides/review-workflow.md`
(self-review format).

## Scope reviewed

The full Sprint19 diff `main...HEAD` (5 commits, ~329 insertions):

- Docs: `docs/decisions.md` (D046), `docs/requirements.md` (section 3 raw
  analysis bullet), `docs/spec.md`, `docs/specifications/README.md`,
  `docs/specifications/interfaces/instruction-diagnosis-analysis.md` (new),
  `docs/specifications/layers/telemetry-ingestion.md`, `docs/task.md`,
  `docs/user-guide/local-monitor.md`, sprint README.
- Product code: `Analysis/MonitorAnalysisModels.cs` (focus enum + wire
  value), `Analysis/DotNetCopilotRawAnalysisRunner.cs`
  (`InstructionDiagnosisPromptBlock` + per-focus prompt branch),
  `Pages/TraceDetail.cshtml` (drawer option).
- Tests: `MonitorAnalysisWireTests.cs` (new), `MonitorAnalysisRouteTests.cs`
  (new), `MonitorTraceDetailTests.cs`.

## Validation commands and results

Pinned suite run from the repository root on 2026-07-06:

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | Succeeded, 0 warnings / 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | Chromium bootstrap completed (`artifacts\playwright-browsers`) |
| `dotnet test CopilotAgentObservability.slnx` | **695 passing** (ConfigCli 301 + LocalMonitor 394, Playwright smoke included), 0 failed / 0 skipped |

The collector config check was not run: no files under `infra\otel-collector\`
changed in this sprint (per the pinned suite's conditional rule).

## Spec-consistency review (behavior checked)

Performed via the `spec-consistency-reviewer` subagent on `main...HEAD`;
cited evidence re-verified in the parent session. All checks pass:

- Additive-only: one new `MonitorAnalysisFocus` value `instruction-diagnosis`
  with `ToWireValue` / `TryParseFocus` round-trip (asserted in
  `MonitorAnalysisWireTests`, including rejection of `instruction_diagnosis`).
  No new routes, no schema change, no new API fields in the diff.
- Canvas boundary: `CanvasExtensionContractTests.cs` is unmodified
  (`git diff main...HEAD -- <file>` is empty) and green; the Canvas helper
  focus set (D036) is not extended — drawer-only exposure per D046.
- `--sanitized-only`: the whole analysis route mapping is gated by
  `if (!options.SanitizedOnly)` (`MonitorHost.cs:413`), so the new focus is
  removed with the rest of the raw analysis surface;
  `SanitizedOnly_RemovesRawAnalysisSurfaces` now POSTs
  `focus = "instruction-diagnosis"` and asserts 404.
- D045 compatibility: `BuildPrompt` swaps only the per-focus output block;
  history / follow-up blocks are appended unchanged
  (`BuildPrompt_InstructionDiagnosis_KeepsHistoryAndFollowUpBlocks`).
- Prompt-spec parity: `InstructionDiagnosisPromptBlock` embeds all 5
  taxonomy v1 category ids with definitions and evidence patterns matching
  `instruction-diagnosis-analysis.md` (including the ambiguity vs
  missing-context disambiguation), the fixed 4-part finding format, the
  citation-existence and no-long-raw-bodies rules, the
  no-evidence-no-finding rule, explicit zero-findings, and the
  Japanese-output pin for this focus only.
- Drawer label 指示診断 (`TraceDetail.cshtml`) matches D046 / spec; asserted
  in `MonitorTraceDetailTests`.
- `docs/user-guide/local-monitor.md` focus list (7 entries) matches the
  shipped `<option>` set.
- No raw prompt/response bodies, tool arguments, PII, credentials, or
  sensitive local paths in the committed diff.
- No product-behavior or public-interface change in the diff is missing from
  requirements/spec/specifications; sprint notes carry no unpromoted spec
  content. The Playwright drawer test does not assert the focus list, so the
  M3 conditional update was correctly not needed.

## Findings

No blocking issues found. Non-blocking observations:

1. The Japanese-output pin for findings is enforced only by the prompt in v1;
   actual output language is checked at the M5 live-validation gate
   (consistent with the prompt-only posture).
2. The prompt embeds category ids only, not the Japanese category names from
   the spec table; conformant ("Category ids are prompt-template and output
   vocabulary"). If M5 wants JA names in output, align prompt and spec then.
3. `docs/task.md` still phrases taxonomy v1 as "4〜6 分類から開始" while
   D046/spec fixed 5 categories; plan-era framing, acceptable at rank 6.

## Residual risks / unverified scope

- Output quality (trace-specific, correctly cited findings) is not verifiable
  by the regression suite; it is exactly the human-gated M5 scope (BYOK live
  validation, at least 3 real traces, Phase 2 go / no-go on Issue #46).
- LLM citation hallucination risk remains open until M5 checks citations
  against real traces.

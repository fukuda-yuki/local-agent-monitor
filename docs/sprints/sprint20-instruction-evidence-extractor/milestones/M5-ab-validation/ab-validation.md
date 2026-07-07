# Sprint20 M5 A/B Live Validation

Date: 2026-07-07 (BYOK runs); 2026-07-08 (no-BYOK C1/C2 completion).

Verdict: **Conditional pass / Phase 2 GO with provider caveat**. All six
preserved Sprint19 traces now have results: A1/A2/B1/B2 completed under the
validated BYOK path (glm-5.2), and C1/C2 completed under the SDK default
(no-BYOK) Copilot provider after the BYOK runs timed out at 900 seconds.
Prompt v3 with the deterministic instruction-evidence extractor produced
grounded, coupled findings (C1) and a correct no-evidence-no-finding result
(C2), so the implementation is not the blocker. The same-provider BYOK parity
criterion is recorded as a caveat, but the BYOK C1/C2 timeout is treated as an
external GitHub Copilot/provider-path issue rather than a Sprint20 product
blocker. With that caveat, Sprint20 M5 is closed and Issue #46 may be updated
with a repository-safe GO comment.

## Environment

- Repository branch: `feat/sprint20`
- Local Monitor DB:
  `C:\Users\mwam0\Documents\Codex\copilot-agent-observability\src\CopilotAgentObservability.LocalMonitor\artifacts\sprint19-live-validation\monitor-live.db`
- Analysis focus: `instruction-diagnosis`
- Provider configuration labels:
  - A1/A2/B1/B2 (BYOK): `Provider:Type=openai`, `WireApi=completions`,
    model `glm-5.2`, OpenAI-compatible OpenCode Go endpoint
  - C1/C2 (no-BYOK): SDK default Copilot provider, using the bundled
    `runtimes\win-x64\native\copilot.exe` next to the Local Monitor build
    output (see Root Cause)
- Timeout: BYOK runs used 600 then 900 seconds; the no-BYOK C1/C2 runs
  completed well inside the timeout
- Monitor execution: built Local Monitor executable, raw-default mode,
  loopback URL `http://127.0.0.1:4320`. BYOK attempts used one PowerShell
  orchestration per run; the successful no-BYOK C1/C2 runs were executed as
  the Local Monitor **.NET process** from `bin\Debug\net10.0` (not via
  in-PowerShell reflection).

Secrets, API key values, provider base URL, raw prompts, raw responses, tool
arguments/results, and full analysis markdown are intentionally omitted.

## Iteration Log

| Step | Trace | Run | Outcome | Notes |
| --- | --- | ---: | --- | --- |
| Initial launcher | n/a | n/a | Failed before analysis | The startup wrapper attempted to write under the user's AppData log location and was blocked by local permissions. |
| `dotnet run` launcher | n/a | n/a | Failed before analysis | Restore was attempted and failed with `NU1301`; this was treated as an environment failure, not validation evidence. |
| Direct long-lived monitor (BYOK) | B1 | 14 | Failed | SDK reached message send, then timed out after 600 seconds. |
| Direct long-lived monitor (BYOK) | B1 | 15 | Failed | Second 600-second timeout. |
| Timeout revision | n/a | n/a | Applied | `CopilotAnalysis:TimeoutSeconds` was raised to 900 seconds. |
| Direct long-lived monitor (BYOK) | B1 | 16 | Succeeded | Reused as Sprint20 M5 B1 evidence. |
| Direct long-lived monitor (BYOK) | B2 | 17 | Succeeded | Reused as Sprint20 M5 B2 evidence. |
| Interrupted monitor (BYOK) | C1 | 18 | Orphaned | User interruption left the DB row in `running`; the row was not mutated. |
| Revised orchestration (BYOK) | C1 | 19 | Failed | SDK reached message send, then timed out after 900 seconds. |
| Revised orchestration (BYOK) | C2 | 20 | Failed | SDK reached message send, then timed out after 900 seconds. |
| No-BYOK reflection attempt | C1/C2 | 21, 22 | Failed | Ran the runner via in-PowerShell reflection; the no-BYOK Copilot provider looked for `copilot.exe` under the PowerShell host location and did not find it. Environment/launch failure, not analysis evidence. |
| No-BYOK .NET process | C1 | 23 | Succeeded | Ran the runner as the Local Monitor .NET process from `bin\Debug\net10.0`, where `runtimes\win-x64\native\copilot.exe` is present. Result 2078 chars, 1 finding (`missing-context-constraints`). |
| No-BYOK .NET process | C2 | 24 | Succeeded | Same no-BYOK .NET process path. Result 2021 chars, 0 findings (no-evidence-no-finding upheld). |

The successful and failed BYOK SDK event sequences both reached the
client/session message-send phase. That evidence rules out "Copilot SDK never
started" as the primary explanation for the BYOK C1/C2 failure.

The subsequent no-BYOK C1/C2 completions (runs 23-24) confirm that the SDK,
prompt v3, and extractor operate correctly on these two large traces when the
BYOK provider path is removed. Combined with the BYOK
client/session/message-send evidence above, this localizes the 900-second
timeout to the BYOK provider path rather than the SDK, prompt, or extractor.

## Root Cause (External BYOK Provider Timeout)

- The in-PowerShell reflection attempts (runs 21-22) failed because the
  no-BYOK Copilot provider resolves `copilot.exe` relative to the host process
  location; under the PowerShell host it was not found.
- The Local Monitor build output contains
  `runtimes\win-x64\native\copilot.exe`. Running the runner as the Local
  Monitor .NET process from `bin\Debug\net10.0` lets the no-BYOK provider find
  that runtime, and C1/C2 then completed quickly (runs 23-24).
- Because the same two large traces complete promptly under the no-BYOK path
  but time out at 900 seconds under BYOK, the timeout is localized to the BYOK
  provider path (GitHub Copilot/provider response time or large-trace handling),
  not to the SDK, prompt v3, or the deterministic extractor. This is recorded
  as an external provider caveat and is not treated as a Sprint20
  implementation blocker.

## A/B Run Table

| Ref | Trace ID | Sprint19 M5 baseline | Sprint20 M5 run | Sprint20 status | Finding count | Categories recorded | Citation verdict | Grounding verdict | Coupling verdict |
| --- | --- | ---: | --- | --- | ---: | --- | --- | --- | --- |
| A1 | `3de778a823c30e19a55f32189b9d9983` | Succeeded | 12 (BYOK) | Succeeded | 1 | `goal-clarity` | Pass: 8/8 cited spans resolved | Pass: extractor/raw-verified citation present | Pass |
| A2 | `3a9a195df2ea4a3390db205ac79e3929` | Succeeded | 13 (BYOK) | Succeeded | 2 | `goal-clarity`, `ambiguity` | Pass: 5/5 cited spans resolved | Pass: extractor/raw-verified citations present | Pass |
| B1 | `d646a7c4b389fd22d5b79741149eea45` | Succeeded | 16 (BYOK) | Succeeded | 1 | `missing-context-constraints` | Pass: 6/6 cited spans resolved | Pass: extractor/raw-verified citation present | Pass: Sprint19 B1 finding 3/4 equivalent category-coupling deviations did not recur as findings |
| B2 | `079b1d0a0e2a238d57f205a21320c229` | Succeeded | 17 (BYOK) | Succeeded | 1 | `ambiguity` | Pass: 2/2 cited spans resolved | Pass: extractor/raw-verified citation present | Human comparison pending: category differs from Sprint19 baseline (not re-scored in this update) |
| C1 | `432da7a06c1115e51f32bf42f65de9b6` | Succeeded | 23 (no-BYOK) | Succeeded | 1 | `missing-context-constraints` | Pass: 4/4 cited spans resolved | Pass: `errorSpans`/`retryChains` extractor-grounded | Pass |
| C2 | `06803b4012e9904cc927ebce99088282` | Succeeded | 24 (no-BYOK) | Succeeded | 0 | none (no finding) | Pass: 6/6 evidence-summary spans resolved | N/A (no finding) | N/A: `task-size-split` not emitted (no multi-goal `user_instruction` descriptor; correctly judged non-applicable) |

Run 18 for C1 remains an ignored orphaned attempt (`running`) caused by user
interruption. It is recorded as validation evidence only; the DB was not
edited.

Note on the earlier record: the first quick read of C2 reported a
`task-size-split` category. That was a misread of a non-applicable category
evaluation row, not an emitted finding. The corrected C2 result is **zero
findings** with the `task-size-split` category explicitly judged
non-applicable (no multi-goal `user_instruction` descriptor to ground it).

## Gate Assessment

| Gate | Verdict | Evidence |
| --- | --- | --- |
| Citation existence | Pass (all six) | A1 8/8, A2 5/5, B1 6/6, B2 2/2, C1 4/4 cited spans resolved; C2 emits no finding but its 6/6 evidence-summary span refs resolve. |
| Trace specificity | Pass (findings-bearing runs) | A1/A2/B1/B2/C1 cite trace-local evidence; C2 emitted no finding (N/A). |
| No-evidence-no-finding | Pass (all six) | No run emitted a finding without a citation; C2 correctly emitted 0 findings. |
| Extractor or raw-verified grounding | Pass (findings-bearing runs) | A1/A2/B1/B2 and C1 grounded (C1 in `errorSpans`/`retryChains`); C2 N/A (no finding). |
| No recurrence of Sprint19 B1 coupling deviations | Pass | B1 run 16 did not emit equivalents of Sprint19 B1 finding 3/4 category-coupling deviations. |
| Provider parity (same 6 traces via same BYOK path) | Caveat accepted | C1/C2 completed under the SDK default no-BYOK provider and a different model, not the BYOK glm-5.2 path used for A1/A2/B1/B2 and the Sprint19 baseline. The BYOK C1/C2 timeout is treated as an external GitHub Copilot/provider issue, not a product blocker. |
| Recall signal | Not a clean verdict | Sprint20 produced 6 findings across the six traces (A1 1, A2 2, B1 1, B2 1, C1 1, C2 0) vs 9 in Sprint19. B1 intentionally drops the 2 prior coupling deviations, and C1/C2 ran under a different provider/model, so the comparison is confounded; no material recall collapse is evident on the comparable BYOK runs. |

Final gate verdict: **Conditional pass / Phase 2 GO with provider caveat.**
Prompt v3 + the deterministic extractor produced grounded, coupled findings
(C1) and a correct no-evidence-no-finding result (C2), and no completed run
reproduced the Sprint19 B1 coupling deviations. The provider parity caveat is
explicitly recorded because C1/C2 completed under the SDK default no-BYOK
provider and a different model. Per validation decision, the BYOK C1/C2 timeout
is a GitHub Copilot/provider-path issue outside the Sprint20 implementation,
so it does not block M5 closeout or the Issue #46 Phase 2 step-1 GO update.

## Issue #46 Comment Draft

Repository-safe draft, not posted:

> Sprint20 M5 A/B live validation update. All six preserved Sprint19 traces
> now have results under prompt v3 with the deterministic
> instruction-evidence extractor. A1/A2/B1/B2 ran under the validated BYOK
> path (glm-5.2); C1/C2 were completed under the SDK default (no-BYOK)
> Copilot provider after the BYOK runs timed out at 900 seconds.
>
> Result: conditional pass / Phase 2 GO with provider caveat.
> C1 produced one `missing-context-constraints` finding grounded in the
> extractor's error/retry evidence (4/4 cited spans resolved). C2 correctly
> produced no finding (no multi-goal user-instruction descriptor for
> `task-size-split`; no-evidence-no-finding upheld, 6/6 evidence refs
> resolved). The Sprint19 B1 category-coupling deviations did not recur.
>
> C1/C2 were completed under a different provider and model than the BYOK path
> used by A1/A2/B1/B2, so provider parity is recorded as a caveat. Evidence
> localizes the 900-second BYOK C1/C2 timeout to the GitHub
> Copilot/provider path (the no-BYOK runs completed quickly and the
> SDK/prompt/extractor behaved correctly), not to the Sprint20 implementation.
> With that caveat recorded, Sprint20 Phase 2 step 1 is ready to proceed.
>
> No raw prompts/responses, tool arguments/results, credentials, or full
> result markdown are included in the repository evidence.

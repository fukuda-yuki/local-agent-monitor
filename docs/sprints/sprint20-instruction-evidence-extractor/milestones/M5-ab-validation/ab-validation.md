# Sprint20 M5 A/B Live Validation

Date: 2026-07-07

Verdict: **Blocked**. Sprint20 M5 cannot be closed as PASS because the
revised C1 and C2 runs timed out after the 900-second BYOK analysis timeout.
The completed A1/A2/B1/B2 runs show the SDK path is active and producing
grounded results, so the observed failure is not "SDK did not start."

## Environment

- Repository branch: `feat/sprint20`
- Local Monitor DB:
  `C:\Users\mwam0\Documents\Codex\copilot-agent-observability\src\CopilotAgentObservability.LocalMonitor\artifacts\sprint19-live-validation\monitor-live.db`
- Analysis focus: `instruction-diagnosis`
- Provider configuration labels: BYOK, `Provider:Type=openai`,
  `WireApi=completions`, model `glm-5.2`, OpenAI-compatible OpenCode Go
  endpoint
- Timeout: originally 600 seconds; revised validation used 900 seconds
- Monitor execution: built Local Monitor executable, raw-default mode,
  loopback URL `http://127.0.0.1:4320`, one PowerShell orchestration per
  validation attempt

Secrets, API key values, provider base URL, raw prompts, raw responses, tool
arguments/results, and full analysis markdown are intentionally omitted.

## Iteration Log

| Step | Trace | Run | Outcome | Notes |
| --- | --- | ---: | --- | --- |
| Initial launcher | n/a | n/a | Failed before analysis | The startup wrapper attempted to write under the user's AppData log location and was blocked by local permissions. |
| `dotnet run` launcher | n/a | n/a | Failed before analysis | Restore was attempted and failed with `NU1301`; this was treated as an environment failure, not validation evidence. |
| Direct long-lived monitor | B1 | 14 | Failed | SDK reached message send, then timed out after 600 seconds. |
| Direct long-lived monitor | B1 | 15 | Failed | Second 600-second timeout. |
| Timeout revision | n/a | n/a | Applied | `CopilotAnalysis:TimeoutSeconds` was raised to 900 seconds. |
| Direct long-lived monitor | B1 | 16 | Succeeded | Reused as Sprint20 M5 B1 evidence. |
| Direct long-lived monitor | B2 | 17 | Succeeded | Reused as Sprint20 M5 B2 evidence. |
| Interrupted monitor | C1 | 18 | Orphaned | User interruption left the DB row in `running`; the row was not mutated. |
| Revised orchestration | C1 | 19 | Failed | SDK reached message send, then timed out after 900 seconds. |
| Revised orchestration | C2 | 20 | Failed | SDK reached message send, then timed out after 900 seconds. |

The successful and failed SDK event sequences both reached the client/session
message-send phase. That evidence rules out "Copilot SDK never started" as the
primary explanation for M5 failure.

## A/B Run Table

| Ref | Trace ID | Sprint19 M5 baseline | Sprint20 M5 run | Sprint20 status | Finding count | Categories recorded | Citation verdict | Grounding verdict | Coupling verdict |
| --- | --- | ---: | ---: | --- | ---: | --- | --- | --- | --- |
| A1 | `3de778a823c30e19a55f32189b9d9983` | Succeeded | 12 | Succeeded | 1 | `goal-clarity` | Pass: 8/8 cited spans resolved | Pass: extractor/raw-verified citation present | Pass |
| A2 | `3a9a195df2ea4a3390db205ac79e3929` | Succeeded | 13 | Succeeded | 2 | `goal-clarity`, `ambiguity` | Pass: 5/5 cited spans resolved | Pass: extractor/raw-verified citations present | Pass |
| B1 | `d646a7c4b389fd22d5b79741149eea45` | Succeeded | 16 | Succeeded | 1 | `missing-context-constraints` | Pass: 6/6 cited spans resolved | Pass: extractor/raw-verified citation present | Pass: Sprint19 B1 finding 3/4 equivalent category-coupling deviations did not recur as findings |
| B2 | `079b1d0a0e2a238d57f205a21320c229` | Succeeded | 17 | Succeeded | 1 | `ambiguity` | Pass: 2/2 cited spans resolved | Pass: extractor/raw-verified citation present | Needs final human comparison; category differs from Sprint19 baseline |
| C1 | `432da7a06c1115e51f32bf42f65de9b6` | Succeeded | 19 | Failed: 900-second timeout | 0 | n/a | Not scored | Not scored | Not scored |
| C2 | `06803b4012e9904cc927ebce99088282` | Succeeded | 20 | Failed: 900-second timeout | 0 | n/a | Not scored | Not scored | Not scored |

Run 18 for C1 remains an ignored orphaned attempt (`running`) caused by user
interruption. It is recorded as validation evidence only; the DB was not
edited.

## Gate Assessment

| Gate | Verdict | Evidence |
| --- | --- | --- |
| Citation existence | Partial pass | A1/A2/B1/B2 completed and all cited span IDs resolved. C1/C2 did not produce results. |
| Trace specificity | Partial / human-gated | Completed runs cite trace-local evidence. C1/C2 cannot be judged. |
| No-evidence-no-finding | Partial pass | Completed runs did not emit findings without citations. C1/C2 cannot be judged. |
| Extractor or raw-verified grounding | Partial pass | Completed runs include extractor-grounded or raw-verified span citations. C1/C2 cannot be judged. |
| No recurrence of Sprint19 B1 coupling deviations | Pass for completed B1 run | B1 run 16 did not emit equivalents of Sprint19 B1 finding 3/4 category-coupling deviations as findings. |
| Recall signal | Incomplete | Completed Sprint20 runs A1/A2/B1/B2 produced 5 findings against 7 corresponding Sprint19 findings. Because B1 intentionally suppresses the prior coupling deviations and C1/C2 are missing, this is not a final recall verdict. |

Final gate verdict: **Blocked / no Issue #46 GO update** until C1 and C2 can
complete, or until the timeout cause is understood and documented as an
accepted limitation.

## Issue #46 Comment Draft

Repository-safe draft, not posted:

> Sprint20 M5 A/B live validation was re-run against the preserved Sprint19
> six-trace DB using the Local Monitor BYOK raw analysis path and prompt v3
> with the deterministic instruction-evidence extractor.
>
> Current result: blocked, not PASS. A1/A2/B1/B2 completed successfully. Their
> findings had resolvable citations, and the Sprint19 B1 category-coupling
> deviations did not recur in the completed B1 run. C1 and C2 timed out after
> the revised 900-second analysis timeout, so the full six-trace M5 gate cannot
> be closed.
>
> The DB event evidence shows the SDK reached client/session/message-send
> phases, so this is not an SDK-startup failure. It is a large-trace or
> provider-response-time blocker in the BYOK live validation path. Before
> raising the timeout beyond 900 seconds, the next step should inspect why
> C1/C2 require more time or reduce the analysis payload/tool loop for large
> traces.
>
> No raw prompts/responses, tool arguments/results, credentials, or full result
> markdown are included in the repository evidence.


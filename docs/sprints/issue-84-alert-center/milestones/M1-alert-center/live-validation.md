# Issue #84 M1 Alert Center validation evidence

This is historical candidate evidence, not a product specification. It
contains no prompts, responses, tool arguments/results, credentials, raw
records, sensitive paths, or content-enabled capture output.

## Evidence boundary

| Field | Record |
| --- | --- |
| Functional candidate | `b1fb801101f25ff5d8716a56a8b0ff9c95cc988e` |
| Date / OS | 2026-07-23 / Windows x64 |
| Runtime | Repository-pinned .NET SDK; exact build result recorded below |
| Browser | Repository-local Playwright Chromium bootstrap and headless Chromium |
| Data | Disposable loopback hosts, temporary SQLite databases, and sanitized synthetic metadata only |
| Capture policy | No genuine producer execution and no raw/content-enabled capture |

## `91-A-084` automated gate

The focused suite exercises the #80 typed read boundary, strict owner cursor
and page guards, the 2,000-receipt incomplete snapshot, exact evidence and
source/version joins, lifecycle reconstruction, recurring scope, suppression
coverage, explicit source-gated evaluation, restart reconstruction, and the
Alert Center's bounded UI pagination. The candidate passed this gate without
failed or skipped tests.

## `91-S-084` security gate

HTTP and Chromium cases cover metadata-only DTOs in raw-default and
`--sanitized-only` modes, `Cache-Control: no-store`, loopback Host validation,
cross-site denial, CSRF enforcement, strict query/body/media limits, inert text
rendering, missing/expired/unknown evidence, and stale lifecycle revisions.
The candidate passed this gate without failed or skipped tests.

## `91-L-084` live provider gate

Classification: `blocked_external` / severity `high`.

No genuine provider evaluation was started. The frozen #61 compatibility
authority contains no named reviewed source/version adapter that grants the
required Alert Center rule capabilities for GitHub Copilot or Claude Code.
The production evaluator therefore remains suppression-only for admitted
exact partitions and cannot provide honest positive receipt evidence for
either provider.

- Blocker: no named #61 manifest/source-version adapter authorizes the
  required capabilities for either provider.
- Retry condition: promote named reviewed mappings, freeze a new candidate,
  and rerun exact explicit evaluation plus canonical receipt readback for both
  providers.
- Unverified capability: genuine positive alert receipt generation and Alert
  Center display for GitHub Copilot and Claude Code.

Synthetic fixtures prove deterministic behavior but are not substituted for
this live row. No prompt, response, tool payload, credential, raw record, or
content-enabled capture was created for the blocked attempt.

## Candidate validation commands

All commands below were executed from exact functional SHA
`b1fb801101f25ff5d8716a56a8b0ff9c95cc988e` unless noted otherwise.

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter "FullyQualifiedName~AlertCenter|FullyQualifiedName~AlertLifecycle|FullyQualifiedName~MonitorOverview" --logger "console;verbosity=minimal"
dotnet test tests\CopilotAgentObservability.Alerts.Tests\CopilotAgentObservability.Alerts.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~AlertReceiptConsumerV1Tests
dotnet test tests\CopilotAgentObservability.Alerts.Tests\CopilotAgentObservability.Alerts.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~AlertCenterReceiptConsumerV1Tests|FullyQualifiedName~AlertEvaluationApplicationTests|FullyQualifiedName~SqliteAlertEngineQueryStoreTests"
dotnet test tests\CopilotAgentObservability.Alerts.Tests\CopilotAgentObservability.Alerts.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~AlertLifecycleDomainTests|FullyQualifiedName~SqliteAlertLifecycleStoreTests"
dotnet build CopilotAgentObservability.slnx --no-restore
pwsh scripts\agent\sync-claude-skills.ps1 -Check
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
pwsh scripts\validation\issue-91\validate-matrix.ps1 -MatrixPath docs\sprints\issue-84-alert-center\validation-matrix.json
```

Results:

- Alert Center, Alert Lifecycle, and Monitor Overview focused gate: 106/106,
  zero failed or skipped;
- #80 strict receipt consumer: 49/49;
- #80 Wave 3 typed consumer/application/query gate: 33/33;
- #83 lifecycle owner gate: 79/79;
- isolated Alert Center filter: 79/79, and the deterministic overview
  raw-default/sanitized regression: 2/2;
- build: 0 warnings and 0 errors;
- Claude skill mirror: 5/5;
- Playwright Chromium bootstrap: passed;
- full solution: 7889/7889, zero skipped — InstructionFindings 20, Alerts
  451, Doctor 266, Config CLI 4522, Local Monitor 2630;
- Issue #91 scanner self-test: 118 transformations and 5 negative cases,
  passed; Issue #84 evidence scan: 3 files, 354 variants, zero matches;
- matrix validator: three rows,
  `release_ready_with_external_blockers`, passed.

## Required failure and correction history

The original `c6e2af8` candidate failed the full suite 7546/7547 because the
raw-default overview timed out waiting for the prompt label. The intermediate
`7c37dd39bee079cbeb8664fbf24f4d891a420c8e` candidate passed 7547/7547 but was
superseded after deterministic request-order testing exposed the deeper race.

The new regression held the seven-day TOP5 response and proved that Alert
Center requests had already started; before the fix both raw-default and
sanitized cases failed 0/2. Instrumentation recorded the overview request as
200 while the TOP5 trace-list request returned 503 `persistence_busy`, with no
TOP5 DOM mutation or prompt-label request. The correction orders overview,
then TOP5, then Alert Center; serializes the three Alert Center reads; and keeps
the generation and abort guards. It passed once as 2/2 and five repeated runs
as 10/10 before the final candidate gates above.

The final implementation review also corrected five-part overview semantics,
lifecycle history, filters/facets, stale request generations, custom dates and
units, generic Session/Trace/Event evidence, bounded coverage with an explicit
incomplete state, unsafe raw/path/token scope removal, exact labels,
accessibility, and the Playwright matrix. No P0–P2 finding remained in the
final static review.

## Extension and publication state

Rows `91-A-084`, `91-S-084`, and `91-L-084` are present in the Issue #84
matrix. The Alert Center placeholder is removed from the #91 future registry;
the registry is not used to claim an active pass. The revisions and evidence
are local only; no push or pull request was performed.

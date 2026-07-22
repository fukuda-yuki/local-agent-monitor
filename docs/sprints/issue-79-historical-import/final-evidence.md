# Issue #79 Final Candidate Evidence — FINAL

This repository-safe record contains no credentials, authorization values,
prompt/response bodies, tool arguments/results, PII, source database content,
reversible markers, or machine-sensitive paths.
No prior Issue #91 pass was inherited.

## Candidate boundary

| Field | Value |
| --- | --- |
| Work item | Issue #79 historical import |
| Wave 3 kickoff SHA | `c02c10ab18553acef1619ce12ec630f4f6f5aa5f` |
| Exact functional validation SHA | `071b00e319de86cfa842371ee745025f3f2cfe96` |
| Dependencies | #76 `f7dfe70`, #77 `1086409`, #78 `810d9a`, plus accepted retention/mutation contracts |
| Environment | Windows native; deterministic synthetic fixtures, disposable SQLite databases, loopback-only hosts, Playwright Chromium |

## 91-I-079 — `passed`

Fresh execution from the exact functional SHA passed:

- Config CLI `FullyQualifiedName~HistoricalImport`: 83/83, zero skipped;
- Local Monitor `FullyQualifiedName~HistoricalImport`: 107/107, zero skipped;
- Claude skill mirror: 5/5;
- solution build: 0 warnings, 0 errors;
- Playwright Chromium bootstrap;
- full solution: 7809/7809, zero skipped — InstructionFindings 20,
  Alerts 451, Doctor 266, Config CLI 4522, Local Monitor 2550.

The focused corpus covers the v1 schemas, admitted source gateways, preview and
confirmation binding, durable queued/running/terminal state, stale/changed
preview rejection, all transactional rollback injection stages, exact identity
deduplication, explicit conflict preservation, retention ownership, history,
CLI/API parity, raw-default and sanitized-only UI journeys, fresh database,
the real pre-Issue-79 session-v10 upgrade fixture, accepted #73 component
coexistence, and rollback to the pre-import state.

## 91-S-079 — `passed`

The Issue #91 scanner self-test passed 118 transformation cases and 5 negative
cases. Contract fixture scanning passed for 21 files, 2478 variants, and zero
matches. Issue #79 evidence scanning passed for 2 files, 236 variants, and zero
matches. The focused/full suites additionally cover metadata-only DTOs,
include-content denial, same-origin and `no-store` responses, inert rendering,
private five-minute locator expiry/deletion, bounded output, path redaction,
and absence of a parallel retention lifecycle.

The first multi-directory scanner invocation passed a comma-delimited string to
the child PowerShell process and returned `required_target_missing`. The command
was corrected to pass a real two-element array; that exact retry produced the
21-file/2478-variant clean result above. The initial failure is not represented
as a successful attempt.

## 91-L-079 — `blocked_external`

No source owner has promoted a production-supported fixture tuple for a real
historical producer, so no live producer run was attempted. Retry requires an
exact promoted profile/adapter/source-version/fixture-digest/schema-fingerprint
tuple followed by discovery, preview, confirmed import, bounded result
inspection, and leak scanning. This does not block the deterministic production
surface or convert the live row into a pass.

## Platform-limited scope

The Unix SQLite descriptor-replacement/ABA regression test is present and its
descriptor-binding code and test were independently accepted, but its body is
platform-gated on this Windows host. Ubuntu under WSL was inspected but had no
`dotnet`; the Docker daemon was unavailable. Dynamic Unix execution therefore
remains unverified on this machine and was not substituted with another test.

## Exact commands

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~HistoricalImport
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~HistoricalImport
pwsh scripts\validation\issue-91\validate-matrix.ps1 -MatrixPath docs\sprints\issue-79-historical-import\validation-matrix.json
pwsh -NoProfile -File scripts\validation\issue-91\test-scan-outputs.ps1
pwsh -NoProfile -Command "& 'scripts/validation/issue-91/scan-outputs.ps1' -InputPath @('docs/specifications/contracts/historical-import-workflow/v1/fixtures','docs/specifications/contracts/historical-import/v1/fixtures') -OutputType evidence"
pwsh -NoProfile -File scripts\validation\issue-91\scan-outputs.ps1 -InputPath docs\sprints\issue-79-historical-import -OutputType evidence
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

The matrix decision is `release_ready_with_external_blockers`: automated and
security rows are passed, while required live row `91-L-079` remains exact
`blocked_external` evidence.

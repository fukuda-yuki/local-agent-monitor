# Issue #79 Final Candidate Evidence — DRAFT

This is a repository-safe preparation record, not final validation evidence.
It contains no credentials, authorization values, prompt/response bodies, tool
arguments/results, PII, source database content, reversible markers, or
machine-sensitive paths.

## Candidate boundary

| Field | Draft value |
| --- | --- |
| Work item | Issue #79 historical import |
| Matrix preparation SHA | `c02c10ab18553acef1619ce12ec630f4f6f5aa5f` |
| Placeholder final validation SHA | `c02c10ab18553acef1619ce12ec630f4f6f5aa5f` |
| Final candidate state | PENDING |
| Environment | Windows native; deterministic synthetic fixtures, disposable SQLite databases, and loopback-only test hosts |

The placeholder final SHA and every nonterminal result must be replaced after
the functional candidate is frozen. No prior Issue #91 pass is inherited.

## 91-I-079

Classification: `not_attempted`.

The final run must cover these concrete test classes through the Local Monitor
or Config CLI test projects as applicable:

- `HistoricalImportWorkflowContractTests`
- `HistoricalImportStoreTests`
- `HistoricalImportApplicationTests`
- `HistoricalImportGatewayTests`
- `HistoricalImportCliTests`
- `HistoricalImportRouteTests`
- `HistoricalImportUiPlaywrightTests`

Required evidence: exact final SHA, discovered/executed/passed counts with zero
skip, transactional rollback, exact deduplication and idempotency, stale
preview rejection, provenance/conflict preservation, API/CLI parity, and the
Playwright preview-to-result journey. Result: **PENDING**.

## 91-S-079

Classification: `not_attempted`.

Required evidence: exact final SHA security-focused tests, Issue #91 secret
corpus/scanner coverage, same-origin and no-store checks, metadata-only output,
include-content rejection, no parallel retention lifecycle, inert UI rendering, and separate
repository-artifact/application-output scans. Result: **PENDING**.

## 91-L-079

Classification: `blocked_external`.

No source-owner-promoted production fixture tuple currently exists. A source
owner must first promote the exact profile, adapter, source version, format,
fixture digest, schema fingerprint, and golden test tuple. Then a real producer
run must execute discovery, preview, confirmed import, result inspection, and
a repository-safe leak scan. No real producer run is claimed in this draft.

## Pending commands

Run from the repository root at the frozen final candidate. Every command and
count below is currently **PENDING**:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~HistoricalImport
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~HistoricalImport
pwsh scripts\validation\issue-91\validate-matrix.ps1 -MatrixPath docs\sprints\issue-79-historical-import\validation-matrix.json
pwsh -NoProfile -File scripts\validation\issue-91\test-scan-outputs.ps1
pwsh -NoProfile -File scripts\validation\issue-91\scan-outputs.ps1 -InputPath docs\specifications\contracts\historical-import-workflow\v1\fixtures,docs\specifications\contracts\historical-import\v1\fixtures -OutputType evidence
pwsh -NoProfile -File scripts\validation\issue-91\scan-outputs.ps1 -InputPath docs\sprints\issue-79-historical-import -OutputType evidence
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

The final evidence update must replace both SHA fields, record bounded exact
results, classify `91-I-079` and `91-S-079`, preserve `91-L-079` as
`blocked_external` until its retry condition is met, and recompute the release
decision without converting a retry, blocker, or historical observation into a
pass.

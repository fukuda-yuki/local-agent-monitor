# Issue #86 Final Candidate Evidence — FINAL

This repository-safe record contains no credentials, authorization values,
prompt/response bodies, tool arguments/results, PII, source database content,
reversible markers, raw bundle content, or machine-sensitive paths. No prior
Issue #91 pass was inherited.

## Candidate boundary

| Field | Value |
| --- | --- |
| Work item | Issue #86 transactional sanitized evidence import |
| Wave 3 kickoff SHA | `c02c10ab18553acef1619ce12ec630f4f6f5aa5f` |
| Exact functional validation SHA | `60c635970f5448fabed3bb96478208e37d3dfb95` |
| Accepted input | #85 `37a095d7c11dd180851e75bdcb290f0894ba01d5` plus evidence revision `56c2033257a04751bc52468efa249c4058b20e7d` |
| Migration order | `historical_import.v1 -> sanitized_import.v1` in one caller transaction |
| Migration hash | `6f6ddfa063bc69f874b7540d1a0b1c9ebc8e5180f49b3a6f23220998606f9a42` |
| Environment | Windows native; deterministic synthetic sanitized bundles, disposable SQLite databases, loopback-only hosts, Playwright Chromium |

## 91-I-086 — `passed`

Fresh execution from the exact functional SHA passed:

- Config CLI `FullyQualifiedName~SanitizedImport`: 12/12, zero skipped;
- Local Monitor `FullyQualifiedName~SanitizedImport`: 101/101, zero skipped;
- Claude skill mirror: 5/5;
- solution build: 0 warnings, 0 errors;
- Playwright Chromium bootstrap;
- full solution: 8079/8079, zero skipped — InstructionFindings 20,
  Alerts 451, Doctor 266, Config CLI 4586, Local Monitor 2756.

The focused corpus proves strict #85 archive reinspection, preview/digest/state
binding, all-or-nothing commit, exact record identity, idempotent replay,
same-ID/different-content conflict, append-only origin/provenance receipts,
closed evidence graphs, retention `not_applicable`, bounded API/history output,
and six browser journeys including overlapping and ambiguous responses.

Fresh and supported-upgrade tests prove the intentional forward order from a
real pre-#79 Session v10 fixture, a pre-#86 database containing historical
import rows, the supported Monitor v7 / Session v13 vector, idempotent repeated
initialization, and rollback that preserves historical rows while removing all
sanitized-import schema work.

## 91-S-086 — `passed`

The focused and full suites reject traversal, malformed/truncated ZIP data,
duplicate paths and keys, invalid UTF-8 names, CRC/checksum mismatch, oversize
and graph bounds, forbidden carriers/fields, attached objects, weakened DDL,
off-namespace triggers, incomplete owner receipts, unresolved graph corruption,
foreign-key corruption, stale previews, and every injected transaction failure
without partial writes or owner/raw-retention mutation.

The Issue #91 scanner self-test passed 118 transformation cases and 5 negative
cases. Sanitized-evidence contract scanning passed for 3 files, 354 variants,
and zero matches. The final matrix and this ledger passed for 2 files, 236
variants, and zero matches.

## 91-L-086 — `blocked_external`

Only the current Windows machine was available. Same-machine path/database
relocation passed the source-neutral logic it can observe, but the canonical
contract expressly says that this is not genuine second-machine evidence.
Retry requires a separately provisioned second Windows machine, byte-identical
bundle transfer, and strict preview, commit, replay, history, graph, and
retention-boundary observation there. Cross-machine filesystem, runtime,
timestamp, and local-identity compatibility remains unverified.

## Unsupported schema and archive cases

The candidate fails closed for future `historical_import` or `sanitized_import`
versions, unstamped/partial owned namespaces, altered table or index shape,
missing indexes, off-namespace executable objects, unrelated foreign-key
corruption, unsupported bundle/carrier versions, raw OTLP, runtime backups,
embedded executable content, and incomplete or conflicting graph ownership.
No reverse `#86 -> #79` migration promise or heuristic merge was added.

## Corrected validation failures

The first post-rebase build failed with CS8752 in one alert fixture; the element
type was made explicit. The first focused Config CLI run passed 10/12 and found
two corruption fixtures blocked by active SQLite foreign keys; the fixtures now
explicitly disable enforcement before injecting invalid rows and pass 12/12.
The first focused Local Monitor run passed 100/101 and exposed a request-limit
exception that reached ASP.NET exception middleware and appended `no-cache` to
the required `no-store` header. The body reader now maps that exception inside
the route; the regression passed alone and the exact focused rerun passed
101/101. None of those failed attempts is represented as a successful run.

## Exact commands

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --no-restore --filter FullyQualifiedName~SanitizedImport
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter FullyQualifiedName~SanitizedImport
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx --no-restore
pwsh -NoProfile -File scripts\validation\issue-91\validate-matrix.ps1 -MatrixPath docs\sprints\issue-86-sanitized-import\validation-matrix.json
pwsh -NoProfile -File scripts\validation\issue-91\test-scan-outputs.ps1
pwsh -NoProfile -File scripts\validation\issue-91\scan-outputs.ps1 -InputPath docs\sprints\issue-86-sanitized-import -OutputType evidence
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter FullyQualifiedName~Issue91ValidationContractTests
```

The matrix decision is `release_ready_with_external_blockers`: automated and
security rows are passed, while required live row `91-L-086` remains exact
`blocked_external` evidence.

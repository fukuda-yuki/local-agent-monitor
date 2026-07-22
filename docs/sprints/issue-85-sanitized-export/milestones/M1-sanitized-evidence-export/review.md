# Issue #85 M1 — Accepted Functional Evidence Review

Issue #85 is accepted at functional execution SHA
`56c2033257a04751bc52468efa249c4058b20e7d` (tree
`b9e1fc31dcd6899cecc34c858b5c643e4bc81dd3`). Both Issue #91 rows are
`passed`, and the Issue #85 release decision is `release_ready`. This review is
an evidence-only follow-up: it records commands already executed from that
unchanged functional SHA and does not identify its own documentation commit as
the execution SHA.

Scope: trusted read-only SQLite snapshot capture, deterministic sanitized bundle
creation and inspection, Config CLI commands, Local Monitor loopback routes,
canonical schema/golden fixtures, fail-closed scanner and archive validation,
shared #59 and #80 producer carriers, current specifications, and focused/full
regression evidence. No package dependency, lockfile, database migration,
remote operation, upload, signing, encryption, import, replay, backup, or
restore behavior was added.

## Exact functional validation

The repository was clean at
`56c2033257a04751bc52468efa249c4058b20e7d` before the acceptance commands.
These required commands were executed from that exact SHA:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~SanitizedExportAuthorizationTests|FullyQualifiedName~SanitizedExportServiceTests|FullyQualifiedName~SanitizedExportSurfaceTests|FullyQualifiedName~SqliteSanitizedExportSnapshotProviderTests|FullyQualifiedName~Issue91ValidationContractTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~SanitizedExportCliTests
dotnet test tests\CopilotAgentObservability.InstructionFindings.Tests\CopilotAgentObservability.InstructionFindings.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~InstructionFindingHandoffConsumerV1Tests
dotnet test tests\CopilotAgentObservability.Alerts.Tests\CopilotAgentObservability.Alerts.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~AlertReceiptConsumerV1Tests
```

Observed results:

- Claude skill mirror: up to date, five shared skills.
- Solution build: succeeded with zero warnings and zero errors.
- Playwright Chromium bootstrap: succeeded.
- Full solution: 7,216 passed, zero failed, zero skipped. Project totals were
  InstructionFindings 20, Alerts 339, Doctor 266, ConfigCli 4,374, and
  LocalMonitor 2,217.
- LocalMonitor sanitized export and Issue #91 focus: 86 passed, zero failed,
  zero skipped.
- ConfigCli sanitized export focus: 10 passed, zero failed, zero skipped.
- Issue #59 handoff consumer: 20 passed, zero failed, zero skipped.
- Issue #80 alert receipt consumer: 49 passed, zero failed, zero skipped.

The ConfigCli project was also rerun alone with `--no-build --no-restore` after
the solution output was truncated in the terminal transcript; it independently
confirmed 4,374/4,374. This diagnostic repetition does not replace the required
full solution command, which itself exited successfully.

## Final review findings closed

Independent quality review rejected implementation candidate
`05d35ff817b34aa386104e3f38aade473e366594`. The accepted functional SHA closes
all four findings with regression coverage:

- snapshot ambiguity is based on distinct session identities, so multiple
  surface rows for the same session/trace remain exportable;
- the public control-request parser enforces the 1 MiB bound before its own
  `ToArray()` copy or JSON materialization;
- all control timestamps use and accept only the exact seven-fraction-digit UTC
  `Z` form; equivalent offsets and variable fractions are rejected before
  snapshot capture;
- repository-safe path scanning detects one-segment and nested POSIX absolute
  paths, including synthetic roots; relative forms such as `docs/file`,
  `./file`, and `../file` do not match, while HTTP(S) URLs fail closed as
  `local_path` through the existing `//` path branch.

Provider selection, deterministic snapshots, unique record identity, surface
selection, CLI/API status mapping, schema patterns, exact golden request bytes,
and pre-capture failure behavior are covered by the accepted tests.

## Preserved review history

The first implementation candidate
`48d3734106fb572c5d8f013f8935c4288147ee23` was rejected because caller-owned
bytes and markers could establish a repository-safe claim without exact trusted
producer validation. Corrected checkpoint
`905b7b750a655daff7cbe73bbf5ad770bf29fce9` closed caller injection and hardened
the bundle, but production snapshot capture, shared #59 validation, and the full
solution gate were incomplete. Its matrix states therefore remained
`not_attempted` and `failed`; those results are retained as historical evidence,
not promoted or overwritten.

Candidate `05d35ff817b34aa386104e3f38aade473e366594` integrated the missing production
and carrier work but failed final quality review on ambiguity identity, parser
allocation timing, timestamp lexical strictness, and one-segment POSIX paths.
The accepted SHA is a distinct follow-up and was validated afresh. No pass is
inherited from any earlier candidate.

## Validation boundary

Evidence uses deterministic synthetic data, disposable SQLite databases,
generated file-system bundles, and server-managed loopback test hosts. It does
not exercise or claim real telemetry, live user data, remote publication,
general DLP, privacy/legal certification, recursive decoding, decryption,
decompression, or secure erasure. Those exclusions are product boundaries, not
unverified requirements for the Issue #85 release decision.

The scanner is a bounded negative scanner. Bundle inspection proves canonical
profiles, framing, inventory, checksums, bounds, and declared scanner rules; it
does not claim source/store provenance beyond the trusted provider capture
boundary.

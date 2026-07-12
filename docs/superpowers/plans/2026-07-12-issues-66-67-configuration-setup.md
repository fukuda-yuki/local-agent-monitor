# Issues #66/#67 Configuration Setup Implementation Plan

> Execute sequentially with `superpowers:subagent-driven-development`. Every
> task uses a fresh implementer and then an independent read-only reviewer.

**Goal:** Deliver the approved `setup.v1` ownership ledger and reversible setup
commands, then the GitHub Copilot VS Code/CLI/App-SDK guided setup.

**Architecture:** Existing Config CLI is the only DTO producer. Private plans,
backups, and journals are separate from the repository-safe ledger. Repository
and Release PowerShell wrappers forward the real producer output. No HTTP,
proxy, UI, database, new project, or new dependency.

**Contract:** `docs/specifications/interfaces/configuration-setup.md`, D057, and
`docs/specifications/security-data-boundaries.md`.

**Exact path roots used below:** `Setup/` means
`src/CopilotAgentObservability.ConfigCli/Setup/`; `tests/.../` means
`tests/CopilotAgentObservability.ConfigCli.Tests/`. These are path aliases, not
wildcards. Every filename following them is an exact owned file.

## Per-task protocol

Every dispatch names purpose, owned files, non-scope, constraints, completion,
verification, and report path. The implementer records RED and GREEN commands in
`artifacts/sdd/<task>-report.md`, updates the durable ledger, and creates the
listed local commit only after review fixes pass. A reviewer inspects the diff
and requirement-to-test mapping; it does not edit. Two consecutive fix cycles
in one area stop patching and trigger contract/test-design re-audit. Tasks are
sequential because contracts and transaction states are shared.

## #66 — Shared ownership framework

### Task 1a — Result DTO and serializer

Own `src/CopilotAgentObservability.ConfigCli/Setup/Contracts/SetupContracts.cs`,
`SetupCodes.cs`, `SetupJson.cs`, and
`tests/CopilotAgentObservability.ConfigCli.Tests/SetupContractShapeTests.cs`.
RED/GREEN:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupContractShapeTests
```

Pin exact fields/casing/enums/order and one `setup.v1` serializer. Commit:
`Issue #66: feat: define setup result contract`.

### Task 1b — Result bounds, redaction, and recovery correlation

Own `Setup/Contracts/SetupContractValidator.cs` and
`tests/.../SetupContractValidationTests.cs` (full prefix:
`tests/CopilotAgentObservability.ConfigCli.Tests/`). RED/GREEN filter
`FullyQualifiedName~SetupContractValidationTests`. Cover target/member/status
bounds, sanitized versions/labels, forbidden path/value/secret markers,
mixed-operation derivation, and requested/recovered IDs. Commit:
`Issue #66: feat: validate setup result safety`.

**Review-discovered dependency gate:** execute Task 8a immediately after the
structural Task 1b work and before Task 1b can receive final approval. Then
resume Task 1b with a narrow integration that calls the runtime canonical
matcher for every non-null `expected_result`. The generic serializer validates
canonical membership only; Task 8b owns target-to-surface pairing. Do not copy
manifest JSON or hard-code fingerprints in the validator.

### Task 2a — Platform contracts and deterministic fake

Own `Setup/Platform/ISetupPlatform.cs`, `SystemSetupPlatform.cs`, and
`tests/.../SetupTestPlatform.cs`, `SetupPlatformTests.cs`. RED/GREEN filter
`SetupPlatformTests`. Expose only #66 filesystem, user environment,
notification, clock, UUID, barrier, and fault seams without sleeps. Registry,
process/version, and endpoint detection are added in Task 8b. No transaction
logic. Commit:
`Issue #66: refactor: add setup platform boundary`.

### Task 2b — Runtime paths and non-waiting lock

Own `Setup/Storage/SetupRuntimePaths.cs`, `SetupLock.cs`, and
`tests/.../SetupRuntimeTests.cs`. RED/GREEN filter `SetupRuntimeTests`. Cover the
exact LocalAppData layout, safe IDs, exclusive non-waiting contention, no retry,
and disposal/reacquire. Commit: `Issue #66: feat: guard setup runtime`.

### Task 2c — Private plan and ledger v1 stores

Own `Setup/Storage/SetupPlanStore.cs`, `SetupLedgerStore.cs`,
`tests/.../SetupStorageTests.cs`, and production-serialized fixtures under
`tests/CopilotAgentObservability.ConfigCli.Tests/Fixtures/Setup/v1/`.
RED/GREEN filter `SetupStorageTests`. Cover plan-first/ledger-second ordering,
orphan plan, missing plan, atomic replace, close/reopen v1, corrupt/unknown
version, and repository-safe output. Do not create v0/migration fixtures.
Commit: `Issue #66: feat: persist setup ownership ledger`.

### Task 3a — JSONC and bounded TOML documents

Own `Setup/Documents/JsoncSettingsDocument.cs`, `TomlSettingsDocument.cs`, and
`tests/.../SetupDocumentTests.cs`. RED/GREEN filter `SetupDocumentTests`. Cover
comments/trailing commas/unrelated-key preservation and only the existing sample
TOML table/scalar subset; malformed/unsupported input fails closed. No package.
Commit: `Issue #66: feat: edit bounded setup documents`.

### Task 3b — Path/hash policy and atomic file step

Own `Setup/Transactions/SetupPathPolicy.cs`, `SetupHash.cs`,
`AtomicFileSetupStep.cs`, and `tests/.../SetupFileStepTests.cs`. RED/GREEN filter
`SetupFileStepTests`. Cover missing versus empty hashes, traversal/UNC/device/
reparse rejection, same-directory temp+flush+replace, backup restore, and faults
before/after each boundary. Failure paths never delete a temp pathname; a
rebinding callback proves foreign replacement bytes survive. Native metadata
tests cover read-only regular files, links, FIFO, socket, and character devices
on Windows/Linux/macOS; another OS fails closed. Commit:
`Issue #66: feat: add atomic setup file step`.

### Task 4 — Current-user environment member step

Own `Setup/Transactions/UserEnvironmentSetupStep.cs`,
`Setup/Platform/ISetupPlatform.cs`, `tests/.../SetupTestPlatform.cs`, and
`tests/.../SetupEnvironmentStepTests.cs`. Extend the platform/fake only if an
exact approved member operation was absent in 2a. RED/GREEN filter
`SetupEnvironmentStepTests`. Cover missing/empty/value states, allowlist hash,
unrelated keys, User-only/no-setx, every member fault boundary, no early
notification, one uninterrupted attempt, and replay after uncertain delivery.
Commit: `Issue #66: feat: add user environment setup step`.

### Task 5a — Journal schema and atomic store

Own `Setup/Storage/SetupTransactionJournalStore.cs`,
`Setup/Transactions/SetupFaultPoint.cs`, and `tests/.../SetupJournalStoreTests.cs`.
RED/GREEN filter `SetupJournalStoreTests`. Pin prepared/started/completed/
committed step records, apply/restore operation, atomic flush, close/reopen, and
corrupt/unknown rejection. Commit:
`Issue #66: feat: persist setup transaction journal`.

### Task 5b — Apply preflight and forward write intents

Own `Setup/Transactions/SetupApplyCoordinator.cs` and
`tests/.../SetupApplyTests.cs`. RED/GREEN filter `SetupApplyTests`. Cover full
preflight before artifacts, per-step started/completed ordering, final desired
verification, successful commit ordering, and applying/applied transitions. Use
barriers/faults only. Commit: `Issue #66: feat: coordinate setup apply`.

### Task 5c — Reverse compensation

Own `Setup/Transactions/SetupApplyCoordinator.cs` and
`tests/.../SetupCompensationTests.cs`. RED/GREEN filter
`SetupCompensationTests`. Cover every forward failure boundary, reverse order,
pre-restore intent, immediate desired/prior/third-party classification,
preserved external edits, and restored/partial transitions. Commit:
`Issue #66: feat: compensate failed setup apply`.

### Task 5d — Restart recovery and reconciliation

Own `Setup/Transactions/SetupRecoveryCoordinator.cs` and
`tests/.../SetupRecoveryTests.cs`. RED/GREEN filter `SetupRecoveryTests`. For
file and each environment member, close/reopen after intent, write, completion,
journal commit, and before ledger commit. Cover prior/desired/mixed/third-party,
committed-ledger lag, failed-recovery projection input, and notification replay.
Commit: `Issue #66: feat: recover setup transactions`.

### Task 6a — Hash-guarded rollback

Own `Setup/Transactions/SetupRollbackCoordinator.cs` and
`tests/.../SetupRollbackTests.cs`. RED/GREEN filter `SetupRollbackTests`. Cover
all-target preflight, no force, rolling_back before restore, per-member restore
intents, barrier edit after preflight, idempotent restart, and rolled_back versus
partial. Commit: `Issue #66: feat: rollback setup change sets`.

### Task 6b — Lifecycle-relative status semantics

Own `Setup/Status/SetupStatusProjector.cs` and
`tests/.../SetupStatusTests.cs`. RED/GREEN filter `SetupStatusTests`. Cover every
lifecycle reference, all-desired/all-previous/desired+previous/third-party/
unavailable aggregate targets, guidance exclusion, partial rollback false, and
change-set aggregation. Commit:
`Issue #66: feat: project setup ownership status`.

### Task 6c — Status filtering, priority, and hard cap

Own `Setup/Status/SetupStatusProjector.cs` and
`tests/.../SetupStatusOrderingTests.cs`. RED/GREEN filter
`SetupStatusOrderingTests`. Cover filter→priority→100 cap, 99/100/101 rows,
timestamps, lowercase UUID ordinal ties, truncated, and recovery-blocking rows.
Commit: `Issue #66: feat: bound setup status history`.

### Task 7a — Adapter registry and four CLI commands

Own `Setup/Adapters/ISetupAdapter.cs`, `Setup/Adapters/SetupAdapterRegistry.cs`,
`Setup/Cli/SetupOptions.cs`, `SetupCommand.cs`, `Cli/CliApplication.cs`,
`Cli/CliHelpText.cs`, and `tests/.../SetupCliTests.cs`. RED/GREEN filter
`SetupCliTests`, then run the full ConfigCli test project. Use a fake adapter
only to prove parsing/dispatch, exact exit mapping, stdout single producer JSON,
fixed stderr, recovery correlation, and mutation block. Commit:
`Issue #66: feat: expose reversible setup commands`.

### Task 7b — Repository PowerShell wrapper

Own `scripts/local-monitor/setup.ps1` and the RequiredScripts/parser/repository-
wrapper cases in
`tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorScriptTests.cs`.
RED/GREEN:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~LocalMonitorScriptTests
```

Invoke the real ConfigCli project, forward exact args/stdout/stderr/exit, and do
not reshape JSON. Commit: `Issue #66: feat: add repository setup wrapper`.

## #67 — GitHub Copilot adapter

### Task 8a — Runtime #61 manifest resource and loader

Own `src/CopilotAgentObservability.ConfigCli/CopilotAgentObservability.ConfigCli.csproj`,
`Setup/Capabilities/SourceCapabilityManifest.cs`,
`SourceCapabilityManifestLoader.cs`, and `tests/.../SourceCapabilityRuntimeTests.cs`.
Embed/link the canonical files
`docs/specifications/contracts/source-capabilities/v1/manifests/github-copilot-vscode.json`
and `github-copilot-cli.json` into the ConfigCli assembly so Release does not
depend on `docs/`. RED/GREEN filter `SourceCapabilityRuntimeTests`; compare the
runtime typed loader against canonical JSON and reject unknown/malformed data.
Do not copy a test fixture. Commit:
`Issue #67: feat: load Copilot capability manifests`.

This task is pulled forward to the Task 1b dependency gate. Its loader provides
semantic canonical matching that ignores JSON whitespace/object-property order
but preserves property names, values, value kinds, and array order. Task 1b
consumes that matcher; Task 8b later consumes target selection.

### Task 8b — Real GitHub Copilot plan producer

Own `Setup/Adapters/GitHubCopilot/GitHubCopilotAdapter.cs`,
`GitHubCopilotDetection.cs`, `GitHubCopilotPolicy.cs`,
`GitHubCopilotEndpointProbe.cs`, `GitHubCopilotTargetRegistry.cs`,
`Setup/Adapters/SetupAdapterRegistry.cs`, `Setup/Platform/ISetupPlatform.cs`,
`tests/.../SetupTestPlatform.cs`, and
`tests/.../GitHubCopilotDetectionTests.cs`.
RED/GREEN filter `GitHubCopilotDetectionTests`. Register the real adapter and
produce a real #66 DTO through `setup plan`; cover typed #61 manifests,
target/version detection, policy>env>user>default, observed/unverified policy,
loopback probe, and apply-time endpoint/version/policy no-artifact failures.
Commit: `Issue #67: feat: detect GitHub Copilot setup targets`.

**Early interface gate:** A separate reviewer invokes the real CLI producer for
Task 8b, parses its `setup.v1` JSON, verifies runtime #61 manifest content and
that the adapter creates production `SetupCommandResult`/target DTO instances
serialized directly by the CLI, and records HTTP/proxy/UI as N/A. A
fake adapter cannot satisfy this gate. Resolve findings before Tasks 9–11.

### Task 9 — VS Code adapter

Own `Setup/Adapters/GitHubCopilot/VsCodeSetupAdapter.cs`,
`Setup/Adapters/GitHubCopilot/GitHubCopilotTargetRegistry.cs`, and
`tests/.../VsCodeSetupAdapterTests.cs`. RED/GREEN filter
`VsCodeSetupAdapterTests`. Register the target and execute real
`setup plan --adapter github-copilot --target vscode`. Cover stable 1.128+/
extension, unsupported older and
Insiders, observable policy sources, JSONC preservation, exact enabled/type/
endpoint members, restart, capture default preservation and explicit warning,
and forbidden credential/resource/client.kind writes. Commit:
`Issue #67: feat: guide VS Code Copilot telemetry`.

### Task 10 — Copilot CLI adapter

Own `Setup/Adapters/GitHubCopilot/CopilotCliSetupAdapter.cs`,
`Setup/Adapters/GitHubCopilot/GitHubCopilotTargetRegistry.cs`, and
`tests/.../CopilotCliSetupAdapterTests.cs`. RED/GREEN filter
`CopilotCliSetupAdapterTests`. Register the target and execute real
`setup plan --adapter github-copilot --target cli`. Cover 1.0.4+, process/user diff, exact enabled/
exporter/endpoint/protocol allowlist, shared-variable warning, explicit capture,
terminal restart, and forbidden keys/headers. Commit:
`Issue #67: feat: guide Copilot CLI telemetry`.

### Task 11 — App/SDK no-write guidance

Own `Setup/Adapters/GitHubCopilot/CopilotSdkGuidanceAdapter.cs`,
`Setup/Adapters/GitHubCopilot/GitHubCopilotTargetRegistry.cs`,
`tests/.../CopilotSdkGuidanceAdapterTests.cs`, and
`tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotSdkTelemetryContractTests.cs`.
The latter uses the existing LocalMonitor project reference and its already
pinned `GitHub.Copilot.SDK` 1.0.4 transitive compile asset; do not add/change a
PackageReference or use network restore as proof. RED/GREEN filters
`CopilotSdkGuidanceAdapterTests` and `CopilotSdkTelemetryContractTests`.
Register the target and execute real
`setup plan --adapter github-copilot --target app-sdk`. Cover
detected/version/no path, no write/rollback, no borrowed manifest, and compile
the exact sample. Commit: `Issue #67: feat: add Copilot SDK setup guidance`.

### Task 12 — Self-contained Release packaging

Own `scripts/local-monitor/package-release.ps1` and the package/layout/
executable cases in `LocalMonitorScriptTests.cs`. The existing
`.github/workflows/local-monitor-release.yml` already invokes the package script
and is explicitly not modified by this task.
RED/GREEN filter `LocalMonitorScriptTests`. Extend its deterministic temp output
helper to run the real package script, assert `app/config-cli/`, `setup.ps1`, and
manifest layout, invoke the self-contained published ConfigCli executable
directly (not through `dotnet`) to prove no installed runtime is required,
and compare parsed DTO shape with repository wrapper output. Do not alter other
install/startup/user-env scripts. Commit:
`Issue #67: feat: package guided setup workflow`.

## Documentation, reviews, and integration

### Task 13a — Exact user and operator workflow

Own `README.md`, `scripts/local-monitor/README.md`,
`docs/user-guide/local-monitor.md`,
`docs/agent-guides/repository-workflow.md`, and
`docs/specifications/interfaces/config-cli.md`. Update only implemented behavior
and examples; never claim first-trace success. Review-only, then commit:
`Issues #66-#67: docs: document guided setup workflow`.

### Task 13b — Executable requirement matrix and durable closeout

Own `tests/CopilotAgentObservability.ConfigCli.Tests/ConfigurationSetupContractTests.cs`,
`docs/sprints/sprint23-configuration-ownership-github-copilot/README.md`,
`ledger.md`, and `docs/task.md`. RED/GREEN filter
`ConfigurationSetupContractTests`; it maps every acceptance row to the real
focused test/producer contract without copying DTO fixtures. Run all test
classes matching `Setup|GitHubCopilot|VsCode|CopilotCli|CopilotSdk` plus
`LocalMonitorScriptTests`. Commit only after final reviews/validation:
`Issues #66-#67: docs: record guided setup evidence`.

### Required independent reviews

1. Task review after every task; implementer and reviewer differ.
2. Early real #66→#67 interface gate after Task 8b.
3. Security/concurrency/migration review after Task 12: secret/path negatives,
   lock/barriers, crash/rollback/partial states, v1 close/reopen, future-real-
   version migration rule, no fake v0.
4. Separate final integration review: #66↔#67, #61 runtime manifests,
   repository/Release parity, HTTP/proxy/UI N/A, requirement-to-test table.

### Final validation

Run exactly in order:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Then run `git diff --check`, inspect the full commit range/worktree, update the
durable ledger with exact counts and remaining minors/interfaces, and create the
13b evidence commit. On failure use `superpowers:systematic-debugging`; do not
substitute commands, guess-fix, retry, push, or open a PR.

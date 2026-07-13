# Issues #66/#67 Configuration Setup Implementation Plan

**Goal:** Complete the approved `setup.v1` ownership framework and deliver the
GitHub Copilot VS Code/CLI/App-SDK adapter with one production DTO path, bounded
user-scoped mutation, and no claim that static setup proves telemetry receipt.

**Architecture:** Config CLI is the only result producer. Adapters produce
internal plans, the #66 coordinator owns every mutation/recovery/rollback, the
CLI serializes `SetupCommandResult`, and PowerShell forwards the JSON unchanged.
No HTTP, proxy, UI, database, AppHost resource, project, or dependency is added.

**Contract:** `docs/specifications/interfaces/configuration-setup.md`, D057,
`docs/specifications/security-data-boundaries.md`, and the paired design.

## Completed baseline and evidence preservation

T0 current-spec alignment supersedes the old prospective Task 1a-13b ordering.
It does not erase completed #66 work or rename historical evidence. Exact
commit ranges, focused counts, reviews, residuals, and the old task labels
(1a-8a, including split recovery/rollback remediations) remain canonical in
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`.

The implementation DAG starts from these completed prerequisites:

- `setup.v1` result types, serializer, bounds, and redaction validation;
- runtime #61 manifest loader (old Task 8a);
- runtime paths, private plans, ownership ledger v1, status snapshot schema,
  JSONC/TOML codecs, path/hash/atomic file primitives, and Windows current-user
  environment primitives;
- journal, apply, reverse compensation, restart recovery, rollback producer,
  notification replay, and their recorded fault/review evidence;
- SetupOptions parsing through the durable-ledger entry recorded on 2026-07-14.

Do not rewrite those rows as new T1-T9 work. T9 alone appends the final results
to the durable ledger and identifies both the current T-number and any
historical task whose residual it closes.

## Bounded dependency DAG

```text
T0 current-spec alignment
 |
 v
T1 #66 status/terminal closure
 |
 v
T2 #66 public command producer
 |
 v
T3 #67 shared detection/policy/probe foundation
 |          |          |
 v          v          v
T4         T5         T6
VS Code    CLI       App/SDK
 |          |          |
 +----------+----------+
            |
            v
T7 real #66->#67 integration
 |
 v
T8 package/docs/evidence
 |
 v
T9 reviews/full validation
```

The only permitted order is T1 -> T2 -> T3 -> {T4 || T5 || T6} -> T7 -> T8 ->
T9. T4, T5, and T6 may proceed concurrently only after T3 freezes the shared
contract/platform/GitHubCopilot seam. Each target task owns only its partition;
T7 owns the composition-root join. No adapter may declare completion before T7.
T8 and T9 are sequential closeout.

Gate releases are explicit:

| Gate | Release condition |
| --- | --- |
| T1 -> T2 | exact T1 status/rollback/recovery tests and all Setup tests pass; review and commit are complete |
| T2 -> T3 | registry, generic CLI seam, four-command producer, and wrapper tests pass; review and commit are complete |
| T3 -> T4/T5/T6 | shared codes, validation, platform, manifest-pairing, detection, policy, and endpoint tests pass; the shared seam is then frozen |
| T4/T5/T6 -> T7 | all three target partitions pass focused tests and are separately reviewed and committed without editing T3-owned files |
| T7 -> T8 | the real composition root and CLI handoff pass the cross-target producer gate and full ConfigCli tests |
| T8 -> T9 | package, wrapper parity, links, and operator documentation checks pass; review and commit are complete |
| T9 -> close | final reviews, required repository validation, contradiction searches, clean range/worktree inspection, and the sole ledger update pass |

Every task uses test-driven changes, an independent read-only review, a focused
RED/GREEN command, `git diff --check`, and a coherent local commit. Workers do
not revert concurrent edits and adjust to the shared contract. Two consecutive
fix cycles in one area trigger a contract/test-design re-audit.

## T1 - Close #66 status and terminal invariants

**Own exactly:** `Setup/Status/SetupStatusProjector.cs`,
`Setup/Status/SetupStatusListProjector.cs`,
`Setup/Transactions/SetupRollbackPreflightEvaluator.cs`, the status-preflight
integration in `Setup/Transactions/SetupRollbackCoordinator.cs`, and
`SetupStatusProjectorTests.cs`, `SetupStatusOrderingTests.cs`,
`SetupRollbackTests.cs`, and `SetupRecoveryTests.cs`.

**Deliver:** lifecycle-relative reference/current state, immutable ledger
snapshot reconstruction without adapter rediscovery, fresh private-plan/target/
backup checks, all-NoOp guard-only equivalence with rollback, partial rollback
false, filter -> priority -> deterministic order -> hard 100 cap -> truncation,
and the remaining terminal environment hash/evidence residuals.

**Verify:** focused `SetupStatus|SetupRollback|SetupRecovery` tests, then all
Setup tests. Do not change #67 target behavior. Commit:
`Issue #66: feat(setup): complete ownership status`.

## T2 - Expose the real #66 command producer

**Own exactly:** `Setup/Adapters/ISetupAdapter.cs`,
`Setup/Adapters/SetupAdapterRegistry.cs`, `Setup/Cli/SetupOptions.cs`,
`Cli/CliApplication.cs`, `Cli/CliHelpText.cs`, `SetupAdapterRegistryTests.cs`,
`SetupOptionsTests.cs`, `CliApplicationTests.cs`, and the new repository
`scripts/local-monitor/setup.ps1` plus new `SetupWrapperTests.cs`. T2 exposes a
generic CLI/composition handoff but does not register a #67 adapter.

**Deliver:** all four commands through the real coordinator, one JSON stdout
producer, fixed stderr/exit mapping, mandatory recovery correlation, mutation
blocking, and exact wrapper forwarding. Pin the closed command matrix:

- unknown adapter at plan -> `plan`/`unsupported_adapter`;
- valid persisted plan whose adapter is no longer registered ->
  `apply`/`unsupported_adapter`, persisted adapter ID retained, no platform
  probe, artifact, ledger transition, or target write;
- macOS/Linux CLI persisted plan -> `apply`/`unsupported_target`, no shell
  profile, artifact, ledger transition, notification, or target write.

**Verify:** `SetupAdapterRegistryTests`, `SetupOptionsTests`,
`CliApplicationTests`, `SetupWrapperTests`, then full ConfigCli tests. Commit:
`Issue #66: feat(setup): expose reversible setup commands`.

## T3 - Build the #67 detection, policy, and endpoint foundation

**Depends on:** T2.

**Own exclusively:** shared files directly under
`Setup/Adapters/GitHubCopilot/` (target files must live in the T4/T5/T6
subdirectories), `Setup/Contracts/SetupCodes.cs`,
`Setup/Contracts/SetupContractValidator.cs`, `Setup/Platform/ISetupPlatform.cs`,
`Setup/Platform/SystemSetupPlatform.cs`, `SetupTestPlatform.cs`,
`SetupContractShapeTests.cs`, `SetupContractValidationTests.cs`,
`SourceCapabilityRuntimeTests.cs`, `GitHubCopilotDetectionTests.cs`, and
`GitHubCopilotEndpointProbeTests.cs`. T3 does not register unfinished target
implementations in the production adapter registry. After this gate, no later
task edits these shared files; a finding reopens T3.

**Deliver:** the real adapter foundation and internal #66 plan DTO path;
Stable/Insiders and CLI version detection; planning OS capture; canonical #61
manifest pairing; official read-only managed-source tables; native > server >
file whole-channel selection with no merge inside the Copilot system;
independent VS Code enterprise-policy observation; and the exact endpoint
classifier:

- no-redirect `GET <origin>/health/live`;
- one 500 ms total budget; connect/read/total timeout is foreign-owner;
- maximum 4096 payload bytes, using one sentinel byte unless a trustworthy
  `Content-Length` already proves oversize;
- accept only HTTP 200 and exactly `{ "status": "live" }` modulo JSON
  whitespace/property order, with no duplicate/extra property;
- refused/proved no-listener -> warning `monitor_not_running`;
- every other transport failure, timeout, redirect, non-200, oversize,
  malformed/non-object, or different JSON ->
  `port_owned_by_foreign_process`.

T3 exposes bounded observations/classifiers only. It does not own target plan
creation or target-specific apply revalidation: T4 owns VS Code version/
extension/policy/member/endpoint revalidation and T5 owns CLI OS/version/
environment-member/endpoint revalidation.

**Verify:** `SetupContractShapeTests`, `SetupContractValidationTests`,
`SourceCapabilityRuntimeTests`, `GitHubCopilotDetectionTests`, and
`GitHubCopilotEndpointProbeTests`. Commit:
`Issue #67: feat(setup): add Copilot detection foundation`.

## T4 - Implement VS Code Stable/Insiders Default Profile setup

**Depends on:** T3.

**Own exactly:** the target partition
`Setup/Adapters/GitHubCopilot/VsCode/` and `VsCodeSetupAdapterTests.cs`. Do not
edit T3-owned shared files or the production composition root; T7 owns the join.

**Deliver:** Stable and Insiders 1.128+; deterministic Stable-then-Insiders
physical targets when both are eligible; per-channel Default Profile paths on
Windows/macOS/Linux; per-channel `GitHub.copilot-chat` requirement; exact
members `github.copilot.chat.otel.enabled`, `.exporterType`, `.otlpEndpoint`,
and optional separate `.captureContent`; JSONC preservation; restart guidance;
and fixed warning `vscode_non_default_profiles_not_modified` exactly once when
any non-default profile exists. Non-default profile files are never opened,
hashed, parsed, planned, backed up, written, or rolled back.

Extension listing is exactly `code --list-extensions --show-versions` or
`code-insiders --list-extensions --show-versions`, with no `--profile` argument;
setup never creates/selects a named profile. Insiders paths use the official
Profiles rule that replaces intermediate folder `Code` with `Code - Insiders`.

Read official Copilot native/server/file managed sources and select their
winning channel whole, not field-merged. Separately read VS Code enterprise
`CopilotOtel*` policies; never let them suppress Copilot server/file. A
differing observed constraint from either system blocks. Copilot native absence
produces `managed_policy_unverified` because server presence cannot be proved.

**Verify:** `VsCodeSetupAdapterTests` cover three-OS paths, both channels,
profile no-open proof, exact warning de-duplication, managed-source precedence,
content opt-in, forbidden global keys, and apply-time version/extension/policy/
member revalidation. Commit:
`Issue #67: feat(setup): guide VS Code Copilot telemetry`.

## T5 - Implement OS-bounded Copilot CLI setup

**Depends on:** T3.

**Own exactly:** the target partition
`Setup/Adapters/GitHubCopilot/CopilotCli/` and
`CopilotCliSetupAdapterTests.cs`. Do not edit T3-owned shared files or the
production composition root; T7 owns the join.

**Deliver:** CLI 1.0.4+ and the exact default allowlist:
`COPILOT_OTEL_ENABLED`, `COPILOT_OTEL_EXPORTER_TYPE`,
`OTEL_EXPORTER_OTLP_ENDPOINT`, and `OTEL_EXPORTER_OTLP_PROTOCOL`; optional
content capture adds only
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`. Never write
`client.kind`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`,
`OTEL_EXPORTER_OTLP_HEADERS`, `COPILOT_OTEL_SOURCE_NAME`, or credentials.

Detect-only `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` never joins the write
allowlist. Matching `http/protobuf` is retained with
`cli_trace_protocol_override_not_modified`; any other value returns
`environment_override_conflict` plus `review_cli_trace_protocol_override` and
does not persist a plan.

CLI policy detection is environment-only and every successful plan includes
`managed_policy_unverified`. Windows alone writes the current-user environment.
macOS/Linux run detect/plan but their persisted plan applies as the fixed
`unsupported_target` no-write combination; never edit a shell profile. Preserve
current-process/current-user difference and restart guidance on Windows.

**Verify:** `CopilotCliSetupAdapterTests` cover exact allowlist/forbidden keys,
Windows state matrix, macOS/Linux plan then refusal, no-artifact proof, warning,
capture option, and endpoint/version/member revalidation. Commit:
`Issue #67: feat(setup): guide Copilot CLI telemetry`.

## T6 - Implement App/SDK no-write guidance

**Depends on:** T3.

**Own exactly:** the target partition
`Setup/Adapters/GitHubCopilot/AppSdk/`, `CopilotSdkGuidanceAdapterTests.cs`, and
the new `CopilotSdkTelemetryCompileTests.cs`. Do not edit T3-owned shared files
or the production composition root, and do not add/change a PackageReference.

**Deliver:** detected package/version without path, one guidance target, null
manifest, no mutation/rollback, and the exact .NET `TelemetryConfig` sample with
loopback endpoint and `http/protobuf`. Other languages remain caller-managed.

**Verify:** guidance adapter tests and SDK telemetry compile test. Commit:
`Issue #67: feat(setup): add Copilot SDK guidance`.

## T7 - Join the real #66 producer to all #67 adapters

**Depends on:** T1, T2, T4, T5, T6.

**Own exactly:** `Program.cs`, new `Setup/SetupCompositionRoot.cs`, and new
`ConfigurationSetupIntegrationTests.cs`. T7 registers the three target
partitions and passes the real setup producer into the generic T2 CLI seam. It
does not edit adapter, shared-contract, platform, or target-specific files;
findings reopen the originating owner and downstream gates.

**Deliver:** real plan/apply/rollback/status for all targets through the same
ledger/transaction/result types, all-target `all` behavior, stale and rollback
boundaries, recovery correlation, repeated no-op, exact manifest pairing, and
the two exceptional apply/code combinations. Verify that App/SDK and Unix CLI
never become mutation authorities. HTTP/proxy/UI remain N/A.

**Early real-producer gate:** invoke the real CLI, parse `setup.v1`, prove the
runtime #61 manifests and production `SetupCommandResult`/target DTO types, and
record HTTP/proxy/UI N/A. A fake adapter cannot satisfy this gate.

**Verify:** `ConfigurationSetupIntegrationTests`, all test classes matching
`Setup|GitHubCopilot|VsCode|CopilotCli|CopilotSdk`, and the full ConfigCli
project. Commit:
`Issues #66-#67: test(setup): verify guided setup integration`.

## T8 - Package and document only verified behavior

**Depends on:** T7.

**Own exactly:** `scripts/local-monitor/package-release.ps1`; the package/layout/
executable and wrapper-parity cases in
`tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorScriptTests.cs`;
`README.md`; `scripts/local-monitor/README.md`;
`docs/user-guide/local-monitor.md`;
`docs/agent-guides/repository-workflow.md`;
`docs/specifications/interfaces/config-cli.md`;
`docs/sprints/sprint23-configuration-ownership-github-copilot/README.md`;
and `docs/task.md`. Package the T2-owned `scripts/local-monitor/setup.ps1`
without changing its command contract.
Do not change unrelated startup/user-env scripts or the release workflow.

**Deliver:** self-contained `app/config-cli/`, exact wrapper parity, no installed
.NET runtime requirement, implemented examples that never claim first trace,
and requirement-to-test evidence with exact counts, remaining minors, and
interfaces. Preserve all completed #66 evidence rows for T9.

**Verify:** `LocalMonitorScriptTests`, CLI/wrapper parsed DTO parity, link/path
checks, `git diff --check`. Commit:
`Issues #66-#67: docs(setup): record guided setup evidence`.

## T9 - Independent review, validation, and closeout

**Depends on:** T8.

**Own exactly:** final read-only review and validation plus
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`. T9
does not edit implementation, tests, package files, or other documentation. A
finding reopens its originating T1-T8 owner and every downstream gate; after the
fix is reviewed and committed, execution returns to T9 from that gate.

Run separate final reviews for:

1. #66 transaction/security/concurrency/status and shipped-v1 migration N/A;
2. #67 official-source/profile/OS/managed/endpoint/forbidden-key contracts;
3. real #66 -> #67 DTO/manifest/wrapper integration and HTTP/proxy/UI N/A;
4. repository-safe negative markers and exact full diff.

Resolve findings, then run exactly:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Finally run contradiction searches and `git diff --check`, inspect the complete
commit range/worktree, and make the only T1-T9 durable-ledger update with exact
results and unverified scope. On failure use systematic debugging; do not
substitute a command, guess-fix, retry blindly, push, or open a PR.

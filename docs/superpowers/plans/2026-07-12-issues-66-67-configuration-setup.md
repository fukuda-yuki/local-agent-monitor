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
| T1 -> T2 | exact T1 status/rollback/recovery tests, including the implemented status-lifecycle validation invariant, and all Setup tests pass; review and commit are complete |
| T2 -> T3 | sequential T2a contract/registry, T2b typed diagnostics carrier, T2c dispatcher, and T2d process/wrapper gates all pass; the four-command producer and exact #66 exceptional apply combinations are reviewed and committed, while target-specific #67 values remain T3-owned |
| T3 -> T4/T5/T6 | remaining #67 shared catalog/validation/runtime, platform, manifest-pairing, detection, policy, and endpoint tests pass; the shared seam is then frozen |
| T4/T5/T6 -> T7 | all three target partitions pass focused tests and are separately reviewed and committed without editing T3-owned files |
| T7 -> T8 | the real composition root and CLI handoff pass the cross-target producer gate and full ConfigCli tests |
| T8 -> T9 | package, wrapper parity, links, and operator documentation checks pass; review and commit are complete |
| T9 -> close | final reviews, required repository validation, contradiction searches, clean range/worktree inspection, and the sole ledger update pass |

Shared contract files use sequential hunk/section ownership, never parallel
file ownership:

| Files | Active owner and bounded section |
| --- | --- |
| `SetupContractValidator.cs` and `SetupStatusProjectorTests.cs` | T1 through its gate owns the already-implemented `ValidateStatusChangeSet` lifecycle/aggregate invariant and its projector serialization proof; that hunk then freezes |
| `SetupCodes.cs`, `SetupContractValidator.cs`, `SetupContractShapeTests.cs`, and `SetupContractValidationTests.cs` | after T1 releases, T2 owns only the exact #66 `apply`/`unsupported_adapter` and `apply`/`unsupported_target` combinations plus generic carrier/result validation; those sections then freeze, while T3 owns the target-specific #67 code/warning/action values |
| `ISetupAdapter.cs` and `SetupAdapterRegistryTests.cs` | T2a owns the adapter-plan/registry bridge, then hands these files sequentially to T2b for only the generic warnings/next-actions and sanitized failure-carrier additions; T2b freezes that carrier before T2c starts |
| `ISetupApplyRevalidator.cs`, `SetupApplyCoordinator.cs`, and `SetupApplyTests.cs` | T2b alone owns the generic revalidation carrier handoff and its no-artifact/fixed-diagnostic tests; no T2c/T3 target owner may reopen transaction semantics through these files |
| `SetupCommandDispatcher.cs` and `SetupCommandDispatcherTests.cs` | T2c alone owns the generic four-command producer, exact lock/recovery delegation, target projection, and result construction; T2d consumes this seam and T7 only composes it |
| the same four shared contract files plus `SourceCapabilityRuntimeTests.cs` | after T2 releases, T3 receives them for only the remaining #67 catalog, validator, shape/validation, and runtime-manifest additions; T3 then freezes the complete shared seam |

No two tasks edit these files concurrently. A finding reopens the task that owns
the affected hunk/section and invalidates its downstream releases.

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
`SetupRollbackTests.cs`, and `SetupRecoveryTests.cs`; plus only the implemented
`ValidateStatusChangeSet` lifecycle/aggregate hunk in
`Setup/Contracts/SetupContractValidator.cs` and its projector serialization
invariant in `SetupStatusProjectorTests.cs`.

**Deliver:** lifecycle-relative reference/current state, immutable ledger
snapshot reconstruction without adapter rediscovery, fresh private-plan/target/
backup checks, all-NoOp guard-only equivalence with rollback, partial rollback
false, filter -> priority -> deterministic order -> hard 100 cap -> truncation,
and the remaining terminal environment hash/evidence residuals.

**Verify:** focused `SetupStatus|SetupRollback|SetupRecovery` tests, then all
Setup tests. Do not change #67 target behavior. Commit:
`Issue #66: feat(setup): complete ownership status`.

## T2 - Expose the real #66 command producer

T2 is four small sequential review units. T2a -> T2b -> T2c -> T2d is mandatory;
none may run in parallel, and T3 starts only after the T2d gate. Each unit owns
only the files listed below, runs its exact focused command, receives an
independent review, and commits before handing off shared files.

### T2a - Freeze the generic contract, options, and adapter registry

**Own exactly:** `Setup/Adapters/ISetupAdapter.cs`,
`Setup/Adapters/SetupAdapterRegistry.cs`, `Setup/Cli/SetupOptions.cs`,
`SetupAdapterRegistryTests.cs`, and `SetupOptionsTests.cs`; after the T1 gate,
only the bounded #66 exceptional-apply and generic carrier/result-validation
sections of `Setup/Contracts/SetupCodes.cs`,
`Setup/Contracts/SetupContractValidator.cs`, `SetupContractShapeTests.cs`, and
`SetupContractValidationTests.cs`.

**Deliver:** private-plan/planned-ledger/public-target bridging, adapter-slug
parsing without early registry resolution, persisted-adapter lookup, and the
closed `apply`/`unsupported_adapter` and `apply`/`unsupported_target` result
shapes. Do not add GitHub Copilot target values or construct a public command
result.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupAdapterRegistryTests|FullyQualifiedName~SetupOptionsTests|FullyQualifiedName~SetupContractShapeTests|FullyQualifiedName~SetupContractValidationTests"
git diff --check
```

Commit: `Issues #66-#67: feat(setup): close generic setup contracts`.

### T2b - Carry sanitized adapter and revalidation diagnostics

**Depends on:** T2a.

**Own exactly:** the diagnostics-carrier additions in
`Setup/Adapters/ISetupAdapter.cs` and `SetupAdapterRegistryTests.cs` after the
T2a handoff; `Setup/Transactions/ISetupApplyRevalidator.cs`,
`Setup/Transactions/SetupApplyCoordinator.cs`, and `SetupApplyTests.cs`.

**Deliver:** one immutable generic carrier vocabulary whose only public-safe
diagnostic fields are an allowed nullable failure code plus ordered closed
`warnings` and `next_actions`. Adapter plan success carries targets and those
two arrays; adapter plan failure carries the fixed code and arrays without raw
exception text. `ISetupApplyRevalidator` uses the same vocabulary for fresh
success/failure diagnostics, and `SetupApplyCoordinator` returns or throws the
typed safe payload needed by the later dispatcher without rerunning the
adapter. Unexpected/framework failures keep both arrays empty. Persisted-
adapter removal and `unsupported_target` also keep both arrays empty.

T2b owns only the generic transport and validation. T3 later supplies the
GitHub Copilot-specific fixed values; T4/T5 may emit those values through the
frozen carrier but may not change its shape. Do not edit status/rollback
projection, CLI/process files, or `SetupCommandDispatcher`.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupAdapterRegistryTests|FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupContractShapeTests|FullyQualifiedName~SetupContractValidationTests"
git diff --check
```

Commit: `Issue #66: feat(setup): carry sanitized adapter diagnostics`.

### T2c - Implement the generic four-command dispatcher

**Depends on:** T2b.

**Own exactly:** new `Setup/Cli/SetupCommandDispatcher.cs` and new
`SetupCommandDispatcherTests.cs`.

**Deliver:** the sole generic constructor of `SetupCommandResult` for plan,
apply, rollback, and status. Plan/apply each acquire one non-waiting lock and
run common recovery once; rollback delegates its one recovery pass to
`SetupRollbackCoordinator` under the same held lock; status delegates directly
to `SetupStatusService` with no outer lock/recovery. The dispatcher preserves
requested versus recovered correlation, adapter/ledger target order, plan
prospective versus apply actual rollback availability, typed warnings/actions,
`no_changes` persistence, missing-row/private-plan distinctions, and the closed
exceptional apply matrix. It never serializes JSON and never resolves target-
specific values.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupCommandDispatcherTests|FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupRollbackTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupStatus"
git diff --check
```

Commit: `Issue #66: feat(setup): dispatch reversible setup commands`.

### T2d - Expose the process and wrapper surfaces

**Depends on:** T2c.

**Own exactly:** `Cli/CliApplication.cs`, `Cli/CliHelpText.cs`,
`CliApplicationTests.cs`, new repository `scripts/local-monitor/setup.ps1`, and
new `SetupWrapperTests.cs`.

**Deliver:** recognized setup grammar, exactly one JSON stdout result for each
recognized verb, fixed stderr/exit mapping, bare/unknown setup-verb handling,
preserved legacy top-level behavior, and byte-for-byte wrapper forwarding. T2d
only consumes T2c; it does not recreate lock, recovery, adapter, target, or
diagnostics behavior.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~CliApplicationTests|FullyQualifiedName~SetupWrapperTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
git diff --check
```

Commit: `Issue #66: feat(setup): expose reversible setup commands`.

The T2 gate releases only after all four commits and reviews exist. Its closed
command matrix is: unknown adapter at plan -> `plan`/`unsupported_adapter`;
valid persisted plan whose adapter is removed ->
`apply`/`unsupported_adapter` with retained adapter ID and zero platform/
artifact/lifecycle/target activity; macOS/Linux CLI persisted plan ->
`apply`/`unsupported_target` with no shell profile, artifact, lifecycle,
notification, or target write.

## T3 - Build the #67 detection, policy, and endpoint foundation

**Depends on:** T2.

**Own after the T2 gate:** shared files directly under
`Setup/Adapters/GitHubCopilot/` (target files must live in the T4/T5/T6
subdirectories); only the remaining #67 sections in
`Setup/Contracts/SetupCodes.cs`, `Setup/Contracts/SetupContractValidator.cs`,
`SetupContractShapeTests.cs`, and `SetupContractValidationTests.cs`;
`Setup/Platform/ISetupPlatform.cs`, `Setup/Platform/SystemSetupPlatform.cs`,
`SetupTestPlatform.cs`, `SourceCapabilityRuntimeTests.cs`,
`GitHubCopilotDetectionTests.cs`, and `GitHubCopilotEndpointProbeTests.cs`. T3
preserves the frozen T1 lifecycle hunk and T2 exceptional-combination/catalog
sections and does not register unfinished targets. After this gate, no later
task edits the shared seam; a finding reopens its hunk owner.

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
environment-member/endpoint revalidation. T3 declares the GitHub Copilot fixed
warning/next-action/failure values in its bounded shared-contract sections and
makes them available to the target partitions, but it does not edit the T2b
carrier or T2c dispatcher. T4/T5 emit those fixed values through the frozen
generic carrier; T6 emits only its bounded guidance diagnostics.

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

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

Do not rewrite those rows as new T1-T9 work. New reports append to the durable
ledger and identify both the current T-number and any historical task whose
residual they close.

## Bounded dependency DAG

```text
T0 current-spec alignment (this document set)
 |
 +--> T1 #66 status/terminal closure -----------------+
 +--> T2 #66 public command producer                  |
          |                                           |
          v                                           |
         T3 #67 detection/policy/probe foundation     |
          |          |          |                     |
          v          v          v                     v
         T4         T5         T6 -------------> T7 real #66->#67 integration
       VS Code     CLI       App/SDK                  |
                                                       v
                                               T8 package/docs/evidence
                                                       |
                                                       v
                                               T9 reviews/full validation
```

T1 and T2 may proceed concurrently. T3 follows T2 so the shared registry/CLI
contract has one owner. T4, T5, and T6 may proceed concurrently after T3 and
own only their target-specific implementation/tests; T7 owns their production
registry join. No adapter may declare completion before T7. T8 and T9 are
sequential closeout.

Every task uses test-driven changes, an independent read-only review, a focused
RED/GREEN command, `git diff --check`, and a coherent local commit. Workers do
not revert concurrent edits and adjust to the shared contract. Two consecutive
fix cycles in one area trigger a contract/test-design re-audit.

## T1 - Close #66 status and terminal invariants

**Own:** `Setup/Status/`, the shared pure rollback-preflight evaluator and its
narrow rollback integration, status-related contract/serializer code only when
required, `SetupStatusTests.cs`, `SetupStatusOrderingTests.cs`, paired
`SetupRollbackTests.cs`, and remaining terminal-evidence tests.

**Deliver:** lifecycle-relative reference/current state, immutable ledger
snapshot reconstruction without adapter rediscovery, fresh private-plan/target/
backup checks, all-NoOp guard-only equivalence with rollback, partial rollback
false, filter -> priority -> deterministic order -> hard 100 cap -> truncation,
and the remaining terminal environment hash/evidence residuals recorded in the
durable ledger.

**Verify:** focused `SetupStatus|SetupRollback|SetupRecovery` tests, then all
Setup tests. Do not change #67 target behavior. Commit:
`Issue #66: feat(setup): complete ownership status`.

## T2 - Expose the real #66 command producer

**Own:** `Setup/Adapters/ISetupAdapter.cs`, adapter registry, `Setup/Cli/`,
`Setup/Contracts/SetupContractValidator.cs`, `CliApplication.cs`,
`CliHelpText.cs`, `SetupCliTests.cs`, and the command-composition cases in
`SetupContractValidationTests.cs`/`SetupContractShapeTests.cs`; plus repository
`scripts/local-monitor/setup.ps1` plus its exact wrapper tests.

**Deliver:** all four commands through the real coordinator, one JSON stdout
producer, fixed stderr/exit mapping, mandatory recovery correlation, mutation
blocking, and exact wrapper forwarding. Pin the closed command matrix:

- unknown adapter at plan -> `plan`/`unsupported_adapter`;
- valid persisted plan whose adapter is no longer registered ->
  `apply`/`unsupported_adapter`, persisted adapter ID retained, no platform
  probe, artifact, ledger transition, or target write;
- macOS/Linux CLI persisted plan -> `apply`/`unsupported_target`, no shell
  profile, artifact, ledger transition, notification, or target write.

**Verify:** `SetupCliTests`, `SetupContractShapeTests`,
`SetupContractValidationTests`, `LocalMonitorScriptTests`, then full ConfigCli
tests. Commit: `Issue #66: feat(setup): expose reversible setup commands`.

## T3 - Build the #67 detection, policy, and endpoint foundation

**Depends on:** T2.

**Own:** the shared `Setup/Adapters/GitHubCopilot/` composite-adapter,
detection, policy, endpoint-probe, and target-extension-point files; only the
required platform interface/fake extensions; `GitHubCopilotDetectionTests.cs`;
and `GitHubCopilotEndpointProbeTests.cs`. T3 does not register unfinished target
implementations in the production adapter registry.

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

**Verify:** `GitHubCopilotDetectionTests`, `GitHubCopilotEndpointProbeTests`,
and runtime manifest tests. Commit:
`Issue #67: feat(setup): add Copilot detection foundation`.

## T4 - Implement VS Code Stable/Insiders Default Profile setup

**Depends on:** T3.

**Own:** VS Code adapter files, `VsCodeSetupAdapterTests.cs`, and the narrow
`SetupCodes.cs`/`SetupContractValidator.cs`/`SetupContractShapeTests.cs`/
`SetupContractValidationTests.cs` additions for the fixed non-default-profile
warning. Do not edit the shared production adapter registry; T7 owns the join.

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

**Own:** Copilot CLI adapter files, `CopilotCliSetupAdapterTests.cs`, and the
narrow `SetupCodes.cs`/`SetupContractValidator.cs`/shape-validation additions
for the trace-protocol override conflict/warning/action. Do not edit the shared
production adapter registry; T7 owns the join.

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

**Own:** App/SDK guidance adapter files, `CopilotSdkGuidanceAdapterTests.cs`, and
the existing LocalMonitor SDK compile contract test. Do not edit the shared
production adapter registry and do not add/change a PackageReference.

**Deliver:** detected package/version without path, one guidance target, null
manifest, no mutation/rollback, and the exact .NET `TelemetryConfig` sample with
loopback endpoint and `http/protobuf`. Other languages remain caller-managed.

**Verify:** guidance adapter tests and SDK telemetry compile test. Commit:
`Issue #67: feat(setup): add Copilot SDK guidance`.

## T7 - Join the real #66 producer to all #67 adapters

**Depends on:** T1, T2, T4, T5, T6.

**Own:** the production adapter/target registry join, narrow production
integration points, and `ConfigurationSetupContractTests.cs`; adapter-specific
files only for findings.

**Deliver:** real plan/apply/rollback/status for all targets through the same
ledger/transaction/result types, all-target `all` behavior, stale and rollback
boundaries, recovery correlation, repeated no-op, exact manifest pairing, and
the two exceptional apply/code combinations. Verify that App/SDK and Unix CLI
never become mutation authorities. HTTP/proxy/UI remain N/A.

**Early real-producer gate:** invoke the real CLI, parse `setup.v1`, prove the
runtime #61 manifests and production `SetupCommandResult`/target DTO types, and
record HTTP/proxy/UI N/A. A fake adapter cannot satisfy this gate.

**Verify:** all test classes matching
`Setup|GitHubCopilot|VsCode|CopilotCli|CopilotSdk|ConfigurationSetupContract`
and the full ConfigCli project. Commit:
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
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`; and
`docs/task.md`. Package the already implemented
`scripts/local-monitor/setup.ps1` without changing its T2 command contract.
Do not change unrelated startup/user-env scripts or the release workflow.

**Deliver:** self-contained `app/config-cli/`, exact wrapper parity, no installed
.NET runtime requirement, implemented examples that never claim first trace,
and a requirement-to-test ledger with exact counts, remaining minors, and
interfaces. Preserve all completed #66 evidence rows.

**Verify:** `LocalMonitorScriptTests`, CLI/wrapper parsed DTO parity, link/path
checks, `git diff --check`. Commit:
`Issues #66-#67: docs(setup): record guided setup evidence`.

## T9 - Independent review, validation, and closeout

**Depends on:** T8.

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
commit range/worktree, and update the durable ledger with exact results and
unverified scope. On failure use systematic debugging; do not substitute a
command, guess-fix, retry blindly, push, or open a PR.

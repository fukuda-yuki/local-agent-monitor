# Task 06 (T5): OS-bounded Copilot CLI target partition

**Objective:** Implement the `cli` target partition: Copilot CLI 1.0.4+
detection, the exact environment write allowlist, the detect-only trace
protocol override rule, Windows current-user environment planning with
mandatory shared-environment warning, macOS/Linux detect/plan-but-never-apply
behavior, and apply-time revalidation.

**Depends on:** task-04 (T3d) committed and reviewed. May run in parallel
with tasks 05/07; the file sets are disjoint.

**Files (T5 ownership):**
- Create: everything under
  `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/CopilotCli/`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/CopilotCliSetupAdapterTests.cs`

**Non-scope:** T3-owned shared files, other target subdirectories, the
production composition root (T7), catalog edits, any managed-source read
(deliberately environment-only for CLI), any shell-profile file.

**Interfaces:**
- Consumes (frozen): the T3d partition seam; `GitHubCopilotObservations`
  (CLI detection/version, planning OS); `GitHubCopilotEndpointClassification`;
  `ISetupUserEnvironment` (read-only here — mutation is the #66
  coordinator's job at apply); `SetupChangeRecord` with
  `SetupTargetKind.Env` and label exactly `copilot-cli-user-environment`.
- Produces: `CopilotCliTargetPartition : IGitHubCopilotTargetPartition` with
  `TargetToken == "cli"` — consumed by T7's composition only.

**Contract (spec "Terminal GitHub Copilot CLI" — encode verbatim):**
- Detection: `copilot version`; floor `1.0.4`. Not installed →
  `install_copilot_cli`; below floor → `upgrade_copilot_cli` (pair each with
  the failure code the spec's matrix assigns — verify before pinning).
- Exact write allowlist (nothing else, ever):
  `COPILOT_OTEL_ENABLED=true`, `COPILOT_OTEL_EXPORTER_TYPE=otlp-http`,
  `OTEL_EXPORTER_OTLP_ENDPOINT=<validated loopback endpoint>`,
  `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`. With
  `--include-content-capture`, additionally and only
  `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` +
  `content_capture_sensitive` + `review_content_capture_warning`.
- Forbidden keys (never written, asserted by negative tests): `client.kind`,
  `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`,
  `OTEL_EXPORTER_OTLP_HEADERS`, `COPILOT_OTEL_SOURCE_NAME`, any credential,
  any official telemetry variable outside the allowlist.
- `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` is detect-only, never in the
  allowlist: already exactly `http/protobuf` → leave unchanged + warning
  `cli_trace_protocol_override_not_modified`; any other value → plan fails
  `environment_override_conflict` + `review_cli_trace_protocol_override`,
  and creates no private plan or target artifact. Revalidation repeats the
  same classification before mutation artifacts.
- Managed policy: environment-only detection; never open native/server/file
  managed sources for CLI (prove via the managed-settings fake: zero reads).
  Every successful CLI plan includes `managed_policy_unverified`;
  `effective_source` describes only the observed environment layer.
- Windows: plan shows current-process versus current-user environment state
  per member (the 3x3 state matrix the #66 environment step defines); always
  warns `shared_user_environment_affects_other_processes`; restart guidance
  `restart_terminal_session`. Mutation itself is the generic #66 Windows
  user-environment transaction — the partition only plans members.
- macOS/Linux: detection, version validation, endpoint probing, manifest
  selection, and redacted plan creation still run; the persisted plan is
  tagged with its planning OS; apply later returns `unsupported_target`
  (that mapping is #66 coordinator/revalidation behavior — this partition's
  `Revalidate` must return the fixed `unsupported_target` failure for a plan
  whose planning OS is not Windows, with empty arrays per the T2 exceptional
  contract). No shell profile, backup, journal, or notification (negative
  proofs).
- Endpoint gating: same as VS Code — `ForeignOwner` fails the plan;
  `MonitorNotRunning` warns + `start_local_monitor`.
- Metric delivery is reported `not_verified` in the static expected result —
  the manifest already encodes this; never claim complete signal receipt.

## Steps

- [ ] **Step 1: Write the failing partition test matrix** in
  `CopilotCliSetupAdapterTests`: exact allowlist (member set equality, not
  subset); forbidden-key negatives; capture opt-in default/explicit;
  trace-protocol override matrix (absent / `http/protobuf` / other value);
  Windows current-process-vs-user state reporting; mandatory shared-env
  warning; `managed_policy_unverified` on every success; zero managed-source
  reads; macOS and Linux plans succeed with planning-OS tag and their
  `Revalidate` returns `unsupported_target` with empty arrays; no-artifact
  proof for the conflict path; endpoint gating; not-installed/below-floor
  actions; secret-marker negative (marker in an existing env value never
  reaches records/projections).

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~CopilotCliSetupAdapterTests
```

- [ ] **Step 3: Implement the partition** (detection → override
  classification → endpoint gate → member planning with redacted
  previous/new states → records + diagnostics; revalidation repeats
  version/override/endpoint/member checks and the non-Windows refusal).

- [ ] **Step 4: Run GREEN + full ConfigCli + build + `git diff --check`.**

- [ ] **Step 5: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/CopilotCli tests/CopilotAgentObservability.ConfigCli.Tests/CopilotCliSetupAdapterTests.cs
git commit -m "Issue #67: feat(setup): guide Copilot CLI telemetry"
```

  Body: why — the spec pins a closed environment allowlist with detect-only
  override handling and Windows-only mutation so setup can never edit shell
  profiles or non-allowlisted variables.

## Validation

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~CopilotCliSetupAdapterTests
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

## Completion criteria

- Allowlist equality, forbidden-key negatives, override matrix, Windows
  state matrix, macOS/Linux plan-then-refusal, zero managed reads, and
  secret-marker negatives all executable.
- No T3-owned or shared file edited.
- Full ConfigCli suite and build pass; independent review PASS. End-to-end
  behavior is task-08's gate, not this task's claim.

**Report destination:** chat + ledger row per README policy.

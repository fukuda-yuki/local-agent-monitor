# Task 05 (T4): VS Code Stable/Insiders Default Profile target partition

**Objective:** Implement the `vscode` target partition: per-channel Default
Profile `settings.json` planning for Stable and Insiders 1.128+, exact
telemetry members with JSONC preservation, managed-policy and enterprise-
policy gating, endpoint gating, content-capture opt-in, non-default-profile
warning, restart guidance, and apply-time revalidation.

**Depends on:** task-04 (T3d) committed and reviewed (seam frozen). May run
in parallel with tasks 06/07; the file sets are disjoint.

**Files (T4 ownership):**
- Create: everything under
  `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/VsCode/`
  (suggested: `VsCodeTargetPartition.cs` plus focused helpers in the same
  directory — keep files single-responsibility)
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/VsCodeSetupAdapterTests.cs`

**Non-scope:** T3-owned shared files (detection, resolver, probe, aggregate,
seam), the production composition root (T7), catalog/validator edits, any
other target subdirectory.

**Interfaces:**
- Consumes (frozen): `IGitHubCopilotTargetPartition` /
  `GitHubCopilotPartitionContext` / `GitHubCopilotPartitionPlan` from T3d;
  `GitHubCopilotObservations` (channel detection, versions, non-default
  profile flags) from T3a; `GitHubCopilotManagedPolicyResolver.Resolve` from
  T3b (T4 calls it with its own desired values);
  `GitHubCopilotEndpointClassification` from context;
  `ISetupProcessRunner` for extension listing; `JsoncSettingsDocument` for
  JSONC-preserving member mutation; `SetupHash` for base hashes;
  `SetupChangeRecord`/`SetupPrivatePlanMember`/`SetupStatusProjection` (T2
  types).
- Produces: `VsCodeTargetPartition : IGitHubCopilotTargetPartition` with
  `TargetToken == "vscode"` — consumed by T7's composition only.

**Contract (spec "VS Code GitHub Copilot Chat" — encode verbatim):**
- Channels: Stable (`code`) and Insiders (`code-insiders`), each 1.128.0+.
  1.119–1.127 detected → whole plan fails `unsupported_version` +
  `upgrade_vscode` (no channel mutated). Neither channel installed →
  `target_not_installed`? — NO: verify the exact code for "neither channel
  installed": the next-action table says `install_vscode` is used when
  "neither requested VS Code channel is installed"; pair it with the failure
  code the spec's detection matrix assigns (`target_not_installed`). Confirm
  against the spec before pinning the test expectation.
- Extension: for every installed supported channel run exactly
  `code --list-extensions --show-versions` (or `code-insiders ...`), never
  `--profile`; require `GitHub.copilot-chat`. Missing in any installed
  channel → `target_not_installed` + `install_github_copilot_chat_extension`,
  no partial plan.
- Both channels eligible → two physical JSON targets, Stable then Insiders,
  labels exactly `vscode-stable-default-user-settings` /
  `vscode-insiders-default-user-settings`.
- Writable paths (per channel/OS, from T3a's path rule): Stable
  `%APPDATA%\Code\User\settings.json`,
  `$HOME/Library/Application Support/Code/User/settings.json`,
  `$HOME/.config/Code/User/settings.json`; Insiders replaces intermediate
  `Code` with `Code - Insiders`.
- Members: `github.copilot.chat.otel.enabled = true`,
  `github.copilot.chat.otel.exporterType = "otlp-http"`,
  `github.copilot.chat.otel.otlpEndpoint = <validated loopback endpoint>`.
  Default plan never touches `github.copilot.chat.otel.captureContent`;
  with `--include-content-capture` it proposes `true` as a separate member
  change in the same file target and emits `content_capture_sensitive` +
  `review_content_capture_warning`.
- Non-default profiles: if either channel has any, warning
  `vscode_non_default_profiles_not_modified` exactly once; profile files are
  never opened/hashed/parsed/planned/backed up/written/rolled back (prove
  via the file-system fake's operation log).
- Managed policy: any differing observed value from either policy system →
  `managed_policy_conflict`, no plan. Copilot native absent → warning
  `managed_policy_unverified` + next action `run_vscode_policy_diagnostics`
  on every successful plan. Equal observed constraints → member `managed`
  flag true, member not rewritten.
- Environment layer (precedence 2): `COPILOT_OTEL_ENABLED`,
  `COPILOT_OTEL_ENDPOINT`, `OTEL_EXPORTER_OTLP_ENDPOINT`,
  `COPILOT_OTEL_PROTOCOL`, `OTEL_EXPORTER_OTLP_PROTOCOL`,
  `COPILOT_OTEL_CAPTURE_CONTENT` — a shared override is reported (member
  `effective_source`/conflict fields) and never silently deleted.
- Endpoint: `ForeignOwner` → `port_owned_by_foreign_process`, no plan;
  `MonitorNotRunning` → plan usable + `monitor_not_running` +
  `start_local_monitor`.
- Restart: a running `Code` process → `restart_vscode`.
- Revalidation (apply-time): repeat version/extension/both-policy/member/
  endpoint checks against persisted facts through the partition `Revalidate`;
  differing → the matching preflight failure code, fresh warnings/actions;
  unchanged → `Revalidated` with fresh warnings (e.g. unverified policy
  persists).
- JSONC: unknown keys/comments/formatting outside owned members preserved
  (use `JsoncSettingsDocument`; assert byte-preservation outside members).

## Steps

- [ ] **Step 1: Write the failing partition test matrix** in
  `VsCodeSetupAdapterTests` (drive `VsCodeTargetPartition` directly through
  scripted `GitHubCopilotPartitionContext`; no dispatcher). Cover at
  minimum: three-OS path construction for both channels; both-channels
  ordering; single-channel plans; below-floor version; missing extension;
  neither installed; profile no-open proof; warning exactly-once; conflict
  from Copilot managed native; conflict from enterprise policy; equal
  managed constraint → managed flag, no rewrite; unverified-policy warning
  pair on success; environment override reporting; capture opt-in default
  and explicit; endpoint gating both non-live outcomes; restart requirement;
  JSONC preservation; revalidation happy/differing rows; secret-marker
  negative test (inject a marker as an existing settings value; assert no
  record/projection/failure carries it).

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~VsCodeSetupAdapterTests
```

- [ ] **Step 3: Implement the partition** (detection composition → policy
  gate → endpoint gate → per-channel JSONC member planning → records +
  diagnostics; revalidation reuses the same checks against persisted facts).

- [ ] **Step 4: Run GREEN + full ConfigCli + build + `git diff --check`.**

- [ ] **Step 5: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/VsCode tests/CopilotAgentObservability.ConfigCli.Tests/VsCodeSetupAdapterTests.cs
git commit -m "Issue #67: feat(setup): guide VS Code Copilot telemetry"
```

  Body: why — the spec pins per-channel Default-Profile-only mutation with
  policy/endpoint gating so guided setup cannot touch profiles, managed
  sources, or foreign ports.

## Validation

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~VsCodeSetupAdapterTests
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

## Completion criteria

- Every contract bullet above has at least one executable case, including
  the profile no-open and secret-marker negative proofs.
- No T3-owned or shared file edited (`git diff --stat` shows only the two
  owned locations).
- Full ConfigCli suite and build pass; independent review PASS. This task
  does NOT claim end-to-end #67 behavior — that is task-08's gate.

**Report destination:** chat + ledger row per README policy. Record
fake-only macOS/Linux path coverage as unverified scope.

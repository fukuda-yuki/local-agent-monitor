# Task 05 (T4): VS Code Stable/Insiders Default Profile target partition

**Objective:** Implement the `vscode` target partition: per-channel Default
Profile `settings.json` planning for Stable and Insiders 1.128+, exact
telemetry members with JSONC preservation, managed-policy and enterprise-
policy gating, endpoint gating, content-capture opt-in, non-default-profile
warning, restart guidance, and apply-time revalidation. New VS Code records
persist only the tagged v1 owned-values desired state; complete JSONC bytes are
materialized transiently under the generic apply lock.

**Depends on:** task-04 (T3d), task-04a, task-04b, and task-04c committed and
reviewed, plus the fresh #66 security/concurrency/recovery review PASS required
by the README. The former #66 gate is reopened for these corrections. This task
may run in parallel with tasks 06/07 only after that gate; the file sets remain
disjoint.

**Worktree / branch:** Run only from
`C:\Users\mwam0\Documents\Codex\copilot-agent-observability` on
`codex/issues-66-67-guided-setup`. Before editing and before committing, verify
the root/branch plus `git status --short` and `git diff --name-only`; only
`VsCodeTargetPartition.cs` and `VsCodeSetupAdapterTests.cs` may appear.

**Files (T4 ownership):**
- Create/Modify:
  `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/VsCode/VsCodeTargetPartition.cs`
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
  `ISetupProcessRunner` for extension listing and bounded running-state
  observation; `JsoncSettingsDocument` for
  JSONC-preserving member mutation; `SetupHash` for base hashes;
  `SetupChangeRecord`/`SetupPrivatePlanMember`/`SetupStatusProjection` (T2
  types).
- Produces: `VsCodeTargetPartition : IGitHubCopilotTargetPartition` with
  `TargetToken == "vscode"` — consumed by T7's composition only.
- Emits: only `SetupTargetKind.Json` records owned by `github-copilot` with
  label `vscode-stable-default-user-settings` or
  `vscode-insiders-default-user-settings` use the schema-v1 tagged
  `desired_state` object
  `{"kind":"jsonc_owned_values_v1","expected_state_hash":...,"owned_values":[...]}`
  for new VS Code records. It never emits the canonical inline string retained
  for historical/generic non-tagged targets. `Revalidate` returns the matching transient
  materialized bytes only for changed records; no-op records have no carrier
  entry and retain the generic base-state guard.

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
- Both channels eligible → two physical `SetupTargetKind.Json` targets, Stable
  then Insiders, owned by `github-copilot`, with labels exactly
  `vscode-stable-default-user-settings` /
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
- JSONC read/materialization: plan and revalidation read each Default Profile
  settings file through exactly a 1 MiB payload bound plus one sentinel byte.
  Oversize or malformed JSONC returns `malformed_settings` with no private
  plan, artifact, target write, retry, or unbounded read. Planning may render
  the complete JSONC document only in bounded memory to calculate exact
  operations and the lowercase hash, then must discard it before private-plan/
  ledger creation; it preserves unrelated bytes only in that transient render,
  stores the exact tagged owned values in member order, and stores no complete
  JSONC bytes. Revalidation parses
  and re-derives the owned member facts, preserves comments/formatting and
  unrelated keys in its transient materialization, then requires its bytes to
  hash exactly to the persisted `expected_state_hash`; a mismatch is
  `recovery_required` before artifacts or writes.
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
- Restart observation: after all version and extension gates succeed, invoke
  exactly one `code --status` for each eligible Stable channel and exactly one
  `code-insiders --status` for each eligible Insiders channel, through the
  existing five-second-bounded `ISetupProcessRunner`, in Stable-then-Insiders
  order. The official command requires an already-running VS Code instance
  ([performance guidance](https://github.com/microsoft/vscode/wiki/performance-issues),
  [CLI documentation](https://code.visualstudio.com/docs/configure/command-line)).
  Only `Completed` with exit code `0` means that channel is running. The Stable
  target record gets `restart_vscode` iff Stable's own observation meets that
  condition; the Insiders target record does so iff Insiders' own observation
  meets it. All other observations mean that record has `none`, not plan
  failure. Assert the four dual-channel record combinations
  (restart/restart, restart/none, none/restart, none/none); the top-level
  action is deduplicated when either record requires restart and is not a
  substitute for either record field. Do not retry or sleep. Discard `--status`
  stdout immediately; it never reaches a DTO, private plan, ledger, log, or
  repository-safe output.
- Revalidation (apply-time): repeat version/extension/both-policy/member/
  endpoint checks against persisted facts through the partition `Revalidate`;
  differing → the matching preflight failure code, fresh warnings/actions;
  unchanged → `Revalidated` with fresh warnings (e.g. unverified policy
  persists). A different VS Code version that still meets 1.128.0 is supported-
  version drift and returns `recovery_required`; missing/below-floor versions
  retain `target_not_installed`/`unsupported_version`. Make zero `--status`
  calls during `Revalidate`; do not persist,
  compare, recompute, alter, or fail apply/preflight on the ephemeral
  running-state observation or the persisted per-target restart requirement.
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
  and explicit; endpoint gating both non-live outcomes; exact per-partition
  extension-list/status call counts and ordering after successful gates (and
  zero `--status` calls when a gate fails); Stable/Insiders independent running
  observations; the four dual-channel per-target restart combinations and
  top-level action deduplication without replacing the per-target field;
  `Completed`/zero restart guidance plus `Completed` with null/nonzero exit,
  `NotFound`, `Failed`, and `TimedOut` outcomes with no restart guidance or
  plan failure; no retry/sleep; `--status` raw-output non-leakage; JSONC
  preservation; revalidation happy/differing rows with zero `--status` calls
  and unchanged persisted per-target restart requirements. Add a production-
  path secret-marker integration test: seed the deterministic file-system fake
  with a real bounded existing `settings.json` byte sequence containing the
  marker in an unrelated member and positively assert the source bytes contain
  it; use the real VS Code partition to Plan the tagged carrier; persist, close,
  and reopen the private plan; apply through the generic coordinator so
  revalidation materializes the document; and positively assert the private
  prior-state backup contains the marker. Assert the marker is
  absent from the record/tagged carrier, serialized private plan, ledger,
  journal, result, log, exception/error text, and both committed ownership-
  ledger/private-plan fixtures. Add exact
  tagged-union tests: accept only the two exact `SetupTargetKind.Json`
  `github-copilot` VS Code labels; reject tagged `SetupTargetKind.File`/
  `SetupTargetKind.Toml`/other-adapter/other-label records and an inline arm for
  those VS Code JSON records as `recovery_required`; exact property sets and
  canonical order; 1:1 ordered unique owned values/members; boolean/string
  value types and exact string boundaries 0/1/2048/2049 UTF-16 units; lowercase
  expected hash; unknown/malformed/noncanonical union rejection as
  `recovery_required`. Add 1 MiB/1 MiB+sentinel settings
  boundaries for plan and `Revalidate`, both `malformed_settings` with no
  unbounded read/retry/artifact/write. Add transient-materialization tests for
  changed/no-op cardinality, exact record IDs/order/hash, comment/unowned-byte
  preservation, hash mismatch → `recovery_required`, and a still-supported
  version change → `recovery_required` with zero artifacts/writes. Extend the
  production-path marker test through a deterministic crash boundary and
  close/reopen recovery: recovery must make zero partition revalidation/
  materialization calls, use the expected hashes plus marker-bearing backup,
  and keep the marker absent from private plan/ledger/journal/result/log/error/
  fixture evidence. Task-04c owns exhaustive generic crash windows; this task
  supplies the real VS Code partition integration proof.

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~VsCodeSetupAdapterTests
```

- [ ] **Step 3: Implement the partition** (detection composition → policy
  gate → endpoint gate → per-channel JSONC member planning → records +
  diagnostics; revalidation reuses the same checks against persisted facts).

- [ ] **Step 4: Run GREEN + full ConfigCli + build + `git diff --check`.**

- [ ] **Step 4a: Verify scope before staging.** The worktree/branch field,
  `git status --short`, and `git diff --name-only` must show only
  `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/VsCode/VsCodeTargetPartition.cs`
  and `tests/CopilotAgentObservability.ConfigCli.Tests/VsCodeSetupAdapterTests.cs`.
  Otherwise stop rather than staging another worker's change.

- [ ] **Step 5: Commit.**

```powershell
git add -- src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/VsCode/VsCodeTargetPartition.cs tests/CopilotAgentObservability.ConfigCli.Tests/VsCodeSetupAdapterTests.cs
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
  the profile no-open, 1 MiB-plus-sentinel, tagged-union, transient
  materialization, supported-version-drift, and non-vacuous production-path
  secret-marker proof with positive source/backup and negative durable evidence
  assertions plus representative crash recovery.
- Root/branch and status/diff scope gates show only the two exact owned files;
  no T3-owned or shared file is edited.
- Full ConfigCli suite and build pass; independent review PASS. This task
  does NOT claim end-to-end #67 behavior — that is task-08's gate.

**Report destination:** chat only. Do not edit the sprint ledger during this
correction task; a separate post-review documentation step owns it. Record
fake-only macOS/Linux path coverage as unverified scope.

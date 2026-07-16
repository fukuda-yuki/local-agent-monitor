# Configuration Setup Interface

This document is the canonical contract for Issue #66 configuration ownership,
Issue #67 GitHub Copilot guided setup, and Issue #68 Claude Code guided setup.
It defines the public command/result surface, durable ownership record,
transaction semantics, and supported agent targets.

## Scope

Issue #66 supplies a reusable setup framework. Issue #67 supplies the
`github-copilot` adapter for:

- VS Code GitHub Copilot Chat;
- terminal GitHub Copilot CLI;
- caller-managed GitHub Copilot App / SDK integration guidance.

Issue #68 supplies the `claude-code` adapter for:

- interactive Claude Code CLI and `claude -p`, which share user settings;
- caller-managed Python and TypeScript Agent SDK guidance;
- Windows-native and explicitly opted-in WSL2 repository execution.

The framework does not add a Local Monitor HTTP route, Canvas proxy, Razor UI,
remote provisioning, first-trace verification, force rollback, or machine-wide
configuration. Issue #68 also excludes native macOS/Linux installation, a
Windows-to-WSL settings bridge, remote collectors, and Codex setup.

## Cross-surface contract table

| Surface | Producer | Consumer | Contract | Setup status |
| --- | --- | --- | --- | --- |
| CLI command dispatch | `CliApplication` / `SetupCommandDispatcher` | `SetupAdapterRegistry`, transaction coordinators, and status service | parsed `SetupOptions`; private plans, ledger records, recovery, and transaction results remain internal | required |
| Adapter registry | `SetupAdapterRegistry` | registered `ISetupAdapter` | `github-copilot` or `claude-code` selection plus typed `Plan` and `Revalidate` carriers; adapters do not produce the public DTO | required |
| GitHub Copilot aggregate | `GitHubCopilotSetupAdapter` | VS Code, Copilot CLI, and App/SDK target partitions | typed partition plan/revalidation carriers and bounded platform observations; no `Detect` method or public DTO reconstruction | required |
| Claude Code aggregate | `ClaudeCodeSetupAdapter` | CLI/settings, WSL2 detection, and Agent SDK guidance partitions | `SetupPlanRequest` to typed plan/revalidation carriers; nested settings ownership and endpoint/process facts stay private | required for #68 |
| Apply revalidation | persisted private plan + registered owning adapter | `SetupApplyCoordinator` | endpoint/policy/version/extension/member revalidation and materialized-target result; no public DTO reconstruction | required |
| CLI result | `SetupCommandDispatcher` through `CliApplication` / `SetupJson` | terminal user / PowerShell wrapper | the sole public result is `SetupCommandResult` JSON (`setup.v1`) | required |
| PowerShell | repository or Release ZIP `setup.ps1` | Config CLI executable | exact CLI arguments and unchanged JSON stdout | required |
| HTTP | existing Local Monitor | Claude endpoint probes, OTel exporter, and installed `hook-forward` | existing `/health/ready`, `/v1/traces`, and `/api/session-ingest/v1/events`; no new route or DTO | reused, unchanged |
| proxy | repository or Release ZIP `setup.ps1` | Config CLI executable | byte-faithful argument, stdout, and exit-code forwarding, including `--allow-wsl2-routing`; no reshaping | required |
| Local Monitor UI | none | none | no view model | not added |

Mocks and fixtures must be serialized from the same C# result types used by the
CLI. A hand-written HTTP, proxy, or UI facsimile is not an acceptable
cross-surface test because those surfaces are not part of this interface.

## Public commands

```text
config-cli setup plan --adapter github-copilot --target <vscode|cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture]
config-cli setup plan --adapter claude-code --target <cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture] [--allow-wsl2-routing]
config-cli setup apply --change-set <uuid-v7>
config-cli setup rollback --change-set <uuid-v7>
config-cli setup status [--adapter <id>]
```

The `setup.v1` surface is recognized only after the first token is exactly
`setup` and the second token is exactly one of `plan`, `apply`, `rollback`, or
`status`. Once a verb is recognized, every option/arity/value failure returns
that command's repository-safe `setup.v1` result with `code=invalid_arguments`.
A bare `setup` or `setup <unknown-verb>` is a special invalid setup invocation:
it writes no stdout JSON, writes exactly `invalid_arguments` plus one newline to
stderr, exits `2`, and performs no help rendering, lock acquisition, recovery,
registry lookup, or storage read. An unknown top-level token other than `setup`
remains outside this interface and preserves the existing Config CLI
exit-`1`/help behavior.

`--adapter` values are parsed as lowercase ASCII slugs matching
`[a-z0-9]+(?:-[a-z0-9]+)*`, bounded to 1 through 128 UTF-16 code units as in the
generic adapter registry. The parser validates only this safe shape; it does
not hard-code `github-copilot` or consult the registry. For `plan`, registry
resolution happens only after the command owns the setup lock and mandatory
recovery has returned no work. An unregistered well-formed slug then produces
`unsupported_adapter`. The owning adapter, not the generic parser, validates
the selected target. For `status`, the optional well-formed adapter slug is an
exact historical ledger filter and is never resolved against the current
registry, so status remains usable after an adapter is removed.

`--endpoint` defaults to `http://127.0.0.1:4320` and accepts only a loopback HTTP
origin with an explicit port. Hosts are exactly `127.0.0.1`, `localhost`, or
`::1`; userinfo, a non-root path, query, and fragment are forbidden. CLI input
may use case-equivalent scheme/host spelling and one root slash, but normalizes
before plan creation to lowercase `http`, lowercase host, bracketed IPv6, and no
trailing slash. Public DTOs and persisted plans accept only the canonical
`http://<loopback-host>:<port>` form. `all` means the three Issue #67 targets at
plan time; apply still requires the returned change-set ID. App/SDK remains a
no-write target.

`setup plan` persists an immutable private plan under the user runtime root and
returns its change-set ID. `setup apply` accepts only that ID; it never rebuilds
or silently updates the plan. A changed base hash yields `stale_plan` and no
write. `setup rollback` addresses one applied change set. `setup status`
requests no new setup mutation, but the mandatory recovery phase can restore an
interrupted transaction before returning status.

The PowerShell wrapper exposes the same four actions and forwards stdout
without reshaping it. Repository mode invokes the Config CLI project. Release
ZIP mode invokes the self-contained Config CLI published under
`app/config-cli/`. Using the Release ZIP must not require a .NET SDK or runtime.
The wrapper forwards `--allow-wsl2-routing` without interpretation. Windows
Release ZIP execution does not claim to mutate WSL settings; Issue #68 WSL2
support is repository-run from inside WSL2.

For `claude-code`, `cli` covers both interactive CLI and `claude -p` and `all`
means CLI plus Agent SDK guidance. `--allow-wsl2-routing` is required only when
the Config CLI itself runs in a verified WSL2 process. It is
`invalid_arguments` for Windows-native execution, for native macOS/Linux, and
for every adapter other than `claude-code`. It authorizes no gateway,
non-loopback, or Host-header fallback.

## Result contract

Each recognized exact setup verb (`plan`, `apply`, `rollback`, or `status`)
returns one `SetupCommandResult` JSON object on stdout. The stable top-level
shape is:

```json
{
  "contract_version": "setup.v1",
  "command": "plan",
  "success": true,
  "code": "plan_ready",
  "change_set_id": "00000000-0000-7000-8000-000000000000",
  "recovered_change_set_id": null,
  "recovery_operation": null,
  "adapter": "github-copilot",
  "targets": [],
  "change_sets": [],
  "warnings": [],
  "next_actions": [],
  "truncated": false
}
```

Required fields:

| Field | Type | Rule |
| --- | --- | --- |
| `contract_version` | string | exactly `setup.v1` |
| `command` | enum | `plan`, `apply`, `rollback`, or `status` |
| `success` | boolean | command outcome, not first-trace outcome |
| `code` | fixed string | one of the codes below |
| `change_set_id` | UUIDv7 string or null | newly created plan ID, or the requested apply/rollback ID; null for status and for plan when recovery returns before planning |
| `recovered_change_set_id` | UUIDv7 string or null | interrupted change set recovered before the requested command, otherwise null |
| `recovery_operation` | enum or null | `apply` or `rollback` for `recovered_change_set_id`, otherwise null |
| `adapter` | string or null | normalized requested or persisted adapter ID; an apply may return the persisted ID with `unsupported_adapter` when that adapter is no longer registered |
| `targets` | array | bounded to 16 physical targets and sorted by plan order |
| `change_sets` | array | status entries; empty for other commands |
| `warnings` | fixed string array | no exception text or raw values |
| `next_actions` | fixed string array | bounded actionable codes |
| `truncated` | boolean | meaningful for status; otherwise false |

After the mandatory recovery phase, the requested-command composition is
closed, including these non-default combinations. Recovery may already have its
separately correlated documented effect; the no-mutation statements below
apply to the requested command after recovery returns control:

| Command situation | Required result | Mutation/artifact rule |
| --- | --- | --- |
| `plan` names an adapter that is not registered | `success=false`, `code=unsupported_adapter`, `adapter=<requested-id>`, `change_set_id=null` | no plan, ledger row, backup, journal, or target write |
| `apply` loads a valid persisted plan whose owning adapter is no longer registered | `success=false`, `code=unsupported_adapter`, `adapter=<persisted-id>`, requested `change_set_id` retained | the requested plan and its ledger row remain byte-for-byte unchanged; no backup, journal, lifecycle transition, endpoint/platform probe, or target write |
| `apply` loads a Copilot CLI plan created on macOS or Linux | `success=false`, `code=unsupported_target`, `adapter=github-copilot`, requested `change_set_id` retained | the requested plan and its ledger row remain byte-for-byte unchanged; no shell-profile file, backup, journal, lifecycle transition, environment notification, or target write |

These `apply`/code pairs are valid `setup.v1` results. A generic result
validator must not reject them merely because older command matrices associated
`unsupported_adapter` or `unsupported_target` only with planning.

For a recognized command, the generic command producer owns the one public
`SetupCommandResult`; adapters never serialize another result shape. Adapter
planning returns a typed carrier containing its ordered targets, closed
warnings, and closed next actions, or a sanitized typed failure containing one
allowed result code plus the same two closed arrays. Apply-time adapter
revalidation uses the same success/failure carrier for fresh warnings and next
actions. The command producer validates and copies those arrays without parsing
an exception message, inventing a replacement action, or dropping an allowed
adapter diagnostic. Unexpected exceptions map to `internal_error` with empty
arrays. Framework-owned outcomes also have empty arrays except where this
contract explicitly assigns one: successful interrupted recovery adds exactly
`rerun_requested_setup_command`. The two exceptional apply results above keep
both arrays empty.

Normal non-status target projection is closed as follows. A normal requested
outcome is one reached after successful syntax, lock, recovery, and applicable
row/private-plan validation; lock/storage/recovery failures, a missing requested
row, and the exceptional apply results above return an empty `targets` array.
All non-status targets keep `reference_state` and `current_state` null.

| Requested command/outcome | Immutable target source and order | Target `rollback_available` |
| --- | --- | --- |
| `plan` / `plan_ready` or `no_changes` | the validated adapter plan carrier, in adapter physical-target order; the same order is persisted in the private plan and ledger row | prospectively true only for a writable physical target with at least one non-`no-op` member whose successful apply would establish backup-backed ownership; false for an all-`no-op` target or guidance. This is capability of the planned mutation, not a claim that rollback can run before apply |
| normal `apply` result | the requested row's immutable ledger target fields and `status_projection`, in ledger plan order; guidance rehydrates only the fixed public sample; the adapter does not recreate target DTOs | true only after `apply_succeeded` when that target has complete actual applied-hash, safe-backup, and pending-rollback ownership; false for `no_changes`, `partial_apply`, or any failed apply result, and false for all-`no-op`/guidance targets |
| normal `rollback` result | the requested row's same immutable ledger projection, in ledger plan order; no adapter detection or DTO reconstruction | always false, including `rollback_succeeded`, `rollback_stale`, `rollback_not_available`, and `partial_rollback` |

Each target record contains:

```json
{
  "record_id": "00000000-0000-7000-8000-000000000000",
  "target_kind": "json",
  "target_label": "vscode-stable-default-user-settings",
  "detected": true,
  "detected_version": "1.128.0",
  "operation": "replace",
  "effective_source": "user_setting",
  "reference_state": null,
  "current_state": null,
  "restart_requirement": "restart_vscode",
  "rollback_available": true,
  "endpoint": "http://127.0.0.1:4320",
  "expected_result": null,
  "guidance": null,
  "changes": [
    {
      "setting_key": "github.copilot.chat.otel.otlpEndpoint",
      "operation": "replace",
      "previous_state": "present_different",
      "new_state": "configured_loopback",
      "conflict": "none",
      "managed": false
    }
  ]
}
```

`target_kind` is `env`, `json`, `toml`, `startup_task`, `file`, or
`guidance`. A target record is one physical mutation boundary: one file, one
current-user environment allowlist, one startup task, or one no-write guidance
result. `detected` reports bounded target/package presence and
`detected_version` is a sanitized semantic version string of at most 128 UTF-16
code units (`string.Length`) or null; neither may contain a path or free-form
command output. `operation` is `add`, `replace`,
`remove`, `mixed`, or `no-op`. It is `no-op` when every member is no-op, the
single non-no-op operation when all changed members agree, and `mixed` when two
or more non-no-op member operations differ. Each physical
target contains at most 32 member `changes`; member `previous_state` and
`new_state` are redacted state names, never raw setting values. The `endpoint`
field is allowed only for the canonical explicit-port loopback HTTP origin
defined above;
foreign endpoints are reported only as `loopback`, `remote`,
`credential_bearing`, or `invalid` state.

`expected_result` is copied from the matching Issue #61 manifest without
inventing capabilities. A newly produced plan must match the currently embedded
canonical manifest exactly, with JSON property order ignored. VS Code uses
surface `github-copilot-vscode`; terminal CLI uses `github-copilot-cli`.
Claude CLI uses `claude-code`. App/SDK guidance has no Issue #61 manifest and
returns a null manifest with
`planned_source_not_enabled` guidance rather than borrowing another surface's
capability declaration. Ledger-origin status validation is deliberately
separate, as defined below, so a valid immutable historical snapshot is not
silently rewritten or rejected only because the embedded manifest later
changes.

`restart_requirement` is `none`, `restart_vscode`,
`restart_terminal_session`, or `restart_agent_process`. Claude Code writable
settings use `restart_agent_process`; their top-level next action is
`restart_claude_process`.

`reference_state` and `current_state` are null in plan/apply/rollback target
results. In status, `reference_state` is `base`, `desired`, `previous`, or
`none`; `current_state` is `current`, `stale`, `diverged`, `unavailable`, or
`not_applicable`. `changes` is empty only for a `guidance` target. `guidance` is null for writable
targets. Guidance uses the bounded shape
`{"kind":"caller_managed_sample","language":"<fixed-language>","sample":"..."}`.
The fixed language is `dotnet` for GitHub Copilot and `python` or `typescript`
for Claude Agent SDK. It never contains a discovered caller path or file
content.

### Status projection

After mandatory recovery and any failed-recovery overlay, `setup status`
applies the optional exact adapter-ID filter before ordering or counting. It
then assigns each eligible row to one priority class: (1)
`partial`, `applying`, `compensating`, or `rolling_back`; (2) `planned`; or (3)
terminal (`applied`, `no_changes`, `restored`, or `rolled_back`). The four
states in class 1 have equal priority; they are not
sub-ranked by state name. Within each class rows sort by `updated_at` descending,
then by the canonical lowercase UUID string using ordinal ascending comparison.
The command returns the first `min(100, eligible_count)` rows and sets
`truncated` exactly to `eligible_count > returned_count`. Filtering therefore
precedes priority, ordering, cap, and truncation. Recovery-blocking rows are
prioritized but never exempt from the hard 100-row cap.

Each status entry has exactly `change_set_id`, `adapter`, `selected_target`,
`created_at`, `updated_at`, `state`, nullable `outcome_code`, `current_state`,
`rollback_available`, and `targets`. Its target summaries use the same bounded
public target DTO but omit `guidance.sample` after the original plan result;
they retain only `guidance.kind` and `guidance.language`.

Ledger v1 persists an immutable repository-safe `status_projection` snapshot
inside every physical target row when the planned row is created. Lifecycle
updates never rewrite it. Status does not rerun adapter detection or reconstruct
immutable DTO fields from a current adapter, private value, path, or command
output. The backend-to-CLI source contract is:

| Public status field | Internal backend source | Status rule |
| --- | --- | --- |
| top-level `contract_version`, `command`, `change_set_id`, `targets` | fixed result contract | `setup.v1`, `status`, null, and empty respectively |
| top-level `success`, `code`, `recovered_change_set_id`, `recovery_operation`, `warnings`, `next_actions` | requested status outcome plus the mandatory recovery result | fixed-code/correlation rules in this document; never inferred from target content |
| top-level `adapter` | normalized optional status filter | exact adapter ID when filtered, otherwise null |
| top-level `change_sets`, `truncated` | filtered/ordered/capped ledger projection | ordering and count rule above |
| change-set `change_set_id`, `adapter`, `selected_target`, `created_at` | immutable ledger change-set fields | copied exactly |
| change-set `updated_at`, `state`, `outcome_code` | current ledger lifecycle fields, with the documented failed-recovery overlay | copied after recovery/overlay, never taken from the private plan |
| change-set `targets` | immutable ledger target order | no adapter rediscovery, insertion, or reordering |
| target `record_id`, `target_kind`, `target_label`, `restart_requirement` | immutable ledger target fields | copied exactly |
| target `detected`, `detected_version`, `operation`, `effective_source`, `endpoint`, `expected_result` | immutable ledger `status_projection` | plan-time safe facts; ledger-origin `expected_result` uses the historical validation below, not current-manifest equality; these fields are not claims about later installation, policy, precedence, endpoint ownership, or manifest changes |
| target `guidance` | immutable ledger `status_projection` plus fixed contract sample | persist plan-time `kind` and `language` only; rehydrate the one fixed in-memory sample required by the DTO/validator, while status JSON still omits `sample` and never reopens a caller path |
| target `changes[].setting_key`, `operation`, `previous_state`, `new_state`, `conflict`, `managed` | immutable ordered ledger `status_projection.changes` | copied exactly; states/conflict are fixed redacted names and `managed` is the plan-time observation |
| target `reference_state`, `current_state` | lifecycle/journal plus fresh private-plan/current-target classification | recomputed on every status request after recovery under the matrix below |
| target `rollback_available` | fresh lifecycle, ownership, current-target, private-plan, and backup verification | recomputed on every status request; the persisted rollback status alone is insufficient |
| change-set `current_state` | current target results | recomputed by the aggregation below |
| change-set `rollback_available` | shared fresh rollback preflight | recomputed from the owned target/backup quorum plus every unowned all-NoOp target's base-state guard |

The snapshot stores only public-safe immutable facts. `detected_version` remains
a sanitized semantic version of at most 128 UTF-16 code units or null;
`endpoint` remains a canonical credential-free loopback origin or null;
`expected_result` is the canonical Issue #61 manifest object copied at plan time
or null; guidance stores no sample; and member state/conflict names remain
fixed redacted identifiers. The
snapshot contains no desired/previous value, target location, command output,
credential, token, authorization header, raw exception, prompt/response, tool
content, or PII.

Ledger-origin `expected_result` validation is a separate strict path. It
requires the exact source-capability v1 object shape with no unknown or
duplicate fields, `contract_version: "v1"`, the fixed
`github-copilot-vscode`, `github-copilot-cli`, or `claude-code` public surface
matching that snapshot target, the matching canonical `source_adapter`, the
schema's closed support/stability/availability codes and fixed
provenance/completeness arrays, and all source-capability safety and cross-field
invariants. No free-form manifest string remains. Guidance must keep
`expected_result` null. A violation is `ledger_corrupt`. A schema-valid,
target-matched historical manifest is not required to equal the currently
embedded manifest and is never replaced with current facts. This exception
applies only to an immutable ledger snapshot; new plan output continues to use
exact-current canonical matching.

Status uses lifecycle-relative reference state for each writable target:

| Change-set lifecycle | Target `reference_state` |
| --- | --- |
| `planned` | `base` |
| `no_changes` | `desired` (identical to the observed base) |
| `applied` | `desired` |
| `restored` or `rolled_back` | `previous` |
| `partial` | freshly classified aggregate: `desired` when every member/file is desired, `previous` when every member/file is previous, or `none` for a safe mixture/third-party state or unavailable classification |
| `applying`, `compensating`, or `rolling_back` | freshly checked journal-derived `base`, `desired`, or `previous` when the whole target has one reference; otherwise `none`; a failed recovery is projected effectively as `partial` |

For each returned row, status resolves a target location only from its private
immutable plan, uses the same no-follow/path/hash/member classification as
apply/recovery/rollback, and observes the current target during that request. It
does not trust a previously persisted current hash as proof that the target is
still current. For `base`, `desired`, or `previous`, `current` means the freshly
observed canonical state equals that reference and `stale` means a safely known
mismatch, including a missing current value when the typed reference is not
missing.

`diverged` means the transaction safely classified a target that has no single
aggregate reference: desired/previous members are mixed, or at least one
third-party member/file was preserved. Its reference is `none`. `unavailable`
means at least one member/file could not be safely verified or classified, also
with reference `none`. Guidance is
`not_applicable` with reference `none`.

A missing private plan for a non-terminal change set is `recovery_required` and
prevents projection. For a terminal row, a missing/unreadable required private
plan or unsafe/unreadable current target makes that target `unavailable` with
reference `none` and rollback unavailable; status never guesses or reruns the
adapter. A missing current file/member that can be represented by the typed
canonical state is a known state, not an I/O failure, and therefore compares as
`current` or `stale` normally.

Change-set `current_state` is `diverged` if any writable target is diverged,
otherwise `stale` if any is stale, otherwise `unavailable` if any is
unavailable, otherwise `current` when all writable targets are current, and
`not_applicable` when there are no writable targets. Guidance targets do not
participate in current-state aggregation.

A rollback-participating target is a writable physical target with at least one
non-`no-op` member and current ledger ownership: an applied hash, opaque backup
reference, and pending rollback status created by the successful apply. A
writable target whose members are all `no-op` has no applied hash, backup, or
ownership; it remains visible and participates in current-state aggregation but
reports target `rollback_available=false` and is excluded from the ownership and
backup quorum. It is not excluded from the change-set-wide fresh preflight
guard. A mixed target with any changed member participates as one physical
target, and its full immutable member list, including no-op members, remains
part of its aggregate stale guard.

For status, a participating target reports `rollback_available=true` only when
the change-set lifecycle is `applied`, its fresh current state equals the
applied/desired aggregate, its ledger ownership fields are internally
consistent, and its private plan and safe regular non-reparse backup are
present and verify against the recorded previous-state hash. The persisted
rollback status or backup reference alone never proves availability.

Change-set rollback availability uses the same pure preflight evaluation as
`setup rollback` at the state observed by that status request: immutable
plan/ledger/journal identity and lifecycle must agree; at least one changed
physical target must have ownership; every owned target must have internally
consistent applied hash, pending rollback status, journal evidence, and a safe
backup matching its previous-state hash; every owned target must still equal
its applied state; and every unowned all-`no-op` physical target must still
equal its immutable base state. The unowned target does not need a backup and
does not join the ownership quorum, but any fresh guard mismatch makes the
whole change set unavailable, exactly as rollback would return no-write
`rollback_stale`. Guidance never participates. Subject only to a concurrent
change after observation, status reports change-set `rollback_available=true`
if and only if an immediately invoked rollback would pass that preflight. Every
`partial` change set reports
`rollback_available=false`, including an all-desired or all-previous partial
whose unresolved transaction outcome has not reached a terminal lifecycle.

A corrupt or unsupported ledger fails the whole command with `ledger_corrupt` or
`ledger_version_unsupported`; it is not treated as an empty ledger. Status is
the only command allowed while an unresolved partial recovery exists.

`status` performs no newly requested setup mutation, but mandatory interrupted-
transaction recovery may restore configured targets. Successful recovery uses
the corresponding `interrupted_*_recovered` code and still returns the bounded
status projection. Failed recovery with a readable ledger returns
`success=false`, `code=interrupted_recovery_failed`, both recovery-correlation
fields, and the bounded projection. The failed-recovery overlay is formed before
the optional exact adapter-ID filter. When the affected entry is eligible, it is
effectively shown as `partial` and
`outcome_code=interrupted_recovery_failed`, even if persisting that final ledger
update failed. When the filter does not match the affected entry's adapter, the
entry is omitted while the top-level failure code and recovery correlation are
preserved. An unreadable, corrupt, or unsupported ledger and lock contention
return no status projection.

## Fixed result and error codes

Success codes:

- `plan_ready`
- `no_changes`
- `apply_succeeded`
- `rollback_succeeded`
- `status_ready`
- `interrupted_apply_recovered`
- `interrupted_rollback_recovered`

Failure codes:

- `invalid_arguments`
- `unsupported_adapter`
- `unsupported_target`
- `target_not_installed`
- `unsupported_version`
- `managed_policy_conflict`
- `environment_override_conflict`
- `malformed_settings`
- `permission_denied`
- `unsafe_path`
- `stale_plan`
- `rollback_stale`
- `rollback_not_available`
- `port_owned_by_foreign_process`
- `endpoint_unreachable`
- `hook_command_conflict`
- `content_policy_conflict`
- `wsl2_opt_in_required`
- `wsl2_routing_unavailable`
- `partial_apply`
- `partial_rollback`
- `setup_busy`
- `recovery_required`
- `interrupted_recovery_failed`
- `ledger_corrupt`
- `ledger_version_unsupported`
- `internal_error`

The process exit mapping is exhaustive over all 34 result codes:

| Exit | Exact result codes |
| --- | --- |
| `0` | `plan_ready`, `no_changes`, `apply_succeeded`, `rollback_succeeded`, `status_ready`, `interrupted_apply_recovered`, `interrupted_rollback_recovered` |
| `2` | `invalid_arguments` |
| `3` | `managed_policy_conflict`, `environment_override_conflict`, `hook_command_conflict`, `content_policy_conflict`, `stale_plan`, `rollback_stale` |
| `4` | `unsupported_adapter`, `unsupported_target`, `target_not_installed`, `unsupported_version`, `rollback_not_available`, `port_owned_by_foreign_process`, `endpoint_unreachable`, `wsl2_opt_in_required`, `wsl2_routing_unavailable` |
| `5` | `malformed_settings`, `permission_denied`, `unsafe_path`, `partial_apply`, `setup_busy`, `recovery_required`, `interrupted_recovery_failed`, `ledger_corrupt`, `ledger_version_unsupported`, `internal_error` |
| `6` | `partial_rollback` |

A recognized verb writes one result JSON object to stdout. Its successful result
writes nothing to stderr; its failed result writes exactly that fixed result
code plus one newline to stderr. The bare/unknown-setup-verb exception is the
no-JSON behavior specified under Public commands. No raw exception text or help
text may be appended to a setup result.
`environment_override_conflict` is valid only for a CLI-target `plan` or
pre-artifact `apply` revalidation.

The Issue #68 codes are also pre-artifact/no-write outcomes.
`endpoint_unreachable` means the selected Windows-native Claude endpoint did
not satisfy the bounded readiness reachability check;
`wsl2_routing_unavailable` is the equivalent after a verified WSL2 explicit
opt-in. `wsl2_opt_in_required` means the context is verified WSL2 but the flag
is absent. `hook_command_conflict` means the exact owned Hook slot exists with
different private command/args/timeout. `content_policy_conflict` means an
observed higher-priority content gate contradicts the requested explicit
capture policy. None creates or changes a private plan, backup, journal, target,
or ledger lifecycle row for the requested operation.

## Runtime storage and versioning

The private runtime root is available on every supported planning OS so a
macOS/Linux CLI plan can be persisted before a later apply returns
`unsupported_target`:

| OS | Default private runtime root |
| --- | --- |
| Windows | `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\setup\` |
| macOS | `$HOME/Library/Application Support/CopilotAgentObservability/LocalMonitor/setup/` |
| Linux | `${XDG_DATA_HOME:-$HOME/.local/share}/CopilotAgentObservability/LocalMonitor/setup/` |

Production resolves the base with
`Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)`.
On Linux, .NET uses an absolute non-empty `XDG_DATA_HOME`; an unset, empty, or
non-absolute value falls back to `$HOME/.local/share`. An injected
`ISetupPlatform.LocalApplicationData` replaces only that base for a trusted
host/test platform. There is no `setup` CLI flag or setup-specific environment
variable that overrides this root.

Layout:

```text
setup/
  ownership-ledger.v1.json
  setup.lock
  plans/<change-set-id>.json
  backups/<change-set-id>/<record-id>.backup
  transactions/<change-set-id>.journal.json
```

The ledger is repository-safe metadata. Plans, backups, and journals are
private local runtime data. Plans contain exact target locations and validated
desired values but never read and retain previous values. Exact previous state
is captured only in flushed apply-time backups after all stale checks pass.
These artifacts must never be copied into logs, command output,
CI artifacts, docs, Issues, or committed files. The default retention is
indefinite so rollback remains available. Automatic cleanup is not part of
Issues #66/#67/#68.

The ownership ledger retains its existing hard limit of 1 MiB for the complete
serialized file, on bounded read and before atomic replacement. This is the one
storage cap; `status_projection` adds no second per-row or per-snapshot cap. All
snapshot fields are nevertheless strictly shaped and bounded, including the
128-UTF-16-code-unit `detected_version`, so the largest legal single change set
(16 targets, 32 changes per target, and the largest accepted safe snapshot
fields) is finite and must serialize below 1 MiB in an executable boundary
test. Indefinite retention does not imply unlimited history: once appending a
legal row would exceed the ledger cap, the write fails closed without replacing
the durable ledger or leaving a claimed change set. That finite local-history
capacity is an accepted constraint for Issues #66/#67/#68; automatic pruning,
retention policy, and a larger or second cap are not introduced here.

Schema version `1` is the first shipped ownership-ledger version. There is no
fabricated v0 migration fixture. The loader accepts exactly version 1 and fails
closed with `ledger_version_unsupported` for an unknown version. Tests must
write, close, reopen, and verify the shipped v1 state. A later schema version
must add fixtures from every actually shipped older version and verify migration
through restart. The status projection is being added before ledger v1 ships,
so it is a required v1 field rather than a migration or optional extension. A
v1 target missing it, containing unknown projection fields, or violating its
cross-field invariants is `ledger_corrupt`; the loader does not fall back to
adapter rediscovery, a private-plan reconstruction, or an older v1 shape.

Private plan `desired_state` is also a closed schema-v1 JSON union. It is not a
schema-v2 change, a migration, a compatibility layer, or a parse fallback:
the bound target adapter/kind/label and tagged `kind` select exactly one of the
three v1 representations, and no other representation is accepted.

- A JSON string is the canonical v1 inline representation for historical
  private-plan bytes and generic non-tagged file, TOML, and opaque targets. It
  is not a fallback. The existing committed real ownership-ledger v1 fixture
  (`Fixtures/Setup/v1/ownership-ledger.v1.json`) remains unchanged and
  restart-readable as ledger evidence. Before changing the plan serializer,
  task-04b must add a separate committed production-serializer private-plan v1
  fixture containing this legacy string `desired_state`; production
  `SetupPlanStore` must write, close, reopen, and byte-compare it exactly. No
  conversion, rewrite, or synthetic migration of either fixture is permitted.
- A JSON object is a new JSONC-owned-values representation. It has exactly the
  properties `kind`, `expected_state_hash`, and `owned_values`, in that
  canonical write order. `kind` is exactly `jsonc_owned_values_v1` and
  `expected_state_hash` is exactly one lowercase 64-hex SHA-256 value. Its
  `owned_values` array has 1 through 32 entries, each with exactly
  `setting_key`, `value_kind`, and `value` in that write order. Keys are unique,
  are ordered exactly as the target's ordered members, and correspond 1:1 to
  those members. `value_kind` is exactly `boolean` (a JSON boolean `value`) or
  `string` (a JSON string `value` of exactly 1 through 2,048 UTF-16 code
  units). No
  unknown property, duplicate key, wrong JSON value type, empty/over-bound
  array or value, member mismatch, noncanonical hash, or malformed JSON is
  tolerated.
- A JSON object with `kind: "claude_settings_owned_values_v1"` is the Claude
  user-settings representation. It contains exactly `kind`,
  `expected_state_hash`, `owned_env`, and `owned_hooks` in canonical write
  order. The lowercase 64-hex hash covers the complete desired settings bytes.
  `owned_env` is an ordered 1..8-entry array whose unique keys map 1:1 to the
  target's env members and whose string values are 1..2,048 UTF-16 units.
  `owned_hooks` is the ordered 11-event array defined under the Claude adapter;
  each record contains exactly its event name, executable command, ordered
  arguments, and `timeout_seconds: 5`. The command and arguments remain private
  plan data. Missing, duplicate, unknown, reordered, over-bound, noncanonical,
  or adapter/kind/label-mismatched data is `recovery_required` before target
  activity.

For the Claude settings target, private-plan and public projection members use
one closed order and naming rule. Owned env members come first as
`env.<ENV_NAME>`, followed by all Hook members as `hooks.<EventName>` in the
canonical Hook order. A default plan therefore has 5 env plus 11 Hook members
(16 total); explicit content capture has 8 env plus 11 Hook members (19 total).
The three content-gate keys are preserved but are not members when explicit
capture is absent. `owned_env` stores the env name without the `env.` prefix
and maps 1:1 in order to the env-member slice; `owned_hooks` stores the event
name and maps 1:1 in order to the Hook-member slice.

The tagged arm is valid only for a `SetupTargetKind.Json` target owned by the
`github-copilot` adapter with label exactly
`vscode-stable-default-user-settings` or
`vscode-insiders-default-user-settings`: the two new VS Code Default Profile
JSONC records. That target-kind/adapter/label arm relation is validated when an
adapter record becomes the bound private plan and ledger record. These VS Code
JSON records use only the tagged object; `SetupTargetKind.File`,
`SetupTargetKind.Toml`, any other adapter or label, and every generic
non-tagged file/TOML/opaque target retain or require the canonical inline-string
arm. This is required v1 selection, not a fallback or migration path. The
tagged object preserves only the owned desired values and expected complete-file
hash, not a persisted rendered settings document or unowned input. A malformed,
unknown, invalid target-kind/arm, or inline arm on either VS Code JSON record is
`recovery_required`, before registry/platform/target work.

The Claude tagged arm is valid only for a `SetupTargetKind.Json` target owned
by `claude-code` with label exactly `claude-code-user-settings`. That record
must use `claude_settings_owned_values_v1`; it cannot use inline or
`jsonc_owned_values_v1`. The legacy inline and GitHub JSONC tagged fixture bytes
remain unchanged. This is an additive arm in the same unshipped private-plan v1
contract, not a migration, fallback, or schema-v2 path.

Restart compatibility must use the two actual committed v1 fixtures created by
the Issue #66/#67 production serializers. A fresh production composition loads
them without rewriting their bytes, closes and reopens storage, and can project
status and run the eligible apply/rollback lifecycle of the historical GitHub
plan after the Claude arm is registered. Adding and reopening a new Claude plan
must not change the historical fixture bytes or state transitions. No fabricated
older version or permissive parse path is allowed.

## Ownership ledger v1

The ledger root has `schema_version: 1` and a `change_sets` array. Every change
set contains:

- UUIDv7 `change_set_id`;
- adapter ID and selected target;
- `created_at` and `updated_at` UTC timestamps;
- `tool_version`;
- nullable fixed `outcome_code` for the change-set result;
- state: `planned`, `applying`, `applied`, `no_changes`, `compensating`,
  `restored`, `rolling_back`, `partial`, or `rolled_back`;
- ordered physical-target records.

Every record contains:

- UUIDv7 `record_id`;
- `target_kind`;
- fixed sanitized `target_label`;
- `owning_adapter`;
- ordered member setting keys and operations;
- previous-state SHA-256;
- applied-state SHA-256;
- opaque backup reference;
- nullable fixed per-target `outcome_code`;
- rollback status: `not_available`, `pending`, `succeeded`, `failed`, or
  `stale`;
- restart requirement;
- immutable repository-safe `status_projection`;
- tool version.

`status_projection` has exactly these fields:

- `detected`: boolean plan-time target/package presence;
- nullable sanitized semantic-version `detected_version`, at most 128 UTF-16
  code units (`string.Length`);
- aggregate `operation`;
- nullable `effective_source`;
- nullable canonical loopback `endpoint`;
- nullable canonical Issue #61 `expected_result` object;
- nullable guidance object containing exactly `kind` and `language` (never the
  sample);
- ordered `changes`, each containing exactly `setting_key`, `operation`,
  redacted `previous_state`, redacted `new_state`, fixed `conflict`, and boolean
  plan-time `managed`.

The snapshot target `operation` must equal the aggregate of snapshot member
operations. Its ordered `changes` keys and operations must exactly equal the
ledger target's operational member keys and operations. Its expected-result
surface must match the target registered for that plan, under the separate
ledger-origin historical-manifest validation above. Guidance targets have an
empty member list, `no-op`, null source/endpoint/expected-result, and non-null
guidance metadata. Writable targets have one or more members and null guidance.
An all-`no-op` writable record has null applied hash/backup reference and
`not_available` rollback status; a target with any changed member follows the
lifecycle ownership invariants. The existing ledger `record_id`, `target_kind`,
`target_label`, and `restart_requirement` complete the immutable public status
target and are not duplicated inside `status_projection`.

The ledger contains no raw target path, setting value, credential, token,
authorization header, raw exception, prompt, response, tool argument/result, or
PII. Hashes are lowercase SHA-256 hex over a typed canonical byte sequence that
distinguishes missing from empty. A hash does not grant ownership. Only a record
created by the adapter after a successful mutation grants that adapter rollback
authority for that exact post-apply hash.

## Planning rules

An adapter returns at most 16 ordered physical targets and 32 member changes per
target. All changes for one file share one base hash, applied hash, backup, and
ledger record. All current-user environment member changes share one canonical
allowlist hash and one environment backup. Plan construction:

An environment physical target's ordered member list remains immutable across
plan, applying, applied, recovery, and status. Its base and applied aggregate
hashes and its backup cover every listed member, including members whose
operation is `no-op`. Journal steps, forward writes, compensation, and rollback
restore only members whose observed base differs from the desired state. A
listed no-op member is never written or restored, but it remains part of the
aggregate commit and rollback stale-state guard. A physical target with no
changed members creates no backup, journal target, notification requirement,
applied hash, or rollback ownership. Environment values outside the ordered
member list remain outside capture, hashing, backup, status, and mutation.

1. detects the target and supported version;
2. validates path ancestry and structured configuration before proposing a
   replacement;
3. reads current state and calculates the base hash;
4. calculates effective source and managed state;
5. produces only redacted previous/new state;
6. records restart and rollback availability;
7. builds the immutable repository-safe status snapshot from the same validated
   plan result, with guidance sample removed;
8. writes and flushes the private immutable plan first, then atomically replaces
   the repository-safe ledger with its planned record and status snapshot.

A `plan` result whose code is `no_changes` is not artifact-free. It still
persists the inspectable immutable private plan and a `planned` ledger row,
returns that UUIDv7 as `change_set_id`, and creates no backup, journal, applied
hash, or rollback ownership. A later `apply` consumes that same requested ID and
transitions the row to terminal `no_changes` without writing a target. This
distinguishes an inspectable no-change proposal from an unknown/missing change
set and preserves the explicit plan-then-apply workflow.

The two files cannot be atomically replaced together. Their ordering is
crash-consistent: a private plan without a ledger row is an ignored orphan; a
ledger non-terminal row without its private plan is `recovery_required` and
blocks mutation. A normal write failure removes the orphan plan when possible,
but recovery never depends on that cleanup.

Requested-ID lookup checks the readable ledger before treating a private plan
as authoritative. The missing-row/private-plan outcomes are exhaustive:

| Durable condition after mandatory recovery | `apply` | `rollback` | `status` |
| --- | --- | --- | --- |
| no ledger row for the requested UUID, whether or not an orphan private plan exists | `invalid_arguments`; no adapter resolution or mutation artifact | `invalid_arguments`; no preflight or mutation artifact | not applicable because status has no requested UUID; no row is projected |
| matching row exists but its required private plan is missing, unreadable, or fails immutable row/plan identity | `recovery_required`; no registry/platform/target activity | `recovery_required`; no restore artifact or target write | a non-terminal row makes the command `recovery_required`; a terminal row follows the existing target-`unavailable`, rollback-false projection rule |
| readable row and valid bound private plan exist, but the row lifecycle is not accepted by the requested command | `invalid_arguments` unless another explicit apply result in this document applies | `rollback_not_available` unless interrupted recovery owns the row first | project according to the lifecycle matrix |

An unreadable/corrupt/unsupported ledger still returns its fixed storage code
before this table. A private plan is never enough to synthesize a missing ledger
row, and a missing private plan is never downgraded to `invalid_arguments` when
the ledger proves that the change set exists.

Malformed JSON/JSONC or TOML is `malformed_settings`; the adapter does not
best-effort replace it. JSONC comments and trailing commas are accepted for VS
Code settings. A writer must preserve unrelated keys and comments and must not
delete a setting it does not own. The framework TOML codec supports the bounded
table/scalar syntax already emitted by Config CLI samples and rejects malformed
or unsupported TOML before a plan is persisted. Issue #67 does not write TOML;
a later adapter must extend the codec contract before accepting a broader TOML
shape.

VS Code reads `settings.json` through a 1 MiB payload bound plus one sentinel
byte. A trustworthy length over 1 MiB, a sentinel byte after 1 MiB, malformed
JSONC, or a JSONC value outside the owned-value contract is
`malformed_settings` during both planning and revalidation. During planning
only, the partition may render complete JSONC bytes in bounded memory solely to
calculate the exact owned-member operations and `expected_state_hash`; it
discards those bytes before private-plan/ledger creation. No bounded-read
failure may cause an unbounded retry, a best-effort rewrite, a persisted plan,
or a mutation artifact.

## Transaction and concurrency rules

The coordinator acquires `setup.lock` with an exclusive, non-waiting file lock.
Contention returns `setup_busy`; there is no sleep, retry, or timeout loop.
Lock and mandatory-recovery ownership is exact; no command may acquire the lock
twice or run the generic recovery scan twice:

| Recognized command | Lock owner | Mandatory recovery owner | Normal-operation handoff |
| --- | --- | --- | --- |
| `plan` | generic command producer | generic command producer, once under that lock | only a `None` recovery result permits registry resolution and adapter planning under the same lock |
| `apply` | generic command producer | generic command producer, once under that lock | only `None` permits requested ledger-row/private-plan validation, persisted-adapter resolution, and `SetupApplyCoordinator.Apply` under the same lock; the apply coordinator does not recover or reacquire |
| `rollback` | generic command producer | `SetupRollbackCoordinator.Rollback`, once under the caller-held lock | the producer does not run common recovery first; the rollback coordinator performs recovery and, only for `None`, its requested preflight/mutation without reacquiring |
| `status` | `SetupStatusService.Status` | `SetupStatusService.Status`, once under its own lock | the generic producer calls the service without an outer lock or recovery; the service retains the documented same-response recovery projection behavior |

Parsing and result serialization own neither lock nor recovery. Lock contention
is decided before registry lookup, ledger/plan reads, adapter/platform activity,
or recovery. A recovery success/failure returns according to the correlation
rules below and does not also run the requested normal operation, except that
status projects readable ledger state in that same recovery response as already
specified.

Lifecycle transitions are:

```text
planned -> no_changes (apply only)
        -> applying -> applied
                    -> compensating -> restored
                                    -> partial
                       partial -> compensating

applied -> rolling_back -> rolled_back
                        -> partial
           partial -> rolling_back
```

A no-write stale apply remains `planned` with outcome `stale_plan`. A no-write
stale or unavailable rollback retains its prior lifecycle state and records
`rollback_stale` or `rollback_not_available` as its outcome. Attempt outcomes are
not additional lifecycle states. `partial` is the only unresolved mutation-
blocking state.

An exact apply journal in `prepared` paired with its `planned` ledger row is a
dormant, resumable pre-mutation attempt. Every step is `pending`, notification
state is `not_required`, and its immutable targets, hashes, member keys, backup
references, and no-follow backups must exactly match the current private plan
and freshly captured base state. Recovery ignores that exact pair. A repeated
apply performs full preflight again and reuses the exact journal and backups;
it never deletes or overwrites them. Exact orphan backups created before the
journal are reused under the same validation. Missing, rebound, malformed, or
mismatched artifacts fail closed without a target write. A `prepared` journal
paired with an `applying` ledger row is not resumable forward; recovery advances
it into compensation.

Apply rules:

Before any mutation artifact is created, the framework revalidates the
immutable plan's operation coherence. For a current-user environment target,
each member is framework-verifiable and its operation must exactly match the
observed current-to-desired missing/present transition and value: `no-op` has
the same state and value, `add` is missing-to-present, `replace` is
present-to-present with a different desired value, and `remove` is
present-to-missing. An immutable mismatch fails `recovery_required` before any
backup, journal, ledger row or transition, or target write; an inconsistent
plan creates no ownership artifact.

For opaque file, JSON/JSONC, or TOML transaction content, generic framework
revalidation is aggregate only: all member operations are `no-op` if and only
if whole current bytes equal whole desired bytes, and at least one member
operation is non-`no-op` if and only if those byte sequences differ. The
framework does not infer a logical per-member `add`, `replace`, or `remove`
from opaque bytes. The owning adapter must perform mandatory apply-time
revalidation of the exact logical file-member operation semantics; an immutable
mismatch has the same `recovery_required` and no-ownership-artifact outcome.

For an Issue #67 persisted plan, apply performs this complete adapter preflight
before backups, a new journal, a ledger lifecycle/outcome update, environment
notification, or any target write:

1. the persisted owning adapter is still registered, otherwise return the
   command-matrix `unsupported_adapter` result without invoking adapter or
   platform detection;
2. the planning OS still permits that target operation; a macOS/Linux Copilot
   CLI plan returns the command-matrix `unsupported_target` result;
3. every planned VS Code/CLI channel is still installed at the recorded
   supported version (`target_not_installed` or `unsupported_version`); a
   different version that is still supported is version drift and returns
   `recovery_required` rather than silently accepting a newly observed target;
4. every planned VS Code channel still has `GitHub.copilot-chat` installed in
   its Default Profile (`target_not_installed`);
5. Copilot managed-channel selection and the independent VS Code enterprise
   policy values are reread for VS Code, with any differing observed constraint returning
   `managed_policy_conflict`; CLI remains environment-only and unverified;
6. every file/environment member is logically re-derived from current state and
   must match the immutable key, desired value, and `add`/`replace`/`remove`/
   `no-op` operation (`recovery_required` on mismatch);
7. CLI `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` is reclassified as the detect-only
   guard below (`environment_override_conflict` on a conflicting value);
8. every distinct endpoint passes the exact recognition probe below, with no
   listener remaining a warning and every other non-recognition outcome
   returning `port_owned_by_foreign_process`.

For a tagged JSONC target, successful adapter revalidation returns one
transient materialization for each changed record and none for a `no-op`
record. `SetupRevalidation` is an in-memory, per-apply carrier only: its
record IDs are unique and, after excluding `no-op` records, its cardinality and
order exactly match the tagged `SetupTargetKind.Json` records requiring a write.
Each entry is
the complete desired byte sequence plus the record ID and expected lowercase
hash. Under the existing apply lock, `SetupApplyCoordinator` validates this
identity/cardinality/hash contract, takes ownership of the bytes, and uses
them for capture, stale checking, backup/journal construction, replacement,
and final verification. A missing, duplicate, extra, reordered, mismatched, or
hash-incorrect entry is `recovery_required` before any artifact or target
write. The aggregate adapter ignores `no-op` records for materialization; the
generic base-state guard still captures every planned record, including
all-`no-op` targets.

Materialized bytes never enter a private plan, ledger, journal, status result,
log, exception mapping, or repository-safe test evidence. Durable journal and
ledger fields carry hashes only. Recovery never calls an adapter or
re-materializes JSONC: in every before-intent, after-intent, after-replace,
after-completion, compensation, and rollback crash window it classifies files
using the tagged `expected_state_hash`, the journal hashes, and the flushed
backup. This preserves the same hash/backup evidence even if the current
settings JSONC cannot later be parsed.

After this adapter preflight, the generic all-target base/path/hash/reparse
preflight runs. Any failure in either phase creates no mutation artifact and
writes no target.

1. validate all plan base hashes, target paths, non-reparse ancestry, and the
   completed adapter preflight for every distinct writable endpoint before any
   backup, journal, ledger transition, or target write;
2. create and flush every required backup;
3. write and flush a transaction journal in `prepared` state, then atomically
   set the ledger state to `applying`;
4. treat one file replacement as one journal step and every current-user
   environment member API write as its own journal step within the aggregate
   environment target;
5. before every file/member mutation, atomically persist and flush
   `mutation_started`; recheck its path and base/current state immediately
   before the write; after the write atomically persist and flush
   `mutation_completed`;
6. after every step reports completion, verify that every file/member still
   equals desired state; only then mark the journal `committed` and persist
   post-apply hashes and ledger state `applied`;
7. on failure, set journal/ledger to `compensating` and compensate changed
   steps in reverse order, persisting and flushing `restore_started` before and
   `restore_completed` after every restore;
8. immediately before each compensation restore, classify current state as
   desired, prior, or third-party; restore only desired, accept prior
   idempotently, and preserve/report third-party state as partial;
9. persist `restored` when compensation is complete or `partial` with per-target
   rollback outcomes when any compensation fails.

Every command performs recovery after acquiring the lock and before its normal
operation. For an apply step recorded `mutation_started`, current prior state
means the write did not occur, current desired state means recovery restores the
prior state, and a third state becomes `partial` without overwriting it. A
`mutation_completed` step accepts desired or already-restored prior state
idempotently; a third state becomes `partial`. Environment classification is
per member, so a process exit between member API calls remains recoverable while
an independently changed third value is preserved and reported partial. A
committed journal with a stale ledger is reconciled to `applied` only when every
file/member equals desired state.

A `pending` step has no transaction ownership because no write intent was
persisted. Recovery accepts it only when its current state is prior. Current
desired, third-party, or unavailable state is preserved and makes recovery
partial; desired bytes alone never grant ownership. Recovery of an apply
journal visits owned steps in reverse order and may reopen `partial` only into
`compensating`; rollback recovery may reopen `partial` only into
`rolling_back`. Terminal verification for an environment target always uses
the private plan's full ordered member list, including no-op members, while
journal restore steps remain limited to changed members.

Rollback first flushes its journal, atomically sets ledger state
`rolling_back`, and uses the same `restore_started`/`restore_completed` protocol
for every file/member restore. Immediately before every normal or recovered
restore it classifies current state as applied, previous, or third-party;
restores only applied, accepts previous idempotently, and preserves/reports
third-party state as partial. Recovery accepts applied or previous state
idempotently and completes the rollback; a third state becomes `partial`
without overwrite. A committed rollback journal reconciles to `rolled_back`
only when every file/member equals previous state. Successful recovery records
`interrupted_apply_recovered` or
`interrupted_rollback_recovered`. Failed recovery records
`interrupted_recovery_failed`, leaves the change set `partial`, and blocks every
mutation command until the private local failure is resolved; `status` remains
available. Recovery never searches a remembered path outside the private plan
or bypasses path/reparse/hash validation.

Recovery discovers candidates from ledger rows, orders them by immutable
`created_at` ascending and then canonical lowercase UUID ordinal ascending,
attempts at most one candidate per command invocation, and returns immediately
after success or failure. The oldest failed candidate continues to block later
candidates. A terminal journal whose only unresolved work is a pending
environment notification is notification-only recovery: the journal and ledger
retain their truthful `committed`/`applied`, `restored`, or `rolled_back`
lifecycle, while the returned projection overlays that row as effective
`partial`, `interrupted_recovery_failed`, and rollback unavailable if replay
cannot be proven. Target state is not reconciled again for notification-only
recovery. Successful replay persists the recovered terminal ledger result,
attempts notification, then marks notification complete last; any delivery or
completion ambiguity remains pending and permits a duplicate replay.

Before replaying any terminal `pending` environment notification, recovery must
prove immutable notification identity from private artifacts without reading or
writing a target. The private plan must be present, readable, and bound exactly
to the ledger change set. For every changed environment target, its safe regular
non-reparse backup must contain the plan's full ordered member list, including
no-op members, and its aggregate hash must equal both the plan base hash and the
ledger previous-state hash. The journal must contain exactly the changed
environment members in plan order, with each step's key and prior-state hash
derived from that backup, desired-state hash derived from the plan, and backup
reference equal to the canonical lowercase record UUID. An `applied` terminal
row additionally requires the full desired aggregate derived from the plan and
backup to equal the ledger applied-state hash. `restored` and `rolled_back`
terminal rows use the same plan/backup/journal identity gate but do not require
an applied hash that their lifecycle has already cleared. A missing, unreadable,
corrupt, rebound, or mismatched plan, backup, member hash, aggregate hash, or
backup reference fails closed before notification: the marker remains
`pending`, the terminal durable lifecycle remains truthful, and recovery returns
fixed `interrupted_recovery_failed` / `Failed` through the terminal-lifecycle-
preserving failure path. Drift in the current target does not fail this artifact
gate and does not cause a target read. A notification already marked
`completed` imposes no new plan or backup requirement.

When recovery changes or reconciles state, the command returns the corresponding
`interrupted_*_recovered` result immediately with next action
`rerun_requested_setup_command`; it does not also execute the originally
requested mutation in the same process. For `apply B` or `rollback B` that first
recovers `A`, `change_set_id` is `B` and `recovered_change_set_id` is `A`. For
`plan` or `status` stopped by recovery, `change_set_id` is null. Normal results
set both recovery fields to null. `status` always projects readable ledger state
in the same recovery response under the rules above.

File updates write a temporary file in the destination directory, flush it,
then use a same-volume atomic replace. Existing files receive a backup first.
New files use an atomic same-directory move. Root, ancestor, and target symlink,
junction, or reparse-point paths are rejected before plan, apply, compensation,
and rollback. Absolute paths remain private and are never returned.

Path metadata classification is no-follow and permission-independent. Only an
actual regular file is a file target; FIFO, socket, character/block device, and
symlink are rejected without opening their content. Windows, Linux, and macOS
use native metadata-only classification; another OS fails closed. A failed
create/flush/revalidation/replace/move never deletes a temporary pathname: its
identity may have been rebound by another actor. Successful replace/move
consumes the temp; interrupted-transaction recovery relies on journaled target
state and never on pathname cleanup of a temp orphan.

Issue #67 user environment mutation is Windows-only and uses the current-user
API (`EnvironmentVariableTarget.User` / HKCU user environment), broadcasts one
environment-change notification attempt on an uninterrupted path only after the
final committed or restored state, and never uses `setx`. A recovery retries the
notification when prior delivery cannot be proven, so duplicate notifications
are permitted but an early notification is not. Its canonical hash and backup cover only the member keys
listed in that physical target; unrelated user environment values are neither
read into the plan nor changed/restored. It does not require administrator
privileges. The framework exposes no macOS/Linux persistent-user-environment or
shell-profile writer.

Rollback validates that every current hash equals its applied hash before the
first restore. One mismatch returns `rollback_stale` and performs no write.
Force rollback is absent. DB, telemetry, logs, and other runtime data are never
deleted by setup rollback.

Because one journal pathname is retained per change set, rollback does not
delete or overwrite the apply journal opportunistically. After rollback
preflight it atomically supersedes only an exact terminal apply journal whose
notification is complete with a prepared rollback journal. A prepared rollback
journal paired with an `applied` ledger is the rollback equivalent of the
dormant resumable apply state.

Repeated plan/apply against the desired state produces `no-op` member changes and
`no_changes`; it creates no second ownership claim and does not rewrite files or
environment values.

Tests for stale state, lock contention, concurrent edit, failure after each
mutation, compensation, and partial rollback use barriers or injected fault
points. Sleeps and probabilistic timing assertions are prohibited.

## GitHub Copilot adapter

Adapter ID is `github-copilot`.

The adapter/backend/CLI source contract is:

| Selected target | Adapter detection/plan source | Physical target DTO | Apply-time adapter source | Mutation owner |
| --- | --- | --- | --- | --- |
| `vscode` | Stable/Insiders executable version, per-channel Default Profile extension list and user settings, one bounded per-eligible-channel running-state observation, current process environment, read-only Copilot managed channels plus independent VS Code enterprise policies, endpoint probe, canonical `github-copilot-vscode` manifest | one JSON target per eligible installed channel, Stable then Insiders; labels are exactly `vscode-stable-default-user-settings` and `vscode-insiders-default-user-settings`; `expected_result` is the exact manifest | persisted channel/OS/member facts revalidated against current version (including supported-version drift), Default Profile extension, both policy systems, bounded settings members, and endpoint; the ephemeral running-state observation is not persisted or compared | generic #66 file transaction only with transient tagged-JSONC materialization |
| `cli` | Copilot CLI version, read-only current-process environment and, on Windows, the distinct current-user environment, endpoint probe, canonical `github-copilot-cli` manifest; managed sources are deliberately not opened | one `copilot-cli-user-environment` env target; `expected_result` is the exact manifest; macOS/Linux plan remains inspectable but non-applicable | persisted OS/version/member/endpoint facts; Windows is writable, macOS/Linux returns `unsupported_target` | generic #66 Windows user-environment transaction only |
| `app-sdk` | current repository .NET package/version and canonical fixed sample | one `github-copilot-app-sdk-guidance` no-write guidance target with null `expected_result` | no target revalidation or write | none |

Adapters produce the internal plan/record types; the coordinator alone produces
the public `SetupCommandResult`. The CLI serializes that DTO directly, and the
PowerShell wrapper forwards it unchanged. No adapter creates a second result
shape or writes through a platform API outside the #66 coordinator.

### VS Code GitHub Copilot Chat

Supported VS Code channels are Stable (`code`) and Insiders
(`code-insiders`), each at version `1.128.0` or newer. Version 1.128 is the
minimum because it is the first release that enforces the required precedence
across managed-setting delivery channels. Versions 1.119 through 1.127 are
detected but the whole requested `vscode` plan returns `unsupported_version`
with `upgrade_vscode`; no channel is mutated. Channel discovery runs exactly
one `code --version` for Stable and one `code-insiders --version` for Insiders.
For every installed supported channel, the partition then runs exactly one
`code --list-extensions --show-versions` for Stable, or the same arguments with
`code-insiders` for Insiders, through `ISetupProcessRunner`, and requires
`GitHub.copilot-chat`. A missing extension in any installed supported channel
returns `target_not_installed` with
`install_github_copilot_chat_extension`; it does not produce a partial plan.
When both channels are eligible, the plan has two physical JSON targets in
Stable-then-Insiders order.

After all version and extension gates have succeeded, the partition observes
each eligible installed channel exactly once, in Stable-then-Insiders order,
through `ISetupProcessRunner`: `code --status` for Stable and
`code-insiders --status` for Insiders. This uses the runner's existing fixed
five-second process bound. The official VS Code `--status` command requires an
already-running VS Code instance; it is used here only as a bounded running
state observation ([VS Code performance guidance](https://github.com/microsoft/vscode/wiki/performance-issues),
[VS Code CLI documentation](https://code.visualstudio.com/docs/configure/command-line)).
Only `Completed` with exit code `0` means that the observed channel is running.
`NotFound`, `Failed`, `TimedOut`, a nonzero exit code, or any other observation
means it is not observed running: it adds no restart guidance and never fails
the plan. The observation has no retry or sleep. Its standard output is
discarded immediately and must not appear in a DTO, private plan, ledger, log,
or repository-safe artifact.

Only each channel's Default Profile user `settings.json` is a writable target:

| Channel | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Stable | `%APPDATA%\Code\User\settings.json` | `$HOME/Library/Application Support/Code/User/settings.json` | `$HOME/.config/Code/User/settings.json` |
| Insiders | `%APPDATA%\Code - Insiders\User\settings.json` | `$HOME/Library/Application Support/Code - Insiders/User/settings.json` | `$HOME/.config/Code - Insiders/User/settings.json` |

These are the documented standard user-data locations: the VS Code settings
documentation supplies the Stable paths, and the Profiles documentation
explicitly says Insiders replaces the intermediate `Code` folder with
`Code - Insiders`. Extension detection deliberately omits `--profile`. The
official CLI treats `--profile <name>` as a named-profile selector and creates
a new empty profile when the name does not exist, so setup never passes
`--profile`, never names `Default`, and never creates or selects a non-default
profile while checking extensions.

The adapter may enumerate the documented sibling `profiles/<profile-id>/`
directories only to determine whether non-default profiles exist. It never
opens, hashes, parses, plans, writes, backs up, or rolls back a non-default
profile `settings.json`. If either channel has any non-default profile, the
top-level warning array contains the fixed code
`vscode_non_default_profiles_not_modified` exactly once.

The writable user settings are:

- `github.copilot.chat.otel.enabled = true`;
- `github.copilot.chat.otel.exporterType = "otlp-http"`;
- `github.copilot.chat.otel.otlpEndpoint = <validated loopback endpoint>`.

Every newly planned VS Code target serializes `desired_state` only as the
tagged `jsonc_owned_values_v1` v1 object. Its expected hash is the hash of the
complete JSONC-preserving desired bytes calculated at plan time, while its
owned values are exactly the planned members in member order. The complete
bytes are deliberately not persisted. At apply, revalidation first re-reads
the bounded current settings, confirms all persisted logical member and
effective-source facts, then materializes the complete desired bytes under the
apply lock. It returns those bytes only through the transient revalidation
carrier and only when the record is not `no-op`; a hash mismatch or a
different-but-supported VS Code version is `recovery_required` before an
artifact or write. A missing channel remains `target_not_installed`, and a
below-floor version remains `unsupported_version`.

The default plan does not add, remove, or change
`github.copilot.chat.otel.captureContent`. With
`--include-content-capture`, it explicitly proposes `true`, emits
`content_capture_sensitive`, and makes it a separate member change within the
single VS Code settings-file target.

Effective value precedence is:

1. Copilot managed policy;
2. environment variable;
3. VS Code user setting;
4. product default.

The environment mapping includes `COPILOT_OTEL_ENABLED`,
`COPILOT_OTEL_ENDPOINT`, `OTEL_EXPORTER_OTLP_ENDPOINT`,
`COPILOT_OTEL_PROTOCOL`, `OTEL_EXPORTER_OTLP_PROTOCOL`, and
`COPILOT_OTEL_CAPTURE_CONTENT`. A shared environment override is reported and
never silently deleted.

The adapter reads these official managed sources without modifying them:

| Copilot managed tier | Windows | macOS | Linux |
| --- | --- | --- | --- |
| native | `HKEY_LOCAL_MACHINE\SOFTWARE\Policies\GitHubCopilot` | managed preferences for the `com.github.copilot` domain | not available; Linux uses server/file delivery |
| server | signed-in GitHub account policy; not locally observable to this external CLI | same | same |
| file | `%ProgramFiles%\GitHubCopilot\managed-settings.json` | `/Library/Application Support/GitHubCopilot/managed-settings.json` | `/etc/github-copilot/managed-settings.json` |

Linux has no native Copilot managed-settings channel.

Managed tiers use `native > server > file`. The first tier that supplies any
managed settings is the sole managed object; lower managed tiers are ignored
wholesale and fields are never merged across channels. Per setting, a value in
that winning object is then above environment, user setting, and product
default. An observed conflicting value in the winning Copilot managed-settings
channel yields `managed_policy_conflict` and no plan. Because the
server tier cannot be proved present or absent by an external CLI, when native
is absent a successful plan includes warning `managed_policy_unverified` and
next action `run_vscode_policy_diagnostics`; a locally observed file remains a
bounded fact but is not claimed effective over a possibly present server tier.
When native is present, it proves the winning managed channel and server/file
are ignored without merging.

VS Code enterprise policy is a separate policy system, not a fourth delivery
channel and not part of the precedence above. The adapter also reads these
official sources without modifying them:

| OS | Independent VS Code enterprise policy source |
| --- | --- |
| Windows | `CopilotOtel*` values under both `HKEY_LOCAL_MACHINE\Software\Policies\Microsoft\VSCode` and `HKEY_CURRENT_USER\Software\Policies\Microsoft\VSCode`; computer policy wins over user policy within this system |
| macOS | installed VS Code configuration-profile `CopilotOtel*` values |
| Linux | `CopilotOtel*` values in `/etc/vscode/policy.json` |

The adapter resolves the Copilot managed-settings object and the VS Code
enterprise-policy values independently. It never lets a Microsoft VS Code
policy suppress Copilot server/file discovery and never merges an enterprise
policy into the winning Copilot object. For each planned telemetry field, any
observed value from either system that differs from the desired value returns
`managed_policy_conflict`; equal observed constraints are reported as managed
without being rewritten. When Copilot native MDM is absent, an observed VS Code
enterprise policy does not prove the Copilot server tier absent, so
`managed_policy_unverified` and Policy Diagnostics guidance remain required.

Each physical target record maps only its own channel observation: the Stable
record has `restart_requirement=restart_vscode` iff Stable `code --status` is
`Completed` with exit code `0`, and otherwise `restart_requirement=none`; the
Insiders record follows the equivalent rule for `code-insiders --status`.
Stable and Insiders observations are independent. Thus the dual-channel plan
has four record-level cases: Stable/Insiders are restart/restart,
restart/none, none/restart, or none/none. The top-level `next_actions` contains
the one deduplicated `restart_vscode` action iff at least one target record has
that requirement; it never substitutes for, changes, or infers either target
record's field.

`Revalidate` makes zero `--status` calls. It does not persist, compare,
recompute, or alter the persisted per-target restart requirement, and it cannot
turn the running-state observation into an apply/preflight failure fact.
Revalidation repeats the persisted version, extension, policy, member, hash,
effective-source, and endpoint checks only. Post-apply verification reparses
the settings, rechecks the target hashes and effective-source calculation, and
does not claim that a trace arrived.

### Terminal GitHub Copilot CLI

The adapter detects `copilot version`. Version `1.0.4` or newer is supported
because `1.0.4` introduced the documented OpenTelemetry instrumentation.

The exact desired environment allowlist is:

- `COPILOT_OTEL_ENABLED=true`;
- `COPILOT_OTEL_EXPORTER_TYPE=otlp-http`;
- `OTEL_EXPORTER_OTLP_ENDPOINT=<validated loopback endpoint>`;
- `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`.

The adapter does not set `client.kind` globally and does not change
`OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`,
`OTEL_EXPORTER_OTLP_HEADERS`, `COPILOT_OTEL_SOURCE_NAME`, or any credential.
It does not write any official telemetry variable outside the allowlist above
and the one explicit content-capture member below.
The default plan does not change content capture. With
`--include-content-capture`, it separately proposes
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` and emits the same
sensitive warning.

`OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` is detect-only and is never added to that
write allowlist. GitHub documents it as overriding
`OTEL_EXPORTER_OTLP_PROTOCOL` for traces. If it is already exactly
`http/protobuf`, the plan leaves it unchanged and emits
`cli_trace_protocol_override_not_modified`. If it has any other value, the
requested global protocol cannot become effective for traces: plan fails with
`environment_override_conflict`, includes
`review_cli_trace_protocol_override`, and creates no private plan or target
artifact. Apply repeats this detect-only classification before mutation
artifacts; a then-conflicting value returns the same code/action and no write.
Setup never removes or rewrites the override.

On Windows, the plan displays current-process versus current-user environment
state and warns `shared_user_environment_affects_other_processes`. These are
separate observation surfaces: `ISetupProcessEnvironment` is read-only and
reads only the current Config CLI process, while `ISetupPlatform.UserEnvironment`
continues to mean the Windows current-user persistent environment. The
current-process surface has no set/delete/notify operation and is never used as
the mutation target. Apply uses only the Windows current-user environment
API/HKCU user environment and broadcasts the framework notification;
already-running terminals and Copilot CLI sessions require
`restart_terminal_session`.

On macOS and Linux, detection, version validation, endpoint probing, manifest
selection, and redacted plan creation still run, so the user can inspect the
same desired allowlist. The persisted plan is tagged with its planning OS.
`setup apply` for that plan returns `unsupported_target` as specified in the
command/result matrix. It does not edit `.zshrc`, `.bashrc`, `.profile`, shell
launch configuration, system environment files, or any other target, and it
does not create a backup or transaction journal.

Copilot CLI emits traces and metrics; Local Monitor currently accepts traces.
The static result reports metric delivery as `not_verified` rather than
claiming complete signal receipt.

Copilot managed telemetry can apply to the CLI, but this adapter deliberately
uses environment-only detection for the terminal CLI target. It does not open
or interpret native, server, or file managed sources for CLI planning and never
edits them. Every successful CLI plan therefore includes
`managed_policy_unverified`; its `effective_source` describes only the observed
environment layer and is not a claim about the final managed effective value.

### GitHub Copilot App / SDK

App/SDK configuration is caller-owned. Detection reports whether the current
repository's .NET `GitHub.Copilot.SDK` package is present and its version, but
does not search or mutate arbitrary external application files.

The guidance target sets `detected` to package presence and
`detected_version` to its sanitized semantic version when present, otherwise
null. It does not return the project or package path.

Plan returns `no-op`, `rollback_available=false`, and a bounded .NET sample
matching the repository's pinned SDK contract:

```csharp
new CopilotClientOptions
{
    Telemetry = new TelemetryConfig
    {
        OtlpEndpoint = "http://127.0.0.1:4320",
        OtlpProtocol = "http/protobuf"
    }
}
```

The sample is guidance, not an applied setting. Other SDK languages remain
caller-managed and are linked to the official SDK observability contract rather
than inferred. App/SDK success means the sample is available; it does not mean
the caller used it or a trace arrived.

## Claude Code adapter

Adapter ID is `claude-code`. It reuses the Issue #66 storage, transaction,
status, recovery, concurrency, and rollback coordinators. It does not own a
second public DTO or transaction path.

The adapter/backend/CLI source contract is:

| Selected target | Adapter producer | Physical target DTO | Consumer/mutation owner |
| --- | --- | --- | --- |
| `cli` | strict Claude version and execution-context detection, user settings reader, higher-priority configuration observation, Local Monitor endpoint probe | one `claude-code-user-settings` JSON target with `restart_agent_process` | generic #66 file transaction with transient Claude-settings materialization |
| `app-sdk` | canonical fixed Python and TypeScript guidance | `claude-agent-sdk-python-guidance` then `claude-agent-sdk-typescript-guidance`; both no-write guidance | caller only; setup has no file, environment, backup, or rollback target |
| `all` | CLI producer then both SDK guidance records | the same records in that order | each record keeps the owner above; first failure returns no partial plan |

### Version and execution-context detection

Detection runs `claude --version` once through the bounded process runner. A
supported result is exactly one normal `MAJOR.MINOR.PATCH (Claude Code)` line,
apart from its final platform newline, at version `2.1.207` or newer. An older
normal release, a prerelease/build-suffixed version, malformed or additional
output, nonzero exit, timeout, or missing executable returns
`unsupported_version` or `target_not_installed` as applicable without settings
or endpoint activity. Normal future releases are accepted.

The execution context is one of:

- Windows native: Windows process, not WSL2; writable through the existing file
  transaction and `--allow-wsl2-routing` is invalid;
- WSL2 repository-run: Linux process, non-empty `WSL_DISTRO_NAME`, and a bounded
  kernel release containing the Microsoft marker are all required;
- native macOS/Linux or an incomplete WSL observation: no Issue #68 installer
  target and planning fails `unsupported_target`.

A verified WSL2 context without `--allow-wsl2-routing` returns
`wsl2_opt_in_required`. With the opt-in, it follows the Claude readiness probe
below. The adapter does not discover or configure a WSL gateway, Windows host
alias, non-loopback bind, Host-header relaxation, NAT forwarding, or
Windows-to-WSL mutation bridge. Windows Release ZIP execution does not claim
WSL settings mutation.

### Claude Local Monitor readiness recognition

Windows-native and verified, opted-in WSL2 CLI plans both probe readiness. The
same check runs again during apply revalidation after recovery and all other
adapter validation, but before backup, journal, ledger transition, or target
write. The probe is closed and bounded:

- send exactly `GET <canonical-origin>/health/ready` with redirects disabled;
- enforce one 500 ms total budget over connect, request, response headers, and
  body read;
- reject a trustworthy valid `Content-Length` over 4096 immediately;
  otherwise read at most 4096 payload bytes plus one sentinel byte;
- recognize Local Monitor only for HTTP 200 and a complete JSON object that
  validates the exact readiness body defined in
  [telemetry ingestion](../layers/telemetry-ingestion.md): exactly top-level
  `status`, `checks`, and `degraded_reasons`, no duplicate or unknown fields,
  the documented exact checks `loopback_bound`, `db_open`,
  `migration_complete`, `writer_running`, `projection_worker_running`,
  `ingestion_accepting`, `projection_lag_seconds`, `projection_backlog`,
  `span_projection_lag_seconds`, `span_projection_backlog`, and
  `projection_failure_count` with their documented types,
  `checks.loopback_bound=true`, and `status` exactly `ready` or `degraded` with
  the documented HTTP/status/reason invariants. This identity accepts a healthy
  or momentarily degraded Local Monitor but not a foreign JSON service;
- connection refused/no listener, connect/read/total timeout, redirect, every
  non-200 response including readiness `503`, another transport failure,
  oversized body, malformed/non-object JSON, missing/duplicate/unknown/wrong-
  typed fields, false loopback identity, or an invalid readiness invariant is a
  hard no-plan/no-write failure. Windows native returns
  `endpoint_unreachable`; verified WSL2 returns
  `wsl2_routing_unavailable`.

Claude readiness failure never becomes `monitor_not_running`,
`port_owned_by_foreign_process`, or `start_local_monitor`; those outcomes
belong only to the GitHub Copilot liveness probe below. No retry, sleep,
gateway discovery, non-loopback fallback, Host-header relaxation, or NAT path
is permitted.

### Claude user settings ownership

The writable document is the current user's official
`~/.claude/settings.json`. It is read with the same 1 MiB plus one sentinel
bound used for setup JSON settings. The dedicated Claude renderer preserves
unrelated JSON properties, comments, existing Hook array order, and final
newline. Malformed, duplicate, unsupported, or oversize input fails closed as
`malformed_settings`; setup never replaces it with an empty document.

The default owned `env` values are exactly:

```text
CLAUDE_CODE_ENABLE_TELEMETRY=1
CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1
OTEL_TRACES_EXPORTER=otlp
OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=<canonical-origin>/v1/traces
```

Only explicit `--include-content-capture` adds these three owned values, all
set to string `1`:

```text
OTEL_LOG_USER_PROMPTS
OTEL_LOG_TOOL_DETAILS
OTEL_LOG_TOOL_CONTENT
```

Without that option, setup preserves those three keys whether absent or
present. It never sets `OTEL_LOG_RAW_API_BODIES`, metrics/log exporters,
`OTEL_EXPORTER_OTLP_HEADERS`, another authorization header, credential, global
resource attribute, service name, or caller identity.

Process, managed, project, and local configuration surfaces that have higher
precedence than user settings are observed read-only. An observed value equal
to the requested value is redacted read-only evidence and is not rewritten. A
differing routing/telemetry value returns `content_policy_conflict` for a
content gate and the existing fixed managed/environment conflict code for its
respective non-content source. No conflict result writes settings, backup,
journal, or ledger lifecycle state. Public projection reports only fixed
endpoint/protocol/signal/process-inheritance state names, never source paths or
values.

### Claude Hooks

The CLI target manages all Hook events already supported by the installed
Claude mapper, in this canonical order:

```text
SessionStart
UserPromptSubmit
PreToolUse
PermissionRequest
PostToolUse
PostToolUseFailure
SubagentStart
SubagentStop
Stop
StopFailure
SessionEnd
```

For every event, setup adds one command-handler group with matcher omitted,
handler timeout `5` seconds, and an exec-form command. The private command and
arguments invoke the installed Local Monitor `hook-forward` command with the
canonical loopback endpoint, `--timeout-ms 250`, and exact
`--source claude-code` plus the bounded detected source provenance required by
the existing Hook forwarding contract. Repository mode invokes the Local
Monitor project with `dotnet run --no-build`; Release mode invokes the packaged
Local Monitor executable. The PowerShell wrapper does not rewrite those
private arguments.

Every non-owned Hook entry remains in its original position. Setup owns only an
entry whose command and ordered arguments identify its exact
`hook-forward --source claude-code` form. An identical owned entry is no-op; a
different command, argument order/value, or timeout in such an entry returns
`hook_command_conflict` and performs no write. Setup never deletes, reorders,
or takes ownership of another Hook command.

Hook capture and OTel content gates are independent. Because the default event
set includes prompt and tool events that can carry raw content, every
successful CLI plan emits `claude_hooks_capture_raw_content`, even when
`--include-content-capture` is absent. Explicit OTel content capture also emits
`content_capture_sensitive` and `review_content_capture_warning`.

Plan and revalidation render complete desired settings only in bounded memory,
calculate the expected state hash, and discard the bytes before persistence.
The private plan uses only `claude_settings_owned_values_v1`. Apply-time
revalidation rereads the version, execution context, endpoint, higher-priority
sources, env members, Hook ownership, and current document under the setup
lock, returns one transient materialization for the changed record, and applies
the generic identity/cardinality/hash gate before any artifact or write.
Status and recovery never rerun the adapter or materialize the document.

### Agent SDK guidance

Agent SDK guidance is caller-managed and has no setup mutation or rollback
target. The fixed Python guidance uses `ClaudeAgentOptions(env=...)`; the fixed
TypeScript guidance uses `options.env`. Both instruct the caller to merge with,
not replace, its existing process environment and to flush telemetry before a
short-lived process exits. Guidance contains no discovered application path,
token, authorization header, raw setting value, or caller code. Its success
means only that guidance is available.

### Claude completion boundary

A successful changed CLI target returns next action
`restart_claude_process`; the target restart requirement is
`restart_agent_process`. Setup verifies only static settings and endpoint
reachability. It does not emit `run_first_trace_doctor`, wait for telemetry, or
claim a first real trace. Issue #104 owns first-real-trace and Doctor mapping.

## GitHub Copilot endpoint and error-state detection

Before returning a writable GitHub Copilot plan, the `github-copilot` adapter
probes the selected loopback port.
Apply repeats the probe for every distinct writable endpoint after recovery and
all other validation, but before backups or any write:

- send `GET <canonical-origin>/health/live` with redirects disabled;
- enforce one 500 ms total budget covering connect, request, response headers,
  and body read; connect, read, and total-budget timeout all classify as a
  foreign owner once the probe attempt begins;
- if a trustworthy valid `Content-Length` exceeds 4096, fail immediately;
  otherwise read at most 4096 payload bytes plus one sentinel byte so an
  unknown/chunked 4097-byte response is detected without an unbounded read;
- recognize Local Monitor only for HTTP 200 whose complete body parses as a
  JSON object with exactly one property, case-sensitive string
  `"status":"live"`; JSON whitespace and property order are irrelevant, but
  duplicate or additional properties are forbidden;
- socket connection refused or an equivalent positive no-listener result is
  warning `monitor_not_running`; plan/apply remains usable and includes
  `start_local_monitor`;
- any redirect (without following it), non-200 response, timeout, transport
  failure other than the explicit refused/no-listener case, body over 4096
  bytes, malformed JSON, non-object JSON, or any other JSON object is
  `port_owned_by_foreign_process`; plan creation or apply fails with no mutation
  artifacts or target write.

The apply-time probe is an observation and does not claim that port ownership
cannot change after the probe.

The warning allowlist is closed and exhaustive:

| Warning | Exact condition |
| --- | --- |
| `content_capture_sensitive` | explicit content-capture member is planned |
| `managed_policy_unverified` | VS Code Copilot server tier is unobservable, or CLI uses environment-only managed detection |
| `monitor_not_running` | connection is refused or an equivalent positive no-listener result is observed |
| `shared_user_environment_affects_other_processes` | Windows CLI user-environment target is planned |
| `vscode_non_default_profiles_not_modified` | one or both channels have a non-default profile; emitted once per command, with no remediation action |
| `cli_trace_protocol_override_not_modified` | existing trace-specific protocol override already equals `http/protobuf` and is left untouched |
| `claude_hooks_capture_raw_content` | Claude CLI plan manages the default prompt/tool-capable Hook set, independently of OTel content-gate selection |

The `next_actions` allowlist is also closed and exhaustive:

| Next action | Exact use |
| --- | --- |
| `install_vscode` | neither requested VS Code channel is installed |
| `install_github_copilot_chat_extension` | an installed requested channel lacks the extension in Default Profile |
| `upgrade_vscode` | an installed requested channel is below 1.128 |
| `install_copilot_cli` | Copilot CLI is not installed |
| `upgrade_copilot_cli` | Copilot CLI is below 1.0.4 |
| `run_vscode_policy_diagnostics` | emitted whenever `managed_policy_unverified` is returned for VS Code |
| `restart_vscode` | one or more eligible VS Code target records have their own bounded `--status` observation completed with exit code `0`; the action is deduplicated, but each record retains its independent requirement |
| `restart_terminal_session` | a Windows CLI user-environment change requires a new process |
| `restart_claude_process` | Claude user settings changed and a new Claude process is required to inherit them |
| `start_local_monitor` | endpoint probe proves no listener |
| `review_content_capture_warning` | explicit sensitive content capture was requested |
| `review_cli_trace_protocol_override` | trace-specific protocol override blocks the requested global protocol |
| `run_first_trace_doctor` | reserved for downstream first-trace work; Issue #68 never emits it and Claude handoff is Issue #104 |
| `rerun_requested_setup_command` | mandatory recovery completed instead of the requested command |

## Security and evidence rules

Plan, ledger, status, logs, repository-safe evidence, and test failure messages
must not contain:

- credentials, tokens, authorization headers, or secret-like values;
- raw previous/new setting values other than a validated loopback endpoint;
- absolute or user-derived path;
- raw exception messages;
- prompts, responses, tool inputs/results, source fragments, or PII.

Negative tests inject markers into every input boundary and assert that no
serialized command result, ledger, journal, log, exception mapping, tagged
private plan, planning/revalidation materialization diagnostic, or committed
fixture contains a previous-state secret/value marker. Complete materialized
JSONC bytes may exist only in bounded in-memory planning, the in-memory apply
carrier, and the target write path; they are discarded and never serialized or
surfaced as evidence. A flushed local backup is allowed to
contain exact previous state because it is the rollback source; tests instead
prove that backup content is never serialized or logged. Tests keep the
committed ownership-ledger v1 fixture byte-identical/restart-readable and,
before serializer changes, add a separate production-serializer private-plan
v1 fixture with legacy inline `desired_state` whose `SetupPlanStore`
write-close-reopen bytes are identical. Fixtures are synthetic and use the
production DTO serializer for public results and the production `SetupPlanStore`
serializer for private-plan bytes.

## Validation

Focused validation covers:

- ledger v1 status snapshot write-close-reopen, exact-shape/cross-field
  validation, plan/current-manifest versus ledger/historical-manifest validation,
  128-code-unit detected-version boundary, missing-snapshot/unknown-version
  rejection, and largest-legal-change-set size below the retained 1 MiB cap;
- private-plan v1 `desired_state` union serialization/load validation: the
  existing committed ownership-ledger fixture remains byte-identical and
  restart-readable; task-04b first adds a separate production-serializer
  private-plan fixture containing legacy inline `desired_state`, then
  byte-compares its `SetupPlanStore` write-close-reopen output; inline is the
  canonical arm for generic non-tagged file/TOML/opaque records; tagged is
  accepted only for `SetupTargetKind.Json` `github-copilot` records with either
  exact VS Code Default Profile label; `SetupTargetKind.File`,
  `SetupTargetKind.Toml`, other adapters/labels, and inline on either VS Code
  JSON record fail `recovery_required`, as do unknown/missing properties, wrong
  kind/value type, 0/1/2048/2049 string boundaries, duplicate/reordered keys,
  non-1:1 members, invalid target-kind/arm relation, and non-lowercase hashes;
- Claude private-plan arm validation accepts only the exact adapter/kind/label,
  1..8 ordered unique env members, the exact 11 ordered Hook events, bounded
  command/argument/value strings, timeout 5, and lowercase expected hash; it
  rejects inline/GitHub-tagged substitution, unknown/missing/duplicate/reordered
  fields, 0/1/2048/2049 string boundaries, and any public/ledger/log leak;
- JSONC/TOML malformed fail-closed behavior;
- file backup/temp/atomic replace and non-reparse path policy;
- deterministic stale apply/rollback and lock contention;
- file faults before intent, after intent, after replace, and after completion,
  followed by close/reopen recovery;
- temp-path rebinding after create/flush preserves foreign bytes and performs no
  failure-path pathname deletion;
- native no-follow metadata classifies read-only regular files, symlinks,
  dangling links, FIFO, Unix sockets, and character devices on each supported
  OS family;
- per-environment-member faults before/after intent, write, and completion,
  including missing/present-empty state and unrelated-key preservation;
- third-party file/member state during recovery is preserved and yields partial;
- barrier edit after mutation completion but before final desired-state
  verification prevents commit and is preserved as partial;
- failure at every apply/compensation and rollback/restore boundary;
- barrier edit after rollback preflight but before a restore is preserved and
  yields partial rather than being overwritten;
- committed apply/rollback journal versus stale-ledger reconciliation;
- recovery DTO correlation for plan/apply/rollback/status and readable-ledger
  projection after failed recovery, including omission of a nonmatching
  failed-recovery row after the exact adapter filter;
- status hard-cap, priority, timestamp/UUID tie-break, and adapter-filter order;
- status immutable-field reconstruction from the ledger snapshot while changed
  installation/version/policy/manifest facts are not rediscovered;
- fresh current/reference/backup verification after external target or backup
  changes, including terminal missing-plan/unavailable classification;
- endpoint, supported-version, and managed-state changes between plan and apply
  plus VS Code extension/member changes between plan and apply produce no
  mutation artifacts or target writes; a still-supported persisted-version
  drift is specifically `recovery_required`;
- endpoint recognition covers exact 200 JSON, whitespace, refused/no-listener,
  every 3xx without redirect following, non-200, connect/read/total timeout,
  valid oversized `Content-Length`, sentinel-based 4096/4097-byte boundaries,
  malformed/non-object/extra-property/wrong-status JSON, and proves the 500 ms
  total budget;
- VS Code Stable/Insiders Default Profile path selection on Windows/macOS/Linux,
  deterministic dual-channel order, non-default-profile warning de-duplication,
  exact no-`--profile` extension-listing commands, and proof that no non-default
  profile is created, selected, opened, or included in a plan; plan and
  revalidation settings reads prove the exact 1 MiB-plus-sentinel limit and
  `malformed_settings` mapping;
- VS Code running-state observation uses exactly one `--status` call per
  eligible channel after successful version/extension gates in Stable-then-
  Insiders order, with zero calls on an early gate failure or during
  `Revalidate` and unchanged persisted per-target restart requirements; its
  four dual-channel per-target restart combinations, every representable runner
  outcome (`Completed` with zero, null, or nonzero exit; `NotFound`; `Failed`;
  `TimedOut`), stdout non-leakage, and no retry/sleep are proven;
- managed source matrices for official Copilot native/server/file locations and
  independent Windows/macOS/Linux VS Code enterprise policies, whole-channel
  Copilot selection without merge, cross-system conflict/equality behavior,
  server-unobservable warning, and read-only source behavior;
- Copilot CLI exact environment allowlist/forbidden global keys, always-
  unverified managed state, Windows user-environment apply, and macOS/Linux
  detect/plan followed by `apply`/`unsupported_target` with no shell-profile or
  mutation artifact; matching/conflicting
  `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` is detected without entering the write
  allowlist;
- private runtime-root selection covers Windows, macOS, Linux absolute/invalid/
  missing `XDG_DATA_HOME`, and the trusted injected base while proving there is
  no public setup override;
- setup CLI recognition covers all four exact verbs, recognized-verb option
  failures, bare `setup`, unknown setup verbs, and an unknown non-setup
  top-level command; all 34 result codes are mapped exhaustively to process
  exits and exact stdout/stderr behavior;
- adapter parsing covers the 1/128 UTF-16-code-unit lowercase-slug boundaries,
  malformed/uppercase/over-bound input, plan registry resolution only after
  lock/recovery, and status filtering after adapter removal without a registry
  lookup;
- plan/apply lock ownership and recovery happen exactly once, rollback delegates
  its one recovery pass under the caller-held lock, and status owns its lock and
  recovery without an outer dispatcher lock;
- adapter plan/revalidation success and sanitized failure carriers preserve
  their closed warnings/actions, while unexpected/framework failures cannot
  leak exception text or invent diagnostics;
- current-process environment observation is read-only and distinct from the
  current-user persistent environment writer; process values never become a
  mutation target;
- tagged JSONC apply materialization has exact changed-record identity,
  cardinality, order, and expected-hash validation under the existing apply
  lock; no-op records produce no materialization while retaining their generic
  base-state guard; malformed carrier results produce `recovery_required`
  before artifacts or writes;
- materialized settings bytes appear only transiently during bounded planning
  and apply and are absent from plans, ledger, journals, logs, and repository-
  safe evidence; plan-time marker tests prove discard before private-plan/ledger
  creation; deterministic crashes before/after intent, replacement, completion,
  compensation, and rollback prove recovery uses expected hashes plus backups
  and never calls the adapter or rematerializes JSONC;
- non-status targets use adapter order for plan and immutable ledger order for
  apply/rollback, with prospective versus actual rollback availability proven
  separately; plan `no_changes` persists an inspectable private plan and
  `planned` row before apply reaches terminal `no_changes`;
- requested-ID tests distinguish a missing ledger row, an orphan private plan,
  a matching row with a missing/unreadable/mismatched private plan, and a
  lifecycle that is ineligible for apply/rollback;
- a valid persisted plan whose adapter is removed from the registry returns the
  permitted `apply`/`unsupported_adapter` result and leaves all durable bytes
  and targets unchanged;
- environment notification faults before and after delivery, allowing recovery
  replay but forbidding notification before a final state; terminal pending
  replay additionally proves plan/ledger/backup/journal identity, including
  per-member prior/desired hashes and previous/applied aggregates, without
  target reads or writes, while completed notification handling adds no artifact
  requirement;
- ownership preservation, repeated-apply no-op, all-no-op physical-target
  exclusion from ownership/backup quorum, and status/rollback preflight
  equivalence when that unowned target remains current, drifts, or is
  unavailable;
- VS Code/CLI/App-SDK detection and plan contracts;
- Claude strict-version, Windows-native/WSL2/native-Unix partition, explicit
  routing opt-in, readiness reachability, user-settings env/Hook ownership,
  content-policy conflict, and Python/TypeScript SDK guidance contracts;
- Claude settings preservation covers comments, unrelated JSON, Hook ordering,
  stale plan and final guard, reverse compensation, rollback bytes, idempotent
  duplicate apply, and non-waiting barrier-controlled concurrent apply without
  sleeps or retries;
- actual committed ownership-ledger/private-plan v1 fixtures are copied to a
  fresh runtime root and exercised through close/reopen and fresh production
  composition before and after a Claude-arm plan; historical GitHub
  status/apply/rollback and fixture bytes remain unchanged;
- mixed-member target-operation aggregation;
- per-target and change-set status state/rollback aggregation for mixed
  writable and guidance targets;
- lifecycle/reference-state matrix across planned, no-change, applied,
  restored, rolled-back, and mixed partial target outcomes;
- partial aggregate target cases: all desired, all previous, desired+previous,
  previous+third-party, desired+third-party, and unavailable member;
- policy/environment/user-setting precedence;
- Release ZIP and repository wrapper invocation;
- secret/header/value/path negative evidence.

The setup implementation must keep this requirement-to-test mapping:

| Requirement | Executable proof |
| --- | --- |
| strict unshipped v1 ledger snapshot, constructor/fixture coverage, immutable lifecycle updates, no migration/fallback | `SetupStorageTests` round-trip the production serializer and committed `Fixtures/Setup/v1/ownership-ledger.v1.json`; all ledger-target constructor fixtures in apply, compensation, rollback, and recovery tests compile with the required snapshot |
| closed v1 private-plan desired-state union and secret-free JSONC plan representation | Before changing `SetupPlanStore` serialization, `SetupStorageTests` capture and commit `Fixtures/Setup/v1/private-plan.v1.json` from the production serializer with a legacy inline-string `desired_state`, then byte-compare production write-close-reopen output; they accept canonical inline generic file/TOML/opaque records and tagged `SetupTargetKind.Json` `github-copilot` records with either exact VS Code Default Profile label, reject tagged `SetupTargetKind.File`/`SetupTargetKind.Toml`/other-adapter/other-label records and inline on those VS Code JSON records, reject every malformed/unknown/noncanonical/arm-mismatched union shape and 0/1/2048/2049 string boundary, and prove plan-time/revalidation markers are absent from plan/ledger/journal/log evidence except the private backup |
| new-plan exact-current manifest matching versus ledger-origin historical v1 validation | `SetupContractValidationTests` reject a non-current plan manifest; `SetupStorageTests` accept a schema-safe target-matched historical snapshot and reject unknown shape/code, unsafe data, surface mismatch, and cross-field mismatch |
| finite legal snapshot under the retained ledger cap | `SetupStorageTests` construct the largest accepted 16-target/32-change shape with 128-code-unit versions and largest safe fields, prove it serializes below 1 MiB, and prove an over-cap complete ledger is rejected before replacement |
| immutable status DTO fields, adapter not rerun, and guidance sample omission/rehydration | `SetupStatusProjectorTests.Project_AppliedTarget_UsesImmutableSnapshotAndFreshTargetAndRollbackEvidence`, `Projector_HasNoAdapterDetectionDependency`, and `Project_Guidance_RehydratesFixedSampleInMemoryButStatusJsonOmitsIt` project the immutable ledger snapshot without adapter detection and omit `sample` from status JSON |
| missing private plan distinction and fresh target/backup checks | `SetupStatusProjectorTests.Project_NonTerminalMissingPlan_RequiresRecovery` and `Project_TerminalArtifactFailure_FailsClosedWithoutPrivateData` distinguish missing-plan recovery from terminal artifact/current-state rollback unavailability |
| status and rollback use equivalent fresh preflight, including all-NoOp guard-only targets | `SetupStatusProjectorTests.Project_RollbackAvailability_EqualsSharedFreshPreflight` and `SetupRollbackTests.Rollback_Preflight_evaluator_and_execution_reject_same_fresh_state` cover shared valid/drift/backup/all-NoOp fresh-preflight outcomes without adapter detection |
| lifecycle/reference/current aggregation and partial rollback false | `SetupStatusProjectorTests.Project_TerminalAndPlannedLifecycle_UsesCanonicalReference` and `Project_PartialLifecycle_ClassifiesFreshAggregate` cover lifecycle/reference/current aggregation and partial outcomes |
| filter, priority, deterministic tie-break, hard 100-row cap, truncation | `SetupStatusOrderingTests.Project_AppliesExactAdapterAndChangeSetFiltersBeforePriorityCapAndProjection`, `Project_PrioritizesRecoveryRowsThenPlannedThenTerminalAndPreservesTargetOrder`, `Project_UsesLowercaseCanonicalUuidOrdinalTieBreakWithinEqualPriorityAndTimestamp`, and `Project_CapsEligibleRowsAtOneHundredAndSetsTruncatedFromEligibleCount` cover filtering, ordering, cap, and truncation |
| terminal pending environment notification artifact identity and notification-only target isolation | `SetupRecoveryTests` use actual `SetupApplyCoordinator` artifacts for successful replay and valid-64-hex tampering of journal prior/desired hashes and ledger previous/applied aggregates; they also cover missing/corrupt/rebound plan or backup, canonical backup reference, applied/restored/rolled-back terminal lifecycles, current-target drift, no target reads/writes, preserved terminal durable lifecycle, fixed failed overlay, and unchanged completed-notification handling |
| closed apply/error command composition | `SetupCommandDispatcherTests.DispatchApply_ExceptionalCoordinatorFailureRetainsAdapterWithEmptyPayloads`, `SetupContractValidationTests.Serialize_WhenApplyCannotUsePersistedPlan_PreservesTheArtifactFreeExceptionalResult`, and `ConfigurationSetupIntegrationTests.Apply_RemovedAdapter_ReturnsTheExceptionalPairWithoutChangingDurableBytesOrStartingTargetActivity` / `Apply_MacOsCliPlan_ReturnsUnsupportedTargetBeforeAnyShellProfileOrMutationActivity` cover the closed exceptional `apply` pairs and artifact-free no-write behavior |
| setup.v1 recognition, generic adapter parsing, and exhaustive process mapping | `SetupOptionsTests` cover the 1/128 lowercase-slug bounds and recognized-verb grammar; `CliApplicationTests` cover each of the 34 result-code exits, one-JSON stdout/fixed stderr, bare/unknown setup verbs with no JSON/help/lock, and preserved legacy unknown-top-level behavior |
| exact lock/recovery ownership and post-recovery resolution | `SetupCommandDispatcherTests.DispatchPlan_WhenLockIsBusyStopsBeforeRecoveryAndAdapterResolution`, `DispatchApply_ApplicableRowRunsExactlyOneLockAcquisitionAndOneRecoveryPass`, and `DispatchStatus_DelegatesValidatedSameInstanceWithoutOuterLockOrRecovery`, plus `SetupStatusServiceTests.Status_ProductionServiceCompletesRecoveryReadBeforeStatusReadAndProjection` and `Status_LockContentionReturnsSetupBusyWithoutRunningRecoveryOrProjection`, cover lock/recovery ownership and post-recovery resolution |
| diagnostics carrier and non-status target projection ownership | `SetupAdapterRegistryTests` and `SetupCommandDispatcherTests` serialize production plan/revalidation success and sanitized failure carriers, prove closed warning/action preservation and exception redaction, adapter-versus-ledger target source/order, prospective plan versus actual apply rollback availability, and rollback-false results |
| no-change persistence and missing durable artifact distinctions | `SetupCommandDispatcherTests` prove `plan`/`no_changes` persists a private plan plus `planned` ledger row and later apply reaches terminal `no_changes`; paired apply/rollback/status cases distinguish no row, orphan plan, matching row with missing/unreadable/mismatched plan, and ineligible lifecycle without target activity |
| VS Code channel/profile, running-state, and managed-source contract | `VsCodeSetupAdapterTests` cover Stable/Insiders Default Profiles on all three OS path maps, exact no-`--profile` extension commands, dual-channel order, fixed non-default warning/no-create/no-open behavior, Copilot whole-channel precedence, independent enterprise-policy equality/conflict, and apply-time version/extension/policy/member revalidation. They assert the exact tagged v1 `desired_state` union (never an inline document), 1 MiB-plus-sentinel settings reads, supported-version drift as `recovery_required`, and transient materialization with exact expected hash. They also assert exactly one post-gate Stable-then-Insiders `--status` call per eligible channel, zero calls after an early gate failure and during `Revalidate`, no retry/sleep, no stdout leakage, all representable observations (`Completed` with zero, null, or nonzero exit; `NotFound`; `Failed`; `TimedOut`), and the four dual-channel per-record restart combinations with top-level action deduplication only; revalidation proves persisted record requirements are unchanged. Contract shape/validation tests close the warning/action values. |
| Copilot CLI OS and exact environment contract | `CopilotCliSetupAdapterTests` cover the five-member explicit-capture allowlist, forbidden global identity/resource/header/credential keys, matching/conflicting detect-only trace protocol override, environment-only managed warning, Windows apply, and macOS/Linux no-write apply refusal; contract shape/validation tests close the new code/warning/action values |
| cross-platform private setup root | `SetupRuntimeTests` cover Windows/macOS/Linux local-application-data mappings, absolute and invalid/unset `XDG_DATA_HOME`, injected platform base, and absence of a CLI/environment override |
| Local Monitor recognition | `GitHubCopilotEndpointProbeTests` cover the 500 ms/no-redirect/4096-byte-plus-sentinel or oversized-`Content-Length`/exact-JSON matrix and fixed refused-versus-timeout/connected failure mapping |
| Claude nested settings and private-plan arm | `ClaudeSettingsDocumentTests` and `SetupStorageTests` cover exact nested ownership, preservation, malformed/duplicate/oversize input, both existing v1 fixture byte identities, `claude_settings_owned_values_v1` bounds and arm relation, and secret/path/command non-leakage outside the private plan |
| Claude adapter and WSL2 contract | `ClaudeCodeSetupAdapterTests` cover strict 2.1.207 version floor/future version acceptance, prerelease/older/malformed rejection, Windows-native and three-factor WSL2 classification, explicit opt-in, no gateway fallback, and the `/health/ready` matrix: exact ready/degraded identity, refused/no-listener, connect/read/total timeout, every redirect/non-200 including 503, 4096/4097 and `Content-Length` oversize, malformed/non-object/duplicate/unknown/wrong-typed/false-loopback/invalid-invariant responses, with platform-specific fixed codes; they also cover env/content/Hook rules, restart guidance, and Python/TypeScript no-write guidance |
| Claude cross-surface integration | `ClaudeConfigurationSetupIntegrationTests` cover adapter to registry/dispatcher/`setup.v1`, physical Config CLI, repository and Release wrappers, exact `--allow-wsl2-routing` forwarding, stdout/exit parity, apply-time revalidation, stale/final guard, reverse compensation, rollback, idempotency, and deterministic lock contention |
| Actual-v1 restart compatibility after Claude arm | `SetupStorageTests` and `ClaudeConfigurationSetupIntegrationTests` copy the committed `ownership-ledger.v1.json` and `private-plan.v1.json`, run write-close-reopen through a fresh production composition, exercise historical GitHub status/apply/rollback as eligible, add/reopen a Claude plan, and byte-compare both historical fixtures without v0/v2 or fallback parsing |

Required repository validation remains:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

# Configuration Setup Interface

This document is the canonical contract for Issue #66 configuration ownership
and Issue #67 GitHub Copilot guided setup. It defines the public command/result
surface, durable ownership record, transaction semantics, and supported GitHub
Copilot targets.

## Scope

Issue #66 supplies a reusable setup framework. Issue #67 supplies the
`github-copilot` adapter for:

- VS Code GitHub Copilot Chat;
- terminal GitHub Copilot CLI;
- caller-managed GitHub Copilot App / SDK integration guidance.

The framework does not add a Local Monitor HTTP route, Canvas proxy, Razor UI,
remote provisioning, first-trace verification, force rollback, or machine-wide
configuration. Claude Code and Codex adapters remain separate issues.

## Cross-surface contract table

| Surface | Producer | Consumer | Contract | Issue #66/#67 status |
| --- | --- | --- | --- | --- |
| Internal backend | adapter `Detect` / `Plan` | transaction coordinator | `SetupChangePlan` and `SetupChangeRecord` | required |
| CLI | transaction coordinator | terminal user / PowerShell wrapper | `SetupCommandResult` JSON (`setup.v1`) | required |
| PowerShell | repository or Release ZIP `setup.ps1` | Config CLI executable | exact CLI arguments and unchanged JSON stdout | required |
| HTTP | none | none | no route or DTO | not added |
| proxy | none | none | no proxy DTO | not added |
| Local Monitor UI | none | none | no view model | not added |

Mocks and fixtures must be serialized from the same C# result types used by the
CLI. A hand-written HTTP, proxy, or UI facsimile is not an acceptable
cross-surface test because those surfaces are not part of this interface.

## Public commands

```text
config-cli setup plan --adapter github-copilot --target <vscode|cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture]
config-cli setup apply --change-set <uuid-v7>
config-cli setup rollback --change-set <uuid-v7>
config-cli setup status [--adapter github-copilot]
```

`--endpoint` defaults to `http://127.0.0.1:4320` and accepts only loopback HTTP
hosts (`127.0.0.1`, `localhost`, or `::1`). `all` means the three Issue #67
targets at plan time; apply still requires the returned change-set ID. App/SDK
remains a no-write target.

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

## Result contract

Every command writes one JSON object to stdout. The stable top-level shape is:

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
| `adapter` | string or null | registered adapter ID |
| `targets` | array | bounded to 16 physical targets and sorted by plan order |
| `change_sets` | array | status entries; empty for other commands |
| `warnings` | fixed string array | no exception text or raw values |
| `next_actions` | fixed string array | bounded actionable codes |
| `truncated` | boolean | meaningful for status; otherwise false |

Each target record contains:

```json
{
  "record_id": "00000000-0000-7000-8000-000000000000",
  "target_kind": "json",
  "target_label": "vscode-user-settings",
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
`detected_version` is a sanitized semantic version string or null; neither may
contain a path or free-form command output. `operation` is `add`, `replace`,
`remove`, `mixed`, or `no-op`. It is `no-op` when every member is no-op, the
single non-no-op operation when all changed members agree, and `mixed` when two
or more non-no-op member operations differ. Each physical
target contains at most 32 member `changes`; member `previous_state` and
`new_state` are redacted state names, never raw setting values. The `endpoint`
field is allowed only for a validated non-credential-bearing loopback HTTP URL;
foreign endpoints are reported only as `loopback`, `remote`,
`credential_bearing`, or `invalid` state.

`expected_result` is copied from the matching Issue #61 manifest without
inventing capabilities. VS Code uses surface `github-copilot-vscode`; terminal
CLI uses `github-copilot-cli`. App/SDK has no Issue #61 manifest and returns a
null manifest with `planned_source_not_enabled` guidance rather than borrowing
another surface's capability declaration.

`reference_state` and `current_state` are null in plan/apply/rollback target
results. In status, `reference_state` is `base`, `desired`, `previous`, or
`none`; `current_state` is `current`, `stale`, `diverged`, `unavailable`, or
`not_applicable`. `changes` is empty only for a `guidance` target. `guidance` is null for writable
targets. App/SDK uses the bounded shape
`{"kind":"caller_managed_sample","language":"dotnet","sample":"..."}`.
It never contains a discovered caller path or file content.

### Status projection

After applying the optional adapter filter, `setup status` orders eligible
change sets by these classes: (1) `partial`, `applying`, `compensating`, and
`rolling_back`; (2) `planned`; (3) terminal states. Within each class it orders
by `updated_at` descending and then canonical lowercase UUID string ordinal
ascending. It returns the first `min(100, eligible_count)` entries and sets
`truncated` to `eligible_count > returned_count`. Thus recovery-blocking entries
are prioritized within, but never exempt from, the hard 100-entry bound. Each
entry contains only the
repository-safe change-set fields, physical target summaries, current
verification state (`current`, `stale`, `diverged`, `unavailable`, or
`not_applicable`),
and whether rollback remains available. It never returns private plan or backup
content.

Each status entry has exactly `change_set_id`, `adapter`, `selected_target`,
`created_at`, `updated_at`, `state`, nullable `outcome_code`, `current_state`,
`rollback_available`, and `targets`. Its target summaries use the same bounded
public target DTO but omit `guidance.sample` after the original plan result;
they retain only `guidance.kind` and `guidance.language`.

Status uses lifecycle-relative reference state for each writable target:

| Change-set lifecycle | Target `reference_state` |
| --- | --- |
| `planned` | `base` |
| `no_changes` | `desired` (identical to the observed base) |
| `applied` | `desired` |
| `restored` or `rolled_back` | `previous` |
| `partial` | aggregate member outcomes: `desired` when every member/file remains desired, `previous` when every member/file is previous, or `none` for a safely classified mixture or failed classification |
| in-progress state | journal-derived `base`, `desired`, or `previous`; a failed recovery is projected effectively as `partial` |

For `base`, `desired`, or `previous`, `current` means the canonical current
state equals that reference and `stale` means a later known mismatch.
`diverged` means the transaction safely classified a target that has no single
aggregate reference: desired/previous members are mixed, or at least one
third-party member/file was preserved. Its reference is `none`. `unavailable`
means at least one member/file could not be safely verified or classified, also
with reference `none`. Guidance is
`not_applicable` with reference `none`.

Change-set `current_state` is `diverged` if any writable target is diverged,
otherwise `stale` if any is stale, otherwise `unavailable` if any is
unavailable, otherwise `current` when all writable targets are current, and
`not_applicable` when there are no writable targets. Change-set
`rollback_available` is true only when lifecycle is
`applied`, at least one writable target exists, every writable target is
`current`, and every writable target has a valid backup and reports
`rollback_available=true`. Guidance targets do not participate in either
writable-target aggregation. Every `partial` change set reports
`rollback_available=false`, including an all-desired or all-previous partial
whose unresolved transaction outcome has not reached a terminal lifecycle.

A missing private plan for a non-terminal change set is `recovery_required`. A
corrupt or unsupported ledger fails the whole command with `ledger_corrupt` or
`ledger_version_unsupported`; it is not treated as an empty ledger. Status is
the only command allowed while an unresolved partial recovery exists.

`status` performs no newly requested setup mutation, but mandatory interrupted-
transaction recovery may restore configured targets. Successful recovery uses
the corresponding `interrupted_*_recovered` code and still returns the bounded
status projection. Failed recovery with a readable ledger returns
`success=false`, `code=interrupted_recovery_failed`, both recovery-correlation
fields, and the bounded projection with the affected entry effectively shown as
`partial` and `outcome_code=interrupted_recovery_failed`, even if persisting that
final ledger update failed. An unreadable, corrupt, or unsupported ledger and
lock contention return no status projection.

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
- `malformed_settings`
- `permission_denied`
- `unsafe_path`
- `stale_plan`
- `rollback_stale`
- `rollback_not_available`
- `port_owned_by_foreign_process`
- `partial_apply`
- `partial_rollback`
- `setup_busy`
- `recovery_required`
- `interrupted_recovery_failed`
- `ledger_corrupt`
- `ledger_version_unsupported`
- `internal_error`

Argument failures use exit code `2`; stale/conflict failures `3`; target
availability failures `4`; apply/IO failures `5`; partial rollback failures
`6`. Success uses `0`. stderr may contain only the fixed result code.

## Runtime storage and versioning

Default private runtime root:

```text
%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\setup\
```

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
Issues #66/#67.

Schema version `1` is the first shipped ownership-ledger version. There is no
fabricated v0 migration fixture. The loader accepts exactly version 1 and fails
closed with `ledger_version_unsupported` for an unknown version. Tests must
write, close, reopen, and verify the shipped v1 state. A later schema version
must add fixtures from every actually shipped older version and verify migration
through restart.

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
- tool version.

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

1. detects the target and supported version;
2. validates path ancestry and structured configuration before proposing a
   replacement;
3. reads current state and calculates the base hash;
4. calculates effective source and managed state;
5. produces only redacted previous/new state;
6. records restart and rollback availability;
7. writes and flushes the private immutable plan first, then atomically replaces
   the repository-safe ledger with its planned record.

The two files cannot be atomically replaced together. Their ordering is
crash-consistent: a private plan without a ledger row is an ignored orphan; a
ledger non-terminal row without its private plan is `recovery_required` and
blocks mutation. A normal write failure removes the orphan plan when possible,
but recovery never depends on that cleanup.

Malformed JSON/JSONC or TOML is `malformed_settings`; the adapter does not
best-effort replace it. JSONC comments and trailing commas are accepted for VS
Code settings. A writer must preserve unrelated keys and comments and must not
delete a setting it does not own. The framework TOML codec supports the bounded
table/scalar syntax already emitted by Config CLI samples and rejects malformed
or unsupported TOML before a plan is persisted. Issue #67 does not write TOML;
a later adapter must extend the codec contract before accepting a broader TOML
shape.

## Transaction and concurrency rules

The coordinator acquires `setup.lock` with an exclusive, non-waiting file lock.
Contention returns `setup_busy`; there is no sleep, retry, or timeout loop.

Lifecycle transitions are:

```text
planned -> applying -> applied
                    -> compensating -> restored
                                    -> partial

applied -> rolling_back -> rolled_back
                        -> partial
```

A no-write stale apply remains `planned` with outcome `stale_plan`. A no-write
stale or unavailable rollback retains its prior lifecycle state and records
`rollback_stale` or `rollback_not_available` as its outcome. Attempt outcomes are
not additional lifecycle states. `partial` is the only unresolved mutation-
blocking state.

Apply rules:

1. validate all plan base hashes, target paths, non-reparse ancestry, supported
   target versions, managed states, and every distinct writable endpoint before
   any backup, journal, ledger transition, or target write;
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

User environment mutation uses the current-user API only
(`EnvironmentVariableTarget.User` / HKCU user environment), broadcasts one
environment-change notification attempt on an uninterrupted path only after the
final committed or restored state, and never uses `setx`. A recovery retries the
notification when prior delivery cannot be proven, so duplicate notifications
are permitted but an early notification is not. Its canonical hash and backup cover only the member keys
listed in that physical target; unrelated user environment values are neither
read into the plan nor changed/restored. It does not require administrator
privileges.

Rollback validates that every current hash equals its applied hash before the
first restore. One mismatch returns `rollback_stale` and performs no write.
Force rollback is absent. DB, telemetry, logs, and other runtime data are never
deleted by setup rollback.

Repeated plan/apply against the desired state produces `no-op` member changes and
`no_changes`; it creates no second ownership claim and does not rewrite files or
environment values.

Tests for stale state, lock contention, concurrent edit, failure after each
mutation, compensation, and partial rollback use barriers or injected fault
points. Sleeps and probabilistic timing assertions are prohibited.

## GitHub Copilot adapter

Adapter ID is `github-copilot`.

### VS Code GitHub Copilot Chat

Supported stable VS Code for apply is version `1.128.0` or newer because that is
the first release that enforces the required precedence across managed-setting
delivery channels. Versions 1.119 through 1.127 are detected but return
`unsupported_version` with `upgrade_vscode`; they are not mutated. The adapter runs
`code --version` and `code --list-extensions --show-versions` through a bounded
process runner and requires `GitHub.copilot-chat`. VS Code Insiders is detected
but not mutated by Issue #67; it returns `unsupported_target` with a next action
to use stable VS Code.

The writable user settings are:

- `github.copilot.chat.otel.enabled = true`;
- `github.copilot.chat.otel.exporterType = "otlp-http"`;
- `github.copilot.chat.otel.otlpEndpoint = <validated loopback endpoint>`.

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

The adapter reads the locally observable managed channels without modifying
them:

- native Copilot MDM values under
  `HKEY_LOCAL_MACHINE\SOFTWARE\Policies\GitHubCopilot`;
- Windows VS Code enterprise policy under
  `Software\Policies\Microsoft\VSCode`;
- `%ProgramFiles%\GitHubCopilot\managed-settings.json`.

Server-managed values resolved from the signed-in GitHub account cannot be
reliably read by an external CLI. If no higher locally observable channel proves
the value, a successful plan includes warning `managed_policy_unverified` and
the next action `run_vscode_policy_diagnostics`. It may write a user setting,
but must not claim
that the setting is effective until VS Code policy diagnostics confirms it.
Any observed conflicting managed value yields `managed_policy_conflict` and no
write for the managed setting.

If a `Code` process is running, restart requirement is `restart_vscode`.
Post-apply verification reparses the settings, rechecks the target hashes and
effective-source calculation, and does not claim that a trace arrived.

### Terminal GitHub Copilot CLI

The adapter detects `copilot version`. Version `1.0.4` or newer is supported
because `1.0.4` introduced the documented OpenTelemetry instrumentation.

The default current-user environment records are:

- `COPILOT_OTEL_ENABLED=true`;
- `COPILOT_OTEL_EXPORTER_TYPE=otlp-http`;
- `OTEL_EXPORTER_OTLP_ENDPOINT=<validated loopback endpoint>`;
- `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`.

The adapter does not set `client.kind` globally and does not change
`OTEL_RESOURCE_ATTRIBUTES`, `OTEL_EXPORTER_OTLP_HEADERS`, or any credential.
The default plan does not change content capture. With
`--include-content-capture`, it separately proposes
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` and emits the same
sensitive warning.

The plan displays current-process versus current-user environment state and
warns `shared_user_environment_affects_other_processes`. Already-running
terminals and Copilot CLI sessions require `restart_terminal_session`.
Copilot CLI emits traces and metrics; Local Monitor currently accepts traces.
The static result reports metric delivery as `not_verified` rather than
claiming complete signal receipt.

Locally observable Copilot managed telemetry settings apply to the CLI too.
The adapter never edits a managed-settings source and stops on an observed
conflict.

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

## Endpoint and error-state detection

Before returning a writable plan, the adapter probes the selected loopback port.
Apply repeats the probe for every distinct writable endpoint after recovery and
all other validation, but before backups or any write:

- no listener: warning `monitor_not_running`, plan remains usable;
- Local Monitor health endpoint recognized: no conflict;
- listener present but Local Monitor not recognized:
  `port_owned_by_foreign_process`, no apply.

The apply-time probe is an observation and does not claim that port ownership
cannot change after the probe.

Other fixed next actions include:

- `install_vscode`
- `install_github_copilot_chat_extension`
- `upgrade_vscode`
- `install_copilot_cli`
- `upgrade_copilot_cli`
- `run_vscode_policy_diagnostics`
- `restart_vscode`
- `restart_terminal_session`
- `start_local_monitor`
- `review_content_capture_warning`
- `run_first_trace_doctor` (Issue #69, never performed here)

## Security and evidence rules

Plan, ledger, status, logs, repository-safe evidence, and test failure messages
must not contain:

- credentials, tokens, authorization headers, or secret-like values;
- raw previous/new setting values other than a validated loopback endpoint;
- absolute or user-derived path;
- raw exception messages;
- prompts, responses, tool inputs/results, source fragments, or PII.

Negative tests inject markers into every input boundary and assert that no
serialized command result, ledger, log, exception mapping, private plan, or
committed fixture contains previous-state secret/value markers. A flushed local
backup is allowed to contain exact previous state because it is the rollback
source; tests instead prove that backup content is never serialized or logged.
Fixtures are synthetic and use the production DTO serializer.

## Validation

Focused validation covers:

- ledger v1 write-close-reopen and unknown-version rejection;
- JSONC/TOML malformed fail-closed behavior;
- file backup/temp/atomic replace and non-reparse path policy;
- deterministic stale apply/rollback and lock contention;
- file faults before intent, after intent, after replace, and after completion,
  followed by close/reopen recovery;
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
  projection after failed recovery;
- status hard-cap, priority, timestamp/UUID tie-break, and adapter-filter order;
- endpoint, supported-version, and managed-state changes between plan and apply
  produce no mutation artifacts or target writes;
- environment notification faults before and after delivery, allowing recovery
  replay but forbidding notification before a final state;
- ownership preservation and repeated-apply no-op;
- VS Code/CLI/App-SDK detection and plan contracts;
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

Required repository validation remains:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

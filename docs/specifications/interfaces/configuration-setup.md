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
App/SDK has no Issue #61 manifest and returns a null manifest with
`planned_source_not_enabled` guidance rather than borrowing another surface's
capability declaration. Ledger-origin status validation is deliberately
separate, as defined below, so a valid immutable historical snapshot is not
silently rewritten or rejected only because the embedded manifest later
changes.

`reference_state` and `current_state` are null in plan/apply/rollback target
results. In status, `reference_state` is `base`, `desired`, `previous`, or
`none`; `current_state` is `current`, `stale`, `diverged`, `unavailable`, or
`not_applicable`. `changes` is empty only for a `guidance` target. `guidance` is null for writable
targets. App/SDK uses the bounded shape
`{"kind":"caller_managed_sample","language":"dotnet","sample":"..."}`.
It never contains a discovered caller path or file content.

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
`github-copilot-vscode` or `github-copilot-cli` public surface matching that
snapshot target, `source_adapter: "otel-http+copilot-compatible-hook"`, the
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
capacity is an accepted constraint for Issues #66/#67; automatic pruning,
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

- ledger v1 status snapshot write-close-reopen, exact-shape/cross-field
  validation, plan/current-manifest versus ledger/historical-manifest validation,
  128-code-unit detected-version boundary, missing-snapshot/unknown-version
  rejection, and largest-legal-change-set size below the retained 1 MiB cap;
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
  projection after failed recovery;
- status hard-cap, priority, timestamp/UUID tie-break, and adapter-filter order;
- status immutable-field reconstruction from the ledger snapshot while changed
  installation/version/policy/manifest facts are not rediscovered;
- fresh current/reference/backup verification after external target or backup
  changes, including terminal missing-plan/unavailable classification;
- endpoint, supported-version, and managed-state changes between plan and apply
  produce no mutation artifacts or target writes;
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
| strict unshipped v1 snapshot, constructor/fixture coverage, immutable lifecycle updates, no migration/fallback | `SetupStorageTests` round-trip the production serializer and committed `Fixtures/Setup/v1/ownership-ledger.v1.json`; all ledger-target constructor fixtures in apply, compensation, rollback, and recovery tests compile with the required snapshot |
| new-plan exact-current manifest matching versus ledger-origin historical v1 validation | `SetupContractValidationTests` reject a non-current plan manifest; `SetupStorageTests` accept a schema-safe target-matched historical snapshot and reject unknown shape/code, unsafe data, surface mismatch, and cross-field mismatch |
| finite legal snapshot under the retained ledger cap | `SetupStorageTests` construct the largest accepted 16-target/32-change shape with 128-code-unit versions and largest safe fields, prove it serializes below 1 MiB, and prove an over-cap complete ledger is rejected before replacement |
| immutable status DTO fields, adapter not rerun, and guidance sample omission/rehydration | `SetupStatusTests` project from the ledger after current installation/policy/manifest facts change and use an adapter fake that fails if invoked; `SetupContractShapeTests` prove the fixed in-memory guidance sample validates while status JSON omits `sample` |
| missing private plan distinction and fresh target/backup checks | `SetupStatusTests` prove non-terminal missing plan returns `recovery_required`, terminal missing/unreadable plan or current target projects unavailable, and missing/unsafe/mismatched owned backup makes rollback unavailable |
| status and rollback use equivalent fresh preflight, including all-NoOp guard-only targets | paired `SetupStatusTests` and `SetupRollbackTests` fixtures assert the same availability/preflight outcome for valid state, owned-target drift, backup mismatch, and unowned all-NoOp target drift; no adapter detection is used |
| lifecycle/reference/current aggregation and partial rollback false | `SetupStatusTests` cover planned/no-change/applied/restored/rolled-back/in-progress/partial plus desired, previous, mixed, third-party, missing, and unavailable member states |
| filter, priority, deterministic tie-break, hard 100-row cap, truncation | `SetupStatusOrderingTests` cover adapter filter before 99/100/101-row ordering and cap, equal-priority states, timestamp order, and lowercase UUID ordinal ties |
| terminal pending environment notification artifact identity and notification-only target isolation | `SetupRecoveryTests` use actual `SetupApplyCoordinator` artifacts for successful replay and valid-64-hex tampering of journal prior/desired hashes and ledger previous/applied aggregates; they also cover missing/corrupt/rebound plan or backup, canonical backup reference, applied/restored/rolled-back terminal lifecycles, current-target drift, no target reads/writes, preserved terminal durable lifecycle, fixed failed overlay, and unchanged completed-notification handling |

Required repository validation remains:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

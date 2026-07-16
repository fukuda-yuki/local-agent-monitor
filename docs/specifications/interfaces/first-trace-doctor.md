# First-Trace Doctor Interface

This document is the canonical source-independent contract for Issue #102.
It defines one deterministic Doctor domain used by direct callers, Config CLI,
and Local Monitor HTTP. Issues #103 and #104 supply source-specific observations
through this contract; Issue #105 owns the later proxy and UI.

## Scope and ownership

The shared Doctor domain owns:

- the fact snapshot and its twelve explicit known/unknown fact families;
- the twenty-state catalog, deterministic evaluation, and `doctor.v1` result;
- verification lifecycle types and transition validation;
- JSON serialization and the bounded human-readable projection; and
- clock and verification-store interfaces.

The evaluator is pure. It does not read a database, environment, process,
network endpoint, or clock. Config CLI and Local Monitor are adapters over the
same already-evaluated `DoctorResult`; they do not select states, reorder them,
or replace missing facts. `CopilotAgentObservability.Persistence.Sqlite` owns
Doctor persistence but not evaluation.

The Doctor is separate from D051 process readiness. Doctor state, source
compatibility, verification-store availability, and verification transitions
must not add a `GET /health/ready` check, reason, threshold, configuration
name, body field, or status transition and must not prevent Local Monitor
startup, telemetry ingestion, or source-independent evaluation.

## Common lexical and size rules

Unless a narrower rule is stated below:

- `source_surface` and `source_adapter` match
  `[a-z0-9][a-z0-9._-]{0,63}`. `source_adapter` may be null only where this
  document says it is optional.
- Doctor and verification identifiers are canonical lowercase UUIDv7 strings:
  `[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}`.
- timestamps are UTC RFC 3339 round-trip strings. Writers emit the canonical
  `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` form; inputs with a non-UTC offset,
  missing fractional precision, or a non-round-trip form are invalid.
- an opaque evidence reference is 1..128 characters, passes the repository's
  unsafe-value guard, and contains no raw content, PII, credential, authorization
  value, URI, or absolute/local path.
- a direct fact snapshot contains at most 16 distinct typed observations.
- an ordered accepted-evidence list contains at most 16 distinct references.
- one verification stores at most 100 distinct evidence candidates.
- a CLI input file and an HTTP request body are each at most 65,536 bytes. The
  implementation reads at most the limit plus one sentinel byte and rejects an
  over-limit input before JSON deserialization or persistence.
- a verification window is 1 through 30 minutes inclusive.
- arrays preserve the canonical order specified here; callers cannot use input
  order to change output order.

Unknown properties, duplicate JSON properties, non-canonical enum spellings,
non-finite or out-of-range numbers, and inconsistent cross-field combinations
are invalid. Unknown never means false, zero, supported, successful, or absent.

## Doctor fact snapshot

`DoctorFactSnapshot` uses `schema_version = "doctor.facts.v1"` and contains:

- required `source_surface`;
- optional `expected_source_adapter`;
- required input `observed_at`;
- optional `verification_id`;
- ordered, distinct, typed `observations`; and
- the twelve fact families below.

Each family property is present and is either null, meaning the entire family
is unknown, or an object whose fields use the listed closed values. An explicit
`unknown` member is distinct from every negative value. A result reports a
family name in `missing_fact_families` when the family is null or when an
`unknown` member prevents a supported conclusion.

| Canonical family name | Closed facts |
| --- | --- |
| `install_and_source_version` | `monitor_install`: `unknown|installed|not_installed`; `source_version`: `unknown|supported|unsupported`; `source_feature`: `unknown|available|unavailable` |
| `process_receiver_and_port` | `monitor_process`: `unknown|running|not_running`; `receiver_bind`: `unknown|bound|not_bound`; `port_owner`: `unknown|monitor|foreign|none` |
| `source_effective_configuration` | `endpoint_alignment`: `unknown|match|mismatch` |
| `endpoint_reachability` | `reachability`: `unknown|reachable|unreachable` |
| `protocol_and_signal_compatibility` | `protocol`: `unknown|http_protobuf|mismatch`; `trace_signal`: `unknown|enabled|disabled` |
| `source_version_and_schema_diagnostics` | `compatibility`: `unknown|supported|unsupported_source_version|feature_unavailable`; `schema`: `unknown|matching|drift_detected` |
| `last_ingest` | `outcome`: `unknown|none|accepted|rejected` |
| `raw_persistence` | `outcome`: `unknown|not_persisted|persisted` |
| `projection` | `outcome`: `unknown|not_started|pending|completed|failed` |
| `exact_session_binding` | `requirement`: `unknown|required|not_required`; `outcome`: `unknown|unbound|exact_bound|not_applicable`; `not_applicable` is valid only when the requirement is `not_required` |
| `completeness_and_content` | `completeness`: `unknown|unbound|partial|rich|full`; `content_capture`: `unknown|enabled|disabled|unsupported`; `raw_access`: `unknown|available|sanitized_only` |
| `restart_or_new_process` | `requirement`: `unknown|required|not_required` |

The two source-version facts serve different evidence boundaries. The install
family records the detected source version gate available before source
diagnostics; the diagnostics family records compatibility from the versioned
source-schema observation. A known unsupported result in either family emits
the same shared state and never creates a source-specific Doctor enum.

When `verification_id` is present, the exact verification context supplied by
the application service must be present. The snapshot's source must match that
context, and its adapter must match when the verification has an expected
adapter. A trace ID by itself,
the latest trace, repository, workspace, working directory, process identity,
or timestamp proximity is never verification context.

## Shared observation and persisted-candidate contracts

`DoctorObservation` is the source-neutral typed evidence input to the pure
evaluator. It contains `source_surface`, optional `source_adapter`,
`evidence_class`, `evidence_kind`, sanitized `evidence_ref`, and `observed_at`.
Its closed values are:

| Field | Values |
| --- | --- |
| `evidence_class` | `real_source`, `synthetic_probe` |
| `evidence_kind` | `ingest`, `raw_persistence`, `projection`, `exact_session_binding`, `completeness_content` |

Direct evaluation callers provide ordered `DoctorObservation` values in the
snapshot, so the pure evaluator can distinguish real-source evidence from a
synthetic probe without reading persistence or inferring evidence type from an
opaque reference. Observation source/adapter must match the snapshot source and
optional expected adapter. A positive ingest/persistence/projection/binding/
completeness fact cannot satisfy `first_trace_ready` unless a matching typed
observation exists.

Issues #103 and #104 may submit facts and candidates only through the shared
source-neutral types. They must not add a source-specific state, severity,
reason code, retryability value, next action, evidence class, or evidence kind.

`DoctorEvidenceCandidate` is the separate persisted verification carrier. It
contains a candidate UUIDv7, verification UUIDv7, the same source-neutral
class/kind fields as `DoctorObservation`, source surface, optional source
adapter, sanitized opaque reference, `observed_at`, and `expires_at`.

Candidate observation is an internal application/store operation named
`ObserveCandidate`. Issue #102 exposes no CLI command or HTTP route for it.
Synthetic probes may establish receiver, persistence, or projection health,
but never satisfy a real-source requirement, never establish exact Session
binding or completeness/content, and never enter a Session candidate reference.
Completing a verification accepts only caller-selected opaque references. The
store/service resolves those references against existing unexpired candidates,
validates expected source/adapter and candidate limits, and constructs trusted
`DoctorObservation` values for the evaluator. A completion caller cannot
supply or override candidate class, kind, source, adapter, timestamp, or
expiry. The completion request's `fact_snapshot.observations` must therefore be
empty; non-empty observations are `invalid_input`. This separation prevents a
caller from promoting a selected synthetic candidate to `real_source` while
keeping direct evaluation independently capable of representing typed evidence.

### GitHub Copilot source adapter handoff

Issue #103 uses one source-specific Doctor adapter identity across all evidence
selected for a verification because the v1 store requires exact adapter
equality. The canonical pairs are:

| Guided setup target | `source_surface` | `source_adapter` |
| --- | --- | --- |
| VS Code Copilot Chat | `github-copilot-vscode` | `github-copilot-doctor` |
| Copilot CLI | `github-copilot-cli` | `github-copilot-doctor` |
| caller-managed App/SDK | `github-copilot-app-sdk` | `github-copilot-doctor` |

`github-copilot-doctor` is the candidate-producing adapter, not a replacement
for actual OTLP, compatible-Hook, or SDK provenance. Before normalization it
must validate source-owned evidence for the selected surface: exact selected
raw/Session identity, actual source adapter, and exact linkage needed by the
claimed evidence kind. Capability-manifest composite adapter labels are not
Doctor adapter tokens and capability declaration is not observed evidence.

Setup detect/plan/apply/no-op/rollback/status may populate only directly
observed install/version, receiver/port, effective configuration, reachability,
protocol/signal, and restart/new-process facts. Setup success and synthetic
probes produce no real-source candidate. Accepted ingest, raw persistence,
projection, exact binding, and completeness/content remain independent runtime
evidence gates. A selected raw record with no successful projection row does
not distinguish `not_started`, `pending`, or `failed`; all remain unknown until
an exact per-record projection disposition exists.

When a surface cannot safely carry the opaque verification ID, candidate
selection is an explicit raw-record or native-Session selection followed by
exact source and identity validation. Latest trace/Session, repository,
workspace, cwd, process identity, trace ID alone, and timestamp proximity are
never selection or binding evidence. The shared Config CLI JSON/human result
and the five Local Monitor HTTP routes remain unchanged; Issue #105 owns any
common proxy or UI.

## Fixed state catalog

The catalog contains exactly these twenty states. Severity, retryability, next
action, and relative blocking precedence are immutable in v1.

| Catalog # / role | State code | Severity | Retryability | Next action |
| ---: | --- | --- | --- | --- |
| 1 | `monitor_not_installed` | `error` | `after_action` | `install_monitor` |
| 2 | `monitor_not_running` | `error` | `after_action` | `start_monitor` |
| 3 | `receiver_not_bound` | `error` | `after_action` | `restart_monitor` |
| 4 | `port_owned_by_foreign_process` | `error` | `after_action` | `free_or_change_port` |
| 5 | `endpoint_mismatch` | `error` | `after_action` | `update_source_endpoint` |
| 6 | `protocol_mismatch` | `error` | `after_action` | `use_http_protobuf` |
| 7 | `signal_disabled` | `error` | `after_action` | `enable_trace_signal` |
| 8 | `unsupported_source_version` | `error` | `after_action` | `use_supported_source_version` |
| 9 | `feature_unavailable` | `error` | `after_action` | `use_supported_source_surface` |
| 10 | `agent_restart_required` | `warning` | `after_action` | `restart_source_process` |
| 11 | `endpoint_unreachable` | `error` | `after_action` | `verify_endpoint_reachability` |
| 12 | `payload_rejected` | `error` | `after_action` | `inspect_rejected_payload` |
| 13 | `raw_persisted_projection_pending` | `warning` | `automatic` | `wait_for_projection` |
| 14 | `projection_failed` | `error` | `after_action` | `open_projection_diagnostics` |
| 15 | `session_unbound` | `error` | `after_action` | `select_exact_session` |
| 16 / advisory 1 | `content_capture_disabled` | `warning` | `after_action` | `enable_content_capture_if_desired` |
| 17 / advisory 2 | `sanitized_only_raw_unavailable` | `warning` | `after_action` | `restart_without_sanitized_only_if_desired` |
| 18 / advisory 3 | `schema_drift_detected` | `warning` | `after_action` | `review_source_diagnostics` |
| 19 / terminal 1 | `ready_no_real_trace` | `info` | `after_action` | `run_bounded_source_interaction` |
| 20 / terminal 2 | `first_trace_ready` | `info` | `none` | `open_verified_trace_or_session` |

In v1 every state's `reason_codes` is the one-element array containing exactly
its `state_code`. A later issue cannot add a reason value without a new Doctor
contract version.

### Applicability

- `monitor_not_installed`: `monitor_install=not_installed`.
- `monitor_not_running`: installed monitor and `monitor_process=not_running`.
- `receiver_not_bound`: running monitor and `receiver_bind=not_bound`.
- `port_owned_by_foreign_process`: `port_owner=foreign`.
- `endpoint_mismatch`: `endpoint_alignment=mismatch`.
- `protocol_mismatch`: `protocol=mismatch`.
- `signal_disabled`: `trace_signal=disabled`.
- `unsupported_source_version`: either source-version fact is known
  unsupported.
- `feature_unavailable`: either feature fact is known unavailable.
- `agent_restart_required`: restart/new-process requirement is `required`.
- `endpoint_unreachable`: `reachability=unreachable`.
- `payload_rejected`: last ingest outcome is `rejected`.
- `raw_persisted_projection_pending`: raw is persisted and projection is
  `not_started` or `pending`.
- `projection_failed`: projection is `failed`.
- `session_unbound`: exact binding is required and outcome is the known value
  `unbound`. A required binding with `outcome=unknown` is not a negative state;
  it follows `partial_fact_snapshot`. `requirement=required` with
  `outcome=not_applicable` is invalid input.
- `content_capture_disabled`: content capture is `disabled` or `unsupported`.
- `sanitized_only_raw_unavailable`: raw access is `sanitized_only`.
- `schema_drift_detected`: schema is `drift_detected`.

`first_trace_ready` requires all of the following with no blocking state:

1. a typed `real_source` / `ingest` observation matching the expected source and,
   when present, expected adapter;
2. a matching `real_source` / `raw_persistence` observation;
3. a matching `real_source` / `projection` observation and completed projection
   fact;
4. a matching `real_source` / `exact_session_binding` observation when the
   source contract requires exact binding;
5. known completeness/content facts plus a matching `real_source` /
   `completeness_content` observation when that evidence is required; and
6. for a persisted verification, selected evidence rows that exist, are
   unexpired, and match the verification source.

Content-disabled, sanitized-only, or unrelated `schema_drift_detected` facts
are advisories and do not by themselves prevent `first_trace_ready`. In
particular, D059 is preserved: schema drift unrelated to the exact evidence
must not fail an otherwise exact verification.

`ready_no_real_trace` applies only when no blocking state exists and the
known infrastructure/configuration facts support a bounded real-source
interaction, but `first_trace_ready` requirements are not yet satisfied.

### Ordering and primary state

Evaluation is deterministic:

1. if one or more blocking states apply, emit only those blockers in numeric
   precedence order; emit no terminal state and no advisory;
2. otherwise emit exactly one terminal state:
   `first_trace_ready` when its complete rule holds, otherwise
   `ready_no_real_trace`;
3. after that terminal state, emit applicable advisories in this exact order:
   `content_capture_disabled`, `sanitized_only_raw_unavailable`,
   `schema_drift_detected`.

`primary_state` is the first blocking state, or otherwise the terminal state.
Advisories are never primary and are never emitted beside a blocker. Later
positive facts do not remove an earlier applicable blocking state.

If an unknown fact prevents the evaluator from deciding whether a required
blocking or terminal condition applies, evaluation returns
`partial_fact_snapshot` with ordered `missing_fact_families` and no fabricated
state. Missing families use the twelve-family order above. Advisory-only
unknowns do not block a conclusion and are reported as missing families while
the supported terminal result remains valid.

The `partial_fact_snapshot` projection is fixed. It has `success=false`,
`code=partial_fact_snapshot`, a non-null `evaluation`,
`evaluation.primary_state=null`, `evaluation.states=[]`, and a nonempty
`evaluation.missing_fact_families` in the twelve-family order. For direct
evaluation, `verification=null`. For a partial verification-complete attempt,
`verification` is the current unchanged active verification; revision,
accepted evidence, state, and timestamps are unchanged. No placeholder state
or primary state is permitted.

## `doctor.v1` result

Every direct, CLI JSON, and HTTP operation returns the same shared result shape:

```text
DoctorResult {
  schema_version: "doctor.v1",
  success: boolean,
  code: fixed code,
  evaluation: DoctorEvaluation | null,
  verification: DoctorVerification | null
}
```

`DoctorEvaluation` contains `source_surface`, nullable `primary_state`, ordered
`states`, and ordered `missing_fact_families`. Each state contains:

```text
schema_version, state_code, severity, source_surface, evidence_refs,
reason_codes, next_action, retryability, observed_at, verification_id
```

`schema_version` on a state is `doctor.v1`. `verification_id` is null outside a
verification. State evidence references are selected from matching typed
observations, de-duplicated, and ordered by observation ordinal after
validation. The evaluator never obtains a current timestamp;
therefore byte-equivalent valid input produces byte-equivalent ordered JSON.

`DoctorVerification` contains:

```text
verification_id, expected_source_surface, expected_source_adapter,
state, revision, started_at, expires_at, completed_at, cancelled_at,
accepted_evidence_refs
```

Persisted `state` is `active`, `completed`, or `cancelled`. On read, an active
row whose `expires_at` is not after the injected clock is projected with
effective state `expired`; `expired` is never persisted. Revision starts at 1
and increments once, in the same transaction as a successful complete or
cancel transition. Exactly one of `completed_at` and `cancelled_at` is set for
the corresponding terminal state.

Fixed successful codes are `evaluation_completed`, `verification_started`,
`verification_active`, `verification_completed`, and
`verification_cancelled`. Fixed non-success codes are:

| Code | Meaning |
| --- | --- |
| `invalid_arguments` | command/route option or transport argument is invalid |
| `invalid_input` | bounded JSON or cross-field validation failed |
| `unsupported_schema_version` | a Doctor input/result schema version is not supported |
| `partial_fact_snapshot` | required fact families remain unknown |
| `verification_not_found` | no verification exists for the ID |
| `verification_stale` | expected revision does not equal current revision |
| `verification_expired` | active verification is expired |
| `verification_already_cancelled` | requested transition targets a cancelled row |
| `verification_already_completed` | requested transition targets a completed row |
| `expected_source_mismatch` | facts or evidence do not match expected source/adapter |
| `evidence_not_found` | a selected evidence reference is not an existing candidate |
| `evidence_expired` | a selected evidence candidate is expired |
| `doctor_store_busy` | the Doctor store is locked/busy |
| `doctor_store_unavailable` | Doctor schema/store is unavailable or unsupported |
| `internal_error` | unexpected sanitized failure |

No error contains parser, filesystem, SQLite, JSON, HTTP-client, or application
exception text. JSON is the canonical machine projection. Human output is a
bounded projection of the same `DoctorResult`, uses only catalog labels/codes
and sanitized references, and never re-evaluates facts.

`success=true` means that the requested evaluation/lifecycle operation was
processed successfully; it does not mean telemetry is ready. A valid non-ready
evaluation therefore uses `code=evaluation_completed`, carries its ordered
diagnosis, returns HTTP 200, and maps to CLI exit 3. An evaluation whose primary
state is `first_trace_ready` uses the same code and maps to CLI exit 0.
`partial_fact_snapshot` always uses the fixed `success=false` projection above;
all other validation/transport/store errors have `evaluation=null` unless this
document explicitly assigns a projection.

## Verification lifecycle

Start creates a server-generated UUIDv7, expected source and optional adapter,
explicit `started_at`, bounded `expires_at`, revision 1, active state, and no
accepted evidence. `expires_at` must be strictly after `started_at` and within
the 1..30-minute window. The application service supplies `started_at` from an
injected clock; a caller cannot choose it.

Status derives expiry from the same injected clock. Complete requires the
verification ID, positive expected revision, a matching-source fact snapshot,
and 1..16 existing candidate references. It rejects missing, expired, or
source-mismatched candidates and any selection that uses synthetic-only
evidence for a real-source condition. Cancel requires the ID and positive
expected revision. Complete and cancel each use one compare-and-swap transaction
over `state=active AND revision=<expected>`. One concurrent terminal operation
may succeed; every loser receives the fixed stale or already-terminal result.

Complete accepts evidence rows, evaluates the matching snapshot, and persists
accepted ordinals plus the completed lifecycle/revision/timestamp atomically.
It reaches `verification_completed` only when the evaluation's primary state is
`first_trace_ready`; a non-ready or partial evaluation does not terminally
complete the verification. A valid non-ready complete attempt returns the
`evaluation_completed` result with the still-active verification; a partial
attempt returns the fully pinned `partial_fact_snapshot` projection with the
current unchanged active verification. Neither changes revision or accepted
evidence. Cancel persists no new accepted evidence.

Candidate selection is explicit and bounded. Latest trace, latest Session,
repository/workspace/cwd match, trace ID alone, and timestamp proximity are
forbidden. A later source-specific slice may present an explicit user-selected
candidate when the source cannot carry the opaque verification ID.

## Config CLI

The public commands are:

```text
config-cli doctor evaluate --input <file> [--json]
config-cli doctor verification start --database <file>
  --source-surface <value> [--source-adapter <value>]
  --expires-at <RFC3339> [--json]
config-cli doctor verification status --database <file>
  --verification-id <UUIDv7> [--json]
config-cli doctor verification complete --database <file>
  --verification-id <UUIDv7> --expected-revision <positive-int>
  --input <file> [--json]
config-cli doctor verification cancel --database <file>
  --verification-id <UUIDv7> --expected-revision <positive-int> [--json]
```

`evaluate --input` reads one `DoctorFactSnapshot`, including its bounded typed
`observations`. `complete --input` reads one object containing a
`fact_snapshot` whose `observations` array is empty and
`accepted_evidence_refs`; the verification service resolves the selected
persisted candidates and constructs trusted observations. The database
path is input only and never appears in output, stderr, logs, evidence, or
errors. Recognized Doctor syntax and input failures still produce a sanitized
`DoctorResult`; they never fall through to raw parser/help/exception output.

`--json` writes the canonical result as one JSON object on stdout. Without it,
stdout contains only the bounded human projection. A successful result writes
nothing to stderr; a non-success writes only its fixed code and one newline.

CLI exit categories are:

| Exit | Outcome |
| ---: | --- |
| 0 | `first_trace_ready`, `verification_started`, `verification_active`, `verification_completed`, or successful cancel |
| 2 | invalid arguments/input/schema |
| 3 | valid non-ready evaluation or `partial_fact_snapshot` |
| 4 | verification not-found/stale/expired/already-terminal/source/evidence conflict |
| 5 | `doctor_store_busy`, `doctor_store_unavailable`, or `internal_error` |

## Local Monitor HTTP

The routes are:

```text
POST /api/doctor/evaluations
POST /api/doctor/verifications
GET  /api/doctor/verifications/{verificationId}
POST /api/doctor/verifications/{verificationId}/complete
POST /api/doctor/verifications/{verificationId}/cancel
```

Evaluation accepts one `DoctorFactSnapshot` with bounded typed observations.
Start accepts
`source_surface`, optional `source_adapter`, and `expires_at`. Complete accepts
`expected_revision`, a `fact_snapshot` with an empty `observations` array, and
`accepted_evidence_refs`; the service supplies trusted observations from the
resolved persisted candidates. Cancel
accepts only `expected_revision`. Requests are JSON, use the 64 KiB bound, and
reject unsupported media types or malformed/duplicate/unknown fields.

All routes retain loopback bind and Host-header protection. State-changing
routes require same-origin and the existing `x-monitor-csrf: local-monitor`
header. Every response, including errors, has `Cache-Control: no-store` and
contains sanitized metadata only. Doctor routes return no raw telemetry,
prompt/response/tool body, source fragment, PII, credential, authorization
header, absolute/local path, database path, rejected body, or exception detail.

HTTP mapping is fixed:

| Status | Outcome |
| ---: | --- |
| 200 | valid evaluation, status, complete, or cancel result |
| 201 | verification start |
| 400 | malformed/invalid input, invalid arguments, unsupported schema/media type |
| 404 | verification not found |
| 409 | stale revision, already completed/cancelled, or expected-source/evidence conflict |
| 410 | expired verification or expired evidence |
| 422 | `partial_fact_snapshot` |
| 503 | `doctor_store_busy` or `doctor_store_unavailable` |
| 500 | sanitized internal failure |

A valid non-ready Doctor diagnosis is still HTTP 200. HTTP status expresses
transport/application processing, not the twenty-state severity.

## SQLite Doctor v1

Doctor persistence uses the existing Local Monitor database with a separate
`schema_version(component='doctor', version=1)` entry. It does not change the
`monitor` or `session` component versions.

`doctor_verifications` contains exactly the lifecycle data needed by the shared
verification projection:

| Column | Contract |
| --- | --- |
| `verification_id` | TEXT primary key; canonical lowercase UUIDv7 |
| `expected_source_surface` | TEXT NOT NULL; source token grammar |
| `expected_source_adapter` | TEXT NULL; adapter token grammar |
| `state` | TEXT NOT NULL; `active|completed|cancelled` |
| `revision` | INTEGER NOT NULL and positive |
| `started_at` | TEXT NOT NULL; canonical UTC timestamp |
| `expires_at` | TEXT NOT NULL; canonical UTC timestamp and bounded window |
| `completed_at` | TEXT NULL; set only for completed |
| `cancelled_at` | TEXT NULL; set only for cancelled |

`doctor_verification_evidence` stores candidates and accepted selection:

| Column | Contract |
| --- | --- |
| `candidate_id` | TEXT primary key; canonical lowercase UUIDv7 |
| `verification_id` | TEXT NOT NULL; foreign key to the lifecycle row |
| `source_surface` | TEXT NOT NULL; source token grammar |
| `source_adapter` | TEXT NULL; adapter token grammar |
| `evidence_class` | TEXT NOT NULL; `real_source|synthetic_probe` |
| `evidence_kind` | TEXT NOT NULL; shared five-value evidence-kind enum |
| `evidence_ref` | TEXT NOT NULL; unique per verification and bounded opaque reference |
| `observed_at` | TEXT NOT NULL; canonical UTC timestamp |
| `expires_at` | TEXT NOT NULL; canonical UTC timestamp |
| `accepted` | INTEGER NOT NULL; `0|1` |
| `accepted_ordinal` | INTEGER NULL; contiguous `0..N-1` only when accepted |

CHECK/unique/foreign-key constraints enforce the closed vocabularies,
timestamp/state nullability, evidence uniqueness, accepted/ordinal pairing,
and maximum cardinality in application transactions. No raw payload, prompt,
response, tool body, PII, path, credential, exception, repository/workspace
identifier, or guessed latest-trace pointer is stored.

Doctor v1 creation and every terminal transition are transactions. Candidate
acceptance and the lifecycle compare-and-swap commit together. A migration or
transition checkpoint seam permits deterministic fault injection; any failure
leaves the exact prior schema version and rows after close/reopen. Startup is
idempotent. A Doctor schema newer than version 1 is rejected as
`doctor_store_unavailable` without downgrade or compatibility fallback.

Migration evidence copies each committed historical monitor v1-v4 fixture,
runs the current monitor v5 migration, then creates Doctor v1. After close and
reopen it proves fixture-manifest provenance/SHA-256, historical sentinels,
complete monitor v5 and Doctor v1 schema semantics, byte-stable Doctor rows,
second-start idempotence, and `PRAGMA integrity_check`. Injected Doctor
migration failure must restore the exact post-monitor/pre-Doctor schema and
rows.

Doctor schema initialization is isolated. Failure disables only verification
store operations, which return `doctor_store_unavailable`. It does not fail Local
Monitor host construction, change D051 readiness, stop ingestion/projection,
or disable the stateless evaluation route.

## Non-scope and handoff

Issue #102 does not implement source-specific adapters, a live first-trace
workflow, proxy DTO, Razor, JavaScript, Canvas, or UI. Issues #103 and #104 map
GitHub Copilot and Claude Code observations to the twelve fact families and
shared candidates without extending the v1 enums. Issue #105 may proxy and
display `doctor.v1` without redefining, falling back, or reinterpreting it.

Tests use synthetic fixtures, injected clocks, barriers, transaction/checkpoint
seams, and controlled SQLite locks. They must not coordinate through sleep,
polling, or retry loops.

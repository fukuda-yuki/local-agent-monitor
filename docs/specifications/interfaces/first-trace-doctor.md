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

## Cross-surface closeout contract

Issue #105 integrates, but does not replace, the frozen Doctor domain and the
source-specific evidence adapters. The fixed public source registry is:

| `source_id` / `source_surface` / CLI `--adapter` | `display_label` | Expected Doctor adapter | Allowed `--interaction` | Setup ownership |
| --- | --- | --- | --- | --- |
| `github-copilot-vscode` | `GitHub Copilot in VS Code` | `github-copilot-doctor` | `vscode-chat` | managed |
| `github-copilot-cli` | `GitHub Copilot CLI` | `github-copilot-doctor` | `cli` | managed on Windows |
| `github-copilot-app-sdk` | `GitHub Copilot App/SDK` | `github-copilot-doctor` | `app-sdk` | caller-managed |
| `claude-code` | `Claude Code` | `claude-code-otel` | `interactive-cli|print|agent-sdk` | managed CLI; caller-managed Agent SDK |

The registry has ordinal unique IDs and the stable order above. Detection
reports `detected|not_detected|unavailable`; it never removes an item, treats
unknown as absent, or selects a source automatically when more than one managed
source is detected. Caller-managed entries remain visible and are never
represented as managed setup success.

Detection is a read-only presence check and never invokes setup planning,
persists a plan, mutates configuration, or uses Doctor facts as an absence
signal. `github-copilot-vscode` runs the existing Stable and Insiders
`--list-extensions --show-versions` observations: an exact
`github.copilot-chat` entry is `detected`; successful observations with no such
entry, or an executable explicitly reported missing, are `not_detected`; and an
exception, failure, or timeout is `unavailable` unless the other channel
positively detects the extension. `github-copilot-cli` and `claude-code` use
their existing `version` and `--version` process observations respectively:
a completed invocation is `detected` even when its version is unsupported, an
explicit executable-not-found result is `not_detected`, and every ambiguous
failure is `unavailable`. `github-copilot-app-sdk` is caller-managed and always
reports `unavailable` because v1 has no bounded authoritative local detector.
Unknown or unavailable Doctor facts never become `not_detected`.

The common `first-trace begin` command accepts these source IDs through
`--adapter`. A GitHub source accepts only its one interaction value, which is
also the default when omitted. Claude retains its existing optional interaction
behavior. `status|complete|cancel` derive the registered adapter from the exact
stored verification source and do not accept `--adapter`. JSON output is the existing
`FirstTraceEnvelope`; human output is projected from that same envelope. The
Local Monitor proxy and UI consume the same application operation and preserve
the embedded `DoctorResult` byte-for-byte after canonical JSON serialization.
They do not add a state, change precedence, synthesize a missing fact, or
substitute `evaluation_preview` for `doctor.evaluation`.

### Local Monitor UI proxy

The additive proxy is versioned under `/api/doctor/ui/v1`. JSON property names
and request bodies are closed; unknown or duplicate properties are invalid:

- `GET /api/doctor/ui/v1/sources` returns the complete ordered registry and its
  bounded detection state;
- `POST /api/doctor/ui/v1/verifications` accepts required `source_id` and
  optional `interaction` and `expires_at`, and begins one exact selected source;
- `GET /api/doctor/ui/v1/verifications/{verificationId}` refreshes and returns
  that exact verification at its current persisted revision;
- `POST /api/doctor/ui/v1/verifications/{verificationId}/complete` completes
  a body containing required positive `expected_revision` and required ordered
  distinct `accepted_evidence_refs` (0..16); and
- `POST /api/doctor/ui/v1/verifications/{verificationId}/cancel` cancels the
  exact required positive `expected_revision`.

The source response is exactly
`{schema_version:"doctor.ui.v1",sources:[...]}`. Each source has exactly
`source_id`, `display_label`,
`setup_ownership = managed|managed_windows|caller_managed|managed_cli_caller_managed_agent_sdk`,
and `detection_state = detected|not_detected|unavailable`. It contains no
envelope. Every verification-operation response is exactly
`{schema_version:"doctor.ui.v1",envelope:<FirstTraceEnvelope>,navigation_targets:[...]}`.
The envelope is present on success and failure; targets are empty unless the
returned result contains their evidence references.

Mutation routes require the existing loopback, valid Host, same-origin and
CSRF controls. Every route uses `Cache-Control: no-store`, the existing 64 KiB
body limit, canonical UUIDv7/timestamp/revision rules, fixed sanitized error
codes, and no redirect. `expires_at` has the existing 1..30 minute bounds and
defaults to exactly ten minutes from the store clock. `interaction` is 1..32
ASCII lower-case token characters and must match the registry row. A safe
status retry may repeat a GET. A client must
not blindly repeat a mutation after response loss; it first GETs the exact
verification and revision and then offers the one currently valid action.

An active result lists its bounded opaque candidates as explicit checkbox
choices. With one or more selected references the single primary action may
complete at the exact displayed revision; with none available it refreshes by
GET. A separate non-primary cancel action may cancel at that revision. After a
lost complete/cancel response the UI performs the exact status GET before it
offers another mutation and never repeats the POST automatically. Terminal
completed, cancelled, and expired results expose no lifecycle mutation. A
cancelled result may expose one safe exact status GET so the user can refresh
current setup facts after the external rollback; it retains the cancelled
verification history. An unresolved begin locks source selection and exposes
no begin retry because no exact verification identity is available.

HTTP status mapping is closed: successful source/status/complete/cancel is
`200`; a newly started verification is `201`; invalid JSON/arguments is `400`;
verification not found is `404`; stale revision, active-verification conflict,
not-ready completion, or required explicit selection is `409`; expired or
already terminal verification/evidence is `410`; store busy/unavailable is
`503`; and an internal/unmapped failure is `500`. The envelope retains the
canonical FirstTrace/Doctor code and is authoritative; HTTP status never turns
a failure into success.

The Doctor section lives in the existing `/diagnostics` page. D042 remains a
seven-screen, two-navigation-item information architecture. The section shows
source selection, empty/detected state, the returned current state, severity,
source, evidence references, next action, retryability and active/expired/
cancelled/completed lifecycle. It exposes at most one primary action. API
failure provides an explicit GET retry action, restores focus to the result
heading, and announces state changes through a labelled live region. All
controls are keyboard reachable in DOM order and carry explicit labels.

### Exact evidence navigation

Navigation is a separate sanitized projection; it never changes or parses the
opaque Doctor evidence reference. Each target contains exactly
`evidence_ref`, `target_kind = trace|session|source_diagnostic`, `target_id`,
and `href`. The reference must occur in the returned result; the identity must
already be persisted and exact; and the server generates one fixed relative
href:

| Target kind | Fixed relative href |
| --- | --- |
| `trace` | `/traces/{traceId}` |
| `session` | `/diagnostics?session_id={sessionId}#doctor-session` |
| `source_diagnostic` | `/diagnostics?observation_id={observationId}#source-diagnostics` |

The source evidence adapter persists this linkage when it accepts the
candidate. The additive `first_trace_navigation` schema component v1 owns
`first_trace_evidence_navigation`, keyed by
`(verification_id,evidence_ref,target_kind,target_id)`, with a foreign key to
the exact Doctor evidence row and cascade lifetime. One evidence reference may
have at most one target of each kind; duplicate identical writes are
idempotent and a conflicting target is rejected. `trace` IDs are 32 lowercase
hex characters, `session` IDs are canonical lowercase UUIDv7, and
`source_diagnostic` IDs are the existing 1..128-character safe opaque
`observation_id`. Only source candidate producers write this table; the UI
proxy is read-only and lists targets only for evidence references present in
the authoritative returned `doctor.evaluation` states. Candidate-only and
`evaluation_preview` references do not authorize a target. Missing linkage
produces no target and remains missing evidence; no
caller may recover it by hash reversal, latest row, repository, workspace,
cwd, process, trace ID alone, or timestamp proximity. The proxy validates kind,
identity, and same-origin relative href. Query values are encoded and the UI
creates links with DOM properties and inert text only. The diagnostics page may
show a bounded sanitized Session summary and exact source observation for these
query parameters. It adds no Session page, raw route, raw payload fetch, or
prompt/response field.

`GET /api/doctor/ui/v1/sessions/{sessionId}` and
`GET /api/doctor/ui/v1/source-diagnostics/{observationId}` are the two
sanitized exact-read endpoints used by those diagnostics sections. An exact
Session/source observation read that does not exist or no longer has
retention-authorized sanitized metadata renders a fixed `evidence_not_found`
empty state and HTTP `404` from its additive exact read endpoint. It does not
fall back to a list row. A malformed query identity is `400`. The source-
diagnostic exact projection contains the existing sanitized DTO fields plus its
opaque `observation_id`; the Session projection contains exactly the bounded
Doctor navigation summary fields
`session_id,status,completeness,started_at,ended_at,last_seen_at`. It is not the
broader Session workspace DTO and exposes no native IDs, runs, events,
evaluation, raw payload, prompt, response, or event content.

### Release ZIP and rollback handoff

The Windows x64 Release ZIP includes `first-trace.ps1` beside the existing
installed scripts and self-contained `config-cli.exe`. The wrapper forwards
only `begin|status|complete|cancel` arguments, rejects a caller-supplied
`--database`, injects the installed current-user runtime database
`%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\raw-store.db`, and
passes stdout, stderr, and exit code through unchanged. It performs no source
build, restore, environment mutation, setup apply, source restart, telemetry
generation, or retry. Missing executable/database/runtime root is a fixed
fail-closed error with no resolved local path in output.

The setup completion handoff remains the existing sanitized
`run_first_trace_doctor` next action plus the `/diagnostics` source selector;
there is no setup Razor screen. A release journey performs explicit first-trace
cancel when a verification is active, then source setup rollback, then refresh.
Rollback does not erase Doctor history or invent a new Doctor state: the
refreshed envelope projects current setup facts and the cancelled lifecycle.
Uninstall runs only after cancellation/rollback in the supported journey,
removes only tool-owned configuration/runtime installation artifacts under the
existing uninstall contract, and never converts retained evidence into a pass.

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

For Copilot CLI OTLP, the exact selected raw record is source-owned only when
its resource `service.name` is the documented canonical `github-copilot` and
at least one scope containing a span is named the documented canonical
`github.copilot`. The #67 setup adapter does not set `OTEL_SERVICE_NAME` or
`COPILOT_OTEL_SOURCE_NAME`; a different or missing value is therefore unknown
provenance and produces no candidate. Copilot CLI does not require or infer a
global `client.kind` resource attribute.

Setup detect/plan/apply/no-op/rollback/status may populate only directly
observed install/version, receiver/port, effective configuration, reachability,
protocol/signal, and restart/new-process facts. Setup success and synthetic
probes produce no real-source candidate. Accepted ingest, raw persistence,
projection, exact binding, and completeness/content remain independent runtime
evidence gates. A selected raw record with no successful projection row does
not distinguish `not_started`, `pending`, or `failed`; all remain unknown until
an exact per-record projection disposition exists.

Managed VS Code and CLI do not require a Session binding after exact accepted
raw provenance and a completed per-record projection have been established.
For those two surfaces the adapter reports `not_required` / `not_applicable`
binding and may persist a raw-derived `completeness_content` candidate with
known unbound completeness. Caller-managed App/SDK remains binding-required
and produces no completeness candidate without exact Session identity.

During completion, each explicitly selected opaque evidence reference must
resolve through its persisted `source_diagnostic` navigation target to one
`source_schema_observations` row and its exact `raw_record_id`. All selected
references must resolve to the same raw record before runtime fact families are
merged. A missing, conflicting, or mixed-record linkage fails closed; hashes,
latest records, trace IDs alone, and timestamp proximity are not resolution
authority.

When a surface cannot safely carry the opaque verification ID, candidate
selection is an explicit raw-record or native-Session selection followed by
exact source and identity validation. Latest trace/Session, repository,
workspace, cwd, process identity, trace ID alone, and timestamp proximity are
never selection or binding evidence. The shared Config CLI JSON/human result
and the five Local Monitor HTTP routes remain unchanged; Issue #105 owns any
common proxy or UI.

#### GitHub Copilot setup-to-Doctor orchestration

Issue #103 adds an internal GitHub Copilot-only orchestrator; it adds no Config
CLI command, HTTP route, proxy, or UI contract. Its distinct setup evaluation,
start, status, selected-evidence continuation, and rollback-cancel operations
prevent nullable mode combinations. Setup evaluation receives one exact
successful setup result and has no database input. Start receives one
caller-selected guided-setup target, the Doctor database path, and an explicit
expiry. Status receives the exact verification ID and revision.
Status also receives the newly obtained matching setup-status result and its
singular selected target; it does not rediscover either value.
Selected-evidence continuation receives the exact successful `apply` or
`no_changes` setup result used for its static facts, requires an explicit
raw-record ID, and accepts the exact native Session identity; App/SDK requires
that native identity. Rollback-cancel receives the exact successful rollback
result, its singular selected target, verification ID, and revision; it rejects
failed rollback results, other setup commands/codes, and target mismatches
before lifecycle mutation. None of those values is derived from a latest
record, repository, workspace, cwd, process, trace ID alone, or timestamp
proximity.

The lifecycle mapping is closed:

- `plan` never starts a verification;
- successful `apply` (`apply_succeeded` or `no_changes`) may start one for the
  singular selected target;
- a newly obtained `status` may start only from exactly one matching change-set
  in `applied` or `no_changes` state: the singular CLI target and all one-or-two
  VS Code targets are `current` with the `desired` reference; caller-managed
  App/SDK has one unchanged guidance target with `not_applicable` current state,
  the `none` reference, and a `no_changes` change-set;
- an exact verification ID reuses `status`; it does not extend expiry or add a
  verification row; and
- successful `rollback` may only cancel the caller-supplied verification ID at
  the caller-supplied revision.

Starting remains allowed while restart/new-process is `required`, so the fixed
`agent_restart_required` diagnosis and `restart_source_process` action can be
shown before the bounded source interaction handoff. Setup success itself does
not clear that requirement and produces no evidence candidate. The orchestrator
keeps it `required` until the caller explicitly selects matching real-source
evidence within the verification window; only then may the merged runtime
observation set restart/new-process to `not_required`. Synthetic/probe evidence
never does so.

For each common-adapter request, static facts use exactly one newly dispatched
`setup status`. Managed target configuration must be current at the desired
reference and its recorded endpoint must equal the normalized request endpoint.
Current source detection and the existing source-specific version thresholds
(VS Code 1.128 or later; Copilot CLI 1.0.4 or later) are authoritative for
source availability/version, and the existing bounded loopback health probe is
authoritative for monitor process, receiver/port ownership, and reachability.
Historical detected versions and plan/apply projections are not authority;
unavailable or malformed current observations remain unknown.

For evaluation or completion the orchestrator maps setup families 1--5 and 12
through the GitHub Copilot setup mapper and replaces only runtime families
6--11 with the exact evidence-adapter snapshot. It preserves the frozen
`DoctorResult` contract and source/verification identity. It cannot complete a
verification before exact persisted candidate references exist, and it emits
no raw/native identity, database path, content, PII, credential, or source path
in results or evidence references.

Each operation returns an unmodified valid `DoctorResult` projection. In
particular, exact status reuse returns `verification_active` with
`evaluation=null`; it never attaches a setup evaluation to a lifecycle result.
Setup-only evaluation, when needed, is a separate `evaluation_completed` or
`partial_fact_snapshot` result with `verification=null`. Only genuine Complete
after persisted candidate resolution may return a merged evaluation and
verification together. Wrong revisions return `verification_stale` without
creating candidates or changing lifecycle state.

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

# Issue #102 Doctor Core Design

**Date:** 2026-07-16

**Work item:** Issue #102 — Doctor core: deterministic state engine and verification contract

**Branch:** `codex/issue-102-doctor-core`

**Base:** `8940b34f4e031b894705682dc50c079a9ed5c180`

## Purpose

Issue #102 introduces the source-independent Doctor contract that later
GitHub Copilot and Claude Code first-trace slices consume. It owns the fixed
state vocabulary, fact input, deterministic precedence, verification-window
lifecycle, shared CLI/HTTP read model, and local persistence required to carry
an explicit real-source verification across a Local Monitor restart.

It does not implement source-specific fact collection, guided setup adapters,
live first-trace workflows, the final proxy/UI, or cross-surface release
closeout. Those remain in Issues #68, #103, #104, and #105.

Current product behavior and public interfaces must be promoted into
`docs/requirements.md`, `docs/spec.md`, and a new canonical interface
specification before implementation. This design record is not a substitute
for those sources of truth.

## Considered Approaches

### 1. Dedicated shared Doctor domain project — selected

Create a dependency-free `CopilotAgentObservability.Doctor` project referenced
by Config CLI, Local Monitor, and their tests. It owns domain types, validation,
the deterministic evaluator, the canonical state catalog, result projection,
and shared serialization. SQLite persistence stays in
`CopilotAgentObservability.Persistence.Sqlite`.

This keeps product diagnostics separate from telemetry acquisition and gives
CLI and HTTP one executable domain contract.

### 2. Put Doctor types in the Telemetry project — rejected

This minimizes project-file changes but makes product diagnosis, verification
lifecycle, and CLI/HTTP projection part of telemetry acquisition. The coupling
would obscure ownership and make later source-specific integration harder to
review.

### 3. Make Local Monitor the only Doctor owner — rejected

The Config CLI would have to duplicate evaluation or call the monitor over
HTTP. An HTTP-only CLI cannot diagnose `monitor_not_running`, and duplicated
evaluation would violate the shared-result requirement.

## Component Boundaries

### Shared Doctor domain

The shared project owns:

- `DoctorFactSnapshot` and its explicit known/unknown fact families;
- the twenty fixed Doctor state codes and their catalog metadata;
- `DoctorEvaluator` and the canonical precedence table;
- `DoctorResult`, ordered state entries, fixed error results, validation, and
  JSON serialization;
- verification lifecycle domain types and transition validation;
- the human-readable summary projector;
- interfaces for a clock and verification store, but no SQLite or ASP.NET
  implementation.

The evaluator is pure. It does not read a database, environment, process,
network endpoint, or current clock. `observed_at`, source identity, evidence
references, and verification identity all come from validated input.

### SQLite persistence

`CopilotAgentObservability.Persistence.Sqlite` implements the verification
store in the existing Local Monitor database. A separate `doctor` entry in
`schema_version` starts at version 1. The component owns only Doctor lifecycle
and evidence tables; it does not alter the existing `monitor` or `session`
component versions.

### Config CLI

Config CLI parses Doctor commands, invokes the shared evaluator or verification
service, and emits either shared JSON bytes or a human summary projected from
the same `DoctorResult`. It does not reimplement state selection or rewrite the
result DTO.

### Local Monitor

Local Monitor maps HTTP requests to the same evaluator and verification
service. Route handlers validate transport concerns and map fixed domain/store
errors to sanitized HTTP responses. They do not select Doctor states or invent
missing facts.

## Producer/Consumer Contract Table

| Surface | Producer | Contract | Consumer | Issue #102 executable evidence |
| --- | --- | --- | --- | --- |
| Backend/domain | source-independent fixture or later source fact mapper | `DoctorFactSnapshot` to `DoctorResult` | `DoctorEvaluator` and verification service | real production DTO fixture passes through the production evaluator and serializer |
| Config CLI JSON | shared evaluator/service | canonical `doctor.v1` JSON | terminal automation and later setup integration | CLI invocation deserializes to the same result as the direct domain call |
| Config CLI human | the same `DoctorResult` | fixed bounded summary | local user | projection test proves it consumes the domain result and does not reevaluate facts |
| HTTP | shared evaluator/service and SQLite verification store | canonical Doctor request/result DTOs | later Local Monitor UI and source-specific slices | HTTP response deserializes to the same result as domain and CLI JSON |
| Proxy | none | no proxy DTO or transformation | Issue #105 | absence is recorded as N/A; no proxy facsimile is accepted |
| Local Monitor UI | none | later consumes the HTTP DTO without enum or fallback reinterpretation | Issue #105 | no Razor/page/client implementation is added in Issue #102 |

The first implementation test is the cross-surface executable contract. It is
written before isolated evaluator features so the backend, CLI, and HTTP paths
cannot drift into parallel DTOs.

## Fact Snapshot

The snapshot contains independent, nullable fact groups for:

1. install and source version;
2. process, receiver bind, and port ownership;
3. source effective configuration;
4. endpoint reachability;
5. protocol and signal compatibility;
6. source version and schema diagnostics;
7. last ingest acceptance or rejection;
8. raw persistence;
9. projection cursor, backlog, and error;
10. exact Session binding;
11. completeness and content state; and
12. restart or new-process requirement.

Every group distinguishes `unknown` from negative and positive observations.
Unknown input is never converted to `false`, zero, supported, or successful.
The snapshot has a schema version, source surface, optional expected adapter,
an input `observed_at`, ordered sanitized opaque evidence references, and an
optional verification ID.

Validation rejects malformed schema versions, non-canonical enums, unbounded
text, raw-bearing evidence, invalid timestamps, inconsistent fact groups, and
a verification ID without the required expected-source context.

## Deterministic Evaluation

The catalog contains exactly the twenty codes required by Issue #102:

1. `monitor_not_installed`
2. `monitor_not_running`
3. `receiver_not_bound`
4. `port_owned_by_foreign_process`
5. `endpoint_mismatch`
6. `protocol_mismatch`
7. `signal_disabled`
8. `unsupported_source_version`
9. `feature_unavailable`
10. `agent_restart_required`
11. `endpoint_unreachable`
12. `payload_rejected`
13. `raw_persisted_projection_pending`
14. `projection_failed`
15. `session_unbound`
16. `content_capture_disabled`
17. `sanitized_only_raw_unavailable`
18. `schema_drift_detected`
19. `ready_no_real_trace`
20. `first_trace_ready`

Each catalog entry fixes severity, retryability, next action, and precedence.
Evaluation emits every applicable state in canonical precedence order and
identifies the first entry as the primary state. Earlier failures are not
removed by later success facts. A result contains:

- `schema_version = doctor.v1`;
- success/error code;
- source surface;
- primary state and ordered states;
- per-state severity, evidence references, ordered reason codes, fixed next
  action, retryability, observed time, and optional verification ID; and
- missing fact families when the snapshot is partial.

If unknown facts prevent a supported conclusion, evaluation returns the fixed
`partial_fact_snapshot` error rather than a fabricated Doctor state. Transport
or validation failures use fixed error codes outside the twenty-state catalog.
The evaluator never calls a clock, so byte-equivalent valid input produces
byte-equivalent ordered JSON.

`first_trace_ready` requires matching expected-source evidence, accepted
ingest, raw persistence, completed projection, exact binding when required by
the surface contract, and honest completeness/content facts. Synthetic probe
evidence is typed separately and can prove receiver, persistence, or projection
health only; it never satisfies a real-source condition or enters Session
candidate references.

## Verification Lifecycle

The persisted lifecycle states are `active`, `completed`, and `cancelled`.
`expired` is a deterministic read/transition outcome derived from `expires_at`
and an injected `TimeProvider`; no background timer is required.

A start operation creates:

- a server-generated opaque UUIDv7 `verification_id`;
- expected source surface and optional adapter;
- explicit `started_at` and bounded `expires_at`;
- revision 1; and
- no accepted evidence.

Complete requires the verification ID, expected revision, matching expected
source, and bounded opaque accepted event/trace/Session references. Cancel
requires the verification ID and expected revision. Complete or cancel uses a
single compare-and-swap transaction. Exactly one concurrent terminal
transition can succeed; a stale revision, already-terminal state, source
mismatch, or expiry returns its fixed sanitized result.

Latest trace, repository, workspace, working directory, trace ID alone, and
timestamp proximity never select a verification candidate. A later
source-specific slice may submit explicit user selection when the source cannot
carry the opaque ID.

## Public Interface

### Config CLI

The command family is:

```text
doctor evaluate --input <file> [--json]
doctor verification start --database <file> --source-surface <value>
  [--source-adapter <value>] --expires-at <RFC3339> [--json]
doctor verification status --database <file> --verification-id <UUID> [--json]
doctor verification complete --database <file> --verification-id <UUID>
  --expected-revision <positive-int> --input <file> [--json]
doctor verification cancel --database <file> --verification-id <UUID>
  --expected-revision <positive-int> [--json]
```

The database path is an input only and never appears in output, errors, logs,
or evidence. JSON is the canonical machine-readable projection. Without
`--json`, the command writes the bounded human projection. Invalid recognized
commands still return a fixed sanitized Doctor result rather than raw parser,
filesystem, SQLite, or JSON exception text.

### Local Monitor HTTP

The routes are:

```text
POST /api/doctor/evaluations
POST /api/doctor/verifications
GET  /api/doctor/verifications/{verificationId}
POST /api/doctor/verifications/{verificationId}/complete
POST /api/doctor/verifications/{verificationId}/cancel
```

All routes retain existing loopback and Host-header protection. State-changing
routes require the existing same-origin/CSRF policy used by Local Monitor
writes. Requests and responses are bounded JSON. Responses are sanitized
metadata with `Cache-Control: no-store`. No route returns raw telemetry,
prompts, responses, tool bodies, PII, credentials, authorization headers,
absolute paths, or raw exception text.

`GET /health/ready` remains the D051 process-readiness interface. Doctor is a
separate product diagnostic: its state, store availability, or source
compatibility does not alter D051 thresholds, status mapping, or response body.

## Fixed Failure Model

The canonical specification fixes transport-independent result codes for:

- invalid arguments or malformed input;
- unsupported Doctor schema version;
- partial fact snapshot;
- verification not found, stale, expired, cancelled, or completed;
- expected-source mismatch;
- missing or expired evidence reference;
- store busy or unavailable; and
- internal failure.

HTTP maps malformed input to 400, missing verification to 404, stale/terminal
conflicts to 409, expired verification to 410, and busy/unavailable storage to
503. Unexpected failures return the existing sanitized 500 shape. Config CLI
maps the same domain codes to fixed exit categories without exposing exception
text.

## Persistence And Migration

Doctor persistence adds `doctor_verifications` and
`doctor_verification_evidence`, plus `schema_version(component='doctor',
version=1)`. The lifecycle row contains only validated source identifiers,
timestamps, state, and revision. Evidence rows contain a fixed evidence kind,
ordinal, and bounded opaque reference.

Schema creation and each terminal transition are transactions. Evidence rows
and the lifecycle state commit together. A migration or transition checkpoint
seam permits deterministic fault injection; failure must leave the exact prior
schema version and rows after close/reopen.

Migration evidence copies each committed historical monitor v1-v4 fixture,
runs the current monitor v5 migration, creates Doctor v1, closes the stores,
reopens them, and proves:

- the fixture manifest provenance and SHA-256 remain valid;
- all historical monitor sentinels survive;
- the complete monitor v5 and Doctor v1 schemas are present;
- Doctor rows survive restart byte-for-byte;
- a second startup is idempotent;
- `PRAGMA integrity_check` succeeds; and
- an injected Doctor migration failure restores the exact pre-Doctor schema
  and rows.

## Test Strategy

Implementation follows test-driven development in this order:

1. RED cross-surface production DTO fixture through direct domain, Config CLI
   JSON/human, and Local Monitor HTTP;
2. parameterized twenty-state decision table and precedence conflicts;
3. missing/null facts, ordering, byte determinism, and validation bounds;
4. synthetic/real separation and no latest/repository/time guessing;
5. verification start/status/complete/cancel, expiry, source mismatch, and stale
   revision;
6. SQLite transaction rollback and concurrent complete/cancel using barriers,
   controllable checkpoints, and transactions only;
7. historical fixture migration, injected rollback, close/reopen, and
   idempotent restart;
8. raw/secret/PII/path negative tests across domain, CLI, HTTP, persistence,
   logs, and errors;
9. exact D051 readiness response/status/threshold regression; and
10. proxy/UI absence and Issue #103/#104 handoff compile/shape tests.

No test coordinates with `Thread.Sleep`, polling, or retry loops. `TimeProvider`
controls expiry. Barriers and transaction/checkpoint seams control stale-state
and concurrency windows.

Focused tests run after each task. Before completion, the pinned repository
validation is:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

## Delegation And Review

The implementation plan divides work into small, ownership-safe tasks. Every
Sub-Agent instruction includes purpose, scope, non-scope, constraints,
completion criteria, validation commands, worktree/branch, and report target.
Worktree, branch, HEAD, and status are checked before each delegation and
before integration.

Implementers do not review their own task. Fresh reviewers inspect spec
compliance and test quality after each bounded implementation. Final independent
reviews cover:

- security and data leakage;
- SQLite atomicity and migration rollback;
- concurrency, stale revision, and expiry behavior;
- backend/CLI/HTTP contract identity;
- D051 non-regression; and
- unverified interfaces handed to Issues #103, #104, and #105.

The root orchestrator compares reports with the requirements, diff, and actual
test output. A Sub-Agent report alone never establishes completion.

## Durable Evidence

`docs/sprints/issue-102-doctor-core/ledger.md` records task state, commit range,
focused and full validation, review verdicts, unresolved issues, and unverified
Issue interfaces throughout implementation. It is historical evidence only;
all current behavior is promoted to canonical specifications.

Feature-branch completion and integration into `main` are distinct outcomes.
This work item may report the former after verified local commits. It does not
claim the latter without observing the actual main branch integration.

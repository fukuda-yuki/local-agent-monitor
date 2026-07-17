# First-Trace Orchestration Interface (`first-trace`) and Claude Code Mapping

Status: normative for Issue #104. This interface composes the frozen
`setup.v1` contract (`configuration-setup.md`) and the frozen `doctor.v1` /
`doctor.facts.v1` contract (`first-trace-doctor.md`). It does not add, remove,
or reinterpret any Doctor state, reason code, next action, evidence class,
evidence kind, or fact family, and it does not add any `setup.v1` code beyond
activating the already-reserved `run_first_trace_doctor` next action.

## Purpose

Provide one deterministic setup-to-verification flow: after an explicit
guided-setup apply, open a `doctor.v1` real-source verification window, guide a
bounded real interaction on the source, track the expected source's records
through ingest, raw persistence, projection, exact Session binding, and
completeness/content, and report `first_trace_ready` or a specific failure or
capability state with next actions.

The CLI syntax, result envelope, and application boundary are source-neutral.
Version 1 registers exactly one source adapter: `claude-code`. Claude-specific
behavior is confined to the `claude-code` first-trace adapter and the Claude
candidate observer described below.

## CLI surface

Recognized only when token 0 is `first-trace` and token 1 is one of the exact
verbs below. All verbs accept `--json`. Unknown or duplicate arguments are
`invalid_arguments`.

```text
first-trace begin  --database <path> --adapter claude-code [--interaction <interactive-cli|print|agent-sdk>] [--expires-at <iso-8601-utc>] [--json]
first-trace status   --database <path> --verification-id <uuid-v7> [--json]
first-trace complete --database <path> --verification-id <uuid-v7> --expected-revision <n> [--evidence <ref>]... [--json]
first-trace cancel   --database <path> --verification-id <uuid-v7> --expected-revision <n> [--json]
```

- `--database` names the shared monitor SQLite database and has exactly the
  semantics of the existing `doctor verification` verbs' `--database`
  argument. No other path discovery exists.
- `--adapter` selects the registered first-trace source adapter. v1 accepts
  only `claude-code`; anything else is `invalid_arguments`.
- `--interaction` selects which bounded-interaction guidance variant is
  rendered. When omitted, guidance for all three variants is rendered. It does
  not change verification identity: all three variants share source surface
  `claude-code` and expected source adapter `claude-code-otel`.
- `--expires-at` is passed through to Doctor verification start unchanged and
  is bound by the Doctor contract's window rules. When omitted, the window is
  exactly 10 minutes from the Doctor store clock reading taken at start.
- `status`, `complete`, and `cancel` derive the adapter from the stored
  verification's expected source; they do not take `--adapter`.

`first-trace` never mutates setup state, source configuration, or
caller-owned Agent SDK configuration. `begin` does not run `setup plan` or
`setup apply`; it verifies the already-applied effective state read-only.
`DoctorCli` verbs remain available and unchanged; `first-trace` uses the same
Doctor application service and store.

## Result envelope: `first_trace.v1`

Every command writes exactly one JSON object to stdout (with `--json`) or a
bounded human projection of the same data. Failure additionally writes the
envelope code to stderr, mirroring the `setup.v1` and `doctor.v1` conventions.

```json
{
  "contract_version": "first_trace.v1",
  "command": "begin|status|complete|cancel",
  "success": true,
  "code": "<envelope code>",
  "adapter": "claude-code",
  "source_surface": "claude-code",
  "verification_id": "<uuid-v7> | null",
  "doctor": { "...": "verbatim doctor.v1 DoctorResult, or null" },
  "evaluation_preview": { "...": "verbatim doctor.v1 DoctorResult, or null" },
  "guidance": [
    { "interaction": "common|interactive-cli|print|agent-sdk", "text": "...", "command": "... | null" }
  ],
  "candidates": [
    {
      "candidate_id": "<uuid-v7>",
      "evidence_class": "real_source",
      "evidence_kind": "ingest|raw_persistence|projection|exact_session_binding|completeness_content",
      "source_surface": "claude-code",
      "source_adapter": "claude-code-otel",
      "evidence_ref": "...",
      "observed_at": "<iso-8601-utc>",
      "expires_at": "<iso-8601-utc>"
    }
  ],
  "truncated": false
}
```

Rules:

- `doctor` and `evaluation_preview` are unmodified `doctor.v1` `DoctorResult`
  values serialized by the existing Doctor serializer. The envelope never
  restates, renames, or re-derives Doctor states, evidence, reason codes, or
  next actions. Consumers read verification state and evaluation outcomes only
  from these embedded results.
- `evaluation_preview` is advisory display data (a stateless evaluation over
  the current fact snapshot plus candidate-derived observations). Completion
  authority remains exclusively the Doctor store's `complete` path, which
  resolves caller-selected candidate refs into trusted observations.
- `guidance` entries are bounded strings. `command` values are copyable,
  contain no secrets, credentials, tokens, absolute or user-derived paths, or
  PII, and never implicitly enable content capture.
- `candidates` lists persisted Doctor evidence candidates for the verification
  with sanitized opaque refs only.

### Envelope codes (closed)

| Code | Meaning | success | Exit |
| --- | --- | --- | --- |
| `first_trace_verification_started` | begin: facts healthy, Doctor verification started | true | 0 |
| `first_trace_blocked` | begin: evaluation produced blocking states; no verification started | false | 3 |
| `active_verification_exists` | begin: an active verification for the surface already exists; its id is returned | false | 3 |
| `first_trace_status_reported` | status: verification and candidates reported | true | 0 |
| `first_trace_completed` | complete: Doctor completion succeeded (`first_trace_ready`) | true | 0 |
| `first_trace_not_ready` | complete: the Doctor evaluated the input as valid but not `first_trace_ready` (embedded result: success `evaluation_completed` with a non-ready primary state; the verification stays active) | false | 3 |
| `first_trace_cancelled` | cancel: Doctor cancellation succeeded | true | 0 |
| `explicit_evidence_selection_required` | complete: more than one distinct candidate chain exists and `--evidence` was not given | false | 3 |
| `first_trace_doctor_failed` | the embedded `doctor.v1` result is a failure; it is authoritative | false | Doctor CLI mapping of the embedded code (2/3/4/5) |
| `invalid_arguments` | argument parsing/validation failed before any Doctor call | false | 2 |

`first_trace_doctor_failed` covers every Doctor failure passthrough, including
`partial_fact_snapshot` (unknown facts that prevent a decision), stale
revisions, expiry, evidence errors, and store busy/unavailable. The envelope
adds no parallel failure vocabulary.

## Verification identity

- Source surface: `claude-code` for interactive Claude Code CLI, `claude -p`,
  and caller-managed Agent SDK alike.
- Expected source adapter: always `claude-code-otel`. Hook traffic uses the
  session-store adapter `claude-code-hook` and never appears as a Doctor
  candidate adapter.
- The opaque Doctor verification ID is the only verification identity. Because
  the Claude producer cannot carry the verification ID in its payloads,
  candidates are constrained by expected source/adapter and the verification
  window, and completion requires explicit or deterministic single-chain
  selection (below). A Session or verification target is never selected by
  latest trace, repository, workspace, cwd, transcript path, process identity,
  or timestamp proximity.

## `begin` behavior

1. Collect the Claude fact snapshot read-only (mapping table below), using
   the fixed pre-window values for the pipeline families: `last_ingest.outcome
   = none`, `raw_persistence.outcome = not_persisted`, `projection.outcome =
   not_started`, `exact_session_binding = (not_required, not_applicable)`, and
   `completeness_and_content.completeness = unknown`. These are the same
   pre-trace values the Doctor contract's own `ready_no_real_trace` shape
   uses, so a healthy environment evaluates to `ready_no_real_trace`, never to
   `partial_fact_snapshot`, before any window exists. (Unknown completeness
   prevents a Doctor conclusion only when every other `first_trace_ready`
   requirement is already met; with `last_ingest = none` it never does.)
2. Evaluate with the frozen Doctor evaluator.
3. If any blocking state applies: return `first_trace_blocked` with the
   evaluation embedded in `doctor`. Guidance may add copyable setup commands
   (for example `setup plan --adapter claude-code --target cli`), but the
   authoritative states and next actions are the embedded Doctor ones.
4. Otherwise start a Doctor verification (surface `claude-code`, adapter
   `claude-code-otel`). The active-check and the start execute atomically in
   one store transaction (internal exclusive-start read/write, below): when an
   active, non-expired `claude-code` verification already exists — including
   one created by a concurrent `begin` — the command deterministically returns
   `active_verification_exists` with that verification's id and creates
   nothing. If more than one active verification exists (possible only through
   the non-exclusive Doctor `start` verb), the one with the smallest
   `(started_at, verification_id)` is returned. No retry, sleep, or
   best-effort pre-check is involved.
5. On a successful start return `first_trace_verification_started` with the
   Doctor start result embedded, restart/new-shell guidance, and the
   bounded-interaction guidance for the selected interaction variant(s).

Setup apply success, Hook receipt, and synthetic probes never satisfy
real-source requirements; this is inherited unchanged from the Doctor
contract's evaluator gates.

### Bounded-interaction guidance (fixed variants)

- `common`: a changed apply requires a new Claude process; environment-derived
  settings require a new shell so the process inherits them. One bounded test
  interaction is enough; close the source afterwards.
- `interactive-cli`: open a new terminal, run `claude` in the target project,
  send one short test prompt (for example "Reply with exactly: OK"), then exit.
- `print`: open a new terminal and run `claude -p "Reply with exactly: OK"`.
- `agent-sdk`: caller-managed only. Reuse the fixed #68 Agent SDK guidance
  (merge, do not replace, the process environment; flush telemetry before a
  short-lived process exits). `first-trace` never edits caller-owned
  configuration.

## `status` behavior

Return the Doctor verification-status result in `doctor`, the persisted
candidate list, and an `evaluation_preview` built from a fresh read-only fact
snapshot plus candidate-derived observations. Expired verifications surface
exactly as the Doctor contract defines them (read-time derived state).

## `complete` behavior

- With `--evidence` refs: pass them through to Doctor completion unchanged.
- Without `--evidence`: group current non-expired `real_source` candidates
  into chains keyed on the exactly-bound Session. Each `exact_session_binding`
  ref names one `(traceId, sessionGuid)` pair, and the observer emits such a
  ref only for a record of exactly that trace that exactly bound to exactly
  that Session — so the grouping below reproduces persisted observer
  decisions, never string proximity. A Session's chain is all binding refs
  naming its GUID, every `ingest`/`raw_persistence`/`projection` candidate
  whose trace ID appears in one of those binding refs, and the Session's
  `completeness_content` candidate. Candidates whose trace ID appears in no
  binding ref form one trace-keyed group per trace ID. Auto-selection happens
  only when exactly one group exists in total; then all of its refs are
  selected, ordered by ordinal comparison of the ref strings. If a trace ID is
  claimed by more than one Session's binding refs, or more than one group
  exists, return `explicit_evidence_selection_required` with the candidate
  list. Ordering or recency never selects a group.
- The completion fact snapshot carries no inline observations (the Doctor
  store constructs trusted observations from resolved candidates).

## Claude fact-family mapping (normative)

The fact collector re-runs the #68 detector components read-only and reads the
monitor's persisted pipeline state. It never mutates setup or source state and
never derives binding facts from the monitor's `SourceProjectionState`
projection. Families whose inputs are unavailable are emitted as `null`
(unknown family) or member `unknown`, exactly as `doctor.facts.v1` defines.

| Family | Deterministic rule (v1, `claude-code`) |
| --- | --- |
| `install_and_source_version` | `monitor_install`: `installed` when the bounded probe classifies monitor-live or the monitor database exists at the `--database` path; `not_installed` when neither; `unknown` on read error. `source_version`: `supported` iff the Claude version detector reports a supported version (>= 2.1.207); `unsupported` on a detected lower version; `unknown` when undetectable. `source_feature`: mirrors `source_version` in v1 (`available` iff supported). |
| `process_receiver_and_port` | The fact collector performs its own bounded liveness probe (`GET <canonical-origin>/health/live`) applying exactly the classification the setup contract pins for its endpoint probe: HTTP 200 whose complete body is the single-property JSON object `"status":"live"` -> monitor-live -> `running`/`bound`/`monitor`; socket connection refused or an equivalent positive no-listener result -> `not_running`/`not_bound`/`none`; every other outcome (redirect, non-200, oversized or malformed body, timeout, other transport failure) -> `not_running`/`not_bound`/`foreign`. The probe cannot run at all (no canonical origin resolvable) -> all `unknown`. |
| `source_effective_configuration` | `endpoint_alignment`: `match` iff the effective `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` equals the canonical monitor origin plus `/v1/traces`; otherwise (different value or absent) `mismatch`; `unknown` when a precedence source cannot be read or a precedence conflict leaves no single effective value (when the precedence order determines a winner, that winner is the effective value and no conflict exists). Effective values come from a first-trace read-only resolver that applies the setup contract's precedence (process environment > managed policy > local settings > project settings > user settings) and reports per-key effective value, absence, or conflict; the setup adapter's higher-precedence observer alone is not sufficient and setup components are reused read-only where they expose values. |
| `endpoint_reachability` | `reachable` iff the bounded readiness probe (`GET <canonical-origin>/health/ready`, setup-contract semantics) succeeds; `unreachable` for no-listener/foreign/other probe failure; `unknown` when the probe cannot run. |
| `protocol_and_signal_compatibility` | `protocol`: `http_protobuf` iff the effective trace protocol is `http/protobuf`; a different effective value is `mismatch`; unreadable is `unknown`. `trace_signal`: `enabled` iff effective `CLAUDE_CODE_ENABLE_TELEMETRY=1` and `OTEL_TRACES_EXPORTER=otlp`; explicitly off or absent is `disabled`; unreadable is `unknown`. Effective values come from the same read-only resolver as `source_effective_configuration`. |
| `source_version_and_schema_diagnostics` | `compatibility`: `supported` iff the version detector reports supported and persisted claude-code source-compatibility rows (when present) report a known fingerprint; `unsupported_source_version` on a detected unsupported version or an incompatible fingerprint; `unknown` otherwise. `schema`: `drift_detected` iff claude-code rows report drift; `matching` when rows exist without drift; `unknown` when no rows exist. |
| `last_ingest` | Over claude-code ingest records observed at or after the verification start: `accepted` if at least one accepted record exists; else `rejected` if at least one rejected record exists; else `none`. Without a verification window, `unknown`. |
| `raw_persistence` | `persisted` iff at least one `raw_persistence` candidate exists for the verification; `not_persisted` when ingest was accepted but no raw row was committed; `unknown` without a window. |
| `projection` | `completed` iff at least one `projection` candidate exists; `pending`/`failed` from the persisted projection state for accepted records; `not_started` when raw rows exist without projection activity; `unknown` without a window. |
| `exact_session_binding` | Stage-dependent, matching the Doctor contract's own pre-trace shape: while no window record's projection has completed, `(not_required, not_applicable)`; once at least one window record's projection completed, `(required, exact_bound)` iff at least one `exact_session_binding` candidate exists, else `(required, unbound)`. Exact binding is always required for `claude-code` `first_trace_ready`; the pre-trace `not_required` value expresses only that nothing bindable exists yet. |
| `completeness_and_content` | From the exactly-bound Session: `completeness` maps the Session completeness value verbatim (`unbound`/`partial`/`rich`/`full`); `unknown` while no Session is exactly bound. `content_capture`: agreed content state `available` or `redacted` -> `enabled`; `not_captured` -> `disabled`; `unsupported` -> `unsupported`; no agreement/rows -> from the effective content-gate settings (`enabled`/`disabled`); unreadable -> `unknown`. `raw_access`: taken from the monitor runtime-state row (below) only when the liveness probe classifies monitor-live; otherwise `unknown`. |
| `restart_or_new_process` | `required` iff the setup ledger's most recent applied `claude-code` change set has no accepted claude-code ingest at or after its apply time; `not_required` when no applied change set exists or an accepted ingest follows the apply; `unknown` when the ledger cannot be read. |

## Claude candidate observer (Local Monitor side)

A monitor-hosted component turns persisted real claude-code pipeline records
into Doctor evidence candidates through the existing internal
`ObserveCandidate` application boundary.

- **Sweep, not inline.** The observer exposes a `RunOnce()` seam and runs as a
  hosted worker with an injectable interval. Each cycle: list active
  `claude-code` verifications; when none exist, do nothing; otherwise derive
  candidates from records persisted at or after each verification's start.
  The sweep is stateless and idempotent (existing
  `(verification_id, evidence_ref)` dedup); it survives monitor restarts by
  recomputation. Tests drive `RunOnce()` directly; no sleeping or polling.
- **Kinds and stages.** `ingest` and `raw_persistence` derive from committed
  raw claude-code OTLP records; `projection` from the projected span row;
  `exact_session_binding` only when the shared exact-binding rule (below)
  binds the record's Session; `completeness_content` only for that exactly
  bound Session once its completeness is at least `partial` and a content
  state is agreed.
- **Exact-binding firewall.** The exact-binding decision is computed by the
  same rule component the OTel enricher uses (identical native session ID
  bytes, or an explicit resume/handoff link — the closed allowlist of the
  Claude exact-binding contract). The observer never reads persisted
  `source_adapter` labels (`otel-exact` or otherwise) and never treats shared
  `trace_id` continuity, repository, cwd, transcript path, process identity,
  or timestamps as binding evidence.
- **Hook traffic.** Hook events never produce candidates of any kind; their
  only role is establishing the native session identity that exact binding
  requires.
- **Class.** The observer emits `real_source` candidates exclusively, and only
  from records the ingestion pipeline actually persisted. No synthetic-probe
  traffic is generated or observed by #104.
- **Candidate eligibility (provenance).** Only records the ingestion pipeline
  recognized and persisted as claude-code OTel traffic are candidate-eligible
  in v1. A span that entered through the generic raw-OTLP path may still bind
  exactly under the exact-binding contract (that Session state stays valid,
  and provenance is never rewritten), but it produces no first-trace
  candidates. This is a documented v1 residual: an environment whose records
  are not recognized as claude-code cannot reach `first_trace_ready` and
  surfaces its state through the version/schema diagnostics families instead.
- **Evidence-ref grammar (closed).** `claude-otel-ingest-<traceId>-<spanId>`,
  `claude-otel-raw-<traceId>-<spanId>`,
  `claude-otel-projection-<traceId>-<spanId>`,
  `claude-otel-binding-<traceId>-<sessionGuid>`,
  `claude-otel-completeness-<sessionGuid>`, where `<traceId>`/`<spanId>` are
  lowercase hex OTLP identifiers and `<sessionGuid>` is the monitor-generated
  Session identifier. Refs never contain native session IDs, paths, prompts,
  tool arguments/results, credentials, or PII, and must satisfy the Doctor
  contract's evidence-reference validation.
- **Candidate timestamps.** A candidate's `observed_at` is the persisted
  record timestamp that was compared against the verification window (never
  the sweep's wall clock), and its `expires_at` equals the verification's
  `expires_at`.
- **Storage.** The Doctor verification tables and the ingestion pipeline
  tables live in the same monitor SQLite database; the ConfigCli process reads
  candidates from that database through the Doctor application service.
  Contention surfaces as the existing `doctor_store_busy` behavior — no
  retries or sleeps.

### Internal store operations (not part of `doctor.v1`)

`ListActive(source_surface, now)` and `ListCandidates(verification_id)` are
internal, source-neutral reads on the SQLite Doctor store and application
service. An internal exclusive start — check for an active, non-expired
verification of the same source surface and insert the new one inside a single
store transaction — backs `begin`'s atomic `active_verification_exists`
behavior. None of these are CLI verbs, HTTP routes, or additions to the public
Doctor store contract, and none change the SQLite schema of the Doctor tables.

### Monitor runtime-state row

At startup the Local Monitor upserts one source-neutral runtime-state row into
the shared monitor database recording its effective raw-access mode
(`available` or `sanitized_only`, from `--sanitized-only`) and the write time.
The first-trace fact collector reads it for the `raw_access` fact member and
trusts it only when the liveness probe classifies monitor-live (the row then
describes the currently running process). The row contains no paths, no
endpoints, and no source-specific data. Adding the row follows the monitor
database's existing migration mechanism and is covered by migration tests from
a committed prior-version database fixture.

## Setup handoff

Per `configuration-setup.md`, a successful changed Claude CLI apply emits next
actions `restart_claude_process` followed by `run_first_trace_doctor`. The
handoff target is `first-trace begin --adapter claude-code`. Setup still makes
no telemetry claim; `run_first_trace_doctor` is guidance to start this flow,
never evidence.

## Security and privacy

The envelope, guidance, candidate refs, logs, and repository-safe evidence
must not contain prompts, responses, tool arguments/results, credentials,
authorization values, native session IDs, absolute or user-derived paths, or
PII. Copyable commands contain no secrets and never implicitly enable content
capture. Negative tests inject markers at every input boundary (OTLP
attributes, hook payloads, settings values) and assert no leak into any
serialized envelope, candidate ref, log line, or committed fixture.

## Required regressions (#109 boundary)

1. A hook event with native session ID `A` plus an OTLP span sharing only
   `trace_id` (its `session.id` absent or equal to `B`) yields no
   `exact_session_binding` candidate, and completion over the remaining chain
   reports `session_unbound`.
2. A persisted event row labeled `otel-exact` (or `claude-code-otel`) is, by
   itself, never sufficient for an `exact_session_binding` candidate or an
   `exact_bound` fact.
3. The fact collector never derives `exact_session_binding` facts from the
   monitor's `SourceProjectionState.BindingState` projection.

## Non-scope

- GitHub Copilot first-trace orchestration (#103) and the shared proxy/UI or
  Release ZIP closeout (#105).
- Any change to `doctor.v1` / `doctor.facts.v1` vocabulary, precedence,
  verification identity, HTTP routes, or the SQLite schema of the Doctor
  tables. (The monitor runtime-state row is a monitor-owned addition through
  the monitor database's existing migration mechanism, not a Doctor-schema
  change.)
- Synthetic probe generation, historical backfill, remote collectors, Claude
  desktop, or claude.ai web.

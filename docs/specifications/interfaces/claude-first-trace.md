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
first-trace begin  --adapter claude-code [--interaction <interactive-cli|print|agent-sdk>] [--expires-at <iso-8601-utc>] [--json]
first-trace status   --verification-id <uuid-v7> [--json]
first-trace complete --verification-id <uuid-v7> --expected-revision <n> [--evidence <ref>]... [--json]
first-trace cancel   --verification-id <uuid-v7> --expected-revision <n> [--json]
```

- `--adapter` selects the registered first-trace source adapter. v1 accepts
  only `claude-code`; anything else is `invalid_arguments`.
- `--interaction` selects which bounded-interaction guidance variant is
  rendered. When omitted, guidance for all three variants is rendered. It does
  not change verification identity: all three variants share source surface
  `claude-code` and expected source adapter `claude-code-otel`.
- `--expires-at` is passed through to Doctor verification start unchanged and
  is bound by the Doctor contract's window rules.
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

1. Collect the Claude fact snapshot read-only (mapping table below).
2. Evaluate with the frozen Doctor evaluator.
3. If any blocking state applies: return `first_trace_blocked` with the
   evaluation embedded in `doctor`. Guidance may add copyable setup commands
   (for example `setup plan --adapter claude-code --target cli`), but the
   authoritative states and next actions are the embedded Doctor ones.
4. If an active `claude-code` verification already exists:
   `active_verification_exists` with that verification's id.
5. Otherwise start a Doctor verification (surface `claude-code`, adapter
   `claude-code-otel`) and return `first_trace_verification_started` with the
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
  into chains. A chain is the set of candidates sharing one OTLP trace ID as
  encoded in the evidence-ref grammar, plus the `completeness_content`
  candidate whose session GUID appears in that chain's
  `exact_session_binding` ref. If exactly one chain exists, select all of its
  candidate refs deterministically. If more than one chain exists, return
  `explicit_evidence_selection_required` with the candidate list; ordering or
  recency never selects a chain.
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
| `install_and_source_version` | `monitor_install`: `installed` when the readiness probe succeeds or the monitor database exists at the configured path; `not_installed` when neither; `unknown` on read error. `source_version`: `supported` iff the Claude version detector reports a supported version (>= 2.1.207); `unsupported` on a detected lower version; `unknown` when undetectable. `source_feature`: mirrors `source_version` in v1 (`available` iff supported). |
| `process_receiver_and_port` | From the bounded readiness probe classification: monitor-live -> `running`/`bound`/`monitor`; positive no-listener -> `not_running`/`not_bound`/`none`; foreign owner -> `not_running`/`not_bound`/`foreign`; any other transport failure -> all `unknown`. |
| `source_effective_configuration` | `endpoint_alignment`: `match` iff the effective (post-precedence) `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` equals the canonical monitor origin plus `/v1/traces`; otherwise `mismatch`; `unknown` when precedence sources cannot be read. |
| `endpoint_reachability` | `reachable` iff the bounded readiness probe classifies monitor-live; `unreachable` for no-listener/foreign/other probe failure; `unknown` when the probe cannot run. |
| `protocol_and_signal_compatibility` | `protocol`: `http_protobuf` iff the effective trace protocol is `http/protobuf`; a different effective value is `mismatch`; unreadable is `unknown`. `trace_signal`: `enabled` iff effective `CLAUDE_CODE_ENABLE_TELEMETRY=1` and `OTEL_TRACES_EXPORTER=otlp`; explicitly off or absent is `disabled`; unreadable is `unknown`. |
| `source_version_and_schema_diagnostics` | `compatibility`: `supported` iff the version detector reports supported and persisted claude-code source-compatibility rows (when present) report a known fingerprint; `unsupported_source_version` on a detected unsupported version or an incompatible fingerprint; `unknown` otherwise. `schema`: `drift_detected` iff claude-code rows report drift; `matching` when rows exist without drift; `unknown` when no rows exist. |
| `last_ingest` | Over claude-code ingest records observed at or after the verification start: `accepted` if at least one accepted record exists; else `rejected` if at least one rejected record exists; else `none`. Without a verification window, `unknown`. |
| `raw_persistence` | `persisted` iff at least one `raw_persistence` candidate exists for the verification; `not_persisted` when ingest was accepted but no raw row was committed; `unknown` without a window. |
| `projection` | `completed` iff at least one `projection` candidate exists; `pending`/`failed` from the persisted projection state for accepted records; `not_started` when raw rows exist without projection activity; `unknown` without a window. |
| `exact_session_binding` | `requirement` is always `required` for `claude-code`. `outcome`: `exact_bound` iff at least one `exact_session_binding` candidate exists; `unbound` when projection completed without one; `unknown` without a window. |
| `completeness_and_content` | From the exactly-bound Session: `completeness` maps the Session completeness value verbatim (`unbound`/`partial`/`rich`/`full`). `content_capture`: agreed content state `available` or `redacted` -> `enabled`; `not_captured` -> `disabled`; `unsupported` -> `unsupported`; no agreement/rows -> `unknown`. `raw_access`: `sanitized_only` iff the monitor runs with sanitized-only raw access, else `available`; `unknown` when unreadable. |
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
- **Evidence-ref grammar (closed).** `claude-otel-ingest-<traceId>-<spanId>`,
  `claude-otel-raw-<traceId>-<spanId>`,
  `claude-otel-projection-<traceId>-<spanId>`,
  `claude-otel-binding-<traceId>-<sessionGuid>`,
  `claude-otel-completeness-<sessionGuid>`, where `<traceId>`/`<spanId>` are
  lowercase hex OTLP identifiers and `<sessionGuid>` is the monitor-generated
  Session identifier. Refs never contain native session IDs, paths, prompts,
  tool arguments/results, credentials, or PII, and must satisfy the Doctor
  contract's evidence-reference validation.
- **Storage.** The Doctor verification tables and the ingestion pipeline
  tables live in the same monitor SQLite database; the ConfigCli process reads
  candidates from that database through the Doctor application service.
  Contention surfaces as the existing `doctor_store_busy` behavior — no
  retries or sleeps.

### Internal store reads (not part of `doctor.v1`)

`ListActive(source_surface, now)` and `ListCandidates(verification_id)` are
internal, source-neutral reads on the SQLite Doctor store and application
service. They are not CLI verbs, not HTTP routes, and not additions to the
public Doctor store contract.

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
  verification identity, HTTP routes, or SQLite schema.
- Synthetic probe generation, historical backfill, remote collectors, Claude
  desktop, or claude.ai web.

# Issue #104 — Claude Code first trace: guided setup and real-source verification

Plan of record for Issue #104. The work checklist is the Issue body's
`Implementation decomposition` (104-A…104-F) and `Acceptance criteria`; this
document does not restate them. It records the architecture decisions, the
cross-layer contract table, and the execution/verification order.

- Kickoff: `main@920ff43`, branch `claude/issue-104-implementation-447584`,
  worktree `.claude/worktrees/issue-104-implementation-447584`.
- Frozen upstream contracts: `doctor.v1` / `doctor.facts.v1` (#102, no enum or
  state changes), `setup.v1` (#66/#67/#68, no new codes; one reserved next
  action is activated spec-first), Claude exact-binding contract
  (`docs/specifications/contracts/source-capabilities/v1/claude-code/exact-binding.md`).
- Normative interface for the new work:
  `docs/specifications/interfaces/claude-first-trace.md`.
- Durable ledger: [`ledger.md`](ledger.md).
- Parallel workstreams: #103 (Copilot) and #106 (validation prep) own separate
  worktrees; collision files listed below are edited surgically and
  source-neutrally.

## Architecture decisions

- **D1 Candidate observer:** stateless sweep `ClaudeDoctorCandidateObserver`
  (`src/CopilotAgentObservability.Persistence.Sqlite/Doctor/ClaudeCode/`) with a
  `RunOnce()` seam; no inline ingestion hooks. Exact-binding evidence derives
  only from `ClaudeExactBindingRule` (extracted from
  `SqliteSessionOtelEnricher`), never from persisted `source_adapter` labels or
  `trace_id` continuity.
- **D2 Store operations:** `ListActive(sourceSurface, now)`,
  `ListCandidates(verificationId)`, and an atomic internal exclusive start
  added to `SqliteDoctorVerificationStore` + `SqliteDoctorApplicationService`
  only (source-neutral, reusable by #103). The public
  `IDoctorVerificationStore` contract and the Doctor tables' SQLite schema are
  unchanged; the separate monitor runtime-state row (raw-access fact input) is
  a monitor-owned addition via the existing monitor migration mechanism.
- **D3 Orchestration:** new source-neutral top-level CLI verb group
  `first-trace` (`begin`/`status`/`complete`/`cancel`) as a third
  application-level boundary beside `setup` and `doctor`. Claude-specific logic
  lives in `ClaudeCodeFirstTraceAdapter`. The `first_trace.v1` envelope embeds
  the unmodified `doctor.v1` result; it never restates Doctor state, evidence,
  or next actions. `begin` never runs setup apply; it verifies already-applied
  setup state read-only. `DoctorCli.cs` is unchanged.
- **D4 Fact assembly:** pure `ClaudeDoctorFactMapper` (inputs record →
  `DoctorFactSnapshot`) + `ClaudeDoctorFactCollector` IO shell in
  `src/CopilotAgentObservability.ConfigCli/FirstTrace/ClaudeCode/`. Pipeline
  families derive from Doctor candidates and pipeline tables, never from
  `SourceProjectionState.BindingState`.
- **D5 Identity:** source surface `claude-code` for all three execution
  surfaces; verification expected adapter always `claude-code-otel`;
  interactive CLI / `claude -p` / Agent SDK differ only in guidance and the
  104-F manifest settings matrix. No new tokens.
- **D6 #109 fix (user decision, in scope):** persist a discriminator for why
  the OTel enricher reached a Session (exact native-ID vs trace continuity vs
  ConversationId), key `SourceProjectionStateBuilder` exact-binding projection
  on that discriminator, and degrade legacy `otel-exact` rows honestly
  (never promote to `exact_linked` without exact evidence). Migration is
  verified from a committed old-version database fixture through restart.
- **D7 Spec-first:** new `claude-first-trace.md`; surgical
  `configuration-setup.md` update activating `run_first_trace_doctor` for the
  Claude adapter's successful changed apply; `first-trace-doctor.md` unchanged.

## Contract table (DTO / producer / consumer)

### Wire contracts

| Contract | Producer | Consumers | Notes |
| --- | --- | --- | --- |
| `setup.v1` `SetupCommandResult` | `SetupCommandDispatcher` (ConfigCli) | user/scripts | #104 adds `run_first_trace_doctor` to the Claude adapter's successful changed CLI apply (spec-first). No shape change. |
| `doctor.facts.v1` `DoctorFactSnapshot` | `ClaudeDoctorFactCollector`/`ClaudeDoctorFactMapper` (ConfigCli, new) | `DoctorEvaluator`, `SqliteDoctorApplicationService` | 12-family mapping pinned in `claude-first-trace.md`; `Observations` empty on `complete`. |
| `doctor.v1` `DoctorResult` | `SqliteDoctorApplicationService` via `DoctorJson` | `DoctorCli` (unchanged), `DoctorRoutes` (unchanged), `FirstTraceCli` (embeds verbatim) | Frozen. Single source of state/evidence/next action. |
| `first_trace.v1` envelope | `FirstTraceCli` (ConfigCli, new) | user/scripts | Embeds `doctor.v1` verbatim + orchestration `code`, `guidance[]`, `candidates[]`. Never restates Doctor vocabulary. |
| OTLP `/v1/traces`, Hook `/api/session-ingest/v1/events` | Claude Code producer | `MonitorHost`, `SessionRoutes` | Existing; unchanged by #104. |
| Monitor trace/session APIs + UI (`SourceProjectionState`) | `SourceProjectionStateBuilder` | `MonitorHost` APIs, `SessionRoutes`, `monitor-tracelist.js`/`monitor-flow.js` | #109 fix changes only the exact-binding derivation input (discriminator), not the wire shape. |

### Persisted candidate contract (cross-process: LocalMonitor writes, ConfigCli reads)

| Evidence kind | Stage / deterministic rule | Evidence ref grammar | Consumer |
| --- | --- | --- | --- |
| `ingest` | accepted claude-code OTLP record committed after verification `StartedAt` | `claude-otel-ingest-<traceId>-<spanId>` | `first-trace status` listing; `complete` resolution; evaluator |
| `raw_persistence` | raw telemetry row committed for that record | `claude-otel-raw-<traceId>-<spanId>` | same |
| `projection` | projected span row exists for that record | `claude-otel-projection-<traceId>-<spanId>` | same |
| `exact_session_binding` | `ClaudeExactBindingRule` binds the record to a Session (identical native session ID bytes or explicit resume/handoff) | `claude-otel-binding-<traceId>-<sessionGuid>` | same |
| `completeness_content` | the exactly-bound Session reaches completeness ≥ partial with an agreed content state | `claude-otel-completeness-<sessionGuid>` | same |

All candidates: `evidence_class = real_source`, surface `claude-code`, adapter
`claude-code-otel`. Hook events never produce candidates (they enable exact
binding only). Records carrying the synthetic-probe marker are excluded.
Refs contain only hex trace/span IDs and monitor-generated GUIDs — never native
session IDs, paths, prompts, or PII. Dedup: existing
`(verification_id, evidence_ref)` uniqueness. Cross-process concurrency relies
on the existing SQLite store behavior (`doctor_store_busy` on contention).

### Fact family contract table (normative decision rules and consumers)

The following table makes the producer/input, decision rule, and consumer
boundary explicit. The decision rules are the v1 rules in
`claude-first-trace.md`; this table does not add a second fact vocabulary.

| Family | Producer / input | Deterministic decision rule (v1, `claude-code`) | Consumer |
| --- | --- | --- | --- |
| `install_and_source_version` | `ClaudeDoctorFactCollector` reads monitor readiness/database presence and reruns `ClaudeCodeVersionDetector` read-only. | `monitor_install`: `installed` when the bounded probe is monitor-live or the monitor database exists at `--database`; `not_installed` when neither; `unknown` on read error. `source_version`: `supported` iff the detector reports a supported version (>= 2.1.207); `unsupported` on a detected lower version; `unknown` when undetectable. `source_feature` mirrors `source_version` in v1 (`available` iff supported). | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; the resulting `doctor.v1` is embedded by `FirstTraceCli`. |
| `process_receiver_and_port` | `ClaudeDoctorFactCollector` performs the bounded `GET <canonical-origin>/health/live` probe using the setup-contract classification. | HTTP 200 with the complete single-property JSON object `"status":"live"` is monitor-live → `running`/`bound`/`monitor`; connection refused or equivalent positive no-listener → `not_running`/`not_bound`/`none`; every other probe result → `not_running`/`not_bound`/`foreign`; an unresolvable canonical origin → all `unknown`. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` reports the embedded result and next action. |
| `source_effective_configuration` | `ClaudeDoctorFactCollector` uses the read-only effective-value resolver over process environment, managed policy, local settings, project settings, and user settings. | `endpoint_alignment`: `match` iff effective `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` equals the canonical monitor origin plus `/v1/traces`; different or absent → `mismatch`; unreadable source or unresolved precedence conflict → `unknown`. The resolver reports each key as effective, absent, or conflict using the setup precedence. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` uses the result for `begin`/`status`. |
| `endpoint_reachability` | `ClaudeDoctorFactCollector` performs the bounded `GET <canonical-origin>/health/ready` readiness probe. | `reachable` iff the setup-contract readiness probe succeeds; no-listener, foreign, or other probe failure → `unreachable`; probe cannot run → `unknown`. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` exposes the authoritative `doctor.v1` outcome. |
| `protocol_and_signal_compatibility` | `ClaudeDoctorFactCollector` reuses the same read-only effective-value resolver for trace protocol and telemetry/exporter gates. | `protocol`: `http_protobuf` iff effective trace protocol is `http/protobuf`; different → `mismatch`; unreadable → `unknown`. `trace_signal`: `enabled` iff effective `CLAUDE_CODE_ENABLE_TELEMETRY=1` and `OTEL_TRACES_EXPORTER=otlp`; explicitly off or absent → `disabled`; unreadable → `unknown`. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` reports the embedded result without reinterpreting it. |
| `source_version_and_schema_diagnostics` | `ClaudeDoctorFactCollector` combines the version detector with persisted Claude source-compatibility fingerprint/drift rows. | `compatibility`: `supported` iff the detected version is supported and present compatibility rows have a known fingerprint; unsupported version or incompatible fingerprint → `unsupported_source_version`; otherwise `unknown`. `schema`: `drift_detected` iff Claude rows report drift; `matching` when rows exist without drift; no rows → `unknown`. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` returns the result and guidance. |
| `last_ingest` | `ClaudeDoctorFactCollector` reads accepted/rejected Claude ingest rows at or after the verification start. | `accepted` if any accepted record exists; else `rejected` if any rejected record exists; else `none`. Without a verification window → `unknown`. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `first_trace.v1` carries the resulting Doctor state. |
| `raw_persistence` | `ClaudeDoctorFactCollector` reads raw rows and `ListCandidates` entries of kind `raw_persistence`. | `persisted` iff at least one `raw_persistence` candidate exists; `not_persisted` when ingest was accepted but no raw row was committed; without a window → `unknown`. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` lists the related candidates and embeds the result. |
| `projection` | `ClaudeDoctorFactCollector` reads projected rows, persisted projection state, and `ListCandidates` entries of kind `projection`. | `completed` iff at least one `projection` candidate exists; `pending`/`failed` from persisted projection state for accepted records; `not_started` when raw rows exist without projection activity; without a window → `unknown`. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` lists candidates and returns the embedded result. |
| `exact_session_binding` | `ClaudeDoctorFactCollector` reads `ListCandidates` entries of kind `exact_session_binding`; it does not derive this family from `SourceProjectionState.BindingState`. | Before any window record has completed projection → `(not_required, not_applicable)`; after projection completion → `(required, exact_bound)` iff an exact-binding candidate exists, otherwise `(required, unbound)`. Exact binding is required for `first_trace_ready`; the pre-trace value only says that nothing bindable exists yet. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` consumes the Doctor result. The candidate observer and exact-binding rule own candidate creation. |
| `completeness_and_content` | `ClaudeDoctorFactCollector` reads the exactly-bound Session, content-state rows, effective content-gate settings, and the monitor runtime-state row for `raw_access`. | Bound-session completeness is copied as `unbound`/`partial`/`rich`/`full`, and is `unknown` while no Session is exactly bound. `available`/`redacted` content → `enabled`; `not_captured` → `disabled`; `unsupported` → `unsupported`; no agreement/rows → effective content-gate setting; unreadable → `unknown`. `raw_access` is trusted only when liveness is monitor-live. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` embeds the result, while `SourceProjectionState` remains the monitor read model for its own Session fields. |
| `restart_or_new_process` | `ClaudeDoctorFactCollector` compares the setup ledger's latest applied Claude change set with accepted Claude ingest since that apply. | `required` iff no accepted Claude ingest exists at or after the apply time; `not_required` when no applied change set exists or accepted ingest follows it; unreadable ledger → `unknown`. | `ClaudeDoctorFactMapper` → `DoctorEvaluator`; `FirstTraceCli` renders the authoritative next action. |

### `first_trace.v1` ↔ `doctor.v1` ↔ `SourceProjectionState`

| `first_trace.v1` member or stage | `doctor.v1` correspondence | `SourceProjectionState` correspondence | Boundary |
| --- | --- | --- | --- |
| `doctor` and `evaluation_preview` | An unmodified serialized `DoctorResult`; state, evidence, reason codes, and next actions come only from the frozen Doctor contract. | No direct replacement. The monitor projection is an input-side read model, not a second Doctor result. | `FirstTraceCli` embeds the Doctor result verbatim and never re-derives Doctor vocabulary. |
| `candidates[]` with `ingest` or `raw_persistence` | Doctor evidence candidates resolved by the Doctor store during completion and represented by the corresponding evidence kinds. | No direct `SourceProjectionState` field; these stages come from persisted ingestion/raw rows. | Candidate refs are opaque and do not infer Session binding. |
| `candidates[]` with `projection` | Doctor `projection` evidence candidate. | A projected span row is an input to building `SourceProjectionState`; projection completion is still decided from persisted pipeline state and candidates. | `SourceProjectionState` does not create or accept a Doctor candidate by itself. |
| `candidates[]` with `exact_session_binding` and the `exact_session_binding` fact | Doctor evidence kind plus the fact member `(required, exact_bound)`; absence yields `(required, unbound)`. | `BindingState=exact_linked` is allowed only when the exact native-ID or explicit resume/handoff rule produced the exact link. Shared trace continuity remains `otel_only`/`hook_only`. | Neither a persisted adapter label nor `trace_id` continuity may promote the binding. |
| `candidates[]` with `completeness_content` and the `completeness_and_content` fact | Doctor evidence kind plus the completeness/content fact family used by the evaluator. | The exactly-bound Session's `Completeness` and `ContentState` are the corresponding monitor read-model values. | The candidate exists only after exact binding, completeness at least `partial`, and agreed content state. |
| Resulting `first_trace_ready` or non-ready Doctor state | The evaluator's fixed primary state and next action remain authoritative. | Monitor `BindingState`, `Completeness`, and `ContentState` remain observable projection fields and are not renamed into Doctor state. | `SourceProjectionState` is never used as the fact collector's exact-binding authority. |

## Execution order

T0 docs/specs → T1 red cross-surface test → parallel tracks
(A: fact mapper/collector; B: store reads + binding rule + observer;
C: #109 fix after the rule extraction) → T6 `first-trace` verbs + adapter +
`run_first_trace_doctor` emission → T7 cross-surface green → T8 104-E matrix →
T9 review, full validation, candidate freeze, `handoff-106.md`.

Collision files (surgical, source-neutral, compare against #103 ownership
before editing): `CliApplication.cs` (1 dispatch line), `MonitorHost.cs`
(~5 registration lines), `SqliteDoctorVerificationStore.cs` +
`SqliteDoctorApplicationService.cs` (two reads), `SqliteSessionOtelEnricher.cs`
+ `SourceCompatibility/SourceDiagnosticDto.cs` (#109), `configuration-setup.md`,
`docs/task.md`. `SetupCompositionRoot.cs` is expected to stay untouched.

## Verification

Focused tests per task; before freeze:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Atomicity/rollback/stale/concurrency without sleep or retry (injected clocks,
controlled locks, CAS revisions). Migration from committed old-version fixtures
through restart. Copilot + shared Doctor regressions rerun for every touched
shared file. Feature-branch completion is reported separately from main
integration (main integration is out of scope for #104).

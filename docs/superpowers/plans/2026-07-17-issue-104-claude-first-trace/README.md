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
- **D2 Store reads:** `ListActive(sourceSurface, now)` and
  `ListCandidates(verificationId)` added to `SqliteDoctorVerificationStore` +
  `SqliteDoctorApplicationService` only (source-neutral, reusable by #103). The
  public `IDoctorVerificationStore` contract and SQLite schema are unchanged.
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

| Evidence kind | Produced when (deterministic rule) | Evidence ref grammar | Consumer |
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

### Fact family producer table (summary; normative copy in the interface spec)

| Family | Input source | Producer |
| --- | --- | --- |
| `install_and_source_version` | monitor readiness/database presence; `ClaudeCodeVersionDetector` (read-only) | fact collector |
| `process_receiver_and_port` | first-trace bounded liveness probe (`/health/live`, setup-contract classification: live / positive no-listener / other=foreign) | fact collector |
| `source_effective_configuration` | first-trace read-only effective-value resolver (setup precedence: env > managed > local > project > user; per-key effective/absent/conflict) | fact collector |
| `endpoint_reachability` | bounded readiness probe (`/health/ready`) | fact collector |
| `protocol_and_signal_compatibility` | same effective-value resolver (protocol + telemetry/exporter gates) | fact collector |
| `source_version_and_schema_diagnostics` | version detector + source-compatibility fingerprint/drift rows | fact collector |
| `last_ingest` | raw/source-compatibility rows in the verification window | fact collector (SQLite read) |
| `raw_persistence` | `ListCandidates` kind `raw_persistence` / raw rows | fact collector |
| `projection` | `ListCandidates` kind `projection` / projection state | fact collector |
| `exact_session_binding` | `ListCandidates` kind `exact_session_binding`; stage-dependent: pre-projection `(not_required, not_applicable)`, then `(required, unbound/exact_bound)` | fact collector |
| `completeness_and_content` | bound-session completeness + content-state rows + monitor runtime-state row (`raw_access`, trusted only when probe = monitor-live) | fact collector |
| `restart_or_new_process` | setup ledger latest applied claude-code change set vs accepted ingest since apply | fact collector |

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

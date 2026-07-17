# Issue #104 T2 Claude Doctor Fact Mapper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a pure Claude Code first-trace fact mapper whose closed, already-classified inputs deterministically produce a valid `doctor.facts.v1` snapshot for all twelve Doctor families.

**Architecture:** `ClaudeDoctorFactInputs` owns the ConfigCli-side input vocabulary: dedicated enums represent probe, resolver, compatibility, content, runtime, ledger, and window states, with no detector reuse or raw setting strings. `ClaudeDoctorFactMapper.Map` performs only enum/boolean projection into the existing Doctor contracts, always emits all twelve family records, keeps observations empty, and applies the normative pre-window and stage-dependent rules.

**Tech Stack:** C#/.NET 10, existing `CopilotAgentObservability.Doctor` contracts/evaluator/validation, xUnit.

## Global Constraints

- Read and implement `docs/specifications/interfaces/claude-first-trace.md`, especially the normative family table and `begin` pre-window values.
- Do not add IO, detector invocation, SQLite access, CLI plumbing, dependencies, or changes to Doctor, Persistence.Sqlite, MonitorHost, or Setup adapter files.
- Keep all new ConfigCli input types internal and closed; only the canonical monitor origin is a free string.
- Emit `sourceSurface = "claude-code"` and `expectedAdapter = "claude-code-otel"` by default, while allowing the mapper parameters to override them.
- Observations must always be an empty list.

---

### Task 1: Add failing contract tests

**Files:**
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/ClaudeDoctorFactMapperTests.cs`

**Interfaces:**
- Consume the intended internal API `ClaudeDoctorFactInputs` and `ClaudeDoctorFactMapper.Map(ClaudeDoctorFactInputs inputs, DateTimeOffset observedAt, string? verificationId, string sourceSurface = "claude-code", string expectedAdapter = "claude-code-otel")`.
- Assert the existing `DoctorEvaluator.Evaluate` and `DoctorValidation.IsValidFactSnapshot` contracts without modifying those projects.

- [ ] **Step 1: Write the failing test**

  Add a healthy no-window test with live monitor, installed database, supported source, matching effective configuration, successful readiness, matching compatibility rows, open content gate, available runtime raw access, and no applied change set. Assert every family member equals the normative fixed pre-window shape and that evaluation is `ReadyNoRealTrace`.

  Add one evaluator-backed test for each blocker/advisory branch required by the brief: endpoint mismatch, disabled signal, unsupported source version, foreign port owner, no listener, rejected ingest, raw-persisted projection pending, projection failed, completed unbound session, restart required, schema drift, disabled content, sanitized-only raw access, and non-live runtime row producing unknown raw access.

  Keep the representative coverage for the remaining detector, resolver, runtime, and ledger inputs, and add a deterministic full Cartesian sweep over the window states: projection state (`unknown`, `not_started`, `pending`, `failed`, `completed`) x binding candidate state x every bound-session completeness value x every agreed content state, plus the pre-window shape. Assert every mapped snapshot passes `DoctorValidation.IsValidFactSnapshot` and that `DoctorEvaluator.Evaluate` returns the expected result class rather than `InvalidInput`.

  Add a same-input determinism test using equal timestamps and verification identity and assert the complete snapshots are equal.

- [ ] **Step 2: Run the focused test to verify RED**

  Run:

  ```powershell
  dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ClaudeDoctorFactMapper
  ```

  Expected: compilation failure because the new input vocabulary and mapper do not exist yet.

---

### Task 2: Implement the closed input vocabulary and pure mapper

**Files:**
- Create: `src/CopilotAgentObservability.ConfigCli/FirstTrace/ClaudeCode/ClaudeDoctorFactInputs.cs`
- Create: `src/CopilotAgentObservability.ConfigCli/FirstTrace/ClaudeCode/ClaudeDoctorFactMapper.cs`

**Interfaces:**
- `ClaudeDoctorFactInputs` contains the classified fields used by the normative table: liveness, database presence, source version, canonical origin, endpoint/protocol/telemetry/exporter resolver results, readiness, compatibility rows, optional window, effective content gate, runtime raw access, and setup ledger state.
- `ClaudeDoctorVerificationWindow` contains accepted/rejected ingest presence, raw/projection/binding candidate presence, projection evidence, bound-session completeness, and agreed content state.
- `ClaudeDoctorFactMapper.Map` returns `DoctorFactSnapshot` and has no dependencies or side effects.

- [ ] **Step 1: Define only closed enums and records required by the tests**

  Use dedicated Claude first-trace enums for:

  - monitor liveness (`MonitorLive`, `PositiveNoListener`, `OtherForeign`, `ProbeUnavailable`);
  - source version (`Supported`, `Unsupported`, `Undetectable`);
  - endpoint/protocol/gate resolver outcomes (`Expected`, `Different`, `Absent`, `Conflict`, `Unreadable` as appropriate);
  - source compatibility rows (`NoRows`, `Matching`, `Drift`, `Incompatible`, `Unreadable`);
  - projection evidence (`NotStarted`, `Pending`, `Failed`);
  - bound-session completeness (`Unbound`, `Partial`, `Rich`, `Full`, `Unavailable`);
  - agreed content state (`None`, `Available`, `Redacted`, `NotCaptured`, `Unsupported`, `Unreadable`);
  - effective content gate (`Enabled`, `Disabled`, `Unreadable`);
  - runtime raw access (`Available`, `SanitizedOnly`, `Absent`, `Unreadable`);
  - setup ledger (`NoAppliedChangeSet`, `AwaitingAcceptedIngest`, `AcceptedIngestAfterApply`, `Unreadable`).

  Use `bool?` only where the collector can distinguish true, false, and unobtainable: monitor database presence and readiness success. Keep the canonical origin as the only free string member.

- [ ] **Step 2: Implement the family projections**

  Construct `DoctorFactSnapshot` with `DoctorSchemaVersions.FactsV1`, the supplied metadata, `[]` observations, and all twelve family records. Map liveness, install/version, endpoint/protocol/signal, readiness, compatibility/schema, and ledger states directly from the closed inputs. For resolver conflicts and unreadable values, emit the contract’s `Unknown` member; map absent/different/off states to the specified mismatch/disabled values.

- [ ] **Step 3: Implement window and content rules**

  With no window, emit exactly `last_ingest = none`, `raw_persistence = not_persisted`, `projection = not_started`, `exact_session_binding = (not_required, not_applicable)`, and completeness `unknown`. With a window, prioritize accepted ingest over rejected, derive raw persistence only from raw candidates or accepted-without-raw, derive projection completion from a projection candidate otherwise from persisted projection evidence, and transition binding to required only after projection completion. Map completeness only from an exact binding, map agreed content state before falling back to effective content-gate settings, and trust runtime raw access only for `MonitorLive`. If an exact-bound input simultaneously claims `unbound` Session completeness, resolve the contradictory pair to `exact_bound` plus completeness `unknown`, preserving the binding evidence without claiming an invalid `unbound` completeness.

  Endpoint resolver `Conflict` maps to `EndpointAlignmentStatus.Unknown`: a precedence winner is an effective value and therefore is not a conflict; only a conflict with no effective value remains unknown.

- [ ] **Step 4: Run the focused test to verify GREEN**

  Run the focused mapper command from Task 1. Expected: all mapper tests pass.

---

### Task 3: Refactor only after green and validate the repository surface

**Files:**
- Modify only the two new mapper source files and the new mapper test file if duplication is removed.

- [ ] **Step 1: Run the focused mapper tests again**

  Confirm the focused command remains green after any mechanical cleanup.

- [ ] **Step 2: Run the required solution build**

  Run:

  ```powershell
  dotnet build CopilotAgentObservability.slnx
  ```

  Expected: build succeeds with no source or project files outside the requested ConfigCli mapper/test scope changed.

- [ ] **Step 3: Inspect the diff and commit**

  Preserve the pre-existing `docs/superpowers/plans/2026-07-17-issue-104-claude-first-trace/ledger.md` change. Commit only the mapper plan and T2 files with a message beginning `Issue #104: fix(first-trace): ` and a body explaining that review proved an invalid cross-field snapshot was reachable and the conflict mapping contradicted the pinned rule.

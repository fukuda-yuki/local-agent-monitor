# Issues #103/#104 Source-Specific Doctor Handoff Design

**Status:** Approved execution design for G0-2 and G0-3

**Branch:** `codex/issues-103-104-doctor-handoff-contract`

**Base:** `main` at the integrated Issues #66/#67, #68, #102, #107, and #108 baseline.

## Goal

Fix one source-neutral handoff between the completed Doctor core and the future
GitHub Copilot and Claude Code first-trace slices. The handoff must let Issues
#103 and #104 contribute setup facts, runtime facts, and explicit evidence
candidates without adding source-specific Doctor states, changing the Doctor
verification lifecycle, or guessing a trace or Session.

G0-2 promotes the handoff to the canonical Doctor specification and adds a
small source-neutral composition contract. G0-3 adds executable contract tests
that preserve the completed Doctor behavior and intentionally remain RED only
for the source-specific producer implementations owned by Issues #103 and
#104.

## Non-goals

- Implement GitHub Copilot setup-to-first-trace behavior.
- Implement Claude Code setup-to-first-trace behavior.
- Add a candidate-injection CLI command or HTTP route.
- Add proxy DTOs, Razor, JavaScript, Canvas, or another UI.
- Reimplement setup transactions, source compatibility, Session binding, or the
  Doctor evaluator/store.
- Add a new Doctor state, reason code, severity, retryability, next action,
  evidence class, or evidence kind.
- Add App/SDK live support where no current manifest-backed real-source path is
  available.

## Considered approaches

### 1. Documentation-only handoff

This would add an authority table but no executable boundary. It is minimal,
but #103 and #104 could still assemble `DoctorFactSnapshot` and candidate
carriers differently and silently move a fact family or verification field
between authorities.

### 2. Source-neutral contribution records plus a composer — selected

The Doctor assembly adds two records whose property sets divide the existing
twelve fact families into five setup-owned and seven runtime-owned families.
A composer creates direct-evaluation snapshots, persisted-verification
completion snapshots, and verification-scoped evidence candidates while
preserving the existing Doctor types and validation rules. Source-specific
implementations remain outside the Doctor core.

This is the smallest executable boundary that prevents authority and carrier
drift without creating a new framework or source-specific enum.

### 3. New source-adapter project and registry

A new project could own source adapters and registrations. It would require
premature decisions about setup/runtime assembly dependencies, registration,
and Issue #105 reuse. G0 does not need that machinery and therefore rejects it.

## Source-neutral composition contract

`CopilotAgentObservability.Doctor` adds:

- `DoctorSetupFactContribution`, containing only:
  - `install_and_source_version`;
  - `source_effective_configuration`;
  - `endpoint_reachability`;
  - `protocol_and_signal_compatibility`;
  - `restart_or_new_process`.
- `DoctorRuntimeFactContribution`, containing only:
  - `process_receiver_and_port`;
  - `source_version_and_schema_diagnostics`;
  - `last_ingest`;
  - `raw_persistence`;
  - `projection`;
  - `exact_session_binding`;
  - `completeness_and_content`.
- `DoctorSourceHandoffComposer`, with three entry points:
  - direct evaluation accepts typed observations and emits no verification ID;
  - persisted completion accepts an existing `DoctorVerification`, copies its
    exact source/adapter/verification ID, and always emits an empty observation
    list so the store remains the only trusted candidate resolver; and
  - candidate composition accepts an existing active verification, copies its
    source/adapter/verification ID/expiry, and accepts evidence observed only in
    the half-open verification window.

The composer does not evaluate a snapshot, read stores, inspect processes,
collect telemetry, persist candidates, select evidence, generate candidate
IDs, or choose a Doctor state. It validates through the existing
`DoctorValidation` contract and returns existing source-neutral Doctor types.

## Authority table

| Doctor fact family | Required authority |
| --- | --- |
| `install_and_source_version` | Issue #67/#68 setup detection and supported-version result |
| `process_receiver_and_port` | Local Monitor process/readiness and owned-port observation |
| `source_effective_configuration` | persisted setup status and effective setting source |
| `endpoint_reachability` | the bounded setup readiness probe |
| `protocol_and_signal_compatibility` | setup plan/status and source signal configuration |
| `source_version_and_schema_diagnostics` | persisted source-schema observation/compatibility result |
| `last_ingest` | ingestion acceptance/rejection record |
| `raw_persistence` | exact raw-record persistence evidence |
| `projection` | monitor projection state/error evidence |
| `exact_session_binding` | persisted Session exact-enrichment result |
| `completeness_and_content` | Session/source projection; content and raw access remain distinct |
| `restart_or_new_process` | setup result/status restart requirement |

A later source implementation may leave an authority unknown. It must not move
the family to another authority or replace unknown with false, zero, supported,
successful, or absent.

## Source identity and adapter scope

The first implementation slices are manifest-backed for:

- `github-copilot-vscode`;
- `github-copilot-cli`;
- `claude-code`.

A first-trace verification is surface-scoped in this handoff. It starts with
`expected_source_adapter = null` because a successful source journey may need
more than one concrete adapter, such as OTel plus Hook evidence. Doctor evidence
candidates created for this surface-scoped verification also use a null Doctor
adapter. The referenced source observation, raw record, and Session event keep
their actual adapter provenance; the Doctor carrier does not rewrite those
records.

This rule preserves Issue #108/D059: a Claude OTel record still labeled
`raw-otlp` can contribute exact native-session evidence without being promoted
to `claude-code-otel`.

GitHub Copilot App/SDK and Claude Agent SDK remain caller-managed setup targets.
G0 does not invent a new manifest surface or claim a live first-trace path for
them. Their availability/degraded mapping remains owned by #103/#104 and must
use the same twelve families when implemented.

## Verification and candidate flow

1. The source-specific slice loads an active `DoctorVerification` by the exact
   opaque verification ID.
2. Trusted internal source code observes exact records within
   `started_at <= observed_at < expires_at`.
3. It composes bounded `DoctorEvidenceCandidate` values through the shared
   composer. Verification ID, source, nullable adapter, and expiry are copied
   from the persisted verification and cannot be overridden.
4. It persists candidates through the existing internal `ObserveCandidate`
   path.
5. A caller explicitly selects opaque evidence references.
6. Completion submits a snapshot with an empty `observations` array.
7. The store resolves the selected candidates, validates source and expiry, and
   constructs trusted observations for the evaluator.

After a Local Monitor restart, the source slice reloads the same persisted
verification ID and repeats the source refresh under that context. It does not
select the latest verification, latest trace, or latest Session. Candidate ID
generation, deduplication against already persisted candidates, and source/store
integration remain #103/#104 responsibilities under the existing store
contract.

Forbidden selection signals remain unchanged: latest verification, latest
trace, latest Session, repository, workspace, cwd, process identity, trace ID
alone, and timestamp proximity.

## G0-3 executable contract tests

The shared tests contain two groups.

### Green shared-boundary tests

- setup and runtime contributions map to the exact twelve families;
- direct composition retains typed observations and has no verification ID;
- persisted-completion composition copies the verification identity and has no
  caller observations;
- candidate composition copies verification scope/expiry and rejects evidence
  outside the half-open verification window;
- invalid source identity, inactive verification, and unsafe observation
  references are rejected with the fixed sanitized error; and
- the Doctor assembly still contains no source-specific Doctor enum.

### Intentional RED source-implementation tests

The test assembly scans the Doctor, Config CLI, and Local Monitor production
assemblies for concrete source handoff implementations. Three separate tests
remain RED until their owning Issue provides the corresponding implementation:

- #103: `GitHubCopilotVsCodeSourceHandoff_IsImplementedOutsideDoctorCore`;
- #103: `GitHubCopilotCliSourceHandoff_IsImplementedOutsideDoctorCore`;
- #104: `ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore`.

The tests do not require a concrete class name or assembly location. A later
implementation satisfies its owned row by implementing the shared
`IDoctorSourceHandoff` contract and exposing the exact surface identity. The
three-way split lets #103 and #104 work in separate worktrees and run their
owned contract tests without editing or weakening the other Issue's expected
surface.

## Shared implementation interface

`IDoctorSourceHandoff` is source-neutral. It exposes only:

- `SourceSurface`;
- nullable `ExpectedSourceAdapter`;
- direct composition from the two contribution records plus typed observations;
- persisted-completion composition from an existing verification plus the two
  contribution records; and
- candidate composition from an existing verification plus existing evidence
  class/kind/reference values.

Implementations must use `DoctorSourceHandoffComposer`; they must not select a
state, reorder facts, override candidate verification scope, inject candidate
observations into a completion snapshot, or add a source-specific result type.

## Error and safety behavior

- Invalid composition throws one fixed `ArgumentException` message without
  echoing a source value, evidence reference, path, payload, or exception detail.
- Evidence-reference and candidate validation remain authoritative in
  `DoctorValidation`.
- The handoff stores no prompt, response, tool body, source fragment, PII,
  credential, authorization value, local path, database path, or raw payload.
- No network, process, clock, database, or filesystem operation is added to the
  Doctor core.

## Test and review gates

Focused commands after implementation:

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorSourceHandoffContractTests
dotnet build CopilotAgentObservability.slnx
```

The intended G0-3 checkpoint is:

- eight shared-boundary tests: GREEN;
- the two GitHub Copilot source-implementation tests: RED for #103;
- the Claude Code source-implementation test: RED for #104;
- no unrelated test failure or compile failure.

Before later integration, #103 and #104 must turn their owned rows GREEN and
the repository must run the pinned build, Playwright bootstrap, and full
solution test commands.

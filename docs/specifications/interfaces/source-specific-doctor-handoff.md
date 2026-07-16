# Source-Specific Doctor Handoff

This document is the canonical handoff contract from the completed
source-independent First-Trace Doctor to Issues #103 and #104. The shared
Doctor state model, validation, lifecycle, persistence, CLI, and HTTP contracts
remain defined by [First-Trace Doctor](first-trace-doctor.md).

## Scope

This interface fixes:

- which of the twelve Doctor fact families come from setup evidence and which
  come from Local Monitor runtime evidence;
- how direct evaluation and persisted verification completion construct one
  `DoctorFactSnapshot`;
- how source-specific implementations identify themselves without adding a
  source-specific Doctor enum;
- how candidate carriers inherit the exact verification source, adapter,
  identity, and expiry;
- how first-trace verifications scope evidence when more than one concrete
  adapter may contribute to one source journey; and
- the RED contract gates that Issues #103 and #104 must turn GREEN.

This interface does not implement a GitHub Copilot or Claude Code producer,
add a public candidate-write route or command, add UI, or change the Doctor
state catalog.

## Shared types

`CopilotAgentObservability.Doctor` owns the following source-neutral types.

### `DoctorSetupFactContribution`

The record contains exactly these nullable existing Doctor family types, in
this constructor/property order:

1. `InstallAndSourceVersionFacts? InstallAndSourceVersion`
2. `SourceEffectiveConfigurationFacts? SourceEffectiveConfiguration`
3. `EndpointReachabilityFacts? EndpointReachability`
4. `ProtocolAndSignalCompatibilityFacts? ProtocolAndSignalCompatibility`
5. `RestartOrNewProcessFacts? RestartOrNewProcess`

No runtime-owned family is present on this record.

### `DoctorRuntimeFactContribution`

The record contains exactly these nullable existing Doctor family types, in
this constructor/property order:

1. `ProcessReceiverAndPortFacts? ProcessReceiverAndPort`
2. `SourceVersionAndSchemaDiagnosticsFacts? SourceVersionAndSchemaDiagnostics`
3. `LastIngestFacts? LastIngest`
4. `RawPersistenceFacts? RawPersistence`
5. `ProjectionFacts? Projection`
6. `ExactSessionBindingFacts? ExactSessionBinding`
7. `CompletenessAndContentFacts? CompletenessAndContent`

No setup-owned family is present on this record.

### `DoctorSourceHandoffAttribute`

A concrete source handoff class carries exactly one non-inherited attribute:

```csharp
[DoctorSourceHandoff("github-copilot-vscode")]
```

The constructor takes one `source_surface` string. The attribute is metadata
for implementation coverage and does not supply facts, candidates, or state.
The implementation's runtime `SourceSurface` property must equal the attribute
value.

### `IDoctorSourceHandoff`

The interface contains:

```csharp
string SourceSurface { get; }
string? ExpectedSourceAdapter { get; }

DoctorFactSnapshot ComposeDirectEvaluation(
    DateTimeOffset observedAt,
    DoctorSetupFactContribution setupFacts,
    DoctorRuntimeFactContribution runtimeFacts,
    IReadOnlyList<DoctorObservation> observations);

DoctorFactSnapshot ComposeVerificationCompletion(
    DoctorVerification verification,
    DateTimeOffset observedAt,
    DoctorSetupFactContribution setupFacts,
    DoctorRuntimeFactContribution runtimeFacts);

DoctorEvidenceCandidate ComposeCandidate(
    DoctorVerification verification,
    string candidateId,
    DoctorEvidenceClass evidenceClass,
    DoctorEvidenceKind evidenceKind,
    string evidenceRef,
    DateTimeOffset observedAt);
```

A concrete implementation delegates all three operations to
`DoctorSourceHandoffComposer`. It does not select a Doctor state, evaluate the
snapshot, query a store, collect telemetry, persist candidates, or introduce a
source-specific result type.

## Authority partition

| Doctor family | Authoritative producer |
| --- | --- |
| `install_and_source_version` | Issue #67/#68 setup detection and supported-version result |
| `process_receiver_and_port` | Local Monitor process/readiness and owned-port observation |
| `source_effective_configuration` | persisted setup status and effective setting source |
| `endpoint_reachability` | bounded setup readiness probe |
| `protocol_and_signal_compatibility` | setup plan/status and source signal configuration |
| `source_version_and_schema_diagnostics` | persisted source-schema observation/compatibility result |
| `last_ingest` | ingestion acceptance/rejection record |
| `raw_persistence` | exact raw-record persistence evidence |
| `projection` | monitor projection state/error evidence |
| `exact_session_binding` | persisted Session exact-enrichment result |
| `completeness_and_content` | Session/source projection; content and raw access are separate |
| `restart_or_new_process` | setup result/status restart requirement |

Unknown remains unknown. A source implementation must not move a family to a
different authority or convert missing evidence into false, zero, supported,
successful, or absent.

## Composer

`DoctorSourceHandoffComposer` is a public static source-neutral helper.

### Direct evaluation

```csharp
DoctorFactSnapshot ComposeDirectEvaluation(
    string sourceSurface,
    string? expectedSourceAdapter,
    DateTimeOffset observedAt,
    DoctorSetupFactContribution setupFacts,
    DoctorRuntimeFactContribution runtimeFacts,
    IReadOnlyList<DoctorObservation> observations)
```

It creates `doctor.facts.v1` with:

- the supplied source and optional adapter;
- the supplied canonical `observed_at`;
- `verification_id = null`;
- the supplied ordered observations; and
- the five setup plus seven runtime families in canonical snapshot order.

### Persisted completion

```csharp
DoctorFactSnapshot ComposeVerificationCompletion(
    DoctorVerification verification,
    DateTimeOffset observedAt,
    DoctorSetupFactContribution setupFacts,
    DoctorRuntimeFactContribution runtimeFacts)
```

It accepts only a valid active verification and creates `doctor.facts.v1` with:

- `source_surface` copied from `verification.expected_source_surface`;
- `expected_source_adapter` copied from the verification;
- the verification ID copied exactly;
- `observations = []`; and
- the five setup plus seven runtime families in canonical snapshot order.

The empty observation rule is mandatory. Persisted completion accepts only
caller-selected opaque references; the existing store resolves candidates and
constructs trusted observations.

### Candidate composition and refresh boundary

```csharp
DoctorEvidenceCandidate ComposeCandidate(
    DoctorVerification verification,
    string candidateId,
    DoctorEvidenceClass evidenceClass,
    DoctorEvidenceKind evidenceKind,
    string evidenceRef,
    DateTimeOffset observedAt)
```

It accepts only a valid active verification and a candidate observed in the
half-open verification window:

```text
verification.started_at <= observed_at < verification.expires_at
```

It creates the existing `DoctorEvidenceCandidate` with:

- caller-supplied canonical UUIDv7 `candidate_id`;
- `verification_id` copied exactly from the verification;
- source surface and nullable adapter copied exactly from the verification;
- the supplied existing evidence class, evidence kind, and bounded opaque
  reference;
- the supplied canonical `observed_at`; and
- `expires_at` copied exactly from the verification.

The caller cannot override verification source, adapter, ID, or expiry. A
source-specific refresh first loads the exact persisted verification by its
opaque ID, observes source records, composes candidates through this method,
and persists them through the existing internal `ObserveCandidate` operation.
After a Local Monitor restart it reloads and reuses that same verification ID;
it never selects the latest verification, trace, or Session.

This method does not generate IDs, query evidence stores, deduplicate refresh
results, or persist candidates. Those source/store integration responsibilities
remain in #103/#104 and must preserve the existing candidate-count and evidence
uniqueness contract.

### Validation and error

All composer methods use the existing `DoctorValidation` contract. Null
contributions, invalid source/adapter/timestamp/verification, inactive
verification, candidate outside the verification window, invalid enum
combinations, source-mismatched observations, duplicate observations, or unsafe
evidence references throw:

```text
Source handoff produced an invalid Doctor fact snapshot.
```

The message contains no rejected value, raw content, path, payload, database
path, or underlying exception detail.

## Source identity scope

The first implementation coverage gate contains exactly these manifest-backed
surfaces:

| Issue | Source surface | Doctor expected adapter |
| --- | --- | --- |
| #103 | `github-copilot-vscode` | null |
| #103 | `github-copilot-cli` | null |
| #104 | `claude-code` | null |

The verification is surface-scoped because one first-trace journey may require
more than one concrete adapter, such as OTel plus Hook evidence. Candidates
created for this handoff therefore also use a null Doctor adapter. The exact
referenced source observation, raw record, and Session event retain their real
adapter provenance; the Doctor carrier does not rewrite those records.

For Claude Code this preserves D059: an OTel record whose stored adapter remains
`raw-otlp` may contribute exact native-session evidence without being promoted
to `claude-code-otel`.

GitHub Copilot App/SDK and Claude Agent SDK remain caller-managed setup targets.
This G0 contract does not invent a new manifest source surface or claim live
first-trace support for either target. Issues #103/#104 must represent their
availability honestly through the same twelve families.

## Evidence candidates

The candidate contract is unchanged:

- use only `DoctorEvidenceCandidate`;
- use only `real_source|synthetic_probe`;
- use only `ingest`, `raw_persistence`, `projection`,
  `exact_session_binding`, or `completeness_content`;
- compose through the shared candidate method;
- persist through the existing internal `ObserveCandidate` operation;
- preserve the verification source, null Doctor adapter, expiry, and bounded
  opaque reference; and
- require explicit caller selection at completion.

Latest verification, latest trace, latest Session, repository, workspace, cwd,
process identity, trace ID alone, and timestamp proximity are forbidden
selection inputs.

## Implementation placement

The Doctor core contains only the shared records, attribute, interface, and
composer. Concrete source implementations must live outside the
`CopilotAgentObservability.Doctor` assembly. G0 does not fix whether a concrete
implementation is placed in Config CLI or Local Monitor; it fixes the behavior
and discoverable interface only.

A concrete implementation:

- is non-abstract;
- implements `IDoctorSourceHandoff`;
- has one `DoctorSourceHandoffAttribute`;
- exposes one of the three manifest-backed surface values above;
- returns `ExpectedSourceAdapter = null` for this v1 handoff; and
- delegates direct, completion, and candidate composition to
  `DoctorSourceHandoffComposer`.

## Contract tests

`DoctorSourceHandoffContractTests` pins:

1. setup/runtime authority partition and canonical snapshot mapping;
2. direct observation preservation and null verification ID;
3. persisted completion identity and empty observations;
4. candidate inheritance of verification identity/source/adapter/expiry and
   the half-open observation window;
5. the fixed sanitized invalid-composition error;
6. absence of source-specific Doctor enums in the core assembly; and
7. three separately executable implementation gates:
   - `GitHubCopilotVsCodeSourceHandoff_IsImplementedOutsideDoctorCore`;
   - `GitHubCopilotCliSourceHandoff_IsImplementedOutsideDoctorCore`;
   - `ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore`.

At the G0-3 checkpoint, items 1 through 6 are GREEN. The three implementation
gates are intentionally RED. Issue #103 owns and turns the two GitHub Copilot
gates GREEN; Issue #104 independently owns and turns the Claude Code gate GREEN.
This split lets both worktrees verify their own production handoff without
weakening or editing the other Issue's expected row. Each RED is an assertion
failure, not a compile failure or unrelated test error.

## Security and non-regression

- No new dependency is added.
- No public Doctor route, command, state, result field, or storage schema is
  added.
- No prompt, response, tool body, source fragment, PII, credential,
  authorization value, absolute/local path, or raw payload is stored or emitted.
- D051 readiness, setup transaction behavior, ingestion, projection, source
  compatibility, and Session binding remain unchanged.
- Tests use synthetic metadata only and do not coordinate through sleep,
  polling, or retries.

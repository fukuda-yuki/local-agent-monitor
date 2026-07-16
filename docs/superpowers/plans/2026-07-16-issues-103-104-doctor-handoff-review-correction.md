# Issues #103/#104 Doctor Handoff Plan Review Correction

This correction supersedes the bundled source-implementation test, the narrow
invalid-input example, and the snapshot-only interface shown in
`2026-07-16-issues-103-104-doctor-handoff.md`. All other task boundaries,
commands, and constraints remain unchanged.

## Finding 1 — bundled implementation gate

The original plan required one test to observe all three manifest-backed
surfaces at once. After #103 implemented its two GitHub Copilot handoffs, that
test would still fail for the Claude Code handoff owned by #104. Conversely,
#104 could not make the test GREEN without #103. The test therefore obscured
parallel ownership and prevented either worktree from independently verifying
its own deliverable.

Replace the bundled gate with three owner-specific facts:

```csharp
[Fact]
public void GitHubCopilotVsCodeSourceHandoff_IsImplementedOutsideDoctorCore() =>
    AssertSourceHandoff("github-copilot-vscode");

[Fact]
public void GitHubCopilotCliSourceHandoff_IsImplementedOutsideDoctorCore() =>
    AssertSourceHandoff("github-copilot-cli");

[Fact]
public void ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore() =>
    AssertSourceHandoff("claude-code");
```

The common discovery scans the Doctor, Config CLI, and Local Monitor production
assemblies, rejects every concrete implementation in the Doctor assembly,
requires exactly one `DoctorSourceHandoffAttribute` on each implementation, and
requires exactly one registration for the requested surface.

## Finding 2 — incomplete invalid-input coverage

The canonical contract gives invalid source identity, source-mismatched
observation, invalid verification state, and unsafe evidence the same fixed
sanitized failure. The reviewed tests therefore cover:

- `UnsafeObservation_UsesFixedSanitizedError`;
- `InvalidSourceIdentity_UsesFixedSanitizedError`;
- `SourceMismatchedObservation_UsesFixedSanitizedError`; and
- `InactiveVerification_UsesFixedSanitizedError`.

All require the exact fixed message and verify that rejected values are not
echoed.

## Finding 3 — missing candidate carrier boundary

The original snapshot-only interface left source implementations free to
reconstruct `DoctorEvidenceCandidate` verification ID, source, adapter, expiry,
and observation-window rules independently. That contradicted the G0-2 goal of
fixing candidate collection and restart/reuse boundaries before #103/#104
parallelize.

The reviewed `IDoctorSourceHandoff` and `DoctorSourceHandoffComposer` therefore
add:

```csharp
DoctorEvidenceCandidate ComposeCandidate(
    DoctorVerification verification,
    string candidateId,
    DoctorEvidenceClass evidenceClass,
    DoctorEvidenceKind evidenceKind,
    string evidenceRef,
    DateTimeOffset observedAt);
```

The shared method:

- accepts only a valid active verification;
- requires `started_at <= observed_at < expires_at`;
- copies verification ID, source surface, nullable adapter, and expiry;
- validates the existing candidate class/kind/reference/UUID contract; and
- never generates IDs, queries or persists evidence, or selects a latest entity.

Shared tests pin successful inheritance and rejection at the exclusive expiry
boundary.

## Finding 4 — split gates must retain a closed registration set

Splitting the original exact-array assertion into three owner-specific tests
could allow an unknown fourth registration to pass unnoticed. The reviewed
shared test
`SourceHandoffRegistrations_AreUniqueManifestBackedAndOutsideDoctorCore`
therefore rejects:

- every concrete implementation in the Doctor assembly;
- duplicate source-surface registrations; and
- every registration outside `github-copilot-vscode`, `github-copilot-cli`, and
  `claude-code`.

## Finding 5 — original G0-3 evidence semantics were not explicit

The initial shared tests did not directly prove two requirements from the G0-3
brief:

1. an observation for another source cannot be retagged into the requested
   source; and
2. synthetic receiver/persistence/projection evidence cannot become a first
   real trace.

The reviewed test file adds source-mismatch rejection and a two-case theory for
`github-copilot-vscode` and `claude-code`. With otherwise ready facts and real
binding/completeness evidence, synthetic ingest/raw/projection evidence must
produce `ready_no_real_trace`, never `first_trace_ready`.

## Corrected G0 checkpoint

- Eleven shared test methods / twelve shared cases are expected GREEN.
- The two `GitHubCopilot*SourceHandoff` facts are intentionally RED and owned by
  #103.
- `ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore` is intentionally RED
  and owned by #104.
- A compile error or any unrelated failure is not an acceptable RED checkpoint.

## Corrected parallel handoff

- #103 may run and turn GREEN only its two named facts while #104 remains RED.
- #104 may run and turn GREEN only its named fact while #103 remains RED.
- Both Issues implement the expanded shared interface and delegate candidate as
  well as snapshot composition to `DoctorSourceHandoffComposer`.
- The full `DoctorSourceHandoffContractTests` class becomes GREEN only after the
  two implementation branches are integrated.
- Neither branch edits the expected surface values to suppress a test owned by
  the other Issue.

The executable test file and
`docs/specifications/interfaces/source-specific-doctor-handoff.md` are the
reviewed authority for this correction.

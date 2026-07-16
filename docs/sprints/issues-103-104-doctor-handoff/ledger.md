# Issues #103/#104 Doctor Handoff Ledger

This ledger records the G0-2 source-specific Doctor handoff contract and the
G0-3 cross-source RED contract-test checkpoint. Product behavior belongs in
`docs/specifications/interfaces/source-specific-doctor-handoff.md` and
`docs/specifications/interfaces/first-trace-doctor.md`.

## Execution identity

- Branch: `codex/issues-103-104-doctor-handoff-contract`
- Base branch: `main`
- Base commit: `920ff43a9ec63088a9cc109bcd15d0e6f4f9dc5c`
- Current reviewed branch commit before this ledger:
  `072a3eb4822c9b5750e9095ef2a9e2483b65f0d1`
- Pull request, merge, and Issue closure: not performed

## Commit sequence

| Commit | Purpose |
| --- | --- |
| `67770f785e3b0dac88164dc58d3be2398e8bfa06` | Record the approved source-handoff design and alternatives |
| `8b7efb5c90bc9028617e5d18b953c590419b467b` | Add the canonical source-specific Doctor handoff specification |
| `00fbbad1e2876ed6fc9ed5a684eb38b196762937` | Index the new canonical interface |
| `a868f581531bff6124371fd95b9ea676c4c79de7` | Record the TDD implementation plan and exact commands |
| `4383337bf2b7173ae69394f9de8b3458f6875044` | Add reflection-based tests before production handoff types |
| `87a7ca2985dd5fbabcdd3a26dcd38a5d683804b0` | Add the source-neutral contribution, discovery, interface, and composition types |
| `c869c39c18a8e3f7469b786e8643b213d7bb2e3b` | Make the implementation gate inspect the Doctor core as well as source assemblies and make missing-type failure explicit |
| `072a3eb4822c9b5750e9095ef2a9e2483b65f0d1` | Compare composed observations by value rather than depending on overload inference |

## G0-2 contract state

The branch fixes the following source-neutral boundary:

- five setup-owned fact families;
- seven runtime-owned fact families;
- direct composition with typed observations and no verification ID;
- persisted completion composition with exact active-verification identity and
  an empty caller-observation list;
- one discoverable `IDoctorSourceHandoff` interface and
  `DoctorSourceHandoffAttribute` without source-specific Doctor states;
- surface-scoped v1 verification for `github-copilot-vscode`,
  `github-copilot-cli`, and `claude-code`, with a null Doctor adapter while
  referenced source records retain their actual adapter provenance; and
- fixed sanitized invalid-composition failure text.

No public Doctor command, route, state, result field, storage schema, dependency,
UI, setup transaction, ingestion behavior, source compatibility behavior, or
Session binding behavior is changed.

## G0-3 test intent

`DoctorSourceHandoffContractTests` contains four shared-boundary tests and one
intentional source-implementation coverage test.

The intended post-G0 result is:

- `DirectComposition_MapsFixedAuthorityAndPreservesObservations`: GREEN;
- `VerificationComposition_UsesVerificationIdentityAndNoCallerObservations`:
  GREEN;
- `InvalidComposition_UsesFixedSanitizedError`: GREEN;
- `DoctorCoreDefinesNoSourceSpecificDoctorEnum`: GREEN; and
- `ManifestBackedSourceHandoffs_AreImplementedOutsideDoctorCore`: RED until
  Issue #103 provides `github-copilot-vscode` and `github-copilot-cli`, and
  Issue #104 provides `claude-code`.

This table records intended contract state only. No test result is claimed
without command output.

## Static review

A complete branch-file inventory against `main` found only:

- one specification-index line;
- the canonical handoff specification;
- the approved design and implementation plan;
- one Doctor source-neutral production file; and
- one Doctor test file.

Static review corrected three issues before this ledger:

1. The first discovery test scanned only Config CLI and Local Monitor, so its
   Doctor-core exclusion assertion was vacuous. It now scans all three
   assemblies and explicitly rejects any concrete implementation in the Doctor
   core.
2. Reflection type lookup now uses an explicit assertion and null-forgiving
   return, avoiding nullable-flow ambiguity.
3. Observation preservation compares materialized values, avoiding dependence
   on an assertion overload chosen from `IReadOnlyList<T>` and array inputs.

The reviewed source contains no placeholder, fallback, source-specific Doctor
enum, public candidate-write surface, sleep, polling, retry loop, raw content,
PII, credential, authorization value, or local path.

## Validation blocker

The active execution container has no `dotnet`, `pwsh`, `csc`, or `mcs`
executable. Outbound network and DNS are blocked, so the repository toolchain
and .NET SDK 10.0.203 cannot be installed or cloned into this container. The
repository's only discovered GitHub Actions workflow is manual
`workflow_dispatch`; the active GitHub connector exposes no workflow-dispatch
action. Therefore the following required commands have **not** been run in this
execution environment:

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorSourceHandoffContractTests
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

No RED count, GREEN count, successful build, Playwright bootstrap, or full-suite
result is claimed. The branch must not be merged until a repository-capable
Windows/.NET environment executes these exact commands and confirms that the
only intended test failure is
`ManifestBackedSourceHandoffs_AreImplementedOutsideDoctorCore`.

## Handoff

- Issue #103 implements and annotates the `github-copilot-vscode` and
  `github-copilot-cli` handoffs outside the Doctor assembly.
- Issue #104 implements and annotates the `claude-code` handoff outside the
  Doctor assembly.
- Each implementation returns a null expected Doctor adapter for this v1
  surface-scoped handoff and delegates composition to
  `DoctorSourceHandoffComposer`.
- Neither Issue edits the shared test merely to weaken the expected surface
  list. Each turns its owned missing rows GREEN through production code and
  source-specific tests.
- After all three rows are present, the focused test, build, Playwright
  bootstrap, and full solution tests must all pass before integration.

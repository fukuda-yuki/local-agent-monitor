# Issues #103/#104 Doctor Handoff Ledger

This ledger records the G0-2 source-specific Doctor handoff contract and the
G0-3 cross-source RED contract-test checkpoint. Product behavior belongs in
`docs/specifications/interfaces/source-specific-doctor-handoff.md` and
`docs/specifications/interfaces/first-trace-doctor.md`.

## Execution identity

- Branch: `codex/issues-103-104-doctor-handoff-contract`
- Base branch: `main`
- Base commit: `920ff43a9ec63088a9cc109bcd15d0e6f4f9dc5c`
- Current reviewed branch commit before this ledger update:
  `ef1946d16420638afcc1578dd58f7fa6e75fb4b5`
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
| `f61c669ed2d5dc2932bad98a05a54e4fb02496fb` | Record the initial G0 state and unavailable validation commands |
| `4b60c130c2fd5581f6270808389d49355f0b3a0f` | Split the bundled source-implementation RED into three independently owned tests |
| `9db24b92a191fc82478bf13fef370cd32c16ee1c` | Align the canonical specification with the parallel RED ownership |
| `6048c13033717be75da00f50435f2c5af618b356` | Align the approved design with the three parallel implementation gates |
| `116c37e8214aef0ad20aacd64d67c4748a24a32f` | Record the implementation-plan correction for parallel ownership |
| `d8fc9da4a3af08111be2281233ba4c200d4ae317` | Cover invalid source identity and inactive verification with the fixed sanitized error |
| `af0d0aab9cb6a71607fab530285fda3b6789f653` | Align the plan correction with the reviewed six shared tests |
| `a61e6b2e4a0b771e392832688cc3ee49d9ad0399` | Record the reviewed parallel G0 state |
| `461a9546782f2a642b3fe9eebddb4e835daa6bd5` | Add candidate-scope and half-open verification-window RED tests |
| `d49eb2d198f4a1502737158207909add3f916a43` | Add verification-scoped candidate composition to the shared interface/composer |
| `956f5d028ecd565f29941fbca689d30f8f1014c0` | Promote candidate refresh/restart semantics to the canonical handoff specification |
| `a6f9390c5a834705558b8ea2ccef8d98c05dbd4b` | Align the approved design with candidate composition and refresh scope |
| `8395989dcfde0c9b3760af2e8bbf3ee872d52012` | Record the candidate-boundary implementation-plan correction |
| `8dbcbe923fb42f108dffd02264f5c941439b9b65` | Record the reviewed candidate handoff state |
| `94525d73deea8137748b797d9e4ffe1ed5522788` | Preserve the closed, unique manifest-backed registration set after RED splitting |
| `a5fa715c450659759cc7b0487622112fdd886c5a` | Pin the registration allowlist in the canonical handoff specification |
| `588fff33f0c35eeeb09b0b96b4d4883690ced775` | Align the approved design with the registration allowlist gate |
| `96c9d936c6c3b4f52651a6849b0dadd718ff7914` | Align the implementation-plan correction with nine shared tests |
| `c8fdee0cf8376b44d1ded24e496b6bd4ff5c4495` | Record the closed registration review state |
| `4a439abef3772c5551ab002da45321a4f010f8b8` | Add source-mismatch rejection and cross-source synthetic-evidence semantics |
| `0acc935d4fba6802812aa5b1934a174a5c3cf668` | Promote source-mismatch and synthetic-evidence semantics to the canonical contract |
| `d14cffbc6a84b4c7a53f6d39db54cd0feb2903f1` | Align the approved design with eleven shared test methods/twelve cases |
| `c9f3e99d4f86889a7ccf5d5b94be119213690f33` | Finalize the implementation-plan review correction |
| `5cd2f697a0cc24769c58fe86c3efda5bcb991c6f` | Record the cross-source G0 review state |
| `9eb26a52939855847d6524e965e8fa92b7d58cee` | Verify registered runtime source/adapter identity through a stateless wrapper |
| `ef1946d16420638afcc1578dd58f7fa6e75fb4b5` | Pin parameterless construction and attribute/runtime identity in the canonical contract |

The original implementation-plan code block bundled all three surfaces into
one test, showed one invalid-input example, and exposed only snapshot
composition. The reviewed executable test, canonical specification, and
plan-correction record supersede those details.

## G0-2 contract state

The branch fixes the following source-neutral boundary:

- five setup-owned fact families;
- seven runtime-owned fact families;
- direct composition with typed observations and no verification ID;
- persisted completion composition with exact active-verification identity and
  an empty caller-observation list;
- candidate composition that copies verification ID/source/adapter/expiry and
  accepts only `started_at <= observed_at < expires_at`;
- one discoverable `IDoctorSourceHandoff` interface and
  `DoctorSourceHandoffAttribute` without source-specific Doctor states;
- a unique v1 registration allowlist containing only
  `github-copilot-vscode`, `github-copilot-cli`, and `claude-code` outside the
  Doctor assembly;
- parameterless stateless wrappers whose runtime `SourceSurface` equals their
  attribute and whose expected Doctor adapter is null;
- surface-scoped v1 verification with a null Doctor adapter while referenced
  source records retain their actual adapter provenance;
- exact verification-ID reuse after restart, without latest-verification,
  latest-trace, or latest-Session selection;
- rejection of source-mismatched observations without retagging; and
- fixed sanitized invalid-composition failure text.

No public Doctor command, route, state, result field, storage schema, dependency,
UI, setup transaction, ingestion behavior, source compatibility behavior, or
Session binding behavior is changed.

## G0-3 test intent

`DoctorSourceHandoffContractTests` contains eleven shared test methods, yielding
twelve shared cases because the synthetic-evidence test runs for GitHub Copilot
and Claude Code, plus three independent source-implementation tests.

The intended post-G0 result is:

- `DirectComposition_MapsFixedAuthorityAndPreservesObservations`: GREEN;
- `VerificationComposition_UsesVerificationIdentityAndNoCallerObservations`:
  GREEN;
- `CandidateComposition_CopiesVerificationScopeAndExpiry`: GREEN;
- `CandidateOutsideVerificationWindow_UsesFixedSanitizedError`: GREEN;
- `UnsafeObservation_UsesFixedSanitizedError`: GREEN;
- `InvalidSourceIdentity_UsesFixedSanitizedError`: GREEN;
- `SourceMismatchedObservation_UsesFixedSanitizedError`: GREEN;
- `InactiveVerification_UsesFixedSanitizedError`: GREEN;
- `SyntheticEvidence_DoesNotSatisfyFirstTraceReadyAcrossSources`: GREEN for
  `github-copilot-vscode` and `claude-code`;
- `DoctorCoreDefinesNoSourceSpecificDoctorEnum`: GREEN;
- `SourceHandoffRegistrations_AreUniqueManifestBackedAndOutsideDoctorCore`:
  GREEN;
- `GitHubCopilotVsCodeSourceHandoff_IsImplementedOutsideDoctorCore`: RED until
  Issue #103 provides `github-copilot-vscode`;
- `GitHubCopilotCliSourceHandoff_IsImplementedOutsideDoctorCore`: RED until
  Issue #103 provides `github-copilot-cli`; and
- `ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore`: RED until Issue
  #104 provides `claude-code`.

Each source implementation gate also constructs the registered wrapper and
requires runtime source identity to equal its attribute plus
`ExpectedSourceAdapter = null`.

This table records intended contract state only. No test result is claimed
without command output.

## Parallel execution boundary

Issue #103 owns only the two `GitHubCopilot*SourceHandoff` RED tests. Issue #104
owns only `ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore`. Each lane
can run its owned methods while the other lane remains intentionally RED. The
full class becomes GREEN only after integration of both Issues.

Source-specific implementations must not edit the shared expected surface
values merely to suppress another lane's RED. They satisfy the tests by adding
concrete, annotated, parameterless production wrappers outside the Doctor
assembly and implementing all three shared composition operations.

## Static review

A complete branch-file inventory against `main` found only:

- one specification-index line;
- the canonical handoff specification;
- the approved design, implementation plan, and reviewed plan correction;
- one Doctor source-neutral production file;
- one Doctor test file; and
- this durable ledger.

Static review corrected ten issues before this ledger update:

1. The first discovery test scanned only Config CLI and Local Monitor, so its
   Doctor-core exclusion assertion was vacuous. It now scans all three
   assemblies and explicitly rejects any concrete implementation in the Doctor
   core.
2. Reflection type lookup now uses an explicit assertion and null-forgiving
   return, avoiding nullable-flow ambiguity.
3. Observation preservation compares materialized values, avoiding dependence
   on an assertion overload chosen from `IReadOnlyList<T>` and array inputs.
4. A single test originally required all three source implementations. That
   would keep both parallel worktrees RED for work owned by the other Issue.
   The test is now split into two #103 facts and one #104 fact, with common
   discovery and Doctor-core exclusion logic.
5. The fixed invalid-composition error was initially covered only for an unsafe
   evidence reference. The reviewed test also pins invalid source identity,
   source mismatch, and inactive verification without echoing rejected values.
6. Snapshot-only composition left candidate verification ID, source, adapter,
   expiry, and observation-window rules to source-specific reconstruction. The
   reviewed interface/composer now owns those fields and the half-open window,
   while ID generation, source collection, deduplication, and persistence
   remain #103/#104 responsibilities.
7. Splitting the exact surface assertion could have permitted an unknown fourth
   registration. A shared GREEN test now preserves the closed allowlist,
   uniqueness, and Doctor-core exclusion independently of the three owner RED
   tests.
8. The original tests did not prove that a source-mismatched observation is
   rejected rather than retagged. A dedicated test now fixes that boundary.
9. The original tests did not directly prove that synthetic health evidence
   cannot become first real trace success. A two-source theory now requires
   `ready_no_real_trace` for otherwise-ready GitHub Copilot and Claude Code
   snapshots backed by synthetic ingest/raw/projection observations.
10. Attribute discovery alone could not detect a runtime `SourceSurface` or
    expected-adapter mismatch. Each owner gate now constructs the stateless
    wrapper and checks exact identity plus null adapter.

The reviewed source contains no placeholder, fallback, source-specific Doctor
enum, public candidate-write surface, sleep, polling, retry loop, real raw
content, PII, credential, authorization value, or local path.

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
eleven shared test methods/twelve cases are GREEN and the only intended
failures are the three source-implementation tests listed above.

## Handoff

- Issue #103 implements and annotates the `github-copilot-vscode` and
  `github-copilot-cli` handoffs outside the Doctor assembly.
- Issue #104 implements and annotates the `claude-code` handoff outside the
  Doctor assembly.
- Each parameterless wrapper returns a runtime source equal to its attribute,
  returns a null expected Doctor adapter, and delegates direct, completion, and
  candidate composition to `DoctorSourceHandoffComposer`.
- A refresh reloads the exact active verification by ID after restart and
  composes candidates only from records observed inside that verification
  window. It does not select a latest entity.
- Neither Issue edits the shared test merely to weaken the expected surface
  values. Each turns only its owned RED methods GREEN through production code
  and source-specific tests.
- After all three methods are GREEN, the focused test, build, Playwright
  bootstrap, and full solution tests must all pass before integration.

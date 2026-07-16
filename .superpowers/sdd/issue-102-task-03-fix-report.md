# Issue #102 Task 3 Fix Report — Domain Validation Gaps

## Identity and Scope

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability\.worktrees\issue-102-domain`
- Branch: `codex/issue-102-domain`
- Starting HEAD: `e00063faf82e92ab8186b79b78c9a6baa6085af5`
- The worktree, branch, and HEAD matched the fix brief before editing.
- Production changes are limited to `DoctorJson.cs` and `DoctorValidation.cs`.
- Test changes are limited to the owned Doctor evaluator and validation tests.
- No specification, CLI, HTTP, persistence, cross-surface test, project file, dependency, ledger, review artifact, push, PR, or integration was changed.

## Finding Disposition

| Finding | Disposition | Evidence |
| --- | --- | --- |
| I-1 unsafe URI/local-path evidence | Resolved | The evidence guard rejects every RFC3986 scheme prefix, slash/backslash path form, standalone current/parent/home marker, control, and the existing credential/PII matrix before evaluation can emit a state. The negative matrix includes `file:relative/trace.json`, `urn:doctor:evidence`, current/parent/home forms in both slash styles, rooted and drive-relative forms, and no-echo assertions. Opaque references at lengths 1 and 128 remain accepted. |
| I-2 omitted required JSON properties | Resolved | Strict fact parsing now requires all 16 required root properties, all 21 family members, and all 5 required observation members. Explicit null remains accepted for each of the 12 families, and explicit enum `unknown` remains accepted. Duplicate, unknown-property, noncanonical enum/timestamp, malformed, and sanitized-error behavior is unchanged. |
| I-3 impossible verification revisions | Resolved | Active and effective-expired projections require revision 1; completed and cancelled projections require revision 2. Existing terminal timestamp, nullability, window, and accepted-reference invariants remain enforced. |
| M-1 incomplete boundary/unknown tests | Resolved | Tests now pair 16/17 observation counts, 1/128 versus empty/129/control evidence lengths, and independently exercise explicit `Unknown` for each of the 12 families with the exact ordered missing-family and fixed partial projection. |

## TDD RED/GREEN Evidence

1. Unsafe evidence references
   - RED: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorValidationTests --no-restore`
   - Observed 13 failures: the evaluator returned `evaluation_completed` for the newly added scheme and relative/local path cases.
   - GREEN after the coherent scheme/separator/marker guard: 41 passed, 0 failed.
2. Strict required-property parsing
   - RED: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter "FullyQualifiedName~DeserializeFactSnapshot_OmittedRequired" --no-restore`
   - Observed 37 failures covering every required root property and every required family member because no exception was thrown.
   - GREEN after shape validation, including required observation coverage and explicit null/unknown controls: the `DeserializeFactSnapshot` slice passed 44/44 before the later exhaustive null-family expansion; the final validation class passed 98/98.
3. Verification state/revision combinations
   - RED: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~Validation_VerificationState --no-restore`
   - Observed failure: active revision 2 was accepted; the same test also pins expired revision 2, completed revision 1, and cancelled revision 1 as invalid.
   - GREEN: 1 passed, 0 failed after state-specific revision enforcement.
4. Boundary and explicit-unknown hardening
   - Test-first confirmation: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter "FullyQualifiedName~ObservationCount|FullyQualifiedName~EvidenceReferenceLength|FullyQualifiedName~EachExplicitUnknownFamily" --no-restore`
   - GREEN immediately: 3 passed, 0 failed, confirming the existing numeric comparisons and per-family evaluator semantics were already correct. No production change was manufactured for this Minor test-only finding.

## Counterexample and Cross-Surface Checks

- `rg -n -F` confirmed the exact review counterexamples and both named omitted family/member targets are present in the owned regression matrix.
- `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter "FullyQualifiedName~Evaluate_UnsafeEvidenceReference|FullyQualifiedName~DeserializeFactSnapshot_OmittedRequired|FullyQualifiedName~DoctorCrossSurfaceContractTests" --no-restore`
  - PASS: 77 passed, 0 failed, 0 skipped.
  - This includes the unchanged Task 2 direct/real CLI/real HTTP cross-surface contract.

## Exact Final Verification

Fresh commands were run after the last production/test edit:

1. `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter "FullyQualifiedName~DoctorCatalogTests|FullyQualifiedName~DoctorEvaluatorTests|FullyQualifiedName~DoctorValidationTests|FullyQualifiedName~DoctorDeterminismTests"`
   - PASS: 136 passed, 0 failed, 0 skipped.
2. `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj`
   - PASS: 137 passed, 0 failed, 0 skipped.
3. `dotnet build CopilotAgentObservability.slnx`
   - PASS: 0 warnings, 0 errors.
4. `git diff --check`
   - PASS: exit 0, no output.
5. `git status --short --branch --untracked-files=all`
   - Branch remained `codex/issue-102-domain`; only the four owned source/test edits and the supplied/untracked SDD artifacts were present before this report was added.

The SDK emitted informational `NETSDK1057` preview-support-policy messages; the build summary contained zero warnings and zero errors.

## Self-Review

- I-1: the policy is source-neutral and bounded; it performs no normalization or filesystem access, preserves opaque references, and returns only the fixed invalid result without evidence projection.
- I-2: required-presence checking is case-sensitive, occurs after duplicate detection and before deserialization, applies only to fact snapshots, and preserves the canonical distinction between omission, family null, and enum unknown.
- I-3: revision constraints are in the existing per-state invariant switch and do not loosen timestamps, accepted references, or expiry windows.
- M-1: every requested inclusive/exclusive bound and all twelve explicit-unknown families have executable assertions.
- Scope and purity: the diff adds no clock, environment, process, network, database, filesystem, sleep, polling, retry, fallback parser, compatibility shim, dependency, or source-specific rule.

Verdict: all three Important findings and the Minor finding are resolved within Task 3 ownership. No unresolved item remains in this fix lane.

## Re-review Follow-up — Disguised Unsafe References

- Re-review starting HEAD: `4c90d52de919ef22f9d444ab6fd865ad18c6c5be`.
- Remaining finding I-1R was verified: the anchored URI regex allowed a leading-space `urn:` value and an embedded `urn:` value to reach `evaluation_completed` and state evidence.
- Scope remained limited to `DoctorValidation.cs`, `DoctorValidationTests.cs`, and this fix report.

TDD evidence:

1. Tests were added first for leading/trailing whitespace around file/URN and otherwise-valid opaque values, a tab-prefixed file value, embedded/surrounded URI forms, and embedded/surrounded slash/backslash path forms.
2. RED command: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~Evaluate_UnsafeEvidenceReference --no-restore`.
   - Observed 4 failures: ` event-ref`, `event-ref `, ` urn:doctor:evidence`, and `prefix urn:doctor:evidence suffix` all returned `evaluation_completed` instead of `invalid_input`.
   - The path-bearing additions already rejected through the prior separator rule, confirming the remaining bypass was whitespace/scheme detection rather than path emission.
3. Minimal production fix:
   - reject any evidence reference whose leading/trailing whitespace would change under `Trim()`;
   - run scheme, unsafe-content, and separator checks against the normalized value;
   - detect RFC3986 scheme tokens anywhere in the accepted string;
   - never substitute normalized text into a state or result.
4. GREEN rerun: 45 passed, 0 failed, 0 skipped.

Fresh follow-up verification after the final production/test edit:

1. Focused Task 3 tests: 147 passed, 0 failed, 0 skipped.
2. Full Doctor test project: 148 passed, 0 failed, 0 skipped.
3. Exact unsafe/omission/revision/boundary/unknown/cross-surface slice: 92 passed, 0 failed, 0 skipped.
4. `dotnet build CopilotAgentObservability.slnx`: 0 warnings, 0 errors.
5. `git diff --check`: exit 0, no output before this report update.
6. Status showed only the two authorized source/test edits and the supplied untracked fix/review artifacts.

Follow-up self-review found no alternate whitespace, embedded-scheme, or separator path that can pass the shared evidence guard. Valid opaque references without surrounding whitespace, including the pinned 1- and 128-character boundaries, remain accepted. I-1R is resolved with no remaining Task 3 finding.

## Intentionally Unverified Later Handoffs

Task 4 persistence, Task 5 lifecycle CLI, Task 6 lifecycle HTTP, Task 7 full integration/security, Issues #103/#104 source producers, and Issue #105 UI remain outside this fix scope. Playwright bootstrap and the full solution test suite were not required by the fix brief and are not represented as completion evidence.

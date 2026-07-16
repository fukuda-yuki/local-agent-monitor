# Issue #102 Task 1 Independent Re-review Report

## Identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability`
- Branch: `codex/issue-102-doctor-core`
- Review HEAD: `0a2542d9e973429e39a4986c823996b057fb607b` plus the corrected uncommitted Task 1 diff
- Identity result: matched the re-review dispatch before inspection and again
  before reporting.
- Review mode: read-only except for replacing this report. No canonical file,
  production file, test, ledger, plan, implementer report, commit, push, PR, or
  issue state was changed.

## Reviewed evidence

I re-read the updated implementer report, the actual complete tracked canonical
diff, and the complete untracked
`docs/specifications/interfaces/first-trace-doctor.md`. I re-applied the
`spec-update` source-of-truth checks across requirements, system specification,
detailed interface, Config CLI interface, architecture, and D060 rather than
accepting the implementer report as evidence.

The full review was repeated against the approved design/plan, existing D051
and D059 decisions, Local Monitor security/readiness contracts, SQLite
component/migration behavior, and #103/#104/#105 ownership boundaries. The
existing security and telemetry-ingestion specifications have no uncommitted
diff; the new Doctor interface retains their loopback, Host-header,
same-origin/CSRF, no-store, sanitized-output, and readiness isolation rules.

Mechanical checks found exactly 12 fact-family rows, 20 state-catalog rows, 5
CLI commands, and 5 HTTP routes. The 20 state rows exactly match the approved
state/severity/retryability/next-action tuples.

## Prior Important finding disposition

| Prior finding | Re-review result | Corrected evidence |
| --- | --- | --- |
| I-1 unscoped store failure code | RESOLVED | The fixed catalog now uses `doctor_store_busy` and `doctor_store_unavailable` at `docs/specifications/interfaces/first-trace-doctor.md:323`-324; CLI exit 5, HTTP 503, newer-schema rejection, requirements, architecture, and D060 use the same scoped tokens. A negative PCRE search found no unscoped `store_busy` / `store_unavailable` token in the seven canonical files. |
| I-2 direct evaluation lacked real/synthetic typing | RESOLVED | `DoctorFactSnapshot` now carries typed observations at line 67. `DoctorObservation` fixes class/kind at lines 104-122. Persisted candidates remain separate, and complete callers provide references only while the store/service resolves trusted observations at lines 128-146 and 391-395. |
| I-3 `unknown` triggered `session_unbound` | RESOLVED | Lines 198-201 restrict `session_unbound` to known `unbound`, send required-plus-unknown to partial, and reject required-plus-not-applicable. |
| I-4 partial wire shape was ambiguous | RESOLVED | Lines 253-260 fix `success=false`, code, non-null evaluation, null primary, empty states, ordered nonempty missing families, and direct/complete verification projections. Lines 276 and 337-339 align DTO nullability and other-error behavior. |
| I-5 blockers emitted advisories contrary to the plan | RESOLVED | Lines 231-244 now emit blockers only when any blocker applies; otherwise they emit one terminal followed by fixed applicable advisories. Requirements, `docs/spec.md`, and D060 mirror that order. |

## Requirement-by-requirement verdict

| Contract family | Verdict | Evidence / rationale |
| --- | --- | --- |
| Source-of-truth placement and seven-file scope | PASS | Product outcome is in `docs/requirements.md:58`, cross-cutting behavior in `docs/spec.md:154`, detail in the indexed Doctor interface, CLI behavior in `config-cli.md`, dependency direction in architecture, and policy in D060. No production/test/user-guide/ledger scope was added. |
| Shared DTO/producer/consumer ownership and dependency direction | PASS | `docs/architecture.md:125`-157 and 211-228 keep Doctor below persistence and adapters; direct typed observations and persisted candidates have distinct carriers; Config CLI/HTTP project one already-evaluated result. |
| Twelve explicit-known/unknown fact families | PASS | Exactly 12 rows at `first-trace-doctor.md:78`-89. Null family and explicit unknown semantics are fixed at lines 70-74; unknown is never converted to a negative/default at line 57. |
| Exact twenty-state catalog | PASS | Exactly 20 rows at lines 155-174; all state/severity/retryability/next-action tuples match the approved table, and v1 reason equality is fixed at lines 176-178. |
| Blocking/terminal/advisory order and primary state | PASS | Lines 231-244 specify blocker-only output or terminal-then-advisory output, numeric/fixed order, and primary selection without fallback interpretation. |
| Partial input and fixed wire shape | PASS | Lines 246-260 fix when partial applies, missing-family order, every partial DTO field, and unchanged active verification behavior for partial completion. CLI exit 3 and HTTP 422 are aligned. |
| Synthetic/real separation | PASS | Lines 104-146 define typed direct observations and trusted persisted-candidate resolution. Lines 206-218 require matching real-source kinds for readiness; synthetic evidence cannot establish real receipt, binding, or completeness. |
| Approved limits, UUIDv7/timestamp form, and no-leak boundary | PASS | Lines 34-53 pin token grammar, lowercase UUIDv7, canonical UTC timestamp, 128-character refs, 16 direct observations/accepted refs, 100 candidates, 64 KiB inputs, and 1..30-minute windows. Lines 435-440 and 496-500 retain the no-raw/PII/path/credential/exception boundary. |
| Five CLI commands, strict parsing, JSON/human projection, exit mapping | PASS | Exactly five commands at lines 378-388. Lines 391-412 pin strict input, empty caller observations on complete, trusted resolution, sanitized output, and exits 0/2/3/4/5 with Doctor-scoped store codes. `config-cli.md:95`-146 is consistent. |
| Five HTTP routes, status mapping, no-store, loopback/Host/same-origin/CSRF | PASS | Exactly five routes at lines 419-423. Lines 426-457 pin request shapes, 64 KiB JSON, security/no-store/no-leak controls, and `200/201/400/404/409/410/422/503/500`. |
| Doctor v1 schema ownership and candidates | PASS | Lines 459-500 define the separate Doctor component, lifecycle/candidate columns, fixed class/kind, accepted ordinal, constraints, and sanitized storage without changing monitor/session versions. |
| CAS, atomicity, historical v1-v4 migration/reopen/rollback, degradation | PASS | Lines 341-371 define lifecycle/CAS/expiry/trusted completion; lines 502-520 pin atomic transitions, fault seams, exact rollback, monitor v1-v4 to v5 plus Doctor v1, reopen/idempotence/integrity, newer-schema rejection, and verification-only degradation. |
| D051 readiness non-regression | PASS | Lines 24-28 and 517-520 preserve host construction, ingestion/projection, stateless evaluation, and the existing readiness checks/reasons/thresholds/config/body/status. |
| D059 exact-binding behavior | PASS | Lines 220-223 keep unrelated schema drift advisory and non-blocking for otherwise exact verification. |
| #103/#104 producer handoff and #105 non-ownership | PASS | Lines 124-146 provide shared fact/observation/candidate carriers without source-specific enums. Lines 522-528 leave live source adapters/evidence to #103/#104 and proxy/Razor/JavaScript/Canvas/UI to #105 without facsimiles. |
| Unnecessary scope, compatibility behavior, contradictions, testability | PASS | No production/UI/dependency/main-integration scope, fallback parser, compatibility shim, downgrade, heuristic selection, sleep/poll/retry test guidance, placeholder, or source-specific state vocabulary was added. The corrected contracts are directly testable through fixed DTOs, ordering, clocks, barriers, checkpoints, and controlled locks. |

## Findings

No Critical, Important, or Minor findings remain in the corrected Task 1
canonical specification diff. The five prior Important findings are resolved as
shown above; no new ambiguity or regression was found.

## Command results

- `git branch --show-current` — exit 0; `codex/issue-102-doctor-core`.
- `git rev-parse HEAD` — exit 0;
  `0a2542d9e973429e39a4986c823996b057fb607b`.
- `git diff --check` — exit 0, no output for tracked changes.
- `git diff --no-index --check -- NUL docs/specifications/interfaces/first-trace-doctor.md`
  — exit 1 only because the untracked file differs from `NUL`; no whitespace
  diagnostic was emitted.
- `rg -n "doctor.v1|/api/doctor|first_trace_ready|D060" docs` — exit 0;
  expected canonical and approved design/plan matches were present.
- `rg --pcre2 -n '(?<!doctor_)store_(busy|unavailable)' <seven canonical files>`
  — exit 1, no unscoped fixed token found.
- Structural PowerShell check — `fact_families=12`,
  `state_catalog_rows=20`, `cli_commands=5`, `http_routes=5`.
- Exact catalog tuple comparison — `catalog_mapping_exact=true`.
- `git diff -- docs/specifications/security-data-boundaries.md docs/specifications/layers/telemetry-ingestion.md`
  — exit 0, no diff.
- Targeted D051/D059/security/migration/#103/#104/#105 search — exit 0; all
  required boundary clauses were present in the corrected canonical sources.
- `git status --short --branch` before replacing this report — exit 0; expected
  six tracked canonical modifications plus the untracked Doctor interface and
  SDD brief/report artifacts, with no production/test/runtime artifact.

Build, Playwright bootstrap, and solution tests were intentionally not run for
this specification-only Task 1 re-review. They are not substitutes for the
required canonical checks and remain later implementation/full-validation work.

## Intentionally unverified later-Issue interfaces

- Issue #103 GitHub Copilot source-specific fact/observation/candidate
  production and live first-trace evidence remain unimplemented and unverified.
- Issue #104 Claude Code source-specific fact/observation/candidate production,
  exact evidence, and live first-trace behavior remain unimplemented and
  unverified.
- Issue #105 proxy DTO, Razor/JavaScript/Canvas/UI workflow, and live UI
  validation remain unimplemented and unverified.
- No executable shared DTO equality, evaluator, CLI, HTTP, SQLite
  migration/atomicity/concurrency, or D051 byte-regression behavior exists yet;
  those are intentionally later implementation-plan tasks.

## Final verdict

**PASS** — 0 Critical, 0 Important, 0 Minor findings. The corrected Task 1
canonical specification diff is complete, internally consistent,
implementation-ready, scoped to Issue #102, and approved for the root agent's
next planned step.

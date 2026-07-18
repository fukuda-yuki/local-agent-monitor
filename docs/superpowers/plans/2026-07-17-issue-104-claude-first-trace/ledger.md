# Issue #104 durable ledger

Continuously updated by the orchestrator after every delegation, review, and
integration step. Trust this file plus `git log` over conversation memory.

- Kickoff: `main@920ff43`, branch `claude/issue-104-implementation-447584`,
  worktree `.claude/worktrees/issue-104-implementation-447584`.
- Delegation default: Codex GPT-5.6 Luna, reasoning xhigh; reviewers are
  separate fresh runs.

## Task states

| Task | State | Commits | Focused tests | Full test | Review |
| --- | --- | --- | --- | --- | --- |
| T0 docs/specs/contract table | complete | b43ed58, f012db9, 328e29d, 939e6f4, 5628323, 400a917, 8d6c669, f333d94 | n/a (docs) | — | Codex Luna xhigh review cycles resolved in the review log; contract remains normative and the T9 documentation closeout is complete |
| T1 red cross-surface test | complete | 3700d6b, a1e5db2, 3576421, 5838cc4, 54d758a | cross-surface 7/7 at frozen candidate | — | Red-to-green acceptance evidence retained through T7/T8; the T9 documentation closeout is complete |
| T2 fact mapper | done | b6e5bbf, d21fbb4 | mapper 73/0; build clean | — | review #1 REVISE (3 findings) → fixed in d21fbb4; re-review APPROVE (3/3, scope+purity confirmed) |
| T3 store reads | complete | 07f44c1, cb2db6f, 7b0be26 | Store 58/0, AppService 9/0, migration 12/12; frozen Doctor suite 260/260 | frozen full rerun included in T9 evidence below | Luna xhigh review findings (atomic start and expiry boundary) resolved; the T9 documentation closeout is complete |
| T4 binding rule + observer | complete | 6270a83, 1ed3e12, eb00ac5 | Observer 10/0, Doctor 252/0 after fix | frozen full rerun included in T9 evidence below | re-review: 4/4 RESOLVED, scope confirmed; the low-strength expiry-boundary assertion is accepted as a test-quality note (disposition below) |
| T5 fact collector | done | 01f0e4a (orchestrator-committed; 97/97 re-verified), 9a78afe, 079686e | fact suite 127/0 | solution 5728/0 by fix-2 run | review #1 REVISE (7) → 9a78afe → re-review (3 remained) → 079686e → combined re-review APPROVE (A2/A6/A7, OpenReadOnly invariant confirmed) |
| T6 first-trace verbs + handoff | done | e1a4a56, f640812, a83334f, 8d6c669 (spec.md), 7b0be26, 521b7d6 | FirstTrace 43/0, ConfigCli 4026/0, store 63/0 | solution 5727/0 + build + Playwright by implementer (pre-fix); post-fix full run at T7/T9 gates | review #1 REVISE (4 Imp + 2 Min) → fixes + orchestrator spec.md alignment → combined re-review APPROVE (B1-B5, Copilot path diff-free) |
| T4b runtime-state row | done | 52e0e57, 112962e, ffd4ae2 | new 4/4, LM migration 102/102, fail-fast 1/1, DoctorMigration 12/12 after ffd4ae2 | — | review #1 REVISE (1 Important fail-fast unpinned) → spec pinned + test 112962e; regression: monitor v6 bump broke 12 Doctor.Tests migration expectations (brief omitted Doctor.Tests rerun — orchestrator error, logged), fixed in ffd4ae2 via production version constant |
| T-109 projection defect fix | done | 09978b8, c807c7a, 8f4b6b7, d062f48 | SPS 5/0, Enrich 19/0, MigrationFixture 72/0, LM 1476/0, Doctor 255/0 | per-suite full by implementer | review APPROVE (no findings; exact/explicit-only projection, legacy NULL degrade, v12 migration sound, wire unchanged). No backfill: reach-reason not reconstructible from persisted data |
| T7 cross-surface green | complete | 3700d6b, a1e5db2, 5838cc4, 6de19b6, 66b7bff | `ClaudeFirstTraceCrossSurfaceTests` 7/7; `FirstTraceCliTests` 37/37 | frozen full rerun included in T9 evidence below | Initial review REVISE findings were resolved; focused post-repair review found no further issue; the T9 documentation closeout is complete |
| T8 104-E matrix | complete | 5799958, 3576421, a4ff479, 91e4802, f333d94, 5838cc4, 6de19b6, 66b7bff, 54d758a | matrix covered; cross-surface 7/7; `git diff --check` clean before T9 docs | frozen full rerun included in T9 evidence below | Terra high bounded privacy-evidence review APPROVE; the T9 documentation closeout is complete |
| T9 freeze + handoff | complete | frozen product candidate `54d758a260f347cc31a3191d342ad509eb62d81f`; finalized documentation-only closeout | `MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary` (false/true) 2/2; validation evidence below | build/install/full-test evidence below | Terra high review #1 REVISE → re-review #2 REVISE → final re-review #3 APPROVE |

## Issue #104 repair log

## Frozen product candidate validation

All product code and tests were frozen at
`54d758a260f347cc31a3191d342ad509eb62d81f`. The following evidence belongs to
that candidate and predates this T9 documentation-only diff:

- `dotnet build CopilotAgentObservability.slnx`: PASS, 0 warnings, 0 errors.
- `pwsh scripts\test\install-playwright-chromium.ps1`: PASS.
- The first `dotnet test CopilotAgentObservability.slnx` run was **FAIL only**
  on one intermittent Local Monitor assertion:
  `MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary(false)`;
  Local Monitor was 1475/1476 while Doctor was 260/260 and ConfigCli was
  4029/4029. The failing assertion was the request-list observation at line
  80; the production UI showed the expected prompt label. That test and its
  JavaScript were unchanged by Issue #104.
- An exact rerun of `dotnet test CopilotAgentObservability.slnx` passed: Doctor 260/260, ConfigCli 4029/4029,
  Local Monitor 1476/1476; 5765 total, 0 failed, 0 skipped.
- The focused `MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary` diagnostic passed 2/2 (false/true cases).
- Exact focused command: `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary" --no-restore`
- `git diff --check` was clean before the T9 documentation changes.

The first-run Playwright failure is retained as evidence, not converted into a
product failure claim or silently omitted. The exact rerun is the validation
result for the frozen candidate. No product code or test edit is permitted
after the frozen SHA.

- 2026-07-18 — Zero-candidate `first-trace complete` repair: the
  candidate-free path now retries a `partial_fact_snapshot` as a stateless
  pre-window evaluation, returns `first_trace_not_ready` only with successful
  `evaluation_completed` and a non-ready primary state, and leaves the
  verification active. No candidate is selected or synthesized. Regression
  evidence: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~ClaudeFirstTraceCrossSurfaceTests`
  (7/7) and `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~FirstTraceCliTests`
  (37/37). Changed orchestrator and focused tests; broader privacy/T9 findings
  remain out of scope.

- 2026-07-18 — T8 bounded privacy-evidence repair: the Claude cross-surface
  OTLP fixture now injects synthetic response, tool-argument, tool-result,
  credential, authorization, PII, and sensitive-local-path markers while
  retaining the existing prompt, native-session, path, transcript-path, and
  cwd markers. `AssertNoSensitiveMarkers` is exercised through the existing
  first-trace stdout/stderr boundaries for begin/status/complete, including
  guidance, candidates, previews, and the not-ready path; setup plan/apply and
  the owning Local Monitor security tests remain separate evidence. No
  production code changed and no marker was added to repository-safe output.
  Validation: `dotnet test
  tests\\CopilotAgentObservability.Doctor.Tests\\CopilotAgentObservability.Doctor.Tests.csproj
  --filter FullyQualifiedName~ClaudeFirstTraceCrossSurfaceTests` — 7 passed,
  0 failed, 0 skipped; `git diff --check` passed. Luna xhigh implementation
  report: test-only, production unchanged. Terra high fresh read-only review:
  APPROVE. Live Claude producer behavior remains unverified and is outside
  this bounded repair.

## Open items / unverified inter-issue interfaces

Closed at the frozen candidate:

- The Doctor verification tables, ingestion/projection tables, Session tables,
  and monitor runtime-state store are wired to the same monitor database path;
  `MonitorHost` constructs each store from `options.DatabasePath`, while the
  ConfigCli Doctor application reads that same path through
  `SqliteDoctorVerificationStore`.
- The monitor runtime-state row and monitor schema revision 6 migration are
  implemented and covered by the committed prior-version migration evidence.
- The internal atomic exclusive-start operation and the expiry-boundary store
  read are implemented and included in the T3/T7 validation evidence.
- The #109 fix persists Session `match_kind`; it does not add a new
  `source_adapter` value or public wire vocabulary. Existing
  `exact-binding.md` rules remain authoritative, including the shared-trace
  `exact_linked` prohibition.
- Collision-file edits are recorded in the log below with their
  source-neutrality decisions. The derived README, user guide, and local
  monitor script guide already describe the current Claude handoff and were
  not changed for T9.

Still open and explicitly handed off:

- **#110 → #106:** live Claude producer behavior when telemetry is enabled and
  `OTEL_LOG_USER_PROMPTS` is off. #106 must run one interactive prompt and one
  `claude -p` prompt, export the raw records, and record whether the
  `user_prompt` key is present. This branch makes no live-producer claim.
- **Future binding interface:** the complete provenance-bearing trace-context
  DTO required for byte-equivalent trace-context binding is deferred in
  Session v1. Trace-id-only evidence remains non-binding.
- **T9 documentation closeout:** Terra high final re-review #3 approved this
  documentation-only closeout. Main integration, live Claude execution, and
  #106 result recording remain outside the completion claim for this branch.

## Collision-file edit log

| File | Task | Change | Source-neutral? |
| --- | --- | --- | --- |
| `docs/specifications/interfaces/configuration-setup.md` | T0 | Claude completion boundary + `run_first_trace_doctor` row activation | Claude-scoped rows only; no Copilot text touched |
| `SqliteDoctorVerificationStore.cs` + `SqliteDoctorApplicationService.cs` | T3 | internal ListActive/ListCandidates/StartExclusive + DoctorStoreOutcome member | source-neutral; public contract/schema untouched |
| `MonitorHost.cs` | T4 (1ed3e12) | 2 usings + testOptions-gated observer/worker registration (7 lines) | registration shape source-neutral; observer itself Claude-specific |
| `Telemetry/Properties/AssemblyInfo.cs` | T4 (1ed3e12) | +1 InternalsVisibleTo(Doctor.Tests) | additive, source-neutral |
| `SqliteSessionOtelEnricher.cs` | T4 (6270a83) | delegates binding decision to extracted ClaudeExactBindingRule; behavior pinned by tests first | Claude path only; generic/Copilot path untouched |

## Review findings log

T0 review #1 (Codex Luna xhigh, read-only, on b43ed58): VERDICT REVISE.
Resolutions applied in the T0 revision commit:

1. (Critical) begin pre-window evaluation would always hit
   `partial_fact_snapshot` → begin now pins the Doctor contract's own
   pre-trace family values (`none`/`not_persisted`/`not_started`/
   `(not_required, not_applicable)`/completeness `unknown`), verified against
   `DoctorTestSnapshots.ReadyNoRealTrace()` and `DoctorValidation`
   cross-field rules.
2. readiness probe cannot separate no-listener from foreign owner → the fact
   collector performs its own `/health/live` probe with the setup contract's
   three-way classification.
3. higher-precedence observer insufficient for effective values → new
   read-only effective-value resolver (setup precedence, per-key
   effective/absent/conflict) pinned in the spec.
4. no read path for DB path / raw-access mode → `--database` argument with
   doctor-verb semantics; new source-neutral monitor runtime-state row for
   `raw_access`, trusted only when the liveness probe is monitor-live.
5. fixed `claude-code-otel` adapter vs raw-otlp exact binds → explicit v1
   candidate-eligibility rule (recognized claude-code provenance only;
   binding state stays valid; provenance never rewritten; documented
   residual).
6. valid-but-not-ready completion had no envelope code →
   `first_trace_not_ready` (success=false, exit 3) added.
7. trace-keyed chain selection could mix Sessions → session-keyed grouping
   with explicit-selection on any ambiguity (incl. one trace claimed by two
   Sessions).
8. `--expires-at` default undefined → exactly 10 minutes from the Doctor
   store clock at start.
9. active-check + start race → internal atomic exclusive-start inside one
   store transaction (spec'd under Internal store operations); adds a small
   internal store write to T6 scope.

T0 re-review (Codex Luna xhigh, read-only, on f012db9): VERDICT REVISE —
2/9 not resolved + 1 new. Adjudication:

- Finding 1 "still partial_fact_snapshot": REJECTED after code verification.
  `DoctorEvaluator.UnknownPreventsConclusion` treats unknown completeness as
  blocking only when `MeetsFirstTraceRequirementsExceptKnownContent` is true;
  with `last_ingest = none` it is false, so the pre-window snapshot concludes
  `ready_no_real_trace` (DoctorEvaluator.cs:146-149). A clarifying sentence
  was added to the spec; no rule change.
- Finding 4 schema-wording contradiction: ACCEPTED — Non-scope now says
  "SQLite schema of the Doctor tables"; README D2 reworded; runtime-state row
  is a monitor-owned migration.
- Finding 7 chain-grouping soundness/ordering: ACCEPTED — spec now states the
  grouping reproduces persisted observer decisions (binding refs are emitted
  only for the exact record/trace/Session decision) and pins ordinal ref
  ordering for auto-selection.
- NEW-1 multiple actives: ACCEPTED — smallest `(started_at,
  verification_id)` returned.

T4 review #1 (Codex Luna xhigh, read-only, on 6270a83+1ed3e12): VERDICT
REVISE. Confirmed sound: extraction byte-identical (#109 untouched), observer
uses only source_schema_observations recognition metadata (no otel-exact / no
trace-id continuity), refs/timestamps/adapter/real_source/dedup/wiring/SQL
compliant, busy aborts recompute safely. Fixes required: (1) skip records with
received_at >= verification expires_at (would emit invalid candidates), (2)
#109(a) regression must drive completion to session_unbound, (3) #109(b) must
assert no exact_bound through the fact/completion path, (4) minor: no-active
test lacks an assertion. Fix dispatch queued behind the running T2 writer.

T3 review #1 (Codex Luna xhigh, read-only, on 07f44c1): VERDICT REVISE —
(1) atomic exclusive start missing (moved from T6 scope into a T3 fix
dispatch), (2) ListActive expiry boundary test missing. Fix dispatched.

Minor items for final review triage:

- [low] T4 re-review NEW-1: the observer expiry-boundary test cannot
  distinguish "record skipped before ObserveCandidate" from "candidate
  rejected by the store" (both leave candidates empty) — behavior proven, red
  strength weak; consider a call-count seam only if the final review deems it
  worth the surface (ClaudeDoctorCandidateObserverTests.cs:178).

T9 disposition: the low-strength expiry-boundary assertion is retained as a
known test-quality note; the expiry behavior is covered by the observer suite
and the frozen full validation. No product or test change is made in this
documentation-only closeout.

T9 Terra high review #1 (read-only, on the uncommitted documentation-only diff):
VERDICT REVISE — the focused 2/2 evidence must name
`MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary`
(false/true cases), and the Three-surface settings matrix separator must have
exactly five columns. These corrections are recorded above; Terra high re-review
is pending. No final approval is claimed.

T9 Terra high re-review #2 (read-only, on the uncommitted documentation-only
diff): VERDICT REVISE — `docs/task.md` prematurely claimed “feature branch
complete” while the handoff and ledger recorded `closeout-in-review`. The status
correction is applied in this diff; final re-review remains pending. No approval
is claimed.

T9 Terra high final re-review #3 (read-only, on the corrected documentation-only
diff): VERDICT APPROVE. The focused diagnostic, settings matrix, and review-state
records are corrected. The frozen candidate validation evidence and the live
Claude producer, #110, and main-integration exclusions are retained.

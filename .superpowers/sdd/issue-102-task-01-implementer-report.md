# Issue #102 Task 1 Implementer Report

## Identity verified

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability`
- Branch: `codex/issue-102-doctor-core`
- Starting HEAD: `0a2542d9e973429e39a4986c823996b057fb607b`
- Identity matched the task brief before editing.
- Starting status contained only the untracked task brief.
- No commit, push, PR, or main-integration action was performed.

## Files changed

Canonical specification scope:

- `docs/requirements.md`
- `docs/spec.md`
- `docs/specifications/interfaces/first-trace-doctor.md` (new)
- `docs/specifications/interfaces/config-cli.md`
- `docs/specifications/README.md`
- `docs/architecture.md`
- `docs/decisions.md` (D060)

Required task evidence:

- `.superpowers/sdd/issue-102-task-01-implementer-report.md` (this report)

No production code, test code, dependency, fixture, schema implementation,
migration implementation, user guide, issue status, durable ledger, or sprint
history was changed.

## Contract coverage

- Added one canonical `first-trace-doctor.md` interface owning the shared direct
  / Config CLI / Local Monitor `DoctorResult` (`doctor.v1`) contract.
- Defined all twelve fact families with explicit null-family and `unknown`
  behavior and fixed source-neutral evidence candidate class/kind values.
- Defined all twenty states with the approved exact severity, retryability, and
  next-action mapping; v1 reason code equals state code.
- Fixed deterministic blocking precedence, terminal selection, advisory order,
  primary selection, partial facts, synthetic/real separation, and D059's
  unrelated-schema-drift rule.
- Fixed limits: 64 KiB CLI/HTTP input, source/adapter grammar and length,
  128-character evidence refs, 16 accepted refs, 100 candidates, 1..30 minute
  windows, canonical lowercase UUIDv7, and canonical UTC RFC 3339 round-trip
  timestamps.
- Added all five CLI commands, strict parsing/input rules, JSON/human projection,
  sanitized output behavior, and fixed exit categories.
- Added all five HTTP routes, request shapes, fixed status mapping,
  loopback/Host/same-origin/CSRF rules, `Cache-Control: no-store`, and the
  no-raw/PII/path/credential/exception boundary.
- Defined Doctor v1 SQLite component/table columns, candidate/accepted evidence,
  active/completed/cancelled lifecycle with derived expiry, revision CAS,
  lifecycle/evidence atomicity, historical monitor v1-v4 to current v5 plus
  Doctor v1 migration evidence, close/reopen/idempotence/rollback checks, and
  verification-only degradation.
- Kept Doctor evaluation/verification isolated from D051 readiness and Local
  Monitor host startup/ingestion.
- Kept architecture dependency direction acyclic: the Doctor domain is lower
  than persistence and the CLI/HTTP adapters.
- D060 fixes the shared domain, explicit bounded verification, D051 isolation,
  #103/#104 producer handoff without new enums, #105 proxy/UI ownership, and
  D059 preservation.

## Commands and results

Required brief validation was run from the repository root:

1. `git diff --check` — exit 0, no output.
2. `rg -n "doctor.v1|/api/doctor|first_trace_ready|D060" docs` — exit 0;
   matches found in the new canonical interface and all intended indexes/
   summaries/decision references, plus the approved design/plan history.
3. `git status --short --branch` — exit 0; branch remains
   `codex/issue-102-doctor-core`; only the seven canonical files, the untracked
   task brief, and this required report are present.

Additional review checks:

- `git diff --no-index --check -- NUL docs/specifications/interfaces/first-trace-doctor.md`
  — whitespace check passed for the new untracked canonical file (the expected
  no-index content difference was normalized to success by the wrapper).
- Structural review counted 20 catalog rows, 5 CLI command lines, and 5 HTTP
  routes in the canonical interface.
- Searched the canonical interface for TODO/TBD/placeholders, silent fallback,
  compatibility shims, main-integration claims, and sleep/polling guidance; no
  blocking placeholder or prohibited behavior was found. References to
  forbidden latest-trace selection and forbidden compatibility fallback are
  normative prohibitions.

## Self-review verdict

Pass — no blocking issue found.

Reviewed perspectives:

- source-of-truth placement and `spec-update` coverage;
- exact public contract and cross-document consistency;
- deterministic state/unknown/order semantics;
- CLI and HTTP boundary completeness;
- data safety and sanitized failure behavior;
- SQLite lifecycle, migration, atomicity, restart, and degradation boundaries;
- D051 and D059 non-regression;
- acyclic dependency ownership;
- unnecessary scope, placeholders, compatibility behavior, and integration
  claims.

The approved design/plan adds the Issue #102 canonical contract to previously
unspecified Issue #69 first-trace responsibility; no conflicting canonical
behavior was found before editing.

## Unresolved findings

None in Task 1 scope.

## Explicitly unverified later interfaces

- Issue #103 GitHub Copilot source-specific fact/candidate production and live
  first-trace evidence are not implemented or verified.
- Issue #104 Claude Code source-specific fact/candidate production and live
  first-trace evidence are not implemented or verified.
- Issue #105 proxy DTO, Razor/JavaScript/Canvas/UI workflow, and live UI
  validation are not implemented or verified.
- No production implementation, executable DTO equality test, CLI/HTTP route
  test, SQLite migration/atomicity test, D051 regression test, build, Playwright
  bootstrap, or solution test was required or performed for this
  specification-only task. Those remain later implementation-plan tasks.

## Review fixes

The independent review recorded five Important findings. All five were
addressed in the canonical specification scope; this section supersedes the
earlier self-review verdict and unresolved-findings statement for the reviewed
revision.

- I-1: replaced the unscoped store failure names with the fixed
  `doctor_store_busy` / `doctor_store_unavailable` vocabulary across the
  canonical result catalog, CLI exit 5, HTTP 503, newer-schema rejection,
  isolated store degradation, architecture summary, requirements, and D060.
  Key locations are `first-trace-doctor.md:323-324`, `:412`, `:453`, `:507`,
  `:518`, `config-cli.md:142`, `architecture.md:155`, and
  `decisions.md:2102`.
- I-2: added source-neutral typed `DoctorObservation` input for direct
  evaluation and kept persisted `DoctorEvidenceCandidate` as a separate
  carrier. Verification completion accepts references only; the store/service
  resolves existing unexpired candidates into trusted observations, and a
  completion snapshot must contain no caller-supplied observations. There is
  no public `ObserveCandidate` command or route. Key locations are
  `first-trace-doctor.md:104-145`, `config-cli.md:105-110`,
  `architecture.md:136-153`, and `decisions.md:2081-2086`.
- I-3: narrowed `session_unbound` to the known `unbound` outcome. Required plus
  unknown now follows the partial result, while required plus `not_applicable`
  is invalid input (`first-trace-doctor.md:198-201`).
- I-4: fixed the entire partial projection: `success=false`, code
  `partial_fact_snapshot`, non-null evaluation, null primary state, empty
  states, nonempty canonically ordered missing families, null verification for
  direct evaluation, and an unchanged active verification for a partial
  complete attempt (`first-trace-doctor.md:253-260`, `:337`, `:364-366`).
- I-5: fixed evaluation order to blocker-only output when any blocker applies;
  otherwise one terminal state precedes fixed applicable advisories. Advisories
  are never emitted beside a blocker (`first-trace-doctor.md:231-244`,
  `spec.md:161-166`, and `decisions.md:2076-2080`).

Review-fix validation from the repository root:

1. `git diff --check` — exit 0, no output.
2. `rg -n "doctor.v1|/api/doctor|first_trace_ready|D060" docs` — exit 0;
   expected canonical, index, architecture-decision, and approved design/plan
   references were present.
3. `git status --short --branch` — exit 0; branch remained
   `codex/issue-102-doctor-core`, with only the Task 1 canonical/evidence files
   and preserved review artifacts present.
4. Structural PowerShell checks — exactly 12 fact rows, 20 state rows, 5 CLI
   command lines, and 5 HTTP route lines.
5. `rg --pcre2 -n '(?<!doctor_)store_(busy|unavailable)' ...` over all seven
   canonical files — no unscoped fixed token remained. The first attempt used
   the same lookbehind without `--pcre2`; ripgrep rejected that unsupported
   regex mode, so the check was rerun with the required engine.
6. Targeted searches for typed observation/candidate resolution,
   `session_unbound`, the fixed partial projection, and blocker/terminal/
   advisory ordering all found the intended canonical clauses cited above.

No build or test command was added: this review fix remains a specification-only
Task 1 change, and the task brief's exact validation scope does not require
production validation.

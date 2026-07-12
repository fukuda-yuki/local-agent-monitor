# Canvas Effect Comparison Interface

## Scope

This specification defines Issue #56. It compares user-confirmed pre-change
and post-change Session cohorts for one exact Issue #54 proposal revision and
one Issue #55 application receipt. It owns the only transition to proposal
status `verified`.

It does not infer Session identity, generate a proposal, apply or roll back a
file, invoke git, claim statistical proof, or collapse quality and efficiency
into a single score.

## Exact Inputs And Revisions

Each proposal has a positive integer `proposal_revision`, initially `1`.
Changing lifecycle state between `candidate` and `recommended` increments the
revision. Compare records the revision that the Issue #55 draft and apply
receipt captured. Verification requires the proposal to remain at that exact
revision and `recommended` state. The atomic `verified` transition does not
change the revision recorded by the effect receipt.

An eligible application receipt is exactly one persisted Issue #55 apply row
with:

- the requested `apply_id`, proposal ID, and proposal revision;
- state `applied`, not `rolled_back` or pending;
- a committed `applied_at` timestamp; and
- every current target still matching its recorded post-apply SHA-256 under
  the configured non-reparse root.

A missing, rolled-back, pending, recovery-failed, revision-mismatched, or
post-hash-stale application produces `insufficient_evidence`. Compare never
reads or returns target paths, source, diff, replacement, or snapshot data.

## Objective Evaluation Receipt

An objective evaluation receipt is immutable local-runtime metadata:

| Field | Contract |
| --- | --- |
| `objective_evaluation_id` | Server-generated UUIDv7 string. |
| `session_id` | Exact-bound local Session UUIDv7. |
| `run_id` | Existing local Run UUIDv7 belonging to `session_id`. |
| `trace_id` | Nonblank trace ID exactly equal to the Run trace ID. |
| `result` | `pass` or `fail`. |
| `severity` | `normal` or `severe`; `pass` requires `normal`. |
| `evaluator_id` | Sanitized stable identifier, 1..100 characters. |
| `evaluator_version` | Sanitized version, 1..100 characters. |
| `criterion_id` | Sanitized stable criterion, 1..100 characters. |
| `case_key` | Sanitized opaque repeatable-case key, 1..200 characters. |
| `evidence_refs` | 1..10 existing Run/Event/Trace/Gate references exact-linked to the same Session and Run/trace scope. |
| `recorded_at` | Server-generated ISO-8601 timestamp. |

Identifiers accept only `^[A-Za-z0-9][A-Za-z0-9._:-]*$`. They are labels, not
paths, URIs, source fragments, credentials, or free-form notes. A receipt is
rejected unless the Session is terminal and `full`, the Run and trace match
exactly, and every evidence reference resolves in the same exact Session
scope. Repository, workspace, timestamp proximity, prompt similarity, and
target label never establish identity.

The repository-safe normalized measurement dataset is not an objective
receipt source by itself. Its manually supplied `success_status=pass` has no
durable Session/Run linkage and cannot satisfy this interface without first
being recorded through the exact receipt boundary above.

## Candidate Suggestions And Cohort Confirmation

Candidate suggestions are non-authoritative. They may use exact Session facts,
application time boundaries, and identical `case_key` / evaluator / criterion
metadata. Repository or timestamp proximity alone is forbidden. A suggestion
never includes a Session automatically and never records a verdict.

The user submits one immutable cohort revision containing:

- `proposal_id`, `proposal_revision`, and `apply_id`;
- included `pre` and `post` Session IDs;
- one sanitized `case_key` for each included Session;
- excluded Session IDs with one fixed reason:
  `not_comparable`, `wrong_case`, `missing_evidence`,
  `overlaps_application`, or `user_excluded`.

Every Session appears once at most. Included Sessions must be exact-bound,
terminal, and `full`. A pre Session must end at or before `applied_at`; a post
Session must start at or after `applied_at`. A Session spanning the boundary
cannot be included. At least three pre and three post Sessions are required for
an effect verdict other than `insufficient_evidence`.

Pair drill-down groups included pre/post Sessions by exact `case_key`; group
sizes need not be equal. Cohort summary and pair drill-down are projections of
the same stored Session and evidence-reference rows. Neither may reconstruct,
drop, or substitute evidence.

## Effective Quality Evidence

Every included Session must have at least one decisive quality input captured
by the cohort revision:

- human evaluation `expected` or `problem`, including its `recorded_at`; or
- one or more immutable objective receipts.

An effective Session outcome is `fail` if any human evaluation is `problem` or
any objective receipt is `fail`. Conflicting evidence therefore fails closed.
It is `pass` only when at least one decisive input exists and every available
input is positive. It is missing otherwise. A Session has severe failure when
any objective receipt has `result=fail` and `severity=severe`.

Missing, changed, deleted, ambiguous, or out-of-scope evidence yields
`insufficient_evidence`; it is never imputed as zero, pass, or expected.

## Efficiency Evidence

The only v1 efficiency metrics are:

- Session duration in milliseconds from valid `started_at` and `ended_at`;
- Session total tokens, the sum of Run `total_tokens` when every included Run
  supplies a nonnegative value.

A metric participates only when it is positive and complete for every Session
in both cohorts. Cohort aggregation uses the median; an even-sized median is
the arithmetic mean of the two middle values using decimal arithmetic.
Percentage change is `(pre_median - post_median) / pre_median`. Exactly 10%
improvement is material. Worsening is material only when greater than 10%.
Rounding is display-only and never changes a verdict.

## Deterministic Quality-First Verdict

The fixed verdicts are `improved`, `no_change`, `regressed`, and
`insufficient_evidence`. Evaluation uses this precedence:

1. Return `insufficient_evidence` when application/proposal/cohort linkage is
   invalid, either cohort has fewer than three Sessions, any included Session
   is not exact-bound/terminal/full, any quality input is missing, or a
   comparison required below has no complete common efficiency metric.
2. Return `regressed` when any post Session has severe failure.
3. Compare quality pass rates exactly by integer cross multiplication. A
   higher post rate is `improved`; a lower post rate is `regressed`.
4. When quality rates are equal, compare every complete common efficiency
   metric. Return `improved` when at least one improves by 10% or more and none
   worsens by more than 10%. Return `regressed` when at least one worsens by
   more than 10% and none improves by 10% or more.
5. Return `no_change` for equal quality with only sub-threshold changes or a
   mixed material efficiency result.

Quality is therefore lexicographically prior to efficiency: an efficiency gain
can never override lower quality. The output includes counts, exact fractions,
medians, unrounded deltas, included/excluded reasons, and fixed insufficiency
reason codes; it contains no composite score.

## Effect Receipt And Verified Transition

Comparison creates an immutable effect receipt containing IDs for the
comparison, cohort revision, proposal/revision, application, every included /
excluded Session, every captured human/objective evidence reference, quality
summary, efficiency summary, verdict, fixed reasons, and `recorded_at`.

Inside one SQLite transaction, the store re-reads the proposal revision,
application state, cohort revision, and evidence identities, records the effect
receipt, and changes the proposal from `recommended` to `verified` only when
the verdict is `improved`. Other verdicts never change proposal maturity.
Races return a fixed stale/insufficient result and never partially verify.

A later successful rollback leaves the effect receipt as historical evidence
but makes its derived `verification_state` equal `invalidated`. The proposal's
historical `verified` status remains; UI and APIs must not present the receipt
as an active improvement. No rolled-back receipt can verify another cohort.

## Local Monitor Interface

All responses use `Cache-Control: no-store`. Writes require loopback/Host
validation, same-origin, `x-monitor-csrf: local-monitor`, exact JSON media type,
and a request body no larger than 1 MiB.

```text
POST /api/session-workspace/objective-evaluations
GET  /api/session-workspace/objective-evaluations?session_id={sessionId}
GET  /api/session-workspace/proposal-applies/receipts?proposal_id={proposalId}
GET  /api/session-workspace/effect-comparisons/candidates?proposal_id={proposalId}&apply_id={applyId}
POST /api/session-workspace/effect-comparisons
GET  /api/session-workspace/effect-comparisons/{comparisonId}
```

The proposal-apply receipt response is sanitized and contains only opaque IDs,
proposal revision, application state/time, file count, and derived active /
stale / invalidated state. A successful candidate response contains its
requested `proposal_id`, `apply_id`, and the exact persisted
`proposal_revision` from that active application receipt; the value is the
authoritative proposal/application linkage for the subsequent confirmation and
is not browser-supplied or inferred. Candidate and comparison responses
contain only sanitized Session metadata and evidence identifiers.

Fixed errors include `invalid_objective_evaluation`,
`objective_evidence_not_exact`, `objective_store_unavailable`,
`invalid_comparison_request`,
`proposal_revision_stale`, `application_not_active`, `cohort_not_confirmed`,
`comparison_evidence_stale`, `comparison_not_found`,
`cross_origin_forbidden`, `csrf_required`, `unsupported_media_type`, and
`request_too_large`. Rejected content and exception details are never echoed.
`objective_evidence_not_exact` is reserved for a validly processed request
whose referenced Session/Run/trace/evidence scope does not match.
`objective_store_unavailable` is `503` for SQLite busy, I/O, schema, or other
storage failures; it never claims that a valid request has bad evidence.

The additive tables are `objective_evaluations`,
`objective_evaluation_evidence`, `effect_comparisons`,
`effect_comparison_sessions`, `effect_comparison_evidence`, and
`effect_receipts`. They store no raw content, prompt/response, path,
source/diff, replacement, snapshot, credential, token, or free-form note.

## Canvas UI And Authority Boundary

Compare replaces the placeholder with application selection, candidate review,
explicit cohort classification/exclusion, case-key pair drill-down, and an
explicit `比較を確定` action. It displays fixed verdict/reason text and the same
evidence refs used by the summary. Strings render as inert text.

The Canvas helper may proxy only the routes above through the existing
per-launch token. No Canvas action, `session.send()` prompt, log, URL, static
artifact, committed output, or repository-safe summary receives evaluation
payloads, cohort membership, local paths, raw content, or effect details.
There is no automatic cohort confirmation, verdict, Verified transition,
apply, rollback, git operation, retry, or background polling.

## Required Tests

- exact Session/Run/trace/evidence receipt linkage and fixed no-echo errors;
- active, rolled-back, stale-hash, recovery, and proposal-revision application
  receipt cases;
- candidate suggestions without repository/timestamp-only matching;
- user confirmation, duplicate/overlap exclusion, exact three-by-three and
  two-by-three boundaries, unbound/partial/rich/nonterminal Sessions;
- missing/conflicting/human/objective/severe quality evidence;
- exact 10% improvement, exact 10% worsening, greater-than-10% worsening,
  mixed efficiency, missing metric, odd/even median, and quality precedence;
- atomic effect receipt + Verified, stale cohort/evidence/application races,
  concurrent rollback, and post-verification rollback invalidation;
- summary/pair drill-down reference identity and Canvas helper/action/no-leak
  boundaries; and
- repository build, Playwright bootstrap, and full solution tests.

## Non-Goals

No statistical significance claim, composite score, heuristic Session merge,
timestamp/repository-only pairing, raw content comparison, proposal generation,
file mutation, rollback implementation, git/PR operation, automatic Verified,
or reuse of unlinked normalized measurement labels.

# Issue #56 Effect Comparison Design

## Goal

Determine whether one explicitly applied Issue #54 proposal improved a
user-confirmed set of comparable Sessions, using exact Issue #51 identities,
an active Issue #55 receipt, and quality evidence that cannot be inferred from
repository or time proximity.

## Chosen Approach

Use immutable objective evaluation receipts at Run/trace granularity and roll
them up to user-confirmed Session cohorts. A Session-level receipt would lose
the exact Run/trace audit trail; a cohort-level receipt would make individual
pair drill-down unverifiable. The selected approach keeps the local model
small while preserving exact evidence ownership.

The existing normalized measurement `success_status` is not reused directly:
it is file-output data without a durable Session/Run foreign key. An objective
result becomes authoritative only after the Local Monitor validates and stores
the exact Session/Run/trace/evidence receipt.

## Components

1. **Revision and application boundary** — proposal lifecycle revisions are
   captured by Issue #55 drafts/applies. A sanitized receipt read reports only
   opaque linkage, state/time, file count, and current post-hash validity.
2. **Objective receipt store** — immutable pass/fail facts with evaluator,
   version, criterion, case key, severity, and exact evidence references.
3. **Candidate and cohort service** — offers non-authoritative candidates,
   accepts only explicit pre/post/excluded classification, and groups drill-down
   by case key without merging Session identity.
4. **Verdict engine** — a pure deterministic quality-first function. Quality
   pass rates are compared before 10% median duration/token thresholds; missing
   evidence is never imputed.
5. **Effect transaction** — revalidates all revisions and active application,
   writes one immutable receipt, and sets `verified` only for `improved`.
6. **Compare helper UI** — token-gated local display and explicit confirmation;
   no Canvas action or `session.send()` authority is added.

## Data Flow

The user records or selects exact objective/human evidence, chooses an active
application, reviews candidate Sessions, and explicitly confirms the cohort.
The Local Monitor freezes a cohort revision, projects effective quality and
complete efficiency facts, computes the verdict, then atomically persists the
effect receipt and optional Verified transition. Summary and case-key
drill-down read the same stored rows.

Rollback never deletes history. It invalidates the receipt for active-effect
display while preserving the historical verified observation.

## Failure Handling

Invalid or ambiguous identity, incomplete Session evidence, missing quality,
stale proposal/application/cohort/evaluation, insufficient counts, and absent
common efficiency evidence return fixed `insufficient_evidence` reasons. They
do not mutate proposal state. SQLite transaction serialization prevents a
rollback or evidence change from racing a successful Verified write.

## Testing Strategy

Build the pure verdict engine first with table-driven boundary tests, then add
SQLite receipt/cohort/atomic-transition tests, HTTP policy/no-echo tests, and
emitted Canvas UI tests. A Terra High reviewer evaluates quality precedence,
cohort truthfulness, and evidence sufficiency independently. Required closure
includes cohort/insufficient/boundary tests, exact build, Playwright bootstrap,
full solution tests, and the final #54–#56 integration review.

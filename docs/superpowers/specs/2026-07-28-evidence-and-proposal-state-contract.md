# Evidence and Proposal State Contract (v1)

Status: draft, for review. Issue #130 (C2a). Derived from
`docs/superpowers/specs/2026-07-28-local-monitor-v1-product-definition.md`,
"Before the IA can be designed", item 2.

This document defines what a persisted evidence selection is, how it is scoped,
how several proposals for one Session are represented, and how promotion and
abandonment work. It is a state contract, not an information architecture and
not an implementation plan. Issue #131 (C2b) implements the store, API and
migration from it; Issue #132 (C3) chooses the Japanese label vocabulary.

**Out of scope, deliberately.** The apply-draft pointer and any "which draft is
the user advancing" rule belong to Stage 5. Nothing here reserves space for
them. Effect comparison and the `verified` transition are likewise untouched.

## What exists today

Measured against `SqliteSessionStore.cs`,
`docs/specifications/interfaces/canvas-improvement-proposals.md`, and
`docs/specifications/interfaces/canvas-session-evidence.md`.

| Object | Storage | Written when |
| --- | --- | --- |
| Proposal | `improvement_proposals` (`proposal_id`, `revision`, `status`, `target_kind`, `target_label`, `title`, `summary`, `expected_effect`, `risk_note`, timestamps) | `POST /api/session-workspace/improvement-proposals` |
| Proposal → Session | `improvement_proposal_sessions` (`proposal_id`, `proposal_revision`, `session_id`, `source_order`) | same request |
| Proposal → evidence | `improvement_proposal_evidence` (`proposal_id`, `evidence_order`, `kind`, `reference_id`) | same request |
| Objective-evaluation evidence | `objective_evaluation_evidence`, `kind CHECK IN ('run','event','trace','gate')` | `POST .../objective-evaluations` |

`improvement_proposals.status` is `CHECK IN ('candidate','recommended','verified')`.
`PUT .../improvement-proposals/{id}/status` accepts exactly
`{"status": "candidate" | "recommended"}`; `verified` is rejected with
`verification_owned_by_compare`.

**The gap.** There is no row anywhere for "the user has selected this Span as
evidence" until a complete, valid proposal is submitted. That state is a
JavaScript variable in the Canvas extension. It does not survive a reload, so a
proposal cannot be assembled over more than one sitting, and evidence cannot be
gathered before the user knows what they want to propose. This is defect 10 in
the product definition's ledger.

A second, smaller gap: the evidence kinds reachable today are `run`, `event`,
`trace` and `gate`. **`span` is not among them**, while v1's stated promise is
that the user can "select exact Spans and Events as evidence". A span is the
unit the Evidence surface actually renders.

## Design decisions

### D1 — A selection is a first-class object, not a draft proposal

Rejected: adding a `draft` status to `improvement_proposals`. That widens a
frozen `CHECK` constraint and a frozen route's accepted domain, and it makes
`GET /api/session-workspace/improvement-proposals` start returning objects that
are not proposals. Frozen v1 response bytes must not change.

Adopted: a separate additive object, `evidence_selection`, with its own
lifecycle. `improvement_proposals` and its three tables are untouched.

### D2 — A selection is scoped to exactly one Session, and several may coexist

A selection is made while looking at one Session's record, so its anchor is one
`session_id`. A *proposal* may cite several Sessions — promotion to
`recommended` requires evidence from at least two distinct exact-bound Sessions
— so the layering is: many selections per Session, and a proposal draws from one
or more selections.

Rejected: one working selection per Session (a singleton). A singleton is a
"current selection" pointer in disguise. The product definition bans exactly
that: "There is no automatically inferred single correct next action; the user
selects which proposal they are advancing." Several candidate proposals for one
Session are simultaneously valid, and each needs its own evidence set to be
assembled independently.

**No selection is marked current, active, or preferred.** Ordering in any read
is deterministic (`created_at` ascending, `selection_id` ascending as the exact
tie-breaker) and carries no ranking meaning.

### D3 — A proposal's evidence is an immutable snapshot taken at creation

When a proposal is created from selections, the selected items are **materialized
into an immutable evidence snapshot** at creation time. The selection remains
editable afterwards, and editing it does not change any proposal already created.

The snapshot cannot reuse `improvement_proposal_evidence`. That table is
`(proposal_id, evidence_order, kind, reference_id)` — a single reference column
with no qualifier — so it structurally cannot hold a `span`, whose identity is
the pair `(trace_id, span_id)`, nor any future composite kind. The snapshot
therefore needs its own additive table capable of storing every exact identifier
each kind requires. `improvement_proposal_evidence` is left untouched and
continues to serve proposals created through the legacy route.

This is the load-bearing invariant. Without it, a `recommended` proposal's cited
evidence could silently change under it, and the two-distinct-Session promotion
check could be invalidated after the fact. The repository already works this way
elsewhere — `approval_digest` on apply drafts, and the immutable effect receipt.

A proposal additionally records **which selections it was built from**, in an
additive link table, so the user can see the lineage. That link is provenance
only; it is never read back to reconstruct the proposal's evidence.

### D4 — Evidence item identity is exact and closed

An evidence item is a closed-enumeration `kind` plus the exact identifiers that
kind requires. This repository already has a correct precedent in
`AlertEvidenceKind` / `AlertReceiptConsumerV1`, where a `span` reference is
invalid unless **both** `trace_id` and `span_id` are present. The selection
contract adopts that rule rather than inventing a second one.

| `kind` | Required identifiers | Validated against |
| --- | --- | --- |
| `run` | `run_id` | `session_runs`, must belong to the anchor Session |
| `event` | `event_id` | `session_events`, must belong to the anchor Session |
| `trace` | `trace_id` | monitor store, must be exact-bound to the anchor Session via `runs[].trace_id` |
| `span` | `trace_id` **and** `span_id` | monitor store; `span_id` must exist within that `trace_id` |
| `gate` | `gate_id` | sanitized Evidence gate identifiers, per `canvas-session-evidence.md` |

`session` is deliberately **not** an evidence kind. A selection is already
anchored to exactly one Session (D2), so a `session` item would be a reference to
the anchor itself — no information, and one more kind the snapshot must
represent. If cross-Session evidence is ever needed it arrives as several
selections, not as a `session` item.

A `span_id` alone is not an identity: span ids are only unique within a trace.
An item that cannot supply every required identifier is rejected at write time
with a fixed error; it is never stored partially and never repaired by
inference.

**No referenced body is ever copied.** A selection stores identifiers and an
optional user note. It stores no prompt text, response text, tool argument, tool
result, file path, or span attribute value. This preserves the existing
raw-bearing surface enumeration unchanged: the selection tables are not
raw-bearing.

### D5 — Selection lifecycle is two states, and abandonment is not deletion

| State | Entry | Meaning |
| --- | --- | --- |
| `open` | created | The user is assembling evidence. Items may be added, removed and reordered. |
| `abandoned` | explicit user action | The user has given up on this line of reasoning. Items are frozen; the selection is excluded from default reads but remains readable. |

`abandoned` is terminal and reversible only by creating a new selection. A
selection is never deleted by user action, so that a proposal's lineage link
(D3) never dangles. Retention deletion of the anchor Session cascades the
selection away, matching `improvement_proposal_sessions`'s existing
`ON DELETE CASCADE`; the proposals themselves survive, because their evidence is
a materialized copy.

An empty selection is legal. Creating a proposal from it is not — proposal
creation continues to require at least one evidence reference, as today.

### D6 — Proposal promotion is unchanged; proposal abandonment is deferred

Promotion (`candidate` → `recommended`) keeps its existing rule verbatim: an
explicit user action, requiring evidence from at least two distinct exact-bound
Sessions, checked inside the same SQLite transaction that updates the status,
with at most one `recommended` proposal per Session and demotion by explicit
user action. Nothing in this contract changes it.

**Proposal abandonment does not exist in v1, and this is a stated limit, not an
omission.** Expressing it requires either a fourth value in the frozen
`improvement_proposals.status` `CHECK`, or a new accepted value on the frozen
`PUT /api/session-workspace/improvement-proposals/{id}/status` route. Both
change a frozen v1 contract. In v1 the user abandons *a selection*, and demotes
a proposal to `candidate`; a candidate that is no longer wanted simply stays a
candidate. The honest UI statement is "this is a candidate", never "this was
rejected".

If proposal abandonment is later judged necessary, the additive route is the
correct shape, not a widened enum. That decision belongs to #132/#118, not here.

## The contract

### Objects

```
evidence_selection
  selection_id      UUIDv7, local
  session_id        anchor Session, exactly one
  label             sanitized, 1..200 chars, user-authored, not a path or URI
  state             'open' | 'abandoned'
  revision          positive integer, starts at 1, incremented on every
                    item add / remove / reorder
  created_at        ISO-8601, Local Monitor clock
  updated_at        ISO-8601, Local Monitor clock
  abandoned_at      ISO-8601 or null

evidence_selection_item
  selection_id
  item_order        >= 0, dense, user-controlled
  kind              'run' | 'event' | 'trace' | 'span' | 'gate'
  reference_id      the primary identifier for that kind
  qualifier_id      trace_id for kind 'span'; null for every other kind
  note              sanitized, 0..500 chars, or null — why the user selected it
  added_at          ISO-8601

improvement_proposal_selections            (additive, provenance)
  proposal_id
  proposal_revision
  selection_id
  selection_revision   the exact revision consumed at materialization
  link_order           >= 0

improvement_proposal_evidence_snapshot     (additive, the snapshot authority)
  proposal_id
  proposal_revision
  evidence_order       >= 0, dense
  kind                 'run' | 'event' | 'trace' | 'span' | 'gate'
  reference_id
  qualifier_id         trace_id for kind 'span'; null otherwise
  note                 copied from the selection item at materialization
```

`evidence_selection.revision` exists so that materialization is exact: the
proposal records *which version* of the selection it consumed. Without it, "this
proposal was built from selection X" is ambiguous the moment X is edited, and the
immutability invariant becomes unverifiable rather than merely unenforced.
Creating a proposal from a stale revision is rejected, not silently accepted.

`(selection_id, kind, reference_id, qualifier_id)` is unique: the same item
cannot appear twice in one selection. `item_order` is dense and rewritten on
reorder, so ordering is exact rather than a sparse rank.

`qualifier_id` exists so that no existing table's `reference_id` semantics have
to change. Only `span` uses it, and for `span` it is mandatory.

### Invariants

1. Every item's identifiers resolve to a record that belongs to the anchor
   Session, checked at write time inside the write transaction. An item that
   stops resolving later is displayed as honestly unresolvable and cannot
   satisfy any rule; it is not deleted and not repaired.
2. A selection's items never change as a side effect of any proposal operation.
3. A proposal's `improvement_proposal_evidence` rows never change after the
   revision that created them.
4. No selection is current. No read returns a "current" or "active" field.
5. Selection tables are not raw-bearing and gain no raw-bearing field. They
   remain fully available under `--sanitized-only`.
6. `label` and `note` pass the existing secret / unsafe-value validation
   boundary used by proposal text, and rejection returns a fixed error code
   without echoing the rejected text.

### State-changing routes (shape for #131 to implement)

All are additive, loopback-only, and carry the existing Host-header check,
same-origin check, `x-monitor-csrf: local-monitor`, JSON content type, 1 MiB
body limit and `Cache-Control: no-store`.

```text
GET    /api/session-workspace/evidence-selections?session_id={uuidv7}
POST   /api/session-workspace/evidence-selections
GET    /api/session-workspace/evidence-selections/{selectionId}
PUT    /api/session-workspace/evidence-selections/{selectionId}
POST   /api/session-workspace/evidence-selections/{selectionId}/items
DELETE /api/session-workspace/evidence-selections/{selectionId}/items/{itemOrder}
PUT    /api/session-workspace/evidence-selections/{selectionId}/state
```

`GET` by `session_id` returns `{"items": [...]}` ordered `created_at` ascending
then `selection_id` ascending, `open` selections only unless an explicit
`include_abandoned=true` is passed. `/api/monitor/*` and the v1
`/api/session-workspace/*` routes are not touched in shape, ordering or bytes.

Fixed error codes: `invalid_session_id`, `invalid_selection_id`,
`invalid_selection_request`, `invalid_selection_state`, `unsafe_selection_content`,
`invalid_evidence_kind`, `evidence_reference_incomplete`, `evidence_not_found`,
`evidence_not_exact_bound`, `duplicate_evidence_item`, `selection_not_found`,
`selection_abandoned`, `cross_origin_forbidden`, `csrf_required`,
`unsupported_media_type`, `request_too_large`.

## Impact on frozen contracts

Stated plainly, because this is where the contract can go wrong.

| Change | Frozen? | Verdict |
| --- | --- | --- |
| New `evidence_selection*` tables and routes | No | Purely additive. Safe. |
| `improvement_proposal_selections` link table | No | Purely additive. Safe. |
| `improvement_proposals` and its two child tables | Yes | **Unchanged.** |
| `GET/POST/PUT .../improvement-proposals` response bytes | Yes | **Unchanged**, including rejection bytes. |
| `POST .../improvement-proposals` accepted input domain | Yes | **Unchanged.** `span` stays rejected there, with the exact historical status and body. |
| `POST .../improvement-proposals/from-selections` | New | Additive. Accepts selection revisions only. |
| `improvement_proposal_evidence_snapshot` | New | Additive. The only table that stores composite evidence identity. |
| `objective_evaluation_evidence.kind` CHECK | Yes | **Unchanged.** Objective evaluations keep `run/event/trace/gate`. |

### Resolved: how span evidence reaches a proposal

v1 promises the user can select exact **Spans** as evidence and carry that into a
proposal. Selections can hold `span` items freely (new tables, no freeze). But
`POST /api/session-workspace/improvement-proposals` enumerates
`event | run | trace | gate` for `evidence_refs`.

**Decision: add an explicit proposal-from-selections operation and leave the
frozen proposal-creation route unchanged.**

```text
POST /api/session-workspace/improvement-proposals/from-selections
```

It accepts the proposal-authored fields plus one or more
`{selection_id, selection_revision}` references — and **no arbitrary
`evidence_refs`**. It loads those exact selection revisions and atomically
materializes their items into the immutable snapshot of D3.

Widening the existing route was considered and rejected. The argument for it was
that the freeze is worded "shape, ordering and bytes" and widening changes none
of them. That reading is too narrow in a way that matters:

- **A rejection is a response.** The frozen route today rejects `kind: "span"`
  with a specific status and a specific error body. Making that request succeed
  changes observable bytes on a request that previously had defined behaviour.
  "Bytes" does not mean "success-path bytes only".
- **It changes a state transition, not just validation.** A request that
  previously persisted nothing would now create a proposal.
- **It is not implementable anyway.** `improvement_proposal_evidence` has no
  qualifier column, so the widened route could not store the `(trace_id, span_id)`
  pair a span requires. Accepting the input would mean either silently degrading
  span identity to a bare `span_id` — which is not an identity — or altering the
  frozen table.

The two routes are **not a forbidden dual path**, and the distinction is load
bearing. They are thin adapters over **one** proposal-creation domain service
which owns field validation, revision assignment, Session association, status
initialization, transaction boundaries, immutability enforcement, and ordering.
Neither route calls the other, and no promotion rule or SQL state machine is
duplicated. What differs is only the input contract: legacy evidence references
versus exact selection revisions. AGENTS.md forbids duplicated behaviour, not
additive versioning of an input surface — and the v1 requirement and the frozen
route cannot both be satisfied otherwise. This is an explicit, recorded
exception, to be confirmed at the #118 gate and written into `docs/decisions.md`.

The new route is expected to become the route the v1 UI uses. That is intended:
the frozen route's continued value is compatibility, not feature parity. It is
kept honest by contract tests rather than by withholding capability from its
successor.

Reviewed against this reasoning by an independent model (2026-07-28); the
original recommendation in this document was the opposite and was overturned.

## What #131 (C2b) must deliver

Storage with an additive schema bump and fixture migration coverage, the routes
above, the `from-selections` route, and one shared proposal-creation domain
service that both creation routes adapt to.

Tests must cover:

- item identity validation per kind, including the `span` two-identifier rule;
- cross-Session reference rejection; duplicate item rejection; dense reorder;
- revision semantics: a stale `selection_revision` is rejected, and every
  item mutation increments the revision;
- the snapshot invariant — edit a selection after creating a proposal, assert
  the proposal's snapshot is unchanged;
- abandonment excluded from default reads but readable; Session-deletion cascade
  with proposal survival; unresolvable-item honest display;
- secret/unsafe-value rejection with no echo; CSRF and same-origin policy;
- **negative regression on the frozen route**: `POST .../improvement-proposals`
  with `kind: "span"` still returns the exact historical rejection status and
  error body and persists nothing;
- **positive regression on the frozen route**: every previously valid payload
  still succeeds with byte-identical responses, and existing golden fixtures are
  unchanged;
- **no leakage into frozen reads**: `GET .../improvement-proposals` must not
  begin emitting snapshot rows or the `qualifier_id` representation. The new
  contract carries its own complete snapshot read;
- equivalence: for the overlapping legacy kinds, both creation routes produce
  the same proposal core state.

## Open questions for review

1. Should `note` exist at all in v1? It is the only free-text field on a
   selection and therefore the only new secret-validation surface. It is
   genuinely useful ("this span is why I think the Skill did not load") and is
   the thing a proposal's `summary` is later built from. Kept, with validation.
2. Should a selection be creatable with no anchor Session, for evidence spanning
   Sessions from the start? Deferred: promotion already requires two Sessions,
   and a proposal can draw from several single-Session selections, so the
   multi-Session case is representable without a second scoping rule.

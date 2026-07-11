# Canvas Improvement Proposal Interface

## Scope

This specification defines Issue #54: the Canvas Improve workspace and the
local-runtime proposal lifecycle. It extends the Issue #51 Session Workspace
and Issue #53 Evidence contracts without changing Session identity,
completeness, Agent ownership, or the Issue #45 `session.send()` execution
boundary.

The feature turns a user-reviewed analysis into a structured, evidence-backed
proposal. It does not apply a file change by itself. Issue #55 exclusively owns
approval, diff review, direct apply, snapshots, rollback, and audit of an
applied change through `canvas-proposal-apply.md`.
Issue #56 exclusively owns the comparison that may set a proposal to
`verified`.

## Analysis Boundary

The Improve primary action is available only for an exact-bound completed or
failed Session. It opens the existing token-gated `/analysis` helper and uses
the existing `POST /analyze` -> `session.send({ prompt })` fire-and-forget
path. The prompt continues to contain only the allowed trace/span identifiers,
focus, requested analysis options, and bounded Canvas action names.

The action does not call the Local Monitor raw analysis runner, wait for a
model response, scrape Copilot chat, store a model response, or claim that a
requested model/reasoning/timeout was enforced. A response unavailable in the
current Canvas context is rendered as `利用不可`; it is never reconstructed or
guessed.

## Proposal Model

A proposal is local runtime data with these fields:

| Field | Contract |
| --- | --- |
| `proposal_id` | Local UUIDv7 string. |
| `status` | `candidate`, `recommended`, or `verified`. |
| `target_kind` | One of `skill`, `agent`, `instructions`, `template`, `hook_config`. |
| `target_label` | A user-facing opaque label, 1..200 characters. It is not a path, URI, source fragment, or secret. |
| `title` | Sanitized short title, 1..200 characters. |
| `summary` | Sanitized rationale, 1..2000 characters. |
| `expected_effect` | Sanitized expected effect, 1..1000 characters. |
| `risk_note` | Sanitized risk and side-effect note, 1..1000 characters. |
| `source_sessions` | Distinct exact-bound local Session UUIDv7 references, stored in insertion order. |
| `evidence_refs` | 1..10 structured references to existing session event IDs, run IDs, trace IDs, or sanitized Evidence gate identifiers. The referenced body is never copied. |
| `created_at`, `updated_at` | ISO-8601 timestamps produced by the Local Monitor. |
| `recommended_at` | ISO-8601 timestamp or `null`. |
| `verified_at` | ISO-8601 timestamp or `null`; only Issue #56 may set it. |

All proposal text is local display data, not repository-safe output. The
validation boundary rejects raw prompt/response markers, credentials, tokens,
local sensitive paths, source-code fragments, and unbounded free-form payloads
using the existing secret/unsafe-value policy. Rejection returns a fixed error
code and never echoes rejected text.

## Lifecycle

| State | Entry rule | Meaning |
| --- | --- | --- |
| `candidate` | User submits a valid proposal with one or more citeable evidence references from one exact-bound Session. | A possible durable improvement requiring human review. |
| `recommended` | User explicitly promotes a candidate after attaching evidence from at least two distinct exact-bound Sessions. | The preferred proposal; alternatives remain candidates. |
| `verified` | Only the Issue #56 comparison contract records its operational-verification result. | Improvement effect has been confirmed; this interface cannot set it. |

The Improve tab displays at most one `recommended` proposal for a selected
Session and groups all other related proposals under alternatives. A candidate
is never promoted automatically. Invalid, missing, expired, unbound, or
unresolvable evidence is displayed honestly and cannot satisfy the promotion
rule. Multiple recommendations for the same Session are rejected until the
existing recommendation is demoted to `candidate` by an explicit user action.

The lifecycle does not mean approved for file mutation. A later Issue #55
operation may create an immutable, separately human-approved apply draft, but
that operation never changes this lifecycle. This interface never creates a
branch, commit, push, pull request, file edit, local configuration change, or
Skill/Agent/Instruction installation.

## Local Monitor Interface

All proposal reads are sanitized metadata reads. All writes are state-changing
Local Monitor routes and require the existing loopback and Host-header checks,
same-origin request check, `x-monitor-csrf: local-monitor`, JSON content type,
at most **1 MiB (1048576 bytes)** request body, and `Cache-Control: no-store`
response policy.

```text
GET  /api/session-workspace/improvement-proposals?session_id={uuidv7}
POST /api/session-workspace/improvement-proposals
PUT  /api/session-workspace/improvement-proposals/{proposalId}/status
```

`POST` accepts exactly the proposal fields above except server-generated IDs,
timestamps, and lifecycle timestamps. Its initial `status` must be
`candidate`. `PUT .../status` accepts exactly `{ "status": "candidate" |
"recommended" }`; promotion performs the two-distinct-session check inside the
same SQLite transaction that updates the status. `verified` is rejected with
`400` `verification_owned_by_compare`.

Success responses are `201` with the sanitized proposal object for `POST`,
`200` with the object for `PUT`, and `200` with `{ "items": [...] }` for
`GET`. Fixed errors are `{ "error": "<code>" }`: `invalid_session_id`,
`invalid_proposal_id`, `invalid_proposal_request`, `invalid_proposal_status`,
`unsafe_proposal_content`, `evidence_not_found`, `evidence_not_exact_bound`,
`insufficient_recommendation_evidence`, `recommendation_already_exists`,
`proposal_not_found`, `cross_origin_forbidden`, `csrf_required`, and
`unsupported_media_type`. A body exceeding 1 MiB returns `413`
`request_too_large` with the same fixed response shape.

The additive SQLite tables are `improvement_proposals`,
`improvement_proposal_sessions`, and `improvement_proposal_evidence`.
`improvement_proposal_sessions` uses a Session foreign key. Run/Event/Trace
references are validated against the referenced Session and existing monitor
store at write time; they are not copied into a raw-content table. The schema
migration is additive; no existing session, telemetry, or raw-content table is
repurposed. A proposal stores opaque identifiers and sanitized text, never raw
content or filesystem locations.

The Canvas helper may proxy these routes only through its existing per-launch
token-gated loopback server. It never exposes proposal data through a Canvas
action DTO, `session.send()` prompt, log, committed file, CI artifact, static
artifact, or repository-safe summary.

## UI Contract

Improve replaces its placeholder with:

1. an honest empty/unavailable state when the selected Session is not
   exact-bound, terminal, or has no valid evidence;
2. an action opening the existing detailed-analysis helper;
3. a structured proposal form and evidence-reference selector;
4. one recommended proposal with title, target label, expected effect, risk,
   lifecycle state, and reference links; and
5. collapsed candidate alternatives.

Evidence references navigate only to existing sanitized Session/Evidence
views. The UI does not render raw event content, source code, target paths, or
model response text. It renders all user-entered strings as inert text.

## Tests

- xUnit: proposal route validation, CSRF/same-origin policy, additive
  migration, persistence, lifecycle transitions, single-recommendation rule,
  two-session promotion rule, and fixed-error/no-echo behavior.
- Node tests: Improve state rendering, unavailable states, bounded proposal
  payload construction, evidence selection, and absence of apply/git controls.
- Canvas contract tests: proposal routes remain token-gated; no action DTO,
  log/prompt construction, or sanitized-only response gains raw/PII/path
  fields.

## Non-Goals

No raw analysis execution, response scraping, automatic proposal generation or
promotion, direct apply, patch generation, target-path resolution, rollback,
comparison verdict, or modification to the #45/#51/#53 contracts.

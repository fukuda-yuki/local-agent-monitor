# Local Monitor IA Rebuild Requirements

Revision 12 (2026-07-27) is the canonical requirements definition for the
Local Monitor information-architecture extension.

## Goal

Move the Local Monitor information architecture from a Trace-first console to a
Repository -> Session -> Run/Trace -> Span investigation workspace, so that the
product supports one concrete cycle end to end:

```text
which repository -> what the user actually instructed -> which Session/Run/Trace ran
-> what happened in which Span/Event/Tool/LLM call -> why an error, quality drop,
token increase, or latency occurred -> what Evidence supports that judgement
-> how to change Prompt/Skill/Tool/Agent configuration -> whether the change
   improved things without losing quality
```

The rebuild changes navigation, routes, screen responsibilities, and display
semantics. It introduces no composite score and no heuristic merge.

It supersedes several existing contracts. The complete inventory is the
"Source-of-truth conflicts and required supersedes" section, and every entry in
it requires approval before a document edit lands. No shorter summary of that
inventory is authoritative.

Two of those supersedes are semantic rather than presentational and are called
out here because they change the meaning of persisted values:

1. D040's `repository_name` gains a second role. In addition to being a nullable
   sanitized display label, the exact value becomes the grouping key of the
   display hierarchy. It does not become Session identity or merge evidence.
2. The normalization rule that treats a missing `cache_read_tokens` as `0` is
   superseded **for the new display surfaces only**. Stored rollups,
   `/api/monitor/overview`, `/api/monitor/trace-list`, `/costs`, the normalized
   measurement dataset, and the dashboard dataset keep their existing math and
   shape unchanged.

UI language is Japanese. This document is agent-facing and therefore written in
English, quoting Japanese UI labels verbatim where they are normative.

## Confirmed premises

These are binding inputs, not derivations.

| Premise | Decision |
| --- | --- |
| Repositories observed in parallel | 2 to 4 |
| Repository identity | Established operationally by injecting `vcs.repository.name` per repository. Local Monitor derives a label only from the two authoritative sources already defined by D040 and never infers one. Data with neither goes to `Repository不明` |
| Improvement cycle | Proposal creation, promotion, approval, file apply, rollback, cohort confirmation, and comparison all complete inside Local Monitor |
| `--sanitized-only` | Not used in real operation. An explicitly degraded diagnostic mode. No dual label design is funded |
| Permanent navigation | D042's two-item sidebar is superseded and expanded to four |
| 要確認 taxonomy v1 | Only reason codes derivable from existing persisted facts |
| `ホーム` counts | Aggregate over the most recent 30 days, with the window stated on screen |
| `comparison_regressed` | Token-only regressions are excluded from 要確認 and shown in `改善` instead |

## Design principles

- quality before efficiency; exact trace fidelity; evidence provenance.
- Never treat `unknown` or `missing` as `0` on the new display surfaces.
- Only exact Session binding. No heuristic merge by repository, workspace,
  timestamp, path, tool name, or prompt similarity. No case folding, Unicode
  normalization, or whitespace trimming of a repository label, because any of
  those would be a merge.
- A displayed reason never claims more than its owner fact proves.
- Loopback / local-first. Existing helper-server and Local Monitor process
  security boundary preserved. The raw / sanitized **controls** are preserved
  unchanged: loopback bind, Host validation, same-origin, CSRF, `no-store`, inert
  text, and the prohibition on raw reaching any action DTO, log, committed file,
  or repository-safe output. The **set of raw-bearing surfaces does change**: it
  gains the Repository and Session pages and the instruction-label route, and
  loses `/`. That set is enumerated closedly in the current specifications, so
  every addition and removal is listed in the supersede inventory.
- Razor Pages plus vanilla JavaScript. No frontend build step.
- Existing blue-tinted dark theme. Usable at 1366x768.
- No composite score: no repository health score, no session quality score, no
  risk score, no 0-100 importance, no blended error/quality/token/latency
  number, no AI-decided priority without a stated reason.
- Display-side defence stays at framework text encoding (inert text, no
  `Html.Raw`). No additional sanitizer or CSP apparatus (D020).

## Information architecture

### Permanent navigation (four destinations)

```text
ホーム        Repository list. The investigation entry point.
横断調査      Cross-repository Trace search (TraceId / model / tool / error).
監視          アラート and コスト (budget).
システム      Sources/Ingestion, Diagnostics, 履歴, Retention, Backup/Restore,
              データ受け渡し, Data Boundary, Settings.
```

### Routes

| Route | Kind | raw-bearing |
| --- | --- | --- |
| `/` | ホーム | No |
| `/repos/{repositoryName}` | Repository Workspace | Yes |
| `/repos-unknown` | Repository Workspace for `Repository不明` | Yes |
| `/repos/{repositoryName}/sessions/{sessionId}` | Session Workspace | Yes |
| `/repos-unknown/sessions/{sessionId}` | Session Workspace | Yes |
| `/traces` | 横断調査 | Yes (existing) |
| `/traces/{traceId}` | Trace Workspace | Yes (existing) |
| `/sessions/{sessionId}/instruction-label` | Raw-bearing label route for list rendering | Yes |
| `/alerts`, `/costs` | 監視 | No |
| `/diagnostics`, `/historical-import`, `/historical-analysis`, `/retention/{targetKind}/{targetId}`, `/sanitized-import` | システム | No |
| `/backup-restore` | システム | **Yes**. The backup API and UI are raw-bearing surfaces per `docs/specifications/security-data-boundaries.md:1468` |

`{repositoryName}` is the exact `repository_name` value, percent-encoded per
RFC 3986 as a single path segment. Decoding must yield byte-identical UTF-8. The
comparison is case-sensitive and applies no normalization. `Repository不明` uses
the separate literal route `/repos-unknown`, so no real label can collide with
it. A label whose encoded form exceeds 512 bytes is listed on `ホーム` with a
fixed non-routable reason; it is never folded into another repository.

`/traces/{traceId}` keeps its existing route so a Trace reached from `横断調査`
and a Trace reached from a Session resolve to the same screen.

**Exact repository membership is required on the nested Session routes.** The
server verifies that the Session's own repository matches the route's repository
exactly, and that a Session with no repository is reachable only through
`/repos-unknown/sessions/{sessionId}`. A mismatch is refused rather than rendered
with a breadcrumb that misstates which repository the Session belongs to. The
repository segment is part of the identity of the page, not decoration. Which
status code expresses the refusal belongs to the downstream interface
specification; that it is a refusal rather than a fallback is decided here.

### Breadcrumb context

The Session breadcrumb on a Trace Workspace is rendered only when the link
carries `?from=session&sessionId={sessionId}` and the server verifies an exact
persisted binding between that Session and that Trace. Otherwise the `横断調査`
breadcrumb is rendered. The HTTP `Referer` header is never used.

### Placement of previously unplaced surfaces

| Surface | Placement | Note |
| --- | --- | --- |
| `/alerts` | 監視 | Cross-repository alert surface. Not a v1 要確認 input |
| `/costs` | 監視 | Owner of budget rules |
| `/historical-analysis` | システム / 履歴 | Separate lineage that does not synthesize Session/Run/Event, so it cannot sit inside the Repository hierarchy |
| `/sanitized-import` | システム / データ受け渡し | |
| Sanitized export API | システム / データ受け渡し | No UI in v1. Screen states the operation runs through the Config CLI |
| Raw local replay API | システム / データ受け渡し | No UI in v1. Explicit opt-in danger surface |

`/diagnostics`, `/historical-import`, `/retention/{targetKind}/{targetId}`, and
`/backup-restore` keep their existing routes. `/backup-restore` keeps its `404`
under `--sanitized-only`.

Sources/Ingestion, Data Boundary, and Settings are a **reorganization of existing
`/diagnostics` sections, not new routes**. This keeps D042's decision that
ingestion history lives at `/diagnostics#ingestion-history` and avoids inventing
public surfaces with no owner.

## New read area

Existing public routes keep their shape and ordering. The new IA reads through a
new area, `/api/workspace/v1/*`, which is declared the **compatibility successor**
for two responsibilities that already have an owner. The older routes are
retained unchanged for their existing consumers, including the Canvas helper and
its passing tests. This is explicit successorship, not a silent duplicate.

| Endpoint | Responsibility | Relationship to existing routes |
| --- | --- | --- |
| `GET /api/workspace/v1/repositories?window=30d` | `ホーム` repository rollup | New. `/api/monitor/overview` remains the owner of period token KPIs |
| `GET /api/workspace/v1/sessions?repository_scope=&repository=&after=&limit=` | Repository-scoped, cursor-paged, filterable Session list | Successor to `/api/session-workspace/sessions`, which stays frozen at `{items}` only, newest first, max 200, no filter or pagination |
| `GET /api/workspace/v1/review-items?repository_scope=&repository=&after=&limit=` | 要確認 items | New. Alert Center keeps its own taxonomy and routes |
| `GET /api/workspace/v1/traces?...&tool=` | Cross-cutting Trace search including a tool filter | Successor to `/api/monitor/trace-list`, which stays frozen |

All four are sanitized metadata only and remain prompt-free. Instruction text is
never added to `/api/workspace/v1/*`, `/api/monitor/*`, or SSE. It is served only
by the raw-bearing page routes, the existing raw event content route, and the new
`/sessions/{sessionId}/instruction-label` label route defined below.

### Contract level and downstream ownership

These requirements define which endpoint owns what, what may not appear in a
response, which ordering and completeness guarantees apply, and which existing
contract each endpoint succeeds. This document does not carry the serialized
wire contract.

The full request and response shapes, property names, status codes, error bodies,
cursor encoding, filter value sets, and paging limits are authored as a new
`docs/specifications/interfaces/local-monitor-workspace.md` before implementation
begins. That file is the canonical specification for those details, matching how
every other Local Monitor surface in this repository is specified. Writing them
here instead produced repeated inconsistency between the prose and the tables
across successive revisions of this document.

The constraints below are binding on that downstream specification.

### Binding constraints on the new endpoints

**Prompt-free.** All four endpoints return sanitized metadata only. Instruction
text is never added to `/api/workspace/v1/*`, `/api/monitor/*`, or SSE.

**Existing controls.** `GET` only, loopback bind, Host-header validation,
same-origin, `Cache-Control: no-store`, strict JSON. Error bodies carry no raw
values, no paths, no query echo, and no exception text. Where an equivalent
existing route already defines a response for a condition, such as the
cross-origin rejection or the unregistered-route fallback, the new endpoints
reuse it rather than defining a competing one.

**Existing routes unchanged.** `/api/monitor/*` and `/api/session-workspace/*` v1
keep their shape and ordering. The successor relationship is explicit, and the
older routes stay registered for their existing consumers and tests.

**Ordering must be instant-correct and total.** Every ordering key and cursor key
is compared as a parsed instant, never as stored text. No column that SQLite
aggregates as TEXT may be an ordering, cursor, or window key, for the reason given
in the count display contract; that excludes `sessions.last_seen_at`,
`sessions.updated_at`, `monitor_traces.first_seen_at`, and
`monitor_traces.last_seen_at`.

Each list has a total order, so paging can neither duplicate nor skip a row:

| List | Order |
| --- | --- |
| Sessions | `sessions.created_at` descending, then `session_id` descending by UTF-8 bytes |
| Traces | `monitor_traces.projected_at` descending, then `trace_id` descending by UTF-8 bytes |
| Review items | The item's owner-fact timestamp descending, then `reason_code` ascending by UTF-8 bytes, then the owner-fact row identity descending by UTF-8 bytes |

A review item is **one owner-fact row, never an aggregate**. Two failing objective
evaluations on the same Session are two items, because `objective_evaluations` has
its own primary key and no uniqueness on `session_id`, and collapsing them would
hide one exact piece of Evidence. The final tie-breaker is therefore the exact
identity of the owner-fact row — `objective_evaluation_id`, `comparison_id`,
`trace_id`, or for Human Evaluation the `session_id`, which is that table's primary
key — not the item's subject. That makes the order total even when two rows share a
timestamp and a reason code.

A list row whose timestamp carries a non-`+00:00` offset, which a restored
database can contain, is **included** in the list and ordered by its parsed
instant. Lists are not windowed counts, so excluding such a row would hide data
the user has. The row is marked so the UI can state that its timestamp was not
written by this monitor. Only the `ホーム` counts go `算出不可` on such a value,
because a count asserts completeness and a list does not.

**Repository scope on the wire.** A repository is identified by a discriminator
plus an exact label, so a real repository named `unknown` cannot collide with the
unknown bucket: a `repository_scope` of `known`, `unknown`, or `all`, and an
exact `repository` label required only when the scope is `known`. No key is
derived by hashing or normalizing the label.

**Repository existence and empty results are distinct.** Whether an exact
repository label exists in the store is independent of every other filter. A
label that exists but matches nothing after filtering yields an empty successful
result, not a not-found error. A value outside a closed enum is rejected rather
than silently returning an empty list, so a typo is never mistaken for
"no results".

**Trace status compatibility.** The Trace endpoint preserves the existing
trace-list status filter exactly, including its composite value: `ok`,
`recovered`, `unrecovered`, `unknown`, and `error`, where `unknown` selects rows
whose nullable `monitor_traces.trace_status` is `NULL` and `error` selects
`recovered` or `unrecovered`. This contract does not narrow the set.

**No heuristic resolution.** `session_runs.trace_id` is nullable and not unique,
so a Trace can resolve to more than one exact-linked Session. The response
distinguishes none, exactly one, and more than one, and in the last case it
carries the candidate Session identifiers so the user can choose. Selecting the
most recent candidate, as the existing monitor helper does, is a heuristic and is
not used.

**Nulls stay null.** A null column is serialized as null and rendered with the
`算出不可` or `不明` vocabulary. It is never coerced to `0` or to an empty string.
The one named exception is `trace_status`, where a `NULL` row is presented as the
literal `unknown`, because that is an established value of the existing contract
rather than an invented one.

### Instruction label route

The Session list is labelled by the user's actual instruction, and
`/api/workspace/v1/*` is prompt-free, so a separate raw-bearing route carries the
label: `GET /sessions/{sessionId}/instruction-label`.

It mirrors the existing `/traces/{traceId}/prompt-label` pattern: same-origin,
`Cache-Control: no-store`, escaped inert text, and not registered under
`--sanitized-only`. It returns an abbreviated label and its instruction state,
never full event content.

Binding constraints:

- Retention remains the single authority for item-level read denial. This route
  defers to the existing Retention catalog exactly as the raw event content route
  does, introducing no second denial path, no second expiry rule, and no cache. A
  denied read is the frozen expiry response and never a success carrying a label.
- A label is returned only when the instruction content is actually available.
  Every other instruction state returns the state without a label.
- Full raw event content remains available only through the existing
  `GET /sessions/{id}/events/{eventId}/content` route. This route narrows that
  "only" clause to labels, which is recorded in the supersede inventory.

Its response, error, and state contract is specified in the same downstream
interface specification as the four read endpoints.

## Screen responsibilities

### ホーム

Answers exactly one question: which repository should be checked now, and why.
Not a KPI dashboard. The 30-day window is stated on screen.

Per repository row, v1 shows only what existing facts can produce:

- repository label, or `Repository不明`
- last observed time
- exact-bound Session count
- unlinked Trace count
- 要確認 count, broken down by the five v1 reason codes
- improvement proposal states present in the window (`candidate`,
  `recommended`, `verified`, and comparison `insufficient_evidence`)

Deliberately not shown in v1, with the reason stated in the UI:

- A separate "execution failures" figure. There is no `session_failed` producer,
  so v1 expresses execution problems only through the unrecovered-Trace reasons.
  A row never implies that a Session failed.
- An efficiency-problem figure. The only exact basis available today is a budget
  breach, and production alert evaluation is currently suppression-only.

Row ordering is total. A repository whose 要確認 count is `算出不可` sorts **first**
among known repositories, because "whether this needs attention cannot be
determined" is itself a reason to look, and burying it under a known zero would
hide it. After that, rows are ordered by 要確認 count descending, then
`last_observed_at` descending with `null` last, then the exact label ascending by
UTF-8 bytes. `Repository不明` sorts last regardless of any count, so it never
displaces a real repository.

A repository with nothing to check displays
`現在、明確な要確認項目はありません` and no filler KPIs.

### Repository Workspace

`要確認` is a deterministic read-only projection over accepted owner facts.

`最近の実行` is the repository's execution history, cursor-paged. It is not an
anomaly screen, it is not filtered by problem state, and it is bounded by its
cursor rather than described as complete. A Session appearing in both `要確認` and
`最近の実行` is intended.

It is ordered by **first observation**, `sessions.created_at`, not by execution
time, because no trustworthy execution-time column exists. The screen says so. The
visible consequence is that a Session imported or enriched later appears at the
position of when this monitor first observed it, not when the work ran. Calling
the list "newest execution first" would be a claim the data does not support.

`すべてのSession` is the same cursor-paged list without the recency emphasis,
with filters for status, completeness, and quality state.

`改善` is separated from `要確認`, because an observation problem is not the
progress state of an improvement proposal. It displays four independent persisted
lifecycles side by side and never merges them into a single status:

| Lifecycle | Persisted values | Owner |
| --- | --- | --- |
| Proposal | `candidate`, `recommended`, `verified` | Issue #54 |
| Apply draft | `draft`, `approved`, `applied`, `rolled_back`, `failed` | Issue #55 |
| Apply receipt | `applied`, `rolled_back`, `failed` | Issue #55 |
| Comparison | `improved`, `no_change`, `regressed`, `insufficient_evidence` | Issue #56 |

`failed` is displayed, not collapsed into another value. "Verified but not
current" is not a fourth proposal state; it is the derived display of a
`verified` receipt retained as history after a rollback.

A comparison whose `regressed` verdict rests only on token or duration reasons is
displayed here with its basis named, because it is excluded from 要確認.

`Discovery` is repository-scoped: `Repository -> Tool -> 使用Session -> Trace ->
Span`, plus Prompt/Instruction metadata keyed on `prompt_version`. Skill shows
`Skill identity: 利用不可` until an explicit producer exists.

`未紐付けTrace` lists Traces with no exact Session binding, including OTel-only
Traces.

#### Membership when an object spans repositories

`sessions.repository` and `monitor_traces.repository_name` are single-valued, so
a Session and a Trace each belong to at most one repository.

A Proposal or a Comparison may reference exact Sessions in more than one
repository. Such an object appears in every repository that owns at least one of
its exact source Sessions, and its display names those repositories. It is never
assigned to one primary repository by inference, and it is never hidden.

### Session Workspace

Opening a Session must not land on a waterfall. `Review` comes first.

`Review` shows the full instruction, Session identity, native bindings, source,
completeness, execution status, quality status, Human Evaluation, Objective
Evaluation, a Run/Trace summary, 要確認 reasons, and Evidence coverage.

Execution status and quality status are separate fields, so `実行状態: 完了` with
`品質状態: 問題あり` renders correctly.

Quality status is derived fail-closed from existing owner facts, and both source
values are always displayed next to the derived status:

| Derived | Condition |
| --- | --- |
| `問題あり` | `session_human_evaluation.verdict` is `problem`, or any `objective_evaluations.result` is `fail` |
| `期待どおり` | No negative evidence, and at least one of Human `expected` or an Objective `pass` exists |
| `未評価` | No Human Evaluation row and no objective receipt |

A Human `expected` combined with an Objective `fail` resolves to `問題あり`, and
both values remain visible so the disagreement is apparent.

#### Instruction selection and content states

The primary instruction is the first event of the user-instruction family in
event order, matching the existing Canvas helper rule. Later user messages are
not merged or summarised; `Review` lists every instruction event in event order.

Lists abbreviate the primary instruction to the first three lines or 300
characters, whichever is shorter, for information density and not for safety.
`Review` shows the stored content in full.

`session_events.content_state` is `NOT NULL` and closed to five values. The UI
maps each to a fixed sentence and never invents or renames one:

| `content_state` | Display |
| --- | --- |
| `available` | the stored text |
| `not_captured` | `指示内容: 取得されていません` |
| `redacted` | `指示内容: secret filter により除去されました` |
| `unsupported` | `指示内容: 未対応の形式です` |
| `expired_pending_deletion` | `指示内容: 保持期限切れ（削除待ち）`, consistent with the frozen `410` on the raw content route |

One display state is Session-level rather than event-level: no instruction-family
event exists for the Session, displayed as `指示内容: 該当 event がありません`. It
is never written to `session_events.content_state` and never rendered as any of
the five values above.

There is no "could not resolve which event is primary" state. The primary
instruction is always resolvable when an instruction event exists:
`session_events.event_id` is the primary key, `occurred_at` is `NOT NULL`, and the
read order `occurred_at, event_id` is a total order, so a first event always
exists.

The five wire values are the frozen Session detail contract, and these
requirements conform to them rather than superseding them. `no_event` is an
additive value of the new `instruction_state` field on
`/api/workspace/v1/sessions` and
`/sessions/{sessionId}/instruction-label` only. It is never written to
`session_events.content_state` and never appears in the frozen
`/api/session-workspace/*` responses.

`Execution` shows Runs and Traces. A Trace name or role appears only when an
explicit attribute carries it. Roles such as planning, implementation, or testing
are never inferred from operation names, content, or time.

`Evidence` shows agent hierarchy, event timeline, span timeline, error evidence,
token evidence, tool evidence, prompt/instruction evidence, skill evidence,
Human/Objective Evaluation references, and Alert references. Every relation uses
an exact reference or an existing explicit relationship.

`Improve` covers Evidence selection, proposal creation, candidate and recommended
review, apply draft, approval, apply, rollback, and related proposal history.

`Compare` covers the exact proposal revision, exact apply receipt, pre/post
Session candidates, explicit cohort confirmation, quality comparison, efficiency
comparison, verdict, immutable effect receipt, and active or invalidated
verification state.

### Trace Workspace

Forensic screen reached after descending into a problematic execution.

It has exactly seven sections, presented as tabs:

| Section | Content |
| --- | --- |
| Flow | Vertical flow over the exact span tree, including parallel execution |
| Waterfall | Timeline view over the same exact span tree |
| Span Inspector | Per-span formatted and raw views, tool arguments and results, LLM input composition |
| Error Analysis | Error summary and the distinction between recovered and unrecovered errors |
| Token Evidence | The `新規処理` block and its per-span `reasoning_tokens` |
| Cache | The `キャッシュ` block and cache behaviour per Turn, Trace, and Model |
| Raw OTLP | The raw record, through the existing raw-bearing route |

The Copilot analysis drawer remains a drawer over this screen rather than an
eighth section, because it carries AI findings and must stay visually separate
from the observed facts and derived results in the sections above.

This tabbed structure is what supersedes D042's tab-less trace detail. The count
and the section names above are the contract; their internal layout belongs to
the downstream UI specification.

Under `--sanitized-only` the count stays seven. The Raw OTLP tab remains present
and states that the raw record is unavailable in this mode, rather than
disappearing. Removing a tab would make the section set depend on the runtime mode,
and the screen would silently differ from its contract instead of saying why.

### 横断調査

Auxiliary entry for a known TraceId, cross-cutting search by model, tool, or
error, OTel-only Traces with no Session, and starting forensics directly.

### 監視

`アラート` is the cross-repository alert surface. `コスト` owns pricing, cost
estimation, and budget rules.

### システム

Operational surfaces only, not mixed into the investigation screens.

## Data semantics

### Repository grouping

Group only by the exact value of resource-scoped `vcs.repository.name`. Only when
that key is absent may the canonical GitHub HTTPS `vcs.repository.url.full`
allowlist supply the sanitized repository segment.

Never group or merge by `repo.name`, path, CWD, workspace label, timestamp,
prompt similarity, or Session content. Grouping for display is not Session merge;
the prohibition on repository/workspace/timestamp as Session identity or merge
evidence is unchanged.

Data whose repository identity cannot be obtained goes to `Repository不明` and is
never distributed into a known repository, including into a known repository's
zero count.

There are exactly two authoritative sources and no third. Using the second is not
inference: it is an exact, already-specified derivation from an authoritative
attribute, applied only when the first key is absent, and D040 and its
implementation already do it. "Local Monitor never infers a repository" means it
adds no source beyond these two and never guesses from a path, a workspace label,
a timestamp, or content.

The second source has an accepted residual risk. It keeps only the repository
segment and discards the owner, so `owner-a/api` and `owner-b/api` would collide
into one row. These requirements do not change that behaviour, because changing
it would alter a persisted display label outside this scope. The mitigation
is the operational one: injecting `vcs.repository.name` directly avoids the
fallback path entirely, and that is the documented recommendation. The residual is
stated rather than silently carried.

Backfill behaviour: there is no migration backfill of existing rows. However, a
later contribution to the same Trace fills a previously null `repository_name`
through `COALESCE`, and a Session's null repository is filled by later exact OTel
enrichment. So a repository label can appear on rows first written before the
injection began, and `ホーム` must not claim that historical rows are permanently
unlabelled.

Repository identity is established operationally, not by inference.
`vcs.repository.name` is part of the pinned `OTEL_RESOURCE_ATTRIBUTES` public
interface and the guided setup does not write it, so the user injects it per
repository. When **neither** authoritative source is available — no
`vcs.repository.name` and no canonical GitHub HTTPS `vcs.repository.url.full` from
which a repository segment can be taken — `ホーム` honestly shows a single
`Repository不明` row. Producers that emit the URL but not the name still group,
through the second source, with the owner-collision residual noted above.

### Count display contract

Two different mechanisms produce counts, and they have different guarantees.

**`ホーム` counts are windowed SQL aggregates.** Each is computed over the most
recent 30 days with no row cap, so it is either exact or unavailable. It is never
bounded.

The window is the half-open UTC interval `[T - 30 days, T)`, where `T` is the
request time and is echoed as `generated_at`.

Membership compares parsed UTC instants, never stored text. A null, invalid
ISO-8601 value, or value whose parsed offset is not `+00:00` is unclassifiable
and forces `acquisition_state=unavailable` with
`算出不可（不足: <column>）`. The projection never guesses an offset or falls
back to text comparison.

#### `sessions.last_seen_at`

- On an existing Session, compare the stored value and the candidate value as
  parsed ISO-8601 instants with an explicit UTC designator or numeric offset.
  Do not compare stored text.
- Compare inside the immediate transaction that serializes the Session write.
- Persist the exact text of the later value. If both values denote the same
  instant, preserve the existing text.
- Preserve round-trip timestamp precision. Do not use scalar TEXT `MIN`/`MAX`,
  `julianday`, `unixepoch`, or another reduced-precision comparison.
- If the stored value is not a valid ISO-8601 timestamp with an explicit UTC
  designator or numeric offset, fail the write without changing the aggregate.
  Do not guess, replace, or fall back to text comparison.
- Do not normalize all Session timestamps or rewrite historical rows.
- This requirement applies only to `sessions.last_seen_at`. It does not include
  `sessions.updated_at`, `monitor_traces.first_seen_at`, or
  `monitor_traces.last_seen_at`.
- Do not use `sessions.last_seen_at` as an IA window, ordering, cursor, or
  completeness key. Previously discarded values cannot be recovered.

Window membership uses one named column per fact:

A membership column qualifies only if it is written once, or last-write-wins, from
a server UTC clock. A column maintained by SQLite's scalar `MIN` or `MAX` over
TEXT does not qualify, for the reason given below.

| Fact | Membership column | Write pattern | Nullability |
| --- | --- | --- | --- |
| Session | `sessions.created_at` | Insert only; absent from the upsert's `DO UPDATE SET` | `NOT NULL` |
| Trace | `monitor_traces.projected_at` | `projected_at = excluded.projected_at`, last write wins | `NOT NULL` |
| Human Evaluation | `session_human_evaluation.recorded_at` | `recorded_at = excluded.recorded_at`, last write wins | `NOT NULL` |
| Objective evaluation | `objective_evaluations.recorded_at` | Insert-only receipt | `NOT NULL` |
| Effect comparison | `effect_receipts.recorded_at` | Insert-only receipt | `NOT NULL` |
| Proposal lifecycle | `improvement_proposals.updated_at` | Insert plus explicit status updates; never aggregated | `NOT NULL` |

Exclude every column that has been aggregated as TEXT:
`sessions.last_seen_at`, `sessions.updated_at`,
`monitor_traces.first_seen_at`, and `monitor_traces.last_seen_at`. Past writes
may already have discarded the true instant, so these columns remain ineligible
even after future writes follow the `sessions.last_seen_at` rule above.

The chosen columns are first-projection and last-projection timestamps rather
than execution times. `ホーム` therefore states its window as covering Sessions
first observed and Traces last projected within it, and does not claim to cover
execution time. A Session first observed before the window and still active
inside it is not counted, and the screen says so rather than implying coverage it
does not have.

`monitor_traces` has no `start_time` column and `monitor_spans.start_time` is
nullable, so no exact trace-start timestamp exists. `first_seen_at` carries the
intended meaning, earliest observation of the Trace, but it is maintained as
`MIN(first_seen_at, excluded.first_seen_at)` over TEXT and is therefore excluded
by the rule above. `projected_at` is used instead: it is `NOT NULL`,
last-write-wins from the projection worker's `TimeProvider.GetUtcNow()`, and never
aggregated. It is a processing timestamp, which is why the window is stated as
covering Traces last projected within it rather than Traces executed within it.

Classification is defined over the whole candidate set, not over the window, so
the rule is not circular. The candidate set for a count is every row in the
repository scope. Each row is classified as one of:

- in-window: the membership column is non-null and falls inside the interval;
- out-of-window: non-null and outside it;
- unclassifiable: the membership column is null, its value cannot be parsed as a
  valid ISO-8601 instant, or its parsed offset is not `+00:00`, so the row cannot
  be placed on either side without guessing.

| `acquisition_state` | Condition |
| --- | --- |
| `complete` | The candidate set contains no unclassifiable row. `value` is the in-window count |
| `unavailable` | The candidate set contains at least one unclassifiable row. `value` is `null` and `missing_fields` names the membership column |

Every membership column in the table above is `NOT NULL`, so no count goes
`unavailable` through a null. The reachable cause is a restored row whose value is
unparseable or carries a non-`+00:00` offset, and the projection applies both
checks uniformly to every column rather than assuming the `NOT NULL` columns are
clean.

`last_observed_at` on a `ホーム` row is the maximum in-window membership instant
across that repository's classified Session and Trace rows, compared as parsed
instants rather than by SQL `MAX` over text. It is `null` when the repository has
no classified in-window row, and a `null` sorts after every non-null value in the
row ordering.

**Repository Workspace lists are cursor-paged** and therefore bounded. Their
counts are lower bounds, never totals.

The UI renders exactly four forms:

| Form | Source |
| --- | --- |
| `N件` | `acquisition_state=complete` |
| `0件` | `acquisition_state=complete` with value 0 |
| `取得範囲内 N件（全体未確定）` | A cursor-paged list. N is a lower bound |
| `算出不可（不足: <exact field>）` | `acquisition_state=unavailable`, listing `missing_fields` |

Never sum missing components as zero. Never present a paged snapshot as a latest,
top, or exact global value.

### 要確認 taxonomy v1

`要確認` reads accepted owner facts directly. It adds no lifecycle, no
acknowledgement, no assignee, no queue state, no notification, no second state
store, no duplicate receipt parser, and no browser-side rule evaluation.

The v1 reason codes are the five derivable from existing persisted facts:

| Reason code | Japanese label | Owner fact |
| --- | --- | --- |
| `human_evaluation_problem` | 人手評価で問題あり | `session_human_evaluation.verdict = 'problem'` |
| `objective_evaluation_failed` | 客観評価が失敗 | `objective_evaluations.result = 'fail'` |
| `unrecovered_trace_error` | 未回復エラーあり | `monitor_traces.trace_status = 'unrecovered'` |
| `comparison_regressed` | 比較で品質が悪化 | `effect_receipts.verdict = 'regressed'` whose `result_json` `Reasons` array contains `post_severe_failure` or `quality_regressed` |
| `unrecovered_trace_unbound` | 未回復エラーのTraceがSessionに紐付いていない | `monitor_traces.trace_status = 'unrecovered'` whose existing binding-state projection is not `exact_linked` |

The binding-state vocabulary is the existing one: `exact_linked`, `hook_only`,
`otel_only`. Exactness is established by a native Session binding together with
`session_events.match_kind` in `exact_native` or `explicit_link`. A non-null
`session_runs.trace_id` does **not** establish it, because an OTel-only unbound
Session also receives Runs carrying a trace ID, and `session_runs` has no
exactness column.

The **rule** is reused; the existing *query* is not. The current trace projection
resolves a Trace's Session by fetching the most recent 200 Sessions and taking the
first match, which is exactly the bounded most-recent heuristic these requirements
forbid. Determining that a Trace has no exact-linked Session must therefore be
computed over the **full** exact-link set, not over that bounded helper. This
requires an additive query but adds no producer, column, or rule. It removes a
bound that would otherwise let a Trace be reported as unbound merely because its
Session fell outside the most-recent 200.

`unrecovered_trace_unbound` states only what the absence of an exact binding
proves. The Trace's own spans, status, and error attributes remain reachable by
TraceId alone, so the reason must not claim that the root cause is
undeterminable. What is missing is the Session-level context: the instruction, the
evaluations, and any proposal or comparison linkage. This is the Evidence-gap
concern stated at the precision the data supports, and it is the only
Evidence-gap reason in v1. An old Session whose instruction is merely
`not_captured` is displayed honestly without becoming a 要確認 entry.

`comparison_regressed` is restricted to quality regressions. The current
comparison producer can emit `regressed` from the median of cache-inclusive
`total_tokens` alone when quality is equal, and these requirements forbid
cache-inclusive totals as an efficiency basis. The projection therefore inspects
the persisted `Reasons` array and admits the entry only when a quality reason is
present. A `regressed` verdict carrying only `tokens_regressed` or
`duration_regressed` is displayed in `改善` with its basis named, never in
`要確認`. The comparison producer, its receipts, and its tests are not modified.

Each 要確認 item names an exact subject. A comparison regression is neither a
Session nor a Trace, so the subject kinds are `session`, `trace`, and
`comparison`, with `comparison_regressed` carrying the exact `comparison_id`. Its
repository membership follows the rule already defined for objects spanning
repositories: it appears in every repository owning at least one of the
comparison's exact source Sessions.

Evidence references, and their honest limits:

- Trace, Session, and comparison receipt references are exact and immutable.
- A Human Evaluation is referenced by `session_id` plus
  `session_human_evaluation.recorded_at`. That row is upsertable, so the
  reference points at the current evaluation and is not an immutable receipt. The
  UI states this rather than implying receipt semantics. Introducing an immutable
  Human Evaluation identity would require a new column or an append-only table
  and is out of scope for v1.

Explicitly out of v1, with the reason stated in the UI rather than hidden:

- `session_failed`. The current normalizer maps every terminal event to
  `Completed`, so a failed execution cannot be distinguished. Until a per-source
  failure mapping exists, `要確認` cannot express "the execution failed".
- `required_token_metric_missing`. No required-metric-set contract exists, and
  establishing one would need new producer facts.
- `exact_comparison_regression`. Removed as a duplicate of
  `comparison_regressed` with no distinct persisted fact.
- Efficiency-based reasons in general. The only exact basis available today is an
  explicit budget breach, and production alert evaluation is currently
  suppression-only because the source capability manifests declare the required
  capabilities as `unknown`, producing `missing_required_capability`. Positive
  receipts exist only in synthetic fixtures.
- Open alerts as an input. When manifests are promoted, alerts become an additive
  input without changing this contract. Until then `アラート` is a screen under
  `監視`, not a 要確認 source.

The absence of a Human Evaluation is never a 要確認 reason. Lists and details show
`実行状態: 完了` with `品質評価: 未評価` honestly, and an unevaluated Session is
not an investigation target by itself. Human Evaluation stays optional and is
used when there was a real problem, when the user wants Evidence for a proposal,
when the Session should be a Compare target, or when "it worked as expected" must
be recorded explicitly.

AI findings never place a Session into 要確認.

### Token and cache display

A display that lets cache reads hide input and output is prohibited. A
cache-inclusive grand total is never the headline.

The normalization contract pins that `input_tokens` includes cache reads and that
`cache_read_tokens` is a subset of it. A non-null `input_tokens` is therefore
cache-inclusive by contract, and no new producer fact is needed to subtract.

`新規処理` block:

| Line | Rule |
| --- | --- |
| 入力 (non-cache input) | `input_tokens - cache_read_tokens`, when both are non-null on the same aggregation scope. If `cache_read_tokens` is null, `算出不可（不足: cache_read_tokens）` |
| 出力 | `output_tokens` when non-null, otherwise `算出不可（不足: output_tokens）` |
| Reasoning | `reasoning_tokens` exists on the span column only and has no trace rollup, so trace-level and repository-level display is `利用不可`. It is shown per span in Span Inspector when present |

`新規処理合計` is non-cache input plus output. Reasoning is not a component,
because it has no trace rollup and its relation to `output_tokens` is undefined
by every current source. The total renders
`新規処理合計: 算出不可（不足: <field>）` when either component is unavailable.

The "other new processing volume" line is not included.
No field and no meaning exist for it, so the line is not created rather than
shown as permanently unavailable.

`キャッシュ` block, independent:

| Line | Rule |
| --- | --- |
| 読取 | `cache_read_tokens` |
| 作成 | `cache_creation_tokens` |
| 読取率 | `cache_read_tokens / input_tokens`, rendered only when both are non-null and `input_tokens > 0` |

`作成後の回収状況` (post-creation reclaim) is removed from v1. No identity links a
cache creation to its later reads, so it cannot be computed from existing facts.

`cache_read_tokens * 0.1 + uncached input` is a cost approximation only, never an
observed fact, an exact processing volume, or 要確認 Evidence.

Entry into an efficiency-based judgement requires an exact basis: an explicit
budget breach, approach to a context limit, an increase against an exactly
determined comparison target, a comparison against a confirmed cohort with the
same case key, repeated feeding of a Tool result confirmed by exact span linkage,
an explicit retry relation, or a quality regression confirmed by Compare. Never
accepted: "seems large", above a repository average, similar prompts, close
timestamps, an AI judgement, ranking by cache-inclusive total, or a comparison
where missing values were filled with zero.

### Fact, derived result, and AI finding

The three are never mixed in the UI.

- Observed fact: directly confirmable from telemetry or persisted data.
- Deterministic derived result: computed by a fixed rule, with the computation
  basis and the fields used inspectable.
- AI finding: an investigation hypothesis from Copilot analysis or similar.

An AI finding is not Evidence and is never registered directly as proposal
Evidence. The user opens the cited Span, Event, or Trace, confirms and selects
the actual Evidence, and only then proceeds to a proposal.

### raw / sanitized boundary

Inside the local UI, raw prompts, responses, tool arguments, tool results, PII,
and local paths may be displayed as escaped inert text.

raw is never emitted to a Canvas action response, a `session.send()` prompt, a
helper server log, the repository, an Issue, a static dashboard, GitHub Pages, a
CI artifact, a committed file, or a repository-safe summary.

`/api/monitor/*`, `/api/workspace/v1/*`, and SSE remain prompt-free sanitized
metadata.

The new raw-bearing page routes `/repos/{repositoryName}`, `/repos-unknown`, and
their `/sessions/{sessionId}` children carry the same controls as the existing
raw-bearing pages: loopback bind, Host-header validation, same-origin, CSRF on
state-changing actions, `Cache-Control: no-store`, and escaped inert text.

Full raw event content continues to be fetched only through the existing
`GET /sessions/{id}/events/{eventId}/content` route and its retention semantics.
The new `/sessions/{sessionId}/instruction-label` route is the one addition: it
returns an abbreviated label rather than event content, carries the same
controls, and defers read denial to the same Retention authority. That narrowing
of the existing "only" clause is recorded in the supersede inventory.

### `--sanitized-only` as a degraded mode

An explicitly degraded diagnostic mode, not a peer mode. Because the primary
label of the new IA is raw, no dual label design is funded.

| Route | Behaviour |
| --- | --- |
| `/`, `/repos/...`, `/repos-unknown`, `/repos/.../sessions/...`, `/traces`, `/traces/{traceId}` | Remain registered. Raw sections and raw labels are removed and replaced by the sanitized fallback |
| Raw-only routes: prompt-label JSON, the new `/sessions/{sessionId}/instruction-label`, raw record, span detail, session event content | Not registered. `404` |
| `/backup-restore` and `/api/runtime-backup/v1/*` | `404` before request-body handling or backup-store access, as today |

Sanitized fallback labels:

- Trace lists fall back to the shortened TraceId, as today.
- Session lists and Session Workspace headers fall back to `session_id`, source,
  first-observed time from `sessions.created_at`, `status`, `completeness`, and
  binding state. `sessions.last_seen_at` is not among them, for the same reason it
  is excluded everywhere else in these requirements: with mixed offsets its TEXT `MAX`
  can have discarded the true latest instant, and no read-time check recovers it.
- Prompt Discovery and instruction display are unavailable and say so.

The mode stays functional for ingestion health and metadata diagnosis. It is not
required to support the full investigation cycle.

## Source-of-truth conflicts and required supersedes

This is the authoritative inventory. Citations were verified on 2026-07-27.

### Permanent navigation

- `docs/requirements.md:51` — `208px サイドバー + 2 項目ナビ（概要 / トレース）`.
- `docs/requirements.md:69` — Alert Center must not increase the two items.
- `docs/requirements.md:82` — Issue #105 maintains the two-item nav.
- `docs/spec.md:48` — 208px, `概要` / `トレース`, diagnostics popover.
- D042 (`docs/decisions.md:1310-1326`) — two items, seven screens, in-page state.
- D064 (`docs/decisions.md:2243-2247`), D066 (`docs/decisions.md:2352-2356`),
  D074 (`docs/decisions.md:2718-2721`).
- `docs/spec.md:667`, `docs/spec.md:769`, `docs/spec.md:1028-1031` — further
  statements pinning the canonical two-item navigation.
- `docs/specifications/security-data-boundaries.md:1468-1472` — the explicit
  "do not add a third permanent nav destination" contract, restated in the
  runtime backup boundary.
- Placement statements in
  `docs/specifications/interfaces/first-trace-doctor.md:132-135`,
  `historical-source-import.md:759-760`, `cost-analytics.md:1798-1801`,
  `runtime-backup-restore.md:620-624`.
- `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.css:443-455` and
  `Pages/Shared/_Layout.cshtml:12-15`.
- Playwright regression tests: `MonitorShellPlaywrightTests.cs:39-43`,
  `AlertCenterPlaywrightTests.cs:198-199`,
  `HistoricalImportUiPlaywrightTests.cs:785`, `CostPagePlaywrightTests.cs:37`.

### Home purpose

D042 makes token cost reduction the primary scenario and pins an overview
dashboard with period token KPIs, per-model breakdown, cache efficiency, and a
top-5 cost list (`docs/requirements.md:51`, `docs/spec.md:48`,
`docs/decisions.md:1310-1335`). Alert Center pins an Overview integration of its
own — open count, critical and warning breakdown, source breakdown, top recurring
rule, latest critical — at `docs/specifications/interfaces/alert-center.md:434-437`,
restated as current specification at `docs/spec.md:758-760`. Both must be
superseded together. Replacing `/` with a Repository list supersedes all of those.
The Alert Center Overview content moves to `監視`, where the alert surface already
lives, rather than disappearing, and its bounded/incomplete wording and coverage
bounds move with it unchanged.

`GET /api/monitor/overview` keeps its existing shape and ordering
(`docs/decisions.md:1337-1340`).

### The closed set of raw-bearing surfaces

The set of raw-bearing surfaces is enumerated closedly in the current
specifications, so every removal and addition must be recorded together.

**`/` stops being raw-bearing.** D032 (`docs/decisions.md:696-715`) pins `/` as a
raw-bearing route that renders a representative prompt label. The new `ホーム`
shows repository rows rather than traces, so it has no prompt to render. The
restatements that must change with it:

- `docs/requirements.md:51` and `docs/requirements.md:88` — the prompt-label
  requirement and the Overview / trace-list prompt-label fetch contract.
- `docs/spec.md:48`, `docs/spec.md:1141`, and `docs/spec.md:1144` — the same
  contract and its client-side fetch form.
- `docs/specifications/security-data-boundaries.md:164-195` — the closed
  enumeration of raw-bearing surfaces, which lists the dashboard `/` and the
  Overview prompt-label fetch.
- `docs/specifications/security-data-boundaries.md:631-640` and `:649-653` — the
  Overview and trace-list prompt-label fetch contracts.
- `docs/specifications/layers/telemetry-ingestion.md:485` and
  `docs/specifications/layers/raw-store-normalization.md:353`.

**Four surfaces are added.** `/repos/{repositoryName}`, `/repos-unknown`, their
`/sessions/{sessionId}` children, and `/sessions/{sessionId}/instruction-label`
are raw-bearing and must be added to the closed enumeration at
`docs/specifications/security-data-boundaries.md:164-195` and to the raw-surface
statement at `docs/spec.md:1141`.

This is a net change to the raw-bearing set, not merely a narrowing. Every added
surface carries the existing controls unchanged, and no control is relaxed, but the
set itself is pinned and therefore cannot be extended silently.

### Trace detail tabs

D042 pins a tab-less trace detail with a vertical flow / waterfall segment
switch. The seven-section Trace Workspace reintroduces tabs and supersedes that
part of D042.

### Token normalization display rule

`docs/specifications/layers/raw-store-normalization.md:233-261` pins that
`input_tokens` includes cache reads, that a missing cache value is treated as
`0`, that pre-v4 nulls are fully uncached, that the Overview headline is uncached
input plus output, and that effective input is `cache_read * 0.1`. The
implementation aggregates with `COALESCE(SUM(...), 0)`
(`RawTelemetryStore.Overview.cs:13-22`, `MonitorTraceRollup.cs:194-218`).

These requirements supersede only the "missing cache value is `0`" rule, and
only for the new display surfaces listed in the Goal.

### Comparison semantics

`docs/specifications/interfaces/canvas-effect-comparison.md:111-143` and
`EffectVerdictEngine.cs:29-80` make the median of all-Run `total_tokens` the
efficiency measure and can produce `regressed` from it when quality is equal.
`EffectVerdictEngineTests.cs:129-174` pins that behaviour.

These requirements change neither the producer, the receipts, nor those tests.
They constrain only what `要確認` admits.

### Session read contract

`docs/specifications/interfaces/canvas-session-workspace.md:345-397` freezes
`/api/session-workspace/sessions` as `{items}` only, newest first, maximum 200,
with no filter or pagination, and freezes the detail response at exactly five
top-level fields. That route is not modified; `/api/workspace/v1/sessions` is its
declared successor.

`canvas-session-workspace.md:437-438` pins that raw event content is served only
by the existing `GET /sessions/{id}/events/{eventId}/content` route. The new
`/sessions/{sessionId}/instruction-label` route adds a second raw-bearing read of
instruction content and therefore supersedes that "only" clause. The extension is
narrow: the new route returns an abbreviated label rather than event content,
carries the same same-origin, `no-store`, and `--sanitized-only` controls, and
defers item-level read denial to the same Retention catalog authority. Full event
content remains available only through the existing route.

The same contract at `canvas-session-workspace.md:389` freezes `content_state` to
`available | not_captured | redacted | unsupported | expired_pending_deletion`,
matching the `NOT NULL` closed schema in
`SqliteSessionStore.cs:2016` and the assertions in
`SessionWorkspaceRouteTests.cs:48,238,323,388`. These requirements **conform** to
those five values and do not supersede them. The one Session-level display state
introduced here, `no_event`, is an additive value of the new `instruction_state`
field on `/api/workspace/v1/sessions` and
`/sessions/{sessionId}/instruction-label`. It is never written to
`session_events.content_state` and never appears in the frozen
`/api/session-workspace/*` responses, so that contract is unchanged.

### Repository label role

D040 (`docs/decisions.md:1249`, `1260-1274`) defines `repository_name` as a
nullable sanitized display label and stores no owner, full name, or unique
identity. These requirements additionally use the exact value as the grouping
key of the display hierarchy and as a route segment. That role extension is recorded
explicitly: the equivalence class of an exact label is treated as one repository
for display, and it is not Session identity or merge evidence.

### Canvas presentation ownership

The Canvas contract currently owns the whole Session Workspace presentation, not
only Improve and Compare. The upper sources state it too and must be superseded
with the interface files, not after them:

- `docs/requirements.md:84-86` — Canvas Improve, Canvas proposal apply, and Canvas
  effect comparison as the owning surfaces.
- `docs/spec.md:64-70` — the same three, restated as current specification.

The following interface specifications must be superseded for the parts moved into
Local Monitor:

- `docs/specifications/interfaces/canvas-session-workspace-ui.md:5-7,24-26`
  (sidebar, Review, Evidence, Human Evaluation, instruction preview).
- `docs/specifications/interfaces/canvas-session-evidence.md` (Evidence
  presentation).
- `docs/specifications/interfaces/canvas-improvement-proposals.md:130-144`
  (Improve UI contract).
- `docs/specifications/interfaces/canvas-effect-comparison.md:216-228` (Compare
  UI contract).

Store and API authority already sit in Local Monitor, including the apply engine
(`docs/requirements.md:85`), so this changes UI ownership rather than the
security boundary.

### Helper-only full diff and source display

The helper-only restriction is pinned in six places that must all be superseded
together:

- `docs/specifications/interfaces/canvas-proposal-apply.md:10,60,140` — the
  canonical interface itself, which pins the full diff to the Canvas helper's
  local screen. Superseding only its downstream restatements would leave the
  canonical specification contradicting these requirements.

- `docs/requirements.md:85` — Canvas is a token-gated local helper that confirms
  the full diff and selected hunks.
- `docs/spec.md:68` — source/diff text and absolute paths do not leave the helper
  local display.
- `docs/spec.md:1159` — only the token-gated helper display handles the full diff.
- D054 (`docs/decisions.md:1702-1723`) — the apply surface is closed to a
  per-launch-token helper screen proxying the Local Monitor loopback surface.
- `docs/specifications/security-data-boundaries.md:1217-1224` — the Proposal
  Apply Boundary: the helper may show source and diff text only on its
  per-launch-token loopback local screen, and that text never enters an action
  DTO, log, persisted proposal metadata, repository-safe output, CI or static
  artifact, Issue or PR, or documentation.

The extension is narrow and must be stated as such: the Local Monitor's own
loopback, same-origin, CSRF-protected page becomes a second permitted display
surface for source and diff text. Everything else in that boundary is unchanged.
The apply engine, the `--apply-root` restriction, the all-or-nothing staleness
check, the snapshot and journal recovery, the single-use rollback precondition,
`Cache-Control: no-store`, absolute-path suppression, and the prohibition on
source or diff text reaching any action DTO, log, persisted metadata, or
repository-safe output all stay exactly as they are.

### Cross-surface links

Canvas pins "open Local Monitor" primary and secondary actions
(`canvas-session-workspace-ui.md:131-138`), and Retention allows a
navigation-only link carrying an exact Session ID (`docs/decisions.md:2233-2235`).

Decision: those links are retained as optional, non-authoritative affordances and
are not removed, because removing them is a separate Canvas change with its own
tests and no product benefit here. The new Local Monitor IA never depends on
them, and no deep link, helper-server URL, or selected-Session handoff is a
primary or auxiliary path. Continuity comes from the shared store and exact
identifiers.

### Cross-cutting search

`GET /api/monitor/trace-list` supports `q` as a TraceId substring plus `model`,
`status`, `period`, `sort`, `offset`, and `limit`, with no tool filter, and D042
C8 (`docs/decisions.md:1343-1345`) limits full-corpus prompt search. That route is
not modified; `/api/workspace/v1/traces` is its declared successor and adds the
tool filter. Full-corpus prompt search remains out of scope, so C8 is not
superseded.

## Out of scope for v1

- `session_failed`, `required_token_metric_missing`, efficiency-based 要確認
  reasons, and open alerts as 要確認 inputs.
- `作成後の回収状況` (post-creation cache reclaim).
- An immutable Human Evaluation identity.
- A Skill Discovery route beyond `Skill identity: 利用不可`.
- Sanitized export and raw local replay UIs.
- Any new source capability promotion, adapter, producer change, or new telemetry
  column. Additive indexes needed to keep the windowed aggregates exact are
  permitted, because they change no value and no contract.
- A `repository_identity` contract distinct from `vcs.repository.name`.
- Changes to the comparison producer, its receipts, or its tests.
- Changes to `/api/monitor/*` and `/api/session-workspace/*` v1.
- Changes to `sessions.updated_at`, `monitor_traces.first_seen_at`, or
  `monitor_traces.last_seen_at`.
- New page routes for Sources/Ingestion, Data Boundary, and Settings.
- Dual sanitized labels across every list.
- Full-corpus prompt search.

## Validation approach

- Derive test scope from `docs/requirements.md`, `docs/spec.md`, and the relevant
  file under `docs/specifications/`.
- Existing Playwright shell, Alert Center, Historical Import, and Cost page tests
  are updated in the same change that supersedes the navigation contract, not
  left failing.
- `SessionWorkspaceRouteTests` and `EffectVerdictEngineTests` must keep passing
  unchanged. If either needs modification, the change has drifted outside this
  requirements scope.
- New tests pin: the four count display forms; `acquisition_state` of `complete`
  and `unavailable` on `ホーム` and the absence of `bounded` there;
  `Repository不明` separation and last-place ordering; percent-encoded
  `{repositoryName}` round trip and case-sensitivity; the five v1 reason codes
  and the absence of the others;
  exclusion of a `Reasons` array containing only `tokens_regressed` from 要確認
  and its presence in `改善`; `新規処理合計: 算出不可` when `cache_read_tokens`
  is null; Reasoning `利用不可` at trace level; the quality status precedence
  table; all five `content_state` rows plus the `no_event` Session-level state;
  exact repository membership refusal on a nested Session route;
  the per-route `--sanitized-only` behaviour including `404` for
  `/sessions/{sessionId}/instruction-label`; and that `/api/workspace/v1/*`
  responses contain no instruction text.
- Five tests must pin these boundary cases:
  - a Trace whose `session_runs.trace_id` is non-null but whose binding state is
    not `exact_linked` still produces `unrecovered_trace_unbound`;
  - a membership column carrying a non-UTC offset, as a restored database can
    contain, makes the affected `ホーム` count `算出不可（不足: <column>）` while
    the corresponding list still includes and marks the row;
  - a Trace resolving to more than one exact-linked Session reports the
    multiple-candidate case rather than selecting the most recent one;
  - a Trace whose only exact-linked Session lies outside the most-recent 200 is
    **not** reported as `unrecovered_trace_unbound`;
  - two failing objective evaluations on one Session produce two review items with
    a deterministic order, not one aggregated item.
- Retention denial on `/sessions/{sessionId}/instruction-label` returns the frozen
  expiry response and never a success carrying a label.
- `sessions.last_seen_at` requires focused Session-store coverage:
  - an earlier `+09:00` value followed by a later `+00:00` value whose text sorts
    lower retains the later instant and its exact candidate text;
  - two differently represented values for the same instant preserve the
    existing stored text;
  - an invalid existing timestamp fails without changing the Session aggregate.
- Per-endpoint error applicability, cursor encoding, filter value sets, and the
  full request and response shapes are pinned by tests belonging to the
  downstream `local-monitor-workspace` interface specification, not by tests
  derived from this document.
- Pinned repository validation: `dotnet build CopilotAgentObservability.slnx`,
  `pwsh scripts\test\install-playwright-chromium.ps1`, then
  `dotnet test CopilotAgentObservability.slnx`.

## Open items

1. `docs/specifications/interfaces/local-monitor-workspace.md` must be authored
   before implementation. It carries the serialized contract for the four
   `/api/workspace/v1/*` endpoints and the instruction-label route: request and
   response shapes, property names, status codes, error bodies, cursor encoding,
   filter value sets, paging limits, and the multiple-candidate Session payload.
   The "Binding constraints on the new endpoints" section above is its input and
   is not restated there.
2. This document defines the full target IA and is larger than one implementation
   plan. It must be decomposed into sequenced work items before implementation.
   The navigation supersede plus its four Playwright suites is the natural first
   unit, because every later screen depends on the shell contract.

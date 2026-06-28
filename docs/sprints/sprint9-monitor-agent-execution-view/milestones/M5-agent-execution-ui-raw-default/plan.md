# Sprint9 M5 — Agent-Execution UI + raw default (Plan)

Status: **Implemented** — see [review.md](review.md) (recorded self-review; build
0/0, 509 tests passing).
Author role: Claude (orchestrator) per `CLAUDE.md`.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`. Sprint context:
[../../README.md](../../README.md) (M5 milestone row; *Safety boundary*
raw-bearing routes; D023).

## Objective

Deliver the functional agent-execution UI — a trace-detail page (Summary panel,
sub-agent span tree, per-turn token rollup) plus new trace-list columns — and
flip the raw default per D023: raw bodies rendered **inline by default**
(server-rendered, inert text), `--sanitized-only` turns them off,
`--enable-raw-view` removed. SSE / JSON stay sanitized. **Design polish is out of
scope** (later design sprint).

## Scope

In scope:
1. Trace-detail page: Summary panel (total tool calls / token usage / error
   count / duration), span timeline grouped into a **sub-agent tree**, per-turn
   token rollup.
2. New sanitized columns on the trace list.
3. Raw default flip (D023): raw bodies + PII shown **by default**, rendered
   inline as escaped **inert text** (framework default; never `Html.Raw`).
   `--sanitized-only` restores metadata-only mode (raw routes absent → `404`, PII
   excluded). `--enable-raw-view` removed.
4. Pin the **raw-bearing route set**: the trace-detail page becomes raw-bearing
   alongside the existing `GET /traces/{rawRecordId}/raw`; both enforce
   same-origin (`Origin` / `Sec-Fetch-Site` → cross-site `403`) **and**
   `Cache-Control: no-store`. Trace list, `/api/monitor/*`, and SSE stay
   sanitized (not raw-bearing).

Out of scope (deferred):
- The full security negative matrix + live validation (M6).
- The graphical Flow Chart + Cache Explorer views, theming, layout polish (later
  design sprint).

## Tasks
- [ ] Build the trace-detail page (Summary + sub-agent tree + per-turn tokens)
      from the sanitized projection.
- [ ] Add the new sanitized trace-list columns.
- [ ] Implement raw inline default + `--sanitized-only`; remove
      `--enable-raw-view`.
- [ ] Enforce same-origin + `no-store` on every raw-bearing route; keep
      list / JSON / SSE sanitized.

## Acceptance criteria
- Trace-detail page renders Summary + sub-agent tree + per-turn tokens; raw
  bodies inline as inert text (no `Html.Raw`).
- `--sanitized-only`: raw routes absent (`404`), PII excluded, no cacheable raw
  generated.
- `--enable-raw-view` removed.
- SSE / JSON remain sanitized.
- Existing tests stay green.

## Validation
```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```
Pass criteria: 0 build errors; existing tests stay green; UI + flag tests present
and passing. (Full cross-site / `no-store` / sanitized-only negative matrix is
M6.)

## Dependencies
- Depends on **M4** (read API) and **M2/M3** (projection + storage).
- Sub-agent tree rendering depends on the hierarchy confirmed at M6
  (human-gated); the M2 `parent_span_id`-absent fallback keeps the page
  functional on the unconfirmed shape.

## Review
- Challenge-reviewed via the Codex companion `review` / adversarial path
  (read-only) before implementation, per `CLAUDE.md`. Record the outcome here (or
  in a sibling `review.md`).

# Sprint9 M4 — Sanitized Read API (Plan)

Status: **Planned** — to be challenge-reviewed via the Codex companion `review`
path before implementation (per `CLAUDE.md`).
Author role: Claude (orchestrator) per `CLAUDE.md`.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`. Sprint context:
[../../README.md](../../README.md) (M4 milestone row; *Safety boundary*
sanitized-only JSON/SSE invariant).

## Objective

Extend the sanitized read API to surface the new projection: richer
`/api/monitor/traces` rows + a cursor-paginated span list endpoint. **Always
sanitized — no raw, no PII.**

## Scope

In scope:
1. Extend `/api/monitor/traces` rows with the new rollup columns (tokens,
   `turn_count`, `agent_invocation_count`, `duration_ms`, primary model).
2. Add a cursor-paginated span list endpoint
   (`/api/monitor/traces/{traceId}/spans` or `/api/monitor/spans`).
3. Sanitized-only; negative tests (no raw / PII in any response; invalid query
   → `400`).

Out of scope (deferred):
- UI (M5), the raw default flip + raw-bearing routes (M5), the full security
  matrix + live validation (M6).
- A JSON **raw** API — raw stays server-rendered only (README non-goal).

## Tasks
- [ ] Add the new rollup columns to the `/api/monitor/traces` row shape.
- [ ] Add the cursor-paginated span list endpoint (allowlist columns only).
- [ ] Add negative tests (no raw/PII; invalid query `400`) + per-attribute
      sanitization assertions at the API layer.

## Acceptance criteria
- Span list endpoint returns sanitized allowlist columns only; cursor pagination
  works.
- Negative tests: no raw / PII via JSON; invalid query → `400`.
- Per-attribute sanitization negative tests hold at the API layer.
- Existing API tests stay green.

## Validation
```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```
Pass criteria: 0 build errors; existing tests stay green; new endpoint +
negative tests present and passing.

## Dependencies
- Depends on **M3** (tables + columns) and **M2** (projection shape).

## Review
- Challenge-reviewed via the Codex companion `review` / adversarial path
  (read-only) before implementation, per `CLAUDE.md`. Record the outcome here (or
  in a sibling `review.md`).

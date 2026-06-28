# Sprint10 M1 — Specs & Decisions (Plan)

Status: **Done** — docs-only; recorded self-review below (documentation-only
change permitted by `docs/agent-guides/review-workflow.md`). build 0/0, tests
533 passing (300 ConfigCli + 233 LocalMonitor), non-regression.
Author role: Claude (orchestrator) per `CLAUDE.md`.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`. Sprint context:
[../../README.md](../../README.md) (milestone row + *Decision* (D024–D028) /
*Scope* / *Safety boundary* / *Spec changes required* sections).

## Objective

Promote the agreed Sprint10 decisions (D024–D028) into the canonical specs and
pin the monitor **design-views** capability (Flow Chart / Cache Explorer /
timeline filter-sort / themed UI) as **sanitized, client-side presentation over
the existing spans API**, so M2–M6 implement against fixed decisions, scope, and
safety boundary. **No code in M1** — no schema, API field, raw-boundary, or
`wwwroot` change. M2–M6 are gated by this milestone.

## Scope

In scope (docs only):
1. `docs/decisions.md` — add **D024** (narrow the "design views deferred"
   non-goal; D001 / D021 / D020 / D023 preserved), **D025** (Cytoscape.js + dagre
   vendored client-side visualization), **D026** (Cache Explorer
   sanitized-metrics-only / trace-internal; prefix-diff + cross-trace out),
   **D027** (VS Code dark theme; DADS not applied to Local Monitor), **D028**
   (Noto Sans JP / Mono vendored).
2. `docs/requirements.md` — §3 add the design-views capability to the Local
   Ingestion Monitor entry (sanitized presentation over the existing spans API);
   §4 note DADS non-applicability (D027) and A2 prefix-diff / cross-trace out
   (D026), preserving the VS Code-Debug-UI / internal-log non-goals (D001 / D021).
3. `docs/spec.md` — monitor section: the four views (Summary / Timeline / Flow
   Chart / Cache), the TraceDetail two-section tab architecture (JS sanitized
   tabs + Razor raw preview), the vendored client dependency set (Cytoscape.js +
   dagre, Noto fonts), no build step; Public Interfaces unchanged (no new
   endpoint / field — views are client-side over the existing spans API).
4. `docs/specifications/layers/telemetry-ingestion.md` — note the design views
   consume the existing `GET /api/monitor/traces/{traceId}/spans`; no new
   endpoint / field.
5. `docs/specifications/security-data-boundaries.md` — Cytoscape.js + dagre +
   Noto fonts vendored (no CDN; no external fetch; offline / loopback preserved);
   new client views consume sanitized `/api/monitor/*` JSON / SSE only; A2
   prefix-diff and cross-trace out (D026); the rest of the boundary unchanged.
6. `docs/task.md` — Planned Work roadmap pointer for Sprint10.
7. `docs/user-guide/local-monitor.md` — user-facing sections for the new views
   (Summary / Timeline filter-sort / Flow Chart / Cache) and the themed UI.

Out of scope (deferred):
- All product code, `wwwroot` changes, asset vendoring, schema / API / raw
  boundary changes (M2–M6).
- M2–M6 per-milestone `plan.md` (authored at each milestone's execution time,
  per the Sprint9 cadence).
- Vendor provenance SHA / version (recorded in M2 for fonts, M3 for
  Cytoscape / dagre).

## Tasks
- [ ] Add D024–D028 to `decisions.md` in the canonical style (Japanese narrative,
      `Status: Accepted`).
- [ ] Update `requirements.md` §3 (capability) and §4 (DADS / A2 out notes).
- [ ] Update `spec.md` monitor section + Public-Interfaces "no new field" note.
- [ ] Update `telemetry-ingestion.md` (views consume existing spans API).
- [ ] Update `security-data-boundaries.md` (vendored, sanitized-only consumption,
      A2 prefix-diff / cross-trace out).
- [ ] Update `docs/task.md` Planned Work pointer.
- [ ] Update `docs/user-guide/local-monitor.md` with the new view sections.
- [ ] Recorded self-review (cross-doc consistency); record outcome here or in a
      sibling `review.md`.

## Acceptance criteria
- D024–D028 present in `decisions.md`; D001 / D020 / D021 / D023 preserved and
  referenced, not contradicted.
- requirements / spec / specifications / user-guide are consistent with the
  README's *Decision* / *Scope* / *Safety boundary*; no product spec hidden only
  in sprint notes (AGENTS.md "Do Not").
- Docs-only: no code or test delta. The sanitized-JSON / SSE invariant and the
  raw-bearing route set are textually unchanged.

## Validation
```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```
Pass criteria: build / test unchanged and green (non-regression only — M1 is
docs-only). Primary validation is the recorded self-review + cross-doc
link / consistency check.

## Dependencies
- No upstream dependency. **Gates M2–M6**: the design-views scope, the vendored
  dependency set, and the sanitized-only consumption invariant must be pinned
  here before implementation.

## Review
- Recorded self-review (documentation-only is permitted per
  `docs/agent-guides/review-workflow.md`). The Codex companion `review`
  (read-only adversarial) is available on request but is not auto-delegated
  (AGENTS.md Subagent policy). Outcome recorded below.

### Review outcome
Recorded self-review (cross-doc consistency), 2026-06-28:
- D024–D028 added to `decisions.md` in canonical style. D001 / D020 / D021 / D023
  referenced as preserved, not contradicted (D024 narrows only the "design
  deferred" side).
- `requirements.md` §3 carries the design-views capability (sanitized
  presentation over the existing spans API; no input / schema / API field / raw
  boundary change); §4 adds DADS non-applicability (D027) and A2 prefix-diff /
  cross-trace out (D026), keeping the D001 / D021 non-goals.
- `spec.md` monitor section describes the four views + the TraceDetail
  two-section tab architecture + vendored Cytoscape.js / dagre / Noto; Public
  Interfaces explicitly note "no new endpoint / field" — consistent with
  `telemetry-ingestion.md` and `security-data-boundaries.md`.
- `telemetry-ingestion.md` notes the views consume the existing
  `GET /api/monitor/traces/{traceId}/spans`; field coverage confirmed (the
  sanitized row already carries every field the views need, incl. cache token
  counts).
- `security-data-boundaries.md` records the sanitized-only consumption invariant,
  vendored/no-CDN, and the D026 out-of-scope items; the raw-bearing route set and
  default posture are textually unchanged.
- `task.md` Planned Work points to Sprint10; the stale Sprint9 "M1 実施中" status
  corrected. `user-guide/local-monitor.md` documents the four tabs + themed UI
  (Japanese, user-facing) without leaking vendored-asset internals.
- No product spec is hidden only in sprint notes (AGENTS.md "Do Not"): every
  decision is promoted into `decisions.md` + the canonical specs. No code / test
  delta; the sanitized-JSON / SSE invariant and raw-bearing route set unchanged.
- Validation: `dotnet build` 0 warnings / 0 errors; `dotnet test` 533 passing,
  0 failed, 0 skipped (non-regression). Live VS Code Copilot Chat validation
  remains human-gated (inherited; M1 docs-only, not applicable).

Open item flagged (not blocking M1): the Sprint9 README milestone table still
reads "Planned" for all rows (a pre-existing Sprint9 inconsistency, out of
Sprint10 M1 scope).

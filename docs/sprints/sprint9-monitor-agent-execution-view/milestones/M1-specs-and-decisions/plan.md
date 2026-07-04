# Sprint9 M1 — Specs & Decisions (Plan)

Status: **Planned** — to be challenge-reviewed via the Codex companion `review`
path before implementation (per `CLAUDE.md`).
Author role: Claude (orchestrator) per `CLAUDE.md`.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`. Sprint context:
[../../README.md](../../README.md) (milestone row + *Decision* / *Data
classification* / *Safety boundary* / *Spec changes required* sections).

## Objective

Record the agreed Sprint9 decisions and promote the agent-execution-detail
behavior into the canonical specs, so M2–M6 implement against a pinned schema,
sanitization policy, route set, and safety boundary. **No code** in M1.

## Scope

In scope:
1. `docs/decisions.md` — add **D021** (narrow the "no Agent Debug View"
   non-goal), **D022** (span-level sanitized projection), **D023** (raw
   default-on + `--sanitized-only`); **update D020**; **annotate D001** with an
   Update note narrowing its "no re-implementation" rationale to
   input-source / UI-replica only.
2. `docs/requirements.md` — §3 add the agent-execution-detail capability to the
   Local Ingestion Monitor entry; §4 rewrite the "Agent Debug View" non-goal per
   D021; §8 record raw-default-on + `--sanitized-only` (D023).
3. `docs/spec.md` — monitor section: sanitized span projection, read API, UI,
   flag change.
4. `docs/specifications/layers/telemetry-ingestion.md` — new sanitized read API
   endpoints; `--sanitized-only` flag; default-posture note.
5. `docs/specifications/layers/raw-store-normalization.md` — `monitor_spans`
   allowlist schema + additive `monitor_traces` columns; projection-version +
   backfill; span idempotency key (incl. ordinal); per-field sanitization
   policy; no-double-count token rule.
6. `docs/specifications/security-data-boundaries.md` — raw default-on,
   `--sanitized-only`, sanitized-only JSON/SSE invariant, per-field sanitization
   policy, raw-bearing route set + same-origin + `Cache-Control: no-store`,
   extended accepted risk.
7. `docs/task.md` — roadmap pointer; Local Ingestion Monitor user guide — new
   view + flag.

Out of scope (deferred):
- Any code, schema migration, or projection change (M2/M3).
- Read API (M4), UI + raw default flip (M5), security matrix + live validation
  (M6).

## Tasks
- [ ] Draft D021–D023, the D020 update, and the D001 Update note in `decisions.md`.
- [ ] Update `requirements.md` §3/§4/§8.
- [ ] Update `spec.md` monitor section.
- [ ] Update `telemetry-ingestion.md`, `raw-store-normalization.md`,
      `security-data-boundaries.md`.
- [ ] Update `docs/task.md` roadmap pointer + the Local Ingestion Monitor guide.
- [ ] Run the Codex companion `review` over the spec deltas; record the outcome.

## Acceptance criteria
- D021–D023 present; D020 updated; D001 carries the Update note — no residual
  contradiction between D001's rationale and an OTel-derived view (README F6).
- requirements / spec / specifications are consistent with the README's
  *Decision* / *Data classification* / *Safety boundary*; no product spec hidden
  only in sprint notes (per AGENTS.md "Do Not").
- Docs-only: no code or test delta.

## Validation
```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```
Pass criteria: build/test unchanged and green (non-regression only — M1 is
docs-only). Primary validation is review + cross-doc link/consistency check.

## Dependencies
- No upstream dependency. **Gates M2–M6**: schema, per-field sanitization policy,
  token rollup rule, and the raw-bearing route set must be pinned here before
  implementation.

## Review
- Challenge-reviewed via the Codex companion `review` / adversarial path
  (read-only) before implementation, per `CLAUDE.md` and the Sprint9 README
  cadence. Record the outcome here (or in a sibling `review.md`).

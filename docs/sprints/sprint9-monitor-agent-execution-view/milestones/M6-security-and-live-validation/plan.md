# Sprint9 M6 — Security + Live Validation (Plan)

Status: **Implemented** — DR6 negative matrix asserted (build 0 errors; 514
tests green incl. 5 new). Live validation: **Part A (GitHub Copilot CLI 1.0.65)
COMPLETE** (2026-06-28); **Part B (VS Code Copilot Chat) PENDING USER**
(human-gated). Evidence: [live-validation.md](live-validation.md); traceability:
[review.md](review.md).
Author role: Claude (orchestrator) per `CLAUDE.md`.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`. Sprint context:
[../../README.md](../../README.md) (M6 milestone row; *Safety boundary*;
*Validation*).

## Objective

Prove the new default posture and the new surfaces against the DR6 negative
matrix, and perform the human-gated live VS Code Copilot Chat validation of real
tool / MCP / sub-agent / token emission **and** the sub-agent child-span
hierarchy.

## Scope

In scope:
1. DR6 negative matrix for the raw-default-on posture:
   - raw never via `/api/monitor/*` JSON or SSE;
   - raw HTML routes reject cross-site → `403`;
   - `Cache-Control: no-store` on **all** raw-bearing routes (not only the
     raw-detail route); `--sanitized-only` generates no cacheable raw;
   - `--sanitized-only` ⇒ raw routes `404` + PII excluded;
   - per-attribute sanitization negative tests (email/path/secret guarded out,
     including under `--sanitized-only`);
   - non-loopback / bad-`Host` rejection;
   - raw never logged or committed.
2. Live VS Code Copilot Chat validation (**human-gated**): real tool / MCP /
   sub-agent / token emission + sub-agent child-span hierarchy observed. Record
   evidence: date, environment, profile, endpoint shape, trace id / raw record
   id, monitor port, VS Code / extension version, whether `--sanitized-only` was
   set, and confirmation of the sub-agent child-span hierarchy.

Out of scope (deferred):
- Design polish; remote / multi-user; anything beyond the README scope.

## Tasks
- [x] Implement the DR6 negative matrix (each assertion above) on synthetic
      fixtures. (See [review.md](review.md) traceability table.)
- [x] Assert `no-store` on **all** raw-bearing routes (incl. the inline-raw
      trace-detail page), not only `GET /traces/{rawRecordId}/raw`.
      (`AllRawBearingRoutes_SetNoStore`.)
- [x] Assert `--sanitized-only` ⇒ raw routes `404` + PII excluded + no cacheable
      raw. (`SanitizedOnly_ExcludesPiiFromAllReadApis` + the `404` route tests.)
- [x] Live validation — **Part A (GitHub Copilot CLI) recorded complete**;
      **Part B (VS Code, human-gated) recorded as a user checklist** in
      [live-validation.md](live-validation.md).

## Acceptance criteria
- Full negative matrix passes (every assertion above green).
- `no-store` asserted on all raw-bearing routes.
- Live validation evidence recorded — or, if human-gated and not runnable, the
  blocker and exact missing evidence stated (do not substitute a different
  command, per AGENTS.md).

## Validation
```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```
Pass criteria: 0 build errors; existing tests stay green; the full DR6 negative
matrix present and passing. **Plus** the human-gated live VS Code Copilot Chat
validation evidence recorded (synthetic fixtures are not a substitute for the
live run).

## Dependencies
- Depends on **M2–M5**.
- Live validation is **human-gated** (cannot be forced); the automated matrix
  uses synthetic OTLP fixtures only.

## Review
- Challenge-reviewed via the Codex companion `review` / adversarial path
  (read-only) before implementation, per `CLAUDE.md`. Record the outcome here (or
  in a sibling `review.md`); a `live-validation.md` may hold the live-run
  evidence (cf. Sprint8 M6).

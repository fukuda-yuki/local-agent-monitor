# Sprint8 M6 Security And Live Validation — Review

Status: **Verification complete; live validation BLOCKED.** The DR6 negative
security matrix, readiness-failure semantics, restart recovery, and raw
non-logging are proven by tests and a real-process synthetic run. The real VS Code
Copilot Chat live validation could not be performed (human-gated). Per the M6 plan
(Task 5, Step 6), **M6 and Sprint8 are not marked complete.** See
[`live-validation.md`](live-validation.md).

Review recorded by the orchestrator (Claude). M6 is verification-only and exposed
no production defects, so no implementation change was required and nothing was
delegated for fixes.

## Scope Reviewed

- DR6 negative security matrix at the HTTP level.
- Readiness `503` semantics under saturation / lag / unknown status.
- Restart recovery on the same SQLite DB.
- Raw non-logging / no-leak in error responses.
- Synthetic full validation (build + test) and a real-process synthetic ingestion
  run.
- Live VS Code monitor validation (blocked — recorded, not completed).

## Changed Files

Tests only (`tests/CopilotAgentObservability.LocalMonitor.Tests/`):

- `MonitorSecurityBoundaryTests.cs` (new) — DR6 matrix:
  - default `/`, `/ingestions`, `/traces`, `/diagnostics`,
    `/api/monitor/ingestions`, `/api/monitor/traces` never return raw / PII even
    with `--enable-raw-view`;
  - raw route absent without the flag (`404`); with the flag, cross-site /
    foreign-origin reads are `403`;
  - cross-origin `POST /events` is refused (`405`/`404`); SSE is GET-only;
  - non-loopback `Host` header is rejected on page routes (`400`);
  - same-DB restart recovers projection with no loss and reaches `ready`;
  - raw markers / DB path / user name never appear in error responses.
- `MonitorReadinessFailureTests.cs` (new) — `/health/ready` mapping: momentary
  queue-full backpressure and commit-timeout stay `200` (`degraded`) then become
  `503` (`ingestion_stalled`) after the stall threshold; sub-threshold projection
  lag is `200` (`degraded`) and at threshold is `503` (`projection_lag_exceeded`);
  unknown projection status is `503` (`projection_status_unknown`) with the pinned
  body schema present.
- `MonitorTestHelpers.cs` (new) — shared `MutableTimeProvider` and ready-state
  health helper.
- `MonitorHealthTests.cs` — use the shared `MutableTimeProvider`.

No production files changed: every M6 assertion passed against the existing M3–M5
implementation.

## Source-Of-Truth Comparison

- `security-data-boundaries.md` / D020 (DR6): the matrix proves sanitized default
  surfaces, opt-in raw gating + same-origin, non-loopback rejection, SSE GET-only,
  and raw non-logging. Consistent with the de-scoped display hardening (no CSP /
  sanitizer matrix; inert framework encoding only).
- `telemetry-ingestion.md`: readiness `ready`/`degraded`/`not_ready` → `200`/`200`/
  `503` mapping and reason tokens verified at the HTTP level with the pinned body.
- `requirements.md` §10: required `dotnet build` / `dotnet test` run; live VS Code
  evidence required and recorded as blocked.

## Negative Security Matrix Result

All security-boundary tests pass (`7/7`). Default UI / API / SSE never return raw
or PII; the raw route is opt-in, same-origin, inert; error responses do not leak
markers, DB path, or user name.

## Readiness Saturation Result

All readiness-failure tests pass (`5/5`). Sustained ingestion stall and
projection-lag-exceeded and unknown projection status return `503`; momentary
backpressure and sub-threshold lag return `200`.

## Restart Recovery Result

A second monitor on the same SQLite DB catches up the projection and returns
`ready` (`200`). Passing.

## Raw Non-Logging Result

After ingesting a sensitive payload, a malformed `POST /v1/traces` returns a
sanitized `400` whose body contains no raw markers, no DB path, and no user name.
Passing.

## Live Validation Evidence Summary

BLOCKED. Real VS Code Copilot Chat emission to `127.0.0.1:4320` was not performed
(needs a human, credentialed Copilot session). Ready-state evidence is recorded in
[`live-validation.md`](live-validation.md): correct monitor-targeted env
(`OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320`, `http/protobuf`), a
real-process synthetic ingestion loop (`POST /v1/traces` → `200`,
`/api/monitor/ingestions` → 1 item, `/health/ready` → `200`), and protobuf receipt
covered by automated tests.

## Commands Run And Results

- `dotnet build CopilotAgentObservability.slnx -warnaserror`: 0 warnings, 0 errors.
- `dotnet test CopilotAgentObservability.slnx`: 445 passing, 0 failing, 0 skipped
  (300 Config CLI + 145 LocalMonitor; above the M5 count of 433).
- `profile-vscode-env --profile raw-local-receiver --target monitor`: endpoint
  `http://127.0.0.1:4320`, `http/protobuf` (not `4319`).
- Real-process synthetic ingestion run on `127.0.0.1:4320`: confirmed (above).

## Residual Risks Accepted By D020

- Single-trusted-local-user model; same-local-user-process loopback reads are out
  of scope; `--enable-raw-view` reachable for the process lifetime in any launch
  mode; no display-side defense-in-depth beyond inert text rendering.

## Outstanding (blocks Sprint8 completion)

- Real VS Code Copilot Chat HTTP/protobuf live validation at the monitor, with
  sanitized evidence (human-gated). Until then, M6 and Sprint8 stay open.

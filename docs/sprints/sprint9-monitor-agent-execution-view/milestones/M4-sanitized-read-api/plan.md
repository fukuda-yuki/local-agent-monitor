# Sprint9 M4 — Sanitized Read API (Plan)

Status: **Implemented** (2026-06-27). Implementation delegated to a Sonnet
subagent and verified by the orchestrator (build/test + baseline-diff review +
sanitizer/attribute-key trace of the negative tests).
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
- [x] Add the new rollup columns to the `/api/monitor/traces` row shape.
- [x] Add the cursor-paginated span list endpoint
      (`GET /api/monitor/traces/{traceId}/spans`, allowlist columns only).
- [x] Add negative tests (no raw/PII; invalid query `400`) + per-attribute
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
- Recorded orchestrator self-review (per `CLAUDE.md` review-workflow; user
  authorized Sonnet delegation for this milestone in place of the Codex path).
  The orchestrator verified the diff against the sanitized-only invariant and
  traced each negative test end-to-end: the injected markers reach the projection
  via the real builder attribute keys (`gen_ai.tool.name`,
  `github.copilot.tool.parameters.mcp_tool_name`, `gen_ai.agent.name`,
  `error.type`) and are dropped by `MeasurementSanitizer.IsUnsafeStringValue`
  (email regex, `[A-Za-z]:\\` path rule, case-insensitive `secret` substring) —
  so the assertions are meaningful, not trivially passing.
- Validation: `dotnet build` 0 warnings / 0 errors; `dotnet test` **502/502**
  green (300 ConfigCli + 202 LocalMonitor; +6 over the 496 baseline). No storage,
  projection, or sanitization logic changed — read-API surface + tests only.
- Files changed: `MonitorProjectionRows.cs`, `RawTelemetryStore.cs`,
  `IMonitorProjectionStore.cs`, `MonitorHost.cs` (production);
  `MonitorProjectionApiTests.cs` (new tests) plus mechanical interface/allowlist
  updates in `ProjectionWorkerTests.cs` and `MonitorProjectionStoreTests.cs`.

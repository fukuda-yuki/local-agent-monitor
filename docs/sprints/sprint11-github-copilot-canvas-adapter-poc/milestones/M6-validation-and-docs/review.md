# Sprint11 M6 — Recorded Self-Review

Sprint-local review evidence, not product behavior. Review perspectives per
[M6 plan](plan.md): spec compliance, Canvas skill compliance, raw/PII boundary,
loopback/server lifecycle, input schema and bounded DTOs, docs consistency,
generated/runtime artifact exclusion.

> **Issue #35 update:** This review records that M6 live validation used
> `--sanitized-only`. That is historical evidence only, not a current Canvas
> requirement. D030 and Issue #35 allow Canvas adapter use with the normal
> raw-default Local Monitor while keeping Canvas action/log/artifact raw / PII
> exclusions.

This milestone is doc-only + validation evidence (no product code changes),
so a recorded self-review per `docs/agent-guides/review-workflow.md` is
appropriate.

## Changes

| File | Change |
|---|---|
| `docs/user-guide/local-monitor.md` | Added "GitHub Copilot app Canvas adapter" section (setup, actions, security boundary) |
| `docs/task.md` | Updated Sprint11 status from "計画中 / M5 planned" to "完了" with M6 evidence pointer |
| `docs/sprints/.../README.md` | M6 status table row updated to "Implemented — live Canvas validation completed" |
| `docs/sprints/.../M6-validation-and-docs/live-validation.md` | New: live Canvas runtime validation evidence |

## Review perspectives

### 1. Spec compliance

- ✅ Extension reads only sanitized `/api/monitor/*` and `/health/ready`.
  Confirmed via `list_canvas_capabilities` and live action invocations.
- ✅ `list_recent_traces.limit` bounded to 1..50; `limit=100` rejected.
- ✅ No new telemetry input, schema, endpoint, query parameter, response field,
  raw route, normalized dataset field, or dashboard contract added.
- ✅ Historical `--sanitized-only` validation: raw route 404, no raw section in
  TraceDetail, sanitized tabs present. This proves metadata-only compatibility;
  it is not a current Canvas launch requirement.
- ✅ D029/D030 boundary invariants preserved.

### 2. Canvas skill compliance

- ✅ `/create-canvas` remains a skill at `.github/skills/create-canvas/SKILL.md`,
  not a `.github/prompts/*.prompt.md` file.
- ✅ Extension uses `joinSession({ canvases: [createCanvas({...})] })`.
- ✅ Extension binds to `127.0.0.1` only.
- ✅ Diagnostics use the provided log; no `console.log` (contract test asserts).
- ✅ No `package.json`, `node_modules`, or dependencies added.

### 3. Raw/PII boundary

- ✅ All 5 action responses inspected: no raw prompt/response bodies, tool
  arguments/results, PII, credentials, tokens, local sensitive paths, raw OTLP
  payloads, or generated telemetry content.
- ✅ `sanitizeDto()` in extension strips forbidden keys (regex:
  `raw|payload|prompt|content|argument|result|user|email|credential|secret`).
- ✅ Traces proxy returns `compactTrace` shape only (no raw fields).
- ✅ Helper page HTML escapes all values (`escapeHtml`); no `Html.Raw`-equivalent.
- ✅ Evidence file contains only action names, sanitized metadata, status codes,
  and boundary assertions — no captured telemetry or raw data.

### 4. Loopback/server lifecycle

- ✅ Helper URL host: `127.0.0.1` (confirmed live).
- ✅ Helper page GET `/` without token: 401.
- ✅ Helper page GET `/` with wrong token: 401.
- ✅ Traces proxy without token: 401.
- ✅ `/analyze` with wrong token: 401; with invalid traceId: 400; with invalid
  focus: 400.
- ✅ `onClose()` closes servers (code inspection + contract test).

### 5. Input schema and bounded DTOs

- ✅ `monitor_health`: no required input, works when input omitted.
- ✅ `list_recent_traces`: `limit` required (1..50), `status` enum (ok/error),
  `model` string (1..100 chars), `additionalProperties: false`.
- ✅ `get_trace_summary`/`get_trace_span_tree`/`get_cache_summary`: `traceId`
  required, pattern `^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$`, maxLength 128,
  `additionalProperties: false`.
- ✅ Invalid traceId (spaces) rejected by pattern.
- ✅ `limit > 50` rejected by maximum.
- ✅ Span tree bounded to `MAX_TREE_NODES=50`, cache turns to
  `MAX_CACHE_TURNS=50`, top spans to `MAX_TOP_SPANS=10`.
- ✅ `hierarchy_status` returned: `complete` / `flat_missing_parent_ids` /
  `flat_incomplete_parent_links`.

### 6. Docs consistency

- ✅ `docs/user-guide/local-monitor.md`: Canvas adapter section added in
  Japanese, consistent with existing document language and tone.
- ✅ `docs/task.md`: Sprint11 status updated with M6 evidence pointer.
- ✅ Sprint11 README: M6 status table and notes updated.
- ✅ No product spec (`docs/requirements.md`, `docs/spec.md`,
  `docs/specifications/`) changes needed — specs were updated in M1 and are
  still accurate for M6.

### 7. Generated/runtime artifact exclusion

- ✅ No `node_modules`, `package.json`, or dependencies added.
- ✅ Only `extension.mjs` in `.github/extensions/otel-monitor-canvas/`.
- ✅ `data/m6-validation.db` and `data/*.db-*` are local runtime artifacts,
  not committed (gitignore covers data/).
- ✅ `node --check extension.mjs` passes.
- ✅ No captured telemetry, monitor output, or raw data in committed files.

## Known limitation

`monitor_health` with explicit `input: {}` fails because the Copilot CLI
tool framework serializes empty objects as the JSON string `"{}"` before
schema validation. When `input` is omitted (the correct call pattern for
no-input actions), the action works correctly. This is a tool-layer
serialization quirk, not an extension code defect. Documented in the
evidence file.

## Conclusion

M6 acceptance criteria are met. Sprint11 is complete with live Canvas runtime
validation evidence, repository validation green, metadata-only compatibility
verified, server lifecycle checks passed, and user-facing docs updated. Issue
#35 / D030 later removed `--sanitized-only` as a Canvas startup requirement.

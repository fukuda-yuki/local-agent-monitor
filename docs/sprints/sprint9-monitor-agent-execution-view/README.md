# Sprint9: Local Ingestion Monitor — Agent Execution Detail

Sprint9 deepens the **Local Ingestion Monitor** (Sprint8) so a single trusted
local user can understand *what the agent actually did* inside one trace, not
just whether ingestion was healthy. The monitor stops collapsing every span into
counts and instead surfaces a per-span, sanitized **agent-execution view** built
entirely from the OTel telemetry it already receives.

The user-stated goal: by looking at the monitor alone, see at minimum —

1. **Which tools / MCP the agent invoked** (per call, by name).
2. **Whether each call succeeded or failed**.
3. **Sub-agent launches** — the instructions/responses exchanged, the sub-agent's
   model, and its token usage.
4. **Total tokens per turn**.

This is consciously close to the **VS Code Agent Debug View** (Logs timeline,
sub-agent tree, per-event input/output expansion, Summary aggregates of total
tool calls / token usage / error count / duration). That view is the explicit
reference. See [research-notes.md](research-notes.md).

**Design polish is out of scope for Sprint9** (a later design sprint owns visual
layout, the graphical Flow Chart, and the Cache Explorer). Sprint9 is
**data + function first**: the sanitized projection, the read API, and a
functional UI that renders the data correctly.

## Decision

Sprint9 adopts the following decisions (resolved with the product owner on
2026-06-27). They are recorded in `docs/decisions.md` during M1 as **D021–D023**
and update **D020**.

- **D021 — Narrow the "no Agent Debug View" non-goal.** `docs/requirements.md`
  §4 currently lists "VS Code Agent Debug / Chat Debug View 相当の UI" as a
  non-goal. Sprint9 narrows it: the monitor **may** present an OTel-derived,
  sanitized agent-execution view. **D001 is preserved** — input stays the
  official OpenTelemetry signals the monitor receives; VS Code internal logs,
  `workspaceStorage`, and `chatSessions` are **still not** input sources, and we
  do **not** re-implement VS Code's in-editor debug UI. The non-goal is rewritten
  to forbid *those input sources / a UI replica*, not a telemetry-derived view.
  D021 also adds an **Update note to D001** (same pattern as D019→D020) so D001's
  rationale ("VS Code Agent Debug / Chat Debug View … 本製品では再実装しない") no
  longer reads as forbidding an OTel-derived view; only the input-source and
  UI-replica clauses survive.
- **D022 — Span-level sanitized projection.** Add a per-span sanitized
  projection (`monitor_spans`) plus additive token/turn/agent rollup columns on
  `monitor_traces`. Tool / MCP / agent / model **names**, operation/category,
  **token counts**, status, and `error.type` are **sanitized metadata** (default
  surface). Tool call **arguments/results** and sub-agent **instructions/
  responses** (prompt/response bodies) remain **raw** (see D023). The full
  classification — including the **per-field sanitization policy** and the
  **no-double-count token rollup rule** — is in
  [Data classification](#data-classification).
- **D023 — Raw bodies shown by default, with a safety-valve flag (updates
  D020).** The Sprint8 posture (raw **off** unless `--enable-raw-view`) is
  reversed for this single-trusted-local-user tool: bodies are shown **by
  default**, rendered inline. A new `--sanitized-only` flag restores the old
  metadata-only mode (raw routes absent → `404`, PII excluded) for health-check
  or screen-sharing runs. `--enable-raw-view` is removed (its behavior is now the
  default). **All cross-machine controls from D020/DR6 are unchanged**: loopback
  bind, `Host`-header validation, CORS disabled, same-origin + `Cache-Control:
  no-store` on every raw-bearing route, and **no raw / PII in logs or
  repository-committed outputs**. The boundary invariant **"raw is served only by
  server-rendered routes; `/api/monitor/*` and SSE never carry raw / PII"** is
  also unchanged — see [Safety boundary](#safety-boundary).

## Scope

- Extend span classification + token parsing (already in
  `RawMeasurementNormalizer`) to emit a **per-span sanitized projection** with
  tool/MCP name, tool type, sub-agent name, request/response model, per-span
  token usage, status / `error.type`, timing, and `parent_span_id` for hierarchy
  (M2).
- **Additive** schema migration: new `monitor_spans` table + new rollup columns
  on `monitor_traces`; idempotent span projection in the projection worker;
  existing concurrency / readiness / lag contract preserved (M3).
- Extend the sanitized read API: richer `/api/monitor/traces` rows + a
  cursor-paginated span list endpoint. Always sanitized; no raw, no PII (M4).
- Functional **agent-execution UI**: a trace-detail page with a Summary panel, a
  span timeline grouped into a sub-agent tree, and a per-turn token rollup; new
  columns on the trace list. Raw bodies rendered **inline by default**
  (server-rendered, inert text); `--sanitized-only` turns them off (M5).
- Security + live validation for the new default posture and the new surfaces
  (M6).

## Non-goals

- The graphical **Flow Chart** view and the **Cache Explorer** view (deferred to
  the later design sprint — the cache-token columns are still captured in M2 so
  the Explorer can be built later without re-projecting).
- Any visual/design polish, theming, or layout work.
- Using VS Code internal logs / `workspaceStorage` / `chatSessions` as input
  (D001 preserved).
- Re-implementing VS Code's in-editor debug UI.
- Remote / shared deployment, multi-user auth, IIS / Windows Service / packaged
  exe / tray app as the required host.
- Changing the normalized measurement, candidate, or dashboard dataset
  contracts. (Sprint9 only adds sanitized **monitor projection** tables/columns.)
- A JSON raw API. Raw stays server-rendered only.

## Data classification

Every field Sprint9 surfaces is classified here. **Sanitized** fields populate
the projection tables, the `/api/monitor/*` JSON API, and the SSE-driven default
UI. **Raw** fields never enter the projection / JSON / SSE; they are rendered
only by server-rendered routes (default-on, gated off by `--sanitized-only`).

| Field | OTel source | Class |
| --- | --- | --- |
| operation (`invoke_agent` / `chat` / `execute_tool` / `execute_hook`) | `gen_ai.operation.name`, span name | sanitized |
| logical category (llm_call / tool_call / agent_invocation / hook / error / unknown) | derived | sanitized |
| tool name | `gen_ai.tool.name` | sanitized |
| tool type (`function` / `extension`=MCP) | `gen_ai.tool.type` | sanitized |
| MCP tool name | `github.copilot.tool.parameters.mcp_tool_name` | sanitized |
| MCP server (hashed) | `github.copilot.tool.parameters.mcp_server_name_hash` | sanitized |
| sub-agent name | `gen_ai.agent.name` | sanitized |
| request / response model | `gen_ai.request.model` / `gen_ai.response.model` | sanitized |
| input / output / total / reasoning / cache tokens | `gen_ai.usage.*` | sanitized |
| status (ok / error) | span status code | sanitized |
| error class | `error.type` (token only; never an exception message) | sanitized |
| finish reasons | `gen_ai.response.finish_reasons` | sanitized |
| duration | span start/end | sanitized |
| trace_id / span_id / parent_span_id / conversation_id | span / `gen_ai.conversation.id` | sanitized (reference ids) |
| **tool call arguments / results** | `gen_ai.tool.call.arguments` / `.result` | **raw** |
| **sub-agent instructions / responses (prompt/response bodies)** | message content | **raw** |
| **system prompt text** | message content | **raw** |
| **PII** (`user.id` / `user.email`) | resource attrs | **raw** (default-on; excluded by `--sanitized-only`) |

### Per-field sanitization policy

"Sanitized" is **not** a blanket trust of every string — each field has a pinned
policy (M1 spec; tested in M2/M4/M6). The risk (Codex C1): custom MCP / tool /
agent names, an `error.type` token, or a stable id can carry a user name, repo
name, internal identifier, or path fragment. Therefore:

- **Free-form name fields** (`tool_name`, `mcp_tool_name`, `agent_name`, span
  `name`): stored only after passing the existing `MeasurementSanitizer`
  unsafe-value guard (rejects email / path / secret-like values), and **truncated
  to a pinned max length**. A value that fails the guard is dropped (the row keeps
  its other columns), not stored verbatim.
- **`error.type`**: the **class token only** (e.g. `timeout`,
  `ECONNREFUSED`, `TokenExpiredError`). Exception messages and the free-form
  `error` / `exception.message` attributes are **never** copied. Values must be
  identifier-like tokens (`[A-Za-z0-9._]`) and are truncated to the pinned max
  length; malformed strings, paths, emails, and message text are dropped.
- **`finish_reasons`**: enum-like tokens (`stop`, `length`, …) from a fixed set;
  unknown string tokens pass the guard + max length. Malformed serialized arrays
  are dropped rather than stored as raw text.
- **`mcp_server_hash`**: stored as the client-provided **hash** only
  (`github.copilot.tool.parameters.mcp_server_name_hash`); the unhashed server
  name is never derived or stored.
- **Reference ids** (`trace_id`, `span_id`, `parent_span_id`, `conversation_id`):
  treated as opaque reference ids, consistent with `requirements.md` §5 (session
  id / run id are declared collected data) and §8 (reference ids are
  repository-allowed). `conversation_id` is kept as a reference id; if a later
  decision needs stricter handling it is hashed/pseudonymized rather than
  promoted to raw. (These projection tables are **local runtime data**, never
  repository-committed.)

**Mandatory negative tests** (M2/M4/M6): inject email / path / secret-like values
into each free-form attribute (`tool_name`, `mcp_tool_name`, `agent_name`,
`error.type`) and assert they are guarded out of the projection, the
`/api/monitor/*` JSON, and the SSE-driven default UI — including under
`--sanitized-only`.

### Token rollup rule (no double count)

VS Code emits token usage on **both** the `invoke_agent` span (session totals)
**and** each `chat` span (per-call). Naïvely summing every span double-counts —
and the current `RawMeasurementNormalizer` does sum across all spans
([RawMeasurementNormalizer.cs:137](../../../src/CopilotAgentObservability.Telemetry/Normalization/RawMeasurementNormalizer.cs)),
a latent over-count Sprint9 must not inherit. Pinned rule:

- **Per-turn tokens** = the `chat` span's own `gen_ai.usage.*` (one turn = one
  `chat` / LLM span).
- **Per-trace total** = the trace's `invoke_agent` usage when present; otherwise
  the **sum of `chat` spans** (fallback when no agent-level total is emitted).
- **Never** add `invoke_agent` totals to `chat` per-call tokens. Sub-agent
  (child `invoke_agent`) usage is attributed to that sub-agent and rolled into
  the parent only through the parent's own agent-level total, not by re-summing
  child `chat` spans.

M2 fixes the rollup accordingly; a multi-turn + sub-agent fixture asserts the
trace/turn totals match the agent-level numbers (no double count).

## Safety boundary

Sprint9 keeps the Sprint8 **single-trusted-local-user** threat model (D020/DR6)
and changes **only the raw default**, not the defended perimeter.

Unchanged, always-on (cross-machine / other-origin defenses):

- loopback-only bind; `Host`-header validation on every request.
- CORS disabled; raw-bearing routes enforce same-origin (`Origin` /
  `Sec-Fetch-Site`) and `Cache-Control: no-store`; CSRF + same-origin on any
  state-changing action.
- raw / PII never written to logs, repository-safe outputs, the static
  dashboard, or GitHub Pages snapshots (`docs/requirements.md` §8/§9 unchanged).
- **`/api/monitor/*` and the SSE stream carry sanitized metadata only** — raw and
  PII are served **only** by server-rendered routes. This invariant is what keeps
  browser-mediated exfiltration defended even with raw default-on: a foreign page
  cannot read raw through a JSON fetch, and the HTML raw routes reject cross-site
  requests.
- captured content is rendered as **escaped, inert text** (framework default
  encoding; never `Html.Raw` / live markup). No added CSP / sanitizer / XSS
  payload-matrix apparatus (deliberate, per AGENTS.md Local-First Risk Posture).

Changed by D023:

- raw bodies + PII are shown **by default**; `--sanitized-only` restores the
  metadata-only mode (raw routes absent → `404`, PII excluded). `--enable-raw-view`
  is removed.
- accepted risk (extends D020): with the default-on posture, raw / PII is
  reachable on loopback for the full process lifetime **without** a launch flag.
  On this single-user local machine that is the product-owner-accepted trade for
  zero-friction self-debugging; `--sanitized-only` is the opt-out for shared
  screens / health-check runs.

Raw-bearing routes (default-on). Because raw bodies are now rendered as a
bounded inline preview, the **trace-detail page becomes a raw-bearing route**
alongside the existing `GET /traces/{rawRecordId}/raw` full-record view. M1 pins
the exact raw-bearing route set; **every
route in it** enforces same-origin (`Origin` / `Sec-Fetch-Site` ⇒ cross-site
`403`) **and** `Cache-Control: no-store` (so raw / PII is not left in the browser
cache after process exit or a `--sanitized-only` restart). The trace **list**,
the `/api/monitor/*` JSON, and the SSE stream are **not** raw-bearing — they stay
sanitized and are excluded from this set. Under `--sanitized-only` the
raw-bearing routes are **absent** (`404`) and no cacheable raw response is ever
generated. M6's negative matrix asserts `no-store` on **all** raw-bearing routes
(not only the raw-detail route).

## Milestones

Each milestone produces its own `milestones/Mx-*/plan.md` at execution time
(challenge-reviewed via the Codex companion `review` path before implementation,
per `CLAUDE.md`). The cadence mirrors Sprint8.

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Specs & Decisions | Record D021–D023 / update D020; update `requirements.md` §3/§4/§8, `spec.md`, `telemetry-ingestion.md` (projection schema + new API + flag), `raw-store-normalization.md` (allowlist), `security-data-boundaries.md`; Codex adversarial review of the plan. No code. | Planned |
| M2 Span Projection (sanitized) | `MonitorSpanProjection` + builder: per-span operation/category/tool/MCP/agent/model/token/status/error/timing/parent_span_id, reusing existing classification + token parsing. **Per-field sanitization policy** (allowlist + max length + unsafe-value guard; `error.type` = class token only) and the **no-double-count token rule**. **parent_span_id-absent fallback grouping** + degraded display for missing required/optional attributes. Synthetic-fixture unit tests (sub-agent child `invoke_agent` under `execute_tool`, MCP tool, `error.type`, multi-turn chat tokens, multi-trace fan-out) **plus per-attribute negative tests** (email/path/secret injected into name fields are guarded out). | Planned |
| M3 Storage + Migration | Additive `schema_version` bump: new `monitor_spans` table + rollup columns on `monitor_traces` (input/output/total tokens, turn_count, agent_invocation_count, duration_ms, primary model). **Projection-version + backfill**: existing Sprint8-processed `raw_records` are re-projected for spans + new rollup columns (span-projection progress tracked **independently of `monitor_ingestions`**, so a processed-but-span-less record is detected and not hidden as backlog 0); **robust idempotency key including a span ordinal** (tolerates missing / duplicate `span_id`); partial failure → retryable state / readiness, never silent gap. Existing concurrency / lag / readiness contract preserved. **Mandatory upgrade test from a Sprint8-populated DB.** | Planned |
| M4 Sanitized Read API | Extend `/api/monitor/traces` rows; add cursor-paginated span list (`/api/monitor/traces/{traceId}/spans` or `/api/monitor/spans`). Sanitized only; negative tests (no raw/PII, invalid query `400`). | Planned |
| M5 Agent-Execution UI + raw default | Trace-detail page (Summary panel, sub-agent span tree, per-turn token rollup), new trace-list columns; raw bodies inline by default (server-rendered, inert), `--sanitized-only` switch; `--enable-raw-view` removed. SSE/JSON stay sanitized. | Planned |
| M6 Security + Live Validation | DR6 negative matrix for the new default (raw never via JSON/SSE; raw HTML routes cross-site `403`; **`Cache-Control: no-store` on all raw-bearing routes** and `--sanitized-only` generates no cacheable raw; `--sanitized-only` ⇒ raw routes `404` + PII excluded; per-attribute sanitization negative tests; non-loopback/Host rejection; raw never logged/committed); live VS Code Copilot Chat validation of real tool/MCP/sub-agent/token emission **and the sub-agent child-span hierarchy**. | Planned (live validation human-gated) |

## Spec changes required (applied in M1)

- `docs/requirements.md` — §3: add the agent-execution-detail capability to the
  Local Ingestion Monitor entry; §4: rewrite the "Agent Debug View" non-goal per
  D021; §8: record the raw-default-on + `--sanitized-only` posture (D023).
- `docs/spec.md` — monitor section: new sanitized span projection, read API, UI,
  flag change.
- `docs/specifications/layers/telemetry-ingestion.md` — new sanitized read API
  endpoints; `--sanitized-only` flag; default-posture note.
- `docs/specifications/layers/raw-store-normalization.md` — `monitor_spans`
  allowlist schema + additive `monitor_traces` columns; **projection-version +
  backfill** for existing DBs; span idempotency key (incl. ordinal); per-field
  sanitization policy; no-double-count token rule.
- `docs/specifications/security-data-boundaries.md` — Local Ingestion Monitor
  boundary: raw default-on, `--sanitized-only`, sanitized-only JSON/SSE invariant,
  **per-field sanitization policy**, **raw-bearing route set + same-origin +
  `Cache-Control: no-store`**, extended accepted risk.
- `docs/decisions.md` — add D021–D023, update D020, **annotate D001** (Update
  note narrowing its "no re-implementation" rationale to input-source / UI-replica
  only).
- `docs/task.md` — roadmap pointer.
- User guide — update the Local Ingestion Monitor guide for the new view and flag.

## Validation

Per D015:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Automated tests use **synthetic OTLP fixtures only** (no captured Copilot
payloads). Live VS Code Copilot Chat validation (real sub-agent / tool / MCP /
token emission) is **human-gated**, the same constraint that still blocks
Sprint8 M6 live validation; Sprint9 records the same evidence (date, environment,
profile, endpoint shape, trace id / raw record id, monitor port, VS Code /
extension version, whether `--sanitized-only` was set) plus confirmation that the
sub-agent child-span hierarchy was observed.

## Dependencies & risks

- **Live validation is human-gated** (inherited from Sprint8). Sub-agent
  child-`invoke_agent`-under-`execute_tool` hierarchy and MCP attributes are
  documented by VS Code but only *confirmed* by a real run; M2–M5 are built and
  tested against synthetic fixtures shaped to that documented contract.
  **Mitigation (Codex C3):** a single real-telemetry capture is sought as an
  **M1/M2-entry confirmation gate before schema freeze** (still human-gated — it
  cannot be forced); and M2 specifies explicit **fallback behavior** when
  `parent_span_id` is absent or the hierarchy is a span-link / separate-trace
  shape rather than the assumed in-trace parent/child (flat grouping +
  unknown-hierarchy display, no failure).
- **Token over-count is a real, inherited bug, not just a risk.** The existing
  trace rollup sums token usage across all spans, which double-counts once both
  `invoke_agent` and `chat` carry usage. The [Token rollup rule](#token-rollup-rule-no-double-count)
  is mandatory in M2.
- **Backfill of existing DBs (Codex C2).** A monitor upgraded over a
  Sprint8-populated DB must re-project spans for already-processed records; M3
  owns this and a Sprint8-DB upgrade test guards it.
- Span attribute names can drift across client/extension versions. M2 keeps the
  existing multi-key tolerance (`RawMeasurementNormalizer` already accepts
  several spellings) and routes unrecognized spans to the existing unknown-span
  evidence path rather than failing.

## Plan review record (Codex adversarial + self-review, 2026-06-27)

The plan was reviewed before approval: a Codex adversarial review via the
companion `adversarial-review` path (read-only, working-tree diff) plus a Claude
self-review. Verdict: **needs-attention** — addressed by the hardening folded
into the sections above. Preserved per `docs/agent-guides/review-workflow.md`.

| Finding | Source | Resolution |
| --- | --- | --- |
| F1 "sanitized" too broad; identifiers (tool/MCP/agent names, `error.type`, `conversation_id`) could leak PII / path fragments into API/SSE | Codex C1 + self S5/S7 | [Per-field sanitization policy](#per-field-sanitization-policy) + per-attribute negative tests (M2/M4/M6) |
| F2 No backfill for existing Sprint8 DB; `monitor_spans` can stay empty, backlog 0 hides the gap | Codex C2 + self S2 | M3 projection-version + backfill + independent span-projection progress + Sprint8-DB upgrade test |
| F3 Token double-count (`invoke_agent` total + `chat` per-call) | self S1 (not raised by Codex) | [Token rollup rule](#token-rollup-rule-no-double-count) (M2) |
| F4 Sub-agent hierarchy confirmed only at M6; M2–M5 freeze on unconfirmed shape | Codex C3 + self S6 | Earlier confirmation gate (human-gated) + M2 fallback/degraded spec |
| F5 `no-store` absent from M6 matrix; inline-raw trace-detail becomes raw-bearing | Codex C4 + self S4 | [Raw-bearing routes](#safety-boundary) pinned + M6 `no-store` assertions |
| F6 D021 §4 rewrite leaves D001 rationale contradictory | self S3 | D021 adds a D001 Update note |

No finding required a new product decision from the user; all are correctness /
boundary hardening consistent with the agreed raw-default-on + sanitized-JSON/SSE
posture. (`conversation_id` is kept as a reference id per `requirements.md` §5;
hashing remains the documented stricter alternative.)

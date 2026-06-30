# Security And Data Boundaries

## Repository-Allowed Data

Allowed:

- synthetic fixture。
- redacted summary。
- normalized aggregate dataset。
- sanitized dashboard dataset。
- trace id / candidate id / evidence ref。
- real-data-derived aggregate metrics。
- `user.id` / `user.email` only when access control permits it。

## Repository-Forbidden Data

Forbidden:

- raw prompt。
- raw response。
- full system prompt。
- full tool arguments / tool results。
- source code fragment / file contents from observed sessions。
- credential、secret、token、API key、password。
- Base64 authorization header。
- sensitive bundle content。
- sensitive bundle local path。

## Sensitive Bundle Boundary

Sensitive bundle output is local opt-in output.
It may contain raw evidence needed for diagnosis, but it must not be committed.
Repository-safe records may reference only:

- sanitized `evidence_ref`。
- `sensitive_bundle_present=true`。
- non-sensitive summary。

## Raw Local Receiver Boundary

The `raw-local-receiver` profile receives raw telemetry directly from local
clients.

Raw receiver input may contain:

- raw prompt。
- raw response。
- system prompt content。
- tool arguments / tool results。
- source paths or source fragments。
- identity-bearing attributes。
- credential-like strings。

Raw receiver output is local runtime data and must not be committed.
Repository-safe outputs must continue to use normalized / sanitized datasets.
The receiver must bind only to local development endpoints unless a later
security decision allows broader exposure.
The receiver must not expose raw telemetry through dashboard output, generated
static artifacts, GitHub Pages snapshots, or repository-safe review records.
Live validation evidence must use trace ids, raw record identifiers, redacted
summaries, and non-secret endpoint shapes instead of raw payload content.

## Local Ingestion Monitor Boundary

The Local Ingestion Monitor (Sprint8) receives the same raw telemetry as the
`raw-local-receiver` profile and may surface it under an explicit opt-in.
Because the raw store and OTLP payloads can contain raw prompt / response /
system prompt / tool arguments / tool results / source paths / identity
attributes / credential-like strings, the monitor follows an explicit
single-trusted-local-user threat model.

Threat model: the monitor targets a **single trusted local user**. That user
viewing their own prompts / responses on the local UI is **intended**, not a
threat to defend against.

Defended (in scope):

- remote / non-loopback access — loopback-only bind plus `Host`-header
  validation on every request (anti DNS-rebinding).
- browser-mediated exfiltration by another origin — a malicious web page using
  the user's browser as a confused deputy to read raw from `localhost` and send
  it off-machine: CORS disabled, and strict same-origin enforcement (`Origin` /
  `Sec-Fetch-Site`) on the raw-detail route, with CSRF protection plus
  same-origin on any state-changing action.
- raw / PII leaking into logs or repository-committed artifacts — the
  Repository-Forbidden Data list above is unchanged; raw / PII is never logged,
  never written to repository-safe outputs, the static dashboard, or GitHub
  Pages snapshots.

Default posture:

- raw body (tool call arguments / results, sub-agent instructions / responses,
  system prompt) and PII (`user.id` / `user.email`) are shown **by default**
  (server-rendered, inert text) on raw-bearing surfaces.
- `--sanitized-only` restores metadata-only mode: the full raw route returns
  `404`, TraceDetail's raw section and full raw links are omitted, the dashboard
  and trace-list prompt labels are omitted (a shortened TraceId is shown
  instead), PII is excluded, and the sanitized TraceDetail tab shell remains
  available. This is an optional compatibility / opt-out mode, not a Canvas
  requirement.
- API responses (`/api/monitor/*`), list endpoints, and the SSE stream carry
  sanitized metadata only — they **never** return raw / PII, regardless of
  `--sanitized-only`.
- there is **no bearer-token-to-console** scheme; a reusable token printed to a
  capturable stream cannot uphold a secrecy guarantee on this machine, so the
  boundary does not claim one.
- Windows Task Scheduler startup is a user-level convenience wrapper for the
  same loopback monitor process. It may run in the normal raw-default posture or
  with `--sanitized-only`; it does not create a shared service boundary. Runtime
  DB, logs, pid/state files, and task-generated local state live under
  `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` by default and must
  not be committed.
- Release ZIP distribution is a packaging surface for the same local monitor
  process. The ZIP and its GitHub Actions logs/artifacts may contain the
  published app, scripts, manifest, and notices only; they must not contain raw
  store files, runtime DB, logs, state, raw OTLP payloads, raw prompt/response
  bodies, tool arguments/results, PII, credentials, tokens, local sensitive
  paths, or generated monitor output.

Accepted out of scope (explicit accepted risk):

- another process running as the **same local user** reading raw via loopback.
  The raw store, the OTLP payloads, and the existing opt-in sensitive-bundle
  output are already readable by same-user processes; the monitor does not widen
  that exposure. Multi-user-OS isolation is a documented deployment assumption,
  not a control.
- raw / PII is shown **by default** (no launch flag required), so raw / PII is
  reachable on loopback for the **full process lifetime** in any launch mode
  including unattended / background / always-on receivers. This is a
  **product-owner-accepted risk** on this single-user local machine, chosen for
  zero-friction self-debugging. `--sanitized-only` is the opt-out safety valve.
- **defense-in-depth beyond default escaping.** The baseline against stored
  markup is the required inert text rendering above; on top of that the monitor
  adds **no** CSP backstop, payload sanitizer, or XSS payload-matrix test suite.
  On this single-user local machine, that residual (e.g. were a subtle
  output-encoding bug to slip past the inert-rendering requirement) is an accepted
  risk, not a separately defended boundary. This is the display hardening
  deliberately not added — see `AGENTS.md` Local-First Risk Posture and D020.

Raw-bearing surfaces:

- raw / PII is exposed through server-rendered routes only. The raw-bearing
  surfaces are:
  - the **trace-detail page raw section** (agent-execution view lower section,
    which renders a bounded raw preview inline and links to the full
    single-record raw route by default).
  - `GET /traces/{rawRecordId}/raw` (server-rendered HTML, one raw record on
    demand by id from `raw_records`).
  - the **dashboard page (`/`)** and the **trace-list page (`/traces`)** — they
    render a single representative **user prompt per trace** (extracted
    server-side from the trace's raw OTLP payload, truncated, escaped inert
    text) so a trace is identifiable by what the user asked rather than by an
    opaque id (D032). Only this short prompt label is raw; all other columns
    stay sanitized metadata. Under `--sanitized-only` the prompt label is
    omitted and a shortened TraceId is shown instead.
- there is **no JSON raw API**; `/api/monitor/*` and the SSE stream **never**
  return raw / PII (the prompt label is server-rendered only; client-side JS
  never fetches it).
- **default-on**: raw-bearing routes are active by default (no launch flag
  required). `--sanitized-only` removes raw-bearing surfaces: the raw-detail
  route returns `404`, the trace-detail page omits its raw section and full raw
  links while retaining the sanitized tab shell, the dashboard and trace-list
  pages omit their per-trace prompt labels (showing a shortened TraceId), PII is
  excluded, and no cacheable raw response is generated.
- **every raw-bearing route or page variant** enforces:
  - same-origin: a request whose `Sec-Fetch-Site` is cross-site / cross-origin
    (or whose `Origin` is foreign) is rejected with `403`.
  - `Cache-Control: no-store` (so raw / PII is not left in the browser cache
    after process exit or a `--sanitized-only` restart).
- an unknown id returns `404`; no raw is written to logs.
- **inert text rendering is required.** Captured content (and likewise the
  default UI, list, and SSE-rendered fields) is rendered as **escaped, inert
  text** via the UI framework's default output encoding — never via `Html.Raw` /
  raw `MarkupString`, and never reflected into an HTML, attribute, script, style,
  or URL context as live markup. Stored markup therefore displays as text and
  does not execute, so it cannot pivot to a same-origin read of these routes. The
  monitor does **not** add a heavier layer on top of this default escaping (no
  dedicated CSP, `nosniff`, or payload-sanitizer apparatus) — that was judged
  over-engineering for a local single-user tool (see accepted local risk above).

Per-field sanitization policy:

- **free-form name fields** (`tool_name`, `mcp_tool_name`, `agent_name`, span
  `name`): stored only after passing the existing `MeasurementSanitizer`
  unsafe-value guard (rejects email / path / secret-like values), and truncated
  to a pinned max length. A value that fails the guard is dropped (the row keeps
  its other columns), not stored verbatim.
- **`error_type`**: the class token only (e.g. `timeout`, `ECONNREFUSED`,
  `TokenExpiredError`). Exception messages and free-form `error` /
  `exception.message` attributes are never copied. Values must be identifier-like
  tokens (`[A-Za-z0-9._]`) and are truncated to the pinned max length; malformed
  strings, paths, emails, and message text are dropped.
- **`finish_reasons`**: enum-like tokens (`stop`, `length`, …) from a fixed set;
  unknown string tokens pass the guard + max length. Malformed serialized arrays
  are dropped rather than stored as raw text.
- **`mcp_server_hash`**: stored as the client-provided hash only; the unhashed
  server name is never derived or stored.
- **reference ids** (`trace_id`, `span_id`, `parent_span_id`, `conversation_id`):
  treated as opaque reference ids per `requirements.md` §5 and §8.

Additional monitor web-security requirements:

- request body size limit (default 30 MiB, configurable via
  `--max-request-body-bytes` / `CAO_MONITOR_MAX_REQUEST_BODY_BYTES`; the concrete
  value and boundary are pinned in
  [layers/telemetry-ingestion.md](layers/telemetry-ingestion.md)); a body larger
  than the limit is rejected with `413` and writes no raw record.
- request logging excludes the body, path, query string, and exception detail.
- error responses exclude the DB full path, the Windows user name, and raw
  exception messages.
- startup wrapper logs exclude raw monitor content, credentials, tokens, PII,
  raw OTLP payloads, request bodies, query strings, and full exception detail.

Mandatory negative tests:

- non-loopback bind rejected; `Host`-header validation enforced.
- with `--sanitized-only`, the full raw route returns `404`, the trace-detail
  raw section and full raw links are absent, the dashboard and trace-list
  per-trace prompt labels are absent, PII is excluded, and no cacheable raw
  response is generated.
- the dashboard (`/`) and trace-list (`/traces`) pages render the per-trace
  user-prompt label **by default**, omit it under `--sanitized-only` (showing a
  shortened TraceId instead), and the prompt label never appears in
  `/api/monitor/*` or the SSE stream (D032).
- a cross-site / cross-origin request to any raw-bearing route / page variant is rejected with
  `403`.
- `Cache-Control: no-store` is present on **all** raw-bearing routes / page
  variants (not only the raw-detail route).
- raw / PII is never returned by `/api/monitor/*` or the SSE stream.
- a state-changing request without CSRF / same-origin is rejected.
- raw / PII never appears in logs or repository-committed outputs.
- per-field sanitization: email / path / secret-like values injected into
  free-form attributes (`tool_name`, `mcp_tool_name`, `agent_name`,
  `error_type`) are guarded out of the projection tables, the `/api/monitor/*`
  JSON, and the SSE-driven default UI — including under `--sanitized-only`.

Sprint10 design views (client-side presentation, boundary unchanged):

- the Sprint10 design views (Flow Chart, Cache Explorer, timeline filter/sort,
  themed trace-detail UI) are **client-side rendering over the sanitized
  `/api/monitor/*` JSON and SSE only**. They never read a raw-bearing route, add
  no raw-bearing route, and add no new endpoint / field. The raw-bearing
  surfaces, the default-on posture, and `--sanitized-only` raw removal are
  unchanged; the new views work identically under `--sanitized-only` because
  they were sanitized to begin with.
- **vendored, no CDN.** Noto Sans JP / Noto Sans Mono (D028) are vendored
  locally under `wwwroot/vendor/`; no Content-Delivery-Network or external font
  fetch is introduced, so offline / loopback-only operation is preserved and no
  third-party origin is contacted at runtime. Version / SHA-256 / size / license
  are recorded at vendoring time (fonts M2). The Cytoscape.js + dagre +
  cytoscape-dagre vendored graph dependency (D025) is **removed** by D033; the
  Flow Chart and Span Tree are now plain DOM built by Vanilla JS from the
  sanitized spans API (no third-party graph runtime).
- **Cache Explorer is sanitized-metrics-only** (D026): cache-hit rate,
  cache-creation tokens, duration, model, timestamp, and the per-turn token
  breakdown, grouped within one trace. The VS Code "prefix diff of consecutive
  requests" feature compares **raw prompt bodies** and is **out of scope** (it
  would add a raw-bearing surface); `conversation_id` cross-trace stitching is
  deferred (it would require an API change).
- captured content keeps rendering as escaped, inert text (framework default; no
  `Html.Raw`); the design views add no CSP / sanitizer / XSS-matrix apparatus
  (AGENTS.md Local-First Risk Posture, D020).

Sprint11 GitHub Copilot app Canvas adapter (bounded adapter, boundary
unchanged):

- The Canvas adapter is a thin project-scoped extension over the existing Local
  Monitor. It does not add telemetry input, schema, API field, raw-bearing route,
  repository-stored monitor output, or a replacement monitor UI.
- The Canvas adapter may be used with the normal raw-default Local Monitor.
  Canvas surfaces may link to Local Monitor pages that show raw-bearing
  server-rendered UI under the Local Monitor's existing loopback, same-origin,
  no-store, and inert-rendering controls.
- `--sanitized-only` remains an optional Local Monitor opt-out mode, not a
  Canvas requirement.
- Canvas actions consume existing sanitized `/api/monitor/*` responses and
  readiness status only. Action responses must be bounded DTOs and must never
  include raw prompt bodies, raw response bodies, tool arguments, tool results,
  PII, credentials, tokens, local sensitive paths, raw OTLP payloads, or raw
  monitor payload dumps.
- Extension-owned HTTP servers bind only to `127.0.0.1`, close during
  `onClose()`, and use per-launch token protection when a helper/proxy route
  exposes monitor state. CORS stays disabled unless a later explicit decision
  changes that boundary.
- Diagnostics use `session.log()`, not `console.log()`, because stdout is
  reserved for JSON-RPC in the Copilot app extension runtime. Logs must not
  contain raw / PII or local sensitive paths.
- The Canvas adapter introduces no CDN, remote runtime fetch, or third-party
  origin dependency. It reads the configured loopback Local Monitor and serves
  extension-owned helper pages from loopback only.
- Sprint11 M5 UI-to-Copilot trigger (D029): `open()` returns an
  extension-owned loopback helper page (per-launch token in the page URL) that
  shows monitor health, a trace dropdown, a focus selector, and an
  "Analyze selected trace with Copilot" button. The helper page proxies
  sanitized `GET /api/monitor/traces?limit=50` for the dropdown
  (`compactTrace`-shaped items only) on a token-protected route; the proxy
  rejects missing/wrong tokens with `401`. The trigger button posts to a
  token-protected `POST /analyze` route that validates trace id / optional span
  id / focus, then calls `session.send({ prompt })` with an instruction that
  references only the selected ids, the focus, and bounded action names
  (`get_trace_summary` / `get_trace_span_tree` / `get_cache_summary`). Raw
  details remain local Monitor UI data and are not copied into Canvas action
  responses, logs, committed files, or static artifacts. The trigger payload
  never embeds monitor payload, raw bodies, tool arguments / results, PII,
  credentials, tokens, or local sensitive paths. `session.send()` is
  fire-and-forget; the helper page returns `{ ok: true, dispatched: true }`.
  Token transport uses the page URL query parameter for the page and the
  `x-canvas-token` header for the proxy and analyze XHR routes.
- `/create-canvas` is the repository-local skill at
  `.github/skills/create-canvas/SKILL.md`; it must not be replaced with a
  `.github/prompts/*.prompt.md` prompt file. The project scaffold is created
  through `extensions_manage({ operation: "scaffold", kind: "canvas", name:
  "otel-monitor-canvas", location: "project" })` when available. If scaffold or
  validation tools are unavailable, implementation records the blocker and stops;
  hand-written fallback requires explicit product-owner approval.

Sprint12 UX redesign (prompt identification + DOM views, boundary controls
reused):

- **Prompt-identified dashboard / trace list (D032).** The dashboard (`/`) and
  the trace-list page (`/traces`) join the raw-bearing surface set: each renders
  one representative **user prompt per trace**, extracted server-side from the
  trace's raw OTLP payload, truncated, and emitted as escaped inert text (Razor
  default encoding; no `Html.Raw`). Only that short label is raw — every other
  field stays sanitized metadata. Both pages enforce the same controls as the
  existing raw-bearing routes: same-origin (`403` on cross-site), `Cache-Control:
  no-store`, and removal under `--sanitized-only` (the label is dropped and a
  shortened TraceId is shown). The prompt label is server-rendered only; the
  sanitized `/api/monitor/*` JSON and the SSE stream are unchanged and still
  never carry it. No projection schema, API field, or new endpoint is added. The
  old `/ingestions` page is retired and its ingestion list is folded into the
  dashboard.
- **DOM Flow Chart / Span Tree (D033).** The Cytoscape.js + dagre vendored graph
  dependency is removed; the trace-detail visualization is plain DOM (an
  indented Span Tree with a waterfall bar, plus a DOM flow view) built by
  Vanilla JS from the sanitized spans API only — `textContent` rendering, no
  `innerHTML` / `Html.Raw`, no `/raw` access.
- captured content (and the prompt label) keeps rendering as escaped inert text;
  the redesign adds no CSP / sanitizer / XSS-matrix apparatus (AGENTS.md
  Local-First Risk Posture, D020).

## Shared Use Preconditions

Before shared dashboard or real-data publishing:

- define access control。
- define retention。
- define deletion process。
- define masking / redaction。
- define user notice or consent。
- decide identity handling。
- validate Pages visibility。
- confirm snapshot growth monitoring。

Before using `remote-managed-langfuse` or `remote-managed-collector`, define:

- access control。
- retention。
- deletion process。
- masking / redaction。
- user notice or consent。
- identity handling。
- credential handling。

This repository documents the warning but does not implement the consent
workflow.

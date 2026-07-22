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

## Historical Instruction Analysis Boundary

Issue #73 may send one persisted Issue #72 raw-local extraction to an
explicitly supplied provider only when the current Local Monitor host permits
raw execution. The provider receives an independently deserialized view plus a
separate canonical-byte copy; neither shares a mutable object with the
owner-validated #72 snapshot retained for citation and recurrence validation.
Provider mutation therefore cannot redefine persisted evidence.

Under `--sanitized-only`, the #73 composition keeps repository-safe read/status
access but refuses runner construction before a run or provider call, including
when the database contains raw extractions created by an earlier host mode.
The repository-safe #73 receipt/#59 handoff pair contains only tokenized
Session/trace/span refs, closed metadata, distributions, hashes, and fixed #59
templates. It never contains raw descriptors, provider
credentials/configuration, prompt/response
bodies, tool bodies, source excerpts, local paths, or exception text.

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
static artifacts, CI artifacts, or repository-safe review records.
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
  never written to repository-safe outputs, the static dashboard, or CI
  artifacts.

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
- Windows user environment install is a separate current-user routing
  convenience. It writes only HKCU user environment values for loopback
  raw-local-receiver / monitor routing, never machine-wide environment, and
  never secrets. It does not weaken the monitor's loopback / same-origin / log
  boundaries. Existing processes keep their old inherited environment until
  restarted.
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
- there is **no full-payload JSON raw API**; `/api/monitor/*` and the SSE
  stream **never** return raw / PII. The short prompt label route is the narrow
  raw-bearing JSON exception outside `/api/monitor/*` and may be fetched by
  same-origin / token-gated local UI code.
- Update (D039 / D042 / D050): prompt labels are the narrow exception to the
  older server-rendered-only wording. Local Monitor client-side overview /
  trace-list code and Canvas helper routes may same-origin / token-gated fetch
  `GET /traces/{traceId}/prompt-label` and render the short label as inert text.
  Full raw payloads still are not fetched by client-side JS, and
  `/api/monitor/*` plus SSE remain prompt-free.
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
  Monitor. Sprint11 itself did not add telemetry input, schema, API field,
  raw-bearing route, repository-stored monitor output, or a replacement monitor
  UI, except for the Sprint16 scoped sanitized repository metadata fields
  recorded below (D040). Issue #51 supersedes this prohibition only for the
  separate Session ingest/storage/workspace interfaces and exact-link OTel
  enrichment defined in D051.
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

Sprint15 Canvas diagnostic surface — child A helper UX (boundary unchanged,
D036):

- Child A improves only the extension-owned helper page presentation. It
  consumes the same sanitized surfaces as before — `GET /health/ready` and the
  token-protected proxy of `GET /api/monitor/traces?limit=50`
  (`compactTrace`-shaped items only) — and adds no new endpoint, raw-bearing
  route, or monitor payload field. The decision-supporting trace line (status /
  primary model / span count / tool count / token total / duration / time /
  shortened trace id) is formatted from `compactTrace` fields that are already
  sanitized; no prompt body or raw field is introduced.
- Canvas action responses remain bounded DTOs; the loopback bind, per-launch
  token (`x-canvas-token` / URL query), `session.send()` fire-and-forget
  trigger, no-`console.log` diagnostics, and the no-raw/PII-to-logs/committed-
  output/static-artifact boundary are all unchanged.
- Prompt / response preview on any Canvas surface is **not** enabled by child A.
  D050 later authorizes the token-gated loopback helper page itself to show a
  selected trace's local prompt/response preview; Canvas action responses,
  `session.send()` prompts, logs, committed outputs, and static artifacts remain
  raw-free.
- A future Canvas dashboard view (child B) will reuse a new sanitized aggregate
  endpoint on the Local Monitor (e.g. `/api/monitor/summary`, built from
  `MonitorTraceRollup` and the existing projection store) shared with the Razor
  index page; it must stay within the sanitized `/api/monitor/*` boundary (no
  prompt body, no raw payload).

Sprint15 child B/C/D/E resolution (D037):

- **Child B — `GET /api/monitor/summary`.** Loopback-only, no same-origin/
  no-store requirement (sanitized, not raw-bearing, same as `/api/monitor/traces`).
  Fields are limited to the existing sanitized projection allowlist:
  `scope.limit` / `scope.trace_count`, `latest_trace` / `top_token_trace` /
  `error_trace` (each `compactTrace`-shaped), `per_model_summary` /
  `per_client_kind_summary` (`model`/`client_kind`, `trace_count`,
  `total_tokens`, `error_count` only). No prompt body, no raw payload, no PII.
  `readiness` is intentionally excluded — `/health/ready` stays the single
  source of truth. Aggregation is computed in-memory over the same bounded
  `limit`-row window already used by `/api/monitor/traces`; no new SQL surface.
- **Child C — `GET /api/trace-detail/:traceId`.** New route on the
  Canvas-extension-owned loopback server only (not a new Local Monitor
  endpoint), token-protected the same way as the existing `/api/traces` proxy.
  Returns `compactTrace` fields plus `cache_hit_rate` and `primary_model` only.
  Does not return span trees, per-turn cache detail, or any field not already
  exposed by the existing bounded actions. No raw prompt/response.
- **Child D — raw preview, implementation authorized (D038).** New page-
  navigation route `GET /raw-preview/:traceId/:spanId` on the Canvas-owned
  loopback server (token-gated the same way as every other route on that
  server). The handler resolves `spanId` to a `raw_record_id` via the
  existing sanitized `/api/monitor/traces/{traceId}/spans` response (which
  already includes `raw_record_id` per span), then fetches
  `GET {monitorUrl}/traces/{rawRecordId}/raw` server-to-server. That Local
  Monitor route returns a fixed-format HTML page
  (`<pre>{HtmlEncoder.Default.Encode(payload)}</pre>`) — the extension
  extracts the substring between the first `<pre>` and the last `</pre>` and
  re-embeds it **verbatim** (no decode, no re-encode) into its own page. This
  is safe specifically because the source is already HTML-encoded by
  `HtmlEncoder.Default` server-side. The response is a full HTML page
  (`Cache-Control: no-store`), reached only via an explicit link click (a
  real page navigation, not a `fetch()` + `innerHTML`) — the helper page's
  client-side JS never receives raw as JSON, mirroring the "JS does not
  fetch raw" rule from D020/D023/D032. The server-to-server fetch (no
  `Origin`/`Sec-Fetch-Site` headers from a Node `fetch()`) passes
  `MonitorHost.IsCrossSiteRequest` the same way any other same-local-user
  process would — this stays within the already-accepted "another process
  running as the same local user reading raw via loopback" risk (see
  "Accepted out of scope" above), not a new risk category. Canvas **action**
  responses (`get_trace_summary` etc., invoked via `invoke_canvas_action`)
  are unchanged — raw preview is confined to this one page-navigation route
  and never flows into an action response, a Copilot prompt, or a log.
  `sanitizeDto()`'s forbidden-key filter is unaffected (this route doesn't go
  through it — it's a separate code path, not a loosening of the filter). No
  new Local Monitor endpoint is added.
- **Child B remainder — `GET /api/summary` (Canvas-owned proxy, D038; prompt
  label enrichment updated by D039/D050).** New route on the Canvas-owned
  loopback server (same token gate as `/api/traces`) that proxies
  `GET /api/monitor/summary` (already sanitized per the child B entry above)
  into a "Local Monitor 概要" card in the helper page. No new Local Monitor
  endpoint. The helper may add `line` plus the same D039 `prompt_label`
  enrichment to the latest / top-token / error highlight trace objects so the
  local user can identify those rows by prompt on the Canvas helper screen.
  Because that label is raw-bearing local-screen data, this helper route uses
  `Cache-Control: no-store`. `/api/monitor/summary` itself remains sanitized
  and prompt-free.
- **Child E — dropped in the D037 trace-centric adapter.** No implementation or
  resource/span attribute was added there, and that adapter keeps its manual
  trace dropdown. D051 separately introduces an exact-bound Session subsystem;
  it does not change D037 trace selection. Neither subsystem permits heuristic,
  latest-trace, repository, timestamp, or proximity selection.
- **Live Canvas runtime verification is a separate, final, cross-cutting step
  (D038), not part of any child's implementation scope.** It requires
  `extensions_manage`/`open_canvas`/`invoke_canvas_action`, which this Claude
  Code environment does not have. All code for children A–D is written and
  automated-tested (`node --check`/`node --test`/`dotnet build`/`dotnet test`)
  entirely without those tools; only the final live rendering/invocation
  check is delegated to a GitHub Copilot app session, once, covering every
  child together.

Sprint15 continuation — prompt-aware trace selection (D039, implemented, M7):

- **New raw-bearing JSON route, `GET /traces/{traceId}/prompt-label`.** Not
  part of the `/api/monitor/*` sanitized family (D032's "no `/api/monitor/*`/
  SSE field" guarantee is unaffected). Follows the same route-boundary pattern
  D035 already established for `/traces/{traceId}/analysis/...`: registered
  only inside the `!options.SanitizedOnly` block (route absent → `404` under
  `--sanitized-only`), `MonitorHost.IsCrossSiteRequest` same-origin check
  (`403` on cross-site), and `Cache-Control: no-store`. Reuses the existing
  `MonitorPromptExtractor.ExtractPromptLabel` (120-char truncated,
  whitespace-collapsed, exception-safe) and
  `IMonitorProjectionStore.ListRawRecordsByTraceId` exactly as
  `Index.cshtml.cs`/`Traces.cshtml.cs` already call them — no new extraction
  logic. Response: `{ "trace_id", "prompt_label" }`, where `prompt_label` may
  legitimately be `null` (not an error).
- **Canvas-owned `/api/traces` and `/api/summary` highlight traces
  (helper-page surfaces, not Canvas actions) fetch this per trace, in
  parallel, and add `prompt_label` to their own responses.** This is the same
  "helper-page surface" category M5's
  raw-preview already established as distinct from the strictly-bounded
  `invoke_canvas_action` surface — `sanitizeDto()`'s forbidden-key filter
  (which matches `prompt`) is unaffected because these helper proxy routes
  never pass through it in the first place. Canvas **actions** (`monitor_health`,
  `list_recent_traces`, `get_trace_summary`, `get_trace_span_tree`,
  `get_cache_summary`), `session.send()` prompts, logs, and committed
  artifacts are unchanged by this and never carry `prompt_label`. Under
  `--sanitized-only`, the new route is absent (`404`), and Canvas falls back
  to its existing decision-supporting line with no prompt shown, mirroring
  D032's own fallback for the Local Monitor's own pages. The `/api/summary`
  helper route uses `Cache-Control: no-store` when carrying highlight prompt
  labels.
- **Rationale for reconsidering "no `/api/monitor/*` field" for this one
  narrow case**: unlike Local Monitor's own pages (same-origin check only,
  no secret), every route on the Canvas extension's own server — including
  this one — is additionally gated by a per-launch random token unknown to
  any third-party site, which independently blocks the "malicious
  same-browser website" scenario this whole route-boundary pattern exists to
  defend against. Combined with reusing D035's already-accepted "JSON
  raw-bearing route, not `/api/monitor/*`" precedent and the existing 120-char
  truncation, this is judged a narrow, already-precedented extension, not a
  new exposure category. See D039 in `docs/decisions.md` for the full
  discussion and rationale.
- Implementation (Local Monitor endpoint, Canvas-side consumption, and
  contract tests) is complete (Sprint15 M7), following the same two-stage
  (design confirmed → implementation authorized) process D037/D038 already
  used.

Canvas helper prompt/response preview (D050):

- The Canvas helper may show the selected trace's prompt and response preview on
  the extension-owned loopback page. This is a local screen for the same trusted
  user, not a Canvas action response and not a repository-safe output.
- The same local-screen posture applies to prompt labels used to identify trace
  choices and summary highlight traces in the Canvas helper. They may appear on
  token-gated helper routes (`/api/traces` and `/api/summary`) but not in
  Canvas actions, logs, `session.send()` prompts, committed files, CI artifacts,
  or static artifacts.
- The helper uses a token-protected `GET /api/trace-content/:traceId` route on
  the Canvas-owned loopback server. The route rejects non-loopback monitor URLs,
  uses `Cache-Control: no-store`, and fetches existing Local Monitor data
  server-to-server from `GET /traces/{traceId}/spans/{spanId}/detail`.
- No new Local Monitor endpoint, `/api/monitor/*` field, projection schema field,
  or SSE field is added. Under `--sanitized-only`, the span detail route is
  absent and the helper degrades to no preview.
- Client-side rendering must assign preview text with `textContent` (or
  equivalent inert text APIs), not `innerHTML`.
- Canvas actions (`monitor_health`, `list_recent_traces`, `get_trace_summary`,
  `get_trace_span_tree`, `get_cache_summary`), `session.send()` prompts, logs,
  committed files, screenshots intended for repository evidence, CI artifacts,
  and static artifacts must not include the prompt/response preview.

Sprint16 Canvas cross-repo adapter metadata (D040):

- `.github/extensions/otel-monitor-canvas/` is the only copyable Canvas
  extension distribution unit for this sprint. No mirror folder, package
  manifest, lockfile, `node_modules`, or new runtime/development dependency is
  introduced.
- The Local Monitor may project only these sanitized repository metadata fields
  from existing OTLP Resource Attributes: `repository_name` from
  resource-scoped `vcs.repository.name`, or only when that key is absent from
  the sanitized repository segment of an allowlisted canonical
  `https://github.com/{owner}/{repository}` `vcs.repository.url.full`,
  `workspace_label` from `workspace.name`, and `repo_snapshot` from
  `repo.snapshot`.
- `repo.name` is not a repository label source for this surface.
- These fields may appear in sanitized `/api/monitor/*` responses, Canvas
  helper routes, and bounded Canvas action DTOs after the monitor projection
  sanitizer accepts them. They remain display metadata, not raw content.
- Values that look like raw prompt / response body, tool arguments / results,
  PII, credentials, tokens, local sensitive paths, raw OTLP payload fragments,
  or other unsafe free-form content are dropped rather than emitted.
- Existing projected rows are not automatically backfilled. Missing metadata is
  represented as `NULL` on the Local Monitor API, and the Canvas helper renders
  `unknown repository` when it cannot derive a repository label.
- No `repository_full_name`, `workspace_hash`, `git_branch`, `git_commit_sha`,
  `source_kind`, current-repository auto-match, raw endpoint, raw JSON API, or
  Canvas action raw payload is added in Sprint16.
- `vcs.repository.url.full` is not emitted to Canvas helper routes or bounded
  Canvas action DTOs. The owner and raw URL are never persisted in the monitor
  projection. Embedded credentials, ports, query/fragment, percent escapes,
  file/local/UNC/HTTP/SSH/SCP-like forms, extra path segments, email-like
  segments, token-like segments, and non-allowlisted providers cannot produce a
  label. An unsafe authoritative name cannot fall through to the URL.
- `/diagnostics` may inspect bounded recent raw payloads only through Retention
  catalog `access` reads. Its repository metadata inventory emits safe
  attribute key names, counts, `resource`/`span`/`event` scope, candidate
  classification, one fixed metadata status, and label/fallback booleans only.
  It emits no attribute value, repository label, URL, owner, identity,
  credential, token, PII, local path, prompt/response, or tool body. Unsafe key
  names are suppressed rather than truncated. The key-only inventory remains
  available under `--sanitized-only`; this does not authorize any raw value
  display.
- Existing `/api/monitor/*`, SSE, Canvas DTO, normalized dataset, and dashboard
  wire shapes remain unchanged. Repository metadata statuses are diagnostic
  reason codes, not Session binding or repository identity evidence.

Sprint17 Canvas analysis requested options:

- The Canvas helper's "analyze" button remains a `session.send()` trigger that
  instructs Copilot to use existing bounded Canvas actions. It does not invoke
  the Local Monitor raw analysis runner, does not call
  `/traces/{traceId}/analysis`, and does not wait for or store final raw-derived
  analysis results.
- `GET /api/analysis/options` is sanitized configuration metadata. It may return
  profile ids/display names, timeout hints, reasoning effort labels, model ids,
  model display names, provider display names, and whether the model supports
  reasoning controls. It must not return provider API keys, provider base URLs,
  SDK state directories, local paths, raw telemetry, raw prompts/responses, tool
  arguments/results, PII, credentials, tokens, or raw OTLP payloads.
- Canvas helper `POST /analyze` dispatch metadata may include trace/span ids,
  focus, requested profile/model/reasoning/timeout, prompt template version,
  dispatch timestamp, and SDK message id when available. These are requested
  values only. The helper UI must not claim per-message model, reasoning, or
  execution-timeout enforcement unless a future SDK-supported and verified
  mechanism exists.
- Dispatch progress UI describes local preparation and handoff to Copilot chat.
  It must not present a fake model-response wait state or imply that cancelling
  the helper UI cancels in-flight Copilot agent work after `session.send()`
  succeeds.

Sprint18 Local Monitor UI redesign (D042/D043/D044/D045):

- **New raw-bearing JSON route, `GET /traces/{traceId}/spans/{spanId}/detail`
  (D043).** Serves the span inspector's formatted / raw tabs. Follows the same
  route-boundary pattern as D035/D039: not part of `/api/monitor/*`, registered
  only inside the `!options.SanitizedOnly` block (route absent → `404` under
  `--sanitized-only`), `MonitorHost.IsCrossSiteRequest` same-origin check
  (`403` on cross-site), and `Cache-Control: no-store`. Unknown trace / span id
  returns `404`. The response may contain tool call arguments, tool result tail
  lines, per-role message previews, response previews, and the raw OTLP span
  JSON — all of which are local runtime data for the single trusted local user
  and must never appear in logs or repository-committed outputs. Extraction is
  best-effort (`SpanDetailExtractor`, pure / exception-safe); the raw span JSON
  is always returned even when formatted extraction fails.
- **New sanitized endpoints `GET /api/monitor/overview` and
  `GET /api/monitor/trace-list` (D044).** Both stay inside the sanitized
  `/api/monitor/*` boundary: numeric aggregates, model names (already
  sanitizer-guarded), enum-like `trace_status`, timestamps, and opaque trace
  ids only. No prompt text, raw payload, tool arguments/results, or PII. The
  trace-list `q` parameter matches TraceId substrings server-side only; prompt
  search happens client-side over prompt labels the raw-bearing pages already
  loaded (D042 C8) and never adds a prompt field to `/api/monitor/*`.
- **Schema v4 rollup columns (D044)** (`cache_read_tokens`,
  `cache_creation_tokens`, `trace_status` on `monitor_traces`) are numeric /
  enum-like sanitized projection metadata, additive-only, no backfill.
- **Copilot drawer follow-up chat = history resend (D045).** The analysis start
  payload gains optional `question` / `history` (prior Q&A turns held by the
  drawer client, per trace). Each follow-up creates a new local analysis run;
  no server-side chat session state and no `monitor_analysis_runs` schema
  change. The existing analysis route boundary is unchanged: CSRF + same-origin
  on start, `no-store` on results, routes removed under `--sanitized-only`,
  results are local runtime data. The submitted question / history become part
  of the local analysis run's raw-derived local data and must not leak into
  logs or repository-safe summaries.
- The redesigned UI keeps the existing rendering invariants: server-rendered
  raw-bearing content stays escaped inert text; client-side JS builds DOM via
  `createElement` / `textContent` only (no `innerHTML`), and sanitized-context
  pages never fetch raw-bearing routes.

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
  shortened TraceId is shown). The prompt label may be server-rendered or
  fetched from the raw-bearing prompt-label route by same-origin local UI code;
  the sanitized `/api/monitor/*` JSON and the SSE stream are unchanged and still
  never carry it. No projection schema or API field is added
  **for the `/api/monitor/*` family** — updated by D039, which adds a
  narrowly-scoped new endpoint *outside* that family (see "Sprint15
  continuation" above); `/api/monitor/*` and SSE themselves remain unchanged
  and prompt-free. The
  old `/ingestions` page is retired and its ingestion list is folded into the
  dashboard.
- **Prompt-label client fetch update (D039 / D042).** Sprint18's client-side
  overview and trace-list may fetch the short prompt label route and insert it
  with `textContent`; this supersedes only the "server-rendered only" transport
  wording for prompt labels. It does not allow client-side fetching of full raw
  record payloads or adding prompt fields to `/api/monitor/*`.
- **DOM Flow Chart / Span Tree (D033).** The Cytoscape.js + dagre vendored graph
  dependency is removed; the trace-detail visualization is plain DOM (an
  indented Span Tree with a waterfall bar, plus a DOM flow view) built by
  Vanilla JS from the sanitized spans API only — `textContent` rendering, no
  `innerHTML` / `Html.Raw`, no `/raw` access.
- captured content (and the prompt label) keeps rendering as escaped inert text;
  the redesign adds no CSP / sanitizer / XSS-matrix apparatus (AGENTS.md
  Local-First Risk Posture, D020).

## Local Monitor Copilot Raw Analysis Boundary

The Sprint13 raw analysis surface is local runtime behavior for the single
trusted local user. It may send raw trace / raw record / raw span context to a
.NET GitHub Copilot SDK analysis session so Copilot can diagnose the captured
Copilot/agent execution.

Allowed locally:

- Local Monitor displays raw prompt / response / tool content in the normal
  raw-default posture.
- Local Monitor creates local analysis runs.
- Copilot SDK raw analysis tools are process-internal C# tools that can return
  raw trace / raw record / span context to the .NET SDK session.
- Raw analysis result markdown is stored only as local runtime data.

Forbidden in repository-safe outputs:

- raw prompt body.
- raw response body.
- full tool arguments / tool results.
- source fragment or observed file content.
- credential, token, API key, password, or authorization header.
- PII such as `user.id` / `user.email`.
- local sensitive path.

Repository-safe summary export is a separate allowlist output. It may include
trace ids, span ids, raw record ids as references, durations, token counts,
error counts, cache metrics, classifications, and improvement suggestions that
do not copy raw content. The implementation must not treat arbitrary model
free text as repository-safe by default.

Route boundary:

- raw analysis routes live under `/traces/{traceId}/analysis/...`, never under
  `/api/monitor/*`.
- `/api/monitor/*` and SSE remain sanitized metadata only.
- `--sanitized-only` disables the raw analysis UI, start route, and result
  route.
- browser state-changing start requests require same-origin and CSRF protection.
- raw analysis result responses use `Cache-Control: no-store`.
- BYOK provider credentials for `.NET GitHub.Copilot.SDK` are local secrets.
  They may be read from user-secrets or equivalent local configuration, but API
  keys must not be logged, stored in analysis events/results, exposed in UI, or
  included in repository-safe summaries.

### Alert receipt consumer boundary

The existing Issue #80 sanitized dependency/export receipt consumer accepts only exact canonical
`alert.receipt.v1` bytes under `sanitized-alert-receipt.v1`. It rejects
malformed, unknown, duplicate, non-canonical, over-8-MiB, over-depth, or
semantically invalid input through one fixed no-leak error. Parser failures,
input fragments, identifiers, paths, values, and inner exceptions never cross
the boundary.

`AlertReceiptConsumerV1` validation returns only the bounded alert ID, Session ID, optional
trace ID, source surface, and last-observed time required for dependency
selection. It does not return raw JSON, evidence arrays, observed values,
thresholds, summaries, or configuration data. Canonical validation is not a
signature, authorization decision, store-provenance proof, or historical proof
that evidence once resolved. Callers retain responsibility for obtaining bytes
from an authorized producer/store. An over-limit receipt is unavailable/failed;
it is never truncated, partially accepted, or rewritten.

The consumer recomputes only the receipt-internal alert ID. Evaluation, input,
and configuration hashes are shape-checked because their source material is not
present in one receipt. Passing those checks therefore cannot establish origin
or authorization, verify a summary against registry metadata, or bind threshold,
capability, source, and completeness claims to an absent configuration/snapshot.
A self-consistent fabricated receipt can recompute its alert ID; authorized store
acquisition and the downstream scanner remain mandatory separate controls. The
fixed failure also covers nonfatal parser/decoder/serializer exceptions,
including malformed Unicode, without preserving an inner exception.

The Alert Center compatibility query is a trusted local-store acquisition
boundary, not a new origin or authentication proof. It reads only the existing
`alert_engine` v1 tables through fixed parameterized queries and bounded cursor
pages. Every returned receipt uses the same strict authority as
`AlertReceiptConsumerV1` and is returned only as exact bytes plus the sealed
fully typed #80-owned Alert Center projection; #84 does not parse it again. One
invalid receipt, schema mismatch, newer component, decode failure, or malformed
suppression makes the complete page unavailable with no partial items, cursor,
database text, input value, or exception detail. Evaluation metadata is a
closed projection of a strictly reconstructed canonical evaluation. Its exact
bytes, canonical token grammar, nested receipt/suppression/rejected-match
contracts, scalar columns, and correlated child row counts must all agree; a
single mismatch fails the whole page without returning data. Suppression
metadata is reconstructed from canonical engine bytes. The query does not read
raw/content tables, accept SQL, infer source/evidence facts, or
claim signing, authorization, producer identity, or store provenance beyond
the caller's explicit use of the trusted local SQLite store.

### Alert lifecycle boundary

Issue #83 stores sanitized lifecycle state as a separate append-only event
chain. It references an existing immutable alert ID and never copies, changes,
or deletes source receipt/evidence bytes. Public reads expose only bounded
state/revision/event projections. User comments and reason values use closed or
bounded sanitized fields and never carry raw evidence, source content, paths,
credentials, PII, or store exception text.

Lifecycle writes require loopback Host validation, same-origin context, CSRF,
an idempotency key, and an exact expected revision. Every lifecycle response is
`Cache-Control: no-store` with fixed no-leak failures. These sanitized routes
remain available under `--sanitized-only`. Source deletion makes an alert
orphaned through an explicit trusted seam while retaining the sanitized receipt
and lifecycle audit chain; it does not restore or expose deleted source data.

### Instruction finding receipt boundary

The Issue #59 `instruction-finding.v1`, `instruction-rule-candidate.v1`, and
`instruction-finding-handoff.v1` carriers are repository-safe allowlist
projections, not raw-analysis results. They may contain only:

- positive numeric analysis-run identity;
- deterministic domain-separated opaque Session / trace / span reference
  tokens and numeric turn references, never the source IDs;
- closed taxonomy, verdict, extractor-source, relative-position, eligibility,
  target-kind, and scope values;
- deterministic IDs and deduplication keys;
- fixed taxonomy-owned Japanese summaries and instruction-rule templates.

Every cross-component consumer must pass the exact carrier bytes through the
source-neutral v1 consumer validator before using or exporting them. The
validator reads no raw store or source path: it accepts at most 1048576 bytes,
limits JSON depth to 16, requires exact canonical UTF-8 bytes, revalidates all
derived identities, templates, associations, ordering, and kind-specific
opaque reference tokens, and returns only the positive analysis-run identity.
Validation failures expose no input fragment, local path, or field value. A
downstream component may not substitute a copied schema/hash/template parser
or treat successful validation as raw-read, capture, export, effect, apply, or
promotion authority.

This is an integrity/self-consistency check, not an authenticity check.
Opaque tokens, hashes, and templates can be reproduced by an untrusted caller;
therefore validation does not prove producer/store provenance or historical
raw-reference resolution. Callers must acquire the exact bytes from a trusted
owner/store, and only the producer's pre-tokenization evidence-index check may
claim raw-reference resolution. The byte/depth envelope bounds consumer work;
candidate reconstruction is semantically capped at the eight closed
categories. No new per-collection ceiling is imposed on frozen v1, and
producer draft admission remains unchanged.

They must never accept, serialize, or persist producer/model-supplied summary,
instruction, title, target, or rule text. This fixed-template construction is
what prevents copying raw prompt/response/tool content, observed source code or
file bodies, credentials, PII, and local paths into generated instruction text.
A permissive redactor or free-text negative scanner is not an alternative
authority for v1.

Every raw-local submission evidence reference resolves exactly within the
supplied anchor and emitted bounded sibling evidence index before tokenization.
Unresolved references are rejected, not downgraded or persisted. Unsupported
categories are rejected. `weak` and `incomplete` receipts may be retained as
repository-safe diagnosis state but are always `ineligible` and cannot generate
a rule candidate. The handoff does not authorize raw reads, Canvas action
output, proposal promotion, file apply, effect measurement, export, or git
activity.

Raw-local Session, trace, and span IDs exist only during exact index
validation. Before constructing a carrier, each is replaced with a
kind-specific `*-ref-<32-lowercase-hex>` token derived by length-framed,
domain-separated SHA-256. This prevents a syntactically valid but identifying
source ID, local label, or PII-like value from entering the repository-safe
carrier while preserving deterministic equality for downstream references.

## Issue #90 Retention Mutation No-Leak Boundary

The versioned `/api/retention/v1/*` surface is a sanitized local-user
mutation/read surface over the #89 catalog. Preview, confirmation, mutation,
status, item, and history responses use `Cache-Control: no-store`. The
following are forbidden in preview content, confirmation/audit content, logs,
history DTOs, cursors, screenshots/evidence, committed fixtures, and
repository-safe handoff material:

- raw bodies, prompt/response content, system content, tool arguments/results,
  or raw payload fragments;
- credentials, secrets, authorization material, or confirmation token values;
- absolute or relative paths, private locators, filesystem names, database
  primary keys, or source-row identifiers;
- prompt-derived labels, user-entered identifying text, PII, full exceptions,
  provider text, or rejected request values.

Only opaque retention item IDs, existing local Session IDs, fixed store kinds,
fixed lifecycle/error/completion codes, safe digests, and sanitized timestamps
may identify data in these DTOs. The confirmation token is permitted only in
the dedicated confirmation-issue response and the immediately following
mutation request body; it is never a preview, audit, history, log, cursor, or
evidence value. Persistence stores only SHA-256 over the exact full ASCII token
string; the plaintext token, nonce/secret material, and token value are never
persisted or logged.

The only error-response linkage is the optional same-origin relative
`Location: /api/retention/v1/mutations/{operationId}` header on a consumed
confirmation response. It contains only the opaque #90 operation ID in the
fixed status route and never a token, target ID, path, source key, or raw
value. The Canvas Session workspace handoff is likewise navigation-only and
places only the exact local Session ID in the Local Monitor retention route;
it adds no retention proxy/action payload or helper token.

The actor label is the server-derived fixed value `local-user`; the client
cannot provide, override, or cause the current OS user name to appear in an
audit event or response. The exact `rid1_...` workflow idempotency key is not a
secret credential and is the explicit exception: it is stored and returned
only in the persisted audit/history read model. It is never logged and is not
emitted by any other DTO, diagnostic, screenshot, evidence record, or
repository-safe output.

## Cross-Surface Doctor Boundary

Issue #105's `/api/doctor/ui/v1` proxy remains inside the installed Local
Monitor's loopback-only, valid-Host, no-CORS boundary. All responses and the
`/diagnostics` Doctor section are `no-store`. Mutations require same-origin and
the existing CSRF token; status retry is GET-only and mutation response loss is
resolved by reading the exact verification and revision before another action.

The proxy may return the unchanged sanitized `FirstTraceEnvelope`, fixed source
registry metadata, and exact navigation targets containing only an opaque
evidence reference, fixed target kind, opaque persisted identity, and a server-
generated same-origin relative href. It must not return raw prompt/response,
tool body, authorization material, credentials, PII, absolute/local paths,
source payload fragments, raw database rows, or exception/provider text.

Navigation does not authorize a raw read. Trace links use the existing trace
page boundary. Exact Session and source-diagnostic links remain inside
`/diagnostics` and render bounded sanitized metadata only. The server rejects
unknown target kinds, malformed identities, non-relative/other-origin hrefs,
and evidence references absent from the returned Doctor result. The browser
uses DOM properties and inert text; it does not use HTML interpolation or
client-side parsing to recover a target from an opaque evidence reference.

## Session Workspace Boundary

Issue #51 Session ingestion remains inside the installed Local Monitor's
single-trusted-local-user, loopback, Host-header, no-CORS boundary. It does not
authorize remote or non-loopback ingest.

### Source capability semantic contract v1

The v1 schema and its manifests are repository-safe metadata only: contract
version, declared surface/adapters/capabilities, required provenance key names,
and fixed completeness vocabulary. They must not contain raw prompt/response,
tool input/output, file or diff content, paths, credentials, tokens, PII, or a
captured event/trace value. `capture_content_state` reports an observed capture
state; it does not grant any caller read, transport, storage, or display
authority. In particular, manifest grants no content authority.

Actual raw-bearing values remain governed by the Local Monitor's existing
loopback, same-origin, no-store, retention, secret-filter, and
`--sanitized-only` boundaries. Sanitized values remain metadata-only and must
not be promoted to raw content because a manifest says a capability is
available. Later adapters must preserve these boundaries while emitting the
required provenance; the contract itself authorizes no new receiver, adapter,
database, migration, HTTP, proxy, or UI DTO.

`POST /api/session-ingest/v1/events` accepts raw-bearing SDK/Hook event payloads.
Requests require JSON and `X-CAO-Session-Event-Version: 1`, are bounded to 1 MiB
and 1..100 events, and return `204` only after commit. Fixed error responses use
only `{ "error": "<code>" }`; responses and logs never echo payloads, raw
exception details, PII, credentials, tokens, or local paths.

Claude Hook provenance in that envelope is sanitized metadata, not content.
`source_application_version`, `adapter_version`, and `normalization_version`
accept only the closed metadata-token grammar in the Session ingest interface;
`schema_fingerprint` accepts only 64 lowercase hexadecimal characters. The
adapter constructs these values independently of Hook payload/content. The
receiver rejects control characters, whitespace, path or URI separators, and
free-form prompt/response/tool/error/path text rather than promoting it into
event provenance. Provenance never changes identity, binding, ownership, or
content authority and is not written to logs.

Session metadata and content are separated. `session_events` and sanitized
`/api/session-workspace` reads do not contain event payload/content. Raw content
is secret-filtered before storage in `session_event_content` and receives
`expires_at = captured_at + 90 days`.

`GET /sessions/{id}/events/{eventId}/content` is
raw-bearing and must enforce same-origin plus `Cache-Control: no-store`. It is
absent under `--sanitized-only` (`404`). Expired content returns `410` with
`{ "error": "raw_content_expired", "content_state":
"expired_pending_deletion" }`; catalog v1 makes the denial irreversible before
durable physical cleanup and retains only a catalog tombstone after deletion.
The frozen v1 shape does not disclose a detailed lifecycle. Pin, unpin, and
delete-now remain Issue #90 scope. Raw Session content must not enter Canvas
actions, `session.send()` prompts, sanitized workspace reads, logs,
repository-safe outputs, static artifacts, Issue/PR/docs, or CI artifacts.

Issue #53 Canvas Evidence remains inside the sanitized boundary. Its
extension-owned token-gated routes proxy only the existing Agent graph and
paged spans APIs. Evidence adds no Session content route, raw span-detail
route, or action DTO. It shows Session event metadata/content_state only and
never follows the raw event-content route. `--sanitized-only` keeps hierarchy,
timeline metadata, and inspector available without reconstructing prompt /
response, tool arguments/results, Skill identity, or test/review facts. Proxy
errors and logs never echo payload content or upstream exceptions.

Issue #54 Canvas Improve stores only local-runtime sanitized proposal metadata:
opaque target label, bounded rationale/effect/risk text, lifecycle timestamps,
and opaque Session/Run/Event/Trace/Evidence references. It must not store or
return raw event content, model response text, source fragments, filesystem
paths, credentials, tokens, PII, tool arguments, or tool results. Its writes
are loopback-only, same-origin, CSRF-protected Local Monitor operations; Canvas
helper proxies remain per-launch-token-gated and no-store. Proposal data never
enters Canvas actions, `session.send()` prompts, logs, committed/repository-safe
files, CI artifacts, static artifacts, Issue/PR text, or docs. Direct apply,
snapshots, and rollback require the Issue #55 boundary and are not authorized
by this proposal interface.

## Historical Source Import Boundary

Issue #79 remains inside the installed Local Monitor's single-trusted-local-
user boundary. It adds no remote source, background discovery, CORS surface,
Canvas action, `session.send()` payload, upload, or repository publication.
Every historical-import request is loopback/Host-header restricted. POST
requests require same-origin, `x-monitor-csrf: local-monitor`, JSON, and the
1,048,576-byte workflow bound; GET and POST responses use `Cache-Control:
no-store`. Fixed errors never echo rejected input or exception text.

The preview request is the only public stage that accepts a literal local
source selection. It accepts one explicit profile-specific root/session or
exact transcript reference only after the user confirms the displayed probe
scope. That literal may exist in the loopback/CSRF request and in private local
ephemeral database state in the `historical_import_previews` row only for the
five-minute workflow lifetime and commit re-probe. Database-backed state is
required so separate CLI processes and a Local Monitor restart retain the exact
selection; process memory is not an equivalent store. It is never returned,
logged, included in request-path/query logging, copied to history/operation/
observation/conflict rows, rendered in screenshots, or placed in repository-
safe evidence. A zero-candidate or expired preview, and any succeeded,
rejected, or failed terminal attempt after exact confirmation, erase the
private locator. A wrong caller binding creates no operation and does not erase
an otherwise live preview. Public projections use only the
opaque `source_selection_id` and fixed source/profile/adapter metadata. Internal
candidate/source-record keys and exact idempotency values are likewise absent
from preview, progress, result, history, and observation responses.

The source reference must be a host-native, fully qualified canonical local
path. Before any source filesystem call, the application, gateway, and system
adapter reject platform-foreign syntax, relative paths, URI/UNC/device forms,
Windows remote drives and alternate streams, reserved device names,
noncanonical segments, and any target or ancestor symlink/junction/reparse
component. Metadata classification does not follow links; a file read, where a
future admitted profile authorizes one, must use the same handle-bound policy
and reject directories, FIFOs, sockets, devices, and other non-regular targets.
On Unix, a native absolute path already present in the process's local mount
namespace is inside this v1 boundary after those checks. The backing of an
opaque mounted filesystem cannot be proven from path syntax, so v1 neither
parses mount tables nor claims generic network-storage detection. On Windows,
a volume the host API reports as `Network` is rejected before adapter I/O.

The persisted expiry is authoritative even when no process is running. An
active Local Monitor schedules physical erasure at expiry, including
rescheduling every unexpired actionable preview loaded after restart. Every new
workflow process first clears expired locator, trusted-probe, and candidate
columns. It recovers only queued/running operations bound to an already expired
preview; an unexpired operation may be owned by another live process and is not
reclaimed. Thus dormant bytes cannot regain authority or be used after the
five-minute lifetime.

The current workflow accepts only `metadata_only`. `--sanitized-only` remains
the Local Monitor display posture and is not a substitute for probe consent;
the historical-import page and sanitized workflow routes remain available in
that mode because they expose no source body. Both current adapters read no
content, return `content_risk = not_read`, and cannot issue confirmation.
`include_content` is rejected before a source probe. A later content profile
must be specified separately, secret-filter into existing
`session_event_content`, and register through #89; it may not create an import
raw store kind or treat the external source file as a retention target.

Workflow output is a closed allowlist of fixed codes, bounded counts with
explicit availability, opaque IDs/digests, source/profile/adapter tokens,
completeness/capability metadata, and sanitized conflict fingerprints. It
contains no raw prompt/response, tool data, source/file body, path, repository,
workspace, PII, credential, authorization value, local database path, raw
exception, reversible source key, or free-form source-derived label. Public
historical observation text is rendered with framework/default DOM escaping as
inert text; no response field is live markup. Logs, tests, screenshots, Issue/
PR/docs, CI artifacts, and extension-matrix evidence use synthetic opaque IDs
and fixed labels only.

An optional exact Session navigation target originates only in a trusted typed
source-specific binding and is a same-origin relative link to an already-
existing Session. The workflow never derives it from a path, repository,
timestamp, text, model, token count, or source-record key. Historical-only
trace controls remain disabled. Selecting the live tab uses the existing
sanitized Session workspace reads and never unions or coalesces live Session
identity with a historical observation.

## Configuration Setup Boundary

Issues #66/#67 create a separate user-invoked configuration setup boundary.
It does not reuse Canvas actions, `session.send()`, proposal apply roots, Local
Monitor HTTP routes, or the monitor database. The trusted actor is the current
local user explicitly invoking the Config CLI or packaged PowerShell wrapper.

Repository-safe command output and the ownership ledger may contain fixed
adapter/target/setting identifiers, timestamps, UUIDv7 IDs, SHA-256 hashes,
state/error codes, restart requirements, and a validated non-credential-bearing
loopback endpoint. They never contain raw setting values, absolute paths,
credentials, tokens, authorization headers, raw exception text, raw telemetry,
prompts/responses, tool input/output, source fragments, or PII.

Immutable apply plans, backups, and transaction journals are private local
runtime data under the platform local-application-data base plus
`CopilotAgentObservability/LocalMonitor/setup/`: `%LOCALAPPDATA%` on Windows,
`$HOME/Library/Application Support` on macOS, and absolute `XDG_DATA_HOME` or
`$HOME/.local/share` on Linux. The injected platform base is the only trusted
override; no setup command option or setup-specific environment override is
accepted. Plans retain
only target locations and validated desired values; they do not retain previous
values read during preview. Flushed apply-time backups retain the exact previous
local state needed for rollback. None is emitted to stdout/stderr, logs, CI,
static artifacts, docs, Issues/PRs, or committed files.
Automatic cleanup is outside Issues #66/#67; DB/log/telemetry runtime data is
never removed by configuration rollback.

File targets require no-follow metadata proving a regular non-reparse target
and non-reparse ancestry from the filesystem root. Classification never opens
FIFO/socket/device content or requires write permission. Unsupported operating
systems fail closed.
Path traversal, UNC/device/URI targets, symlink/junction/reparse points, and
malformed structured configuration fail closed. Apply creates and flushes a
backup and same-directory temporary file before atomic replacement. Every plan
base hash is revalidated before the first write and again at each mutation.
After a temp-path failure, setup never unlinks that pathname because another
actor may have rebound it. Recovery uses journaled target state, not temp cleanup.
Rollback requires the current post-apply hash; force rollback does not exist.

Current-user environment writes for Issue #67 are Windows-only and use the user
environment API, never machine scope or `setx`. macOS/Linux Copilot CLI plans
are inspectable but their apply returns `unsupported_target`; setup never edits
a shell profile or system environment file. The adapter does not add global
`client.kind`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`,
`OTEL_EXPORTER_OTLP_HEADERS`, `COPILOT_OTEL_SOURCE_NAME`, or any credential,
and it does not implicitly enable content capture.
`OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` is read only: a matching
`http/protobuf` override is retained with a fixed warning, and any other value
returns `environment_override_conflict` before a plan is persisted.

VS Code Stable and Insiders writes are limited to each channel's Default
Profile settings file. Non-default profile directories are observed only for
the fixed `vscode_non_default_profiles_not_modified` warning and are never
opened, hashed, backed up, or mutated. Managed policy sources are read-only.
The adapter selects one whole Copilot managed-settings channel in native >
server > file order; its native channel is only Windows `GitHubCopilot` or
macOS `com.github.copilot`, and Linux has no native channel. It never merges
fields from multiple Copilot channels. VS Code enterprise `CopilotOtel*`
policies under `Software\Policies\Microsoft\VSCode`, macOS configuration
profiles, and Linux `/etc/vscode/policy.json` form an independent policy
system. They are checked separately and never suppress Copilot server/file
evaluation. A differing observed constraint from either system blocks the
relevant plan; an equal value is managed/no-write. An unobservable
server-managed policy remains `managed_policy_unverified`, even when an
enterprise policy is present; Copilot CLI uses environment-only detection and
therefore always returns that warning.

Transaction concurrency uses a non-waiting exclusive lock and deterministic
stale checks. There is no retry loop or timing-based conflict recovery. The
journal flushes intent before every file mutation, environment member mutation,
and restore. Apply performs a final all-step desired-state verification before
commit. Normal compensation, rollback, and recovery classify
prior/desired/current state immediately before each restore and never overwrite
a third-party value. Partial failure triggers reverse compensation
and records each outcome without raw exception content. Every command recovers
or reconciles an interrupted journal before normal work. Failed recovery leaves
setup fail-closed: status returns the readable bounded ledger projection and
recovery correlation, while further mutation is blocked until the private local
state is resolved. Status requests no new setup mutation, but recovery may
restore targets before projection.

Apply revalidates adapter registration, planning OS support, every target
version, VS Code Default Profile extension presence, managed policy, logical
member semantics, CLI detect-only protocol overrides, and every distinct
loopback endpoint before creating backups or mutation artifacts. A removed
persisted adapter returns `unsupported_adapter`; macOS/Linux CLI apply returns
`unsupported_target`; both leave the plan/ledger and targets unchanged.

Endpoint recognition is a no-redirect `GET /health/live` with a 500 ms total
budget and 4096-byte response cap. A trustworthy `Content-Length` may prove
oversize up front; otherwise the probe reads at most 4096 payload bytes plus
one sentinel byte. Only HTTP 200 with a JSON object containing
exactly the string property `status: live` is Local Monitor. Connection refused
or a proved absent listener is `monitor_not_running`; any connect/read/total
timeout, redirect, non-200, oversize, malformed/non-object, extra-property, or
other JSON response is `port_owned_by_foreign_process`. The probe is not a lease and does
not claim the listener cannot change afterward. Environment notification
happens only after a final state. Recovery may replay the notification when
prior delivery cannot be proven; exactly-once external broadcast is not
claimed.

## First-Trace Doctor Boundary

Issue #102's shared diagnosis result, persisted verification evidence, Config
CLI commands, and Local Monitor routes follow the data-minimization, local HTTP,
and lifecycle rules in
[`interfaces/first-trace-doctor.md`](interfaces/first-trace-doctor.md). That
interface is the canonical behavior authority; this section is only a security
boundary cross-reference.

## Proposal Apply Boundary

Issue #55 is the only privileged local file-mutation surface. A Canvas action,
`session.send()` prompt, or Issue #54 proposal never has filesystem authority.
The Canvas helper may show source and diff text only on its per-launch-token,
loopback local screen; that text never enters an action DTO, log, persisted
proposal metadata, repository-safe output, CI/static artifact, Issue/PR, or
documentation.

The Local Monitor accepts an apply target only under an explicitly configured
startup root. It accepts an opaque root ID and a normalized relative path, not
an absolute, drive-relative, UNC, device, URI, or `..` path. Every configured
root and every target ancestor is re-resolved before preview, approval, apply,
recovery, and rollback. A symlink, junction, or other reparse point at the
root, any ancestor, or target is rejected. Only an existing regular file may
be changed; directory creation, deletion, rename, permission changes, and git
operations are structurally absent.

All mutation routes are loopback/Host-header restricted, same-origin,
`x-monitor-csrf: local-monitor`, JSON-only, bounded, and `Cache-Control:
no-store`. The server validates the proposal evidence, immutable approval
digest, selected-hunk digest, target base SHA-256, and current target SHA-256.
If one target is stale, unavailable, reparse-bearing, or invalid, it performs
no write. Before mutation it writes and flushes snapshots plus a write-ahead
recovery journal; failure and uncommitted-startup recovery restore every
already changed target. Rollback requires the target's current hash to equal
the recorded post-apply hash, preventing it from clobbering an intervening
external edit. A startup journal whose opaque root ID cannot be safely resolved
through the current configured root set is a fail-closed host-construction
error: Local Monitor maps no mutation surface and does not rediscover or write
the remembered path. Audit records contain only opaque IDs, actor kind, timestamps,
state/error code, and hashes; they never contain paths, diff/source text,
raw Session data, credentials, or tokens.

## Effect Comparison Boundary

Issue #56 compares sanitized, exact-linked local metadata only. Objective
evaluation receipts bind an existing Session, Run, trace, evaluator/version,
criterion, case key, pass/fail severity, and opaque evidence references. They
contain no evaluator note, prompt/response, tool payload, source fragment,
filesystem path, credential, token, or PII. An unlinked normalized measurement
label is not promoted into this boundary.

Candidate suggestions are non-authoritative and may not use repository or
timestamp proximity as identity. Cohort membership and exclusions require an
explicit local-user confirmation. Comparison writes use the same loopback,
Host-header, same-origin, CSRF, JSON, 1 MiB, no-store controls as other Session
workspace mutations. Fixed errors never echo rejected values or exceptions.

Effect summaries, case-key drill-down, and receipts are projections of the
same persisted Session/evidence references. They may expose opaque IDs,
quality counts/fractions, duration/token medians, fixed verdict/reason codes,
application state/time, and verification activity only. They never expose
Issue #55 root/path/hash/diff/snapshot data. Canvas actions, `session.send()`
prompts, logs, URLs, repository-safe/committed output, CI/static artifacts,
Issue/PR text, and docs receive no cohort or evaluation payload. Verified is
an atomic Local Monitor state transition after explicit comparison; there is
no automatic comparison, mutation, rollback, retry, or git operation.

The installed `hook-forward --endpoint <loopback-url> --timeout-ms 250
[--source claude-code [--source-version <metadata-token>]
[--schema-fingerprint <64-lowercase-hex>]]` mode is
fail-open only with respect to the agent Hook decision: invalid input, network
failure, and timeout still exit `0`, stdout/stderr remain empty, and the
forwarder never changes the Hook decision. This does not relax the Session
ingest validation or raw-data boundary.

Omitting `--source` selects only the existing Copilot path. Exact
`--source claude-code` is the sole Claude selector and is evaluated before
stdin; source is never inferred from payload shape. Claude provenance arguments
are valid only with that selector, are metadata-only, and are never read from
Hook payload/content, documentation labels, or inventory-only evidence. If the
selector is missing or invalid, a provenance option appears without it, or
neither a trusted emitting version nor an approved fingerprint is available,
Claude forwarding is suppressed silently rather than inventing provenance.
Any supplied invalid provenance value invalidates the command. Argument values
and rejected payloads are never logged or echoed.

## Historical Evidence Dataset Boundary

Issue #72 reads only the final included Sessions from one coherent bounded
snapshot. It never loads an excluded or metadata-omitted body, and exact OTel/
finding references must resolve uniquely to the selected Session without
repository, workspace, time, or order inference. Raw descriptor reads occur
only inside the existing current retention-authorized local boundary and only
after selection and capacity checks.

The paired repository-safe representation replaces raw Session/trace/span IDs
with the exact #59 domain tokens, omits descriptor text, rejects unsafe labels
or carriers, and contains no raw body, credential, PII, path, or reversible
sensitive key. The raw-local form remains local runtime data and follows the
existing same-origin/no-store/retention controls; `sanitized_only=true` never
requests descriptor content. Canonical checksums and persistence validation
provide integrity, not export authority or provenance attestation.

## Historical Efficiency Receipt Boundary

Issue #74 reads only the exact canonical Issue #72 repository-safe bytes and
does not inspect the raw-local pair, Session/raw stores, paths, labels, or any
out-of-band source. Its receipt may contain only opaque #72 tokens, exact safe
evidence references, fixed registry text/codes, bounded numeric observations,
and safe distributions. Missing values remain absent; generic error spans are
not relabeled as tool failures. Raw content, source/model names, PII, paths,
credentials, price/currency, and monetary values are forbidden. The receipt
checksum proves byte integrity, not quality improvement, effect, origin, or
provenance. Issue #75 consumes this safe receipt without formula recomputation.

## Sanitized Evidence Export Boundary

Issue #85 exports only explicit sanitized projections and exact canonical
receipt bytes from one immutable source-neutral snapshot. Selection and exact
dependency closure run before a bounded fail-closed scanner. Raw OTLP, prompts,
responses, system prompts, tool bodies, source/file bodies, raw analysis,
credentials, authorization values, PII, and local absolute paths are forbidden.
Absent optional #59/#80 carriers remain explicit `missing` capabilities, while
non-v1 #73/#74/#84 slots remain `unavailable`; the export does not infer or
synthesize their evidence.

The Config CLI publishes through a same-directory partial file and atomic
rename only after complete in-memory validation. Local Monitor export routes
retain loopback/Host validation, same-origin, CSRF on writes, JSON-only input,
bounded requests, and `no-store`; the request cannot name a server output path.
The server returns an opaque archive hash and relative route, never a local path.
The v1 surface does not upload, sign, encrypt, import, replay, back up, restore,
or persist new database state. The complete contract is canonical in
[`interfaces/sanitized-evidence-export.md`](interfaces/sanitized-evidence-export.md).

## Raw Local Replay Boundary

Issue #87 is intentionally outside the repository-safe export boundary. Every
preview, export, archive inspection, import, replay, status, and download is a
raw-bearing local operation with the persistent warning and exact confirmation
binding defined by
[`interfaces/raw-local-replay.md`](interfaces/raw-local-replay.md). Known
credential patterns reject the operation without returning the match; a passing
scan is only a narrow guard and never classifies the remaining bundle as safe.
Raw bundles, members, replay artifacts, paths, private retention identities,
and selected raw-store IDs must not enter logs, errors, screenshots, Issue or
review evidence, repository files, CI artifacts, sanitized bundles, or static
dashboard output. Repository-safe evidence is limited to bounded statuses,
counts, contract versions, and non-reversible hashes.

The Local Monitor raw-replay routes remain loopback-only with Host validation,
no CORS, same-origin reads, CSRF on writes, and `Cache-Control: no-store`; they
are separate from `/api/monitor/*`. `--sanitized-only` denies the complete
surface before reading caller archives or source raw/content rows. Config CLI
can only preview/export/inspect; import/replay goes through the running Local
Monitor. Caller-owned archive files are never catalog items or cleanup targets.
Process-local exported archives and replay-preview bytes share the fixed
count/byte/TTL bound and idle expiry defined by the interface; shutdown clears
them. Public provider failures use only the closed mapped codes, and JSON error
bodies cannot interpolate provider-controlled text.

Durable replay staging reuses the existing Retention catalog v1
`sensitive_bundle` item and `sensitive-bundle-7d` policy, including its private
reservation, capture marker, isolated locator, operation leases, queue,
store-specific deletion adapter, retry, and restart recovery. No replay value
can authorize deletion by path, repository, timestamp, prompt, similarity, or
generic adapter label. Replay never writes or merges into the live raw,
Session, projection, analysis, or evidence stores; it never reconstructs a
heuristic Session and never sends raw data to an external model.
Recovery completes before routes or workers start. Seven-day expiry and read
denial are exact; physical cleanup is excluded by an active operation lease,
resumes forward from durable intent and cursor state, and can delete only the
owned retained child. Caller archives, the configured parent, siblings,
pre-existing partial files, and concurrent publisher files are outside its
authority.

## Sanitized Evidence Import Boundary

Issue #86 accepts only bytes that pass the exact Issue #85 v1 archive,
manifest, inventory, checksum, canonical producer, and repository-safe scanner
validation. Traversal/absolute paths, duplicate entries/identities,
symlink/external attributes, compressed/ratio-bearing members, extra or
forbidden files, oversize input, malformed content, and future/unknown versions
fail before persistence. Validation errors are fixed codes and never echo an
entry, content fragment, credential, or local path.

Preview and commit carry sanitized metadata and hashes only. Commit is bound to
the preview digest, revalidates the same bounded archive, and writes the
component-owned record/origin/graph/history set in one transaction. It never
queries or writes raw/content stores and never changes Session, monitor,
finding, alert, or retention authority. Imported rows are sanitized retained
outputs and create zero retention catalog items.

The loopback UI/API preserves Host-header validation, same-origin checks, CSRF
on archive-bearing POSTs, `application/zip` admission, streaming size bounds,
disabled CORS, and `Cache-Control: no-store`. The UI uses framework encoding or
DOM `textContent` for every source-controlled label, ID, error, and history
value; archive content is never injected or executed. Browser storage is not
used for archive bytes or preview binding. The complete contract is canonical
in [`interfaces/sanitized-evidence-import.md`](interfaces/sanitized-evidence-import.md).

## Shared Use Preconditions

Before shared dashboard or real-data publishing:

- define access control。
- define retention。
- define deletion process。
- define masking / redaction。
- define user notice or consent。
- decide identity handling。
- define shared artifact access control。

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

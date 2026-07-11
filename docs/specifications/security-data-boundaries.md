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
  `vcs.repository.name`,
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
  Canvas action DTOs.

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

## Session Workspace Boundary

Issue #51 Session ingestion remains inside the installed Local Monitor's
single-trusted-local-user, loopback, Host-header, no-CORS boundary. It does not
authorize remote or non-loopback ingest.

`POST /api/session-ingest/v1/events` accepts raw-bearing SDK/Hook event payloads.
Requests require JSON and `X-CAO-Session-Event-Version: 1`, are bounded to 1 MiB
and 1..100 events, and return `204` only after commit. Fixed error responses use
only `{ "error": "<code>" }`; responses and logs never echo payloads, raw
exception details, PII, credentials, tokens, or local paths.

Session metadata and content are separated. `session_events` and sanitized
`/api/session-workspace` reads do not contain event payload/content. Raw content
is secret-filtered before storage in `session_event_content` and receives
`expires_at = captured_at + 90 days`.

`GET /sessions/{id}/events/{eventId}/content` is
raw-bearing and must enforce same-origin plus `Cache-Control: no-store`. It is
absent under `--sanitized-only` (`404`). Expired content returns `410` with
`{ "error": "raw_content_expired", "content_state":
"expired_pending_deletion" }`; automatic physical deletion, pin, and
delete-now remain Issue #57 scope. Raw Session content must not enter Canvas
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

The installed `hook-forward --endpoint <loopback-url> --timeout-ms 250` mode is
fail-open only with respect to the agent Hook decision: invalid input, network
failure, and timeout still exit `0`, stdout/stderr remain empty, and the
forwarder never changes the Hook decision. This does not relax the Session
ingest validation or raw-data boundary.

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

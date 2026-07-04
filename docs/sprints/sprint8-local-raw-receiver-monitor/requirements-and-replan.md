# Sprint8 Requirements Re-definition & Replan

Status: **Revised across `/codex:adversarial-review` rounds. The raw/PII access
boundary follows an explicit product-owner threat model (single trusted local
user): defend remote / browser-mediated exfiltration / repository & log leakage;
accept same-local-user-process access and — per the product owner — raw viewing
in any launch mode including unattended/background (round-4's foreground-only
recommendation deliberately not adopted as an accepted risk). `/health/ready`
returns a non-2xx HTTP status under sustained ingestion failure. Per the
product owner's decision to proceed (option 1), Phase 0 source-of-truth
promotion has been applied without a further re-review; the foreground-only
[high] recommendation is recorded as an accepted risk (wontfix), not a defect.**

This document records the requirements decisions and the re-cut milestone/task
plan for Sprint8, produced after re-reading issue #25, its requirements-review
comment (`#issuecomment-4767662582`), and the current source-of-truth docs.

This is repository guidance / planning evidence, **not** product behavior. The
durable product-behavior and public-interface changes it describes are promoted
into `docs/requirements.md`, `docs/spec.md`, `docs/specifications/`, and
`docs/decisions.md` in **Phase 0**, after the adversarial review. Do not treat
this file as the product specification.

## Why this replan exists

M1 (shared component extraction) is complete and committed. But the issue #25
requirements-review comment raised **10 open decision points** that were never
resolved into the specs, and warned that proceeding to implement M2+ without
settling them invites later spec conflicts — specifically around (1) HTTP 2xx
only after raw-store commit while ingestion is queue-decoupled, (2) compatibility
with the existing CLI receiver, and (3) which fields the monitor projection may
display. Those 10 points have now been resolved (Sections 2–3) and the sprint is
re-cut accordingly (Section 5).

## Adversarial-review outcome (how each finding is resolved)

`/codex:adversarial-review` ran several rounds on this replan. The findings and
their final resolution:

### Raw/PII access boundary (three consecutive high findings) — resolved by an explicit threat model

The reviewer escalated three times: (1) loopback + a flag is not access control;
(2) a token printed to the console is not constrained to foreground-only launch;
(3) an "interactive foreground console" is still a capturable channel (IDE /
debug console / task runner / transcript / wrapper), so any reusable bearer
token emitted to a process-readable stream can leak. Each finding was valid
**given the boundary the draft claimed** — a token-secrecy guarantee against
other same-machine processes.

The product owner has set the actual threat model: this is a **single-user local
tool**, and the local user seeing their own prompts / responses on a local UI is
**intended and acceptable** — not the threat to defend against. The draft's
token-secrecy claim was over-scoped, which is what kept generating findings.

Resolution: **DR6 / DD6 are rewritten around an explicit, narrower threat model,
with no bearer-token-to-console mechanism.**

- **Defended (in scope):** remote / non-loopback access (loopback-only bind +
  Host-header validation, anti DNS-rebinding); **browser-mediated exfiltration**
  — a malicious web page using the user's browser as a confused deputy to read
  raw from `localhost` and send it off-machine (CORS disabled, strict
  same-origin via `Origin` / `Sec-Fetch-Site`, and CSRF on state-changing
  actions); and raw/PII leaking into logs or repository-committed artifacts
  (`docs/requirements.md` §8 unchanged).
- **Accepted (out of scope):** another process running as the **same local
  user** reading raw via loopback. Rationale: the raw store, the OTLP payloads,
  and the existing opt-in sensitive-bundle output are **already** readable by
  same-user processes; the monitor does not widen that exposure, and the product
  owner trusts the local user with their own data. Multi-user-OS isolation is a
  documented deployment assumption, not a control.
- **Default posture, and the round-4 [high] decision:** the raw/PII view is
  **off by default**; an operator enables it with an explicit launch flag
  (`--enable-raw-view`) so a health-checking run never serves raw without a
  deliberate choice. Round 4 recommended restricting that flag to interactive
  foreground (attended) execution and refusing service / task-runner /
  redirected launches. The **product owner decided otherwise**: raw viewing is
  permitted in **any launch mode, including an unattended / background /
  always-on receiver**, for convenience (always-on capture plus view-anytime).
  The consequence — raw/PII reachable on loopback for the **full process
  lifetime** rather than only while attended — is an **explicitly accepted risk**
  on this single-user local machine, not an oversight. Retained mitigations
  (unchanged): default-off, loopback-only + Host validation, CORS-off + strict
  same-origin + CSRF (the other-origin browser-exfiltration defense), and no
  raw/PII in logs or repo-committed outputs. No bearer-token mechanism.

### [medium] Readiness can stay green while ingestion is failing — resolved

DD1/M3 return `503` on queue-full, while the original DD5 excluded queue-full and
projection backlog from readiness — so `/health/ready` could report green while
the monitor rejects all telemetry or serves stale projections, hiding the exact
failure modes the monitor exists to surface. Resolved by revising **DD5**:
sustained inability to accept/commit, or projection lag beyond a bounded
threshold, makes the monitor report a required non-green state (`ready=false` or
a required machine-readable `degraded` state automation must treat as
not-fully-ready); momentary backpressure is surfaced as degraded, not as green.
Health semantics gain mandatory tests (sustained queue-full, projection
backlog). **Round-4 refinement:** `/health/ready` must return a **non-2xx HTTP
status** under sustained queue-full / commit failure / projection-lag-exceeded —
not only a body flag — because many readiness probes read only the status;
detailed degraded info goes in the JSON body or a separate endpoint, and tests
assert both the status and the body.

### Spec-delta review (Phase 0) — two findings, both resolved

The `/codex:adversarial-review` run against the Phase 0 working-tree diff returned
needs-attention with two findings, both resolved in the spec promotion:

- **[high] raw/PII read surface not specified as a public interface.** The most
  sensitive path lacked a concrete route, flag-off behavior, status codes, a
  same-origin tie to a route, and absence tests. Resolved: the raw/PII surface is
  now a single **server-rendered route** `GET /traces/{rawRecordId}/raw` (no JSON
  raw API; `/api/monitor/*` and SSE stay sanitized), default-off (route absent ⇒
  `404`), same-origin enforced (cross-site ⇒ `403`), `Cache-Control: no-store`,
  with mandatory absence / forbidden tests. Recorded in `docs/spec.md` Public
  Interfaces, `security-data-boundaries.md`, `raw-store-normalization.md`, and
  D020.
- **[medium] default monitor port collided with the Collector profile (`4318`).**
  `4318` is the Collector OTLP/HTTP port (`ConfigSamples` constants + tests).
  Resolved: the monitor default is now `4320` (avoiding `4317`/`4318` Collector
  and `4319` CLI receiver), the monitor fails deterministically if its port is
  already bound, and the `raw-local-receiver` profile output stays on `4319`
  (sending to the monitor is an explicit override).

### Spec-delta review (Phase 0, round 2) — two findings, both resolved

> **Superseded in part:** the round-2 [high] heavy XSS hardening (raw-route CSP /
> `nosniff` / payload-matrix tests) was de-scoped as over-engineering for a local
> single-user tool — though escaped inert-text rendering is **kept and required**.
> See *Product-owner right-size* below. The readiness contract remains **pinned**.

The second `/codex:adversarial-review` on the spec delta returned needs-attention
with two findings, both resolved:

- **[high] raw HTML view lacked anti stored-XSS / same-origin-escalation
  requirements.** A `text/html` raw view could execute attacker HTML/JS from a
  prompt or tool result in the monitor origin, breaking the DR6
  browser-exfiltration defense from the inside. Resolved: the raw payload must be
  rendered as HTML-encoded inert text (`<pre>`) or a `text/plain` download, never
  as live markup; the raw route sets a strict CSP
  (`default-src 'none'; script-src 'none'; connect-src 'none'; img-src 'none'; form-action 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'`)
  plus `nosniff` / `no-referrer`; and a malicious-payload negative test
  (`<script>` / `<img onerror>` / `<form>` / `javascript:`) is mandatory.
  Recorded in `security-data-boundaries.md` and D020.
- **[medium] readiness degraded contract was unimplementable (no defaults / units
  / config / body schema).** Resolved: concrete defaults (ingestion-stall `10s`,
  projection-lag `60s` = age of the oldest unprocessed `raw_records` row), a
  configuration surface (`--ingestion-stall-threshold-seconds` /
  `--projection-lag-threshold-seconds` + env), and a fixed machine-readable body
  (`status` / `checks` / `degraded_reasons`; `ready`/`degraded`=`200`,
  `not_ready`=`503`) are pinned in `telemetry-ingestion.md`; tests over the
  defaults and an override are required. Recorded in `raw-store-normalization.md`,
  `docs/spec.md`, and D020.

### Spec-delta review (Phase 0, round 3) — one finding, resolved

> **Superseded:** the round-3 default-UI XSS / CSP hardening was later de-scoped
> as over-engineering for a local single-user tool — see *Product-owner
> right-size* below.

The third `/codex:adversarial-review` on the spec delta returned needs-attention
with one finding, resolved:

- **[high] the default monitor UI could be stored-XSS'd to read the opt-in raw
  view same-origin.** Anti-XSS rendering and a strict CSP were required only on
  the raw-detail route; the default UI was specified as "sanitized metadata only"
  without inert rendering or a default-UI CSP. Because span names / attributes are
  attacker-influenced, unescaped allowlisted metadata could execute in the monitor
  origin and, with `--enable-raw-view` on, `fetch` the same-origin raw route
  (passing its `Origin` / `Sec-Fetch-Site` checks, which a same-origin request
  satisfies) and exfiltrate raw / PII. Resolved: a Default-monitor-UI boundary now
  requires all default-UI / list / diagnostics / SSE strings to be HTML-encoded
  inert text and sets a strict default-UI CSP (`script-src 'self'` no inline,
  `connect-src 'self'`, `form-action 'self'`, `base-uri 'none'`, …); a malicious
  span-name / attribute negative test is mandatory in M5 / M6. Recorded in
  `security-data-boundaries.md` and D020.

### Spec-delta review (Phase 0, round 4) — one finding, resolved

The fourth `/codex:adversarial-review` on the spec delta returned needs-attention
with one finding, resolved:

- **[high] no public config surface pointed VS Code at the monitor.** The
  `raw-local-receiver` profile output emitted only the CLI receiver (`4319`), so
  users following the official `profile-vscode-env` workflow would keep sending to
  the old receiver and the monitor would stay empty. Resolved (product-owner
  choice: a flag on the existing command): `config-cli profile-vscode-env
  --profile raw-local-receiver --target monitor` emits the monitor endpoint
  (`http://127.0.0.1:4320`); `--target receiver` (default) keeps `4319`;
  `--endpoint` overrides for a non-default port; `--target` / `--endpoint` apply
  only to `raw-local-receiver` (deterministic error otherwise). Recorded in
  `config-cli.md`, `telemetry-ingestion.md`, `docs/spec.md`, the
  `telemetry-collection.md` user guide, and D020; the monitor-targeted config is a
  tested surface (M2) and part of the live gate (M6).

### Product-owner right-size (after round 4) — display-side XSS / CSP de-scoped

The product owner judged the accumulated display-side hardening to be
over-engineering for a **local single-user tool** and directed: do not add a
heavy anti-XSS / CSP apparatus for displaying the user's own prompts / outputs.
Applied as a deliberate de-scope of earlier review rounds:

- the **dedicated CSP headers, `nosniff` / `no-referrer`, payload sanitizers, and
  XSS payload-matrix tests** from round 2 (raw route) and round 3 (default UI) are
  **removed** as over-engineering. The genuine, free protection is **kept and made
  an explicit requirement**: captured content (raw view + default UI) is rendered
  as escaped, inert text (framework default; no `Html.Raw` / live markup), so
  stored markup does not execute. The accepted residual is only that there is no
  defense-in-depth on top of that default escaping.
- the readiness contract is **kept pinned** (monitoring correctness, not display
  hardening): default thresholds (ingestion-stall `10s`, projection-lag `60s`),
  config flags / env, HTTP status mapping, and a machine-readable body (`status` /
  `checks` / `degraded_reasons`), with default + override tests. (An earlier draft
  of this note trimmed it; it was restored after a follow-up review flagged the
  monitoring contract as too loose.)
- **kept** (cross-machine / other-origin, low cost): loopback bind, `Host`-header
  validation, CORS off, same-origin on the raw route, CSRF on state-changing
  actions, no raw / PII in logs or repo. The port (`4320`), the `--target monitor`
  config surface, and the user-guide accuracy fix are also kept.

This principle is recorded for future work in `AGENTS.md` and agent memory.

## 1. Re-scope statement

Sprint8 changes from a *health-only monitor that never exposes raw* to:

> **Local Ingestion Monitor** — a single loopback-only ASP.NET Core process that
> receives OTLP over HTTP, persists it to the SQLite raw store, shows
> **sanitized metadata only by default**, and — **only under an explicit
> opt-in** — lets the local user view their own raw prompt / response / tool
> content for self-debugging. All raw access is loopback-only, never logged,
> never written to repository-safe outputs, and off by default.

This is consistent with the current product requirements: `docs/requirements.md`
§1 already makes trace-level investigation of prompt/response/tool data a goal,
and §9 already routes individual-trace drill-down to Langfuse / the raw store /
an opt-in sensitive bundle. The "no raw" rules in §8 (repository-committed
files) and §9 (the static HTML dashboard) are unchanged. What changes is the
**Sprint8-local** boundary only: the README non-goal "raw … viewers" and the
D019 premise that the monitor never surfaces raw.

## 2. Decisions confirmed with the user (DR)

| ID | Topic | Decision |
| --- | --- | --- |
| **DR1** | Existing `serve-raw-local-receiver` (`127.0.0.1:4319`) | **Keep, run side-by-side.** LocalMonitor is added as a separate process on a distinct loopback port. The Sprint7 CLI receiver is not removed or deprecated. |
| **DR2** | Concurrent DB access | **Allowed.** While LocalMonitor runs, `normalize-raw` / dashboard generation / diagnosis (the prompt self-improvement loop) can read the same DB. Requires WAL, `busy_timeout`, read transactions, and projection-worker retry on `SQLITE_BUSY` — all test targets. |
| **DR3** | PII attributes (`user.id` / `user.email`) | **Hidden by default; raw value shown only under an explicit opt-in.** Off by default in UI / API / SSE / logs. |
| **DR4** | Raw prompt / response / tool content | **Viewable under an explicit opt-in** (off by default, loopback-only). Adds an opt-in raw-detail view to Sprint8 scope. Projections stay sanitized; raw is fetched on demand from `raw_records` by id only when opt-in is on, never placed in default projections / lists / SSE. |
| **DR5** | Live validation | **Live VS Code Copilot Chat HTTP/protobuf receipt evidence is a hard Sprint8 completion gate** (date, environment, profile value, endpoint, trace id / raw record id — per `docs/requirements.md` §10). |
| **DR6** | Raw/PII local trust boundary (explicit threat model) | The raw/PII view targets a **single trusted local user**; the user seeing their own prompts/responses on the local UI is intended, not a threat. **Defended:** remote/non-loopback access (loopback-only bind + Host-header validation); browser-mediated exfiltration of raw to off-machine destinations (CORS disabled, strict same-origin via `Origin` / `Sec-Fetch-Site`, CSRF on state-changing actions); raw/PII in logs or repo-committed artifacts (§8 unchanged). **Accepted out of scope:** another same-local-user process reading raw via loopback (the raw store / OTLP payloads / existing sensitive-bundle output are already same-user readable; the monitor does not widen exposure); and execution of markup/scripts in the user's own captured telemetry when displayed (no display-side anti-XSS/CSP apparatus — local single-user tool, per the *Product-owner right-size* note). **Default off; any launch mode:** enabled by an explicit launch flag (`--enable-raw-view`), permitted in **any launch mode including unattended / background / always-on** (product-owner-accepted exposure-window risk; round-4's foreground-only recommendation deliberately not adopted); **no bearer-token mechanism.** Mandatory negative tests: non-loopback rejected; cross-origin / cross-site browser request cannot read raw/PII; raw endpoints absent without the flag; raw/PII never in logs or repo outputs. |

## 3. Defaults resolved by the orchestrator (DD)

Safe defaults for the remaining review points; overridable during adversarial
review.

| ID | Topic | Default |
| --- | --- | --- |
| **DD1** | Queue ↔ HTTP success | Request waits for the writer's commit ack; HTTP `2xx` only after commit. Fixed errors: queue full = `503`, commit timeout = `504`, during shutdown = `503`, DB busy after retry = `503`. |
| **DD2** | Schema migration | `schema_version` table + idempotent additive migration adding `monitor_ingestions` / `monitor_traces` to an existing `raw_records`-only DB. Migration failure ⇒ `ready=false`. `normalize-raw` compatibility preserved. |
| **DD3** | Unsupported signals | Only `/v1/traces` accepted. `/v1/metrics`, `/v1/logs`, and other paths fail with a fixed HTTP status and write **no** raw record. |
| **DD4** | SSE | Notification-only. Gap recovery via the cursor API (`/api/monitor/ingestions`, `/api/monitor/traces`). No `Last-Event-ID` replay; the in-memory channel is not the source of truth. |
| **DD5** | Health (revised per adversarial review) | `/health/live` = process responsive. `/health/ready` = loopback bind + DB open + migration complete + writer & projection worker running + no fatal error **and** the writer can accept/commit **and** projection lag within a bounded threshold. **Sustained** queue-full (writer cannot accept/commit beyond a bounded duration), commit failure, or projection lag beyond a bounded threshold ⇒ `/health/ready` returns a **non-2xx HTTP status** (e.g. `503`), **not merely a body flag** — many readiness probes read only the status. Detailed degraded information is carried in the JSON body or a separate endpoint. Momentary backpressure is reported as degraded detail. Thresholds are configurable. Tests verify **both** the HTTP status and the machine-readable body. |
| **DD6** | Local web security (per the DR6 threat model) | loopback-only bind, CORS disabled, request body size limit, Host-header validation. Request logging excludes body / path / query / exception detail. Error responses exclude the DB full path, the Windows user name, and raw exception messages. **Raw/PII view (DR6):** off unless launched with `--enable-raw-view` (permitted in any launch mode including unattended/background — product-owner-accepted exposure-window risk); raw/PII read endpoints enforce strict same-origin (`Origin` / `Sec-Fetch-Site`); state-changing actions require CSRF + same-origin. **No bearer-token-to-console scheme.** Same-local-user processes reading raw via loopback are explicitly out of scope. |

### Open implementation detail (default, confirm during review)

- **LocalMonitor port** (resolved by the Phase 0 spec-delta review): default
  `http://127.0.0.1:4320` with `--port` / `--url` override. `4320` avoids the
  Collector profile's `4317`/`4318` (OTLP gRPC/HTTP; `ConfigSamples` + tests) and
  the Sprint7 CLI receiver's `4319`, so all three coexist on loopback. The
  monitor fails with a deterministic error if its port is already bound.
  `profile-vscode-env --profile raw-local-receiver` continues to emit the
  CLI-receiver endpoint (`4319`); sending VS Code telemetry to the monitor is an
  explicit override (or a future monitor-targeted profile/flag).

## 4. Phase 0 — source-of-truth promotion (Claude-owned)

These edits must land (and pass adversarial review) **before** M2 coding, to
avoid the spec conflicts the review comment warned about.

- `docs/requirements.md` — add the Local Ingestion Monitor to the functional
  scope; state that opt-in raw viewing is loopback-only and distinct from the
  repository (§8) and static-dashboard (§9) non-exposure rules; record PII
  opt-in.
- `docs/spec.md` — add monitor endpoints / port to Current Product Shape and
  Public Interfaces.
- `docs/specifications/layers/telemetry-ingestion.md` — relationship between the
  Config CLI receiver and the LocalMonitor receiver, side-by-side ports,
  unsupported-signal behavior, host model.
- `docs/specifications/layers/raw-store-normalization.md` — monitor projection
  tables, migration, WAL concurrency, existing raw-store compatibility.
- `docs/specifications/interfaces/config-cli.md` — record `serve-raw-local-receiver`
  as kept (side-by-side) and add the LocalMonitor run interface.
- `docs/specifications/security-data-boundaries.md` — the raw/PII **local trust
  model** (DR6): the explicit threat model (defended: remote / browser-mediated
  exfiltration / repository & log leakage; accepted out of scope:
  same-local-user processes), `--enable-raw-view` default-off, strict
  same-origin + CSRF + CORS-off + Host-header validation, and the
  negative-test obligations. Plus the rest of the monitor security additions
  (DD6).
- `docs/specifications/layers/raw-store-normalization.md` / `telemetry-ingestion.md`
  — the required machine-readable health/degraded semantics (DD5): readiness
  reflects whether the writer can accept/commit and whether projection lag is
  within bounds; sustained saturation is a required non-green state.
- `docs/spec.md` — add the `/health/live`, `/health/ready`, and degraded-state
  signal to Public Interfaces.
- `docs/decisions.md` — **D020** (LocalMonitor scope / opt-in raw / side-by-side
  receiver / concurrent DB / live gate / **DR6 local trust model** / **DD5
  health semantics**) and a note on D019 that its monitor-non-exposure premise
  is superseded by D020.
- `docs/sprints/sprint8-local-raw-receiver-monitor/README.md` — drop "raw …
  viewers" from non-goals, add opt-in raw viewing as a goal, update the
  milestone table scope.
- `docs/task.md` — reflect the Sprint8 replan.

No `docs/requirements.md` §8 (repository data safety) or §9 (static dashboard)
relaxation: the opt-in raw view is a **runtime, loopback-only** surface, not a
repository-committed artifact or a published dashboard.

## 5. Re-cut milestones and tasks

M1 is done. M2–M6 are re-defined with the expanded scope. Phase 0 precedes M2.

### Phase 0 — Requirements & Spec promotion (Claude)
- Update the Section 4 files.
- User runs `/codex:adversarial-review` on this replan + the spec delta.

### M2 — ASP.NET Core Receiver Host (`LocalMonitor` project)
- Create `CopilotAgentObservability.LocalMonitor` (Kestrel, net10); add it to the
  `InternalsVisibleTo` friend lists of `Telemetry` and `Persistence.Sqlite`.
- Loopback-only bind; reject non-loopback (absorbs B3).
- `POST /v1/traces` (OTLP HTTP/protobuf) via the shared `RawOtlpIngestor`.
- Request body size limit ⇒ `413` / `request_too_large` (**B1**).
- Reject unsupported signals; write no raw record (DD3).
- Per-request isolation: one failed request never stops the host (**B2**).
- Distinct loopback port (`4320`, avoiding Collector `4317`/`4318` and CLI
  `4319`) with `--port` / `--url`; coexists with the `4319` CLI receiver;
  deterministic error if the port is already bound (DR1).
- Config surface: `profile-vscode-env --profile raw-local-receiver --target
  monitor` emits the monitor endpoint (`4320`); `--target receiver` (default)
  stays `4319`; `--endpoint` overrides for a non-default port; `--target` /
  `--endpoint` only valid for `raw-local-receiver` (else deterministic error)
  (DR1). Tests cover monitor vs receiver output and the invalid-profile error.
- Raw/PII view is off unless launched with `--enable-raw-view` (default off);
  permitted in any launch mode including unattended/background (DR6,
  product-owner-accepted exposure-window risk); no bearer-token mechanism.
- Tests: non-loopback reject, oversized `413`, unsupported signal, malformed
  payload isolation, valid trace persisted, **raw endpoints absent without
  `--enable-raw-view`**.

### M3 — Ingestion Queue + SQLite Concurrency
- Bounded `System.Threading.Channels` queue + single `IngestionWriterWorker`
  (**T5/T7**).
- Schema set up once at startup; single writer; **WAL** (DD2/DR2).
- `busy_timeout` + read transactions enabling concurrent external readers (DR2).
- HTTP `2xx` only after commit ack; fixed errors for queue-full / timeout /
  shutdown / busy (DD1).
- `schema_version` table + idempotent migration (DD2).
- Graceful shutdown: drain the queue, lose no records.
- Health reflects ingestion: sustained queue-full (writer cannot accept/commit
  beyond a bounded duration) or commit failure ⇒ `/health/ready` returns non-2xx
  (e.g. `503`) with degraded detail in the body; momentary backpressure ⇒
  degraded detail (DD5).
- Tests: `normalize-raw` running concurrently during ingestion (no loss /
  corruption), queue-full `503`, commit-then-`2xx` ordering, restart drains,
  migration on a `raw_records`-only DB, **sustained queue-full ⇒ `/health/ready`
  `503` vs. momentary backpressure ⇒ `degraded` `2xx` (HTTP status + body
  `status`/`degraded_reasons`), at default + override threshold**.

### M4 — Monitor Projection (sanitized) + opt-in raw access
- `monitor_ingestions` / `monitor_traces` sanitized projections with a
  **per-table allowlist schema**.
- `ProjectionWorker` + startup catch-up of unprocessed `raw_records`; projection
  failure does not lose raw (retry / failure state).
- Cursor-paginated query; list never loads all payloads (**T6**).
- PII excluded by default (DR3).
- Opt-in raw-detail path: the **server-rendered route**
  `GET /traces/{rawRecordId}/raw` fetches one raw payload by id from
  `raw_records` only when `--enable-raw-view` is set; off by default (route
  absent ⇒ `404`); no JSON raw API; not in default projections / lists / SSE
  (DR4/DR6).
- The raw-detail route enforces strict same-origin (`Origin` / `Sec-Fetch-Site`)
  so a cross-origin browser page cannot read it (cross-site ⇒ `403`);
  `Cache-Control: no-store` (DR6).
- Captured content (raw view + default UI) rendered as escaped, inert text
  (framework default; no `Html.Raw` / live markup) so stored markup does not
  execute; no heavier CSP / sanitizer on top (DR6).
- Projection lag ≥ `projection-lag-threshold-seconds` (default `60`, age of the
  oldest unprocessed `raw_records` row) ⇒ `/health/ready` `503`; shorter ⇒
  `degraded` `2xx` (DD5).
- Tests: default projections carry no raw / PII, cursor pagination, restart
  catch-up, projection-failure isolation, opt-in raw fetch gating, **raw
  endpoints absent without the flag**, **cross-origin browser request cannot
  read raw/PII**, **projection-lag readiness at default + override threshold
  (HTTP status + body `status`/`degraded_reasons`)**.

### M5 — Web UI + SSE
- Razor Pages: Overview / Live Ingestions / Traces / Diagnostics.
- SSE notification stream; reconnect; gap recovery via the cursor API (DD4).
- Opt-in raw-detail view via the server-rendered route
  `GET /traces/{rawRecordId}/raw` (off by default) (DR4); PII opt-in display
  (DR3).
- Raw-detail view and PII display are served only with `--enable-raw-view`; the
  route enforces same-origin (`Origin` / `Sec-Fetch-Site`; cross-site ⇒ `403`)
  and any state-changing action is CSRF-protected and same-origin enforced,
  blocking other-origin browser reads (DR6).
- Diagnostics view surfaces the required health / degraded state (DD5).
- No raw / PII in default views, API, SSE, or logs. Captured content (raw view
  and default UI alike) is rendered as escaped, inert text (framework default; no
  `Html.Raw` / live markup) so stored markup does not execute; this local
  single-user tool does not add a heavier CSP / anti-XSS apparatus on top (DD6).

### M6 — Security + Live Validation (hard gate)
- Non-loopback rejection; Host header validation (DD6).
- Raw non-logging; error responses exclude DB full path / Windows user name /
  raw exception (DD6).
- Oversized rejection; restart recovery end-to-end; security review of the
  opt-in raw path against the DR6 threat model.
- **Threat-model negative-test suite (DR6)**: non-loopback rejected; Host-header
  validation (anti DNS-rebinding); cross-origin / cross-site browser request
  cannot read raw/PII; raw endpoints unavailable without `--enable-raw-view`;
  CSRF-less state change rejected; **captured content rendered as inert text (a
  stored `<script>` displays as text, does not execute)**; raw/PII never appears
  in logs or repo-committed outputs.
- **Health-semantics validation under saturation (DD5)**: sustained queue-full /
  commit failure / projection-lag-exceeded drive `/health/ready` to `503`;
  momentary backpressure yields a `degraded` `2xx`; tests assert both the HTTP
  status and the machine-readable body (`status` / `checks` / `degraded_reasons`)
  at the default and an overridden threshold.
- **Live**: real VS Code Copilot Chat HTTP/protobuf receipt **at the monitor**,
  using the official `profile-vscode-env --target monitor` config (proving the
  documented path reaches `4320`, not the `4319` receiver); evidence records date,
  environment, profile value, `--target`, endpoint, trace id / raw record id —
  **required** (DR5).
- Final `dotnet build` + `dotnet test` green; live evidence recorded.

## 6. Execution flow (per CLAUDE.md)

1. (this doc) Claude drafts the requirements re-definition + replan.
2. **User runs `/codex:adversarial-review`** to challenge the decisions and the
   spec delta.
3. Claude incorporates feedback, then performs the Phase 0 source-of-truth
   promotion.
4. Implementation (M2–M6) is delegated to Codex, milestone by milestone, with a
   plan per milestone.
5. Claude reviews each milestone (build/test green, diff, dependency direction,
   raw / PII non-regression, loopback boundary) before any commit.

## 7. Validation (unchanged baseline)

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Baseline: 0 build errors, 291 tests passing. Keep both green and grow the test
count as each milestone adds coverage.

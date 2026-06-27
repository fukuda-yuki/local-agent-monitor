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

- the raw / PII view is **off by default**. It is enabled only by an explicit
  launch flag (`--enable-raw-view`), so a health-checking run never serves raw
  without a deliberate choice.
- default views, API responses, list endpoints, and the SSE stream carry
  sanitized metadata only; PII (`user.id` / `user.email`) is excluded by
  default.
- there is **no bearer-token-to-console** scheme; a reusable token printed to a
  capturable stream cannot uphold a secrecy guarantee on this machine, so the
  boundary does not claim one.

Accepted out of scope (explicit accepted risk):

- another process running as the **same local user** reading raw via loopback.
  The raw store, the OTLP payloads, and the existing opt-in sensitive-bundle
  output are already readable by same-user processes; the monitor does not widen
  that exposure. Multi-user-OS isolation is a documented deployment assumption,
  not a control.
- `--enable-raw-view` is permitted in **any launch mode, including an
  unattended / background / always-on receiver**. The consequence — raw / PII
  reachable on loopback for the **full process lifetime** rather than only while
  attended — is a **product-owner-accepted risk** on this single-user local
  machine, chosen for convenience (always-on capture plus view-anytime). A
  foreground-only restriction was considered and deliberately not adopted.
- **defense-in-depth beyond default escaping.** The baseline against stored
  markup is the required inert text rendering above; on top of that the monitor
  adds **no** CSP backstop, payload sanitizer, or XSS payload-matrix test suite.
  On this single-user local machine, that residual (e.g. were a subtle
  output-encoding bug to slip past the inert-rendering requirement) is an accepted
  risk, not a separately defended boundary. This is the display hardening
  deliberately not added — see `AGENTS.md` Local-First Risk Posture and D020.

Raw-detail interface (the only raw / PII surface):

- raw / PII is exposed solely through a server-rendered route
  `GET /traces/{rawRecordId}/raw` (HTML), which reads one raw record on demand by
  id from `raw_records`. There is **no JSON raw API**; `/api/monitor/*` and the
  SSE stream never return raw / PII.
- the `--enable-raw-view` launch flag is the gate. Without it the route is **not
  registered** and any request returns `404` (testable absence). The flag is the
  process-level switch; there is no reusable token.
- with the flag on: a request whose `Sec-Fetch-Site` is cross-site / cross-origin
  (or whose `Origin` is foreign) is rejected with `403`; an unknown id returns
  `404`; the response carries `Cache-Control: no-store`, and no raw is written to
  logs.
- **inert text rendering is required.** Captured content (and likewise the
  default UI, list, and SSE-rendered fields) is rendered as **escaped, inert
  text** via the UI framework's default output encoding — never via `Html.Raw` /
  raw `MarkupString`, and never reflected into an HTML, attribute, script, style,
  or URL context as live markup. Stored markup therefore displays as text and
  does not execute, so it cannot pivot to a same-origin read of this route. The
  monitor does **not** add a heavier layer on top of this default escaping (no
  dedicated CSP, `nosniff`, or payload-sanitizer apparatus) — that was judged
  over-engineering for a local single-user tool (see accepted local risk above).

Additional monitor web-security requirements:

- request body size limit (default 30 MiB, configurable via
  `--max-request-body-bytes` / `CAO_MONITOR_MAX_REQUEST_BODY_BYTES`; the concrete
  value and boundary are pinned in
  [layers/telemetry-ingestion.md](layers/telemetry-ingestion.md)); a body larger
  than the limit is rejected with `413` and writes no raw record.
- request logging excludes the body, path, query string, and exception detail.
- error responses exclude the DB full path, the Windows user name, and raw
  exception messages.

Mandatory negative tests:

- non-loopback bind rejected; `Host`-header validation enforced.
- `GET /traces/{rawRecordId}/raw` returns `404` without `--enable-raw-view`, and
  serves raw only with it.
- with the flag on, a cross-site / cross-origin request to the raw-detail route
  is rejected with `403`.
- raw / PII is never returned by `/api/monitor/*` or the SSE stream.
- a state-changing request without CSRF / same-origin is rejected.
- raw / PII never appears in logs or repository-committed outputs.

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

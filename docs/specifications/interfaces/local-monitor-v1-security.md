# Local Monitor v1 Security and Data Boundary

Status: **Accepted current authority**
Route/transport amendment: PO136-A2b, 2026-08-09
Product posture: loopback-only, single trusted local user

## Human UI posture

The Local Monitor human UI exists only in raw-default posture.

Raw-default may display the local user's captured prompt, response, Tool input/result, Sub-agent input, Skill snapshot, local path and AI report where an accepted contract authorizes it.

Every raw-local surface remains:

- loopback-only;
- Host-header validated;
- same-origin;
- `Cache-Control: no-store`;
- escaped inert text;
- retention-authorized;
- bounded by closed routes and payload limits;
- absent from normal logs and repository artifacts.

Exact human-path, query, method, error, browser-history and Session Explorer
POST behavior is owned by
[Local Monitor v1 Route and Session Explorer Transport](local-monitor-v1-route-transport.md).

## Sanitized-only posture

`--sanitized-only` is receiver-only per Issue #159.

- host, ingestion, health and accepted machine APIs remain available;
- Razor Pages and human static assets are not registered;
- `/api/local-monitor/v1/*` is not registered;
- no per-screen metadata-only fallback or explanation page is provided;
- unmatched human GET/HEAD returns empty 404 with no-store.

Existing `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE bytes/availability remain under their frozen contracts.

## Raw-local surface enumeration

The accepted v1 raw-local surface includes only the following owned categories.

### Session and instruction

- first-instruction list label and search index, retained no longer than its source content;
- full instruction/Event content on demand;
- exact raw record/span/event technical links.

Authority: #133/#134 and existing retention contracts.

### Tool / Sub-agent / Event

- Tool input, result and error;
- exact Sub-agent input when captured;
- Event/error/permission content.

Authority: #133/#134.

### Skill

- historical Skill body and definition path captured only by the accepted
  additive v2 exact `skill.invoked` Session Event/snapshot transaction;
- current file only after explicit POST action and current inventory/path/root/reparse validation.

Frozen Session ingest v1 supports `skill.started | skill.completed` and treats
`skill.invoked` as unsupported without changing any other v1 wire, limit,
status or response bytes. Authority:
[Skill Invocation Snapshot](skill-invocation-snapshot.md), #157/#158.
Historical snapshot and current file are never substituted for one another.
Invocation/inventory claim validity is independently owned by
[Skill Projection](../layers/skill-projection.md). A retained snapshot cannot
resurrect a claim. OTel claims require a current resolved compatibility
revision/generation; SDK Session/Event claims instead require exact persisted
source identity and current-registry acceptance of their complete compatibility
tuple, without requiring trace/span. OTel projection workers hold exact
Retention operation leases through their publish fence; an unavailable input
publishes no OTel claim.
Cross-arm linkage requires exact producer trace ID and span ID together;
name/path/time/cardinality and Session co-membership never link claims.
Production v2 writer, snapshot migration, discovery/current-file service and
raw-local routes remain unregistered until every decision gate in the snapshot
interface is closed.
Startup configuration is the sole current-file discovery-root authority:
`SkillDiscovery.ProjectPaths` (at most 16) and
`SkillDiscovery.SkillDirectories` (at most 32). `DiscoverAsync` receives only
those validated roots. Historical path/CWD, Repository locator, prompt,
workspace, time and out-of-root results never create a root, and the service
never opens the historical path directly. Only an exact accepted discovery
result proceeds through the platform no-follow handle walk; the final
name/path comparison and filesystem-identity proof remain blocked decisions.

### Repository

- canonical GitHub locator only in a dedicated management read;
- standard Repository cards and Session rows do not return raw locator/path/owner.
- admitted canonical locator, fingerprint, display casing and bounded
  provenance are catalog-owned durable raw-local metadata after raw expiry;
  they do not reconstruct the raw body or expose the internal raw-record
  reference;
- Repository management routes and catalog data are absent in receiver-only
  and `--sanitized-only` composition and excluded from sanitized evidence
  export/import.

Authority:
[Local Repository Catalog and Session Assignment](local-repository-catalog.md),
#155/#156. #134 alone owns the composite
`GET /api/local-monitor/v1/repositories` route.

### AI

- immutable Session report content;
- transient node/Repository/Compare result;
- provider-bound raw reads through process-internal tools only.

Authority: #162/#163/#164.

### Backup / replay

Runtime backup and raw replay retain their existing separate explicit authorities. Backup is not a repository-safe export.

## `/api/local-monitor/v1` boundary

This namespace is raw-default human-UI support, not a sanitized public API.

- all responses are no-store;
- mutations require same-origin and CSRF;
- the body-bearing Session collection read also requires same-origin and the
  exact CSRF header because it carries raw-local search/model state;
- request bodies are closed and bounded;
- error responses use fixed codes and never echo raw input or inner exceptions;
- raw values, display names, local paths and provider content never become URL identity;
- routes are absent in sanitized-only posture.

The sole Session collection transport is the 32,768-byte closed JSON
`POST /api/local-monitor/v1/sessions`. The former unimplemented GET is removed;
there is no alias, saved-search handle, compatibility reader or fallback. This
fixes the request/security boundary only; active registration waits for the
later canonical #134 exact success-response contract.

## Retention

- every raw content read uses the existing Retention catalog access lease;
- an active access/operation lease prevents physical deletion;
- expiry/delete denial is rechecked at read time;
- derived raw instruction labels cannot outlive their source content;
- Skill body/path index may outlive content only as digest/provenance/expired state;
- sanitized Skill invocation/inventory rows are readable only through the
  current-valid #154 projection authority;
- archive does not extend retention;
- Session AI report content uses analysis retention;
- node/Repository/Compare AI operational content is deleted after 24 hours and is not backed up;
- Compare deterministic snapshots are deleted after 24 hours and are not backed up.
- Comparison expiry retains only the route/transport-owned append-only
  `(comparison_id, repository_id, expired_at)` tombstone in the current runtime
  database. It contains no cohort, Session, filter, receipt, evidence, metric,
  hash, content, model or path and has no list/read API. Runtime backup removes
  the exact table transactionally from its private staging copy before
  inventory/hash/archive, never from source; manifest/restore omit it and
  restore startup creates it empty. Sanitized export/import never queries it.

## AI provider egress

AI execution crosses the local-only boundary.

- provider is GitHub Copilot SDK in v1;
- actions are visible only when provider-ready;
- the user starts every run explicitly;
- Settings permanently explains that selected content may be sent to GitHub Copilot;
- Repository selection requires a scope preview;
- the provider receives only one bounded immutable snapshot and process-internal tools constrained to its evidence index;
- the SQLite file and arbitrary SQL are never exposed;
- scope-outside identifiers are rejected;
- credentials and secrets are removed by the accepted filters and closed tool responses;
- provider request/response/raw bodies/paths are not logged or emitted in diagnostics/artifacts.

## Repository-safe outputs

Sanitized evidence export, static dashboards, GitHub Issues, repository docs, logs and test artifacts may contain only their existing allowlisted contracts.

They must not contain:

- prompt/response/Tool/Sub-agent/Skill body;
- raw local paths or Repository locators;
- credentials/PII;
- raw AI provider input/output;
- reversible sensitive identifiers unless an existing contract explicitly authorizes them.

## Browser state

- no raw value is stored in URL, browser storage or reusable cache;
- Explorer URLs carry only canonical timestamps, closed source/status,
  `has_skill`/`has_subagent`/`has_error`/`has_retry`, archive/mode/Settings
  tokens and an eligible opaque process-keyed cursor;
- dynamic `q` and `model` values exist only in current-page form/JavaScript
  memory and the bounded Session POST body; non-default limit is also transient;
  reload/back clears all three;
- URL cursor eligibility requires exact q=null/model=[]/limit=null/default 50;
  other cursors remain page-memory/POST state;
- cursor HMACs bind the complete semantic filter without exposing raw or
  normalized q/model values or an unkeyed low-entropy digest;
- comparison and AI URLs carry opaque snapshot/run IDs only;
- pre-preview Compare checkbox selection is transient;
- follow-up AI transcript is held only in the current browser page and is not persisted;
- other Settings/query state contains only the route/transport-authorized
  canonical timestamps, closed tokens, Booleans and opaque IDs/cursors.

## Rendering

- text is inserted with server escaping or DOM `textContent`;
- generated Markdown/HTML is not executed;
- color is not the only signal;
- long content is bounded and never partially returned when its owner contract requires fail-closed oversized state.

## Frozen boundaries

The v1 redesign does not modify:

- `/api/monitor/*` v1 shape/order/bytes;
- `/api/session-workspace/*` v1 shape/order/bytes;
- SSE shape/order/bytes;
- `session_events.content_state` vocabulary;
- exact identity/provenance rules;
- no heuristic Session/Repository/parent binding;
- no missing-to-zero or composite score.

# Local Monitor v1 Security and Data Boundary

Status: **Accepted input to Issue #118**  
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
- request bodies are closed and bounded;
- error responses use fixed codes and never echo raw input or inner exceptions;
- raw values, display names, local paths and provider content never become URL identity;
- routes are absent in sanitized-only posture.

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
- comparison and AI URLs carry opaque snapshot/run IDs only;
- pre-preview Compare checkbox selection is transient;
- follow-up AI transcript is held only in the current browser page and is not persisted;
- Settings/query state contains only closed section tokens and opaque IDs.

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

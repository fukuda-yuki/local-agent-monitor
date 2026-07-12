# Issue #61 Source Capability Contract Design

## Goal

Define one versioned, machine-readable contract for source capability,
provenance, field authority, and deterministic completeness without changing
any receiver, adapter, database, HTTP, proxy, or UI implementation.

## Contract ownership

- `docs/specifications/contracts/source-capabilities/v1/source-capability-manifest.schema.json`
  is the structural source of truth. It uses JSON Schema 2020-12 and rejects
  unknown properties.
- One JSON document per source surface under the same `v1/manifests/` directory
  is the source of truth for declared capabilities.
- Canonical Markdown defines semantics that JSON Schema cannot express:
  evidence authority, precedence, provenance absence, completeness derivation,
  data-safety boundaries, and the adapter handoff.
- A repository test parses every real manifest, validates the exact contract,
  checks uniqueness and cross-file references, and prevents a Markdown-only or
  fixture-only contract from drifting.

## Surface boundary

The initial registry contains distinct entries for GitHub Copilot in VS Code,
GitHub Copilot CLI, Claude Code, Codex App, and Codex CLI. `source_surface`
identifies the product surface; `source_adapter` identifies the normalizer that
produced evidence. Codex App and Codex CLI never share a surface identity.

Each manifest declares support status, stability, version detector, signals,
native identity, hierarchy/timing, model and token data, retry/tool/permission
and error evidence, agent ownership, content/file availability, and the
content-capture gate. Capability values describe what the source can supply;
they do not fabricate observed evidence.

## Authority and provenance

Field families use an ordered authority table. Available OTel identity,
hierarchy, and timing are authoritative. Hook or SDK events are authoritative
for native lifecycle and explicit event identity. Historical summaries may
fill only explicitly allowlisted fields. Lower-authority evidence never
overwrites higher-authority evidence, including with a non-null value.

Every normalized value is traceable to adapter ID, source version or schema
fingerprint, source event ID or trace/span ID, capture/content state, and
normalization version. If required provenance is absent, the value is not
promoted to authoritative evidence; absence contributes the applicable
completeness reason instead of triggering inference.

Repository, workspace, and timestamp proximity are context only. They never
bind or merge identity. The contract prohibits heuristic session merge and
synthetic span creation.

## Completeness

The existing ordered statuses remain `unbound`, `partial`, `rich`, and `full`.
Evaluation is a pure decision over declared surface requirements and observed
evidence. It returns one status and a stable, de-duplicated reason-code list in
the canonical order defined by the specification.

`missing_native_session_id` forces `unbound`. Otherwise missing lifecycle or
required input evidence caps the result at `partial`; missing content or
terminal evidence caps it at `rich`. Unsupported versions, ingest gaps,
inexact/missing required trace context, and historical-only evidence prevent
`full`. Historical summaries never independently raise a Session to `full`.
Planned or disabled sources report their explicit reason rather than being
treated as captured evidence.

## Data safety

The schema and manifests are repository-safe metadata. They contain no raw
prompt/response, tool input/output, file/diff content, paths, credentials, or
PII. `capture_state` distinguishes raw availability from normalized sanitized
metadata. Raw-bearing content remains subject to existing local-only storage,
read, retention, and `--sanitized-only` contracts; the manifest grants no read
or transport authority.

## Testing and delivery

The executable contract test uses the committed producer-facing manifests,
not hand-maintained mock DTOs. It verifies required properties, enums, exact
surface separation, schema/contract versions, capability shapes, reason codes,
authority families, and canonical document references. No network or new
dependency is required.

Issue #61 has no schema migration, concurrent state mutation, rollback, or
stale-state implementation. Review must explicitly confirm these remain out of
scope and that the contract does not silently authorize them.

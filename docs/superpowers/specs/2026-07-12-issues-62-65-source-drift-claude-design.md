# Issues #62-#65 Source Drift and Claude Code Design

## Goal

Implement schema-drift detection and trace-fidelity regression protection,
then add Claude Code ingestion, exact Session binding, projection, UI, and
fixture-backed validation while preserving GitHub Copilot behavior.

## Architecture

The source-independent compatibility subsystem records an immutable
observation per committed ingest batch. A separate Claude adapter consumes
raw OTel and Hook records under the Issue #61 authority rules. Exact Session
binding feeds existing sanitized projection and HTTP DTOs; UI and Canvas reuse
those DTOs without inventing hierarchy or missing values.

## Decisions

- Compatibility follows schema fingerprint, not an application-version
  receive allowlist.
- Ingest batch is the compatibility authority; Session status is derived.
- Claude OTel owns identity/parentage/timing; Hook owns lifecycle/event ID.
- Claude ownership is exact-source-parentage-only.
- Unavailable live Claude surfaces become explicit follow-up work and do not
  replace automated implementation or validation.

## Components

1. Source schema observation model, fingerprint builder, bounded unknown model,
   and focused SQLite store.
2. Compatibility evaluator and sanitized diagnostic projection.
3. Fixture/golden trace-fidelity harness using actual producer DTO shapes.
4. Claude discovery inventory and mapping tables.
5. Claude OTLP and Hook adapters, normalization, provenance, exact binding, and
   deterministic idempotency.
6. Additive monitor/session projection, existing HTTP DTO extensions, and UI.
7. Repository-safe live-validation template and blocker handoff.

Source compatibility is served from the separate sanitized
`GET /api/monitor/source-diagnostics` cursor endpoint. D051 process readiness
is unchanged. JSON/protobuf structural inventory is captured before any lossy
normalization step.

## Data flow and failure handling

Raw persistence commits before an accepted response. Compatibility observation
is committed atomically with the batch handoff; failure cannot leave a false
supported observation. Unknown records remain addressable through opaque raw
references but their values never enter sanitized storage. Adapter failure,
recognized-record drop, unsupported source, and schema drift are distinct.

Hook forwarding stays loopback-only, short-timeout, fail-open, and silent on
stdout/stderr. Duplicate input is deterministic and idempotent. No retry is
introduced to mask a non-reproducible failure.

## Migration

Only additive tables/columns are permitted. Migration evidence uses actual
shipped-version database fixtures, migrates transactionally, closes and
reopens the store, and verifies old rows plus the new schema. Column-dropping
pseudo-fixtures are prohibited.

## Security

Fingerprints and unknown metadata use structural names/types only. Sanitized
APIs, SSE, diagnostics, golden artifacts, logs, and repository evidence contain
no raw body, PII, credential, source text, or sensitive path. Existing
loopback, same-origin, no-store, secret-filter, and `--sanitized-only`
boundaries remain authoritative.

## Testing and review

Each implementation task begins with a failing focused test and ends with an
independent reviewer. The requirement-to-test ledger is the completion gate,
not a DONE message or test count. An early cross-Issue integration review runs
as soon as #62 diagnostics and #64 normalized DTOs meet. A final independent
security/concurrency/migration review precedes the full pinned validation.

If two consecutive review-fix cycles occur in one area, implementation pauses
for a contract and test-design audit. Full-suite failures use systematic
debugging and never speculative retries.

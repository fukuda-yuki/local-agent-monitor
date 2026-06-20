# M4: Raw Store Integration

## Goal

Connect local receiver output to the existing raw data loop.

## Scope

- Persist received raw telemetry to SQLite raw store or a raw OTLP file that can
  be ingested by `ingest-raw`.
- Keep existing `normalize-raw` output schema unchanged.
- Preserve trace-level reference IDs and resource attributes.
- Keep raw receiver output local and uncommitted.

## Verification

- Synthetic receiver payload can be normalized into measurement JSON / CSV.
- Existing raw store and normalization tests continue to pass.

# Issue #110 Redacted Sentinel Design

## Decision

For raw Claude Code OTLP ingestion, the exact ordinal string value
`<REDACTED>` in `claude_code.interaction.user_prompt` is the producer's
gate-disabled sentinel. It derives `capture_content_state = not_captured`, not
`available` and not `redacted`.

An interaction prompt derives `available` only when its OTLP `stringValue` is
non-empty and is not the exact sentinel. An absent key, an empty string, a
non-string value, or `user_prompt_length` without usable prompt content derives
`not_captured`. Other existing gated signals (`tool.output` and `file_path`)
retain their current behavior. A batch without recognized Claude spans retains
the fixed raw-OTLP fallback (`unsupported` through the caller).

## Safety boundary

The resolver may inspect the JSON token in place for type, emptiness, and exact
ordinal sentinel equality. It must not copy prompt content into DTOs, logs,
exceptions, test output, or repository-safe evidence. Tests use synthetic or
sentinel-only values.

## Compatibility and migration

This changes only derivation for newly ingested batches. It adds no public enum,
route, DTO field, database schema, compatibility shim, dependency, or migration.
Persisted historical observations are immutable and are not backfilled.

## Verification

Unit tests pin actual content, exact sentinel, empty, absent, length-only, and
non-string cases. Integration tests pin ingest-to-monitor projection and Claude
Doctor's `ContentCaptureStatus.Disabled` mapping. The repository validation
suite remains the final automated gate. The existing #106 gate-disabled live
harness is rerun when its external Claude surface and authorization are
available; repository-safe evidence must contain no prompt body.

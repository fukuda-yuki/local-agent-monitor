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

## Evidence 2026-06-21

Implemented:

- Receiver records are written to the existing SQLite raw store as
  `source=raw-otlp`.
- The receiver preserves trace ids and resource attributes through the existing
  `RawOtlpIngestor` path.
- Normalized measurement, candidate, and dashboard schemas were not changed.

Validation:

- `RawLocalReceiverIntegrationTests` writes a synthetic protobuf request through
  the receiver handler, runs `normalize-raw`, and verifies
  `client_kind=vscode-copilot-chat`, `experiment_id=baseline`, token counts,
  duration, and trace id.
- Synthetic smoke started the foreground receiver, posted one JSON trace and
  one protobuf trace to a temp SQLite raw store, and `normalize-raw` produced
  2 measurement rows.
- `dotnet test CopilotAgentObservability.slnx` passed with 290 tests,
  0 failures, and 0 skipped tests.

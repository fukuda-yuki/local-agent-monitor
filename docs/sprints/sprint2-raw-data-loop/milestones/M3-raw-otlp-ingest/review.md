# M3 Review: raw OTLP ingest

## Scope

- Sprint2 M3 raw OTLP file-based ingest.
- `config-cli ingest-raw <raw.json> --db <raw-store.db>` command.
- Raw OTLP trace id / Resource Attributes extraction into the M2 SQLite raw store.

## Changed Files

- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/CliApplicationTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/RawOtlpIngestorTests.cs`
- `docs/sprints/sprint2-raw-data-loop/milestones/M3-raw-otlp-ingest/task.md`

## Review Findings

- Spec compliance: The command accepts only saved raw OTLP JSON files and requires `--db`; no HTTP receiver, daemon, independent OTLP receiver, or `normalize-raw` behavior was added.
- Functional correctness: The ingestor stores `source=raw-otlp`, the first non-empty `span.traceId`, the first non-empty Resource Attributes object, ingest-time UTC `received_at`, and the full raw payload.
- Data handling: Tests use synthetic OTLP envelopes only. No credential, secret, Base64 header, real prompt / response content, or real user identity was added.
- Maintainability: The implementation follows the existing hand-written CLI dispatcher and parser style. No new dependency was added.

## Tests

- `RawOtlpIngestorTests` covers trace id extraction, Resource Attributes extraction, multiple `resourceSpans`, missing trace id / Resource Attributes, and OTLP array / kvlist value shapes.
- `CliApplicationTests` covers successful ingest and deterministic errors for missing input, missing `--db`, missing `--db` value, unknown option, missing input file, and malformed JSON.
- `dotnet build CopilotAgentObservability.slnx`: passed after rerun in serial.
- `dotnet test CopilotAgentObservability.slnx`: passed, 141 tests.

## Notes

- The first build attempt was run in parallel with `dotnet test`; build failed because ConfigCli `obj` output was locked by the other process. The serial build rerun passed.
- `normalize-raw`, measurement schema conversion, span classification, and raw-store-to-loop wiring remain M4 / M5 scope.
- Follow-up subagent review found three actionable gaps: `ingest-raw --db <db>` should be classified as missing input, missing/malformed input failures should assert no DB creation, and SQLite open/write failures should be deterministic CLI errors. These were accepted and fixed.
- Follow-up subagent review also noted `resource_attributes_json` is a key/value projection and `trace_id` indexes the first non-empty trace id in an OTLP batch. These are accepted M3 limitations because `payload_json` preserves the full raw payload; M4 should avoid treating those projection fields as the complete raw OTLP batch.
- Re-review after the follow-up fix found no remaining blocker / major / minor issues.

## 2026-06-08 follow-up review

### Sub-Agent finding and Main-Agent evaluation

- Finding: `ingest-raw` accepted any syntactically valid JSON as `source=raw-otlp`; inputs without a top-level `resourceSpans` array could be stored and fail later during `normalize-raw`.
  - Evaluation: Accepted. M3 limits input to saved raw OTLP JSON files, and the M4 normalizer already treats a missing top-level `resourceSpans` array as invalid raw OTLP.
  - Fix: `RawOtlpIngestor` now validates that the input has a top-level `resourceSpans` array before creating the raw record.
  - Regression test: `CliApplicationTests.Run_IngestRaw_ReturnsNonZeroForNonOtlpJson` verifies deterministic error handling and no DB creation for valid non-OTLP JSON.

### Remaining risk

- The validation remains intentionally minimal for Sprint2 MVP. It verifies the raw OTLP traces envelope but does not validate the full OTLP schema.

## 2026-06-08 re-review follow-up

- Re-review result: The previous non-OTLP object JSON finding was resolved.
- New finding: Top-level non-object JSON such as `[]` could take a non-deterministic exception path because the envelope validator called `TryGetProperty` before confirming an object root.
  - Evaluation: Accepted. It is the same invalid raw OTLP input class and should return the same deterministic CLI error without creating a DB.
  - Fix: `ValidateRawOtlpEnvelope` now checks `root.ValueKind == JsonValueKind.Object` before reading `resourceSpans`.
  - Regression test: `CliApplicationTests.Run_IngestRaw_ReturnsNonZeroForNonObjectJson` verifies deterministic error handling and no DB creation.

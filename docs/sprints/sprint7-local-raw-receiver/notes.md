# Sprint7 Shared Agent Notes

This file records implementation-time findings that should be visible to later
agents working on Sprint7. It is not a product specification. Promote durable
behavior decisions to `docs/requirements.md`, `docs/spec.md`, or
`docs/specifications/` before implementation depends on them.

## 2026-06-21: Worktree And Baseline Findings

- The starting checkout was a normal `main` checkout, not an existing linked
  worktree.
- `main` was ahead of `origin/main`; there were no uncommitted changes before
  the Sprint7 worktree setup.
- `.worktrees/` was not ignored initially. Added `.worktrees/` to `.gitignore`
  and committed that preparatory change before creating the worktree.
- Created the implementation branch/worktree:
  `codex/sprint7-raw-local-receiver` at
  `.worktrees/sprint7-raw-local-receiver`.
- Baseline before the worktree:
  `dotnet build CopilotAgentObservability.slnx` passed with 0 warnings and
  0 errors.
- Baseline before the worktree:
  `dotnet test CopilotAgentObservability.slnx` passed with 252 tests,
  0 failures, and 0 skipped tests.
- Baseline inside the worktree:
  `dotnet build CopilotAgentObservability.slnx` passed, but restore emitted
  existing `NU1903` vulnerability warnings for
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 and `MessagePack` 2.5.192.
- Baseline inside the worktree:
  `dotnet test CopilotAgentObservability.slnx` passed with 252 tests,
  0 failures, and 0 skipped tests, but restore emitted the same existing
  `NU1903` vulnerability warnings.

## 2026-06-21: Raw Local Receiver Design Findings

- Current Sprint7 source-of-truth documents require `raw-local-receiver`, but
  the exact receiver command name, receiver arguments, default loopback
  endpoint, and HTTP response contract are not yet specified.
- Current `profile-*` commands intentionally return a Sprint7 reserved error
  for `raw-local-receiver`; Sprint7 implementation must update the relevant
  specification before removing that error.
- VS Code documentation for Copilot Chat OTel monitoring
  (`https://code.visualstudio.com/docs/agents/guides/monitoring-agents`)
  states that `OTEL_EXPORTER_OTLP_PROTOCOL` defaults to `http/protobuf` and
  that only `grpc` changes behavior. A JSON-only OTLP HTTP receiver is
  therefore unlikely to satisfy Sprint7 M5 direct VS Code validation.
- Adding protobuf support may require a dependency or generated-code decision.
  Do not silently add that dependency without recording the source-of-truth
  decision first.
- A reader Sub-Agent recommended keeping the initial receiver inside
  `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/` to reuse the
  existing internal raw store and normalization path, avoiding a premature
  shared library split.
- The first M2 docs/plan edits were accidentally applied to the original
  checkout instead of the Sprint7 worktree because `apply_patch` used the
  thread cwd. The changes were copied into the worktree, and the original
  checkout was restored to a clean state. Continue editing from the worktree
  path explicitly.
- The environment does not have the standalone `nuget` CLI. Package inspection
  used an ignored scratch project under `tmp/pkginspect/` instead.
- `dotnet package search OpenTelemetry.Proto --format json --take 10` did not
  show an official `OpenTelemetry.Proto` package. The official
  `OpenTelemetry.Exporter.OpenTelemetryProtocol` package exists, but scratch
  inspection did not reveal an obvious public receiver-side
  `ExportTraceServiceRequest` model. Avoid assuming that package can decode
  incoming OTLP protobuf requests without a focused proof.
- The Task 2 writer Sub-Agent reported DONE for
  `RawLocalReceiverOptions.cs` and `RawLocalReceiverOptionsTests.cs`, but the
  files were not automatically present in the main worktree. Main chat asked
  the Sub-Agent for a complete patch before integration. Always verify
  subagent-reported file changes with `git status` in the integration worktree.
- Re-running Task 2 targeted tests in the integration worktree passed 14 tests.
  The run emitted the existing `NU1903` warnings for
  `SQLitePCLRaw.lib.e_sqlite3` and the existing `NETSDK1057` preview .NET SDK
  message.
- The Task 3 writer Sub-Agent returned a complete patch rather than relying on
  direct workspace synchronization. After integration, the targeted
  `RawOtlpIngestorTests` run passed 5 tests and emitted the same existing
  `NU1903` and `NETSDK1057` messages.
- During Task 4 RED test authoring, the first attempt failed because of test
  syntax / variable-name mistakes rather than the intended missing converter.
  The test was corrected and then failed for the intended missing
  `OtlpProtobufTraceConverter` type before production code was added.
- Task 4 targeted `OtlpProtobufTraceConverterTests` passed 1 test after adding
  the minimal internal protobuf trace converter. The run emitted the same
  existing `NU1903` and `NETSDK1057` messages.
- A combined targeted run for Task 2-4 tests
  (`RawLocalReceiverOptionsTests`, `RawOtlpIngestorTests`, and
  `OtlpProtobufTraceConverterTests`) passed 20 tests and emitted the same
  existing `NU1903` and `NETSDK1057` messages.
- Task 4 spec/code reviews requested fixes before approval: convert event
  `time_unix_nano` to `timeUnixNano`, cover event/status conversion in tests,
  preserve signed `int64` values, and normalize malformed protobuf length
  overflow to `InvalidDataException`. These fixes were applied, and the
  targeted `OtlpProtobufTraceConverterTests` run passed 6 tests with the same
  existing warnings/messages.
- During Task 5 RED test authoring, the intended compile failure occurred
  because `RawLocalReceiverHandler` and `RawLocalReceiverRequest` did not exist.
  The run also emitted the same existing `NU1903` and `NETSDK1057` messages.
- During Task 5 GREEN verification, the first handler test run failed because a
  test asserted that the JSON error response did not contain `{`, even though
  every JSON response starts with `{`. The assertion was corrected to inspect
  the structured error body instead.
- Task 5 targeted handler/integration tests passed 7 tests after adding the
  deterministic handler and protobuf-to-store-to-normalize integration path.
  The run emitted the same existing `NU1903` and `NETSDK1057` messages.
- Task 5 code-quality review found two actionable issues: raw store write
  failures escaped the handler instead of returning a deterministic failure,
  and empty/non-trace payloads could be accepted as successful raw telemetry.
  Added failing tests for database persistence failure, empty JSON spans,
  empty protobuf payloads, and malformed protobuf at the handler boundary.
- After fixing Task 5 review findings, targeted `RawLocalReceiverHandlerTests`
  passed 10 tests, and the combined handler/integration run passed 11 tests.
  Both runs emitted the same existing `NU1903` and `NETSDK1057` messages.
- A combined targeted run for Task 2-5 receiver tests
  (`RawLocalReceiverOptionsTests`, `RawOtlpIngestorTests`,
  `OtlpProtobufTraceConverterTests`, `RawLocalReceiverHandlerTests`, and
  `RawLocalReceiverIntegrationTests`) passed 36 tests and emitted the same
  existing `NU1903` and `NETSDK1057` messages.
- During Task 6 RED test authoring, CLI/profile tests failed because
  `raw-local-receiver` profile output still used the Sprint6 reserved-error
  path, `ConfigSamples` did not handle the profile, and
  `serve-raw-local-receiver` was still an unknown command.
- The first Task 6 GREEN run passed 79 CLI/profile tests but emitted a new
  `CS8602` nullable warning in `CliApplication.RunProfileCommand`. The warning
  was fixed before continuing.
- After fixing the Task 6 nullable warning, the targeted CLI/profile run passed
  79 tests and emitted only the existing `NU1903` and `NETSDK1057` messages.
- Task 6 Sub-Agent review attempts failed because the environment hit the
  current usage limit. Main chat continued with local spec/code review instead.
- A combined targeted run for Task 2-6 receiver and profile tests
  (`RawLocalReceiverOptionsTests`, `RawOtlpIngestorTests`,
  `OtlpProtobufTraceConverterTests`, `RawLocalReceiverHandlerTests`,
  `RawLocalReceiverIntegrationTests`, `CliApplicationTests`, and
  `ConfigSamplesTests`) passed 115 tests and emitted the same existing
  `NU1903` and `NETSDK1057` messages.

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

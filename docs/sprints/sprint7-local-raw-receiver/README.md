# Sprint7: Local Raw Receiver

Sprint7 implements the Langfuse-free profile where this repository receives
telemetry directly from VS Code / Copilot clients and writes raw telemetry for
the raw data loop.

## Decision

`raw-local-receiver` is a required collection profile, but it is split from
Sprint6 because it introduces a long-running local receiver / agent surface.

This Sprint supersedes the previous current-scope rejection of a custom OTLP
HTTP receiver and long-running local telemetry agent for this specific profile.

## Scope

- Implement a repository-hosted local OTLP HTTP receiver for raw telemetry.
- Support VS Code GitHub Copilot Chat sending directly to the local receiver.
- Persist received telemetry into the existing SQLite raw store or a raw OTLP
  file path that can be ingested deterministically.
- Keep normalized measurement, candidate, and dashboard schemas unchanged.
- Provide a local run mode that does not require installing an exe.
- Evaluate IIS / IIS Express hosting as an optional run mode if it is practical
  for company-managed Windows PCs.

## Non-goals

- Do not build a tray application in the initial Sprint7 implementation.
- Do not require installing a packaged exe for the initial required path.
- Do not implement Windows Service installation unless explicitly decided later.
- Do not implement shared remote consent workflow.
- Do not replace Langfuse as the standard full live trace viewer profile.

## Safety Boundary

The local receiver may receive raw prompt, response, tool arguments, tool
results, source paths, identity attributes, and credential-like strings.

Therefore:

- Raw receiver output must be local runtime data.
- Raw receiver output must not be committed.
- Repository-safe outputs must continue to use normalized / sanitized datasets.
- The receiver must bind only to local development endpoints unless a later
  security decision allows a broader listener.
- The receiver must not expose raw telemetry through dashboard output.

## Milestones

| Milestone | Scope |
| --- | --- |
| M1 receiver requirements and safety boundary | Define receiver input, output, local bind, data safety, and validation evidence. |
| M2 receiver host model | Choose initial host model: repository `dotnet run` path first, IIS / IIS Express only if practical. |
| M3 local OTLP HTTP receiver | Implement receiver endpoint and raw persistence path. |
| M4 raw store integration | Connect receiver output to SQLite raw store / `normalize-raw` without schema changes. |
| M5 VS Code direct telemetry validation | Validate VS Code Copilot Chat sends telemetry directly to the repository receiver without Langfuse. |
| M6 review and release boundary | Review safety, validation, docs, and remaining packaging options. |

## Current Status

Sprint7 has implemented the initial repository-local foreground receiver path
for M2-M4 and synthetic validation for M3-M4.

Implemented:

- `serve-raw-local-receiver` foreground Config CLI command.
- Loopback-only receiver URL validation with default
  `http://127.0.0.1:4319`.
- `/v1/traces` handling for OTLP HTTP JSON and protobuf trace payloads.
- SQLite raw store persistence using the existing raw store schema.
- `raw-local-receiver` profile output that points to the local receiver and
  does not emit Langfuse credentials, Collector headers, or remote endpoints.

Validation on 2026-06-21:

- `dotnet build CopilotAgentObservability.slnx` passed with 0 errors and
  existing package vulnerability warnings.
- `dotnet test CopilotAgentObservability.slnx` passed with 290 tests,
  0 failures, and 0 skipped tests.
- Synthetic smoke started the receiver on `http://127.0.0.1:54320`, posted one
  JSON trace and one protobuf trace, and normalized 2 raw measurement rows from
  the temp SQLite raw store.

Still unconfirmed:

- Live VS Code GitHub Copilot Chat telemetry against the receiver.
- VS Code and GitHub Copilot extension version evidence for M5.
- Packaging / always-on host options beyond the repository-local foreground
  command.

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

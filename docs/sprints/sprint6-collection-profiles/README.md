# Sprint6: Collection Profiles

Sprint6 introduces collection profiles as a public interface so users can switch
between supported telemetry routing modes without changing downstream datasets.

## Decision

All collection profiles are required product support targets.

Sprint6 implements profile selection and the routing profiles that use existing
collection paths:

- `raw-only`
- `docker-desktop-langfuse`
- `docker-desktop-collector-langfuse`
- `wsl2-docker-langfuse`
- `wsl2-docker-collector-langfuse`
- `remote-managed-langfuse`
- `remote-managed-collector`

`raw-local-receiver` is a required support target, but it is implemented in
Sprint7 because it adds a repository-hosted OTLP receiver / local agent surface.

## Public Interface

The profile selector is the environment variable:

```text
CAO_COLLECTION_PROFILE
```

Config CLI must generate profile-aware settings and environment samples.
Existing explicit commands such as `langfuse-vscode-env` and
`collector-vscode-env` remain available during Sprint6.

## Scope

- Define collection profile names and required behavior.
- Add profile-aware Config CLI output.
- Keep raw store, measurement, candidate, and dashboard schemas unchanged.
- Treat `raw-only` as the minimum required profile.
- Treat Docker Desktop + Langfuse as the standard full profile.
- Add Docker Desktop + Collector + Langfuse as a supported required profile.
- Add WSL2 Docker Engine profiles as required profiles with live validation
  evidence for Windows-to-WSL endpoint behavior.
- Add remote managed endpoint profiles with repository README warning only.

## Non-goals

- Do not implement the repository-hosted receiver in Sprint6.
- Do not add tray app, installer, Windows Service, IIS deployment, or local
  background agent in Sprint6.
- Do not implement consent workflow for remote / shared profiles.
- Do not change normalized measurement, candidate, dashboard, or raw store
  output contracts.

## Milestones

| Milestone | Scope |
| --- | --- |
| M1 collection profile requirements and interface | Define profile values, support target, environment variable, and source-of-truth docs. |
| M2 Config CLI profile output | Add profile-aware configuration commands / options while preserving existing explicit commands. |
| M3 Docker Desktop profiles | Validate local Langfuse and Collector profile output and Docker Desktop live path. |
| M4 WSL2 Docker Engine profiles | Validate Windows client to WSL2 Docker Engine endpoint behavior and document required host / port handling. |
| M5 remote managed endpoint warning | Add README / user guide warning and placeholder-only config behavior for remote managed endpoints. |
| M6 review and handoff | Verify build, tests, profile docs, and hand off `raw-local-receiver` to Sprint7. |

## Handoff To Sprint7

Sprint6 must not pretend that `raw-local-receiver` is implemented.
It only reserves the profile name and documents why the repository-hosted
receiver is split into a separate Sprint.

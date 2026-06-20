# Telemetry Ingestion Specification

## Scope

Telemetry ingestion covers OTel configuration samples and accepted client sources.
It does not define raw store schema, candidate generation, or dashboard rendering.

## Supported Sources

Required:

- VS Code GitHub Copilot Chat。
- GitHub Copilot CLI。
- OpenTelemetry Collector as relay。

Optional:

- Codex App / app-server。

Reference-only:

- Claude Code examples。
- Visual Studio client family。

## Collection Profiles

Collection profile selection is a public interface.
The profile selector is:

```text
CAO_COLLECTION_PROFILE
```

The required profile values are defined in
[../interfaces/collection-profiles.md](../interfaces/collection-profiles.md).

Telemetry ingestion must support:

- `raw-only`
- `docker-desktop-langfuse`
- `docker-desktop-collector-langfuse`
- `wsl2-docker-langfuse`
- `wsl2-docker-collector-langfuse`
- `remote-managed-langfuse`
- `remote-managed-collector`
- `raw-local-receiver`

`raw-only` is the minimum profile and does not require a live receiver.
`docker-desktop-langfuse` is the standard full profile.
`raw-local-receiver` is implemented as a local receiver profile and is split
from other routing profiles because it introduces a long-running local process.

## Langfuse Direct Path

Default direct endpoint:

```text
http://localhost:3000/api/public/otel
```

Trace-specific endpoint:

```text
http://localhost:3000/api/public/otel/v1/traces
```

Langfuse requires Basic Auth.
Credentials are passed through local environment variables or user-level config, never repository files.

## Collector Relay Path

Collector relay is required for profiles that include `collector`.

Default local receiver:

```text
http://localhost:4318
localhost:4317
```

Collector may attach Langfuse authorization headers so clients do not store Langfuse credentials.
The repository example handles trace pipeline only.
Masking, sampling, TLS, SSO, and shared operation require separate product / security decisions.

## WSL2 Docker Engine Path

For `wsl2-docker-langfuse` and `wsl2-docker-collector-langfuse`, Docker Engine
runs inside WSL2 while VS Code, GitHub Copilot CLI, or Codex App runs on
Windows. The client endpoint must therefore be reachable from Windows, not only
from inside the WSL2 distro.

Generated samples use:

```text
http://<windows-reachable-wsl2-host>:3000/api/public/otel
http://<windows-reachable-wsl2-host>:4318
```

Use `localhost` when WSL2 localhost forwarding exposes published container
ports to Windows. If forwarding is unavailable, resolve the current WSL2 distro
address during live validation and keep that machine-specific value out of
repository files.

## Raw Local Receiver Path

The `raw-local-receiver` profile sends telemetry directly to a repository-hosted
local receiver instead of Langfuse.

Initial receiver requirements:

- bind to a local endpoint unless a later security decision allows broader exposure.
- accept OTLP HTTP telemetry from supported clients.
- persist raw telemetry as local runtime data for the raw data loop.
- avoid changing normalized measurement, candidate, or dashboard dataset contracts.
- avoid committing raw receiver output.

The local receiver is not implemented through Aspire AppHost by default.

## Resource Attributes

Required:

```text
user.id
user.email
team.id
department
client.kind
experiment.id
```

Recommended `client.kind` values:

```text
vscode-copilot-chat
copilot-cli
codex-app
```

Recommended:

```text
repo.name
workspace.name
task.id
task.category
task.run_index
experiment.condition
prompt.version
repo.snapshot
agent.variant
skill.version
mcp.profile
cli.wrapper.version
```

## Codex App Boundary

Codex App / app-server OTel routing config belongs in user-level `~/.codex/config.toml`.
Project-local `.codex/config.toml` is not a routing source of truth.

## Aspire AppHost Boundary

The Aspire AppHost is retained for build coverage and historical local dashboard connectivity context.
Do not add Langfuse, Collector, Config CLI, raw local receiver, ServiceDefaults, Web app, DB, Redis, or Worker resources to AppHost by default.
If resources are added later, define MCP exposure and sensitive telemetry exclusions before implementation.

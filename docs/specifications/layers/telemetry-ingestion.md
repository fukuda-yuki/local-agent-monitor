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
`raw-local-receiver` is a required support target to be implemented in Sprint7
and is split from other routing profiles because it introduces a long-running
local process.

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

Initial host model:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- serve-raw-local-receiver --db data\raw-store.db --url http://127.0.0.1:4319
```

The initial required path is a repository-local foreground `dotnet run`
process. IIS, IIS Express, packaged exe, tray app, and Windows Service hosting
are not part of the initial required path.

Initial receiver requirements:

- bind to loopback-only local development endpoints unless a later security
  decision allows broader exposure.
- accept OTLP HTTP telemetry from supported clients through the standard OTLP
  HTTP signal paths, including `/v1/traces`.
- accept OTLP HTTP protobuf trace payloads on `/v1/traces` because VS Code
  Copilot Chat uses HTTP/protobuf for `otlp-http` unless configured for gRPC.
- JSON OTLP trace payloads may be accepted for synthetic local validation, but
  JSON support does not replace the protobuf requirement for VS Code direct
  validation.
- accept trace telemetry as the first required signal; metrics and event-like
  telemetry may be accepted when supported by the receiver implementation, but
  unsupported signals must fail clearly and must not be treated as successful
  ingestion.
- persist raw telemetry as local runtime data for the raw data loop.
- write either to the SQLite raw store or to a raw OTLP file that can be passed
  to `ingest-raw`.
- avoid changing normalized measurement, candidate, or dashboard dataset
  contracts.
- avoid committing raw receiver output.

The local receiver is not implemented through Aspire AppHost by default.

Initial HTTP behavior:

- `POST /v1/traces` returns success only after a raw telemetry record is
  persisted.
- methods other than `POST` fail with a deterministic method error.
- `/v1/metrics`, `/v1/logs`, and other unsupported paths fail clearly and must
  not write raw records.
- invalid payloads fail clearly and must not write raw records.
- unsupported content types fail clearly and must not write raw records.

Generated `raw-local-receiver` configuration must point clients at the local
receiver endpoint and must not include Langfuse credentials, Collector headers,
remote endpoints, or repository-stored secrets.

Live validation for this profile must record:

- date and environment.
- receiver command and local bind address.
- collection profile value.
- client kind.
- non-secret endpoint shape.
- raw store path or raw OTLP file path, recorded as local runtime output.
- trace id or raw record identifier.
- confirmation that Langfuse was not required.
- confirmed and unconfirmed telemetry signals.

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

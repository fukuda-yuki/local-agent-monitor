# Collection Profiles Interface

Collection profiles are public routing modes for Copilot Agent Observability.
They define how telemetry reaches the product without changing the raw store,
normalized measurement, candidate, or dashboard dataset contracts.

## Environment Variable

The public profile selector is:

```text
CAO_COLLECTION_PROFILE
```

Supported values:

| Value | Purpose |
| --- | --- |
| `raw-only` | Minimum required profile. Uses saved raw OTLP JSON and the raw data loop without Langfuse or a live receiver. |
| `docker-desktop-langfuse` | Standard full profile. Sends OTLP HTTP to local Langfuse running through Docker Desktop. |
| `docker-desktop-collector-langfuse` | Sends OTLP HTTP to local OpenTelemetry Collector running through Docker Desktop, then relays to Langfuse. |
| `wsl2-docker-langfuse` | Sends Windows client telemetry to Langfuse running on Docker Engine inside WSL2. |
| `wsl2-docker-collector-langfuse` | Sends Windows client telemetry to a Collector running on Docker Engine inside WSL2, then relays to Langfuse. |
| `remote-managed-langfuse` | Sends telemetry to a managed Langfuse endpoint. |
| `remote-managed-collector` | Sends telemetry to a managed Collector endpoint. |
| `raw-local-receiver` | Sends telemetry directly to a repository-hosted local receiver that writes raw telemetry for the raw data loop. |

All profiles are product support targets. Sprint6 implements profile selection
and the non-receiver routing profiles. Sprint7 implements `raw-local-receiver`.

## Required Behavior

- Profile selection must be explicit in generated configuration output.
- Commands must preserve existing explicit `langfuse-*` and `collector-*` entry points until a later compatibility decision removes them.
- Generated settings must use placeholders for credentials.
- Generated settings may use documented local defaults for Docker Desktop
  profiles. Environment-specific non-local endpoints must use placeholders.
- Generated settings must not store Langfuse keys, Basic Auth headers, API keys, or secrets in repository files.
- `raw-only` must work without Langfuse, Docker Desktop, WSL2 Docker Engine, Collector, remote endpoints, or a background process.
- Profile selection must not alter normalized measurement schema, candidate record schema, dashboard dataset schema, or repository-safe data boundaries.

## WSL2 Docker Engine Endpoint Behavior

`wsl2-docker-langfuse` and `wsl2-docker-collector-langfuse` are Windows client
profiles. Langfuse or Collector runs in containers through Docker Engine inside
the selected WSL2 distro, but the Copilot client runs on Windows.

Generated configuration must use a placeholder Windows-reachable host:

```text
<windows-reachable-wsl2-host>
```

Operators should use `localhost` when WSL2 localhost forwarding exposes the
published container ports to Windows. If that forwarding is unavailable, the
operator may resolve the current WSL2 distro address during live validation.
Machine-specific WSL2 IP addresses must not be committed to repository files.

## Remote And Shared Endpoint Warning

`remote-managed-langfuse` and `remote-managed-collector` are supported routing
profiles only after the operator has separately handled access control,
retention, deletion process, masking / redaction, user notice or consent,
identity handling, and credential handling.

This repository documents the warning and emits configuration placeholders.
It does not implement the user consent workflow.

## Validation

Automated validation must cover generated configuration for each profile using
synthetic values only.

Live validation is required for environment-dependent profiles:

- Docker Desktop profiles must record Docker Desktop, endpoint, profile value,
  client kind, and trace or raw record evidence.
- WSL2 Docker Engine profiles must record Windows-to-WSL endpoint behavior,
  port binding, profile value, client kind, and trace or raw record evidence.
- Remote managed profiles must record only non-secret endpoint shape, profile
  value, client kind, access-control confirmation, and trace or raw record
  evidence.
- `raw-local-receiver` validation belongs to the Raw Local Receiver Path in
  [../layers/telemetry-ingestion.md](../layers/telemetry-ingestion.md) and must
  prove that VS Code can send telemetry directly to the repository receiver
  without Langfuse.

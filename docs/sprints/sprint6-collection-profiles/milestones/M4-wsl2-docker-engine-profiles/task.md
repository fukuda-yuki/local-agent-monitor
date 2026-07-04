# M4: WSL2 Docker Engine Profiles

## Goal

Support and validate profiles where Docker Engine runs inside WSL2 instead of
Docker Desktop.

## Scope

- `wsl2-docker-langfuse`
- `wsl2-docker-collector-langfuse`

## Requirements

- Document Windows client to WSL2 endpoint behavior.
- Avoid hard-coding host IP addresses that vary by machine.
- Keep credentials outside repository files.
- Keep downstream raw store and normalized dataset behavior unchanged.

## Verification

- Unit tests verify profile selection and placeholder output.
- Live validation records Windows version, WSL distro, Docker Engine evidence,
  endpoint shape, profile value, client kind, and trace or raw record evidence.

## Validation Notes

2026-06-21 M4 start updated WSL2 profile output and documentation:

- Generated WSL2 profile samples now use `<windows-reachable-wsl2-host>`
  instead of a machine-specific host IP placeholder.
- Generated WSL2 profile samples explain that `localhost` should be used when
  WSL2 localhost forwarding exposes published container ports to Windows.
- Generated WSL2 profile samples state that machine-specific WSL2 IP addresses
  must stay out of repository files.
- `docs/specifications/interfaces/collection-profiles.md`,
  `docs/specifications/layers/telemetry-ingestion.md`, and
  `docs/user-guide/telemetry-collection.md` document Windows client to WSL2
  endpoint behavior.

Automated validation:

- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~ConfigSamplesTests|FullyQualifiedName~CliApplicationTests"` succeeded with 65 passed tests.
- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 244 passed tests.

Environment diagnostics:

- `wsl.exe --list --verbose` showed `Ubuntu` running as WSL version 2.
- `wsl.exe -d Ubuntu -- sh -lc "hostname -I && ip -4 addr show eth0 2>/dev/null"` showed a current WSL2 address for local diagnostics only.
- `docker version --format '{{json .}}'` from Windows reported Docker Desktop 4.71.0.
- `wsl.exe -d Ubuntu -- docker version --format '{{json .}}'` also reported Docker Desktop 4.71.0 as the server platform.

2026-06-21 follow-up live validation installed and used an independent Docker
Engine inside the Ubuntu WSL2 distro because the initial Ubuntu `docker` CLI and
socket were Docker Desktop integration surfaces.

- Removed the Docker Desktop `/usr/bin/docker` symlink inside Ubuntu and
  installed Ubuntu `docker.io`.
- Started `docker.service` through WSL systemd.
- Stopped the Docker Desktop Langfuse and Collector containers without removing
  their volumes so WSL2 Engine could bind the required validation ports.
- Ran Langfuse self-host from `tmp/langfuse` on the WSL2 Docker Engine with a
  local validation override outside tracked repository files.
- Ran the Collector on the WSL2 Docker Engine with `LANGFUSE_AUTH` supplied
  from the local `tmp/langfuse/.env` only. No credential value was recorded.

Live validation environment:

- Windows version: Windows 10 Pro, WindowsVersion 2009, OS build 26200,
  hardware abstraction layer 10.0.26100.1.
- WSL distro: `Ubuntu`, WSL version 2.
- WSL2 Docker Engine evidence:
  `client=29.1.3 server=29.1.3 os=linux kernel=6.6.87.2-microsoft-standard-WSL2`.
- Docker Engine details:
  `operating_system=Ubuntu 24.04.4 LTS docker_root=/var/lib/docker cgroup_driver=systemd`.
- Windows-reachable WSL2 endpoint host used for validation:
  `<redacted-wsl2-validation-host>`.
- Port binding evidence: WSL2 Docker Engine published Langfuse on host port
  `3000/tcp` and Collector OTLP HTTP on host port `4318/tcp`; the
  machine-specific WSL2 host address was redacted from repository files.
- Langfuse health from Windows:
  `GET http://<redacted-wsl2-validation-host>:3000/api/public/health` returned
  `200 {"status":"OK","version":"3.194.0"}`.

`wsl2-docker-langfuse` validation:

- Profile value: `wsl2-docker-langfuse`.
- Client kind: `vscode-copilot-chat`.
- Endpoint: `http://<redacted-wsl2-validation-host>:3000/api/public/otel/v1/traces`.
- Windows PowerShell POST of synthetic OTLP JSON returned HTTP 200.
- Langfuse public API lookup
  `GET http://<redacted-wsl2-validation-host>:3000/api/public/traces/66666666666666666666666666666666`
  returned HTTP 200 with returned trace id
  `66666666666666666666666666666666`.

`wsl2-docker-collector-langfuse` validation:

- Profile value: `wsl2-docker-collector-langfuse`.
- Client kind: `copilot-cli`.
- Collector endpoint: `http://<redacted-wsl2-validation-host>:4318/v1/traces`.
- Windows PowerShell POST of synthetic OTLP JSON returned
  `200 {"partialSuccess":{}}`.
- Langfuse public API lookup
  `GET http://<redacted-wsl2-validation-host>:3000/api/public/traces/88888888888888888888888888888888`
  returned HTTP 200 with returned trace id
  `88888888888888888888888888888888`.

Unverified or residual risk:

- The validation host is a machine-specific WSL2 address and is redacted from
  tracked repository files. It is not used in tracked configuration output.
- WSL2 localhost forwarding was not the successful path on this machine because
  Docker Desktop was already using the localhost validation ports.

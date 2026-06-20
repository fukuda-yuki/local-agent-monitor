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

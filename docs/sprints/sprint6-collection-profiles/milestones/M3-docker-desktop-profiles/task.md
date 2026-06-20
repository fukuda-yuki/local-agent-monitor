# M3: Docker Desktop Profiles

## Goal

Support and validate Docker Desktop based profiles.

## Scope

- `docker-desktop-langfuse`
- `docker-desktop-collector-langfuse`

## Requirements

- Langfuse direct profile targets local Langfuse OTLP HTTP.
- Collector profile targets local Collector OTLP HTTP and lets Collector attach
  Langfuse authorization.
- Collector example validation uses dummy credentials only.

## Verification

- Unit tests verify generated endpoints and placeholder credentials.
- Collector example passes:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

- Live validation records date, profile value, client kind, endpoint, and trace
  or raw record evidence.

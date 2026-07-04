# M3 Review

## Result

Accepted after collaborative live validation.

## Scope Reviewed

- `docker-desktop-langfuse` Config CLI profile output.
- `docker-desktop-collector-langfuse` Config CLI profile output.
- Docker Desktop Langfuse endpoint reachability.
- Docker Desktop Collector startup and OTLP HTTP receiver behavior.
- Secret handling for local validation.
- End-to-end Langfuse ingestion evidence for both Docker Desktop profiles.

## Validation

- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~ConfigSamplesTests|FullyQualifiedName~CliApplicationTests"` succeeded with 61 passed tests.
- `dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile docker-desktop-langfuse` emitted `$env:CAO_COLLECTION_PROFILE="docker-desktop-langfuse"` and `http://localhost:3000/api/public/otel`.
- `dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile docker-desktop-collector-langfuse` emitted `$env:CAO_COLLECTION_PROFILE="docker-desktop-collector-langfuse"` and `http://localhost:4318` without client-side Langfuse authorization headers.
- `$env:LANGFUSE_AUTH="dummy"; docker compose -f infra\otel-collector\docker-compose.example.yml config` succeeded.
- `$env:LANGFUSE_AUTH="dummy"; docker compose -f infra\otel-collector\docker-compose.example.yml up -d` started `otel-collector-otel-collector-1`.
- `Test-NetConnection localhost -Port 3000` succeeded.
- `Test-NetConnection localhost -Port 4318` succeeded.
- Posting synthetic OTLP JSON to `http://localhost:4318/v1/traces` returned `200 {"partialSuccess":{}}`.
- Direct POST to `http://localhost:3000/api/public/otel/v1/traces` with dummy auth returned `401 Unauthorized`.
- Collector logs showed export to `http://host.docker.internal:3000/api/public/otel/v1/traces` failed with `401 Unauthorized`, as expected for dummy auth.
- User supplied real local Langfuse project credentials in their own PowerShell session only; no credential values were recorded in the repository.
- User confirmed Langfuse project `local-validation` showed direct trace id `33333333333333333333333333333333`.
- User confirmed Langfuse project `local-validation` showed Collector relay trace id `55555555555555555555555555555555`.
- Direct trace evidence: profile value `docker-desktop-langfuse`, client kind
  `vscode-copilot-chat`, endpoint
  `http://localhost:3000/api/public/otel/v1/traces`, Docker Desktop local
  Langfuse, trace id `33333333333333333333333333333333`.
- Collector relay trace evidence: profile value
  `docker-desktop-collector-langfuse`, client kind `copilot-cli`, endpoint
  `http://localhost:4318/v1/traces`, Docker Desktop Collector relay to local
  Langfuse, trace id `55555555555555555555555555555555`.
- User reported no unverified items or errors for the final live validation.

## Findings

- No blocking issue was found in generated profile endpoints or placeholder handling.
- The Collector route receives OTLP HTTP locally and attempts to relay to Langfuse.
- Direct Langfuse and Collector relay paths were both confirmed end to end by trace id.

## Residual Risk

- Live validation used a user-managed local Langfuse project and local
  PowerShell session. The repository records only non-secret evidence.

# M6 Review

## Result

Accepted for Sprint6 non-receiver collection profile code and documentation.
Remote managed live validation remains unverified because it requires external
access-control, consent / notice, retention, deletion, masking, identity, and
credential decisions before telemetry is sent.

## Scope Reviewed

- Collection profile public interface and profile selector.
- Config CLI profile-aware output for VS Code, GitHub Copilot CLI, and Codex App.
- Docker Desktop profile validation evidence from M3.
- WSL2 Docker Engine profile validation evidence from M4.
- Remote managed endpoint warning and placeholder-only output from M5.
- Handoff boundary for `raw-local-receiver`.

## Validation

- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ConfigSamplesTests.CreateProfile` first failed with 4 expected failures because remote managed profile output did not state that this repository does not implement a remote or shared endpoint user consent workflow.
- After the warning update, `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ConfigSamplesTests.CreateProfile` succeeded with 29 passed tests.
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~ConfigSamplesTests|FullyQualifiedName~CliApplicationTests"` succeeded with 69 passed tests.
- `dotnet build CopilotAgentObservability.slnx` succeeded with 0 warnings and 0 errors.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 248 passed tests.

## Findings

- No blocking issue was found in the repository-local code and documentation
  after the M5 remote warning update.
- WSL2 generated output uses `<windows-reachable-wsl2-host>` and documents the localhost-forwarding preference and machine-specific IP exclusion.
- Remote managed profile output uses endpoint and credential placeholders and now repeats the repository boundary that user consent workflow is not implemented here.
- `raw-local-receiver` remains listed as a required support profile but profile commands return an error that it is reserved for Sprint7.

## Residual Risk

- Remote managed profiles were not live validated because they require external access-control, retention, deletion, masking / redaction, user notice or consent, identity, and credential decisions outside this repository.
- WSL2 live validation depends on local machine networking. M4 records the machine-specific host as evidence only, not as committed configuration.
- Collector compose validation was not rerun in M6 because Collector files did not change after M3.

## Sub-Agent Review Follow-up

2026-06-21 parallel sub-agent review found no Critical issues. Valid findings
were addressed:

- Clarified that `raw-local-receiver` is a Sprint7 implementation target in the
  telemetry ingestion specification.
- Made `raw-only` profile PowerShell output clear stale live telemetry
  environment variables, and added Codex App raw-only guidance to remove active
  user-level OTel routing.
- Clarified Docker Desktop local endpoint defaults versus placeholders for
  non-local environment-specific endpoints.
- Replaced the committed machine-specific WSL2 validation host with a redacted
  marker and added non-secret port binding evidence.
- Added Docker Desktop live trace evidence fields tying trace ids to profile
  value, client kind, and endpoint.
- Aligned remote managed endpoint safety preconditions across current
  requirements and security docs.
- Updated user-facing Docker Desktop examples to use profile-aware commands.

Validation after follow-up:

- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ConfigSamplesTests` first failed with 2 expected failures for missing raw-only cleanup and Codex App raw-only routing guidance.
- After the fix, the same `ConfigSamplesTests` filter succeeded with 45 passed tests.
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~ConfigSamplesTests|FullyQualifiedName~CliApplicationTests"` succeeded with 73 passed tests.
- `dotnet build CopilotAgentObservability.slnx` succeeded with 0 warnings and 0 errors.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 252 passed tests.

## Handoff To Sprint7

Sprint7 starts from the reserved `raw-local-receiver` profile. It must implement the repository-hosted local receiver, raw store integration, and VS Code direct telemetry validation without changing raw store, normalized measurement, candidate, or dashboard dataset schemas.

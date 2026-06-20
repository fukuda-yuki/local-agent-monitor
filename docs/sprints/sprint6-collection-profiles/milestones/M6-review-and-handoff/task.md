# M6: Review and Handoff

## Goal

Review Sprint6 implementation and hand off local receiver work to Sprint7.

## Reviewed Scope

- Collection profile public interface.
- Config CLI profile output.
- Docker Desktop profile validation.
- WSL2 Docker Engine profile validation.
- Remote managed endpoint warning.

## Verification

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`
- Collector example validation when Collector files change.

## Handoff

Sprint7 starts from the reserved `raw-local-receiver` profile and implements
the repository-hosted telemetry receiver. Sprint6 must record any endpoint,
credential, or validation gaps found while implementing the non-receiver
profiles.

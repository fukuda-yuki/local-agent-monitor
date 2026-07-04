# M3: Local OTLP HTTP Receiver

## Goal

Implement the local OTLP HTTP receiver for `raw-local-receiver`.

## Scope

- Receive OTLP HTTP telemetry from local clients.
- Bind only to loopback local development endpoints unless a later security
  decision allows broader exposure.
- Accept at least trace payloads on the standard OTLP HTTP `/v1/traces` path
  needed for VS Code Copilot Chat validation.
- Return deterministic errors for unsupported payloads.
- Avoid storing secrets in repository files.

## Verification

- Unit tests cover accepted synthetic OTLP payloads.
- Unit tests cover invalid payload handling.
- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`

## Evidence 2026-06-21

Implemented in `CopilotAgentObservability.ConfigCli`:

- `serve-raw-local-receiver` starts the foreground local receiver.
- The receiver accepts `POST /v1/traces` with `application/json` and
  `application/x-protobuf`.
- Unsupported methods, unsupported signal paths, unsupported content types,
  malformed payloads, empty trace payloads, and persistence failures return
  deterministic non-success responses without writing raw records.

Validation:

- `RawLocalReceiverHandlerTests`, `OtlpProtobufTraceConverterTests`, and
  related receiver tests cover accepted JSON/protobuf payloads and invalid
  payload handling.
- `dotnet build CopilotAgentObservability.slnx` passed with 0 errors and
  existing package vulnerability warnings.
- `dotnet test CopilotAgentObservability.slnx` passed with 290 tests,
  0 failures, and 0 skipped tests.
- Synthetic smoke started the receiver on `http://127.0.0.1:54320`, posted
  JSON and protobuf traces to `/v1/traces`, and received HTTP 200 for both.

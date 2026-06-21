# M5: VS Code Direct Telemetry Validation

## Goal

Validate that VS Code GitHub Copilot Chat can send telemetry directly to the
repository-hosted receiver without Langfuse.

## Scope

- Configure VS Code for `raw-local-receiver`.
- Run a synthetic or approved test workflow.
- Confirm receiver raw output.
- Confirm `normalize-raw` can produce measurement output from the received data.

## Verification Evidence

- Date and machine environment.
- `CAO_COLLECTION_PROFILE=raw-local-receiver`.
- VS Code / extension version where available.
- Receiver command and local bind address.
- Non-secret receiver endpoint shape.
- Client kind.
- Raw store path or raw OTLP file path, recorded as local runtime output.
- Trace id or raw record identifier.
- Confirmation that Langfuse was not required.
- Confirmed items and unconfirmed items.
- Confirmed and unconfirmed telemetry signals.

## Validation Procedure

1. Start the repository-local receiver:

   ```powershell
   dotnet run --project src\CopilotAgentObservability.ConfigCli -- serve-raw-local-receiver --db data\raw-store.db --url http://127.0.0.1:4319
   ```

2. Generate VS Code environment variables:

   ```powershell
   dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver
   ```

3. Apply the generated environment to the VS Code process used for validation.
4. Run an approved Copilot Chat workflow.
5. Stop the receiver.
6. Normalize the raw store:

   ```powershell
   dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --json <local-measurements.json>
   ```

7. Record evidence without committing raw store output or raw telemetry.

## Synthetic Evidence 2026-06-21

Confirmed without VS Code:

- `profile-vscode-env --profile raw-local-receiver` emits
  `CAO_COLLECTION_PROFILE=raw-local-receiver`.
- Generated profile output points to `http://127.0.0.1:4319`.
- Generated profile output sets `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`.
- Generated profile output includes `client.kind=vscode-copilot-chat` and
  `experiment.id=baseline`.
- Generated profile output does not include Langfuse credentials, Collector
  headers, `<langfuse-host>`, or `<collector-host>`.
- The local receiver accepted synthetic JSON and protobuf trace payloads and
  wrote them to a temp SQLite raw store.
- `normalize-raw` produced 2 synthetic measurement rows from that temp raw
  store.

Unconfirmed live items:

- VS Code version.
- GitHub Copilot Chat extension version.
- Live VS Code Copilot Chat telemetry reaching the receiver.
- Live trace id or raw record id from an approved VS Code workflow.
- Whether VS Code emits additional signals beyond traces in this environment.

Confirmed telemetry signals:

- Synthetic trace payloads over OTLP HTTP JSON and protobuf.

Unconfirmed telemetry signals:

- Live VS Code trace payloads.
- Live VS Code logs, metrics, or event-like telemetry, if any.

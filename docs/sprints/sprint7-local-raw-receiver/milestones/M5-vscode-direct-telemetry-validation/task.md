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
- Receiver endpoint.
- Trace id or raw record identifier.
- Confirmed items and unconfirmed items.

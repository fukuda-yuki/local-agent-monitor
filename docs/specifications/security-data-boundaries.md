# Security And Data Boundaries

## Repository-Allowed Data

Allowed:

- synthetic fixture。
- redacted summary。
- normalized aggregate dataset。
- sanitized dashboard dataset。
- trace id / candidate id / evidence ref。
- real-data-derived aggregate metrics。
- `user.id` / `user.email` only when access control permits it。

## Repository-Forbidden Data

Forbidden:

- raw prompt。
- raw response。
- full system prompt。
- full tool arguments / tool results。
- source code fragment / file contents from observed sessions。
- credential、secret、token、API key、password。
- Base64 authorization header。
- sensitive bundle content。
- sensitive bundle local path。

## Sensitive Bundle Boundary

Sensitive bundle output is local opt-in output.
It may contain raw evidence needed for diagnosis, but it must not be committed.
Repository-safe records may reference only:

- sanitized `evidence_ref`。
- `sensitive_bundle_present=true`。
- non-sensitive summary。

## Raw Local Receiver Boundary

The `raw-local-receiver` profile receives raw telemetry directly from local
clients.

Raw receiver input may contain:

- raw prompt。
- raw response。
- system prompt content。
- tool arguments / tool results。
- source paths or source fragments。
- identity-bearing attributes。
- credential-like strings。

Raw receiver output is local runtime data and must not be committed.
Repository-safe outputs must continue to use normalized / sanitized datasets.
The receiver must bind only to local development endpoints unless a later
security decision allows broader exposure.

## Shared Use Preconditions

Before shared dashboard or real-data publishing:

- define access control。
- define retention。
- define deletion process。
- define masking / redaction。
- define user notice or consent。
- decide identity handling。
- validate Pages visibility。
- confirm snapshot growth monitoring。

Before using `remote-managed-langfuse` or `remote-managed-collector`, also
define user notice or consent and credential handling.
This repository documents the warning but does not implement the consent
workflow.

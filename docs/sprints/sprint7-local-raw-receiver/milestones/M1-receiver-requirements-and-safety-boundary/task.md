# M1: Receiver Requirements and Safety Boundary

## Goal

Define the `raw-local-receiver` input, output, local bind, data safety boundary,
and validation evidence before implementing the receiver.

## Scope

- Require a repository-hosted local OTLP HTTP receiver.
- Require loopback-only local development binding unless a later security
  decision allows broader exposure.
- Require standard OTLP HTTP signal paths, including `/v1/traces`.
- Treat trace telemetry as the first required signal.
- Require unsupported signals to fail clearly instead of being counted as
  successful ingestion.
- Allow receiver output to be either the existing SQLite raw store or a raw OTLP
  file that can be passed to `ingest-raw`.
- Keep normalized measurement, candidate, and dashboard dataset schemas
  unchanged.
- Keep receiver-created raw stores and raw OTLP files as local runtime data that
  must not be committed.
- Require validation evidence that proves VS Code can send telemetry directly
  to the repository receiver without Langfuse.

## Safety Boundary

The receiver may receive raw prompt, response, tool arguments, tool results,
source paths, identity attributes, and credential-like strings.

Repository-safe evidence must use trace ids, raw record identifiers, redacted
summaries, and non-secret endpoint shapes. It must not include raw payload
content, credentials, sensitive paths, or captured source content.

## Validation Evidence Required For Later Milestones

Live validation for `raw-local-receiver` must record:

- date and environment.
- receiver command and local bind address.
- collection profile value.
- client kind.
- non-secret endpoint shape.
- raw store path or raw OTLP file path, recorded as local runtime output.
- trace id or raw record identifier.
- confirmation that Langfuse was not required.
- confirmed and unconfirmed telemetry signals.

## Verification

- Current source-of-truth specifications define the same local receiver boundary
  as this milestone.
- The repository roadmap marks Sprint7 local receiver work as started.
- No implementation work starts before M1 is recorded in current specifications.

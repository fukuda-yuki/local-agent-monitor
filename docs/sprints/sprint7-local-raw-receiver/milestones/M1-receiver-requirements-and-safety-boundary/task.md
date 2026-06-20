# M1: Receiver Requirements and Safety Boundary

## Goal

Define the `raw-local-receiver` requirements before implementing a long-running
local receiver.

## Scope

- Define accepted OTLP HTTP signal paths.
- Define local bind address and port behavior.
- Define raw output path and retention expectations.
- Define how raw receiver output enters the existing raw data loop.
- Define safety rules for raw prompt, response, tool arguments, tool results,
  local paths, identity attributes, and credential-like values.

## Verification

- Requirements, spec, architecture, and security boundaries agree.
- No receiver code is added before the safety boundary is documented.

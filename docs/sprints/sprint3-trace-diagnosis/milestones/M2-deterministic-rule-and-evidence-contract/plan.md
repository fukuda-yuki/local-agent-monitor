# M2 Plan

## Scope

M2 is a blocking documentation milestone. It finalizes the deterministic rule and evidence contract that M3-M5 must implement, without changing CLI behavior.

The contract stays sprint-local until the command contracts are ready to be promoted into `docs/spec.md`.

## Work Items

1. Finalize the initial `rule_id` set for diagnosis candidate generation.
2. Finalize the initial `decision_rule_id` set for auto-decision generation.
3. Define exactly which normalized measurement fields, raw OTLP fields, span events, and content fragments content-aware rules may inspect.
4. Define deterministic literal, regex, and field-predicate patterns for error and sensitive-content detection.
5. Define the M24-M27 compatibility and adapter mapping contract.
6. Finalize sensitive bundle schema version 1, reverse lookup, TTL metadata, and manual deletion policy.

## Verification

M2 verification is documentation-focused:

- Markdown links point to existing sprint-local documents.
- `auto-approved` remains a Sprint3 output state and is not connected to repository modification.
- M1 placeholder wording is superseded by the M2 contract.
- No build or test run is required unless code changes are introduced.

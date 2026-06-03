# M24 Plan

## Scope

M24 implements a record-validation MVP for human-classified diagnosis records.
It does not infer failures from traces, generate improvement proposals, adopt proposals, modify repositories, or decide comparison winners.

## Implementation

- Add `validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]` to Config CLI.
- Accept JSON as either a top-level array or `{ "diagnoses": [...] }`.
- Accept CSV only when the header exactly matches the fixed diagnosis columns.
- Validate M23 taxonomy IDs, severity, improvement target, review status, task category, and client kind.
- Reject M17 `failure_type` as a diagnosis column.
- Reject unsafe `evidence_summary` values that appear to contain raw content, credentials, secrets, tokens, or user identity.

## Verification

- Add synthetic fixture and xUnit coverage for valid JSON, valid CSV, invalid enum values, blank evidence, unsafe evidence, and `failure_type` misuse.
- Run `dotnet build CopilotAgentObservability.slnx`.
- Run `dotnet test CopilotAgentObservability.slnx`.

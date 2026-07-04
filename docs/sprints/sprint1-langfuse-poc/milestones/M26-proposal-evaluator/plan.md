# M26 Plan

M26 implements a deterministic pre-review evaluator for M25 improvement proposal records.
It does not adopt proposals, implement improvements, modify repositories, generate patches or diffs, create commits or pull requests, decide comparison winners, or evaluate improvement effects.

## Implementation

- Add `evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]` to Config CLI.
- Accept M25 proposal CSV / JSON input, including top-level JSON arrays and `{ "proposals": [...] }`.
- Validate fixed proposal columns, required fields, allowed values, `human_review_status=needs-human-review`, and unsafe material.
- Emit one proposal evaluation record per input proposal.
- Set `proposal_evaluation_status` to `ready-for-human-approval`, `needs-revision`, or `blocked` using deterministic text and metadata checks only.

## Verification

- Add xUnit coverage for JSON object input, top-level array input, CSV input / output, all three statuses, empty input, unsafe material, invalid enum values, unknown columns, and missing output options.
- Run `dotnet build CopilotAgentObservability.slnx`.
- Run `dotnet test CopilotAgentObservability.slnx`.

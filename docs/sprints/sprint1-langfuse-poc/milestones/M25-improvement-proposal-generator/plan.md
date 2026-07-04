# M25 Plan

## Scope

M25 implements a deterministic improvement proposal generator for validated M24 diagnosis records.
It does not adopt proposals, implement improvements, modify repositories, generate patches or diffs, create commits or pull requests, decide comparison winners, or evaluate improvement effects.

## Implementation

- Add `generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]` to Config CLI.
- Reuse the M24 diagnosis input reader and validator before generating proposals.
- Generate one proposal per input diagnosis row where `review_status` is `accepted-for-proposal`.
- Preserve source diagnosis metadata and map `recommended_improvement_target` to `improvement_target`.
- Generate `proposal_title`, `proposal_summary`, `proposed_change`, and `acceptance_check` with deterministic templates only.
- Set `human_review_status` to `needs-human-review` for every generated proposal.

## Verification

- Add xUnit coverage for JSON object input, top-level array input, CSV input, accepted-only filtering, empty proposal output, unsafe evidence rejection, invalid taxonomy values, `failure_type` misuse, and missing output options.
- Run `dotnet build CopilotAgentObservability.slnx`.
- Run `dotnet test CopilotAgentObservability.slnx`.

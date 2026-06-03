# M24 Notes

## Decisions

- M24 is a validation and formatting MVP for human-authored diagnosis records.
- Diagnosis records use fixed columns from M23 handoff and are independent from M17 run exclusion `failure_type`.
- The CLI rejects suspicious evidence summaries instead of attempting masking or redaction.
- Live Langfuse access is not required; validation uses synthetic fixtures only.

## Out of Scope

- Automatic trace diagnosis.
- Improvement proposal generation.
- Proposal evaluation or adoption.
- Repository modification, patch generation, commit, push, or pull request creation.
- Automatic comparison winner selection.

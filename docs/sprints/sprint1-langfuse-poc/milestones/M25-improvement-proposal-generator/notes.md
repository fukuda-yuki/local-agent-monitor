# M25 Notes

- Proposal generation uses M24 diagnosis records as the only input source.
- `source_diagnosis_index` is the 1-based input row index, so reviewers can trace each proposal back to the validated diagnosis record.
- Proposal text is intentionally generic and human-review oriented. It must not contain patches, diffs, file edits, adoption decisions, or raw trace content.

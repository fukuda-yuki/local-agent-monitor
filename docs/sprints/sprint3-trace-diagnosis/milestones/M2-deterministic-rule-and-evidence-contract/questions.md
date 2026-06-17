# M2 Questions

## Resolved

| Question | Decision |
| --- | --- |
| Should M3 implement rules beyond the M1 inventory? | No. M3 starts with the five `DIAG-*` rules finalized in `rule-and-evidence-contract.md`. |
| Are the five M3 rules the final long-term rule set? | No. They are the Sprint3 initial implementation set. Additional rules such as duration, token volume, unknown spans, or repeated same-tool calls are future candidates, likely Sprint4 or later. |
| Should content-aware rules infer from raw content using an LLM? | No. They use only literal field predicates, regexes, and the Base64 decoding predicate defined in M2. |
| Should `prompt.version` be treated as sensitive because it contains `prompt`? | No. `prompt.version` is a safe version identifier. `prompt.content` and raw prompt fields are sensitive. |
| Should `auto-approved` trigger implementation? | No. It records a Sprint3 decision only and exits to M5 / M6 evidence or Sprint4 planning handoff. |
| Should auto-decision records become M27 human decision records? | No. Auto-decision records stay separate. M27 remains a human decision workflow. |
| How is `evidence_ref` preserved when mapping to M24? | The adapter appends sanitized `rule_id` and `evidence_ref` to M24 `evidence_summary`, and may write a non-content sidecar in M5. |
| Does the adapter need normalized measurements? | Yes. Candidate output intentionally does not carry measurement context, so the M24 adapter joins candidates back to measurements by `trace_id` and `source_record_ref`. |
| Should M5 implement an adapter command or document a manual mapping procedure first? | Implement an adapter command in M5, rather than leaving the connection as a manual mapping only. |

## Remaining

| Question | Handling |
| --- | --- |
| Which exact Sprint4 planning handoff document should receive `auto-approved` records? | Decide in Sprint4 planning or at Sprint3 closeout. |

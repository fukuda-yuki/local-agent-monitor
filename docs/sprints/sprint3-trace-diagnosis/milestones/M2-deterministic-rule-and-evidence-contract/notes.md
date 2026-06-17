# M2 Notes

## 2026-06-17: rule and evidence contract

- Finalized the five initial diagnosis `rule_id` values from M1.
- Confirmed the five M3 rules are the initial Sprint3 implementation set and that additional rules are future candidates, likely Sprint4 or later.
- Finalized the four initial auto-decision `decision_rule_id` values from M1.
- Defined content-aware rule read scope for normalized measurements, raw OTLP JSON, raw store payloads, span attributes, span events, and sensitive bundles.
- Replaced vague error and sensitive placeholder wording with deterministic field predicates, regexes, and a Base64 credential predicate.
- Confirmed `prompt.version` is safe metadata, while `prompt.content` and raw prompt fields are sensitive.
- Defined sensitive bundle schema version 1 for `manifest.json` and `evidence/*.json`.
- Fixed reverse lookup from standard output `evidence_ref` to bundle `manifest.json` and evidence files.
- Kept automatic bundle deletion out of scope; deletion remains manual via `manifest.json` `delete_target_paths`.
- Defined the M24 adapter mapping and confirmed the adapter needs the normalized measurement dataset to recover context columns.
- Confirmed M5 should implement an adapter command rather than leaving the M24-M27 connection as a manual mapping only.
- Kept M27 human decision records separate from Sprint3 auto-decision records.
- Confirmed `auto-approved` exits to Sprint4 planning handoff or M5 / M6 review evidence, not to repository modification.

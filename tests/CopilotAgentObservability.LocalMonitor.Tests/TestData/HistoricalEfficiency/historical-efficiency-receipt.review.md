# Historical Efficiency Fixture Review

Review date: 2026-07-23
Review type: recorded semantic self-review of the repository-safe synthetic fixture
Disposition: PASS

Fixture: `historical-efficiency-receipt.canonical.base64`
Payload SHA-256: `c25d2291332c2573d74dd72f90153dadb543f85859e892446cbf3ad18c214ecc`
Receipt ID: `historical-efficiency-receipt-ce323e140d07e0ec0e1528563ebb4197`
Extraction ID: `historical-extraction-00000000000000000000000000000001`
Extraction SHA-256: `e34b2a44b31342b7482ca2fb39b27ec5c1d0ffc4122f72abfd47a28db9b7b55b`

## Semantic review

- Formula: `session_total_gt_p75_and_ratio_gte_1_50`; the exact totals are 100, 110, 120, and 300, so the median is 115, nearest-rank p75 is 120, and the 300-token subject is above p75 and at least 1.50 times the median.
- Evidence: the token driver cites all four ordered cohort references and all four separate quality references. The mitigation repeats the exact metric evidence references.
- Quality: all four source Sessions have decisive `pass` quality evidence, so quality availability is `available`.
- Verdict: `supported`; the matched cohort has complete metrics and dimensions, full/live source Sessions, one source/version/adapter/model partition, and available quality evidence.
- Coverage: all ten registry rows are present in registry order. `tool_call_volume`, `tool_failure_overhead`, `permission_wait`, and `subagent_fanout` are explicitly unavailable with their frozen #72 reason codes and zero eligible Sessions.
- Mitigation: `review_high_token_sessions` uses fixed registry text and carries no effect, verified-improvement, provider, model-choice, price, currency, or cost claim.
- Identity: driver `historical-efficiency-driver-4542088fe2068207a632257d2c9fcba7` repeats the receipt extraction ID/hash, and its driver ID binds both fields plus the exact rule, observations, cohort values, evidence, verdict, and notes.
- Ordering: category coverage, Sessions, evidence references, observed values, and drivers follow the frozen canonical ordering rules.
- Repository safety: PASS. The decoded canonical payload contains only synthetic opaque identifiers, fixed registry text/codes, bounded numeric observations, and repository-safe references; it contains no raw prompt/response/tool body, local path, credential, PII, price, currency, or monetary cost.

## Validation binding

`FrozenHandoffFixtureAndSchema_MatchTheExactIssue75DtoStateAndEvidenceContract` recomputes this exact fixture and SHA-256, round-trips it through the strict reader, checks driver extraction identity, validates it against the committed JSON Schema, and requires this review record to name the same payload hash and disposition facts.

# Issue #95 Cost Analytics Closeout

This directory is historical validation evidence for the immutable functional
candidate `88e4c40dd7e76d6a80bb87a17c4e5acd88081bf8`. Current product behavior
remains defined by the repository requirements and specifications.

## Candidate and ancestry

- Matrix preparation: `98bfd62c5aad65961beefe42f51141c7e9f54169`
- P1 integration: `07dc219c4f5c5ef56e7810a23c6466a52e90aa97`
- P2 foundation integration: `245de89b0d016012a68e29ed00309c9cc768e81a`
- Preserved #95 functional candidate: `e42e40de1378fc652f8e2e018c41e4452cb8d534`
- Committed-object semantic correction: `6e7c262cd07290d29f5d9043726592d8d546c90d`
- Historical budget isolation candidate: `c1de4033a278ae858166d4af39d9668c9fc9c771`
- Multi-path RED correction verifier: `7227c1f8c80b6c5d26db62578e45ba313f695759`
- Final functional candidate: `88e4c40dd7e76d6a80bb87a17c4e5acd88081bf8`

The final candidate contains the accepted #75, #84, and #88 owner revisions.
The migration tail remains:

`historical_instruction_analysis -> historical_import -> sanitized_import -> runtime_backup -> pricing`

Pricing is last and requires Session v13, alert engine v2, and runtime backup
v1.

## Validation disposition

- `91-A-095`: passed
- `91-S-095`: passed
- `91-L-095`: blocked_external/high
- Release decision: `release_ready_with_external_blockers`

The live row was not promoted with synthetic data. Reviewed positive
source/version mappings and separate live authorization remain unavailable for
both GitHub Copilot and Claude Code.

## Evidence files

- `live-validation.md` records exact commands, current results, OS coverage,
  and retained RED history.
- `validation-matrix.json` contains the three canonical Issue #95 rows.
- `artifact-checksums.json` binds the committed evidence artifacts to the
  functional candidate.
- `evidence-attestation.json` is created only in the attestation child commit.

No raw prompts, responses, tool bodies, private archives, secrets, or sensitive
locators are included.

# M1 Pricing Registry And Estimator Evidence

## Candidate Boundary

- Workspace base SHA:
  `07dc219c4f5c5ef56e7810a23c6466a52e90aa97`
- Worktree: `.worktrees/issue-94-pricing-registry`
- Branch: `codex/issue-94-pricing-registry`
- This writer performed no commit, push, or pull-request operation.
- Functional revision:
  `def9311659328c473566d54476b81dddafa6638d`
- Accepted #88 test-integrity revision:
  `672b83d86d83d10cf07a1609fe9bd370ce9aa742`
- Accepted #89 test-integrity revision:
  `50db4d9f343361f31e42ed684b2ab2828d5fe7b9`
- Immutable integrated candidate:
  `7e688fecdeecd81013f3c9097719d45e412245f4`
- The coordinator records the evidence-only head separately after this file is
  committed. No push or pull-request operation was performed.

## TDD Evidence

The initial focused command failed as intended before implementation:

```powershell
dotnet test tests\CopilotAgentObservability.Pricing.Tests\CopilotAgentObservability.Pricing.Tests.csproj
```

Result: exit `1`; the production pricing assembly built as an empty scaffold
and the test compiler reported missing `PricingEstimationEngine`,
`PricingRegistryDocument`, `PricingUsage`, provenance, quantity, and fixed-token
types. This was the expected RED boundary.

After implementation and review hardening:

```powershell
dotnet test tests\CopilotAgentObservability.Pricing.Tests\CopilotAgentObservability.Pricing.Tests.csproj --no-restore --logger "console;verbosity=minimal"
```

Latest focused result: exit `0`; `162/162` passed, `0` failed, `0` skipped.
This includes the registry JSON Schema check; producer/consumer symmetry at
64 catalog documents and exact 4 MiB snapshot bytes; the shared production/
consumer 1 MiB estimate ceiling in code, consumer rejection at 1 MiB plus one
byte, and a maximum-admitted 4,096-unit source-reference estimate producer
round-trip below that ceiling; valid-pair/unpaired-surrogate cases; request
mutation snapshotting; catalog snapshot digest/reload adversarial cases;
exact-decimal boundaries; and fixed no-leak admission regressions.

The strict snapshot matrix covers empty/malformed/duplicate/missing documents,
invalid nested registry fields, depth, document count, byte bound, and true
multi-document order. A bundled-plus-two-override snapshot independently pins
document, source-reference, entry, alias, and limitation order: every
reordering changes canonical bytes and catalog digest. The strict estimate
matrix covers
empty/malformed/duplicate/missing identity, invalid status/display shape,
depth, byte bound, registry-null not-estimable replay, and deep immutability.
Deep immutability separately exercises required, estimated, and missing
coverage collections.
Synthetic estimate and full catalog snapshot canonical bytes and their exact
SHA-256 identities are pinned as raw golden bytes with no trailing newline;
tests compare `ReadAllBytes` output without trimming.

Review hardening preserved these additional RED-to-GREEN transitions:

- a mid-edit focused run was `133/136`; three no-echo assertions exposed retained
  nested parser exceptions before consumers/loaders switched to fixed outer
  errors with no `InnerException`;
- a later focused run was `157/159`; only the two raw golden fixtures still had
  one trailing LF before exact-byte normalization;
- the exact/uppercase `home.arpa` runtime cases and an entry-only schema mutation
  were RED at `33/36` selected cases, then GREEN at `36/36` after exact-root and
  subdomain rejection were mirrored in runtime and schema;
- the entry-only raw-whitespace schema mutation was RED at `0/1`, then GREEN at
  `1/1` after the schema mirrored runtime lexical rejection.

This is focused iteration evidence, not a substitute for the required final
validation sequence.

## Preserved Diagnostic Failure

A preliminary `dotnet build CopilotAgentObservability.slnx --no-restore
--verbosity:minimal` failed because the fresh worktree lacked
`project.assets.json` for existing projects. The new Pricing project and tests
built, but this diagnostic is not the repository-required build and is not
claimed as equivalent evidence.

A first post-review full solution test run was also not final evidence:
Pricing `68/68`, Instruction Findings `20/20`, Alerts `451/451`, Doctor
`266/266`, and Config CLI `4598/4598` passed, but Local Monitor finished
`3150/3151` with one existing
`RuntimeBackupPlaywrightTests.Backup_restore_page_creates_downloads_and_previews_without_exposing_web_restore`
`NetworkIdle` timeout. The strict-consumer hardening landed afterward, so the
entire required sequence must be rerun. The timeout is not replaced by a
different command or claimed as success.

## Preserved Earlier Full Validation

An exact required sequence ran before the final catalog-snapshot, arithmetic,
URI, and review hardening in this lane:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Results:

- `pwsh scripts\agent\sync-claude-skills.ps1 -Check`: exit `0`;
  5 shared skills up to date.
- `dotnet build CopilotAgentObservability.slnx`: exit `0`; 0 warnings,
  0 errors.
- `pwsh scripts\test\install-playwright-chromium.ps1`: exit `0`.
- `dotnet test CopilotAgentObservability.slnx`: exit `0`; Pricing `70/70`,
  Instruction Findings `20/20`, Alerts `451/451`, Doctor `266/266`, Config CLI
  `4598/4598`, and Local Monitor `3151/3151`; total `8556/8556`, 0 failed,
  0 skipped.

The earlier required-run failure remains part of the record. It stopped at
Local Monitor `3150/3151` because the runtime-backup Playwright test timed out
at `RuntimeBackupPlaywrightTests.cs:24` while waiting for `NetworkIdle`.
The exact failing test then passed alone in 1 second. A coordinator independently
reproduced the same line-24 timeout in unrelated #92 work, so the retained
classification is shared #88 full-suite timing nondeterminism, not a #94 code
failure. The later exact full sequence passed; it does not erase the earlier
failure.

Because production and contract files changed after that successful sequence,
the `8556/8556` result is preserved historical evidence and is not claimed as
final proof of the current tree. After all reviews settled at C0/I0/M0, the
coordinator committed the functional revision, merged the accepted #88 and #89
test-integrity corrections, and selected clean candidate
`7e688fecdeecd81013f3c9097719d45e412245f4`.

The exact required sequence on that immutable candidate was:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Final results:

- Claude skill mirror: PASS, 5 shared skills.
- Build: PASS, 0 warnings and 0 errors.
- Playwright Chromium bootstrap: PASS, exit 0.
- Full solution: PASS, 8,651/8,651, 0 failed and 0 skipped:
  Pricing `162/162`, Instruction Findings `20/20`, Alerts `451/451`, Doctor
  `266/266`, Config CLI `4598/4598`, and Local Monitor `3154/3154`.

The repository Issue #91 scanner self-test passed all 118
transformation and 5 negative cases. The sprint evidence scan passed for 4
files, 472 variants, and 0 matches. The complete changed-file scan passed for
35 files, 4,130 variants, and 0 matches. `git diff --check` also passed with no
output. The sprint scan and diff check were repeated after the final evidence
update.

No checksum/archive manifest applies: #94 publishes no archive or validation
matrix bundle. Canonical registry and golden estimate fixtures are direct
reviewed repository files, not a release bundle. The catalog golden is 2,364
bytes with SHA-256
`de5be646e5841d67583dc1e2a037194b7adce751e2dcffd02a39b74ec573931b`;
the estimate golden is 4,526 bytes with SHA-256
`328691e8b4c23526ba277feaf810af03751583ea67834cd68966f47e4cc92012`.
Both terminate at `}` with no BOM or trailing newline.

## Unverified And Not Applicable Scope

- Genuine producer quantity/model mapping remains `unverified`: the current
  #61 manifests do not authorize the complete positive production tuple
  required by this domain.
- Runtime price fetching, invoice/account/private-contract reconciliation,
  currency conversion, and provider credential use are `not_applicable` to
  #94. No network or persistence surface exists in the Pricing project.
- Codex App v1 remains explicitly `not-estimable` with
  `subscription_or_contract_unknown`; the #92 Desktop decision is NO-GO.
- #95 persistence, Cost UI/API, recalculation history, budget alerts, alert
  receipt evolution, and migration/backup integration are unattempted here by
  design and remain owned by #95 and the relevant existing alert/export Issues.
- No content-enabled capture, raw prompt/response/tool body, genuine billing
  observation, push, PR, or tag was performed.

## Safety And Data

All positive calculations use the pinned synthetic fixture or reviewed public
list-price metadata. Evidence contains no raw prompt/response, tool data,
credential, invoice/account/contract identifier, PII, private locator, or
local runtime database content.

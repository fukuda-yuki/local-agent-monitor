# Wave 4 M1 integrated candidate validation evidence

This is historical candidate evidence, not a product specification. It
contains no prompt, response, tool body, credential, raw record, personal
data, sensitive path, or content-enabled capture output.

## Evidence boundary

| Field | Record |
| --- | --- |
| Wave 4 kickoff | `2df115682f0e280d020c04b4936968d4602f623c` |
| Resumed #75 pre-freeze HEAD | `da016c581e89dc3902e6e0332b618252d5028481` |
| #75 source functional / evidence / attestation | `e7037714f16bc4c9c14b34ed8f69fc6e18cd6972` / `496c94ef78f77000aa640b8682a48923abc36840` / `fc6cd045c14b7f6f871dd2acbaf1285ae0bf3b95` |
| Integrated functional / evidence / attestation equivalents | `2054bae8daa433315ba30221e456e031a488b02b` / `ef8b3b082f55aacecae31d1a65706b51e4535227` / `bbba6cf79b4e6da1ee1363edd73007ece476c036` |
| Exact integrated functional candidate | `2054bae8daa433315ba30221e456e031a488b02b` |
| Exact integrated validation evidence head | `bbba6cf79b4e6da1ee1363edd73007ece476c036` |
| Branch / worktree | `codex/wave-4-integration-closeout` / `.worktrees/wave-4-integration-closeout` |
| Source branch / worktree | `codex/issue-75-historical-analysis-ui` / `.worktrees/issue-75-historical-analysis-ui` |
| Date / OS | 2026-07-24 / Windows x64 |
| Data | Disposable SQLite databases, loopback hosts, deterministic synthetic metadata, and repository Chromium |
| Capture policy | No content-enabled capture and no genuine provider execution |

The worktree was clean before and after execution. All accepted Wave 1–3 and
parallel-lane revisions are direct ancestors of the candidate. Issue #75
source commits, including the Issue #72 test-only clock repair, were
cherry-picked onto the integration branch with matching stable patch IDs. In
particular, source functional `e7037714` maps to integrated functional
`2054bae8`, source evidence `496c94ef` maps to `ef8b3b08`, and source
attestation `fc6cd045` maps to the exact integrated validation evidence head
`bbba6cf7`.

The resumed clean HEAD `da016c58` is the #75 source-branch test-activation
commit and an ancestor of the final #75 source candidate. It is not the Wave 4
integration kickoff and it is not a validation evidence SHA. The exact kickoff
is the already integrated Wave 3 evidence commit `2df11568`; the two SHAs are
on different branch roles and neither is substituted for the other.

## Terminal graph and ancestry

At this evidence head, #72, #73, and #74 are `closed/completed`; #75 and #48
are temporarily open only to record and close the safety/specification
correction. Their technical close gates pass, and the authorized close actions
are the next operation. Historical import #76–#79, Alerting #80–#84, and
Portability #85–#88 are `closed/completed`. Issue #60 remains open.

Issue #60 remains open because the next P2 entry points are still active:
#92/#93 for the remaining Codex source/app work and #94/#95 for monetary cost
contracts and presentation. Wave 4 completion does not satisfy or absorb
those acceptance items.

Every revision in this accepted set is an ancestor of the integrated
functional candidate:

- #53 `7d7373009c6f8889e059da87dc67ebe807d4e1ee`;
- #58 `6c92070e`, `b85494e5`, `3e17ea0b`;
- #59 `c87acd85`, `5819a702`, `4cfe4544`;
- #72 `7197869f`, `aba60c1d`, `48ac57e6`;
- #73 `f95024d73c8070da7cfc5412743d6595dec8ff7e`;
- #74 `d31819acf617fa2d63a5cb8f3a52ded7aa13b465`;
- #76 `f7dfe704`;
- #77 `1086409a`;
- #78 `810d9a6b`;
- #79 `071b00e3`, `0d68f1e3`;
- #80 `aa2992bd`;
- #81 `f44c72ee`;
- #82 `5e73911b`;
- #83 `38572014`;
- #84 `b1fb8011`, `1a768d28`;
- #85 `37a095d7`, `56c20332`;
- #86 `60c63597`, `4d371ccd`;
- #87 `ba4aa770`, `74966d0d`;
- #88 `556811ef`, `2df11568`.

#73 uses the canonical #59 consumer, #73 and #74 consume the single #72
dataset without forks, and #75 resolves only owner-validated #53 targets.
Historical import, provider pricing, proposal apply, verified effect verdicts,
Alert Center, and portability were not absorbed by #48 or #75.

## Execution table

| Area | Exact result |
| --- | --- |
| Required repository validation | skill mirror 5; build 0 warnings/errors; Playwright bootstrap passed; full solution 8486/8486, zero failed/skipped |
| Full solution split | InstructionFindings 20; Alerts 451; Doctor 266; Config CLI 4598; Local Monitor 3151 |
| #75 historical analysis | handoff gate Local Monitor 279/279 + InstructionFindings 20/20; Playwright 7/7; production evidence 33/33; contract/spec 15/15 |
| #79 historical import | Config CLI 83/83; Local Monitor 107/107; Session migration fixture 72/72 |
| #84 Alert Center | UI/center/lifecycle/overview 106/106; receipt 49/49; evaluator/query 33/33; lifecycle 79/79; exact store class 12/12 |
| #85 sanitized export | Local Monitor/authority/#91 88/88; Config CLI 10/10; #59 consumer 20/20; #80 consumer 49/49 |
| #86 sanitized import | Config CLI 12/12; Local Monitor 101/101; migration 11/11; strict archive 14/14 |
| #87 raw replay | Config CLI 52/52; Local Monitor 25/25 |
| #88 backup/restore | Config CLI 12/12; Local Monitor 308/308; packaged restart/readiness/scripts 45/45 |
| Cross-migration gate | 379/379 for monitor/session/retention, historical analysis/import, sanitized import, and Wave 3 backup/restore |
| Matrix ownership gate | 14 passed rows + 6 preserved `blocked_external` rows = 20/20 classified |
| Matrix validators | row counts 3,3,3,2,3,3,3; #85 `release_ready`; all others `release_ready_with_external_blockers` |
| Contract/scanner self-tests | validation contract 10/10; scanner 118 transformations + 5 negative cases |
| Repository-safe scan | 23 evidence files, 2714 variants, zero matches |
| Historical-import fixture scan | 21 files, 2478 variants, zero matches |
| Artifact integrity | 23 primary artifacts exist and are hashed; #75 attestation, #79 fixture, #85 golden bundle, and #86 fingerprint independently match |

## Active row classifications

| Issue | Passed rows | External row |
| --- | --- | --- |
| #75 | `91-H-075`, `91-S-075` | `91-L-075` — `blocked_external/high` |
| #79 | `91-I-079`, `91-S-079` | `91-L-079` — `blocked_external/medium` |
| #84 | `91-A-084`, `91-S-084` | `91-L-084` — `blocked_external/high` |
| #85 | `91-E-085`, `91-S-085` | none; decision `release_ready` |
| #86 | `91-I-086`, `91-S-086` | `91-L-086` — `blocked_external/medium` |
| #87 | `91-R-087`, `91-S-087` | `91-L-087` — `blocked_external/medium` |
| #88 | `91-B-088`, `91-S-088` | `91-L-088` — `blocked_external/medium` |

The future registry contains only `codex-app`, owned by Issue #93 in
`not_available` state. No implemented row inherits a pass from the registry.

## Blocked, reused, not-applicable, and unverified evidence

- `91-L-075`: content access is not authorized and no reviewed exact
  provider/source/version multi-Session tuple exists. Genuine provider-backed
  instruction and efficiency evidence remains unverified.
- `91-L-079`: #77/#78 expose no production-supported fixture tuple. Real
  historical-producer interoperability remains unverified. Four Unix
  FIFO/descriptor-replacement bodies are platform-gated on Windows; WSL had no
  repository SDK and Docker was unavailable, so dynamic Unix proof remains
  unverified.
- `91-L-084`: no reviewed #61 provider mapping grants the required Alert
  Center capabilities. Genuine positive provider receipts remain unverified.
- `91-L-086`: a genuine second Windows machine was unavailable. Cross-machine
  filesystem/runtime/local-identity portability remains unverified.
- `91-L-087`: content-enabled producer capture was not authorized. Genuine
  producer content through export, replay, restart, and cleanup remains
  unverified.
- `91-L-088`: a genuine compatible second machine was unavailable. Private
  transfer, offline restore, packaged restart/readiness, and Doctor execution
  on that second machine remain unverified.
- #88 reuses three WSL2 native path/device/FIFO/socket checks and the direct
  disposable sparse-size probe from functional execution SHA
  `556811ef0bf96ef1267c4a9d00d9311154fc78e3`; they were not rerun on Windows.
  Shared surface/Playwright and all repository-supported backup filters were
  rerun on the integrated candidate.
- #74 optional AI narrative and collector configuration validation are
  `not_applicable`. No file under `infra/otel-collector/` changed.
- The #85 README line that says release was blocked is preserved as an
  explicitly historical pre-acceptance checkpoint. The authoritative accepted
  review and matrix at `56c20332` record `release_ready`; it is not treated as
  current status.

No live row above was relabeled as passed. No content-enabled environment
variable, producer, raw archive, external model, second-machine substitute, or
repository evidence containing raw data was created.

## Required failure history

The #75 source evidence preserves the pre-repair #72 retention-clock failures
and the first non-reproducible legacy Playwright failure. Both were diagnosed;
the test-only clock repair was integrated, no timeout was increased, and the
fresh candidate full suite passed.

The original `23c5212e`/`0c67e185` candidate family was invalidated when
independent review found a full preview object retained in browser state and a
sanitized-only server posture bypass. The corrected `e2c2e2d5`/`e6017c21`
family was then invalidated for candidate freeze because the exact three-field
browser-state invariant was not yet canonicalized and its evidence dates
preceded the actual execution date. Specification tests were intentionally run
red first (2 of 5 failed and 3 passed), then passed 5/5 after the canonical
requirements/specification/matrix correction. The final candidate family is
`e7037714`/`2054bae8`; all behavior-invalidated checks were rerun.

The first final integrated #79 migration command used the nonexistent filter
`SessionSchemaMigrationTests` and matched no tests. It is not evidence. The
canonical `SessionSchemaMigrationFixtureTests` filter then passed 72/72.

An early scanner invocation supplied a comma-delimited string where a
PowerShell array was required and failed with `required_target_missing`. It is
not evidence. The corrected array invocation passed. An early no-restore build
in a repair worktree failed with `NETSDK1004` because restore assets did not
exist; the required restored build later passed.

During integrated artifact verification, an initial custom registry assertion
used the nonexistent property `surfaces` instead of canonical `entries`.
PowerShell emitted `InvalidOperation` errors and the script incorrectly printed
a PASS-like line. That attempt is invalid and is not evidence. The corrected
terminating assertion verified exactly one `codex-app` entry, owner #93, state
`not_available`.

No required repository or row validation command failed at the final exact
integrated functional candidate. The failures above remain recorded and were
not erased by the successful reruns.

## Commands

The required commands were run exactly:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Focused commands used the canonical handoff filters and the owning
`HistoricalImport`, `AlertCenter`/`AlertLifecycle`, `SanitizedExport`,
`SanitizedImport`, `RawReplay`, and `RuntimeBackup` class filters. Matrix and
scanner commands used `scripts/validation/issue-91/validate-matrix.ps1`,
`test-validation-contract.ps1`, `test-scan-outputs.ps1`, and
`scan-outputs.ps1`. The exact results are recorded above; focused commands do
not substitute for the successful full suite.

## Publication state

Candidate and evidence are local-only. The primary checkout was not modified.
No push, pull request, tag, content-enabled capture, or remote-history action
was performed.

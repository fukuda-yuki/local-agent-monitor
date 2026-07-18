# Issue #106 validation preparation report

This document records preparation for the Issue #106 Claude Code live-
validation matrix. It is not a final validation record. No final validation has
run in this preparation worktree, and no statement below may be promoted to a
final pass without the frozen Issue #104 candidate gate.

## Preparation identity

| Field | Pinned value |
| --- | --- |
| Kickoff SHA | `916484dac668fb029a20221532ed57f8592dcaa1` (`916484d`) |
| Branch | `codex/issue-106-claude-validation-prep` |
| Worktree | `.worktrees/issue-106-claude-validation-prep` |
| Baseline #107 | `fac88b9` |
| Baseline #108 | `bfd2ce9` |
| Recorded merge baseline | `196ca75` |
| Final candidate SHA | **UNFILLED — supplied by Issue #104 at final-run time** |
| Final-run status | **NOT RUN** |

The #107 and #108 commits must be verified as ancestors of the immutable final
candidate before any final classification. The #109 negative binding case and
#110 gate-disabled producer-key observation remain open dispositions to verify
against that candidate.

## Preflight results

Recorded from the 2026-07-18 preparation dry run on kickoff `916484d`
(Windows native boundary, disposable loopback port `4323`, disposable root
`tmp/issue-106-validation` inside this worktree). No raw markers, prompts,
responses, Hook payloads, credentials, PII, or machine-specific absolute
paths are recorded here.

| Check | Command | Exit code | Pass/fail | Missing prerequisite | Retry requires |
| --- | --- | ---: | --- | --- | --- |
| Claude CLI/version | `claude --version` (via preflight) | 0 | PASS (`2.1.214`) | none | none |
| .NET SDK/version | `dotnet --version` (via preflight) | 0 | PASS (`10.0.300-preview`) | none | none |
| Local Monitor build/binary | `dotnet build <LocalMonitor.csproj> --no-restore` (via preflight) | 0 | PASS (build succeeded this run) | none | none |
| Disposable loopback port | bind `127.0.0.1:4323` and release (via preflight) | 0 | PASS | none | none |
| Disposable storage writable | write/remove probe under disposable storage (via preflight) | 0 | PASS | none | none |
| Throwaway Hook path writable | write/remove throwaway `.claude/settings.json` probe (via preflight) | 0 | PASS | none | none |
| Explicit operator authorization | `preflight -OperatorAuthorized` (switch withheld) | 2 | FAIL (fail-closed by design) | distinct operator authorization for live content-enabled runs was not granted in this preparation session | operator_action |

Overall preflight exit: `1` (fail-closed on the authorization check only; all
six environment prerequisites passed). This demonstrates the intended
fail-closed behavior; it is not an environment defect.

## Non-authoritative dry-run results

Executed 2026-07-18 on kickoff `916484d` with the fixture-only direct-receiver
path (OTLP JSON POST to the disposable Monitor at loopback port `4323`) and a
fresh runtime marker (committed reference `sha256:e1cd189ef49a` only). This is
a 106-C dry run only; it cannot establish producer behavior or final candidate
behavior, and no row below is a final classification.

| Matrix case | Dry-run command/result | Classification | Exact blocker or contract reason | Leak scan |
| --- | --- | --- | --- | --- |
| 1A. Content-enabled `claude -p` | Live producer run not executed. Fixture-only receiver check: key-present fixture ingested (HTTP 200); derived `content_state=available`; sanitized traces/list/spans withheld the marker; raw route contained it only in raw-default mode during the local check and was not copied to evidence | dry-run smoke passed (non-authoritative) | Live run blocked: distinct operator authorization not granted this session; final classification additionally gated on the frozen #104 candidate | PASS (exit 0) |
| 1B. Gate-disabled key observation (#110) | Fixture-only receiver check: `key-absent` fixture derived `not_captured`; `key-present-empty` fixture derived `available` — receiver-side confirmation of the #110 presence-only risk mechanism on kickoff main | dry-run smoke passed (non-authoritative); #110 remains open | Real gate-disabled producer observation deferred to 106-E on the frozen candidate | PASS (exit 0) |
| 2A. Hook + OTel exact/native binding | `hook-forward --source claude-code` consumed the synthetic Hook envelope (exit 0) and produced a `hook_only` session (`completeness=partial`); fixture OTel traces projected as separate `otel_only` sessions; distinct synthetic native IDs were correctly not merged | dry-run smoke passed (non-authoritative) | Positive byte-equal native-session case requires the live producer at 106-E | PASS (exit 0) |
| 2B. Negative shared-trace/generic-label binding (#109) | No spurious `exact_linked` observed for shared-nothing fixtures; the dedicated shared-trace-ID negative arrangement is pinned in the runbook for the final run | dry-run smoke passed (non-authoritative); #109 impact unverified | #109 negative case requires the live producer pair on the frozen candidate at 106-E | PASS (exit 0) |
| 3. Restart/reconnect | Stopped and restarted the disposable Monitor on the same database/port mid-run: `/health/ready` 200 after restart; both traces retained with identical IDs; no duplicate, crash, or data loss | dry-run smoke passed (non-authoritative) | Final classification requires an active/resumable live Claude session across the restart at 106-E | PASS (exit 0) |
| 4. `--sanitized-only` | Restarted with `--sanitized-only`: raw route returned 404 (200 in raw-default mode before), trace-detail shell carried `data-raw-available="false"`, trace list showed a shortened TraceId, sanitized traces/spans APIs returned 200 without the marker | dry-run smoke passed (non-authoritative) | Final classification requires a fresh content-disabled live Claude trace on the frozen candidate at 106-E | PASS (exit 0) |

Setup/teardown reproducibility: `preflight.ps1` (fail-closed proof above),
disposable start/restart/sanitized-only start commands, `scan-leaks.ps1`
after the run (final `scan_result: PASS`, exit 0; application-log user-profile
path hits reported as WARN per the scanner's scope rule), and `cleanup.ps1`
teardown (exit 0, disposable root removed, no non-owned process touched) all
completed on this worktree without reusing #104's worktree or mutable state.

## Exact blockers

1. Live content-enabled and gate-disabled `claude -p` executions (cases 1A/1B,
   and the live halves of 2A/2B/3/4): distinct explicit operator authorization
   was not granted in this preparation session. `preflight.ps1` without
   `-OperatorAuthorized` exits `1` (authorization check exit `2`,
   `retry_requires=operator_action`). This is the intended fail-closed gate,
   recorded here as the exact prerequisite for 106-E.
2. Final classification of all four matrix cases: the immutable
   `#104 FINAL_CANDIDATE_SHA` and candidate manifest have not been provided.
   The 106-D gate below remains NOT SIGNED OFF; no final run may start until
   it completes (retry requires the #104 handoff, i.e. a product-candidate
   input, not operator action in this worktree).

## Preparation review record

The preparation deliverables were implemented and then independently reviewed
in two read-only review rounds before this report was finalized:

- Round 1 findings (all fixed and re-verified): the cleanup guard accepted any
  path containing `issue-106` (destructive-delete risk); the live `claude -p`
  export settings used generic OTLP variables instead of the pinned per-signal
  `http/protobuf` contract in
  `docs/specifications/interfaces/configuration-setup.md`; preflight could
  pass on a stale binary after a failed build; the leak scanner missed
  UTF-16/UTF-32 content and case-altered markers.
- Round 2 findings (all fixed and re-verified): the cleanup root could still
  equal `<repo>/tmp` (strict-child rule added) and descendant reparse points
  were not rejected before recursive deletion; the scanner's strict UTF-8
  decode ran before the UTF-16 fallback for BOM-less NUL-bearing content.
- Recorded self-review (minor, reversible): empty (0-byte) files are treated
  as clean `empty` decodings instead of a decode error, and application-log
  scope reports absolute user-profile-path pattern hits as WARN without
  failing the scan (local runtime logs are uncommitted; marker and credential
  patterns still fail in every scope, and all patterns still fail in
  repository-output, evidence, and sanitized-output scopes).

Fail-closed behavior, guard rejections, encoding detection, WARN scoping,
idempotent teardown, and marker non-disclosure were each re-verified by direct
execution in this worktree after the fixes.

## Candidate gate status

| Gate | Evidence | Status |
| --- | --- | --- |
| Issue #104 candidate SHA and manifest received | **UNFILLED** | NOT SIGNED OFF |
| `fac88b9` is an ancestor of candidate | **UNFILLED** | NOT SIGNED OFF |
| `bfd2ce9` is an ancestor of candidate | **UNFILLED** | NOT SIGNED OFF |
| `196ca75` is an ancestor of candidate | **UNFILLED** | NOT SIGNED OFF |
| Clean detached worktree is exactly candidate SHA | **UNFILLED** | NOT SIGNED OFF |
| Fresh disposable database and Hook directory | **UNFILLED** | NOT SIGNED OFF |
| Final matrix executed only after gate | **UNFILLED** | NOT SIGNED OFF |

## Final-run handoff

At final-run time, append the dated section from
`scripts/validation/issue-106/evidence-template.md` to
`docs/sprints/issue-106-claude-live-validation-followup.md`. Never overwrite
or rewrite that prior evidence file's existing sections. This preparation
report remains historical preparation evidence and does not become final
validation by itself.

# Issue #106 final-run evidence section template

> Append a completed, dated copy of this section to
> `docs/sprints/issue-106-claude-live-validation-followup.md` only after the
> final run. Never overwrite, reorder, or rewrite any prior section in that
> evidence file. This template itself is not evidence and makes no claim that
> final validation has run.

## Final run — [UNFILLED DATE]

### Candidate and pinned environment

| Field | Value |
| --- | --- |
| Immutable candidate SHA | **UNFILLED** |
| Candidate manifest reference | **UNFILLED** |
| Monitor revision | **UNFILLED** |
| Claude Code CLI version | **UNFILLED** |
| OS and execution boundary | **UNFILLED** |
| Disposable worktree identity | **UNFILLED** |
| Loopback Monitor URL/port | **UNFILLED** |
| OTLP endpoint | **UNFILLED — live producer: loopback full `/v1/traces` endpoint; fixture dry run: same direct receiver endpoint** |
| OTLP protocol/signal | **UNFILLED — live: OTLP HTTP/protobuf per-signal trace (`Content-Type: application/x-protobuf`); fixture-only dry run: OTLP JSON direct POST with `Content-Type: application/json` (not the live producer contract)** |
| Hook state | **UNFILLED — disabled/configured and exact command shape** |
| Hook endpoint/timeout/selector | **UNFILLED** |
| Content-enabled gate state | **UNFILLED — explicit authorization and `OTEL_LOG_USER_PROMPTS=1`** |
| Gate-disabled state | **UNFILLED — unset/`0` and observed key state** |
| Sanitized-only flag | **UNFILLED per case** |
| Restart timing | **UNFILLED — UTC ordered timestamps** |
| Truncated marker reference | **UNFILLED — `sha256:<12 hex chars>` only** |

### Case classifications

Use only these classifications: `passed`, `blocked` with an exact external
blocker, or `not-applicable` with a contract-based reason. Never use `skipped`
or `unavailable` as a pass.

#### Case 1 — content-enabled and gate-disabled `claude -p`

- Content-enabled classification: **UNFILLED**
- Content-enabled blocker/reason: **UNFILLED**
- Gate-disabled classification: **UNFILLED**
- Gate-disabled `user_prompt` key observation (`key_absent`,
  `key_present_empty`, or `key_present_nonempty`): **UNFILLED**
- `content_state` observation: **UNFILLED**
- Sanitized isolation result: **UNFILLED**
- Producer command exit status: **UNFILLED**
- Raw OTLP inspection exit status: **UNFILLED**
- Sanitized API/UI inspection exit status: **UNFILLED**
- Leak scan after case: **UNFILLED — exit status and marker reference only**

#### Case 2 — Hook + OTel exact/native-session binding

- Positive classification: **UNFILLED**
- Positive blocker/reason: **UNFILLED**
- Exact native-session byte-equality observation: **UNFILLED**
- Negative #109 classification: **UNFILLED**
- Negative #109 contract-based reason or blocker: **UNFILLED**
- Shared trace ID/generic `otel-exact` label rejected: **UNFILLED**
- Hook forwarder command exit status: **UNFILLED**
- OTel producer command exit status: **UNFILLED**
- Binding/API inspection exit status: **UNFILLED**
- Leak scan after case: **UNFILLED — exit status and marker reference only**

#### Case 3 — restart/reconnect

- Classification: **UNFILLED**
- Blocker/reason: **UNFILLED**
- First ingestion exit status: **UNFILLED**
- Disposable stop exit status: **UNFILLED**
- Restart/readiness exit statuses: **UNFILLED**
- Resume/continuation exit status: **UNFILLED**
- Duplicate/crash/data-loss observations: **UNFILLED**
- Leak scan after case: **UNFILLED — exit status and marker reference only**

#### Case 4 — `--sanitized-only`

- Classification: **UNFILLED**
- Blocker/reason: **UNFILLED**
- Monitor start exit status: **UNFILLED**
- Raw route status (`404` required): **UNFILLED**
- `data-raw-available=false` result: **UNFILLED**
- Shortened TraceId list result: **UNFILLED**
- Sanitized flow/waterfall classification result: **UNFILLED**
- Leak scan after case: **UNFILLED — exit status and marker reference only**

### Cleanup

| Action | Command | Exit status | Result |
| --- | --- | ---: | --- |
| Restore/remove Claude and OTEL environment changes | **UNFILLED** |  | **UNFILLED** |
| Stop disposable Monitor only | **UNFILLED** |  | **UNFILLED** |
| Remove disposable database/storage | **UNFILLED** |  | **UNFILLED** |
| Remove throwaway Hook configuration | **UNFILLED** |  | **UNFILLED** |
| Final repository/evidence leak scan | **UNFILLED** |  | **UNFILLED** |

The appended section must state that the final classifications apply only to
the frozen candidate SHA and that any replacement candidate invalidates these
results and requires a clean rerun.

# Issue #106 / 106-D final-run candidate input record

**Status: RECEIVED — gate mechanics verified on 2026-07-18.** The immutable
candidate handoff from Issue #104 was published as
`docs/superpowers/plans/2026-07-17-issue-104-claude-first-trace/handoff-106.md`
(frozen by commit `95d2985`, integrated into `main` by merge `70206ce`).
Values below are copied from that manifest and re-verified in this worktree;
they are not inferred from a moving branch. Live-run sign-off remains gated on
the explicit operator authorization recorded in preflight output at 106-E time.

## Final-run input contract

| Input | Record |
| --- | --- |
| Exact immutable `FINAL_CANDIDATE_SHA` | `54d758a260f347cc31a3191d342ad509eb62d81f` |
| Candidate manifest/reference supplied by #104 | `docs/superpowers/plans/2026-07-17-issue-104-claude-first-trace/handoff-106.md` at `main@70206ce` |
| Kickoff/base SHA | `main@920ff43` (per manifest; #103's `916484d` line is not an ancestor of the candidate and was merged separately at `70206ce`) |
| Included implementation commits | Checkpoint table in the manifest (`b43ed58` … `54d758a`), including the #109 repair series `09978b8`, `c807c7a`, `8f4b6b7`, `d062f48` |
| #107 ancestor verification (`fac88b9`) | `git merge-base --is-ancestor fac88b9 54d758a…` exit `0` |
| #108 ancestor verification (`bfd2ce9`) | `git merge-base --is-ancestor bfd2ce9 54d758a…` exit `0` |
| Recorded merge baseline verification (`196ca75`) | `git merge-base --is-ancestor 196ca75 54d758a…` exit `0` |
| Supported Claude Code version | Exactly one normal `MAJOR.MINOR.PATCH (Claude Code)` at `2.1.207` or newer; preflight observed `2.1.214` (supported) |
| Execution boundary | Windows native shell; disposable Local Monitor process, throwaway project directory, disposable SQLite database, non-default loopback port |
| Required Monitor endpoint | Loopback origin + `/v1/traces` (canonical monitor traces endpoint) |
| Required OTLP protocol/signal | Live: OTLP HTTP/protobuf per-signal trace (`OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf`); fixture-only dry run: OTLP JSON direct POST (not the live producer contract) |
| Hook configuration | Throwaway project `.claude/settings.json` entries invoking `hook-forward --endpoint <loopback>/api/session-ingest/v1/events --source claude-code --source-version <observed>`; Hook traffic keeps Session-store adapter `claude-code-hook` and produces no Doctor candidates |
| Trusted Hook selector | `--source-version` with the observed supported producer version |
| Content-enabled environment | `OTEL_LOG_USER_PROMPTS=1` for case 1A only, after distinct explicit operator authorization captured in preflight output |
| Gate-disabled environment | Telemetry on, `OTEL_LOG_USER_PROMPTS` off; one interactive prompt and one `claude -p` prompt; record `user_prompt` key presence/absence per raw export (#110 input conditions from the manifest) |
| Verification source expectation | Real Claude producer over raw OTLP; `source_surface=claude-code`; first-trace identity per the manifest |
| Verification adapter expectation | Expected Doctor adapter `claude-code-otel` for execution surfaces; actual observed adapter/provenance labels recorded without fabricated promotion |
| #109 known defect/question | Fixed in the candidate per manifest (`09978b8`, `c807c7a`, `8f4b6b7`, `d062f48`); the final run still executes the negative case rejecting shared trace ID/generic `otel-exact` labels as exact evidence |
| #109 expected classification impact | Expected: negative case stays `otel_only`/`hook_only`; if `exact_linked` appears from trace-ID-only evidence, the candidate is blocked by a #109 regression |
| #110 known defect/question | Open; resolved only by the live gate-disabled producer observation (`key_absent` vs `key_present_empty` vs `key_present_nonempty`) |
| #110 expected classification impact | `key_absent` confirms the presence-only rule; `key_present_empty` reproduces the dry-run receiver behavior (`available` derived while the gate is off) and requires a spec-first follow-up, classifying case 1B as blocked pending that disposition |
| Exact focused validation commands already run | Per manifest: focused `MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary` filter run, 2/2 PASS |
| Exact full validation commands already run | Per manifest at the frozen SHA: `dotnet build` PASS (0/0), Playwright bootstrap PASS, `dotnet test` PASS on exact rerun (5765 total, 0 failed; first run had the known intermittent MonitorOverview Playwright flake only) |
| Confirmation: no post-handoff product commit under same label | Manifest: "None permitted; only this documentation closeout"; verified `54d758a..main` contains only docs-freeze `95d2985`, merge `70206ce`, and the independent #103 line |

Raw prompts, responses, Hook payloads, credentials, PII, absolute user-profile
paths, and raw marker values are not valid record content. Use only sanitized
observations and truncated marker references.

## Candidate gate checklist

- [x] The candidate SHA is immutable and came with the #104 manifest (`handoff-106.md`).
- [x] `git merge-base --is-ancestor fac88b9 54d758a…` exited `0`.
- [x] `git merge-base --is-ancestor bfd2ce9 54d758a…` exited `0`.
- [x] `git merge-base --is-ancestor 196ca75 54d758a…` exited `0`.
- [x] A clean detached validation worktree was created at exactly the candidate SHA (`.worktrees/issue-106-final-candidate`, `git rev-parse HEAD` = candidate).
- [x] The clean validation worktree has empty `git status --porcelain` output.
- [ ] A fresh disposable database was initialized after the worktree gate (performed at final-run start).
- [ ] A fresh throwaway Hook configuration directory was initialized after the worktree gate (performed at final-run start).
- [x] A replacement candidate would invalidate and discard all prior final-run results.
- [x] Final classifications will be recorded only against this frozen SHA.

**Sign-off:** Gate mechanics verified 2026-07-18 against
`FINAL_CANDIDATE_SHA=54d758a260f347cc31a3191d342ad509eb62d81f`. Final-run
start remains gated on the explicit operator authorization captured in
preflight output (`-OperatorAuthorized`), which is never inferred.

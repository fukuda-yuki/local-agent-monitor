# Issue #106 / 106-D final-run candidate input record

**Status: NOT SIGNED OFF.** This is a template for the immutable candidate
handoff from Issue #104. It must be completed and reviewed before the final
Claude Code matrix begins. Blank fields are intentional; do not infer them
from a moving branch, an in-progress worktree, or historical evidence.

## Final-run input contract

| Input | Record |
| --- | --- |
| Exact immutable `FINAL_CANDIDATE_SHA` | **UNFILLED** |
| Candidate manifest/reference supplied by #104 | **UNFILLED** |
| Kickoff/base SHA | **UNFILLED** |
| Included implementation commits | **UNFILLED — list commit and purpose** |
| #107 ancestor verification (`fac88b9`) | **UNFILLED — command and exit status** |
| #108 ancestor verification (`bfd2ce9`) | **UNFILLED — command and exit status** |
| Recorded merge baseline verification (`196ca75`) | **UNFILLED — command and exit status** |
| Supported Claude Code version | **UNFILLED — exact version** |
| Execution boundary | **UNFILLED — OS and native/WSL boundary** |
| Required Monitor endpoint | **UNFILLED — live producer uses the loopback full `/v1/traces` endpoint; fixture dry run uses the same direct receiver endpoint** |
| Required OTLP protocol/signal | **UNFILLED — live: OTLP HTTP/protobuf per-signal trace (`Content-Type: application/x-protobuf`); fixture-only dry run: OTLP JSON direct POST with `Content-Type: application/json` (not the live producer contract)** |
| Hook configuration | **UNFILLED — throwaway project, endpoint, timeout, and `--source claude-code`** |
| Trusted Hook selector | **UNFILLED — source-version or approved schema fingerprint** |
| Content-enabled environment | **UNFILLED — explicit `OTEL_LOG_USER_PROMPTS=1` authorization and related settings** |
| Gate-disabled environment | **UNFILLED — `OTEL_LOG_USER_PROMPTS` unset/`0` observation plan** |
| Verification source expectation | **UNFILLED — real Claude producer plus raw OTLP observation** |
| Verification adapter expectation | **UNFILLED — actual adapter/provenance labels; no fabricated promotion** |
| #109 known defect/question | **UNFILLED — negative case must reject shared trace ID/generic `otel-exact` label** |
| #109 expected classification impact | **UNFILLED** |
| #110 known defect/question | **UNFILLED — record key absent vs present-empty under disabled gate** |
| #110 expected classification impact | **UNFILLED** |
| Exact focused validation commands already run | **UNFILLED — commands and exit statuses** |
| Exact full validation commands already run | **UNFILLED — commands and exit statuses** |
| Confirmation: no post-handoff product commit under same label | **UNFILLED — explicit confirmation from #104** |

Raw prompts, responses, Hook payloads, credentials, PII, absolute user-profile
paths, and raw marker values are not valid record content. Use only sanitized
observations and truncated marker references.

## Candidate gate checklist

- [ ] The candidate SHA is immutable and came with the #104 manifest.
- [ ] `git merge-base --is-ancestor fac88b9 <candidate-sha>` exited `0`.
- [ ] `git merge-base --is-ancestor bfd2ce9 <candidate-sha>` exited `0`.
- [ ] `git merge-base --is-ancestor 196ca75 <candidate-sha>` exited `0`.
- [ ] A clean detached validation worktree was created at exactly the candidate SHA.
- [ ] The clean validation worktree has empty `git status --porcelain` output.
- [ ] A fresh disposable database was initialized after the worktree gate.
- [ ] A fresh throwaway Hook configuration directory was initialized after the worktree gate.
- [ ] A replacement candidate would invalidate and discard all prior final-run results.
- [ ] Final classifications will be recorded only against this frozen SHA.

**Sign-off:** NOT SIGNED OFF — reviewer, date, and candidate SHA remain
unfilled until the Issue #104 handoff is complete.

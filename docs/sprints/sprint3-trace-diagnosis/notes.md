# Sprint3 Notes

## 2026-06-12: scope alignment

- Sprint3 is renamed to Content-aware Trace Diagnosis and Auto-decision Foundation.
- Sprint3 includes trace-driven diagnosis candidate generation, content-aware evidence extraction, improvement proposal candidates, and auto-approval decision records.
- Sprint3 allows explicit opt-in sensitive local output to include real prompt / response content, tool arguments / results, credential, secret, Base64 header, and real user identity.
- Sensitive local output is not repository material and must not be committed.
- Sprint3 does not modify repository files, generate patch / diff, commit, push, create pull requests, or decide experiment winners.
- Repository-changing auto-improvement implementation is deferred to Sprint4 or later, after allowlist, dry-run, diff preview, rollback, test execution, commit boundary, and stop conditions are specified.

## 2026-06-12: source-of-truth correction

- `docs/spec.md` now keeps only the high-level Sprint3 scope and safety boundary.
- Candidate command contracts and schemas remain sprint-local until the open questions are resolved.
- Diagnosis candidate generation will use a candidate-specific command and candidate-specific schema first, then map into M24 records after review.
- Auto-decision will use a separate schema rather than extending the existing M27 human decision record.
- Startup checks passed for Config CLI help, solution build, Aspire AppHost start, and Aspire AppHost stop.

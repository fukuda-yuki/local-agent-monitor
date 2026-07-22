# Issue #87 — Raw Local Replay

This directory records implementation and validation evidence for the explicit
local raw replay profile. Current behavior is canonical in
`docs/specifications/interfaces/raw-local-replay.md`; this directory is
historical evidence only and must never contain a raw archive, raw member,
credential, private retention identity, or sensitive local path.

| Milestone | Status | Evidence |
| --- | --- | --- |
| M1 — Raw local replay | Functional candidate accepted locally; automated and security rows passed; content-authorized live validation blocked on separate operator authorization | [validation matrix](validation-matrix.json) |

## Exact candidate

- Wave 3 kickoff: `c02c10ab18553acef1619ce12ec630f4f6f5aa5f`.
- Functional and validation SHA: `ba4aa770b5691930469510e4c67f5381fe5fbcff`.
- Accepted dependency: Issue #85 functional revision
  `37a095d7c11dd180851e75bdcb290f0894ba01d5`, with evidence revision
  `56c2033257a04751bc52468efa249c4058b20e7d`.
- Branch/worktree: `codex/issue-87-raw-replay` /
  `.worktrees/issue-87-raw-replay`.
- Publication: local-only; no push and no pull request.

## Candidate-pinned validation

All automated cases used synthetic bounded records and disposable local state.
No archive, raw member, credential, private path, or reversible raw identifier
was retained as repository evidence.

| Command or gate | Result |
| --- | --- |
| `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --no-restore --filter FullyQualifiedName~RawReplay` | 52 passed, 0 failed, 0 skipped |
| `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter FullyQualifiedName~RawReplay` | 25 passed, 0 failed, 0 skipped |
| Three initially timed-out Setup barrier cases, isolated | 3 passed, 0 failed, 0 skipped |
| `pwsh scripts\agent\sync-claude-skills.ps1 -Check` | 5 shared skills matched |
| `dotnet build CopilotAgentObservability.slnx` | 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | exit 0 |
| First `dotnet test CopilotAgentObservability.slnx` attempt | Required failure retained: Config CLI 4,571/4,574 passed; three unrelated Setup barrier tests timed out. Instruction Findings 20/20, Alerts 451/451, Doctor 266/266, and Local Monitor 2,655/2,655 passed. |
| Exact full-suite retry after isolated diagnosis | 7,966 passed, 0 failed, 0 skipped |
| Issue #91 matrix validator | 3 rows passed; decision `release_ready_with_external_blockers` |
| Issue #91 scanner self-test | 118 transformation cases and 5 negative cases passed |
| Issue #91 evidence scan | 3 files, 354 variants, 0 matches |

The successful retry does not erase the first required failure. The three
failing tests passed 3/3 when executed alone and the unchanged candidate then
passed the exact full command. No raw-replay code or fixture changed between
those executions; the remaining cause is unverified scheduling starvation in
the concurrent full-solution run.

## External and unverified boundary

Row `91-L-087` is exactly `blocked_external`. Content-enabled producer capture
was not authorized, `OTEL_LOG_USER_PROMPTS=1` was not enabled, and no genuine
producer raw payload was created. Therefore real-producer content/provenance,
restart recovery, and cleanup remain unverified until a separately authorized
bounded run is executed on a newly frozen candidate.

# Sprint16 M5: Validation and closeout

M5 verifies the complete Sprint16 change, records live Canvas runtime evidence
or a precise blocker, and closes the sprint.

## Target files

- Modify: `docs/sprints/sprint16-canvas-cross-repo-adapter/README.md`
- Create: `docs/sprints/sprint16-canvas-cross-repo-adapter/milestones/M5-validation-and-closeout/review.md`
- Create one of:
  - `docs/sprints/sprint16-canvas-cross-repo-adapter/milestones/M5-validation-and-closeout/live-validation.md`
  - `docs/sprints/sprint16-canvas-cross-repo-adapter/milestones/M5-validation-and-closeout/live-validation-blocker.md`
- Modify: `docs/task.md`

## Tasks

- [ ] Run all targeted validation commands from the sprint README after M1-M4
  are complete.
- [ ] Run the required repository validation commands:
  `dotnet build CopilotAgentObservability.slnx`,
  `pwsh scripts\test\install-playwright-chromium.ps1`, and
  `dotnet test CopilotAgentObservability.slnx`.
- [ ] Perform live Canvas runtime validation if the runtime/tools are
  available:
  copy the extension to user scope, reload, open Canvas, verify scope,
  monitor URL/readiness, repository labels/filtering, unknown fallback, and
  bounded actions; repeat the copy/reload/open checks for a project-scoped
  target repository.
- [ ] Use only synthetic traces for live validation. Include one trace with
  `repo.name`, `workspace.name`, and `repo.snapshot`, plus one trace without
  metadata to verify `unknown repository`.
- [ ] Confirm action responses contain only bounded sanitized DTO fields and no
  raw prompt/response body, tool arguments/results, PII, credential, token,
  raw OTLP payload, or local path.
- [ ] If live Canvas runtime tools are unavailable, write
  `live-validation-blocker.md` naming the missing tool/session and exact checks
  still required. Do not treat automated tests as a replacement.
- [ ] Write `review.md` with commands run, pass/fail results, unverified scope,
  data-safety review, and remaining risks.
- [ ] Update the sprint README milestone table from Planned to Implemented or
  Blocked as appropriate.
- [ ] Update `docs/task.md` with Sprint16 final status and evidence links.

## Validation

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Expected: all required repository validation commands pass. If any command is
skipped, fails, or cannot run, record it as unverified scope and name the exact
command that still needs to run.

## Commit

```powershell
git add docs\sprints\sprint16-canvas-cross-repo-adapter docs\task.md
git commit -m "Sprint16 Canvas cross-repo adapter: docs: record validation closeout"
```

# Sprint21 M4: Regression Validation

M4 verifies the additive boundary and records self-review evidence.

## Target Files

- `docs/sprints/sprint21-conversation-scope/milestones/M4-regression-validation/regression-validation.md`
- `docs/sprints/sprint21-conversation-scope/README.md`
- `docs/sprints/sprint21-conversation-scope/Plan.md`

## Tasks

- [ ] Run the pinned validation suite from the repository root.

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Expected:
- build succeeds with 0 errors;
- Playwright Chromium bootstrap succeeds;
- full solution test succeeds with no failed tests.

- [ ] Run a spec consistency self-review.
  - Compare implementation against D048.
  - Compare prompt v4 against
    `docs/specifications/interfaces/instruction-diagnosis-analysis.md`.
  - Confirm no public route/API/SSE/projection migration/Canvas focus change.
  - Confirm `--sanitized-only` behavior is unchanged.
  - Confirm bounded sibling loading is capped and deterministic.
  - Confirm committed files contain no raw prompt/response/tool bodies, PII,
    credentials, provider URLs, or full analysis markdown.

- [ ] Write `regression-validation.md`.
  - Commands run and results.
  - Test counts.
  - Review scope.
  - Findings or "no blocking issues".
  - Residual risks and unverified live scope.

- [ ] Update Sprint21 README and Plan status.

## Evidence Template

Create `regression-validation.md` with this structure:

```markdown
# Sprint21 M4 - Regression Validation Record

Date: YYYY-MM-DD

## Commands

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | ... |
| `pwsh scripts\test\install-playwright-chromium.ps1` | ... |
| `dotnet test CopilotAgentObservability.slnx` | ... |

## Self-Review

- Source-of-truth alignment:
- Boundary checks:
- Data safety:
- Test coverage:

## Residual Risks

- M5 live validation is still required.
```

## Commit

```powershell
git add docs/sprints/sprint21-conversation-scope
git commit -m "Instruction Diagnosis Conversation Scope: docs: record Sprint21 M4 regression validation"
```

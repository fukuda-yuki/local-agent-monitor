# Sprint21 M5: Live Validation And Issue #46 Update

M5 validates that bounded conversation context helps the Issue #46 Phase 2
step-2 problem without reintroducing generic or uncited findings.

## Target Files

- `docs/sprints/sprint21-conversation-scope/milestones/M5-live-validation/live-validation.md`
- `docs/sprints/sprint21-conversation-scope/README.md`
- `docs/sprints/sprint21-conversation-scope/Plan.md`
- `docs/task.md` if the sprint outcome changes roadmap state

## Preconditions

- Preserved Sprint19/Sprint20 validation DB is available, or a deviation is
  explicitly recorded.
- Local Monitor raw-default analysis can run.
- Provider configuration is available and named without recording secrets.
- User is available for human judgment on trace-specificity and category
  coupling.

## Validation Set

Use both:

- Regression set: the six preserved Sprint19/Sprint20 traces where Sprint20
  already established extractor grounding.
- Conversation-window set: at least two analyzed traces where relevant
  clarification, acceptance criteria, or missing context appears in a previous
  or following sibling trace in the same `conversation_id`.

If the preserved DB does not contain enough conversation-window cases, capture
fresh local traces and record the deviation. Do not silently substitute a
different source.

## Gate Criteria

| Gate | Required verdict |
| --- | --- |
| Citation existence | Every cited span/turn/trace id exists in the analyzed trace or emitted bounded sibling window. |
| Trace specificity | Findings do not read as generic prompt advice when moved to another trace. |
| No-evidence-no-finding | No finding appears without extractor or raw-verified evidence. |
| Extractor/raw grounding | Every finding is grounded in `get_instruction_evidence` or an explicitly raw-verified citation. |
| Bounded-window compliance | No finding cites or implies evidence outside `conversation_context.traces[]`. |
| Sibling relationship clarity | Findings using sibling evidence name the sibling trace id and explain before/after relevance to the analyzed trace. |
| Sprint20 regression | Sprint20 valid findings do not materially regress unless the reason is recorded and accepted. |
| Data safety | Repository evidence includes no raw prompt/response/tool bodies, PII, credentials, provider URLs, or full analysis markdown. |

## Tasks

- [ ] Verify the preserved DB and provider setup.
  - Record DB path as a local path only if it contains no user-sensitive
    content beyond repository workspace paths already used in prior evidence.
  - Record provider labels, not secrets or base URLs.

- [ ] Run instruction-diagnosis with prompt v4.
  - Keep full raw analysis results as local runtime data only.
  - Record sanitized observations in `live-validation.md`.

- [ ] Compare against Sprint20.
  - Finding counts and categories.
  - Citation validity.
  - Whether bounded sibling context changed a verdict.
  - Whether old trace-scoped results remained valid where no sibling evidence
    is needed.

- [ ] Apply the gate with user judgment.
  - If failed, iterate prompt wording first.
  - Iterate extractor fields only if the model cannot cite needed evidence
    because the bounded output lacks a required summary.
  - Record every iteration and re-run only affected traces.

- [ ] Draft the Issue #46 update.
  - Repository-safe summary only.
  - Do not post without explicit user confirmation.

- [ ] Update README/Plan/task status and commit.

## Commit

```powershell
git add docs/sprints/sprint21-conversation-scope docs/task.md
git commit -m "Instruction Diagnosis Conversation Scope: docs: record Sprint21 M5 live validation"
```

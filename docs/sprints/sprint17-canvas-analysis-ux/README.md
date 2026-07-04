# Sprint17 Canvas Analysis UX

Sprint17 corrects the scope of the Canvas analysis UX work around the existing
Canvas helper path: `POST /analyze` builds a bounded instruction and calls
`session.send({ prompt })`. It does not replace that path with the Local Monitor
Copilot raw analysis runner.

## Scope

- Add Local Monitor `GET /api/analysis/options` as a sanitized configuration
  source for Canvas helper controls.
- Add Canvas helper controls for requested profile, requested model, requested
  reasoning effort, and timeout hint.
- Include those requested values in the generated `session.send()` prompt and
  in `/analyze` dispatch metadata.
- Improve helper-page dispatch progress so the UI says what is actually
  happening: preparing and sending a Copilot instruction, then handing off to
  Copilot chat.

## Non-goals

- Do not start the Local Monitor raw analysis runner from Canvas helper
  `/analyze`.
- Do not claim per-message model, reasoning, or execution-timeout enforcement.
- Do not add a new Canvas action, telemetry input, raw endpoint, raw JSON API,
  dependency, or repository-stored analysis result.
- Do not record final analysis result metadata unless a later sprint safely
  correlates it from observed telemetry.

## Milestones

| Milestone | Goal |
| --- | --- |
| M1 | Scope correction and canonical spec updates |
| M2 | Local Monitor sanitized analysis options endpoint |
| M3 | Canvas helper requested option controls |
| M4 | Prompt requested metadata and dispatch metadata |
| M5 | Dispatch-oriented progress state |
| M6 | Tests, docs, validation, and live Canvas handoff |

## Validation

Required automated validation remains:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Live Canvas runtime validation remains a final handoff step because it requires
GitHub Copilot Canvas runtime tools outside normal repository validation.

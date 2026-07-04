# Sprint16 M4: Helper cross-repo UX

M4 updates the Canvas helper page so cross-repo users can distinguish traces by
sanitized repository/workspace labels and manually filter them. It must not
change the raw-preview boundary or turn the helper page into a replacement
Local Monitor UI.

## Target files

- Modify: `.github/extensions/otel-monitor-canvas/extension.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`
- Create: `docs/sprints/sprint16-canvas-cross-repo-adapter/milestones/M4-helper-cross-repo-ux/review.md`

## Tasks

- [ ] Update `compactTrace` to include `repository_name`, `workspace_label`,
  and `repo_snapshot` from Local Monitor DTOs. Keep forbidden-key sanitization
  for Canvas action DTOs intact.
- [ ] Add pure helper functions for display labels, for example:
  `repositoryLabel(trace)`, `workspaceLabel(trace)`, and
  `repositoryFilterKey(trace)`. Missing repository metadata must display as
  `unknown repository`.
- [ ] Update trace line formatting so repository label appears near the front
  of the existing decision-supporting line. Keep the existing prompt-label
  composition additive.
- [ ] Add helper-page top-row display for extension scope, monitor URL, and
  readiness. Scope may be `project`, `user`, or `unknown`; never display
  `process.cwd()`, an absolute path, a user profile path, or a copied
  extension filesystem location.
- [ ] Add client-side repository/workspace filter controls populated from
  `/api/traces` results. Filtering is local to the helper page; do not add a
  new Local Monitor query parameter or Canvas action.
- [ ] Keep current loopback monitor URL rejection, helper-server token
  protection, `/api/summary`, `/api/trace-detail/:traceId`, `/raw-preview`,
  and `session.send()` behavior unchanged except for display labels.
- [ ] Do not add current-repository auto-match. The accepted behavior is manual
  labels/filtering unless a future runtime provides stable repo context.
- [ ] Extend JS tests for labels, unknown fallback, filter option generation,
  and no path leakage in rendered HTML.
- [ ] Extend contract tests so the helper declares cross-repo label/filter
  surfaces and still does not emit raw/PII/path data, `console.log`, or new
  dependency files.
- [ ] Write `review.md` confirming no metadata flows into analysis prompts,
  logs, or raw-preview JSON, and that helper UI remains token-protected.

## Validation

```powershell
node --check .github\extensions\otel-monitor-canvas\extension.mjs
node --check .github\extensions\otel-monitor-canvas\canvas-helpers.mjs
node --test .github\extensions\otel-monitor-canvas\canvas-helpers.test.mjs
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```

Expected: JS smoke and Canvas contract tests pass.

## Commit

```powershell
git add .github\extensions\otel-monitor-canvas tests\CopilotAgentObservability.LocalMonitor.Tests\CanvasExtensionContractTests.cs docs\sprints\sprint16-canvas-cross-repo-adapter\milestones\M4-helper-cross-repo-ux
git commit -m "Sprint16 Canvas cross-repo adapter: feat: add helper repo filters"
```

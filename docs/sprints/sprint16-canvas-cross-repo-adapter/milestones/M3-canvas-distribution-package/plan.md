# Sprint16 M3: Canvas distribution package

M3 makes `.github/extensions/otel-monitor-canvas/` self-describing and copyable
without adding dependencies or duplicating the extension source.

## Target files

- Create: `.github/extensions/otel-monitor-canvas/canvas.json`
- Create: `.github/extensions/otel-monitor-canvas/README.md`
- Create: `.github/extensions/otel-monitor-canvas/assets/preview.png`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`
- Create: `docs/sprints/sprint16-canvas-cross-repo-adapter/milestones/M3-canvas-distribution-package/review.md`

## Tasks

- [ ] Add `canvas.json` with stable metadata:
  id `otel-monitor`, a descriptive name, a short description, semantic version,
  keywords for Local Monitor / OpenTelemetry / diagnostics, and a screenshot
  entry pointing to `assets/preview.png`.
- [ ] Add `README.md` inside the extension folder. Include:
  user-scope copy destination, project-scope copy destination, Local Monitor
  startup prerequisite, monitor URL expectation, reload/restart instructions,
  and a warning not to commit runtime artifacts, raw telemetry, DB files, logs,
  screenshots with real data, or local machine paths.
- [ ] Create `assets/preview.png` from synthetic mock data only. It must not
  contain real trace ids, real prompts, real workspace paths, user profile
  paths, email addresses, credentials, tokens, raw OTLP payload, or local DB/log
  paths.
- [ ] Extend `CanvasExtensionContractTests` to assert the distribution package
  exists, the screenshot path resolves inside the extension folder, and no
  package manifest, lockfile, `node_modules`, raw fixture, local path, or
  credential-like string was added.
- [ ] Keep `.github/extensions/otel-monitor-canvas/extension.mjs`,
  `canvas-helpers.mjs`, and `canvas-helpers.test.mjs` as the executable source.
  Do not create a mirror folder.
- [ ] Write `review.md` listing the files checked for data safety and recording
  that preview content is synthetic.

## Validation

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
node --check .github\extensions\otel-monitor-canvas\extension.mjs
node --check .github\extensions\otel-monitor-canvas\canvas-helpers.mjs
node --test .github\extensions\otel-monitor-canvas\canvas-helpers.test.mjs
```

Expected: contract tests and JS smoke pass.

## Commit

```powershell
git add .github\extensions\otel-monitor-canvas tests\CopilotAgentObservability.LocalMonitor.Tests\CanvasExtensionContractTests.cs docs\sprints\sprint16-canvas-cross-repo-adapter\milestones\M3-canvas-distribution-package
git commit -m "Sprint16 Canvas cross-repo adapter: docs: package canvas extension"
```

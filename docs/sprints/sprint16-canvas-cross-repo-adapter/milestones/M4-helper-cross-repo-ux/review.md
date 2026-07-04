# Sprint16 M4 self-review

## Scope reviewed

Sprint16 M4 updates the Canvas helper page for cross-repo use. It adds
sanitized repository/workspace labels, an `unknown repository` fallback, local
repository/workspace filtering, and extension scope display without changing
Local Monitor APIs or Canvas raw boundaries.

## Files checked

- `.github/extensions/otel-monitor-canvas/extension.mjs`
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`

## Behavior implemented

- `compactTrace` now carries the Sprint16-approved `repository_name`,
  `workspace_label`, and `repo_snapshot` fields.
- Helper trace lines include the repository label near the front, preserving the
  existing prompt-label composition as an additive helper-page display.
- Missing repository metadata renders as `unknown repository`.
- The helper page displays extension scope, monitor URL, readiness, and a
  local-only repository/workspace filter populated from `/api/traces`.
- Scope display is limited to `project`, `user`, or `unknown`; filesystem paths
  are not rendered.

## Boundary review

No metadata is added to `buildAnalysisPrompt`, `session.send()` payloads, raw
preview JSON, logs, package manifests, lockfiles, dependency files, or mirror
extension folders. The raw-preview route remains the only authorized raw route
reference and remains page-navigation-only. Helper routes keep the existing
per-launch token gate and loopback monitor URL validation.

## Validation

Ran on 2026-07-02:

```powershell
node --check .github\extensions\otel-monitor-canvas\extension.mjs
node --check .github\extensions\otel-monitor-canvas\canvas-helpers.mjs
node --test .github\extensions\otel-monitor-canvas\canvas-helpers.test.mjs
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```

Results:

- `node --check` passed for `extension.mjs`.
- `node --check` passed for `canvas-helpers.mjs`.
- `node --test canvas-helpers.test.mjs` passed: 25 passed, 0 failed.
- Canvas extension contract tests passed: 17 passed, 0 failed, 0 skipped.
- `dotnet build CopilotAgentObservability.slnx` passed with 0 warnings and
  0 errors.
- `pwsh scripts\test\install-playwright-chromium.ps1` exited 0.
- `dotnet test CopilotAgentObservability.slnx` passed: 301 ConfigCli tests and
  320 LocalMonitor tests, 621 total, 0 failed, 0 skipped.

Validation caveat:

- A later rerun of the exact targeted command
  `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests`
  failed during NuGet restore while fetching repository signature metadata from
  `https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json`
  (`NU1301`, SSL authentication / no credentials available). The same test set
  was still covered by the successful full solution test above.

## Findings

No blocking issues found. Live user-scope/project-scope Canvas runtime
validation remains M5 scope.

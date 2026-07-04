# Sprint16 M2 self-review

## Scope reviewed

Sprint16 M2 implements nullable, sanitized repository metadata in the Local
Monitor projection and sanitized monitor trace DTOs.

## Files checked

- `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorRecordProjection.cs`
- `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorProjectionBuilder.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/MonitorProjectionRows.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`
- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/`
- `tests/CopilotAgentObservability.ConfigCli.Tests/RawTelemetryStoreTests.cs`

## Behavior implemented

- Monitor projection schema version is now `3`.
- `monitor_traces` has nullable `repository_name`, `workspace_label`, and
  `repo_snapshot` columns.
- New DBs create the columns directly; existing v1/v2 monitor databases are
  upgraded additively with `ALTER TABLE ADD COLUMN`.
- Existing projected rows are not backfilled; migrated rows keep null metadata
  until future projections explicitly provide sanitized values.
- Trace upsert fills null metadata from future records and preserves existing
  non-null metadata with `COALESCE(existing, excluded)`.
- `/api/monitor/traces` and trace objects embedded in `/api/monitor/summary`
  expose the three sanitized fields through the existing compact trace DTO.

## Sanitizer and boundary review

The implementation reads only the Sprint16-approved OTLP Resource Attributes:
`repo.name`, `workspace.name`, and `repo.snapshot`. Each value passes through
the existing `MeasurementSanitizer.SanitizeFreeFormName` guard. Blank,
path-like, email-like, bearer/basic auth-like, secret/password-like, prompt /
response / content-like, tool argument/result-like, and overlong values are
dropped or bounded by the existing sanitizer behavior.

No raw OTLP payload, raw prompt/response body, tool arguments/results, user id,
email, credential, token, or local path field was added to projection DTOs or
monitor API responses. Measurement, dashboard, diagnosis candidate, raw record,
span projection, Canvas packaging, and Canvas helper UX schemas were not
changed.

## Validation

Ran on 2026-07-02:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionBuilderTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionStoreTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionApiTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSummaryEndpointTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSpanProjectionStoreTests
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~RawTelemetryStoreTests
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Results:

- Targeted Local Monitor and RawTelemetryStore tests passed.
- Build passed with 0 warnings and 0 errors.
- Playwright Chromium install wrapper exited 0.
- Full solution tests passed: 301 ConfigCli tests and 316 LocalMonitor tests,
  617 total, 0 failed, 0 skipped.

## Findings

No blocking issues found. M2 does not implement the Canvas helper fallback text
(`unknown repository`) or repository/workspace filtering; those remain M4 scope.

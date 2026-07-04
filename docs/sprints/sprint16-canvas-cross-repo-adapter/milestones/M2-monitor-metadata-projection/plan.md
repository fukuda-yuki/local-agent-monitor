# Sprint16 M2: Monitor metadata projection

M2 implements sanitized repository/workspace metadata in the Local Monitor
projection and sanitized API DTOs. It must not change normalized measurement,
candidate, dashboard, or repository-safe dataset schemas.

## Target files

- Modify: `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorRecordProjection.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorProjectionBuilder.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/MonitorProjectionRows.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify tests under `tests/CopilotAgentObservability.LocalMonitor.Tests/`
- Create: `docs/sprints/sprint16-canvas-cross-repo-adapter/milestones/M2-monitor-metadata-projection/review.md`

## Tasks

- [ ] Write failing projection-builder tests for:
  `repo.name` -> `repository_name`, `workspace.name` -> `workspace_label`,
  `repo.snapshot` -> `repo_snapshot`, multi-trace fan-out, missing metadata,
  and unsafe values being dropped to null.
- [ ] Add nullable metadata properties to `MonitorTraceContribution` and
  `MonitorRecordProjection` flow. Keep `MeasurementRow` unchanged.
- [ ] In `MonitorProjectionBuilder`, extract resource attributes per trace from
  raw OTLP resource spans using the existing OTLP reader/resource-attribute
  helpers. Use the existing free-form-name sanitizer and store null when a
  value is path-like, email-like, secret-like, credential-like, or otherwise
  unsafe.
- [ ] Add nullable columns to `MonitorTraceRow`: `RepositoryName`,
  `WorkspaceLabel`, and `RepoSnapshot`.
- [ ] Bump the monitor projection schema version to 3. Add
  `repository_name`, `workspace_label`, and `repo_snapshot` to new table
  creation and additive migration with `ALTER TABLE ADD COLUMN`.
- [ ] Update projection upsert SQL so null existing metadata can be filled by
  future records, while non-null existing values are preserved with
  `COALESCE(existing, excluded)`.
- [ ] Update `ListMonitorTraces`, `GetMonitorTrace`, and row mapping to include
  the three new columns.
- [ ] Update `MonitorHost` trace DTO serialization so `/api/monitor/traces` and
  trace objects inside `/api/monitor/summary` include the sanitized fields.
- [ ] Update tests for API shape, migration from older schema, DTO allowlist,
  and summary highlight trace objects. Assert no raw payload, prompt body,
  local path, user profile path, token, or unsafe value is returned.
- [ ] Write `review.md` with the sanitizer reasoning, schema migration result,
  and confirmation that measurement/dashboard schemas were not changed.

## Validation

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionBuilderTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionApiTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSummaryEndpointTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionStoreTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSpanProjectionStoreTests
```

Expected: all targeted Local Monitor tests pass.

## Commit

```powershell
git add src\CopilotAgentObservability.Telemetry src\CopilotAgentObservability.Persistence.Sqlite src\CopilotAgentObservability.LocalMonitor tests\CopilotAgentObservability.LocalMonitor.Tests docs\sprints\sprint16-canvas-cross-repo-adapter\milestones\M2-monitor-metadata-projection
git commit -m "Sprint16 Canvas cross-repo adapter: feat: project monitor metadata"
```

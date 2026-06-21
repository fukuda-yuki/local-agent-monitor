# Raw Local Receiver Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the Sprint7 `raw-local-receiver` foreground receiver path that accepts local OTLP HTTP traces and writes them into the existing raw store without changing downstream schemas.

**Architecture:** Keep the first receiver inside `CopilotAgentObservability.ConfigCli` to reuse the existing internal raw store and normalization path. Add a small `RawLocalReceiver` module with option parsing, loopback validation, request handling, OTLP protobuf-to-JSON conversion, and a thin HTTP host. Preserve the raw store schema by storing receiver records as `source=raw-otlp`.

**Tech Stack:** .NET 10 console project, xUnit tests, `System.Net.HttpListener` or an equivalent built-in HTTP server only, `System.Text.Json`, existing `Microsoft.Data.Sqlite`.

---

## File Structure

- Modify `docs/specifications/layers/telemetry-ingestion.md`: receiver host model, endpoint, protobuf requirement, HTTP behavior.
- Modify `docs/specifications/interfaces/config-cli.md`: `serve-raw-local-receiver` command contract.
- Modify `docs/sprints/sprint7-local-raw-receiver/milestones/M2-receiver-host-model/task.md`: selected host model and rejected alternatives.
- Modify `docs/sprints/sprint7-local-raw-receiver/notes.md`: implementation-time warnings and shared findings.
- Modify `src/CopilotAgentObservability.ConfigCli/Cli/CliApplication.cs`: dispatch `serve-raw-local-receiver` and unblock `raw-local-receiver` profile output.
- Modify `src/CopilotAgentObservability.ConfigCli/Cli/CliHelpText.cs`: command usage.
- Modify `src/CopilotAgentObservability.ConfigCli/Shared/ConfigSamples.cs`: raw-local-receiver profile output.
- Modify `src/CopilotAgentObservability.ConfigCli/RawTelemetry/RawOtlpIngestor.cs`: add payload-string record creation.
- Create `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverOptions.cs`: parse `--db` and `--url`.
- Create `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHandler.cs`: deterministic request handling and persistence.
- Create `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/OtlpProtobufTraceConverter.cs`: convert the required OTLP trace protobuf subset into the existing JSON envelope shape.
- Create `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs`: foreground HTTP loop.
- Create `tests/CopilotAgentObservability.ConfigCli.Tests/RawLocalReceiverOptionsTests.cs`.
- Create `tests/CopilotAgentObservability.ConfigCli.Tests/RawLocalReceiverHandlerTests.cs`.
- Create `tests/CopilotAgentObservability.ConfigCli.Tests/OtlpProtobufTraceConverterTests.cs`.
- Create or extend `tests/CopilotAgentObservability.ConfigCli.Tests/RawLocalReceiverIntegrationTests.cs`.
- Modify `tests/CopilotAgentObservability.ConfigCli.Tests/CliApplicationTests.cs`.
- Modify `tests/CopilotAgentObservability.ConfigCli.Tests/ConfigSamplesTests.cs`.

### Task 1: M2 Specs And Shared Notes

**Files:**
- Modify: `docs/specifications/layers/telemetry-ingestion.md`
- Modify: `docs/specifications/interfaces/config-cli.md`
- Modify: `docs/sprints/sprint7-local-raw-receiver/milestones/M2-receiver-host-model/task.md`
- Modify: `docs/sprints/sprint7-local-raw-receiver/notes.md`

- [x] **Step 1: Record the initial host model**

Add the foreground command:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- serve-raw-local-receiver --db data\raw-store.db --url http://127.0.0.1:4319
```

- [x] **Step 2: Record the protobuf requirement**

State that `/v1/traces` must accept OTLP HTTP protobuf because VS Code Copilot Chat defaults `otlp-http` to HTTP/protobuf.

- [x] **Step 3: Record implementation-time findings**

Append warnings and unresolved risks to `docs/sprints/sprint7-local-raw-receiver/notes.md`.

### Task 2: Receiver Options

**Files:**
- Create: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverOptions.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/RawLocalReceiverOptionsTests.cs`

- [ ] **Step 1: Write failing option tests**

Cover defaults, explicit `--db`, explicit `--url`, duplicate options, missing option values, unknown options, and non-loopback URL rejection.

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~RawLocalReceiverOptionsTests
```

Expected: fail because the test class or production type does not exist.

- [ ] **Step 2: Implement minimal option parsing**

Add defaults:

```text
DatabasePath = data/raw-store.db
Url = http://127.0.0.1:4319
```

Reject non-HTTP URLs and any host that is not `localhost`, `127.0.0.1`, or `[::1]`.

- [ ] **Step 3: Verify options**

Run the same targeted test. Expected: pass.

### Task 3: Raw OTLP Payload Record Factory

**Files:**
- Modify: `src/CopilotAgentObservability.ConfigCli/RawTelemetry/RawOtlpIngestor.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/RawOtlpIngestorTests.cs`

- [ ] **Step 1: Write a failing payload-string test**

Test that a synthetic JSON payload string creates a `RawTelemetryRecord` with the same trace id and resource attributes as the file-based path.

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~RawOtlpIngestorTests
```

Expected: fail because `CreateRecordFromPayload` does not exist.

- [ ] **Step 2: Implement payload-string creation**

Share the existing envelope validation, trace id extraction, and resource attribute extraction.

- [ ] **Step 3: Verify ingestor tests**

Run the same targeted test. Expected: pass.

### Task 4: OTLP Protobuf Trace Conversion

**Files:**
- Create: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/OtlpProtobufTraceConverter.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/OtlpProtobufTraceConverterTests.cs`

- [ ] **Step 1: Write failing protobuf conversion tests**

Build a small synthetic binary `ExportTraceServiceRequest` in the test helper with one resource span, one scope span, and one span containing:

```text
trace_id = 11111111111111111111111111111111
span_id = 2222222222222222
name = chat gpt-4o
start_time_unix_nano = 1000000000
end_time_unix_nano = 1500000000
resource attributes: client.kind=vscode-copilot-chat, experiment.id=baseline
span attributes: gen_ai.operation.name=chat, gen_ai.usage.input_tokens=10, gen_ai.usage.output_tokens=5
```

Assert the converter emits JSON with top-level `resourceSpans`, nested `scopeSpans`, `spans`, `traceId`, `spanId`, `name`, timing fields, and OTLP JSON-style `attributes`.

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~OtlpProtobufTraceConverterTests
```

Expected: fail because converter does not exist.

- [ ] **Step 2: Implement the minimal protobuf reader**

Implement only the OTLP trace fields required by the test and existing normalizer: resource attributes, scope spans, spans, span attributes, events, status, trace id, span id, name, kind, start/end time.

- [ ] **Step 3: Verify conversion tests**

Run the same targeted test. Expected: pass.

### Task 5: Request Handler And Store Integration

**Files:**
- Create: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHandler.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/RawLocalReceiverHandlerTests.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/RawLocalReceiverIntegrationTests.cs`

- [ ] **Step 1: Write failing handler tests**

Cover:

```text
POST /v1/traces application/json persists one raw record.
POST /v1/traces application/x-protobuf persists one raw record after conversion.
POST /v1/traces invalid JSON returns failure and writes no record.
GET /v1/traces returns method failure and writes no record.
POST /v1/metrics returns unsupported signal failure and writes no record.
POST /v1/traces text/plain returns unsupported content type and writes no record.
```

- [ ] **Step 2: Implement handler**

Return a small result object with status code, content type, response body, and persisted raw record id. Do not expose raw payload content in the response.

- [ ] **Step 3: Write failing normalize integration test**

Use handler output written to a temp SQLite DB, then run:

```csharp
CliApplication.Run(["normalize-raw", dbPath, "--json", measurementsPath], output, error);
```

Assert the measurement JSON has `client_kind=vscode-copilot-chat`, `experiment_id=baseline`, token counts, and one trace row.

- [ ] **Step 4: Verify handler and integration tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~RawLocalReceiverHandlerTests|FullyQualifiedName~RawLocalReceiverIntegrationTests"
```

Expected: pass.

### Task 6: CLI Host And Profile Output

**Files:**
- Modify: `src/CopilotAgentObservability.ConfigCli/Cli/CliApplication.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Cli/CliHelpText.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Shared/ConfigSamples.cs`
- Create: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/CliApplicationTests.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/ConfigSamplesTests.cs`

- [ ] **Step 1: Write failing CLI and profile tests**

Assert:

```text
serve-raw-local-receiver --url http://0.0.0.0:4319 returns non-zero before binding.
profile-vscode-env --profile raw-local-receiver succeeds and points to http://127.0.0.1:4319.
raw-local-receiver profile output does not contain Authorization=Basic, x-langfuse-ingestion-version, <langfuse-host>, or <collector-host>.
```

- [ ] **Step 2: Implement profile output**

Emit `CAO_COLLECTION_PROFILE=raw-local-receiver`, clear Langfuse/Collector headers, enable OTel, set the receiver endpoint, set `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, and include required resource attributes.

- [ ] **Step 3: Implement foreground host dispatch**

Wire `serve-raw-local-receiver` to options parsing and the HTTP host. Keep host code thin; do not unit-test long-running blocking behavior through `CliApplication.Run`.

- [ ] **Step 4: Verify CLI/profile tests**

Run the relevant targeted tests. Expected: pass.

### Task 7: Full Validation And Sprint Records

**Files:**
- Modify: `docs/sprints/sprint7-local-raw-receiver/README.md`
- Modify: `docs/sprints/sprint7-local-raw-receiver/milestones/M3-local-otlp-http-receiver/task.md`
- Modify: `docs/sprints/sprint7-local-raw-receiver/milestones/M4-raw-store-integration/task.md`
- Modify: `docs/sprints/sprint7-local-raw-receiver/milestones/M5-vscode-direct-telemetry-validation/task.md`
- Modify: `docs/sprints/sprint7-local-raw-receiver/milestones/M6-review-and-release-boundary/task.md`

- [ ] **Step 1: Run full validation**

Run:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Expected: pass. Record any warnings in `docs/sprints/sprint7-local-raw-receiver/notes.md`.

- [ ] **Step 2: Run synthetic smoke validation**

Run the receiver against a temp DB, post synthetic JSON and protobuf trace payloads from a local script or test helper, then run `normalize-raw` on the DB.

- [ ] **Step 3: Update sprint records**

Mark M2 complete, record M3/M4 synthetic evidence, document the M5 live validation procedure and any unconfirmed live items, and add M6 release boundary notes for IIS/IIS Express, packaged exe, tray app, and Windows Service as future work.

- [ ] **Step 4: Review and commit**

Review diffs for raw data safety, generated artifacts, and unrelated changes. Commit coherent completed steps with messages starting `Sprint7 M2-M7`.

# Sprint11 M3/M4 Review

Scope reviewed:

- M3 minimal Canvas actions: `monitor_health`, `list_recent_traces`,
  `get_trace_summary`.
- M4 trace-analysis Canvas actions: `get_trace_span_tree`,
  `get_cache_summary`.
- Project-scoped extension:
  `.github/extensions/otel-monitor-canvas/extension.mjs`.

Implementation notes:

- The extension still had only the M2 scaffold at start, so M3 and M4 were
  implemented together.
- Actions use the existing sanitized Local Monitor surfaces only:
  `/health/ready`, `/api/monitor/traces`, and
  `/api/monitor/traces/{traceId}/spans`.
- Output DTOs are bounded: recent trace output is limited to 50 rows, span
  reads use one 200-row monitor API page, trace tree output returns at most 50
  nodes, and cache summary returns at most 50 turns.
- `get_trace_span_tree` returns a hierarchy when `span_id` /
  `parent_span_id` links are complete and a diagnostic flat list when parent
  links are missing or incomplete.
- `get_cache_summary` uses sanitized LLM/chat spans only and computes
  `cache_hit_rate` from cache-read input tokens over input tokens; raw prefix
  diffing and `conversation_id` cross-trace stitching remain out of scope.
- A final DTO guard drops forbidden key families if an accidental raw-bearing
  field is introduced before returning action output.

Validation run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
node --check .github\extensions\otel-monitor-canvas\extension.mjs
rg -n "/raw|raw_record_id|payload_json|prompt|arguments|results|user_email|user_id|console\.log" .github\extensions\otel-monitor-canvas
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~IngestionWriterWorkerTests.Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown
```

Results:

- M3/M4 contract tests passed: 4/4.
- `node --check` passed.
- Static forbidden-surface scan found only the extension's final forbidden-key
  guard expression.
- `dotnet build CopilotAgentObservability.slnx` passed with 0 warnings and 0
  errors.
- User-provided Windows PowerShell validation showed the required generated
  Playwright script completed and allowed the run to proceed to `dotnet test`.
- `dotnet test CopilotAgentObservability.slnx` initially reproduced
  `IngestionWriterWorkerTests.Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown`
  under full assembly load. The root cause was a startup/shutdown race where
  `StartAsync` could return before the writer reader loop was active, allowing
  immediate shutdown to leave accepted items undrained under scheduler load.
  `IngestionWriterWorker.StartAsync` now waits for the reader loop to start, and
  the shutdown-drain test waits for accepted request completions with a bounded
  timeout.
- After the fix, `dotnet test CopilotAgentObservability.slnx` passed: 554/554
  tests (300 ConfigCli + 254 LocalMonitor).
- Canvas runtime validation tools (`extensions_reload`, `extensions_manage`,
  `list_canvas_capabilities`, `open_canvas`, `invoke_canvas_action`) were not
  callable in this Codex surface, so live Canvas action validation remains
  unverified.

Review finding:

- No product-spec conflict was found. The implementation stays within the
  Sprint11 boundary: no new telemetry input, monitor endpoint, SQLite schema,
  dependency, raw-bearing route, or committed runtime artifact.

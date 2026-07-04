# Sprint8 Pre-Implementation Code Review

This is a preserved review record created before starting Sprint8
(Local Raw Receiver Monitor, issue #25). It is repository guidance and review
evidence, not product behavior. Do not treat findings here as new product
specifications. Promote any durable behavior decision to `docs/requirements.md`,
`docs/spec.md`, or `docs/specifications/` before implementation depends on it.

## Scope Reviewed

Static read-through of the existing `CopilotAgentObservability.ConfigCli`
sources, focused on the surfaces Sprint8 will extract and build on:

- `RawLocalReceiver/` (Sprint7 foreground OTLP HTTP receiver host and handler)
- `RawTelemetry/` (raw store, OTLP ingestor, attribute converter, normalizer)
- `Measurements/` (measurement sanitizer)
- `Cli/` (command dispatch)
- `Shared/` (CSV helpers, global usings)

Source size at review time: 72 source files / ~11,800 lines under `src/`,
24 test files under `tests/`.

## Validation Commands And Results

Required validation could NOT be run in this review environment.

- `dotnet build CopilotAgentObservability.slnx`: **not run** — no .NET SDK is
  installed and `global.json` pins `10.0.203`.
- `dotnet test CopilotAgentObservability.slnx`: **not run** — same reason.
- SDK install was attempted via `dot.net/v1/dotnet-install.sh` but blocked:
  the environment network egress policy does not allow
  `builds.dotnet.microsoft.com`.

Therefore every finding below is from static reading only. None of the findings
were reproduced by executing code, and no code changes were made as part of
this review. The last recorded green validation is in the Sprint7 README
(2026-06-21: build 0 errors, test 290 passing).

## Findings

Severity is the reviewer's static assessment. By decision recorded with this
review, findings are documented only; the receiver host code was not modified.

### B1 (High) — Unbounded request body in receiver host

`src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs:44-45`

```csharp
using var bodyStream = new MemoryStream();
request.InputStream.CopyTo(bodyStream);
```

The whole request body is copied into memory with no size cap. `HttpListener`
imposes no default body-size limit, so a single large POST can exhaust process
memory. The receiver binds to loopback only, but it is still a long-running
local process that an oversized local payload can crash.

Sprint8 lists "request body size limit" and an explicit "request too large"
failure as required (issue #25, Scope §2 and Safety Boundary). The current
Sprint7 host does not implement either. Expected to be resolved when the
ASP.NET Core host is introduced (M2), with `MaxRequestBodySize` plus a
deterministic `413`/`request_too_large` error.

### B2 (High) — One failed request can stop the whole receiver

`src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs:24-39, 41-63`

The accept loop only guards `catch (HttpListenerException) when (stopping)`.
Any other exception raised inside `HandleContext` propagates out of `Run` and
terminates the receiver process. Realistic triggers:

- a client aborting mid-upload, surfacing as `IOException` during
  `request.InputStream.CopyTo(...)`;
- a failure while writing the response (`response.OutputStream.Write(...)`).

A single misbehaving local client can take down ingestion for everyone. This
contradicts the Sprint8 acceptance criterion that a post-persist failure must
not lose raw records and that the receiver should keep running and recover.
Expected resolution in M2/M3: per-request try/catch isolating one request's
failure, with the queue/worker model continuing on error.

### B3 (Low) — Shutdown flag has no memory barrier

`src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs:16-35`

`stopping` is written from the `Console.CancelKeyPress` handler thread and read
by the loop condition and the `when (stopping)` exception filter without
`volatile`/`Volatile.Read` or other synchronization. On a weak memory model the
filter may observe `false` during shutdown, rethrowing the expected
`HttpListenerException` and crashing on stop. Low impact (shutdown path only).

### T4 (Tech debt) — Duplicated OTLP attribute conversion

`src/CopilotAgentObservability.ConfigCli/RawTelemetry/OtlpAttributeConverter.cs`
vs
`src/CopilotAgentObservability.ConfigCli/RawTelemetry/RawOtlpIngestor.cs:98-193`

`ConvertAttributeValue` / `ConvertArrayValue` / `ConvertKeyValueList` are
near-identical copies (~50 lines) in both files. A schema or type-handling
change risks being applied to only one copy. Directly in scope for Sprint8 M1
"Shared component extraction"; consolidating into the new
`CopilotAgentObservability.Telemetry` project removes the duplication.

### T5 (Tech debt) — DDL executed on every POST

`src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHandler.cs:50-52`

Each request constructs a fresh `RawTelemetryStore` and calls `CreateSchema()`
(CREATE TABLE / three CREATE INDEX statements) before insert. Schema creation
per request is wasteful and reopens a connection each time. Sprint8's
`IngestionWriterWorker` (single writer, serialized SQLite writes, schema set up
once at startup) is the intended fix.

### T6 (Tech debt) — List queries read all payloads into memory

`src/CopilotAgentObservability.ConfigCli/RawTelemetry/RawTelemetryStore.cs:78-111`

`ListRecords()` selects every row including full `payload_json`. `normalize-raw`
materializes all payloads in memory. Sprint8 explicitly requires that list
retrieval not load all raw payloads into memory and adds cursor pagination plus
sanitized monitor projections (`monitor_ingestions` / `monitor_traces`). The
existing `ListRecords()` should not back the monitor list endpoints.

### T7 (Tech debt, minor) — Single-threaded synchronous accept loop

`src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs:26-30`

`GetContext()` handles one request at a time synchronously; concurrent clients
serialize behind the OS accept backlog. Throughput only, not correctness.
Sprint8's bounded-channel + background worker model supersedes this loop.

## Surfaces Reviewed With No Blocking Findings

- `MeasurementSanitizer` — unsafe-key and unsafe-value checks (email regex,
  bearer/basic/authorization, prompt/response/content/tool markers,
  user/identity keys) read as reasonable; recursive node sanitization drops
  unsafe values and empties.
- `OtlpProtobufTraceConverter` and its hand-written `ProtoReader` — varint has a
  10-byte cap, length-delimited and fixed64/fixed32 reads validate remaining
  length, unknown wire types throw. No obvious out-of-bounds or infinite-loop
  path found by reading.
- `CsvLineParser` / `CsvEscaper` — quote handling, escaped quotes, and
  unterminated-quote detection look correct.
- `CliApplication` dispatch — consistent option parse → file existence →
  typed exception handling → exit code pattern across commands.

These were not executed; "no blocking findings" means no defect was identified
by static reading, not that they were validated.

## Residual Risk And Unverified Scope

- No build or test was run; all findings are unverified by execution.
- Sprint8's Prerequisite (live VS Code GitHub Copilot Chat OTLP/protobuf send
  to the receiver, end-to-end) cannot be performed in this environment and
  remains unconfirmed, consistent with the Sprint7 README "Still unconfirmed"
  list.
- B1/B2 describe real availability/robustness risk in the current Sprint7
  foreground host. Per the decision recorded with this review, they are left as
  documented known issues to be absorbed by the Sprint8 ASP.NET Core host
  (M2/M3) rather than patched in the HttpListener host.

## Recommended Sprint8 Entry Point

Start at M1 (shared component extraction into `Telemetry` and
`Persistence.Sqlite`). It is the lowest-risk first step, is independently
buildable/testable, and naturally retires findings T4–T6. Defer M2+ until a
build/test-capable environment is available so each milestone can be validated.

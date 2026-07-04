# Sprint8 Handoff — Validated Findings & Fix Worklist

Self-contained handoff for a fresh agent session. You do NOT need any prior
conversation context to act on this. It complements
[`pre-implementation-review.md`](pre-implementation-review.md): that file is the
original static review; this file records what was **confirmed by execution**
once a .NET SDK became available, plus a concrete fix plan per finding.

This is repository guidance / review evidence, not product behavior. Do not
treat anything here as a new product specification. Promote any durable behavior
decision to `docs/requirements.md`, `docs/spec.md`, or `docs/specifications/`
before implementation depends on it.

## Environment (verified working)

- .NET SDK present: `10.0.300-preview.0.26177.108`; `global.json` pins
  `10.0.203` with `rollForward: latestFeature` + `allowPrerelease`, so the
  preview SDK satisfies it.
- Platform: Windows. Shell: PowerShell (primary) / Bash both available.
- Solution: `CopilotAgentObservability.slnx` at repo root.

## Validation status (executed, not static)

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | **0 errors**, 6 warnings |
| `dotnet test CopilotAgentObservability.slnx` | **291 passing / 0 failing / 0 skipped** |

The original review's "could NOT be run / not run" caveat is now resolved.
Baseline before any fix work: green build, 291 tests. Re-run both after each
change and keep the count at 291+ (only growing as you add tests).

## Findings — all confirmed valid (zero false positives)

Each was re-checked against current source. Severities are the original
reviewer's; the "Assessment" column is the post-execution confirmation and any
severity adjustment.

### B1 (High → suggest Medium) — Unbounded request body

- Location: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs:44-45`
- Confirmed: `request.InputStream.CopyTo(bodyStream)` into an uncapped
  `MemoryStream`. `HttpListener` applies no default body-size limit.
- Assessment: real resource-exhaustion path. Threat model is loopback-only,
  foreground, single local user, so realistic risk is an accidental oversized
  payload rather than an attack — Medium is more honest than High. Still
  in-scope: issue #25 lists a request body size limit + explicit
  `request_too_large` failure as required.
- Fix direction: enforce a max body size and return a deterministic
  `413` / `request_too_large`. Expected to land with the ASP.NET Core host (M2)
  via `MaxRequestBodySize`. If the HttpListener host must be kept in the
  interim, cap the copy manually (read with a bounded buffer, abort past limit).

### B2 (High — agree, top priority) — One failed request stops the receiver

- Location: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs:24-39` (loop) and `:41-63` (`HandleContext`)
- Confirmed: the accept loop's only guard is
  `catch (HttpListenerException) when (stopping)`. `HandleContext` has no
  try/catch, so a client aborting mid-upload (`IOException` from
  `request.InputStream.CopyTo`) or a response-write failure propagates out of
  `Run` and terminates the whole receiver process.
- Assessment: easy to trigger, high availability impact for a long-running
  receiver. High is correct.
- Fix direction: wrap per-request handling in its own try/catch so one
  request's failure is isolated (log + continue), never escaping the accept
  loop. Under the Sprint8 queue/worker model (M2/M3) the background worker must
  also continue on error. Contradicts the acceptance criterion that a
  post-persist failure must not lose raw records and the receiver must keep
  running.
- Regression test to add first: simulate a mid-upload client abort / a handler
  exception and assert the host keeps accepting subsequent requests.

### B3 (Low — agree, arguably Very Low) — Shutdown flag has no memory barrier

- Location: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs:16-35`
- Confirmed: `stopping` is written from the `Console.CancelKeyPress` handler
  thread and read by the loop condition and the `when (stopping)` filter with no
  `volatile` / `Volatile.Read`. On a weak memory model the filter could observe
  a stale `false` during shutdown and rethrow, crashing on stop.
- Assessment: technically real; practical manifestation is narrow because
  `listener.Stop()` independently unblocks `GetContext()`. Low (or Very Low).
- Fix direction: make the flag `volatile` (or use `Volatile.Read/Write`, or a
  `CancellationTokenSource`). Likely moot once the host moves to ASP.NET Core
  hosted-service shutdown.

### T4 (Tech debt — confirmed both copies live) — Duplicated OTLP attribute conversion

- Locations:
  `src/CopilotAgentObservability.ConfigCli/RawTelemetry/OtlpAttributeConverter.cs`
  vs
  `src/CopilotAgentObservability.ConfigCli/RawTelemetry/RawOtlpIngestor.cs:98-193`
- Confirmed: `ConvertAttributeValue` / `ConvertArrayValue` /
  `ConvertKeyValueList` (~50 lines) exist in both. Neither is dead:
  `OtlpAttributeConverter` is used by `RawMeasurementNormalizer` (lines 222,
  229, 237) and `DashboardRawOperationReader` (lines 86, 93); `RawOtlpIngestor`
  carries its own live copy and also reimplements the attribute-array iteration
  inline in `ExtractResourceAttributesJson` (lines 70-87). Because both copies
  are live, a schema/type-handling change risks being applied to only one.
- Fix direction: have `RawOtlpIngestor.ExtractResourceAttributesJson` call
  `OtlpAttributeConverter.ConvertAttributesArray` and delete the private copies.
  Directly in scope for M1 "shared component extraction" into the new
  `CopilotAgentObservability.Telemetry` project.

### T5 (Tech debt) — DDL executed on every POST

- Location: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHandler.cs:50-52`
- Confirmed: each request does `new RawTelemetryStore(...)` then
  `CreateSchema()` (CREATE TABLE + 3 CREATE INDEX) before insert. Note the real
  cost is **two fresh connections per request** (one in `CreateSchema`, one in
  `Insert`); `IF NOT EXISTS` itself is cheap.
- Fix direction: create schema once at startup; reuse a single writer.
  Superseded by Sprint8's `IngestionWriterWorker` (single writer, serialized
  SQLite writes, schema set up once).

### T6 (Tech debt) — List queries read all payloads into memory

- Location: `src/CopilotAgentObservability.ConfigCli/RawTelemetry/RawTelemetryStore.cs:78-111`
- Confirmed: `ListRecords()` selects every row including full `payload_json`;
  `normalize-raw` materializes all payloads. Sprint8 requires list retrieval to
  not load all raw payloads, with cursor pagination + sanitized monitor
  projections (`monitor_ingestions` / `monitor_traces`).
- Fix direction: do not back the monitor list endpoints with `ListRecords()`.
  Add a projection query (id / source / trace_id / received_at, no payload) +
  cursor pagination.

### T7 (Tech debt, minor) — Single-threaded synchronous accept loop

- Location: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHost.cs:26-30`
- Confirmed: `GetContext()` handles one request at a time; concurrent clients
  serialize behind the OS accept backlog. Throughput only, not correctness.
- Fix direction: superseded by the bounded-channel + background worker model.

## Additional findings NOT in the original review (surfaced by building)

The static review could not see build output. The build emits **NU1903 known
high-severity vulnerability** warnings that should be tracked:

- `MessagePack` 2.5.192 — GHSA-hv8m-jj95-wg3x — project
  `CopilotAgentObservability.AppHost`.
- `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 — GHSA-2m69-gcr7-jv3q — projects
  `CopilotAgentObservability.ConfigCli` and `...ConfigCli.Tests`.

The SQLite native lib is a direct dependency of the raw store. Recommend
bumping these packages when M1 extracts `Persistence.Sqlite`. Confirm the
upgrade with a full build + test run.

## Recommended order of work

1. **M1 — shared component extraction** (`Telemetry`, `Persistence.Sqlite`).
   Lowest-risk entry point; naturally retires T4, T5, T6. Independently
   buildable/testable.
2. Bundle the **NU1903 package bumps** into M1 (touches the same projects).
3. **M2/M3 — ASP.NET Core host + queue/worker**, which absorb **B1, B2, B3,
   T7**. Add the B2 regression test (client-abort / handler-exception does not
   kill the receiver) before/with this work.

Per the decision recorded in the original review, B1/B2/B3 were intentionally
left as documented known issues in the HttpListener host, to be absorbed by the
ASP.NET Core host rather than patched in place. Honor that unless you decide
otherwise and record the change.

## How to validate any change

```
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Expected baseline: 0 build errors, 291 tests passing. Keep both green; grow the
test count as you add coverage (especially a B2 regression test).

# Sprint8 M2 — ASP.NET Core Receiver Host (`LocalMonitor` project) (Plan)

Status: **DRAFT — awaiting `/codex:adversarial-review`**. Author role: Claude
(orchestrator) per `CLAUDE.md`. Implementation is delegated to Codex after this
plan is challenge-reviewed and accepted.

This is the milestone plan for Sprint8 (issue #25) **M2 only**. It is repository
guidance / planning evidence, **not** product behavior, and it does **not**
override any spec.

**Source of truth governing M2 (`AGENTS.md` order; higher wins):**

1. The user's latest explicit instruction.
2. `docs/requirements.md` — functional scope incl. the Local Ingestion Monitor
   (monitor goal + opt-in raw/PII boundary; §§ around lines 48–49, 202).
3. `docs/spec.md` — Public Interfaces (monitor endpoints, run interface, client config).
4. `docs/specifications/`:
   - `layers/telemetry-ingestion.md` — **Local Ingestion Monitor Receiver** (receiver requirements, ports, body-size `413`).
   - `interfaces/config-cli.md` — `profile-vscode-env --target` / `--endpoint`.
   - `security-data-boundaries.md` — **Local Ingestion Monitor Boundary** (`--enable-raw-view`, loopback, Host header, body-size `413`).
5. `docs/decisions.md` — **D020**.

**Context only (history; MUST NOT override the specs above, per `AGENTS.md`
"`docs/sprints/` is context only"):**

- [`../../requirements-and-replan.md`](../../requirements-and-replan.md) — Sprint8
  replan (DR/DD rationale, M2 cut). Sprint-local; **superseded by the promoted
  specs wherever they differ**.
- [`../M1-shared-component-extraction/plan.md`](../M1-shared-component-extraction/plan.md)
  — M1 visibility / dependency-direction decisions M2 builds on.

If anything in this plan disagrees with the Source-of-truth specs, **the specs
win** and the disagreement must be surfaced before coding (per `AGENTS.md`), not
silently implemented.

**Key framing: M2 is implementation against already-promoted contracts, with one
spec gap to close first.** Phase 0 promoted nearly every M2-facing contract (run
interface, port `4320`, `POST /v1/traces` behavior, the `413`-on-oversize rule,
unsupported-signal rules, `--enable-raw-view` default-off, the
`--target`/`--endpoint` config surface). The **one** product boundary Phase 0
left unpinned is the *concrete request-body-size limit value*: the specs say
"a limit ⇒ `413`" but give no number. That value is a public accept/reject
boundary, so it is pinned in the specs **before** M2 coding (see *Pre-M2 spec
promotion*), not invented by the implementer. With that gap closed, M2 implements
what the specs pin and makes no other product-behavior decision; anything else
not already in the specs is flagged as an *open scoping decision* for adversarial
review, not a silent product change.

## Objective

Create the `CopilotAgentObservability.LocalMonitor` ASP.NET Core (Kestrel)
process and make it a working OTLP trace receiver on loopback: accept
`POST /v1/traces` (HTTP/protobuf, JSON for synthetic validation) through the
shared `RawOtlpIngestor`, persist one raw record per valid request, and enforce
the host-level safety boundary (loopback-only bind, Host-header validation, body
size limit, per-request isolation, deterministic port-bind failure). Add the
`profile-vscode-env --target monitor` config surface in ConfigCli, and establish
the `--enable-raw-view` option (default off) with the raw route **absent**.

**Explicitly deferred to later milestones (do not build in M2):**

- The bounded ingestion queue, the single `IngestionWriterWorker` as a hosted
  service, read transactions, projection-worker `SQLITE_BUSY` retry,
  queue-full (`503`) / commit-timeout (`504`) / shutdown (`503`) error mapping,
  graceful drain, and **all `/health/*` endpoints + readiness/degraded
  semantics** → **M3**. (Connection-level **WAL + `busy_timeout`** is pulled into
  M2 — see *Non-contradiction rule* and deliverable #4 — so the interim store is
  a *correct* subset; M3 owns the writer/queue architecture on top.)
- `monitor_ingestions` / `monitor_traces` sanitized projections, the
  `ProjectionWorker`, cursor-paginated `/api/monitor/*`, the
  `GET /traces/{rawRecordId}/raw` raw-detail route, **and the `schema_version` +
  additive migration that introduces the `monitor_*` tables** → **M3/M4**. (M2
  adds **no** new table, so there is nothing to migrate in M2 — see
  *Non-contradiction rule*.)
- Razor Pages UI + SSE → **M5**.
- The consolidated DR6 threat-model negative-test suite, no-raw-in-logs proof,
  and the live VS Code gate → **M6**.

M2 establishes the host and the receive→persist path only.

## M2 acceptance & ship status (non-shippable internal increment)

This addresses the adversarial-review concern that "M2 could be accepted while
violating the promoted monitor storage / readiness contract under failure and
concurrency." The resolution is **not** to pull all of M3/M4 into M2 (that
dissolves the user-approved milestone cut), nor to edit the **product** specs to
encode milestone phasing (the product specs describe end-state behavior;
milestone sequencing is sprint-local per `AGENTS.md`). Instead:

1. **M2 is an internal, non-shippable increment.** "M2 accepted" means the host /
   transport / config-surface acceptance gate below passes — it does **not** mean
   the monitor's storage/readiness contract is satisfied. That contract
   (`raw-store-normalization.md` "Local Ingestion Monitor Storage And Projection"
   + `telemetry-ingestion.md` health) completes across **M3** (writer/queue/
   migration/health) and **M4** (projection/projection-lag). The sprint becomes
   shippable only at the **M6** live gate. M2 lands on the feature branch as a
   step, not as a released receiver.

2. **Non-contradiction rule — M2 ships a strict *subset*, never a *misleading*
   contract.** A partial milestone may implement fewer endpoints than the spec;
   it may **not** present a spec contract in a wrong or fake form:
   - **No fake health.** M2 does **not** implement `/health/live` or
     `/health/ready` (their readiness depends on the writer + projection state
     that exist only in M3/M4). Any `/health/*` request returns `404` — honest
     "not built yet," never a fabricated `ready`/`200`. A placeholder that could
     read as green is forbidden.
   - **Error codes are a consistent subset.** The queue-full `503` /
     commit-timeout `504` paths literally cannot occur in M2 (there is no queue),
     so M2 never emits a *wrong* code for them — it simply lacks those paths. For
     the failures M2 *can* produce, it maps to the spec's codes (DB busy past
     `busy_timeout` → `503`; shutdown → `503`), not a generic `500`-where-the-
     spec-says-`503`.
   - **`2xx`-only-after-commit is honored now.** M2's synchronous insert returns
     `200` only after the row commits — already the spec contract; M3 preserves it
     under the queue.
   - **No schema drift presented as migrated.** M2 changes no schema, so it ships
     no `schema_version`/migration surface that could be mistaken for the M3/M4
     migration; the existing `raw_records` `CREATE … IF NOT EXISTS` is unchanged.

## Scope (M2)

In scope:

1. New project `src/CopilotAgentObservability.LocalMonitor` (`Microsoft.NET.Sdk.Web`, net10.0, executable).
2. Add `CopilotAgentObservability.LocalMonitor` to the `InternalsVisibleTo`
   friend lists of `Telemetry` and `Persistence.Sqlite` (it reuses internal
   `RawOtlpIngestor`, `OtlpProtobufTraceConverter`, `RawTelemetryRecord` /
   `RawTelemetrySources` / `RawStoreDefaults`, `RawTelemetryStore`).
3. New test project `tests/CopilotAgentObservability.LocalMonitor.Tests` (xunit, net10.0).
4. Loopback-only bind; reject non-loopback bind URLs at option-parse time
   (absorbs **B3**); `Host`-header validation middleware on every request
   (anti DNS-rebinding).
5. `POST /v1/traces` (OTLP HTTP/protobuf; JSON accepted for synthetic
   validation) via the shared `RawOtlpIngestor`, persisting one raw record.
6. Request body size limit ⇒ deterministic `413` / `request_too_large`, no raw
   record written (absorbs **B1**).
7. Unsupported signals/paths/methods/content-types ⇒ deterministic fixed status,
   **no** raw record (DD3): non-`/v1/traces` path → `404`, non-`POST` → `405`,
   unsupported content type → `415`, invalid payload → `400`.
8. Per-request isolation: one failed/malformed/aborted request never stops the
   host (absorbs **B2**); a sanitized exception-handling middleware.
9. Distinct loopback port (`4320`, avoiding Collector `4317`/`4318` and CLI
   `4319`) with `--port` / `--url`; deterministic startup error (no stack trace,
   non-zero exit) if the port is already bound (DR1).
10. `--enable-raw-view` option (default off) parsed into host options; the
    raw-detail route is **not registered** in M2 (built in M4) so any
    `GET /traces/{id}/raw` returns `404` (testable absence) (DR6).
11. Config surface in ConfigCli: `profile-vscode-env --target <receiver|monitor>`
    + `--endpoint <loopback-http-url>` (DR1; spec already in `config-cli.md`).
12. Test suite (Section *Tests*).
13. Sprint tracking doc updates (sprint README milestone table, `docs/task.md`,
    this milestone's review record). **No product-spec promotion** — Phase 0
    already did it.

Out of scope: everything in *Explicitly deferred* above, plus any change to the
Sprint7 `serve-raw-local-receiver` runtime behavior (it stays as-is on `4319`;
DR1 side-by-side).

## Validated baseline

Documented baseline (replan §7 / handoff worklist): `dotnet build` = 0 errors;
`dotnet test` = **291 passing / 0 failing / 0 skipped**. M2 must end at 0 build
errors and **> 291** tests passing (M2 only adds tests), with the existing 291
unchanged in outcome (no Sprint7 regression).

## Target structure

```text
src/
├─ CopilotAgentObservability.Telemetry/            (M1; +LocalMonitor friend)
├─ CopilotAgentObservability.Persistence.Sqlite/   (M1; +LocalMonitor friend)
├─ CopilotAgentObservability.ConfigCli/            (existing; +--target/--endpoint)
├─ CopilotAgentObservability.LocalMonitor/         (NEW, Microsoft.NET.Sdk.Web, Exe)
│  ├─ Program.cs                                    (entry; calls the host builder)
│  ├─ MonitorOptions.cs                             (--db/--url/--port/--enable-raw-view parse)
│  ├─ Hosting/                                      (host builder, loopback bind, port-bind error)
│  ├─ Ingestion/                                    (POST /v1/traces endpoint + interim persist)
│  ├─ Security/                                     (Host-header validation, sanitized error middleware)
│  └─ Properties/AssemblyInfo.cs                    (InternalsVisibleTo → LocalMonitor.Tests)
└─ CopilotAgentObservability.AppHost/              (existing, untouched)

tests/
├─ CopilotAgentObservability.ConfigCli.Tests/      (existing; +--target/--endpoint tests)
└─ CopilotAgentObservability.LocalMonitor.Tests/   (NEW)
```

Dependency direction (must hold; verified by review):

```text
Telemetry  <-  Persistence.Sqlite  <-  LocalMonitor
                      ^------------------/
ConfigCli  ->  Telemetry, Persistence.Sqlite     (LocalMonitor does NOT reference ConfigCli)
```

`LocalMonitor` references `Persistence.Sqlite` + `Telemetry` only. It must **not**
reference `ConfigCli` (ConfigCli stays the top-level app; no cycle, no coupling).
The `--target`/`--endpoint` config surface lives entirely inside `ConfigCli`.

Namespace: flat `CopilotAgentObservability.LocalMonitor`, matching the repo
convention (folders are organizational only), consistent with M1.

Add both new projects to `CopilotAgentObservability.slnx`.

## Pre-M2 spec promotion (Claude-owned; before delegation)

Phase 0 left one M2 boundary unpinned: the **concrete request-body-size limit**.
The specs (`telemetry-ingestion.md:205`, `security-data-boundaries.md:148`) say
"a request body size limit ⇒ `413`" with no value. Per `AGENTS.md` (public
behavior → specs first) this value is promoted before M2 coding, parallel to how
the readiness thresholds (`10s` / `60s`) are pinned:

- Pin the concrete limit (value + unit + exact boundary semantics: at-limit
  accepted, over-limit `413`) in `docs/specifications/layers/telemetry-ingestion.md`
  (Local Ingestion Monitor Receiver section) and reference it from
  `docs/specifications/security-data-boundaries.md`; add a Public-Interfaces line
  in `docs/spec.md`.
- **Confirmed with the product owner:** default **31,457,280 bytes (30 MiB)**,
  **configurable** via `--max-request-body-bytes <n>` (CLI) with env fallback
  `CAO_MONITOR_MAX_REQUEST_BODY_BYTES` (readiness-threshold naming convention). A
  request body **up to and including** the limit is accepted; a **larger** body ⇒
  `413` / `request_too_large`, no raw record. Rationale: a local single-user
  receiver whose realistic risk is an accidental oversized payload (B1 = Medium),
  so a generous default avoids dropping real traces; M6 live evidence confirms
  real sizes.
- This is the **only** spec delta M2 requires; it runs through the normal review
  before M2 code starts. M2 then references the spec value and adds the at-limit /
  over-limit boundary tests.

## Deliverable detail

### 1. Project + visibility wiring

- `LocalMonitor.csproj`: `Microsoft.NET.Sdk.Web`, `net10.0`, `ImplicitUsings`
  enabled, `Nullable` enabled; `ProjectReference` → `Telemetry` +
  `Persistence.Sqlite`. **No new NuGet packages** (the Web SDK provides Kestrel /
  hosting; persistence comes transitively from `Persistence.Sqlite`).
- `LocalMonitor/Properties/AssemblyInfo.cs`:
  `[assembly: InternalsVisibleTo("CopilotAgentObservability.LocalMonitor.Tests")]`.
- Append `[assembly: InternalsVisibleTo("CopilotAgentObservability.LocalMonitor")]`
  to `Telemetry/Properties/AssemblyInfo.cs` and
  `Persistence.Sqlite/Properties/AssemblyInfo.cs` (as M1 anticipated).
- `LocalMonitor.Tests.csproj`: same xunit/test SDK set as
  `ConfigCli.Tests.csproj`; `ProjectReference` → `LocalMonitor`, `Telemetry`,
  `Persistence.Sqlite`. **No `Microsoft.AspNetCore.Mvc.Testing`** — see *Tests*
  for why M2 boots the real Kestrel host instead.

### 2. Options + run interface (`MonitorOptions`)

Mirror the `RawLocalReceiverOptions` parse style (deterministic
`*ParseResult { Options, Error }`, "only once" guards, no silent fallback):

- `--db <path>` (default `RawStoreDefaults.DefaultDatabasePath`).
- `--url <loopback-http-url>` (default `http://127.0.0.1:4320`). Validated with
  the same loopback allowlist as Sprint7 (`localhost` / `127.0.0.1` / `::1`);
  non-loopback ⇒ deterministic error (absorbs **B3**).
- `--port <n>` convenience: sets the port on `127.0.0.1`. Specifying **both**
  `--url` and `--port` ⇒ deterministic error (no silent precedence).
- `--enable-raw-view` (boolean flag, default **false**).
- `--max-request-body-bytes <n>` (default **31,457,280** = 30 MiB), env fallback
  `CAO_MONITOR_MAX_REQUEST_BODY_BYTES`; non-positive / non-numeric ⇒ deterministic
  error. Feeds Kestrel `MaxRequestBodySize` (deliverable #6).
- Unknown option ⇒ deterministic error.
- Readiness-threshold flags (`--ingestion-stall-threshold-seconds`,
  `--projection-lag-threshold-seconds`) are **M3** — not parsed in M2.

`Program.cs` parses, prints a deterministic error + non-zero exit on parse
failure, otherwise starts the host. Run model (already in `telemetry-ingestion.md`):

```powershell
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\raw-store.db --url http://127.0.0.1:4320
```

### 3. Host builder + loopback bind + port-bind failure

- A testable internal entry point — e.g. `internal static class MonitorHost`
  with `WebApplication Build(MonitorOptions options)` and/or
  `Task<int> RunAsync(MonitorOptions options, CancellationToken)` — so tests can
  start the **real** host on a loopback port and drive it with `HttpClient`
  (friend-visible via `InternalsVisibleTo`).
- Kestrel binds only the validated loopback URL (`UseUrls` / `KestrelServerOptions`).
- Port already bound ⇒ Kestrel throws on start
  (`IOException` / `AddressInUseException`); catch at startup, emit a
  deterministic fixed error (no stack trace), exit non-zero (DR1).

### 4. `POST /v1/traces` ingestion endpoint

- Accept `application/x-protobuf` (decode via
  `OtlpProtobufTraceConverter.ConvertTraceRequestToRawOtlpJson`) and
  `application/json` (passthrough, synthetic validation), then
  `RawOtlpIngestor.CreateRecordFromPayloadJson(payloadJson, receivedAt)` and
  persist (interim writer — see below). On success return `200` with a body
  mirroring Sprint7's shape (`{"accepted":true,"rawRecordId":<id>}`); on failure
  return the fixed error body (`{"accepted":false,"error":"...","message":"..."}`).
- Fixed error mapping (consistent **subset** of the spec; see *Non-contradiction
  rule*): unsupported content type → `415`, invalid/empty-span payload → `400`,
  DB busy past `busy_timeout` → `503` (`persistence_busy`, matching the spec's
  "DB busy = `503`"), during shutdown → `503`, any other persistence failure →
  `500` (`persistence_failed`, sanitized) — all **without** writing a raw record
  on the error paths (DD3). M2 has no queue, so it never emits a queue-full `503`
  or commit-timeout `504` (those paths arrive with the M3 queue).
- **Interim persistence (M2 only).** Persist via a single `RawTelemetryStore`
  created once at host startup (schema created once via the existing
  `raw_records` `CREATE … IF NOT EXISTS` — no new table, no migration), with
  **WAL + `busy_timeout`** enabled on the connection and writes serialized by a
  `SemaphoreSlim(1,1)`. WAL + `busy_timeout` are pulled forward from M3 because
  they are connection-level and make the interim store a **correct** concurrent
  subset (a concurrent external reader such as `normalize-raw` cannot corrupt or
  hard-fail a write — DR2), at the cost of two PRAGMAs. The serialized single
  writer returns `200` **only after** `Insert` commits (spec's "2xx only after
  commit"). **M3 replaces this stopgap** with the bounded channel +
  `IngestionWriterWorker` (hosted service) + read transactions + projection-worker
  `SQLITE_BUSY` retry + queue-full/timeout/shutdown mapping + graceful drain +
  health. M2 owns no queue, no `/health/*`, and no `schema_version`/`monitor_*`
  migration. *(Resolves open decision A: single store + WAL/`busy_timeout` +
  semaphore; the "mirror-Sprint7-per-request" alternative is dropped.)*

### 5. Unsupported signals / methods / paths (DD3)

- Map only `POST /v1/traces`. `/v1/metrics`, `/v1/logs`, any other path → `404`
  (`unsupported_endpoint`); non-`POST` on `/v1/traces` → `405`
  (`method_not_allowed`). No raw record written. (Behavior matches the promoted
  `telemetry-ingestion.md` monitor-receiver list.)

### 6. Body size limit ⇒ 413 (B1)

The request body size limit is a **public receiver boundary** — it decides
accept-`2xx` vs reject-`413` for a given payload — so its value is **not** an
implementer choice and must not be invented in code or in this plan. The concrete
limit + unit is pinned in the specs **before** M2 coding (small Claude-owned spec
delta — see *Pre-M2 spec promotion*); the implementation references that single
spec value.

- Set Kestrel `MaxRequestBodySize` to the resolved limit (default 30 MiB; see
  the `--max-request-body-bytes` option in #2). A payload exceeding it returns the
  deterministic `413` / `request_too_large` fixed body and writes no raw record
  (map the Kestrel `413` to the fixed body via the error middleware).
- **Exact boundary tests required**: a payload **at** the limit is accepted
  (`200`, persisted); a payload **one byte over** is rejected (`413`, no raw
  record). Use a small overridden `--max-request-body-bytes` in the test so the
  boundary payload stays tiny.

### 7. Per-request isolation (B2)

- A top-level exception-handling middleware converts any unexpected exception to
  a fixed, **sanitized** error response (no DB full path, no Windows user name,
  no raw exception text — DD6) and lets the host keep serving. ASP.NET Core
  already isolates requests; M2 makes the guarantee explicit and tested
  (malformed payload / client-abort does not stop the host).

### 8. `Host`-header validation (anti DNS-rebinding)

- Middleware rejecting any request whose `Host` header host is not loopback
  (`localhost` / `127.0.0.1` / `[::1]`, ignoring the port) with a deterministic
  fixed error (e.g. `400` `invalid_host`), no raw record.
  *(Open scoping decision B — M2 vs M6 placement; see below. Recommended in M2.)*

### 9. `--enable-raw-view` (default off) + raw route absent

- Parse and store the flag (default false). The `GET /traces/{rawRecordId}/raw`
  route is **not registered at all in M2** (it is built in M4, gated on the
  flag). M2's contract: with the default host, `GET /traces/{anyId}/raw` → `404`
  (testable absence). The flag's *gated registration* is M4 work; M2 only
  establishes the option and the default-absent route.

### 10. Config surface — `profile-vscode-env --target` / `--endpoint` (ConfigCli)

Already specified in `config-cli.md` lines 9, 39–49. Implementation:

- Extend **only** the `profile-vscode-env` command (not
  `profile-copilot-cli-env` / `profile-codex-app-config`) to parse `--target
  <receiver|monitor>` and `--endpoint <loopback-http-url>` in addition to
  `--profile`. The other two profile commands keep the plain parse and reject
  `--target`/`--endpoint` as unknown options.
- Resolution: `--target receiver` (default) → `http://127.0.0.1:4319`;
  `--target monitor` → `http://127.0.0.1:4320`; `--endpoint` overrides (must be
  loopback; non-loopback ⇒ deterministic error).
- `--target`/`--endpoint` are valid **only** with `raw-local-receiver`; combining
  with any other profile ⇒ deterministic error.
- Thread the resolved endpoint into the `raw-local-receiver` branch of
  `ConfigSamples.CreateProfileVsCodePowerShellScript` (replace the hardcoded
  `RawLocalReceiverOtlpHttpEndpoint` with the resolved value). Keep the default
  output byte-for-byte identical when neither `--target` nor `--endpoint` is
  given (no regression to existing `profile-vscode-env` tests).

## Tests

New `LocalMonitor.Tests` (real Kestrel boot on loopback + `HttpClient`):

- **valid protobuf trace** → `200` + raw record persisted (reuse
  `OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest()` — relocate/share the
  test payload helper currently in `ConfigCli.Tests`).
- **valid JSON trace** → `200` + persisted (synthetic).
- **body-size boundary** → a payload **at** the spec-pinned limit is accepted
  (`200`, persisted); **one byte over** → `413` / `request_too_large`, no raw
  record. (Limit value comes from the spec, not the test.)
- **unsupported signal/path** (`/v1/metrics`, `/v1/logs`, other) → `404`, no raw
  record; **non-POST** → `405`; **unsupported content type** → `415`; **invalid
  payload** → `400`, no raw record.
- **per-request isolation**: a malformed / aborted request fails cleanly and the
  host still serves a subsequent valid request (B2 regression).
- **port already bound** → second host instance fails with the deterministic
  startup error.
- **`GET /traces/{id}/raw` → `404`** with the default host (raw route absent;
  `--enable-raw-view` off).
- **no fake health**: `GET /health/live` and `GET /health/ready` → `404` in M2
  (endpoints arrive in M3; M2 must never serve a placeholder that reads as green —
  *Non-contradiction rule*).
- **concurrent external reader** (DR2 subset): an external read of the same DB
  (e.g. a second `RawTelemetryStore` / `normalize-raw`-style read) running during
  ingestion neither corrupts nor hard-fails the write — proves WAL + `busy_timeout`
  are in effect.
- **Host-header validation**: a request to the loopback-bound host carrying a
  non-loopback `Host` header is rejected deterministically *(if decision B keeps
  Host validation in M2)*.
- **non-loopback bind URL** (`--url http://0.0.0.0:4320` / a LAN IP) → option
  parse error (B3), host never binds.
- **sanitized errors**: an induced persistence/handler error response contains no
  DB full path, no Windows user name, no raw exception text (the M2 slice of
  DD6; the full no-raw-in-logs + DR6 negative matrix is M6).

New tests in `ConfigCli.Tests`:

- `profile-vscode-env --profile raw-local-receiver --target monitor` → `4320`.
- default (`--target receiver` / omitted) → `4319` (unchanged).
- `--endpoint http://127.0.0.1:<n>` override applied.
- `--target` / `--endpoint` with a non-`raw-local-receiver` profile →
  deterministic error.
- non-loopback `--endpoint` → deterministic error.

## Doc / tracking updates (Codex implements with the code; Claude reviews)

- **One small spec delta only** — the request-body-size limit value (see *Pre-M2
  spec promotion*), Claude-owned, before M2 coding. Everything else M2 needs was
  already promoted in Phase 0 (`spec.md`, `telemetry-ingestion.md`,
  `config-cli.md`, `security-data-boundaries.md`, `decisions.md` D020); M2
  implements those.
- `docs/sprints/sprint8-local-raw-receiver-monitor/README.md`: mark M2 status /
  milestone table.
- `docs/task.md`: Sprint8 status → M2.
- This milestone folder: add a `review.md` self/preserved-review record at close.
- Grep `docs/` for any stale endpoint/port claim that M2 now makes concrete; fix
  only if found (expect none — Phase 0 was thorough).

## Open scoping decisions (for `/codex:adversarial-review`)

- **A — Interim persistence model. RESOLVED** (was: single store + semaphore vs.
  mirror-Sprint7). Resolution: single startup `RawTelemetryStore` + connection
  **WAL + `busy_timeout`** + `SemaphoreSlim`-serialized inserts + spec-correct
  error codes (deliverable #4), so M2 is a *correct concurrent subset* (not a
  contradiction) per the *Non-contradiction rule*. M3 replaces it with the
  queue / hosted writer / health. The mirror-Sprint7 alternative is dropped (it
  could `SQLITE_BUSY`/contradict the spec under a concurrent reader).
- **B — Host-header validation placement (M2 vs M6).** The promoted
  `telemetry-ingestion.md` lists Host-header validation under the monitor
  receiver's core requirements (grouped with loopback bind), so M2 implements it;
  M6 owns the *consolidated* DR6 negative-test suite + security review.
  Alternative: implement only loopback-bind rejection in M2 and defer Host-header
  middleware to M6 (leaves the host without anti-DNS-rebinding through M3–M5).
  *Recommendation: implement in M2 (B1).*
- **C — Shared OTLP decode helper vs. a second copy.** The content-type
  negotiation + span-presence check currently live inline in the Sprint7
  `RawLocalReceiverHandler` (ConfigCli). To avoid re-introducing the kind of
  duplication T4 just removed, recommended: extract a small shared decoder into
  `Telemetry` (e.g. `OtlpTraceIngest.Decode(contentType, body)`) used by **both**
  the Sprint7 handler and the LocalMonitor endpoint (Sprint7 behavior held
  identical, proven by its existing tests). Alternative: keep M2 surgical —
  inline the ~15-line decode in the LocalMonitor endpoint and do not touch
  Sprint7. *Recommendation: extract the shared decoder (C1), per the repo's
  no-duplication posture.*
- **D — Test host strategy.** Recommended: boot the **real** Kestrel host on a
  loopback port and drive via `HttpClient` (no new package; required anyway to
  test real port-binding / loopback-bind / Host-header behavior, which
  `WebApplicationFactory`'s in-memory `TestServer` cannot exercise). Alternative:
  add `Microsoft.AspNetCore.Mvc.Testing` (a **dependency addition** — needs
  explicit approval per `AGENTS.md` "Do Not"). *Recommendation: real-host boot,
  no new dependency (D1).*

## Validation

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Targeted while iterating:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj
```

Pass criteria: 0 build errors; **> 291** tests passing / 0 failing / 0 skipped
(existing 291 unchanged in outcome); dependency direction holds (no `LocalMonitor`
→ `ConfigCli` reference; no `Telemetry`/`Persistence.Sqlite` → upward reference).

## Execution flow (per CLAUDE.md)

1. (this doc) Claude drafts the M2 plan.
2. **User runs `/codex:adversarial-review`** on this plan to challenge the design
   and settle open decisions A–D (the command is user-invocable only).
3. Claude incorporates the feedback and finalizes the plan.
4. Implementation (new project + host + endpoint + config surface + tests +
   tracking docs) is delegated to **Codex**, committing in small validated steps
   with messages starting `Sprint8 M2:` then Conventional Commits.
5. Claude reviews the result (build/test green, diff, dependency direction,
   raw/PII non-regression, loopback boundary, no Sprint7 regression) before any
   commit is accepted.

# Sprint8 M1 — Shared Component Extraction (Plan)

Status: **REVISED after `/codex:adversarial-review` (verdict: needs-attention)**.
Author role: Claude (orchestrator) per `CLAUDE.md`. Implementation is delegated
to Codex after this plan is challenge-reviewed and accepted.

### Adversarial-review outcome (how each finding is resolved)

- **[high] Do not publish the unsafe store as a shared public contract** —
  Accepted. Moved types stay **`internal`** with `InternalsVisibleTo` friends
  (see Visibility strategy). `RawTelemetryStore` is relocated but never becomes
  a solution-wide public API, so M3/M4 can fix T5/T6 without an API break and
  no M2+ code can bind to `ListRecords()` / multi-writer paths as a public
  contract. The reviewer's stronger option — *design the monitor-facing
  write/projection interfaces now* — is **declined for M1**: LocalMonitor and
  the M3/M4 worker/projection specs do not exist yet, so defining that contract
  here would be speculative and out of scope. It is deferred to M3/M4 where it
  belongs.
- **[medium] Visibility widens internals without a defined contract** —
  Accepted. Internal-by-default + `InternalsVisibleTo`; no broad public surface.
- **[medium] NU1903 residual must not pass via documentation** — Accepted.
  0 NU1903 warnings is a **hard M1 exit criterion**; if a vulnerable transitive
  cannot be overridden, M1 stops and reports (or the bump is split out), it does
  not pass with a documented residual.

This is the milestone plan for Sprint8 (issue #25) **M1 only**. It is repository
guidance / planning evidence, not product behavior. Inputs:
[`../../handoff-fix-worklist.md`](../../handoff-fix-worklist.md),
[`../../pre-implementation-review.md`](../../pre-implementation-review.md),
and issue #25 (`Sprint8: Local Raw Receiver Monitor`).

## Objective

Extract the shared telemetry and persistence components out of
`CopilotAgentObservability.ConfigCli` into two new class library projects so
they can be reused by the future `LocalMonitor` host (M2+) **without changing
ConfigCli's external behavior or its test outcomes**.

This is a pure structural refactor plus the one safe deduplication (T4) and the
NU1903 package bumps. No product behavior, CLI surface, SQLite schema, or
on-disk format changes in M1.

## Scope (M1)

In scope:

1. Create `src/CopilotAgentObservability.Telemetry/` (class library, net10.0).
2. Create `src/CopilotAgentObservability.Persistence.Sqlite/` (class library, net10.0).
3. Move OTLP decode / attribute conversion / raw ingest / raw record model /
   measurement normalization into `Telemetry`.
4. Move the SQLite raw store into `Persistence.Sqlite`.
5. Fix **T4** (delete the duplicated OTLP attribute conversion in
   `RawOtlpIngestor`; call the shared `OtlpAttributeConverter`).
6. Bundle the **NU1903** package bumps (`SQLitePCLRaw.*`, `MessagePack`).
7. Re-wire the solution, `ConfigCli`, and the test project so the existing
   build and all existing tests stay green.
8. Promote the structural decision into `docs/architecture.md` and
   `docs/decisions.md`, add Sprint8 sprint tracking, and update file-path
   references in the affected layer specs (Claude-owned doc work).

Explicitly **out of scope for M1** (deferred, recorded below):

- The `LocalMonitor` ASP.NET Core host, Kestrel, endpoints, queue/worker, Web
  UI, SSE (M2–M6).
- **B1 / B2 / B3** receiver-host fixes — by the decision recorded in
  `pre-implementation-review.md`, these stay as documented known issues in the
  Sprint7 HttpListener host and are absorbed by the ASP.NET Core host in M2/M3.
  M1 does not touch `RawLocalReceiverHost`.
- **T5** (schema-once / single writer) and **T6** (projection query / no
  `ListRecords()` for monitor lists) — these are *behavior* changes owned by
  the `IngestionWriterWorker` (M3) and monitor projection (M4). M1 relocates
  `RawTelemetryStore` **as-is**; it does not change its behavior. The
  worklist's "M1 naturally retires T5/T6" is interpreted as "M1 puts the store
  in the right place so M3/M4 can retire them," not "M1 fixes them." The store
  stays `internal` (per the Visibility strategy), so M1 does **not** turn the
  unsafe `ListRecords()` / multi-writer / schema-per-call paths into a public
  shared contract — M3/M4 can replace them without an API break.
- **T7** (single-threaded accept loop) — superseded by the M3 channel/worker
  model; the Sprint7 loop is untouched in M1.
- The `Telemetry/Monitoring/` folder and monitor-summary sanitization from
  issue #25's structure — no such *shared* code exists yet; it is created in
  M4 when the monitor projection exists. M1 does not create speculative empty
  folders.

## Validated baseline

Per the handoff worklist (executed): `dotnet build` = 0 errors / 6 warnings;
`dotnet test` = 291 passing / 0 failing / 0 skipped. M1 must end at 0 build
errors and >= 291 tests passing, with the NU1903 warnings resolved.

## Target structure

```text
src/
├─ CopilotAgentObservability.Telemetry/            (new, class library)
│  ├─ Otlp/
│  ├─ RawTelemetry/
│  └─ Normalization/
├─ CopilotAgentObservability.Persistence.Sqlite/   (new, class library)
├─ CopilotAgentObservability.ConfigCli/            (existing, Exe)
└─ CopilotAgentObservability.AppHost/              (existing)
```

Dependency direction (matches issue #25):

```text
Telemetry  <-  Persistence.Sqlite  <-  ConfigCli
                      ^------------------/
```

`Persistence.Sqlite` references `Telemetry` (it persists `RawTelemetryRecord`).
`ConfigCli` references both. `Telemetry` references nothing internal.
(`LocalMonitor` will reference `Persistence.Sqlite` + `Telemetry` in M2.)

### Namespace strategy

The existing code uses a single **flat** namespace
(`CopilotAgentObservability.ConfigCli`) regardless of folder. To match that
convention and keep ripple minimal, the new assemblies use flat root namespaces
equal to the assembly name:

- `CopilotAgentObservability.Telemetry`
- `CopilotAgentObservability.Persistence.Sqlite`

Folders (`Otlp/`, `RawTelemetry/`, `Normalization/`) are organizational only,
not sub-namespaces. (Open for adversarial review: flat vs. folder-aligned
sub-namespaces.)

### Visibility strategy (internal-by-default)

Moved types stay **`internal`** (as they are today). M1 does **not** create a
public shared API. Cross-assembly access is granted with `InternalsVisibleTo`,
so no unsafe or unstable type (`RawTelemetryStore`, the OTLP converters, the
normalizer) becomes a solution-wide public contract before the LocalMonitor /
M3 / M4 design exists.

`InternalsVisibleTo` wiring (one `AssemblyInfo.cs` per new project):

- `Telemetry` → friends: `CopilotAgentObservability.Persistence.Sqlite`,
  `CopilotAgentObservability.ConfigCli`,
  `CopilotAgentObservability.ConfigCli.Tests`.
- `Persistence.Sqlite` → friends: `CopilotAgentObservability.ConfigCli`,
  `CopilotAgentObservability.ConfigCli.Tests`.
- (`CopilotAgentObservability.LocalMonitor` is added to both friend lists in M2
  when that project is created — not now.)

The test project adds `ProjectReference`s to the new assemblies; combined with
`InternalsVisibleTo` it references the moved internal types directly with no
assertion changes. `ConfigCli`'s existing `InternalsVisibleTo` for the test
assembly stays for the types that remain internal in `ConfigCli`.

A genuine public reuse contract (and any monitor-facing write/projection
interface) is defined later, with M3/M4, against real requirements — not
speculatively in M1.

## Extraction map (exact)

### Into `CopilotAgentObservability.Telemetry`

| Source (under `src/CopilotAgentObservability.ConfigCli/`) | Target folder | Notes |
| --- | --- | --- |
| `RawLocalReceiver/OtlpProtobufTraceConverter.cs` | `Otlp/` | + its `ProtoReader`; OTLP protobuf→JSON trace decode |
| `RawTelemetry/OtlpAttributeConverter.cs` | `Otlp/` | stays `internal` (friend-visible) |
| `RawTelemetry/RawOtlpIngestor.cs` | `RawTelemetry/` | **apply T4 fix** (see below) |
| `RawTelemetry/RawMeasurementNormalizer.cs` | `Normalization/` | depends on `OtlpAttributeConverter`, `MeasurementSanitizer`, `MeasurementRow` |
| `Measurements/MeasurementRow.cs` | `Normalization/` | pure record; normalizer return type — must be below ConfigCli |
| `Measurements/MeasurementSanitizer.cs` | `Normalization/` | self-contained (JsonNode/Regex); used by normalizer |
| **new** `RawTelemetry/RawTelemetryRecord.cs` | `RawTelemetry/` | **split out** of `RawTelemetryOptions.cs`: `RawTelemetryRecord`, `RawTelemetrySources`, `RawStoreDefaults` |

### Into `CopilotAgentObservability.Persistence.Sqlite`

| Source | Notes |
| --- | --- |
| `RawTelemetry/RawTelemetryStore.cs` | moved **as-is** (no T5/T6 behavior change); stays `internal` (friend-visible); owns the `Microsoft.Data.Sqlite` package reference |

### Stays in `CopilotAgentObservability.ConfigCli`

- `RawTelemetry/RawTelemetryOptions.cs` — keeps the CLI option-parse records
  (`RawIngestOptions`, `RawNormalizationOptions`, their `*ParseResult`) after
  the record model is split out.
- `RawTelemetry/RawNormalizationInputReader.cs`, `DashboardRawOperationReader`,
  `DiagnosisCandidates/RawEvidenceReader`, `Cli/CliApplication` (ingest-raw),
  and the Sprint7 `RawLocalReceiver/` host + handler — all now reference the
  moved types via project references.
- `Measurements/MeasurementAggregator`, `MeasurementOutputWriter`,
  `MeasurementAggregationOptions` — consume `MeasurementRow` from `Telemetry`.

### T4 fix detail

In the moved `RawOtlpIngestor`:
- delete the private `ConvertAttributeValue` / `ConvertArrayValue` /
  `ConvertKeyValueList` copies;
- rewrite `ExtractResourceAttributesJson` to build the object via
  `OtlpAttributeConverter.ConvertAttributesArray(attributes)` and return
  `attributesObject.Count > 0 ? attributesObject.ToJsonString() : null`,
  preserving today's "first resourceSpan with non-empty attributes wins"
  semantics. No behavior change for `RawOtlpIngestorTests`.

## Package / dependency changes

- `Telemetry.csproj`: net10.0, `ImplicitUsings`/`Nullable` enabled, no external
  package references (uses BCL `System.Text.Json` only). Add a `GlobalUsings.cs`
  with the `System.*` usings the moved files rely on
  (`System.Globalization`, `System.Text`, `System.Text.Json`,
  `System.Text.Json.Nodes`, `System.Text.Json.Serialization`,
  `System.Text.RegularExpressions`).
- `Persistence.Sqlite.csproj`: net10.0, references `Telemetry`; owns
  `Microsoft.Data.Sqlite`. `GlobalUsings.cs` with `Microsoft.Data.Sqlite`,
  `System.Globalization`.
- `ConfigCli.csproj`: add `ProjectReference` to `Telemetry` and
  `Persistence.Sqlite`. Add `global using CopilotAgentObservability.Telemetry;`
  and `global using CopilotAgentObservability.Persistence.Sqlite;` to its
  `Shared/GlobalUsings.cs` (keeps per-file ripple near zero). Remove the direct
  `Microsoft.Data.Sqlite` `PackageReference` and rely on the transitive flow
  through `Persistence.Sqlite` (keep `global using Microsoft.Data.Sqlite;` for
  `SqliteException` in the Sprint7 handler). If the transitive flow does not
  satisfy compilation, restore an explicit reference — Codex verifies via build.

### NU1903 bumps (bundled)

- `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 — GHSA-2m69-gcr7-jv3q (transitive via
  `Microsoft.Data.Sqlite` 10.0.8). Fix: add an explicit `PackageReference` to
  the patched `SQLitePCLRaw.*` package(s) in `Persistence.Sqlite` (flows
  transitively to `ConfigCli` and the test project). Codex resolves the exact
  patched version/package set via `dotnet list package --vulnerable`.
- `MessagePack` 2.5.192 — GHSA-hv8m-jj95-wg3x (transitive via the Aspire
  AppHost SDK). Fix: add an explicit patched `MessagePack` `PackageReference`
  to `AppHost`. Codex resolves the exact version.
- Acceptance (**hard M1 exit criterion**): post-bump `dotnet build` reports
  **0 NU1903 warnings**, proven with restore/build evidence. A documented
  residual does **not** pass M1. If a vulnerable transitive genuinely cannot be
  overridden, Codex stops and reports the exact blocking package/version, and
  the NU1903 bump is split out of M1 for a separate decision rather than shipped
  with the structural extraction.

## Test project changes

`tests/CopilotAgentObservability.ConfigCli.Tests`:
- add `ProjectReference`s to `Telemetry` and `Persistence.Sqlite` (combined with
  the new assemblies' `InternalsVisibleTo` for this test assembly, the moved
  `internal` types are accessible without making them public);
- add `<Using Include="CopilotAgentObservability.Telemetry" />` and
  `<Using Include="CopilotAgentObservability.Persistence.Sqlite" />` to the
  csproj so test files that touch moved types
  (`RawTelemetryStoreTests`, `RawNormalizationTests`, `RawOtlpIngestorTests`,
  `OtlpProtobufTraceConverterTests`, `MeasurementAggregationTests`, and the
  receiver/dashboard/diagnosis tests that touch them) compile with no per-file
  using edits;
- no test assertions change — behavior is preserved.

## Doc / spec promotion (Codex implements with the code; Claude reviews)

- `docs/architecture.md`: add a "Module / Solution Structure" subsection
  recording the new project layout and dependency direction
  (`Telemetry <- Persistence.Sqlite <- ConfigCli`), and note that the Sprint8
  `LocalMonitor` host (M2+) will reuse these shared modules.
- `docs/decisions.md`: add **D019** (Status: Accepted) recording (a) the
  shared-component extraction into `Telemetry` + `Persistence.Sqlite`,
  (b) internal-by-default + `InternalsVisibleTo` (no public API in M1),
  (c) the NU1903 bumps with the 0-warning hard gate, (d) carry-forward of the
  B1/B2/B3 deferral, (e) M1 relocates the store without T5/T6 behavior changes,
  (f) `Monitoring/` deferred to M4.
- `docs/sprints/sprint8-local-raw-receiver-monitor/README.md` + this milestone
  folder: sprint tracking consistent with prior sprints.
- `docs/task.md`: add Sprint8 to the roadmap/history index (in progress / M1).
- `docs/specifications/layers/telemetry-ingestion.md` and
  `raw-store-normalization.md` contain **no file-path references** to the moved
  files (verified) — they describe behavior, which is unchanged. Grep `docs/`
  for any stale path reference to a moved file and fix only if found; expect
  none.
- **No** `docs/requirements.md` / `docs/spec.md` product-behavior changes in M1
  (no product behavior or CLI surface change). Product-spec promotion for the
  monitor (endpoints, projections, safety boundary, UI/SSE) happens with M2+.

## Validation

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Pass criteria: 0 build errors; 0 NU1903 warnings; >= 291 tests passing / 0
failing / 0 skipped. Also confirm the dependency direction holds (no
`Telemetry` or `Persistence.Sqlite` reference back into `ConfigCli`).

## Execution flow (per CLAUDE.md)

1. (this doc) Claude drafts the M1 plan.
2. **User runs `/codex:adversarial-review`** to challenge the design — the
   command is user-invocable only (`disable-model-invocation: true`).
3. Claude incorporates the feedback and finalizes the plan + doc promotion.
4. Implementation (code extraction, T4, NU1903, wiring, test/csproj edits, doc
   path fixes) is delegated to Codex.
5. Claude reviews the result (build/test green, diff, dependency direction,
   raw-data non-regression) before any commit.

## Decisions settled after the adversarial review

- **Visibility** — RESOLVED: internal-by-default + `InternalsVisibleTo`. No
  public shared API in M1. (addresses high + medium findings)
- **NU1903** — RESOLVED: 0 warnings is a hard exit criterion; no documented
  residual passes. (addresses medium finding)
- **T5/T6 / `Monitoring/` deferral** — kept; the internal-visibility decision
  removes the "unsafe public contract" objection to deferring them.
- **Monitor write/projection interface** — intentionally NOT defined in M1
  (no LocalMonitor / M3 / M4 spec yet); deferred to M3/M4.

## Remaining open choices (low-risk, Codex may decide during implementation)

1. Namespace: flat (`CopilotAgentObservability.Telemetry`) is the default to
   match the repo convention; folder-aligned sub-namespaces are acceptable if
   Codex finds them clearer for M2+.
2. `MeasurementRow` + `MeasurementSanitizer` live in `Telemetry/Normalization/`
   (they are the normalizer's contract); splitting them into a separate
   measurement library is deferred unless a concrete need appears.
3. `Microsoft.Data.Sqlite` reference in `ConfigCli`: transitive via
   `Persistence.Sqlite` preferred; restore an explicit reference if build
   requires it.

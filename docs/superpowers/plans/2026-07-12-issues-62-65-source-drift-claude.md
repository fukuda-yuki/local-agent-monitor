# Issues #62-#65 Source Drift and Claude Code Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the fixture-backed portions of Issues #62, #63, #64, and #65 by adding source-schema drift diagnostics, Claude Code OTLP/Hook ingestion with exact Session binding, additive projection/UI support, and deterministic regression gates.

**Architecture:** The OTLP decoder produces canonical raw JSON plus a structural inventory. The single ingestion writer atomically persists the raw record and its immutable batch observation in dedicated monitor schema v5 tables. Claude adapters consume the same raw/session boundaries under the Issue #61 authority contract; existing sanitized projection and Session DTOs receive additive source facts, while `GET /api/monitor/source-diagnostics` owns compatibility diagnostics and `/health/ready` remains unchanged.

**Tech Stack:** .NET 10, C#, ASP.NET Core, Microsoft.Data.Sqlite, System.Text.Json, OTLP JSON/protobuf, Razor, vanilla JavaScript, xUnit, Playwright, Markdown/JSON fixtures

## Global Constraints

- Preserve source trace/span/parent IDs byte-for-byte; never synthesize hierarchy, timing, tokens, or missing values.
- Compatibility is determined by observed schema fingerprint, not an application-version receive allowlist.
- The ingest batch is compatibility authority; Session/trace status is derived.
- Claude OTel owns identity, parentage, and timing. Claude Hook owns native lifecycle and explicit event identity.
- Bind only identical native session ID, explicit resume/handoff, or byte-equivalent trace context. Repository, cwd, transcript path, process identity, and timestamp proximity are forbidden.
- Claude Agent ownership uses exact source parentage only. Copilot retains its existing Issue #49 inference behavior.
- `GET /health/ready`, its thresholds, status mapping, body, and reasons remain unchanged.
- Sanitized API/SSE/UI/golden/log/repository artifacts contain no raw body, attribute values, PII, credentials, source text, or sensitive paths.
- `--sanitized-only` removes raw routes/controls and preserves sanitized hierarchy.
- Migrations are additive, transactional, restart-safe, newer-schema rejecting, and tested from real historical implementation fixtures. Current-schema column/table deletion fixtures are not evidence.
- Atomicity, stale state, dedupe, rollback, and concurrency tests use transactions/barriers/controlled fakes; no sleeps or speculative retries.
- Task delivery order is implement → focused validation → independent review → owner correction → same-reviewer clearance → commit. Whole-branch corrections also require re-review before the independent validation runner executes.
- No new runtime/development dependency, push, PR, or remote-history mutation.
- Every implementation/test task is implemented by a fresh Sub-Agent and reviewed by a different Sub-Agent; the primary orchestrator owns Task 10 contract integration. Critical/Important findings block the next task.

---

### Task 1A: Freeze the Structural Inventory Redesign

**Owner:** primary orchestrator; independent `gpt-5.6-sol` ultra review.

**Files:**
- Modify: `docs/specifications/interfaces/source-schema-drift-claude-code.md`
- Create: `docs/specifications/contracts/source-capabilities/v1/otlp-trace-structural-v1.md`
- Modify: this plan
- Update: `docs/sprints/sprint22-source-drift-claude/ledger.md`

- [ ] Freeze domain-separated producer-name hashing, the shared OTLP descriptor/envelope set, original-input recursive walking, full-set hash semantics, diagnostic bounds, required-signal definition, evaluation precedence, unknown-kind mapping, and closed draft factories.
- [ ] Independent reviewer confirms the corrected contract removes the two-cycle review premises before production work resumes.

### Task 1B: Closed Compatibility Domain and Evaluator

**Owner profile:** fresh `gpt-5.6-sol`, xhigh effort.

**Files:**
- Modify: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/SourceCompatibilityModels.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/SourceCompatibilityEvaluator.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/VerifiedSourceFingerprintRegistry.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SourceCompatibilityTests.cs`

**Interfaces:** private-constructor/factory aggregates produce `SourceCompatibilityDecision`, `SourceObservationBatchDraft`, validated unknown children, fixed capture state, and canonical reason/action pairs. No caller supplies raw state/reason lists, child version labels, arbitrary names, or undefined enums.

`SourceStructuralNameToken`, `SourceOccurrenceCount`, `SourceUnknownIdentity`, and registry evidence are also closed validated values with defensive immutable snapshots. Duplicate/conflicting registry evidence is rejected.

- [ ] Own and add RED tests `InventoryFactory_RejectsInvalidAndDefensivelyCopies`, `Registry_RejectsDuplicateAndConflictingEvidence`, `ObservationFactory_StateReasonAndCaptureAreClosed`, `UnknownFactory_ValidatesNameCountTimeAndReference`, `ReasonSet_DeduplicatesAndOrdersHardCodedVocabulary`, `Assess_VersionAndFingerprintPolicy`, and `Assess_CombinedConditionsFollowCanonicalPrecedence` with independently written expected wire strings.
- [ ] Implement the minimal closed types/evaluator and split registry evidence into fingerprint, incompatibility, and recognition-profile responsibilities.
- [ ] Run the focused compatibility tests and `git diff --check`; obtain independent domain/spec review and same-reviewer clearance before commit.

### Task 1C: Original-Input OTLP Structural Walkers

**Owner profile:** fresh `gpt-5.6-sol`, xhigh effort.

**Files:**
- Create: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpTraceSchema.cs`
- Create: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpJsonStructuralWalker.cs`
- Create: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpProtobufStructuralWalker.cs`
- Replace: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpStructuralInventoryBuilder.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Otlp/OtlpTracePayloadDecoder.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Otlp/OtlpProtobufTraceConverter.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/RawLocalReceiver/RawLocalReceiverHandler.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SourceCompatibilityTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/OtlpProtobufTraceConverterTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/RawLocalReceiverHandlerTests.cs`

**Interfaces:** produce `DecodedOtlpTracePayload(PayloadJson, StructuralInventory)` only after a descriptor-driven recursive walk of the original accepted JSON/protobuf. Consumers explicitly select `PayloadJson`; no implicit string conversion or `additionalEntries` path remains.

- [ ] Own and implement the descriptor matrix tests from `Build_KnownNestedEnvelope_JsonAndProtobufMatchGoldenFingerprints` through `Build_AggregateUnknownCountsIgnoreRetainedRowLimit`, plus `DecodedPayload_ConsumersExplicitlyUsePayloadJson`. Task 1B exclusively owns the inventory-factory, registry, assessment, observation, unknown-factory, and reason-set tests.
- [ ] Implement one descriptor model and both transport walkers; correct fixed32 flags and remove duplicated handled-field tables/post-conversion inventory.
- [ ] Run `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SourceCompatibilityTests|FullyQualifiedName~OtlpProtobufTraceConverterTests|FullyQualifiedName~RawLocalReceiverHandlerTests"`; `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorHostTests`; solution build; and `git diff --check`.
- [ ] Independent transport/security reviewer checks official OTLP wire types, recursive parity scope, full-set hashes, bounds, and absence of every producer literal/value before commit.

### Task 2: Real Monitor v1-v4 Migration Fixtures

**Owner profile:** `gpt-5.6-sol`, high effort.

**Files:**
- Create: `scripts/test/GenerateMonitorSchemaFixtures/GenerateMonitorSchemaFixtures.csproj`
- Create: `scripts/test/GenerateMonitorSchemaFixtures/Program.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor/manifest.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor/monitor-v1.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor/monitor-v2.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor/monitor-v3.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor/monitor-v4.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSchemaMigrationFixtureTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj`

**Interfaces:**
- Fixtures are generated by actual `CreateMonitorSchema` implementations at `655e002`, `f91e195`, `9ca613a`, and `65ec872` and include sentinel rows.
- Manifest records component, version, source commit, SHA-256, generation command, and sentinel IDs.
- A static reviewed v4 semantic contract is validated against the read-only manifest-hashed v4 fixture and every migrated copy. It covers every `table_list` field (`wr`/`strict` included), every `table_xinfo` field, and every `index_xinfo` key/expression identity, order, collation, and `key`/auxiliary flag, plus origin/partial predicate and foreign keys. Quote-aware table SQL inspection is used only for `AUTOINCREMENT` and CHECK clauses. Internal autoindex names are ignored while their semantics remain contractual.

- [ ] Generate each closed `.sqlite` fixture in disposable historical worktrees; do not hand-author DDL.
- [ ] Add RED theory tests that verify manifest hashes/provenance and fail before fixtures exist; after generation, copy each fixture, run the current v4 migration, close/reopen, rerun migration, and preserve every complete sentinel snapshot/count.
- [ ] Add a scratch-schema characterization theory proving the semantic reader detects missing `AUTOINCREMENT`, changed CHECK, changed unique order, `NOCASE`/`DESC` index terms, expression-index identity, `index_xinfo.key = 0` auxiliary-term changes, partial indexes, added foreign keys, and every `table_list`/`table_xinfo` field mutation. Add quote/comment/string-literal decoys for `AUTOINCREMENT`/CHECK and prove equivalent autoindex semantics compare equal despite different internal names.
- [ ] Run `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSchemaMigrationFixtureTests` and finish GREEN against current v4 before Task 3 extends the assertions to v5.
- [ ] Independent migration reviewer validates every fixture against its historical commit and rejects synthetic/pseudo fixtures.
- [ ] After that reviewer clears the task, commit only immutable fixtures, manifest, test, and csproj copy metadata; never commit WAL/SHM/runtime output.

### Task 3: Transactional Monitor v5 Observation Store

**Owner profile:** `gpt-5.6-sol`, xhigh effort.

**Files:**
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/SourceCompatibilityRows.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/ISourceCompatibilityStore.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/SqliteSourceCompatibilityStore.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryRecordSql.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/MonitorSchemaMigrator.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Ingestion/SqliteIngestionCommitStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs` only to delegate existing raw insert SQL to `RawTelemetryRecordSql` and existing v1-v4 base DDL to source-neutral `MonitorSchemaMigrator`; it must preserve higher stamps and gain no compatibility query/write methods.
- Modify: `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionWriteRequest.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionWriterWorker.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSchemaMigrationFixtureTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/SourceCompatibilityStoreTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/IngestionWriterWorkerTests.cs`

**Interfaces:**
- Monitor schema v5 adds dedicated `source_schema_observations` and `source_unknown_observations` tables plus cursor indexes.
- v5 DDL and the static ExpectedV5 semantic oracle pin the exact state/scalar-reason/seven-value `next_action`/capture vocabulary and valid combinations; a direct invalid action insert is rejected.
- `IIngestionCommitStore.Commit(ValidatedIngestionBatch)` returns raw/observation IDs after one SQLite transaction.
- `ISourceCompatibilityStore.RecordAdapterFailure(SourceAdapterFailureDraft)` persists a nullable-batch diagnostic through the same single writer; `List(after, limit)` owns reads.
- A failed observation insert rolls back the raw row; duplicate batch persistence is idempotent.
- The focused initializer applies real v1-v4 base migration and v5 in one transaction; injected failure restores the exact original version/schema. Later raw initialization cannot downgrade v5 or a future stamp.

- [ ] Add RED tests for newer-version rejection/no downgrade, transactional rollback from every real v1-v4 fixture, atomic raw+observation commit, failure after partial unknown children, unknown overflow counts, idempotency, controlled write-lock busy/no-hidden-retry/explicit replay, duplicate delivery at deterministic barriers, ExpectedV5 exact `next_action` CHECK signature, and direct invalid-action rejection.
- [ ] Run focused migration/store/writer tests and confirm RED.
- [ ] Implement v5 migration inside a transaction and the focused store; do not add source-specific methods to unrelated raw CRUD.
- [ ] Re-run focused tests and `git diff --check`.
- [ ] Independent migration/concurrency reviewer checks transaction boundaries, restart, newer stamp, WAL behavior, and no raw/PII columns.

### Task 4B: Recognized View and Compatibility Projection Gate

**Owner profile:** `gpt-5.6-sol`, high effort.

Task 4A (atomic raw plus observation acknowledgement) is complete. Task 4B does
not change its schema, commit store, or transaction.

**Files:**
- Modify: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpTraceSchema.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpJsonStructuralWalker.cs`
- Create: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpJsonRecognizedPayloadBuilder.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/RawTelemetry/RawOtlpIngestor.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Normalization/OtlpSpanReader.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Otlp/OtlpAttributeConverter.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/ISourceCompatibilityStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/SqliteSourceCompatibilityStore.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/OtlpJsonRecognizedPayloadBuilderTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/RawOtlpIngestorTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/RawNormalizationTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SourceCompatibilityIngestionTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/ProjectionWorkerTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/IngestionWriterWorkerTests.cs`

**Interfaces:**
- `/v1/traces` builds observation draft before enqueue and returns success only after atomic persistence.
- One descriptor-derived `JsonDocument`/`Utf8JsonWriter` recognized projection view is shared by monitor pre-persistence normalization and post-reload projection. It recursively excludes unknown properties, wrong representations, invalid repeated elements, and Trace-ignored fields without mutating raw or hashes.
- Valid JSON objects with wrong field/hierarchy representations atomically persist original raw plus degraded/unsupported observation. Malformed transport/non-object root remains pre-persistence failure with no raw. The strict Config CLI path is unchanged.
- Projection accepts the recognized partial/empty view for every raw-backed state and legacy row; one successful scheduled pass completes the existing independent idempotent trace and span phases. No combined transaction, schema, retry, or sleep is added.
- Recognized-record drop and adapter/parse failure are distinct from drift.

- [ ] Add a test-owned descriptor oracle with exactly 59 valid-representation cases, 295 wrong-kind cases, 28 malformed-decimal cases, 225 repeated-element first/middle/last cases, 84 unknown-property cases, 118 duplicate wrong-to-valid/valid-to-wrong cases, and 14 AnyValue consumer cases: 823 fast cases total. Tests must not derive expected rows from production descriptor helpers.
- [ ] Add RED end-to-end JSON/protobuf tests for valid input; every recursive wrong type with valid siblings; every repeated array; duplicate properties; no-valid-span unsupported persistence; unknown JSON/protobuf; malformed transports; injected adapter exception; atomic commit rollback; raw-marker retention versus sanitized exclusion; descriptor-valid numeric `kind` and `status.code`; one-pass completion of both existing backlogs; and hard-coded valid Copilot projection bytes.
- [ ] Run the focused ingestion/projection tests and confirm RED.
- [ ] Implement the descriptor-derived recognized view, pass original payload plus view to monitor raw ingestion, rebuild the same view after reload, and implement explicit projection disposition. Keep local scalar readers strict by semantic type so IDs/names require strings while enum/status fields preserve descriptor-valid numbers.
- [ ] Re-run focused tests plus existing `MonitorProjectionBuilderTests` and `MonitorSpanProjectionBuilderTests`.
- [ ] Independent reviewer checks descriptor/view parity, raw commit ordering, no silent drop, retry/backlog behavior, valid numeric enum/status preservation, and Copilot projection non-regression.

### Task 5: Sanitized Source Diagnostics API and UI

**Owner profile:** `gpt-5.6-terra`, high effort.

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/SqliteSourceCompatibilityStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Diagnostics.cshtml.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Diagnostics.cshtml`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor-diagnostics.js`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/SourceDiagnosticsApiTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSecurityBoundaryTests.cs`

**Interfaces:**
- Implement exact `GET /api/monitor/source-diagnostics?after&limit` DTO and fixed next actions.
- Query through `ISourceCompatibilityStore.List`; do not add compatibility methods to `IMonitorProjectionStore`.
- Do not alter `/health/ready` serialization or status.

- [ ] Add RED API/UI tests for all six compatibility states, 1..200 paging, invalid query, fixed next actions, stable ordering, and empty/error views.
- [ ] Add RED negative tests with raw marker, email, token, credential, and path values absent from diagnostics/API/HTML/log output.
- [ ] Implement store query, route, and diagnostics view using inert text only.
- [ ] Re-run focused API/UI/security tests and existing readiness tests.
- [ ] Independent security reviewer checks DTO allowlist, readiness invariance, and raw/PII absence.

### Task 6: GitHub Copilot Fidelity Golden Gate

**Owner profile:** `gpt-5.6-sol`, high effort.

**Files:**
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Fidelity/github-copilot-vscode-v1.otlp.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Fidelity/github-copilot-vscode-v1.golden.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TraceFidelityRegressionTests.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/VerifiedSourceFingerprintRegistry.cs`
- Create: `docs/sprints/sprint22-source-drift-claude/milestones/M1-schema-drift/live-validation.md`
- Create: `docs/sprints/sprint22-source-drift-claude/templates/source-version-live-validation-template.md`

**Interfaces:**
- Golden contains only source IDs, edges, recognized counts/mappings, token/timing/status, evidence refs, ordering, and compatibility metadata.

- [ ] Add a producer-shaped Copilot fixture based on the current real producer DTO contract, not a new mock DTO.
- [ ] Add RED golden comparison through decode → atomic persist → projection → evidence resolution.
- [ ] Assert raw/sanitized boundary and deterministic byte-stable golden output.
- [ ] Implement only the harness/expected golden needed to make the real pipeline pass.
- [ ] Independent reviewer maps every Issue #62 acceptance item to an executable assertion and checks no payload content in artifacts.

### Task 7: Interactive Claude Code Inventory

**Owner profile:** `gpt-5.6-terra`, high effort.

**Files:**
- Create: `docs/specifications/contracts/source-capabilities/v1/inventories/claude-code-interactive.json`
- Conditionally create after successful execution: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/interactive/otel-content-disabled.structure.json`
- Conditionally create after successful execution: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/interactive/otel-content-enabled.structure.json`
- Conditionally create after successful execution: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/interactive/hooks.structure.json`
- Update: `docs/sprints/sprint22-source-drift-claude/ledger.md`

**Interfaces:**
- Record executable/version, OTel settings, emitted structural inventory, Hook event inventory, content-disabled/enabled labels, and verified fingerprints without raw values.

- [ ] Check installed `claude --version` and official monitoring/Hook configuration.
- [ ] Run one bounded synthetic interactive session with content disabled, then enabled only with synthetic markers, if credentials/tooling are available.
- [ ] Record repository-safe structural evidence; otherwise record the exact blocker and create a named follow-up entry without inventing a fixture.
- [ ] Independent evidence reviewer confirms version/settings/provenance and data safety.

### Task 8: `claude -p` Inventory

**Owner profile:** `gpt-5.6-terra`, high effort.

**Files:**
- Create: `docs/specifications/contracts/source-capabilities/v1/inventories/claude-code-print.json`
- Conditionally create after successful execution: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/print/otel.structure.json`
- Conditionally create after successful execution: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/print/hooks.structure.json`
- Update: `docs/sprints/sprint22-source-drift-claude/ledger.md`

**Interfaces:** same inventory schema as Task 7 with `surface_mode = print`.

- [ ] Run a bounded synthetic `claude -p` session if available, including one tool/error path permitted by the environment.
- [ ] Record emitted/missing capabilities and content gate labels without raw values.
- [ ] If blocked, record exact command/precondition and independent follow-up; do not substitute interactive evidence.
- [ ] Independent evidence reviewer confirms the result.

### Task 9: Claude Agent SDK Inventory

**Owner profile:** `gpt-5.6-sol`, high effort.

**Files:**
- Create: `docs/specifications/contracts/source-capabilities/v1/inventories/claude-agent-sdk.json`
- Conditionally create after successful execution: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/agent-sdk/otel.structure.json`
- Conditionally create after successful execution: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/agent-sdk/hooks.structure.json`
- Update: `docs/sprints/sprint22-source-drift-claude/ledger.md`

**Interfaces:** same inventory schema as Task 7 with `surface_mode = agent-sdk`; record traceparent relationship when observed.

- [ ] Detect an existing Agent SDK runtime without adding a dependency.
- [ ] If available, run a bounded synthetic SDK query with an active parent span and capture structural OTel/Hook evidence.
- [ ] If unavailable, record the missing runtime/package/credential as a separate follow-up; do not install or invent evidence.
- [ ] Independent evidence reviewer confirms the result.

### Task 10: Freeze the #63 Claude Adapter Contract

**Owner:** primary orchestrator for requirements/design; independent `gpt-5.6-sol` xhigh review.

**Files:**
- Modify: `docs/specifications/contracts/source-capabilities/v1/manifests/claude-code.json`
- Modify: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/VerifiedSourceFingerprintRegistry.cs`
- Create: `docs/specifications/contracts/source-capabilities/v1/claude-code/otel-mapping.json`
- Create: `docs/specifications/contracts/source-capabilities/v1/claude-code/hook-mapping.json`
- Create: `docs/specifications/contracts/source-capabilities/v1/claude-code/exact-binding.md`
- Create: `docs/specifications/contracts/source-capabilities/v1/claude-code/security.md`
- Modify: `docs/specifications/interfaces/source-schema-drift-claude-code.md`
- Modify: `docs/specifications/interfaces/canvas-session-workspace.md`
- Modify: `docs/sprints/sprint22-source-drift-claude/ledger.md`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SourceCapabilityContractTests.cs`

**Interfaces:**
- Freeze only officially documented or Task 7-9 observed fields.
- Distinct adapters: `claude-code-otel` and `claude-code-hook`; standard `/v1/traces` and existing strict Session ingest envelope.
- The manifest registry key is the evidence label `claude-code-otel+claude-code-hook`; persisted observations use only the actual producer adapter ID `claude-code-otel` or `claude-code-hook`. Never persist the composite registry label as provenance or change the v1 manifest shape to encode both concerns.

- [ ] Add RED contract tests for manifest/inventory/mapping cross-references and exact authority/binding/content rules.
- [ ] Consolidate observed evidence, explicitly labeling unverified surface cases.
- [ ] Re-run SourceCapabilityContractTests and `git diff --check`.
- [ ] Independent reviewer must confirm #64 can implement without guessing producer fields; unresolved live execution remains a named follow-up only.

### Task 11: Repository-Safe Claude Hook Producer Fixtures

**Owner profile:** `gpt-5.6-sol`, high effort.

**Files:**
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/fixture-manifest.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/session-start.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/user-prompt-submit.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/pre-tool-use.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/post-tool-use.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/permission-request.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/subagent-start.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/subagent-stop.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/stop.json`
- Conditionally create only if Task 10 records an observed/documented producer error envelope: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/stop-error.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/hooks/unsupported-event.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/ClaudeProducerFixtureContractTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj`

**Interfaces:** Every fixture is a complete official/observed producer envelope with synthetic values, never a structural summary. The manifest records source document/inventory, surface mode, content gate, source version label, expected adapter, and SHA-256.

- [ ] Add RED tests requiring every named fixture, exact common/event-specific fields, bounded synthetic values, manifest hash/provenance, and absence of real path/PII/credential markers. Require `stop-error.json` only when Task 10 cites its producer shape; otherwise require the manifest to record error-envelope capability as unavailable and do not invent that fixture or event.
- [ ] Derive complete envelopes from the Task 10 Hook mapping and official Claude Hook contract; preserve exact field names and nesting while replacing values with synthetic markers.
- [ ] Run `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~ClaudeProducerFixtureContractTests` and finish GREEN.
- [ ] Independent reviewer compares each fixture field-for-field with its cited producer contract and rejects mock-only fields.

### Task 12: Repository-Safe Claude OTLP Producer Fixtures

**Owner profile:** `gpt-5.6-sol`, high effort.

**Files:**
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/fixture-manifest.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/otel/content-disabled.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/otel/content-enabled.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/otel/unsupported-version.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/otel/schema-drift.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/otel/content-disabled.bin`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/otel/content-enabled.bin`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/ClaudeProducerFixtureContractTests.cs`

**Interfaces:** JSON and protobuf fixtures are complete OTLP ExportTraceServiceRequest payloads with the same source IDs and recognized field semantics. Unsupported/drift fixtures differ only in the cited version/schema structure.

- [ ] Extend the RED fixture contract tests to require every OTLP file, manifest hash/provenance, JSON/protobuf semantic equivalence, and content-disabled/enabled boundary.
- [ ] Derive full payloads from Task 10 OTel mapping and official/observed Claude OTLP structures; generate protobuf from the same producer-shaped semantic fixture, not a separate mock DTO.
- [ ] Assert no real prompt, response, tool body, PII, credential, or local path is committed.
- [ ] Finish GREEN and obtain independent producer-contract/data-safety review.

### Task 13: Real Session v1-v5 Fixtures

**Owner profile:** `gpt-5.6-sol`, xhigh effort.

**Files:**
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/manifest.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v1.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v2.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v3.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v4.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v5.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionSchemaMigrationFixtureTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj`

**Interfaces:** historical v1-v5 fixtures and manifest entries prove exact real schema lineages accepted by the current v10 implementation.

- [ ] Add RED manifest/hash/source-commit tests and confirm failure before fixtures exist.
- [ ] Generate v1-v5 from `ab02e36`, `b5e02e0`, `8d765ad`, `601c2be`, and `30d5c86`; close before hashing and include sentinels through each historical public store API.
- [ ] Run current v10 migration on every copied fixture, close/reopen, rerun, and verify stamp and sentinels.
- [ ] Independent migration reviewer validates v1-v5 provenance/restart and absence of hand-written DDL/drop evidence.

### Task 14: Real Session v6-v10 and Stamped-v10 Lineage Fixtures

**Owner profile:** `gpt-5.6-sol`, xhigh effort.

**Files:**
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/manifest.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v6.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v7.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v8.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v9.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v10.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v10-from-v4.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v10-from-v5.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/session/session-v10-from-v6.sqlite`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionSchemaMigrationFixtureTests.cs`

**Interfaces:** v6-v10 and the three actual `cf2b15f` stamped-v10 lineages extend Task 13's manifest/test contract.

- [ ] Extend the manifest theory to RED for the eight missing fixture entries.
- [ ] Generate v6-v10 from `6048da1`, `5a28b87`, `87f4a00`, `e55e2df`, and `cf2b15f`; generate lineage fixtures by running the actual `cf2b15f` upgrader over actual Task 13 v4/v5 and Task 14 v6 inputs.
- [ ] Run current v10 migration, close/reopen, rerun, and verify sentinels plus repaired pending/revision/effect objects for every fixture.
- [ ] Independent migration reviewer validates hashes, source/upgrader commits, all lineage repairs, and absence of hand-written DDL/drop evidence.

### Task 15: Session Schema v11 and Claude Provenance Vocabulary

**Owner profile:** `gpt-5.6-sol`, xhigh effort.

**Files:**
- Modify: `src/CopilotAgentObservability.Telemetry/Sessions/SessionDomain.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionIngestModels.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionSchemaMigrationFixtureTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionDomainTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SqliteSessionStoreTests.cs`

**Interfaces:** Session schema v11 supports `claude-code`, distinct adapters, and required version/fingerprint/normalization provenance without changing existing IDs.

- [ ] Extend Tasks 13-14 tests to RED for v11 migrate-close-reopen, newer-v12 rejection, rollback on invalid legacy shape, Claude surface wire values, and provenance persistence.
- [ ] Run the three focused test classes and confirm expected RED failures.
- [ ] Implement transactional v11 migration and additive domain/envelope provenance fields.
- [ ] Re-run focused tests and existing Session store regression tests.
- [ ] Independent migration/spec reviewer checks all fixtures, restart, rollback, exact identity preservation, and no content in provenance columns.

### Task 16: Claude Hook Adapter

**Owner profile:** `gpt-5.6-sol`, high effort.

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/HookForwarding/HookForwardCommand.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Sessions/ClaudeHookEventMapper.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionEventNormalizer.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/HookForwarderTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionWorkspaceRouteTests.cs`

**Interfaces:**
- Convert official/observed Claude Hook envelopes into `source_adapter=claude-code-hook`, `source_surface=claude-code`, stable canonical hash identity, and versioned Session events.
- Hook never creates span/timing/token/parentage and remains loopback, 250 ms, fail-open, silent.

- [ ] Add RED producer-fixture tests for lifecycle, prompt, tool, permission, subagent, stop/error, property-order idempotency, and unsupported event preservation.
- [ ] Add RED negative tests for stdout/stderr silence, timeout/network fail-open, content-disabled, secret/path filtering, and no synthetic OTel fields.
- [ ] Implement minimal mapper/forwarder integration and re-run Hook/Session focused tests.
- [ ] Independent security/spec reviewer checks official shape, authority, strict envelope, and two filter layers.

### Task 17: Claude OTel Adapter and Exact Binding

**Owner profile:** `gpt-5.6-sol`, xhigh effort.

**Files:**
- Create: `src/CopilotAgentObservability.Telemetry/Monitoring/ClaudeCodeSpanAdapter.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorSpanProjectionBuilder.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionOtelEnricher.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSpanProjectionBuilderTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionOtelEnrichmentTests.cs`

**Interfaces:**
- Map only Task 10 recognized fields and preserve trace/span/parent IDs byte-for-byte.
- Bind only by identical native session ID, explicit `resume`/`handoff`, or byte-equivalent trace context; source labels and trace ID alone confirm display only and never bind.

- [ ] Add RED producer-fixture tests for identity, hierarchy, model/tokens/cache/reasoning, TTFT, tool identity/status, attempt/retry, permission, subagent, error, and null for missing fields.
- [ ] Add RED positive binding tests for each canonical condition and near-miss negatives for same trace ID with non-equivalent trace context, generic/non-resume link, repository, cwd, transcript path, PID, prompt, and timestamp proximity.
- [ ] Implement adapter and exact enrichment; re-run existing Copilot projection/enrichment suites.
- [ ] Independent reviewer checks byte equivalence, authority precedence, no zero-fill, and Copilot regression.

### Task 18: Deterministic Claude Idempotency, Concurrency, and Security

**Owner profile:** `gpt-5.6-sol`, xhigh effort.

**Files:**
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/ClaudeIngestionConcurrencyTests.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionEventQueue.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionEventWriterWorker.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionOtelEnricher.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSecurityBoundaryTests.cs`

**Interfaces:**
- Duplicate OTLP/Hook input is idempotent; same source identity cannot move between Sessions; hook/enrichment race produces one exact result or a typed conflict with no partial write.
- Stale-read schedule: pause a Hook writer after its ownership/version read, commit an OTel enrichment, then resume the Hook writer. The final Session has one owner, both events exactly once, monotonic `last_seen`/completeness, no state downgrade, and a cursor that advances only with the successful commit.
- Duplicate schedule: release identical Hook requests or identical OTLP batches at the same barrier. Exactly one source identity/event/observation is visible and replay does not add another.
- Conflict schedule: race two different Session owners for one source identity. One transaction succeeds and the other returns the typed ownership conflict; the rejected path writes no event, content, provenance, observation, or cursor.
- Rollback schedule: inject a deterministic failure between event, content, provenance, observation, and cursor writes. The whole transaction rolls back and a later explicit processing pass commits exactly once.
- Busy schedule: hold the SQLite write lock with a test-controlled transaction. The attempted pass returns the typed busy outcome with no row or cursor movement; after releasing the lock, one explicitly invoked later pass succeeds. Production code adds no retry loop.

- [ ] Add barrier-controlled RED tests implementing every schedule and invariant above, including cursor replay after rollback/busy; use task-completion sources or controlled transactions, never elapsed-time sleeps.
- [ ] Add raw-default/content-disabled/sanitized-only/error/log negative matrix with synthetic secret markers.
- [ ] Implement only necessary production seams; no sleeps, unbounded retries, or fallback merge.
- [ ] Independent concurrency/security reviewer verifies deterministic scheduling and atomic results.

### Task 19: Early #62/#64 Integration Review

**Owner profile:** independent `gpt-5.6-sol`, xhigh effort; implemented none of Tasks 1-18.

**Files:**
- Update: `docs/sprints/sprint22-source-drift-claude/ledger.md`

Review findings are returned to the owning Task 1-18 implementer; the reviewer does not apply open-ended integration edits.

- [ ] Review producer DTO → raw → observation → normalized Session/trace → diagnostics contracts and field names from Tasks 1-18.
- [ ] Execute the cross-surface test using actual producer fixtures, including JSON/protobuf and Hook/OTel exact-link paths.
- [ ] Review security, concurrency, migration, provenance, and Copilot regression together.
- [ ] If one area has a second consecutive correction cycle, stop local patching and audit its premise/contract/test design before continuing.
- [ ] Record Critical/Important/Minor verdict and unresolved Issue interfaces in the ledger.

### Task 20: Additive Projection, HTTP DTOs, and Claude-Only Exact Graph

**Owner profile:** `gpt-5.6-sol`, high effort.

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/SourceCompatibility/SourceDiagnosticDto.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/SourceCompatibilityRows.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/SqliteSourceCompatibilityStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/MonitorProjectionRows.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Projection/AgentExecutionGraphBuilder.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionApiTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionWorkspaceRouteTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/AgentExecutionGraphTests.cs`

**Interfaces:**
- Use one backend-owned `SourceDiagnosticDto` through `SourceCompatibilityRow -> SourceDiagnosticDto -> /api/monitor/traces and /api/monitor/trace-list -> Session list/detail routes -> Canvas`; RawTelemetryStore does not acquire diagnostic ownership and UI/proxy code does not reconstruct it.
- Add this exact additive block to trace and Session DTOs, with snake_case JSON names and nullable values where the canonical spec permits absence:
  ```json
  {
    "source_diagnostic": {
      "source_surface": "...",
      "source_application_version": "...",
      "source_adapter": "...",
      "adapter_version": "...",
      "schema_fingerprint": "...",
      "compatibility_state": "...",
      "reason_codes": [],
      "next_action": "..."
    },
    "binding_state": "hook_only|otel_only|exact_linked",
    "completeness": "...",
    "completeness_reason_codes": [],
    "content_state": "available|not_captured|redacted|unsupported|null"
  }
  ```
- Aggregate trace/Session source facts by canonical compatibility severity, then newest observation; union reasons in canonical order. A pre-persistence adapter failure with no trace/Session identity appears only in `/api/monitor/source-diagnostics`. Emit `content_state` only when all linked observations agree, otherwise `null`.
- Agent graph retains its existing DTO. Claude missing parent emits `unresolved/unknown`; Copilot missing parent retains existing inference.

- [ ] Add RED exact-shape tests at every producer/consumer boundary for the block above, severity/newest aggregation, canonical reason union, Hook-only/OTel-only/exact-linked, mixed content disagreement, and unavailable nulls.
- [ ] Add RED graph tests proving missing, ambiguous, duplicate, and cyclic Claude parent evidence remains unresolved; mixed Claude/Copilot traces apply source-specific policy; and existing Copilot inference remains byte-for-byte unchanged.
- [ ] Implement deterministic read projection and source-aware graph policy without a second hierarchy endpoint.
- [ ] Re-run projection/session/graph/Canvas contract tests.
- [ ] Independent reviewer checks same field names across backend/HTTP/Canvas and no UI reconstruction.

### Task 21: Local Monitor Claude Trace UI and Playwright

**Owner profile:** `gpt-5.6-terra`, high effort.

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/TraceDetail.cshtml`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/TraceDetail.cshtml.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor-tracelist.js`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor-flow.js`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor-waterfall.js`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorTraceListPlaywrightTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorAgentExecutionPlaywrightTests.cs`

- [ ] Add RED Playwright tests for Claude trace list/detail, exact and unresolved (missing/ambiguous/duplicate/cyclic) hierarchy, Hook-only/OTel-only/exact-linked, unavailable values, empty/error/degraded states, content-disabled, and sanitized-only.
- [ ] Implement inert Japanese UI labels/next actions using DTO facts only; no zero-fill, time inference, or raw control when unavailable.
- [ ] Re-run the two Playwright classes plus existing Monitor UI/security tests.
- [ ] Independent UI/spec reviewer confirms Claude uses exact-only facts while Copilot inferred labels/behavior remain unchanged.

### Task 22: Canvas Session Claude Presentation and Contract Tests

**Owner profile:** `gpt-5.6-terra`, high effort.

**Files:**
- Modify: `.github/extensions/otel-monitor-canvas/canvas-workspace-helpers.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-workspace-helpers.test.mjs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`

- [ ] Add RED Node tests for the exact `source_diagnostic` object, Claude label, binding/completeness reasons, agreed/null content state, degraded next action, and unchanged bounded proxy output.
- [ ] Implement inert Canvas presentation without a parallel hierarchy interpretation or additional raw fetch.
- [ ] Run `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests` and the extension's existing Node test command through that harness.
- [ ] Independent Canvas/data-safety reviewer confirms backend/HTTP/Canvas field continuity and no raw/PII/action/log expansion.

### Task 23: User Documentation and Live-Validation Closeout

**Owner profile:** `gpt-5.6-terra`, high effort.

**Files:**
- Modify: `docs/user-guide/local-monitor.md`
- Create: `docs/sprints/sprint22-source-drift-claude/milestones/M5-integration/live-validation.md`
- Update: `docs/sprints/sprint22-source-drift-claude/ledger.md`
- Update: `docs/sprints/sprint22-source-drift-claude/README.md`

- [ ] Instantiate `templates/source-version-live-validation-template.md` for every runnable interactive/print/SDK case with date, OS, source version, settings labels, opaque references, observed/missing capabilities, unknown fields, completeness, and blockers.
- [ ] Record exact named follow-up tasks for unavailable live surfaces; do not copy evidence between surfaces or claim completion for blocked live-only criteria.
- [ ] Update the user guide with supported/verified evidence, content warning, source capability table, diagnostics states/next actions, and limitations already canonical in the specs.
- [ ] Independent documentation/security reviewer checks promotion consistency and repository-safe content.

### Task 24: Final Reviews, Validation, Acceptance Audit, and Commits

**Owner profiles:** whole-branch spec/quality reviewer `gpt-5.6-sol` ultra; separate security/concurrency/migration reviewer `gpt-5.6-sol` ultra; separate validation runner `gpt-5.6-terra` high.

**Files:**
- Create: `docs/sprints/sprint22-source-drift-claude/milestones/M5-integration/review.md`
- Update: `docs/sprints/sprint22-source-drift-claude/ledger.md`
- Update: `docs/sprints/sprint22-source-drift-claude/README.md`

- [ ] Generate a merge-base-to-HEAD review package after every Task 1-23 review is clear.
- [ ] Run independent whole-branch spec/quality review and separate security/concurrency/migration review. Return each Critical/Important finding to its original bounded task owner, or create a fresh bounded fix task for one cohesive area; after task-level clearance, require both original whole-branch reviewers to clear the unified corrected package.
- [ ] Only after re-review clearance, have the independent validation runner execute without substitution: `dotnet build CopilotAgentObservability.slnx`; `pwsh scripts\test\install-playwright-chromium.ps1`; `dotnet test CopilotAgentObservability.slnx`; `git diff --check`.
- [ ] Audit every #62-#65 acceptance criterion against an exact ledger row and executable test/report. Record live-only blockers as incomplete follow-ups rather than closing them.
- [ ] Commit only reviewed coherent scopes with Issue-prefixed Conventional Commit messages after each task gate; commit final evidence last. Do not push or create a PR.

# Issue #89 Raw Read Callsite Inventory

Kickoff SHA and inventory base SHA: `11d6c587903f6ea97026d815f608231efea08d65`.
This inventory is a bounded product-callsite inventory, not filesystem discovery.

| Raw-bearing path | Current production callsite | Classification | Required v1 gate or lease |
| --- | --- | --- | --- |
| Session event content HTTP read | `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs` | `required_cleanup` | Exact `session_event_content` catalog item, readable revision, access lease |
| Session content persistence/read | `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs` | `required_cleanup` | Exact event ID/Session provenance and catalog transaction |
| Monitor raw projection/read | `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs:531,553` | `required_cleanup` | Exact `raw_record` item and projection access lease |
| Claude Doctor fact raw reader | `src/CopilotAgentObservability.ConfigCli/FirstTrace/ClaudeCode/ClaudeDoctorFactCollector.cs:503` | `required_cleanup` | Exact `raw_record` item and access lease |
| Claude Doctor candidate raw reader | `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/ClaudeCode/ClaudeDoctorCandidateObserver.cs:173` | `required_cleanup` | Exact `raw_record` item and access lease |
| Session OTel enrichment raw reader | `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionOtelEnricher.cs:296` | `required_cleanup` | Exact `raw_record` item and access lease |
| Analysis tool-data raw reader | `src/CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs:398,407` | `required_cleanup` | Exact `raw_record` and `analysis_run_raw` items plus operation lease |
| Analysis result reader | `src/CopilotAgentObservability.LocalMonitor/Analysis/SqliteMonitorAnalysisStore.cs:122,138` | `required_cleanup` | Exact `analysis_run_raw` item and access lease |
| Active analysis SDK directory | `src/CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs:79-84,268-304` | `blocked` | Future exact `analysis_sdk_directory` operation lease; no current ownership proof |
| Sensitive Bundle path | `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/SensitiveBundleWriter.cs:17-120` | `blocked` | Explicit catalog binding and exact owned `sensitive_bundle` lease required before read/resume/enumeration |
| Caller-supplied raw OTLP file | Config CLI `--raw` input | `not_applicable` | Caller-owned; never catalog or delete |

Additional concrete production raw reads discovered by the v1 audit: `RawTelemetryStore.ListRecords` (`RawTelemetryStore.cs:61-86`), `ListUnprocessedForProjection` (`:95-123`), and `ListUnprocessedForSpanProjection` (`:582-608`) each load `payload_json`/`resource_attributes_json` and require the exact `raw_record` access or projection lease. `MonitorHost` raw-record (`MonitorHost.cs:713-744`), span-detail (`:750-765`), and prompt-label (`:842-863`) routes each require the same exact readable `raw_record` lease and are `required_cleanup` consumers. `SqliteSessionStore.GetContent` (`SqliteSessionStore.cs:764-804`) and `SessionRoutes` raw-content route (`SessionRoutes.cs:366-397`) require the exact `session_event_content` access lease. `SqliteMonitorAnalysisStore` run/event raw-result reads (`:122-138`) require `analysis_run_raw` access. No direct production Sensitive Bundle or SDK directory reader has an exact catalog-owned artifact at this HEAD; those modes remain `blocked` rather than receiving a permissive lease.

Further direct consumers (all `required_cleanup` and requiring the exact readable `raw_record` access/operation lease unless stated): Config CLI `DashboardRawOperationReader.ReadRawJson` (`DashboardRawOperationReader.cs:30`), `RawNormalizationInputReader.Read` (`RawNormalizationInputReader.cs:9`), `RawEvidenceReader.Read` (`DiagnosisCandidates/RawEvidenceReader.cs:30`), `ClaudeDoctorFactCollector` (`ClaudeDoctorFactCollector.cs:503`), `GitHubCopilotDoctorEvidenceAdapter` (`GitHubCopilotDoctorEvidenceAdapter.cs:165`), Local Monitor `MonitorHost` graph loads (`:158,:378`), page raw prompt loads `Index.OnGet` (`Pages/Index.cshtml.cs:96`), `Traces.OnGet` (`Pages/Traces.cshtml.cs:149`), `TraceDetail.OnGet` (`Pages/TraceDetail.cshtml.cs:85`), `SessionRoutes` projection (`:536`), `ProjectionWorker` raw loads (`:101,:167`), and `DotNetCopilotRawAnalysisRunner` raw loads/tool input (`:355,:358,:413,:497`). These are catalog-gated readers, not independent stores; `InstructionEvidenceExtractor` receives those exact leased raw records and adds no separate persistence.

Safe monitor projections, Session/Event metadata, analysis safe summaries,
catalog receipts, and tombstones are `retained_by_policy`; they never authorize
raw reconstruction. There is no current receiver-created raw file or external
blob implementation. Arbitrary legacy Sensitive Bundle and shared SDK-root
modes are blocked at kickoff and must be retired or positively reconciled before
Issue #89 closeout; they are never recursively scanned or deleted.

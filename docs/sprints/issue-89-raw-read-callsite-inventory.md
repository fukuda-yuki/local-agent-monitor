# Issue #89 Raw Read Callsite Inventory

Kickoff SHA and inventory base SHA: `11d6c587903f6ea97026d815f608231efea08d65`.
This inventory is a bounded product-callsite inventory, not filesystem discovery.

| Raw-bearing path | Current production callsite | Classification | Required v1 gate or lease |
| --- | --- | --- | --- |
| Session event content HTTP read | `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs` | `required_cleanup` | Exact `session_event_content` catalog item, readable revision, access lease |
| Session content persistence/read | `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs` | `required_cleanup` | Exact event ID/Session provenance and catalog transaction |
| Monitor raw projection load | `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs` | `required_cleanup` | Exact `raw_record` item and projection access lease |
| Monitor raw-detail/prompt/span reads | `src/CopilotAgentObservability.LocalMonitor` raw-bearing routes | `required_cleanup` | Exact `raw_record` item and access lease |
| Analysis raw tool-data/result load | `src/CopilotAgentObservability.LocalMonitor/Analysis` | `required_cleanup` | Exact `analysis_run_raw` item and operation lease |
| Active analysis SDK operation | `src/CopilotAgentObservability.LocalMonitor` analysis runner | `required_cleanup` | Exact `analysis_sdk_directory` item and operation lease |
| Sensitive Bundle read/resume/enumeration | `src/CopilotAgentObservability.ConfigCli` candidate generation | `required_cleanup` | Explicit existing `--retention-database`, exact `sensitive_bundle` item, access/operation lease |
| Caller-supplied raw OTLP file | Config CLI `--raw` input | `not_applicable` | Caller-owned; never catalog or delete |

Safe monitor projections, Session/Event metadata, analysis safe summaries,
catalog receipts, and tombstones are `retained_by_policy`; they never authorize
raw reconstruction. There is no current receiver-created raw file or external
blob implementation. Arbitrary legacy Sensitive Bundle and shared SDK-root
modes are blocked at kickoff and must be retired or positively reconciled before
Issue #89 closeout; they are never recursively scanned or deleted.

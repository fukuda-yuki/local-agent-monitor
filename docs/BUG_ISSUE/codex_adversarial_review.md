# Codex Adversarial Review

Target: branch diff against main
Verdict: needs-attention

This file is retained as raw review evidence. Do not plan fixes directly from
this file; use the deduplicated cards in `README.md` and the feature files:

- M2-1 covers the root `invoke_agent` token-rollup finding.
- M2-6 covers the multiple-root `invoke_agent` token-rollup follow-up.
- M2-7 covers range-safe token rollup / overflow handling.
- M3-1 covers hidden span-backfill backlog.
- M3-2 covers missing `traceId` poison records.
- M5-1 covers secondary trace raw lookup.
- M5-4 covers unbounded inline raw rendering.

Follow-up review after the first fix pass:

- [medium] Multiple root `invoke_agent` spans were silently represented by the
  first root usage only. Resolved by specifying and implementing root usage
  summation; tracked as M2-6.
- [low] Chat fallback token sums could overflow unchecked `int` arithmetic and
  produce negative token counts. Resolved by range-safe accumulation and
  nullable out-of-range projection; tracked as M2-7.

出荷不可。agent-execution view の中核である token rollup、span backfill readiness、raw 表示の対応関係が、いずれも実データの順序・欠損・バッチ形状で静かに嘘をつく。raw-default-on 自体と CSP 追加なしは仕様上の受容リスクとして反証できるが、projection/readiness の穴は反証できない。

Findings:
- [high] Trace total can pick a child invoke_agent instead of the root (src/CopilotAgentObservability.Telemetry/Monitoring/MonitorTraceRollup.cs:37-39)
  `ComputeRollup` stores the first `invoke_agent` with input tokens as `rootInvokeAgent`; it never checks `parent_span_id` or whether that span is actually the root. Minimal failing input: spans ordered as child first, then root, e.g. child `{Operation:"invoke_agent", SpanId:"child", ParentSpanId:"root", InputTokens:10, TotalTokens:10}` followed by root `{Operation:"invoke_agent", SpanId:"root", ParentSpanId:null, InputTokens:100, TotalTokens:100}`. The trace total becomes 10 instead of 100, silently undercounting the headline metric.
  Recommendation: Select the root invoke_agent using the span graph, e.g. parent_span_id null or parent not present among the trace span ids, then fall back deterministically only when no root candidate has usage. Add a child-before-root regression fixture.
- [high] Span backfill backlog is hidden from readiness (src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs:134-138)
  The worker updates health from `GetProjectionStatus()` before the span phase, then processes at most 100 span-backfill records and never consults `GetSpanProjectionStatus()`. Minimal failing input: a Sprint8-upgraded DB with 101 `monitor_ingestions` rows where `span_projected_at IS NULL` and no raw-record trace backlog. One pass reports `projection_backlog:0` and can be `ready` while one record still has no spans. This violates the never-a-silent-gap/backfill contract and hides poison records too.
  Recommendation: Surface span-projection backlog separately in `/health/ready`, logs, or metrics, and refresh it after the span phase. Add a Sprint8-upgrade test where more than one batch remains and readiness/body cannot look clean.
- [medium] A missing traceId span poisons span projection forever (src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs:700-701)
  `monitor_spans.trace_id` is NOT NULL, but `ApplySpanProjection` inserts `DBNull` when a projected span has `TraceId == null`. `OtlpSpanReader` returns null when the span omits `traceId`, so an accepted OTLP payload can make the insert fail, roll back the transaction, leave `span_projected_at` null, and retry every pass. Minimal failing input: `{"resourceSpans":[{"scopeSpans":[{"spans":[{"spanId":"s1","name":"chat gpt-4o"}]}]}]}` after trace projection creates the ingestion row. Blank `traceId:""` does not hit the NOT NULL path, but missing `traceId` does.
  Recommendation: Drop or quarantine spans with null/blank trace ids before inserting, then stamp `span_projected_at` so the raw record degrades instead of wedging. Add a missing-traceId fixture that proves the record is not retried forever.
- [medium] Secondary traces in one OTLP export lose their raw payload (src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs:580-591)
  `ListRawRecordsByTraceId` only matches `raw_records.trace_id`, which stores one primary trace id for the whole export. Projection can fan out multiple `monitor_traces`/`monitor_spans` rows from one raw record, so `/traces/{secondaryTrace}` can show summary and spans but `RawRecords` is empty. Minimal failing input: one raw record whose stored `TraceId` is `trace-1` and payload contains spans for `trace-1` and `trace-2`; `/traces/trace-2` renders `No raw records for this trace.` even though its raw body is in the same record.
  Recommendation: Resolve raw records for a trace through `monitor_spans.raw_record_id` or a trace-to-raw join table, not only `raw_records.trace_id`. Add a multi-trace-one-record trace-detail regression test for the secondary trace.
- [medium] Trace detail renders unbounded raw payloads inline (src/CopilotAgentObservability.LocalMonitor/Pages/TraceDetail.cshtml.cs:72)
  The page loads every raw record for a trace, and the Razor view renders each full `PayloadJson` inline. With raw default-on and a 30 MiB per-request limit, a trace split over many accepted records can produce hundreds of MiB of HTML, freezing the local UI or exhausting memory. Minimal failing input: many accepted OTLP exports for the same trace id, each near `MaxRequestBodyBytes`; opening `/traces/{traceId}` synchronously reads and renders all of them.
  Recommendation: Make raw inline bounded: paginate raw records, collapse by default with size limits/previews, and link to the single-record raw route for full payloads. Add a test that the trace-detail page does not render unlimited raw bodies.

Next steps:
- Fix the four correctness blockers before relying on Sprint9 monitor output.
- Add regression tests using the minimal failing inputs above, especially the child-before-root rollup and Sprint8 span-backfill backlog cases.

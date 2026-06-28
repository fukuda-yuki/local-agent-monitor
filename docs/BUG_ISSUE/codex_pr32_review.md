# GitHub PR #32 Codex Review Findings

Target: Pull Request #32 (Sprint9 monitor agent execution view)
Reviewed Commit: `4de6fc5869`
Review URL: https://github.com/fukuda-yuki/copilot-agent-observability/pull/32#pullrequestreview-4587511467
Verdict: Needs Attention

This file is retained as raw review evidence. Do not plan fixes directly from this file; use the deduplicated cards in `M2-span-projection.md`.

## Findings

### 1. Unsanitized Custom Operation Name Projection
* **Target:** `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorSpanProjectionBuilder.cs:181` (`_ => opName`) in `ClassifyOperation`
* **Risk:** 
  An unexpected `gen_ai.operation.name` value in a span is returned as-is (`opName`) by default. This value is stored in `monitor_spans.operation` and exposed via the `/api/monitor/traces/{traceId}/spans` API.
  OTLP attributes can contain raw prompt text, paths, or PII. If a rogue or new client sends unexpected operation names, this can leak sensitive strings.
* **Recommendation:**
  Allowlist allowed operation name tokens, or drop unexpected operation names (return `null` or a generic token) instead of returning the raw value.

### 2. Unallowlisted Tool Type Projection
* **Target:** `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorSpanProjectionBuilder.cs:115` (`var toolType = OtlpSpanReader.ReadFirstString(span.Attributes, ToolTypeKeys);`) in `ProjectSpan`
* **Risk:**
  `tool_type` is documented as an enum-like field (e.g. `function`, `extension`), but the projection maps the raw `gen_ai.tool.type` attribute directly to the database and API.
  If the client sends unexpected values (which could include local paths, emails, or raw tool argument strings), this bypasses the sanitization policy.
* **Recommendation:**
  Restrict `toolType` to known enum values or drop it if it is invalid.

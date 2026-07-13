# Issue #106 Live Validation Follow-up Record

This record intentionally contains no prompts, responses, tool payloads,
credentials, transcript paths, account/PII values, or machine-specific
home-directory paths. It follows on from
`docs/sprints/issue-99-claude-live-validation.md` without overwriting it.

Local Monitor revision: `09e0bd7` (main). Execution boundary: disposable Local
Monitor instance on a throwaway SQLite database and a non-default loopback
port (`127.0.0.1:4322`), independent of the operator's own running instance.
Claude Code CLI `2.1.207`, Windows. Validation date: 2026-07-14.

## Item 1 — `claude -p` content-enabled synthetic-marker run

Operator gave explicit authorization, distinct from the tool-call
authorization already in effect, to enable `OTEL_LOG_USER_PROMPTS=1` for one
synthetic, harmless marker string (no real prompt/response content).

- Ran `claude -p` with `OTEL_LOG_USER_PROMPTS=1` and a synthetic marker
  prompt. Exit `0`. The raw OTLP payload for the resulting trace's
  `claude_code.interaction` span carries a `user_prompt` attribute containing
  the marker text — confirming the documented content gate
  (`docs/specifications/contracts/source-capabilities/v1/claude-code/otel-mapping.json`
  field `otel.user_prompt`) works as documented at the producer/ingestion
  level.
- Sanitized surfaces correctly withheld the content: `GET
  /api/monitor/traces` never included the marker text; only the raw route
  (`GET /traces/{id}/raw`) — HTML-escaped, inert text, consistent with this
  repository's local-first display posture (D020) — contained it.
- **Finding:** the sanitized `content_state` field for this trace (and every
  other OTel-sourced trace observed in this session, capture-enabled or not)
  reported `unsupported`, never `available`. Per
  `docs/specifications/interfaces/source-schema-drift-claude-code.md:408-411`,
  `capture_content_state` must be `available` when capture was explicitly
  enabled and an allowed content-bearing field was emitted — both conditions
  were met here. Reading
  `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
  (`FixedOtlpTraceSourceMetadataProvider.Default`, ~line 1508-1516) shows
  `SourceCaptureContentState.Unsupported` is a hardcoded constant for every
  OTel-ingested trace; no code path in
  `src/CopilotAgentObservability.Telemetry/Monitoring/ClaudeCodeSpanAdapter.cs`
  or elsewhere computes `available`/`redacted` from the actual gate/field
  state. This is an unimplemented contract rule, not a live-only
  classification side effect — filed as
  [fukuda-yuki/local-agent-monitor#107](https://github.com/fukuda-yuki/local-agent-monitor/issues/107).
- **Result:** **blocked** — `content_state_never_computed_for_otel_surface`.
  The synthetic-marker run itself succeeded and sanitized isolation held; the
  specific acceptance check ("confirm `content_state` transitions correctly")
  cannot pass while `content_state` is a hardcoded constant. Not re-classified
  as passed.

## Item 2 — Hook-based exact/native-session binding

Configured a disposable Claude Code Hook forwarder (`hook-forward --source
claude-code`) in a throwaway project directory's `.claude/settings.json` for
`SessionStart`, `UserPromptSubmit`, and `Stop`, per the
[exact-binding contract](../specifications/contracts/source-capabilities/v1/claude-code/exact-binding.md).
Ran `claude -p` with both the Hook forwarder and OTel export
(`OTEL_EXPORTER_OTLP_ENDPOINT` pointed at the same disposable monitor) active
simultaneously.

- The Hook path produced a Session (`binding_state=hook_only`,
  `completeness=rich`, `source_surfaces=["claude-code"]`) — the Hook envelope
  and forwarder worked end-to-end.
- The OTel trace for the same invocation carried an identical `session.id`
  attribute (byte-equal to the Hook session's native session ID), which is
  exactly the allowed binding evidence
  (exact-binding.md's "Identical native session ID" row).
- **Finding:** the two records never merged; the OTel side stayed a separate,
  unbound session (`binding_state=otel_only`,
  `completeness_reason_codes=["missing_native_session_id", ...]`) even after
  re-checking past any projection lag. Reading
  `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionOtelEnricher.cs`
  shows the dedicated Claude exact-binding path (`ProcessClaude`, which reads
  `session.id` from the raw payload and resolves
  `SessionBindingKind.Native` correctly) only runs when
  `ProjectedSpan.IsClaudeCode` is true, which requires
  `source_adapter == "claude-code-otel"`. Every trace observed in this and
  the #99 session classifies as the generic `source_adapter=raw-otlp` /
  `compatibility_state=schema_drift_detected` (the `any_value.int`/`double`
  finding recorded in #99) — so `ProcessClaude` never executes for any real
  producer trace today, independent of whether the session IDs actually
  match.
- This is not a defect in the binding resolver: its logic is correct and
  matches the contract exactly once reached. It is unreachable because the
  producer is never promoted from `raw-otlp` to `claude-code-otel`. That
  promotion question is the same one already recorded as evidence-only in
  #99 and is explicitly out of scope to re-litigate here (per #106's own
  constraints). No new issue filed for the binding resolver itself.
- **Result:** **blocked** — `otel_adapter_promotion_pending` (external
  blocker: the #99-recorded schema-fingerprint/adapter-promotion decision,
  which also gates Item 1's content-state derivation above). Not
  `no_hook_forwarder_configured` — a Hook forwarder was configured and
  exercised successfully this pass, superseding that specific #99 reason
  code, but exact binding still cannot be observed positively or negatively
  until adapter promotion happens.

## Item 3 — Local Monitor restart/reconnect during an active Claude Code session

Started a Claude Code session (`claude -p`), ingested one trace, restarted
the disposable Local Monitor process (same DB file, same port) mid-session,
then resumed the same native session (`claude --resume <id> -p`) across the
restart boundary.

- `GET /health/ready` returned `ready` immediately after restart; the
  previously ingested trace was present, unchanged, with no duplication.
- The resumed turn ingested as a new, distinct trace (expected: `claude -p`
  does not share `trace_id` across turns; only the native session ID is
  shared, which is not carried by the OTel-only path per exact-binding.md).
  No crash, no duplicate trace, no data loss.
- **Result:** **passed**.

## Item 4 — `--sanitized-only` mode check for Claude Code

Restarted the disposable Local Monitor with `--sanitized-only` and ingested a
new content-disabled Claude Code trace.

- `GET /traces/{rawRecordId}/raw` returned `404`.
- The trace-detail page returned the sanitized tab shell with
  `data-raw-available="false"` and no full raw link.
- The trace-list page showed a shortened trace ID in place of the raw
  prompt label, matching
  `docs/specifications/layers/telemetry-ingestion.md:432-436` exactly.
- `GET /api/monitor/traces` (always sanitized, independent of this flag)
  showed no change in behavior.
- **Result:** **passed**.

## Closeout classification

| Case | Classification | Reason |
| --- | --- | --- |
| `claude -p` content-enabled synthetic-marker run | **blocked** — `content_state_never_computed_for_otel_surface` | New defect found and filed as issue #107; not re-classified as passed |
| Exact/native-session binding (Hook-based) | **blocked** — `otel_adapter_promotion_pending` | Hook path and binding resolver both verified correct; blocked purely on the already-tracked #99 adapter-promotion question |
| Local Monitor restart/reconnect during an active session | **passed** | No duplicate traces, no crash, no data loss across a real restart and a resumed native session |
| `--sanitized-only` mode check for Claude Code | **passed** | Raw route `404`, sanitized tab shell, shortened trace ID — all per contract |

Per #106's acceptance criteria, none of the four items is left as
`not_attempted_no_blocker_recorded`; each carries an exact, current reason.
Two of the four remain blocked rather than passed. Item 1's blocker is a new,
independently filed implementation defect (issue #107). Item 2's blocker is
the pre-existing #99 schema-drift/adapter-promotion finding, not re-argued
here, now confirmed (with new evidence) to also gate exact-session binding in
addition to content classification.

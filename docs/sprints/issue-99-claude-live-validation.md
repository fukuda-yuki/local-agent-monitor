# Issue #99 Live Validation Record

This record intentionally contains no prompts, responses, tool payloads,
credentials, transcript paths, or machine-specific home-directory paths.
Unknown-field identities below are the repository's existing keyed-hash form
(`sha256:...` / `json:any_value:...` structural labels), never raw attribute
values.

Local Monitor revision: `d539dfb392b55ad6bfd12bfdd99c26e0d0120aac` (main).
Execution boundary: disposable Local Monitor instance on a throwaway SQLite
database and a non-default loopback port (`127.0.0.1:4321`), independent of
the operator's own running instance. Claude Code CLI `2.1.207`, Windows.

## Surface: `claude -p` (print mode)

### Blocker resolved

Sprint22 M5 (`docs/sprints/sprint22-source-drift-claude/milestones/M5-integration/live-validation.md`)
recorded `no_emitted_structural_telemetry` for this surface. That run set
`CLAUDE_CODE_ENABLE_TELEMETRY=1` but not `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1`.
Per upstream documentation, OTel trace/span export is a Beta capability gated
separately from metrics/logs and requires both variables. With both variables
set and a receiver already listening before process start, `claude -p` emitted
a real structural OTel trace.

### Run 1 — content-disabled, no tool call

| Field | Record |
| --- | --- |
| Settings labels | `CLAUDE_CODE_ENABLE_TELEMETRY=1`, `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1`, `OTEL_TRACES_EXPORTER=otlp`, `OTEL_METRICS_EXPORTER=otlp`, `OTEL_LOGS_EXPORTER=otlp`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/json`, `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4321`; content-capture labels left unset (disabled) |
| Opaque trace reference | `e3a37e3412059ed7b0c4bb2832baddc8` |
| Execution result | exit `0`, no timeout |
| Observed capabilities | `otel_traces`: observed (6 spans ingested); `otel_metrics`: export attempted, rejected; `otel_logs`: export attempted, rejected; native session identity: not observed; content capture gate: `unsupported` (disabled as intended) |
| Ingestion result | `source_surface=raw-otlp`, `source_adapter=raw-otlp`, `compatibility_state=schema_drift_detected`, `reason_code=schema_drift_detected`, `next_action=capture_fixture_and_review_mapping` |
| Completeness | `unbound`; reason codes `missing_native_session_id`, `schema_drift_detected` |
| Result | **passed** (structural OTel trace telemetry now reproducible for this surface) with a new compatibility finding below |

### Run 2 — content-disabled, one authorized safe tool call (`ls`)

Same environment as Run 1, invoked with `--allowedTools "Bash(ls)"` (no broader
permission bypass). Result: exit `0`, 6 spans ingested, identical
`schema_drift_detected` classification and identical unknown-field identity as
Run 1.

### Run 3 — content-disabled, deterministic tool failure

Operator explicitly authorized `--permission-mode bypassPermissions` for this
and the following run. Prompt was bounded to one exact, safe, read-only
command (`cat` of a filename chosen to not exist; no file creation, no retry
with a different name). Trace `0ed9d1c8f56141a403bbcf023c29a110`: 6 spans,
`error_count=1`, `trace_status=recovered`, `compatibility_state=
schema_drift_detected` (same unknown identity as Run 1/2, count 11). The error
status was recognized structurally (`status=error` resolves correctly in
`ClaudeCodeSpanAdapter`).

### Run 4 — content-disabled, sub-agent delegation (Task tool)

Bounded prompt explicitly instructed delegation of a read-only `ls` listing to
a sub-agent via the Task tool, `--max-turns 4`. Trace
`7d4f966a5ac4d326abc956e0deee608f`: 11 spans (vs. 6 baseline), `error_count=0`,
`trace_status=ok`, same `schema_drift_detected` classification (unknown
identity count 9-11 across four ingest batches from this run and Run 3;
`unknown_span_count=0` and `unknown_event_count=0` in every batch — only the
one attribute-representation identity recurs).

Raw span names for this trace (structural OTel operation identifiers only,
not content) were confirmed directly from `raw_records.payload_json`:
`claude_code.interaction`, `claude_code.llm_request` (x4), `claude_code.tool`
(x2), `claude_code.tool.blocked_on_user` (x2), `claude_code.tool.execution`
(x2) — an 11-span nested tree consistent with a sub-agent turn structure.
The sub-agent's own `llm_request` and `tool` spans are parented by the Task
tool's `claude_code.tool.execution` span and carry an `agent_id` attribute
key.

### Compatibility finding: `any_value.int` arrives as `double`

Both ingest batches recorded exactly one unknown-attribute identity:

```
kind: attribute
name: json:any_value:known:any_value.int:actual:double
occurrence_count: 11
```

This is a *known field, wrong representation* case, not an unrecognized
attribute key: the pinned OTLP JSON descriptor
(`contracts/source-capabilities/v1/otlp-trace-structural-v1.md`) expects
integer-valued `AnyValue` fields as `intValue`; Claude Code's JS/Bun-based OTLP
JSON exporter serializes at least one such field as `doubleValue`. Because the
same identity recurs at the same count in both batches, this looks systematic
for this producer version rather than incidental.

This is recorded as evidence only. Whether the descriptor should accept
`double` for this identity, or remain intentionally strict, is a product
decision for the repository owner and is out of scope for this record.

### Implementation finding: `ClaudeCodeSpanAdapter` never classifies category/operation

Distinct from the compatibility finding above, and confirmed by reading
`src/CopilotAgentObservability.Telemetry/Monitoring/ClaudeCodeSpanAdapter.cs`
directly (not a live-validation inference): every span whose name matches the
adapter's recognized set (`claude_code.interaction`, `.llm_request`, `.tool`,
`.tool.blocked_on_user`, `.tool.execution`, `.hook`) is projected with
`Category` hardcoded to the literal string `"unknown"` and `Operation`
hardcoded to `null` (lines 38-39 of that file), regardless of which of the six
names matched. Model, token, tool-name, status, and timing fields are
correctly extracted; category/operation are not.

This is verified against real producer data: Run 4's sub-agent trace contains
literal `claude_code.tool` spans (name match confirmed above), yet
`GET /api/monitor/traces/{id}/agent-graph` returned
`agent_presence: "none_detected"`, `subagent_invocation_count: 0`, and every
span's sanitized `category`/`operation` came back `"unknown"`/`null` through
`GET /api/monitor/traces/{id}/spans`.

Per `docs/specifications/interfaces/trace-agent-execution-graph.md`, Agent
nodes are defined as spans with `category == agent_invocation` or
`operation == invoke_agent`. Because `ClaudeCodeSpanAdapter` never emits
either value, Claude Code sub-agent/tool-category detection cannot currently
succeed for any input, independent of the schema-drift finding above.

**Correction (same session):** this is not an unnoticed code defect. The
canonical mapping contract
(`docs/specifications/contracts/source-capabilities/v1/claude-code/otel-mapping.json`)
explicitly pins `otel.span_name` as `documented_unmapped` with
`absence_behavior: "Keep Operation null unless a later adapter specifies an
exact recognized-name transform"`, under
`mapping_status: "documentation_only_not_live_observed"` and the manifest
rule that availability changes only from approved observed producer
evidence. The adapter faithfully implements that frozen contract. What has
changed is the precondition: Runs 1-4 in this record are the first approved
live producer structure observations, so the contract's own promotion
condition is now satisfiable. The correct follow-up is a contract-promotion
issue (update the mapping from live evidence, then implement the exact
recognized-name transform in the adapter), not a hotfix against the current
contract.

### Metrics / logs endpoint mismatch (expected, not a defect)

`OTEL diag error: 404 unsupported_endpoint "Only /v1/traces is supported."`
appeared for both the metrics and logs exporters. Local Monitor's OTLP
receiver implements only `/v1/traces`; `telemetry-ingestion.md` lists Claude
Code as reference-only and does not require metrics/logs ingestion. This is
therefore a scope note, not a schema-drift or adapter defect: Claude Code's
metrics and logs signals are currently un-ingestable by design, independent of
the trace-side finding above.

## Surface: interactive Claude Code CLI

Status unchanged from Sprint22 M5: `interactive_tty_unavailable`. This
session's execution boundary (redirected stdin/stdout/stderr) cannot start an
authenticated interactive TTY session. Requires the operator's own real
terminal; named follow-up
`task-7-interactive-claude-code-live-inventory-in-tty-authenticated-session`
remains the accepted path.

## Surface: Claude Agent SDK

Status unchanged from Sprint22 M5: `agent_sdk_package_and_credential_unavailable`.
No supported Agent SDK package or authorized credential was available in this
session; no SDK query was started. Named follow-up `#63-live-agent-sdk-inventory`
remains the accepted path.

## Closeout classification

Per #99's own close condition, every matrix case is classified below as
`passed | blocked | not-applicable`. `blocked`/`not-applicable` are terminal
states for this closeout and are not treated as `passed`.

| Case | Classification | Reason |
| --- | --- | --- |
| `claude -p` content-disabled structural telemetry | **passed** | Run 1 (this record) |
| Successful tool call | **passed** | Run 2 |
| Failed tool call / retry | **passed** | Run 3 |
| Sub-agent invocation structural evidence | **passed** | Run 4; spans captured with correct hierarchy. Category/operation classification was fixed separately in #101 (merged) |
| Permission-wait span structure | **passed** | `claude_code.tool.blocked_on_user` spans with `duration_ms`/`decision` attribute keys were observed in Runs 3 and 4 even under `--permission-mode bypassPermissions`; only key names were confirmed, not values, per repository-safety practice. Corrects the earlier "not attempted" note in this record |
| Diagnostic states (supported/known-fingerprint/new-fingerprint) | **passed** | `schema_drift_detected` (new-fingerprint case) observed and correctly degraded, not silently dropped |
| Session/trace list, detail, flow/waterfall display | **passed** | Verified after #101: real trace shows `turn_count=2`, `tool_call_count=1`, `primary_model` populated (previously all-unknown) |
| `claude -p` content-enabled synthetic-marker run | **blocked** — `not_authorized_this_session` | Requires a separate explicit authorization distinct from the tool-call authorization already given in this session; operator has not yet given it |
| Exact/native-session binding (Hook-based) | **not-applicable (this pass)** — `no_hook_forwarder_configured` | All runs used the OTel-only ingestion path (`binding_state=otel_only`); no Hook forwarder was configured for this validation pass, so Hook-side binding was never exercised, positively or negatively |
| Local Monitor restart/reconnect during an active session | **blocked** — `not_attempted_no_blocker_recorded` | No external blocker; simply not exercised in this session. Deferred rather than fabricated |
| `--sanitized-only` mode check for Claude Code ingestion/UI | **blocked** — `not_attempted_no_blocker_recorded` | Not exercised in this session for the Claude surface specifically |
| Interactive Claude Code CLI | **blocked** — `interactive_tty_unavailable` | Unchanged from Sprint22 M5; requires the operator's own real terminal |
| Claude Agent SDK | **blocked** — `agent_sdk_package_and_credential_unavailable` | Unchanged from Sprint22 M5; requires an installed SDK package and authorized credential |

The `schema_drift_detected` finding does not block close by itself under
#99's own contract (a "new fingerprint" is a valid recorded outcome, not a
failure), but it does mean no real Claude Code trace will project as
`supported` until the repository owner reviews the `any_value.int`/`double`
representation question (tracked here, not as a fix issue).

The `ClaudeCodeSpanAdapter` category/operation gap identified during this
validation was a contract-promotion opportunity, not an unnoticed defect; it
was resolved in Issue #101 (merged to `main`) and verified end-to-end against
a real `claude -p` trace.

## Follow-up

The four `blocked`/`not-applicable` items above (content-enabled run, Hook
binding, restart/reconnect, `--sanitized-only` check) are carried forward to
a new follow-up issue rather than blocking this record's closeout matrix.

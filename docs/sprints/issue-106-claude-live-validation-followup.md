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

## Final run — 2026-07-18 (frozen #104 candidate)

This section is appended per the Issue #106 final-run contract and does not
modify any prior section. All classifications below apply only to the frozen
candidate SHA; a replacement candidate invalidates these results and requires
a clean rerun.

### Candidate and pinned environment

| Field | Value |
| --- | --- |
| Immutable candidate SHA | `54d758a260f347cc31a3191d342ad509eb62d81f` |
| Candidate manifest reference | `docs/superpowers/plans/2026-07-17-issue-104-claude-first-trace/handoff-106.md` (frozen `95d2985`, merged `70206ce`) |
| Monitor revision | Candidate worktree `git rev-parse HEAD` = candidate SHA; built with `dotnet build CopilotAgentObservability.slnx` exit `0` in that worktree |
| Claude Code CLI version | `2.1.214 (Claude Code)` (supported: `>= 2.1.207`) |
| OS and execution boundary | Windows native shell; disposable Local Monitor process per case; disposable SQLite database per case; throwaway Claude project directory; OS-temp disposable root |
| Disposable worktree identity | Clean detached worktree `.worktrees/issue-106-final-candidate` at exactly the candidate SHA; `git status --porcelain` empty at the gate |
| Loopback Monitor URL/port | `http://127.0.0.1:4323` |
| OTLP endpoint | `/v1/traces` on the loopback origin |
| OTLP protocol/signal | Live producer: OTLP HTTP/protobuf per-signal trace (`OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf`, `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=<origin>/v1/traces`). Negative-binding arrangement only: synthetic OTLP JSON direct POST (fixture-only, recorded as such) |
| Hook state | Case 2 only: throwaway project `.claude/settings.json` with `SessionStart`/`UserPromptSubmit`/`Stop` invoking the candidate monitor `hook-forward --endpoint <loopback>/api/session-ingest/v1/events --timeout-ms 2000 --source claude-code --source-version 2.1.214`; disabled for all other cases |
| Content-enabled gate state | `OTEL_LOG_USER_PROMPTS=1` for case 1A only, under distinct explicit operator authorization captured as preflight `-OperatorAuthorized` (preflight exit `0`, authorization check PASS) |
| Gate-disabled state | `OTEL_LOG_USER_PROMPTS` unset for 1B; observed producer key state recorded below |
| Sanitized-only flag | `false` for cases 1-3; `true` for case 4 (`--db <disposable> --url <loopback> --sanitized-only`) |
| Restart timing (case 3) | Ordered: start, ready `200`, live turn ingested (1 trace), stop proven disposable PID, restart same db/port, ready `200`, resume native session, resumed turn ingested (2nd trace) |
| Truncated marker references | 1A `sha256:861aa6dc1f3f`; 1B `-p` `sha256:b7bb1d98dab8`; 1B interactive `sha256:6d4053d32c0a`; case 2 `sha256:de43aedbb0dd`; case 3 `sha256:b8b06c397814`; case 4 `sha256:4a72922735fd` |

Preflight ran from the preparation worktree (its build check proves the
preparation-side build); the candidate monitor build was separately proven by
the solution build in the candidate worktree, and every monitor process in
this run was started from the candidate worktree binaries.

### Case 1 — content-enabled and gate-disabled `claude -p`

- Content-enabled classification: **passed**.
- 1A observations: `claude -p` exit `0`; the raw `claude_code.interaction`
  span carried the `user_prompt` attribute key containing the runtime marker
  (`key_present`, marker `MATCH`, observed on the raw route only and not
  copied to evidence); derived `content_state=available` per the #107 rule —
  the prior `content_state_never_computed_for_otel_surface` blocker is
  resolved in this candidate; sanitized traces/list/spans withheld the marker
  everywhere. `source_adapter` remained `raw-otlp` with
  `compatibility_state=schema_drift_detected` (the #99 promotion question,
  unchanged and out of scope here); content-state derivation and sanitized
  isolation — this case's acceptance targets — held.
- Gate-disabled classification: **blocked** — exact blocker: the #110
  presence-only derivation defect is now confirmed against the real producer
  (see below); the spec-first fix belongs to #110's follow-up, not to this
  validation-only Issue.
- Gate-disabled `user_prompt` key observation (#110, both required surfaces):
  `key_present_nonempty` with a literal `<REDACTED>`-style placeholder value
  plus a `user_prompt_length` integer attribute, observed on the real
  `claude -p` export AND on a real interactive CLI export (genuine TTY
  session driven via UI automation; exited cleanly to flush). The marker
  never appeared in the gate-disabled raw payloads. The receiver derived
  `content_state=available` for these gate-disabled batches, which is wrong
  per the spec's own definition of `available`. #110 is resolved on its
  second branch: the producer emits the key with a placeholder/redacted value
  when the gate is off, so the presence-only rule in the spec and
  `ClaudeOtlpCaptureContentStateResolver` requires a spec-first follow-up
  (value-aware derivation; the observed placeholder may also qualify as an
  explicit redaction signal for the `redacted` state).
- Producer command exit statuses: `0` (1A `-p`), `0` (1B `-p`), interactive
  session completed and exited cleanly.
- Leak scan after case: exit `0` (PASS) for 1A, 1B `-p`, and 1B interactive.

### Case 2 — Hook + OTel exact/native-session binding

- Positive classification: **passed** (prior `otel_adapter_promotion_pending`
  blocker no longer applies on this candidate).
- Positive observations: one live `claude -p` with Hook and OTel active in
  the same run produced exactly one Session with
  `binding_state=exact_linked`, `completeness=full`,
  `source_surfaces=["claude-code"]`; the Session's native ID
  (`binding_kind=native`) is byte-equal to the producer-reported session ID
  (case-sensitive comparison `True`); `hook-forward` and OTel export both
  exit `0`. No trace-ID, cwd, prompt, time, or label proximity was used as
  evidence.
- Negative #109 classification: **passed** — #109 is verified fixed in the
  candidate.
- Negative observations: on a fresh database, a live Hook-only session
  (`hook_only`, native ID present) plus synthetic OTLP JSON spans sharing one
  trace ID with a non-matching `session.id` (fixture-only arrangement,
  recorded as such) produced two separate Sessions (`hook_only` and
  `otel_only`) and no `exact_linked` anywhere: shared trace ID and generic
  event labeling were not accepted as exact evidence.
- Leak scan after case: exit `0` (PASS).

### Case 3 — restart/reconnect

- Classification: **passed**.
- Observations: first live turn ingested one trace; the proven disposable
  monitor process was stopped mid-session and restarted on the same database
  and port; `/health/ready` returned `200` immediately; the pre-restart trace
  remained present, unchanged and non-duplicated; the same native session was
  resumed across the restart boundary (`claude --resume <id> -p`, exit `0`,
  no error); the resumed turn ingested as one new distinct trace (expected:
  turns do not share `trace_id`); final state 2 traces, no crash, no
  duplicate, no silent loss.
- Leak scan after case: exit `0` (PASS).

### Case 4 — `--sanitized-only`

- Classification: **passed**.
- Observations: fresh database, monitor started with `--sanitized-only`;
  fresh live content-disabled Claude trace ingested; raw route returned
  `404`; the trace-detail shell carried `data-raw-available="false"` with no
  raw section; the trace list showed a shortened TraceId; sanitized detail
  and spans endpoints returned `200`; the marker appeared nowhere in
  sanitized API/UI output.
- Leak scan after case: exit `0` (PASS).

### Closeout classification (this final run)

| Case | Classification | Reason |
| --- | --- | --- |
| `claude -p` content-enabled synthetic-marker run | **passed** | `content_state=available` derived (#107 fix verified live); sanitized isolation held |
| Gate-disabled content-state run (#110) | **blocked** — `presence_only_rule_derives_available_for_redacted_placeholder` | Real producer emits `user_prompt` key with a redacted placeholder when the gate is off (both `-p` and interactive); #110 resolved, spec-first follow-up required |
| Exact/native-session binding (Hook + OTel) | **passed** (positive and #109 negative) | `exact_linked` from byte-equal native session ID only; shared-trace/generic-label arrangement produced no false `exact_linked` |
| Local Monitor restart/reconnect | **passed** | No duplicate, crash, or silent loss across a real restart and a resumed native session |
| `--sanitized-only` | **passed** | Raw route `404`, sanitized shell/list/spans per contract, no marker in sanitized output |

No case is left `not_attempted_no_blocker_recorded`. Cleanup: environment
variables were process-local per invocation; the throwaway Hook configuration
and every disposable database live under the OS-temp disposable root, which
was removed by the guarded `cleanup.ps1` after the final leak scans (all
scans exit `0`; the preparation-worktree repository output also scanned
clean).

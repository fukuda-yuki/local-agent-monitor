# M2 — Sanitized per-span projection + token rollup + sanitization

Feature: per-span sanitized projection (`MonitorSpanProjection`), the
no-double-count trace token rollup (`MonitorTraceRollup`), and the per-field
sanitization policy.

## Fix-unit index

| Card | Severity | Fix unit | Plan note |
| --- | --- | --- | --- |
| M2-1 | High | Root agent token rollup | Fixed: root selected by span hierarchy; child-before-root regression added. |
| M2-2 | Medium | Agent usage presence detection | Fixed: any usage component counts, including total-only usage. |
| M2-3 | Low | `turn_count` semantics | Closed without code change: current spec defines all `chat` / LLM spans. |
| M2-4 | Low | `error_type` token sanitization | Fixed: identifier/class-token policy replaces generic secret substring guard. |
| M2-5 | Low | `finish_reasons` parsing | Fixed: malformed serialized arrays are dropped. |
| M2-6 | Medium | Multiple root agent token rollup | Fixed: multiple root `invoke_agent` usage fields are summed. |
| M2-7 | Low | Token rollup overflow | Fixed: summed / derived token fields that exceed the nullable `int` projection range become null, not wrapped values. |

Primary next plan: M2-1 + M2-2 as one token-rollup fix plan. Keep M2-3 as a
decision item unless the intended turn-count semantics are confirmed.

Source of truth:
`docs/sprints/sprint9-monitor-agent-execution-view/README.md` — "Token rollup
rule (no double count)", "Per-field sanitization policy";
`docs/specifications/layers/raw-store-normalization.md`.

Key files: `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorTraceRollup.cs`,
`.../Monitoring/MonitorSpanProjectionBuilder.cs`,
`.../Normalization/OtlpSpanReader.cs`, `.../Normalization/MeasurementSanitizer.cs`.

---

<a id="M2-1"></a>

## M2-1 — Trace token total selects the *first* `invoke_agent` by storage order, not the true root — High (confidence: Medium) [Codex-corroborated via M3-1 path; Claude sub-agent]

- **Location:** `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorTraceRollup.cs:36-41` and `:78-83`.
- **Spec:** README "Token rollup rule": *"Per-trace total = the trace's
  `invoke_agent` usage when present … Sub-agent (child `invoke_agent`) usage is
  attributed to that sub-agent and rolled into the parent only through the
  parent's own agent-level total, not by re-summing child `chat` spans."*
  "The trace's `invoke_agent`" means the **root** agent invocation.
- **Observed:** `rootInvokeAgent` is set to the first iterated `invoke_agent` span
  that has `InputTokens` (`rootInvokeAgent is null && span.InputTokens.HasValue`).
  There is **no `parent_span_id` / depth check** distinguishing the root agent
  from a sub-agent. The iteration order is the persisted order
  (`ORDER BY raw_record_id, span_ordinal` in
  `RawTelemetryStore.cs:738`), i.e. OTLP payload order across raw records.
  `MonitorSpanProjection.ParentSpanId` is captured and persisted but unused here.
- **Impact:** A trace with a sub-agent has **two** `invoke_agent` spans (root +
  child). OTLP does not guarantee parent-before-child ordering, and across a
  fan-out the child can land in a lower `raw_record_id` (the M6 live run already
  split one trace across raw records 6 and 8). If the child `invoke_agent` sorts
  first, the trace `total_tokens` becomes the **sub-agent's** usage instead of the
  root's — a silent under-count of the headline metric the rollup exists to fix.
  In the M6 Part-B capture the root happened to sort first, so the bug did not
  trigger; nothing in the code guarantees that.
- **Recommendation:** Pick the root as the `invoke_agent` whose `parent_span_id`
  is null or not present among the trace's span ids (the same root rule the
  trace-detail tree already uses in `TraceDetail.cshtml.cs:98-111`), rather than
  the first by ordinal. Add a fixture where the child `invoke_agent` precedes the
  root.
- **Resolution:** Fixed. Rollup now selects root `invoke_agent` spans by span
  hierarchy, sums multiple root usage fields, and falls back to `chat` / LLM
  sums when no root agent-level usage is present. Child `invoke_agent` usage is
  not promoted to the trace-level headline.

<a id="M2-2"></a>

## M2-2 — Root `invoke_agent` that emits only `total_tokens` (no `input_tokens`) is ignored — Medium (confidence: Medium) [Claude sub-agent]

- **Location:** `MonitorTraceRollup.cs:37` (`span.InputTokens.HasValue`); mirrored
  in `RawMeasurementNormalizer.cs` and in the per-span total derivation
  `MonitorSpanProjectionBuilder.cs:132-134` (requires both input and output).
- **Spec:** README: *"per-trace total = the trace's `invoke_agent` usage when
  present."* "Present" should not be keyed solely on input tokens.
- **Observed:** "agent total present" is gated on `InputTokens.HasValue` only.
  An `invoke_agent` span carrying `gen_ai.usage.total_tokens` (and/or output) but
  no `input_tokens` is treated as "no agent total", so the rollup falls back to
  summing the `chat` spans; and because the per-span `total_tokens` derivation
  also needs both input and output, that agent-level total is dropped entirely.
- **Impact:** Wrong/absent trace total when the client emits an agent-level total
  without a separate input count. Lower likelihood than M2-1 because the
  documented usage contract usually includes input tokens.
- **Recommendation:** Treat the `invoke_agent` total as present when **any** of
  `total/input/output` is set; derive `total` from whichever components exist.
- **Resolution:** Fixed. Monitor rollup and raw normalization now treat any
  input/output/total usage component as agent-level usage.

<a id="M2-3"></a>

## M2-3 — `turn_count` includes sub-agent `chat` spans — Low (confidence: Low) [Claude sub-agent]

- **Location:** `MonitorTraceRollup.cs:43-46`.
- **Spec:** README/raw-store-normalization define `turn_count` as the count of
  `chat` / LLM spans (literally all of them), so the current code is arguably
  spec-compliant; it conflicts only with the user-goal framing "Total tokens
  **per turn**" where a turn is a root-agent turn.
- **Observed:** Every span with `Operation == "chat"` or `Category == "llm_call"`
  is counted regardless of depth, so a sub-agent's internal turns inflate the
  parent trace's `turn_count` (the M6 Part-B trace would count the Explore
  sub-agent's two `chat` spans as parent turns).
- **Impact:** Cosmetic/semantic over-count of `turn_count` for traces with
  sub-agents. No token double-count.
- **Recommendation:** Decide the intended semantics; if "root-agent turns",
  exclude spans whose nearest `invoke_agent` ancestor is not the root. If the
  literal "all chat spans" is intended, no change — but pin it in the spec.
- **Resolution:** Closed without code change. The current source of truth defines
  `turn_count` as all `chat` / LLM spans, so changing it would require a new spec
  decision.

<a id="M2-4"></a>

## M2-4 — Free-form-name guard drops legitimate `error.type` class tokens — Low (confidence: High) [Claude sub-agent]

- **Location:** `MonitorSpanProjectionBuilder.cs:214-218` (`SanitizeErrorType` →
  `MeasurementSanitizer.SanitizeFreeFormName` → `IsUnsafeStringValue`,
  `MeasurementSanitizer.cs:~99`).
- **Spec:** README "Per-field sanitization policy": *"`error.type`: the class
  token only … same guard + max length applies."* — a class token is expected to
  survive the guard.
- **Observed:** `IsUnsafeStringValue` rejects any value containing
  `secret`/`password`/`token`-like substrings (case-insensitive). Real exception
  class tokens such as `TokenExpiredError`, `SecretsManagerException`,
  `PasswordPolicyException` therefore fail the guard and are dropped
  (`error_type` becomes null).
- **Impact:** Fails **closed** (no leak), so it is spec-safe, but it silently
  removes legitimate error classes — degrading the error display the feature
  exists to provide.
- **Recommendation:** For the enum-like `error.type` token, prefer a tighter
  policy (e.g. allow `[A-Za-z0-9._]` identifier tokens up to max length) over the
  generic free-form-secret heuristic, so genuine class names survive while
  emails/paths/secrets are still rejected.
- **Resolution:** Fixed. `error_type` now accepts identifier-like class tokens
  (`[A-Za-z0-9._]`) up to the pinned max length and drops malformed strings.

<a id="M2-5"></a>

## M2-5 — `finish_reasons` parsing drops non-string elements; malformed array routed through the free-form guard — Low (confidence: Medium) [Claude sub-agent]

- **Location:** `MonitorSpanProjectionBuilder.cs:237-250`.
- **Observed:** Only `JsonValueKind.String` array items are collected (numbers /
  objects ignored); on `JsonException` the whole raw string is pushed through
  `SanitizeFreeFormName` and stored if it passes.
- **Impact:** Minor data loss for non-string finish reasons; a malformed array
  string can be stored verbatim (still guard-filtered). Low because
  `finish_reasons` is enum-like and the leak path is guarded.
- **Recommendation:** On parse failure, drop rather than store the raw text; only
  accept string tokens.
- **Resolution:** Fixed. Malformed serialized arrays are dropped; only parsed or
  comma-separated string tokens are considered.

<a id="M2-6"></a>

## M2-6 — Multiple root `invoke_agent` spans silently under-count trace-level tokens — Medium (confidence: Medium) [Codex adversarial follow-up]

- **Location:** `MonitorTraceRollup.cs:42-91`; mirrored in
  `RawMeasurementNormalizer.cs`.
- **Spec:** `raw-store-normalization.md` now pins trace-level `invoke_agent`
  usage as the sum of root `invoke_agent` usage fields when multiple root agent
  invocations are present.
- **Observed:** The first root `invoke_agent` carrying usage was selected as a
  single representative. `agent_invocation_count` still counted all agent spans,
  so a trace with two root agent invocations displayed a count of 2 but only the
  first root's headline token values.
- **Impact:** Silent under-count of `input_tokens`, `output_tokens`, and
  `total_tokens` in the trace list / detail headline for multi-root traces.
- **Recommendation:** Sum root `invoke_agent` usage fields and keep child
  `invoke_agent` excluded from the trace-level rollup to preserve the
  no-double-count rule.
- **Resolution:** Fixed. Root `invoke_agent` token fields are accumulated across
  all root usage-bearing spans, with regression fixtures for monitor rollup and
  raw measurement normalization.

<a id="M2-7"></a>

## M2-7 — Chat fallback token sum can wrap `int` and persist negative tokens — Low (confidence: High) [Codex adversarial follow-up]

- **Location:** `MonitorTraceRollup.cs:124-127`; mirrored in
  `RawMeasurementNormalizer.cs` and per-span total derivation in
  `MonitorSpanProjectionBuilder.cs`.
- **Spec:** `raw-store-normalization.md` now pins range-safe token rollup:
  derived or summed token values outside the nullable `int` projection field
  range are stored as null rather than wrapped.
- **Observed:** `int?` addition used unchecked `int` arithmetic. Two chat spans
  with `gen_ai.usage.input_tokens = 2000000000` overflowed to a negative
  `input_tokens` value during fallback rollup.
- **Impact:** A malformed local OTLP export could corrupt the monitor/API
  headline token fields with impossible negative counts.
- **Recommendation:** Accumulate with wider arithmetic and apply an explicit
  projection-range decision before exposing / storing the field.
- **Resolution:** Fixed. Token accumulation uses `long`, then converts to
  nullable `int`; out-of-range fields become null.

---

## Evaluated but not filed

- **No-double-count core rule:** verified **correct** — `invoke_agent` totals are
  never *added* to `chat` per-call tokens; the rollup uses either the
  `invoke_agent` total or the `chat` sum, exclusively
  (`MonitorTraceRollup.cs:78-89`). The inherited `RawMeasurementNormalizer`
  over-count is genuinely fixed.
- **Per-field sanitization (positive cases):** `tool_name`, `mcp_tool_name`,
  `agent_name` pass through `SanitizeFreeFormName` (guard + truncation); a failing
  value is dropped while the row keeps its other columns; `error.type` reads only
  the `error.type` key (never `exception.message`); `mcp_server_hash` is stored
  from the client hash only. All correct.
- **parent_span_id-absent / missing-usage / negative-duration:** builder degrades
  (null) instead of throwing. Correct.

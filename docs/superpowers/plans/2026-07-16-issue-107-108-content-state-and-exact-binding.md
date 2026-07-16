# Issue #107 / #108 Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix Issue #107 (OTel `capture_content_state` hardcoded to `unsupported`) and Issue #108 (exact session binding unreachable because it is gated on `claude-code-otel` adapter promotion), spec-first.

**Architecture:** (1) Derive `capture_content_state` per ingestion batch from the decoded OTLP payload instead of the fixed provider constant, scoped to batches containing recognized `claude_code.*` spans. (2) In `SqliteSessionOtelEnricher`, run the already-implemented exact native-session-ID resolver in the generic (non-promoted) span path so binding is reachable on its own `session.id` byte-equality evidence, without fabricating `claude-code-otel` provenance labels.

**Tech Stack:** .NET (existing solution), xunit, SQLite, existing `OtlpSpanReader` JSON helpers.

## Global Constraints

Copied from the pinned specs and AGENTS.md — every task's requirements include these:

- Spec first: product behavior changes land in `docs/specifications/` (and `docs/decisions.md` where policy) **before or together with** the code change (AGENTS.md / spec-update skill).
- `capture_content_state` rule (docs/specifications/interfaces/source-schema-drift-claude-code.md): "`available` only when capture was explicitly enabled and an allowed content-bearing field was emitted, `not_captured` when disabled or absent, `redacted` when the source explicitly reports redaction, and `unsupported` when the surface cannot expose the gate."
- Trace/Session `content_state` is the observation value "only when all linked observations agree; otherwise it is null" — this aggregation already exists (`SourceDiagnosticDto.AgreedContentState`) and must not change.
- Exact binding contract (docs/specifications/contracts/source-capabilities/v1/claude-code/exact-binding.md): binding is a closed allowlist; identical-native-session-ID row requires byte-identical UTF-8 values resolving to exactly one Session; "There is no normalization before comparison"; the raw OTel `session.id` "is not persisted as a new native ID".
- Never copy prompt/tool content values into sanitized DTOs, logs, or test assertions — presence only (otel-mapping.json `raw_retained` dispositions; AGENTS.md "no raw/PII in logs or repo").
- No new dependencies. No fallback/dual paths beyond what the updated specs state.
- Wire shape of `/api/monitor/traces` (`ToTraceDto`) and `/api/monitor/trace-list` (`ToTraceListDto`) field sets/ordering is pinned (D042 C6) — values may change per the new derivation, shape may not.
- Commit messages: start with `Issue #107:` / `Issue #108:` then Conventional Commits; `feat`/`fix` bodies record why.
- Do not push, tag, or create PRs. Local commits only.
- Tests: small synthetic fixtures; no sleeps for coordination; derive assertions from the specs.
- Validation commands (repo-pinned): `dotnet build CopilotAgentObservability.slnx`, `pwsh scripts\test\install-playwright-chromium.ps1`, `dotnet test CopilotAgentObservability.slnx` (targeted `dotnet test <proj> --filter ...` while iterating).

## Shared design decisions (pinned by the orchestrator, approved direction from the user)

- **#107 derivation rule (receiver-side):** For an OTLP trace batch that contains at least one recognized Claude Code span (span `name` in: `claude_code.interaction`, `claude_code.llm_request`, `claude_code.tool`, `claude_code.tool.blocked_on_user`, `claude_code.tool.execution`, `claude_code.hook`):
  - `available` when at least one allowed content-bearing gated field is present in the batch:
    - attribute `user_prompt` on a span named `claude_code.interaction` (producer gate `OTEL_LOG_USER_PROMPTS=1`),
    - span event named `tool.output` on a span named `claude_code.tool` (producer gate `OTEL_LOG_TOOL_CONTENT=1`),
    - attribute `file_path` on a span named `claude_code.tool` (producer gate `OTEL_LOG_TOOL_DETAILS=1`);
  - otherwise `not_captured`.
  - The `error` attribute on `claude_code.tool.execution` is **never** capture evidence: the documented gated (`otel.tool_error_detail`) and ungated (`otel.tool_error_category`) forms share one producer path and are indistinguishable at the receiver.
  - `redacted` is not derivable on this surface (no documented explicit redaction signal); the spec update states this.
  - Batches with **no** recognized Claude span keep the provider metadata value (today `unsupported` for raw OTLP). Adapter-failure diagnostics keep provider metadata unchanged.
- **#108 decision (records the user's resolution of the issue's design question):** the exact native-session-ID resolver is gated **only by its own evidence** — a unique `session.id` attribute on the span byte-identical to exactly one persisted `claude-code` Hook native session ID with binding kind `Native`/`ExplicitResume`/`ExplicitHandoff`. It does not require `claude-code-otel` adapter promotion. Evidence written from a non-promoted span keeps its actual provenance labels (generic path labels, e.g. `otel-exact`; surface stays null — no fabricated `claude-code-otel` adapter or ClaudeCode surface). The promoted `ProcessClaude` path is unchanged.

---

### Task 1: Spec-first updates for #107 and #108

**Files:**
- Modify: `docs/specifications/interfaces/source-schema-drift-claude-code.md` (the `capture_content_state` paragraph area, ~line 408, and the binding/exact-link statements)
- Modify: `docs/specifications/contracts/source-capabilities/v1/claude-code/exact-binding.md` (gating statement)
- Modify: `docs/decisions.md` (append a new decision recording the #108 decoupling)
- Check (grep, update only if they state the old behavior): `docs/requirements.md`, `docs/spec.md`, `docs/task.md`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: the pinned wording Tasks 2 and 3 implement and their reviewers check against.

- [ ] **Step 1: Read the two current spec files end to end** (`source-schema-drift-claude-code.md`, `exact-binding.md`) and `docs/decisions.md` tail for the next decision number.
- [ ] **Step 2: Add the OTel receiver-side derivation rule** to `source-schema-drift-claude-code.md`, immediately after the existing `capture_content_state` paragraph, in the file's existing voice. Content to pin (adapt prose, keep every normative element):

> For raw OTLP trace ingestion, `capture_content_state` is derived per batch. A batch containing at least one recognized `claude_code.*` span derives `available` when an allowed content-bearing gated field is present — the `user_prompt` attribute on `claude_code.interaction` (`OTEL_LOG_USER_PROMPTS=1`), a `tool.output` span event on `claude_code.tool` (`OTEL_LOG_TOOL_CONTENT=1`), or the `file_path` attribute on `claude_code.tool` (`OTEL_LOG_TOOL_DETAILS=1`) — and `not_captured` otherwise. The `error` attribute on `claude_code.tool.execution` is never capture evidence because its gated detail and ungated category forms share one producer path and are indistinguishable at the receiver. `redacted` is not derivable on this surface because no explicit redaction signal is documented. A batch with no recognized Claude span keeps the surface's fixed state (`unsupported` for raw OTLP). Derivation inspects field presence only; content values are never copied out of the raw payload.

- [ ] **Step 3: Add the binding decoupling statement** to `exact-binding.md` (in the section describing the shipped v1 resolver) and mirror one sentence in `source-schema-drift-claude-code.md` where exact linking is described:

> The exact native-session-ID resolver is gated only by its own evidence: a single unambiguous `session.id` attribute on the OTel span whose UTF-8 bytes equal exactly one persisted `claude-code` Hook native session ID with binding kind native, explicit resume, or explicit handoff. It does not require `claude-code-otel` adapter promotion; a span still labeled `raw-otlp` binds on this evidence. Binding never rewrites provenance: evidence stored from a non-promoted span keeps its actual source labels, and the raw `session.id` is still not persisted as a new native ID.

- [ ] **Step 4: Append the decision to `docs/decisions.md`** (next free D number), recording: the #108 question, the chosen answer (capability reachable on its own evidence; adapter promotion still gates everything else, e.g. the promoted `ProcessClaude` semantics), why (the drifted `any_value.int` field has no logical relationship to `session.id` byte-equality; #99 promotion remains open and unaffected), and the date 2026-07-16.
- [ ] **Step 5: Grep for conflicts**: `grep -rn "content_state\|unsupported\|claude-code-otel" docs/requirements.md docs/spec.md` — update any sentence that pins the old fixed-`unsupported` behavior or the promotion-gated binding; leave everything else untouched.
- [ ] **Step 6: Commit**

```bash
git add docs/
git commit -m "Issue #107: docs: pin OTel content-state derivation and evidence-gated exact binding"
```
(body: why — implementation follows in the next commits; #108 decision recorded per user direction.)

---

### Task 2: #107 — derive `capture_content_state` from the OTLP payload

**Files:**
- Create: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/ClaudeOtlpCaptureContentStateResolver.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` (TracePath handler, ~line 880-900: the `SourceObservationBatchDraft.Create(...)` call currently passing `metadata.CaptureContentState`)
- Possibly modify: `src/CopilotAgentObservability.Telemetry/Monitoring/ClaudeCodeSpanAdapter.cs` (expose the recognized-span-name set for reuse instead of duplicating it)
- Test: add resolver unit tests in the Telemetry test project and one ingestion-to-API integration test in the LocalMonitor test project (locate the existing test projects under `tests/`; follow their fixture/helper conventions — e.g. existing MonitorHost ingestion tests that POST OTLP JSON and GET `/api/monitor/traces`).

**Interfaces:**
- Consumes: Task 1's pinned derivation rule.
- Produces: `public static class ClaudeOtlpCaptureContentStateResolver` with `public static SourceCaptureContentState? Derive(string payloadJson)` in namespace `CopilotAgentObservability.Telemetry` — returns `null` when the batch contains no recognized Claude span (caller falls back to provider metadata), else `Available`/`NotCaptured`.

- [ ] **Step 1: Write failing resolver unit tests.** Synthetic OTLP JSON fixtures (marker strings only, e.g. `"synthetic-marker"`, never realistic prompt text). Cases:
  1. `claude_code.interaction` span with `user_prompt` attribute → `Available`.
  2. Recognized Claude spans, no gated content field → `NotCaptured`.
  3. `claude_code.tool` span with `tool.output` event → `Available`.
  4. `claude_code.tool` span with `file_path` attribute → `Available`.
  5. `claude_code.tool.execution` span with `error` attribute only → `NotCaptured`.
  6. No recognized Claude spans (foreign span names) → `null`.
  7. `user_prompt` attribute on a non-Claude span name only → `null`; alongside a content-free Claude span → `NotCaptured` (field must sit on the mapped span).
- [ ] **Step 2: Run the new tests, confirm they fail** (type not found).
- [ ] **Step 3: Implement the resolver.** Parse with `JsonDocument` and the existing `OtlpSpanReader` helpers (`EnumerateArrayProperty`, `ReadString` — same assembly, internal is fine; see `SqliteSessionOtelEnricher.ReadExactClaudeNativeSessionId` for the traversal idiom over `resourceSpans[].scopeSpans[].spans[]`). Reuse `ClaudeCodeSpanAdapter`'s recognized-name set rather than duplicating it (widen its accessibility minimally). Skeleton:

```csharp
public static SourceCaptureContentState? Derive(string payloadJson)
{
    using var document = JsonDocument.Parse(payloadJson);
    var hasClaudeSpan = false;
    var hasContentField = false;
    foreach (var span in /* resourceSpans[].scopeSpans[].spans[] */)
    {
        var name = OtlpSpanReader.ReadString(span, "name");
        if (name is null || !ClaudeCodeSpanAdapter.RecognizedSpanNames.Contains(name)) continue;
        hasClaudeSpan = true;
        if (name == "claude_code.interaction" && HasAttribute(span, "user_prompt")) hasContentField = true;
        if (name == "claude_code.tool" && (HasAttribute(span, "file_path") || HasEvent(span, "tool.output"))) hasContentField = true;
    }
    if (!hasClaudeSpan) return null;
    return hasContentField ? SourceCaptureContentState.Available : SourceCaptureContentState.NotCaptured;
}
```

`HasAttribute` checks `attributes[].key` equality (Ordinal); `HasEvent` checks `events[].name` equality (Ordinal). Never read or return attribute **values**.
- [ ] **Step 4: Run resolver tests → PASS.**
- [ ] **Step 5: Write a failing integration test** in the LocalMonitor test project (reuse the existing MonitorHost ingestion test harness): POST an OTLP JSON batch containing a `claude_code.interaction` span with a `user_prompt` attribute (synthetic marker value), then GET `/api/monitor/traces` and assert the trace's `content_state == "available"`; a second case without any gated field asserts `"not_captured"`; a third with only foreign span names asserts `"unsupported"` (unchanged fallback).
- [ ] **Step 6: Wire the derivation in `MonitorHost.cs`:** in the TracePath handler replace the `metadata.CaptureContentState` argument of `SourceObservationBatchDraft.Create` with:

```csharp
var captureContentState = ClaudeOtlpCaptureContentStateResolver.Derive(decodedPayload.PayloadJson)
    ?? metadata.CaptureContentState;
```

Leave every other use of `metadata.CaptureContentState` (adapter-failure drafts) untouched.
- [ ] **Step 7: Run the integration tests → PASS**, then run the affected existing suites (`dotnet test` on the Telemetry and LocalMonitor test projects). Existing tests that asserted fixed `unsupported` for Claude-shaped fixtures must be updated to the spec-derived expectation — update the assertion, not the rule.
- [ ] **Step 8: Commit**

```bash
git add src/ tests/
git commit -m "Issue #107: fix(monitor): derive OTel capture_content_state from batch content evidence"
```
(body: why — the fixed provider constant made the field non-varying and spec-violating for Claude OTel traces.)

---

### Task 3: #108 — exact session binding on its own `session.id` evidence

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionOtelEnricher.cs` (generic `Process(ProjectedSpan row)` path)
- Test: the existing enricher test file(s) in the Persistence.Sqlite test project (locate `SqliteSessionOtelEnricher` tests; follow their store/DB fixture conventions).

**Interfaces:**
- Consumes: Task 1's pinned decoupling rule; existing private helpers `ReadExactClaudeNativeSessionId(payloadJson, traceId, spanId)` and `FindClaudeBinding(nativeSessionId)` (already contract-exact — do not change their matching semantics).
- Produces: no new public API; behavior change only.

- [ ] **Step 1: Write failing enricher tests.** Cases (synthetic IDs only):
  1. **Binds on own evidence:** seed a Hook-created session with a `claude-code` native ID (`binding_kind` native) for native ID `S`; insert a projected span row whose `source_schema_observations` labels are `raw-otlp`/`raw-otlp` (or absent) and whose `raw_records.payload_json` carries exactly one matching span with attribute `session.id == S`. Run `ProcessNextBatch`. Assert: the written run/event rows carry the Hook session's `session_id` (no new session created); no new row in `session_native_ids`; the event's `source_adapter` stays the generic path's label (`otel-exact`), not `claude-code-otel`.
  2. **No match → unchanged:** same row shape, no Hook session → a fresh unbound session, exactly as today.
  3. **Ambiguity refuses:** payload with two `session.id` values for the span, or two candidate sessions holding native ID `S` → no binding (fresh unbound session).
  4. **Byte exactness:** Hook native ID `S`, payload `session.id` differing only by case or trailing space → no binding.
  5. **Promoted path regression:** existing `ProcessClaude` tests still pass unmodified.
- [ ] **Step 2: Run new tests → FAIL** (evidence lands on a new unbound session).
- [ ] **Step 3: Implement.** In `Process(row)`, attempt the Claude exact binding first (mirroring `ProcessClaude`'s precedence: exact binding outranks trace-continuity):

```csharp
private Guid? TryFindClaudeExactBinding(ProjectedSpan row)
{
    if (row.PayloadJson is null)
    {
        return null;
    }
    var claudeNativeSessionId = ReadExactClaudeNativeSessionId(row.PayloadJson, row.TraceId, row.SpanId);
    return claudeNativeSessionId is null ? null : FindClaudeBinding(claudeNativeSessionId);
}
```

and in `Process`:

```csharp
var claudeBoundSessionId = TryFindClaudeExactBinding(row);
var sessionId = claudeBoundSessionId ?? traceSessionId ?? nativeSessionId ?? Guid.CreateVersion7();
```

(rename locals as needed to avoid colliding with the existing ConversationId-based `nativeSessionId`). Everything else in `Process` — surface confirmation (`ConfirmSurface`), event labels, the `nativeIds` block, completeness calculation — stays structurally as-is and now simply operates against the bound session's existing detail. Do not add a `SessionNativeId` row from OTel evidence.
- [ ] **Step 4: Run new tests → PASS.**
- [ ] **Step 5: Add one API-level assertion** (in the LocalMonitor test project, or extend an existing session/trace projection test): after binding, the trace's projection reports `binding_state == "exact_linked"`. If the projection derives binding state from something the generic path does not produce, surface that as a finding instead of forcing it — report `DONE_WITH_CONCERNS` with what the projection actually returned.
- [ ] **Step 6: Run the Persistence.Sqlite and LocalMonitor test projects → PASS.**
- [ ] **Step 7: Commit**

```bash
git add src/ tests/
git commit -m "Issue #108: fix(sessions): gate exact claude binding on its own session.id evidence"
```
(body: why — adapter promotion (blocked on #99 drift) coarsely gated an independently verifiable capability; decision recorded in docs/decisions.md.)

---

### Task 4: Full pinned validation

**Files:** none (verification only; fix regressions if any).

- [ ] **Step 1:** `dotnet build CopilotAgentObservability.slnx` → succeeds.
- [ ] **Step 2:** `pwsh scripts\test\install-playwright-chromium.ps1` → succeeds.
- [ ] **Step 3:** `dotnet test CopilotAgentObservability.slnx` → all pass (Playwright smoke tests included). Report exact totals.
- [ ] **Step 4:** If failures: fix within the task scopes above (spec stays authoritative), re-run, then commit fixes with the owning issue prefix.

## Self-Review

- Spec coverage: #107 derivation → Task 1 Step 2 + Task 2; #108 decoupling + decision record → Task 1 Steps 3-4 + Task 3; aggregation/API shape unchanged → Global Constraints.
- Placeholder scan: code sketches are intentionally minimal where they reuse named existing helpers; every behavior is normatively pinned in "Shared design decisions".
- Type consistency: `Derive(string): SourceCaptureContentState?` consumed in Task 2 Step 6; enricher helpers referenced by their real names verified against current source.

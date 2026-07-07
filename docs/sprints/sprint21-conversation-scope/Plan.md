# Sprint21 Conversation Scope - Implementation Plan

Status: M1-M4 complete. M5 live validation is prepared and remains
user-controlled because it requires preserved/local trace data and provider
execution.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Subagent dispatch is allowed only when the user explicitly asks for it (AGENTS.md); otherwise execute inline.

**Goal:** Let `instruction-diagnosis` use a bounded previous/current/next trace window from the same `conversation_id` while keeping the selected trace as the anchor and preserving raw-analysis safety boundaries.

**Architecture:** Extend the Sprint20 deterministic extractor contract with a bounded conversation-context field computed from existing projection rows and raw records. `MonitorAnalysisToolData.Create` resolves the conversation window through existing projection-store reads, loads only the selected sibling traces within the window, and passes their rows/records into a pure extractor. Prompt template v4 requires the model to cite whether evidence is from the analyzed trace or a named sibling trace.

**Tech Stack:** .NET Local Monitor, xUnit, existing SQLite projection store reads. No new dependencies.

## Global Constraints

- Source-of-truth docs must be updated before implementation: `docs/requirements.md`, `docs/spec.md` if needed, `docs/specifications/interfaces/instruction-diagnosis-analysis.md`, `docs/specifications/layers/telemetry-ingestion.md`, and `docs/decisions.md`.
- Additive internal behavior only: no new public route, no `/api/monitor/*` field, no SSE change, no projection migration, no Canvas focus change.
- Default bounded window is two previous and two following sibling traces around the analyzed trace, maximum five emitted entries.
- Existing `conversation` metadata remains available; new bounded context must be omitted or empty when no `conversation_id` exists.
- Existing six raw-analysis tools and Sprint20 `get_instruction_evidence` tool name stay available; prefer extending that tool's structured output over adding another tool unless M1 records a different decision.
- `--sanitized-only` continues to remove the whole raw analysis surface.
- Sibling trace descriptors are raw-derived local runtime data and must not appear in repository-safe evidence.
- No raw prompt/response/tool bodies, PII, credentials, provider URLs, local sensitive paths, or full analysis markdown in committed files.
- Pinned validation for code changes remains:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

## File Map

| File | Action | Responsibility |
| --- | --- | --- |
| `docs/decisions.md` | Modify | Add D048 for bounded conversation scope |
| `docs/specifications/interfaces/instruction-diagnosis-analysis.md` | Modify | Conversation-context output contract and prompt v4 sibling-evidence rules |
| `docs/specifications/layers/telemetry-ingestion.md` | Modify | Note expanded `get_instruction_evidence` contract if needed |
| `docs/requirements.md`, `docs/spec.md` | Modify if stale | Shipped raw-analysis wording |
| `src/CopilotAgentObservability.LocalMonitor/Analysis/InstructionEvidenceExtractor.cs` | Modify | Add bounded conversation-context extraction |
| `src/CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs` | Modify | Load bounded sibling rows/records and prompt v4 |
| `src/CopilotAgentObservability.LocalMonitor/Projection/IMonitorProjectionStore.cs` | Modify only if needed | Add narrow helper only if existing reads are insufficient |
| `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore*.cs` | Modify only if needed | Store-side read support, preferably no schema change |
| `tests/CopilotAgentObservability.LocalMonitor.Tests/InstructionEvidenceExtractorTests.cs` | Modify | Pure extractor tests for windowing and summaries |
| `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorAnalysisRouteTests.cs` | Modify | Tool-data wiring and prompt v4 tests |
| `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionStoreTests.cs` | Modify only if store read changes | Store ordering/window support |
| `docs/sprints/sprint21-conversation-scope/milestones/M4-regression-validation/regression-validation.md` | Create during M4 | Automated validation evidence |
| `docs/sprints/sprint21-conversation-scope/milestones/M5-live-validation/live-validation.md` | Update during M5 | Sanitized live validation evidence |

---

## M1 - Specs And Decision Record

**Purpose:** Promote the bounded conversation-scope contract into the current source of truth before implementation.

**Exit criteria:**
- D048 records the bounded window, emitted fields, safety boundary, prompt v4 rules, and M5 validation gate.
- The interface spec defines the new conversation-context output contract.
- Requirements/spec wording is reviewed and updated if it still implies trace-only analysis.
- Docs-only self-review is recorded in the commit message or milestone notes.

See `milestones/M1-specs-and-decision/plan.md`.

## M2 - Bounded Conversation Context Extraction

**Purpose:** Implement the conversation window as deterministic data before prompt changes.

**Exit criteria:**
- Extractor output contains a bounded context field with at most five trace entries.
- Tests cover middle, start, end, missing conversation id, missing raw descriptor, and deterministic serialization.
- Existing Sprint20 trace-only evidence tests still pass.

See `milestones/M2-bounded-conversation-context/plan.md`.

## M3 - Tool Wiring And Prompt Template v4

**Purpose:** Expose the bounded context through the existing raw-analysis tool data and require the model to use it safely.

**Exit criteria:**
- `MonitorAnalysisToolData.Create` loads only in-window sibling trace rows/records.
- `get_instruction_evidence` includes the bounded context.
- Prompt v4 requires sibling citations to name trace ids and forbids out-of-window inference.
- Existing non-instruction focuses keep the generic prompt path.

See `milestones/M3-tool-wiring-and-prompt-v4/plan.md`.

## M4 - Regression Validation

**Purpose:** Prove the change is additive and does not loosen raw/sanitized boundaries.

**Exit criteria:**
- Pinned suite is green.
- Self-review confirms no route/API/schema/Canvas change, no repository raw leakage, and no unbounded sibling loading.
- Evidence is recorded under the M4 milestone folder.

See `milestones/M4-regression-validation/plan.md`.

## M5 - Live Validation And Issue #46 Update

**Purpose:** Validate that conversation-scope evidence improves the targeted Phase 2 gap without reintroducing generic or uncited findings.

**Exit criteria:**
- Same preserved Sprint19/Sprint20 trace set is re-run where useful, plus at least two traces where the relevant clarification/follow-up is in a sibling trace.
- Gate records citation existence, trace specificity, no-evidence-no-finding, extractor/raw grounding, bounded-window compliance, and no raw leakage in evidence.
- Repository-safe Issue #46 comment draft is recorded. Posting remains user-controlled.

See `milestones/M5-live-validation/plan.md`.

# Issue #110 Redacted Sentinel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct Claude Code gate-disabled OTLP classification so the exact `<REDACTED>` prompt sentinel derives `not_captured` through resolver, monitor, and Doctor surfaces.

**Architecture:** Keep the existing per-batch resolver and public state model. Replace presence-only prompt evidence with in-place JSON token inspection, retaining existing tool evidence and caller fallback behavior; update the canonical interface specification and add focused regression coverage without persistence migration.

**Tech Stack:** .NET 10, C#, System.Text.Json, xUnit, ASP.NET Core Local Monitor, SQLite test fixtures.

## Global Constraints

- The exact ordinal `stringValue` `<REDACTED>` derives `not_captured`; it never derives `redacted`.
- A non-empty string other than the exact sentinel derives `available`; empty, absent, non-string, and length-only prompt shapes derive `not_captured`.
- `tool.output` and `file_path` retain their current content-evidence behavior; foreign-only batches retain the caller's fixed raw-OTLP fallback.
- Prompt content is inspected in place and is never copied into DTOs, logs, exceptions, test output, or repository-safe evidence.
- No dependency, public enum/route/DTO/schema change, compatibility path, historical backfill, or persistence migration is added.
- Use synthetic fixtures only and follow RED → GREEN for every production-code change.

---

### Task 1: Canonical specification

**Files:**
- Modify: `docs/specifications/interfaces/source-schema-drift-claude-code.md`

**Interfaces:**
- Consumes: accepted Issue #110 decision and #106 live observation.
- Produces: normative value-aware `user_prompt` derivation contract used by Tasks 2-4.

- [ ] **Step 1: Replace the presence-only paragraph**

State that an interaction prompt is evidence only when `value.stringValue` is non-empty and not ordinal-equal to `<REDACTED>`; exact sentinel, empty, absent, non-string, and length-only shapes are `not_captured`; `redacted` remains underivable for Claude OTel v1; values are inspected in place and never copied out.

- [ ] **Step 2: Check source-of-truth consistency**

Run:

```powershell
rg -n "presence only|field presence|<REDACTED>|user_prompt_length|redacted.*deriv" docs/requirements.md docs/spec.md docs/specifications docs/decisions.md
```

Expected: no current source-of-truth statement contradicts the new rule.

- [ ] **Step 3: Self-review the documentation diff**

Verify exact sentinel spelling, `not_captured` classification, unchanged tool rules, and no claim that historical rows are backfilled.

### Task 2: Resolver RED → GREEN

**Files:**
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/ClaudeOtlpCaptureContentStateResolverTests.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/ClaudeOtlpCaptureContentStateResolver.cs`

**Interfaces:**
- Consumes: `ClaudeOtlpCaptureContentStateResolver.Derive(string)` and `SourceCaptureContentState`.
- Produces: value-aware interaction prompt evidence without changing the public method signature.

- [ ] **Step 1: Write failing parameterized tests**

Add cases asserting `<REDACTED>`, empty `stringValue`, `intValue`, and `user_prompt_length`-only return `NotCaptured`; retain the existing synthetic non-empty case as `Available`. Add an exactness case such as `<redacted>` returning `Available` so comparison semantics are pinned.

- [ ] **Step 2: Verify RED**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ClaudeOtlpCaptureContentStateResolverTests
```

Expected: the exact `<REDACTED>` and empty/non-string cases fail because the current resolver checks key presence only.

- [ ] **Step 3: Implement minimal value-aware inspection**

Replace the interaction `HasAttribute(span, "user_prompt")` condition with a helper that finds the exact key, reads only `value.stringValue`, rejects non-string/empty values, and uses ordinal token equality for `<REDACTED>` without materializing the prompt as a managed string. Update the class comment so it records the external sentinel constraint rather than restating presence-only code.

- [ ] **Step 4: Verify GREEN**

Run the Task 2 command again.

Expected: all resolver tests pass with zero failures.

### Task 3: Ingestion and Doctor regression

**Files:**
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SourceCompatibilityIngestionTests.cs`
- Modify: `tests/CopilotAgentObservability.Doctor.Tests/ClaudeCode/ClaudeFirstTraceCrossSurfaceTests.cs`
- Modify only if existing harness requires byte-accurate shape: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Claude/otel/content-disabled.json`

**Interfaces:**
- Consumes: Task 2 resolver behavior and existing ingest/Doctor projection pipelines.
- Produces: end-to-end proof that the live-observed gate-disabled shape stays `not_captured` and Doctor reports capture disabled.

- [ ] **Step 1: Add the live-observed synthetic shape**

Use `user_prompt.stringValue = "<REDACTED>"` plus an integer `user_prompt_length`; do not include a real prompt or marker.

- [ ] **Step 2: Add monitor and Doctor assertions**

Assert Local Monitor `content_state == "not_captured"` after ingestion and Doctor `ContentCaptureStatus.Disabled` for the exact candidate. Keep tests on public/observable results rather than private helper calls.

- [ ] **Step 3: Run focused tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SourceCompatibilityIngestionTests
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~ClaudeFirstTraceCrossSurfaceTests
```

Expected: all selected tests pass with zero failures.

### Task 4: Validation, closeout, and Issue disposition

**Files:**
- Modify: `docs/task.md`
- Modify only when a new dated live run occurs: `docs/sprints/issue-106-claude-live-validation-followup.md`

**Interfaces:**
- Consumes: reviewed Tasks 1-3 and repository validation evidence.
- Produces: durable roadmap status and a repository-safe #110 close comment.

- [ ] **Step 1: Run repository validation in order**

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Expected: build exit 0 with zero warnings/errors, bootstrap exit 0, and all tests pass. Record any initial intermittent failure and the exact rerun; do not substitute a focused test for the full suite.

- [ ] **Step 2: Run bounded live validation when available**

Use the existing `scripts/validation/issue-106/` gate-disabled interactive and `claude -p` path with `OTEL_LOG_USER_PROMPTS` off. Expected sanitized result: `content_state=not_captured`; no prompt body appears in repository-safe evidence. If the external surface or authorization is unavailable, record the exact unverified scope and do not claim this step passed.

- [ ] **Step 3: Update roadmap status**

Add an Issue #110 row or update the Issue #106 note in `docs/task.md` with the immutable implementation commit, focused/full validation counts, live status, and no-migration conclusion.

- [ ] **Step 4: Close GitHub Issue #110 only after integration is remotely reachable**

Post a concise comment containing the implementation commit, decision table, automated validation, live evidence status, security boundary, no-migration conclusion, and #105 dependency release. Close as completed only when the fix commit is reachable from the repository's shared branch; local-only work is not sufficient.


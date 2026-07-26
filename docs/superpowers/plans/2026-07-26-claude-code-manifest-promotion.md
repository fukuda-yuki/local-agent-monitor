# Claude Code Manifest Promotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the Claude Code v1 capability manifest and its canonical references with the approved observed and implemented capabilities.

**Architecture:** This is a contract-only change. Existing v1 shapes, runtime behavior, mapping authority, and safety boundaries remain unchanged; only existing manifest values, stale prose, regression expectations, and the manifest digest are updated.

**Tech Stack:** JSON v1 manifest, Markdown specifications, C# xUnit tests, .NET solution validation.

## Global Constraints

- The v1 schema is closed and rejects unknown fields.
- `support_status` becomes `preview`; `stability` remains `preview`.
- Only `signals.trace`, `signals.hook`, all three `trace_span_identity` leaves, `timing_ttft.timing`, and `content_capture_gate` become `available`.
- All other Claude capability leaves remain `unknown`.
- Do not change schema, mapping authority, runtime code, public interfaces, dependencies, or security boundaries.
- Preserve unrelated pre-existing working-tree changes.

---

### Task 1: Pin the promoted contract in the regression test

**Files:**
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SourceCapabilityContractTests.cs`

**Interfaces:**
- Consumes: The existing repository-manifest contract test and its `UnknownAvailabilityMatrix()` helper.
- Produces: An exact expected Claude header and availability matrix for Issue #116.

- [ ] **Step 1: Change the expected Claude header and matrix before changing the manifest**

Change the Claude header expectation to:

```csharp
AssertManifestHeader(manifests["claude-code"], "claude-code", "claude-code-otel+claude-code-hook", "preview", "preview");
```

After the existing `source_version_detector` assignment, add:

```csharp
claudeAvailability["signals.trace"] = "available";
claudeAvailability["signals.hook"] = "available";
claudeAvailability["trace_span_identity.trace_id"] = "available";
claudeAvailability["trace_span_identity.span_id"] = "available";
claudeAvailability["trace_span_identity.parentage"] = "available";
claudeAvailability["timing_ttft.timing"] = "available";
claudeAvailability["content_capture_gate"] = "available";
```

Keep the existing `AssertAvailabilityMatrix` call so every unlisted leaf stays
`unknown`.

- [ ] **Step 2: Verify the test fails for the intended reason**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SourceCapabilityContractTests.Repository_manifests_declare_the_initial_distinct_producer_surfaces
```

Expected: FAIL because the current Claude manifest is still `planned` and the
newly promoted leaves are still `unknown`.

---

### Task 2: Align the manifest and canonical Claude contract prose

**Files:**
- Modify: `docs/specifications/contracts/source-capabilities/v1/manifests/claude-code.json`
- Modify: `docs/specifications/contracts/source-capabilities/v1/claude-code/exact-binding.md`
- Modify: `docs/specifications/contracts/source-capabilities/v1/claude-code/security.md`

**Interfaces:**
- Consumes: Task 1's exact promotion list and the existing Issue #99/#101/#104/#107 evidence.
- Produces: A v1-valid manifest and canonical prose with matching observed/unknown boundaries.

- [ ] **Step 1: Update only the approved existing manifest values**

Set `support_status` to `preview`; keep `stability` as `preview`. Set only the
following availability leaves to `available`: `signals.trace`, `signals.hook`,
`trace_span_identity.trace_id`, `trace_span_identity.span_id`,
`trace_span_identity.parentage`, `timing_ttft.timing`, and
`content_capture_gate`. Do not change any other leaf.

- [ ] **Step 2: Narrow stale canonical claims**

In `exact-binding.md`, state that live OTel evidence establishes trace/span
identity, parentage, and timing, and that the Hook path is implemented, while
complete trace-context binding, native-session manifest capability, interactive
CLI, and Agent SDK producer structure remain unverified or deferred.

In `security.md`, distinguish the observed OTel/Hook path and implemented
content gate from raw content-field availability. Preserve the existing rules
for `not_captured`, `redacted`, prohibited raw API bodies, and documentation not
granting content authority. Retain the interactive and Agent SDK gaps.

- [ ] **Step 3: Verify the focused contract test turns green**

Run the Task 1 command again. Expected: PASS.

---

### Task 3: Refresh the pinned Claude manifest digest

**Files:**
- Modify: `docs/specifications/interfaces/token-context-cache-alert-rules.md`
- Modify: `tests/CopilotAgentObservability.Alerts.Tests/TokenContextCacheAlertRuleTests.cs`

**Interfaces:**
- Consumes: The final Claude manifest bytes from Task 2.
- Produces: Matching documentation and test SHA-256 pins.

- [ ] **Step 1: Compute the new digest**

Run:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath docs\specifications\contracts\source-capabilities\v1\manifests\claude-code.json
```

- [ ] **Step 2: Replace only the Claude digest**

Copy the returned hexadecimal digest into the Claude row of the Markdown table
and the matching `[InlineData]` entry in the alert test. Leave both GitHub
Copilot digests unchanged.

- [ ] **Step 3: Verify the focused hash test**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.Alerts.Tests\CopilotAgentObservability.Alerts.Tests.csproj --filter FullyQualifiedName~TokenContextCacheAlertRuleTests.CurrentSourceManifests_ArePinnedAndKeepAllFiveRulesCapabilitySuppressed
```

Expected: PASS.

---

### Task 4: Run complete validation and commit the scoped change

**Files:**
- Read-only review of all changed files and the existing working tree.

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: Fresh repository validation evidence and one coherent local commit.

- [ ] **Step 1: Check diff scope**

Run `git diff --check` and inspect only the Issue #116 files. Confirm no schema,
runtime, dependency, raw-data, or unrelated workflow change was introduced.

- [ ] **Step 2: Run the pinned build**

```powershell
dotnet build CopilotAgentObservability.slnx
```

Expected: exit code 0.

- [ ] **Step 3: Install the pinned Playwright browser**

```powershell
pwsh scripts\test\install-playwright-chromium.ps1
```

Expected: exit code 0.

- [ ] **Step 4: Run the pinned full solution tests**

```powershell
dotnet test CopilotAgentObservability.slnx
```

Expected: exit code 0 with no failed tests.

- [ ] **Step 5: Commit only the scoped files**

After validation succeeds, stage the design/plan and Issue #116 contract files,
preserving unrelated pre-existing workflow changes. Use this commit subject:

```text
Issue #116: fix(spec): promote Claude Code manifest capabilities
```

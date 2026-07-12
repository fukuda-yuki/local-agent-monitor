# Issue #61 Source Capability Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish and verify the versioned source capability, provenance, authority, and completeness contract required by Issue #61.

**Architecture:** JSON Schema 2020-12 owns structure, per-surface JSON manifests own capability declarations, and canonical Markdown owns semantic rules and handoff policy. A deterministic .NET repository test validates the committed artifacts without adding a dependency.

**Tech Stack:** Markdown, JSON Schema 2020-12, JSON, C# / System.Text.Json, MSTest

## Global Constraints

- Do not implement or change receivers, adapters, database schema, HTTP routes, proxy behavior, or UI.
- Do not change Issue #51 exact Session / Run / Event identity or Issue #49 Agent ownership.
- Repository, workspace, and timestamp proximity never establish identity.
- Do not permit heuristic Session merge or synthetic span creation.
- Do not add dependencies or include raw/PII in committed artifacts.
- GitHub Copilot VS Code, GitHub Copilot CLI, Claude Code, Codex App, and Codex CLI are separate declared surfaces.
- Lower-authority evidence never overwrites higher-authority evidence.

---

### Task 1: Versioned Schema And Executable Contract Harness

**Files:**
- Create: `docs/specifications/contracts/source-capabilities/v1/source-capability-manifest.schema.json`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/SourceCapabilityContractTests.cs`

**Interfaces:**
- Produces: exact v1 property names, enums, capability shapes, provenance keys, completeness statuses/reasons, and a reusable manifest validator.

- [ ] Write a failing repository test for the required v1 schema, exact enums, five distinct surface fixtures, and no unknown properties.
- [ ] Run `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SourceCapabilityContractTests` and confirm RED because artifacts are absent.
- [ ] Add the minimal JSON Schema and System.Text.Json validator with no third-party dependency.
- [ ] Re-run the focused test and `git diff --check`.
- [ ] Report files, RED/GREEN evidence, and concerns; do not commit until task review passes.

### Task 2: Initial Producer-Facing Surface Manifests

**Files:**
- Create: `docs/specifications/contracts/source-capabilities/v1/manifests/github-copilot-vscode.json`
- Create: `docs/specifications/contracts/source-capabilities/v1/manifests/github-copilot-cli.json`
- Create: `docs/specifications/contracts/source-capabilities/v1/manifests/claude-code.json`
- Create: `docs/specifications/contracts/source-capabilities/v1/manifests/codex-app.json`
- Create: `docs/specifications/contracts/source-capabilities/v1/manifests/codex-cli.json`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SourceCapabilityContractTests.cs`

**Interfaces:**
- Consumes: Task 1 exact v1 schema and validator.
- Produces: initial declared capability values for every required surface.

- [ ] Add RED assertions for exact distinct surfaces, adapters, planned/active state, detector and all required capability families.
- [ ] Run the focused contract test and confirm the expected missing/invalid fixture failures.
- [ ] Add minimal repository-safe manifests based only on current canonical evidence; use unavailable/unknown declarations rather than invented producer fields.
- [ ] Re-run the focused test and `git diff --check`.
- [ ] Report files, RED/GREEN evidence, and concerns; do not commit until task review passes.

### Task 3: Canonical Semantic Contract And Handoff

**Files:**
- Modify: `docs/requirements.md`
- Modify: `docs/spec.md`
- Modify: `docs/specifications/layers/telemetry-ingestion.md`
- Modify: `docs/specifications/layers/raw-store-normalization.md`
- Modify: `docs/specifications/interfaces/canvas-session-workspace.md`
- Modify: `docs/specifications/security-data-boundaries.md`
- Modify: `docs/decisions.md`
- Modify: `docs/task.md`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SourceCapabilityContractTests.cs`

**Interfaces:**
- Consumes: Tasks 1-2 schema and manifest vocabulary.
- Produces: field-family authority table, provenance contract, deterministic completeness decision table, safety boundary, and adapter handoff.

- [ ] Add RED cross-reference assertions for every canonical document and all required reason/authority/handoff identifiers.
- [ ] Run the focused contract test and confirm missing canonical references fail.
- [ ] Update source-of-truth documents with one non-contradictory semantic contract and links to machine-readable artifacts.
- [ ] Re-run the focused test and `git diff --check`.
- [ ] Report files, RED/GREEN evidence, and concerns; do not commit until task review passes.

### Task 4: Acceptance Audit, Reviews, Validation, And Closeout

**Files:**
- Modify only Task 1-3 artifacts when a review or acceptance gap is proven.
- Update: `.superpowers/sdd/progress.md` (ignored durable working ledger).

**Interfaces:**
- Consumes: complete Issue #61 diff.
- Produces: acceptance-criterion-to-test matrix, independent review verdicts, security/concurrency/migration audit, and validation evidence.

- [ ] Run an early integration review after the schema/manifests and semantic contract meet.
- [ ] Run an independent final spec/quality review by an agent that implemented none of Tasks 1-3.
- [ ] Run a separate security/concurrency/migration review and resolve all Critical/Important findings.
- [ ] Run the focused contract test, `dotnet build CopilotAgentObservability.slnx`, `pwsh scripts\test\install-playwright-chromium.ps1`, `dotnet test CopilotAgentObservability.slnx`, and `git diff --check` without substitution.
- [ ] Audit every Issue #61 acceptance criterion against an exact document location and executable assertion or recorded cross-reference evidence.
- [ ] Commit the reviewed coherent change with the `Issue #61:` prefix; do not push or create a PR.


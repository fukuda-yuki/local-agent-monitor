# Issue #92 discovery plan

## Objective

Determine whether Codex App/app-server exposes a stable, supportable telemetry
surface without private-state scraping, heuristic correlation, or
content-enabled capture. Produce a `GO`, `LIMITED-GO`, or `NO-GO` decision and
a production-integration gate.

Kickoff:

- branch: `codex/issue-92-codex-app-inventory`
- candidate base: `07dc219c4f5c5ef56e7810a23c6466a52e90aa97`
- date: 2026-07-24

## Scope

In scope:

- public Desktop package and CLI/app-server version detection;
- official configuration and public app-server protocol documentation;
- content-disabled, per-command-only live probes to a disposable loopback
  receiver;
- signal, envelope, field-key, identifier-shape, parentage, and correlation
  inventory;
- repository-safe sanitized fixtures;
- canonical specification and source-capability manifest update;
- explicit production-integration gate and prerequisite retry conditions.

Out of scope:

- production adapter, receiver, Setup, Doctor, UI, or persistence changes;
- content-enabled capture;
- private App state, database, cache, history, or content reads, and committed
  installation-path values;
- Codex CLI product support, cloud tasks, ChatGPT Web, or generic Agents SDK;
- process/repository/workspace/cwd/timestamp/prompt/order heuristics;
- GitHub mutation, commit, push, tag, or pull request.

## Working order

1. Read current requirements, specification, layer/security contracts,
   decisions, architecture, task history, and source-capability contract.
2. Inspect official OpenAI documentation and public Codex protocol/schema
   sources.
3. Detect versions without private state and attempt the Desktop-bundled
   producer separately from the standalone CLI/app-server.
4. Add a failing repository contract test for decision, fixture safety,
   correlation, future-registry, and canonical-document boundaries.
5. Run authorized content-disabled probes using per-command overrides and a
   disposable loopback receiver.
6. Commit only sanitized structural evidence; remove disposable raw probe
   output after extraction.
7. Update canonical requirements/specifications first, then the manifest and
   sprint evidence.
8. Run focused tests, required repository validation, safety scan, and
   self-review.

## Decision rules

`GO` requires Desktop-owned producer execution, stable version/config
detection, exact source identity and parentage, exact-or-explicitly-unbound
native correlation, safe loopback routing, and sufficient profile coverage for
the proposed production surface.

`LIMITED-GO` is permitted when a stable public producer surface and exact OTel
metadata exist but Desktop ownership, native binding, or field/profile coverage
remains incomplete for that same scoped producer surface. Generic standalone
app-server evidence cannot satisfy or downgrade a Desktop-specific `NO-GO`.
Any limited result must preserve unknown values and prohibit unsupported
support claims.

`NO-GO` applies when stable detection or source identity is absent, correlation
requires heuristics/private state, or safe raw/content boundaries cannot be
maintained.

## Validation

Required commands:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Issue92CodexAppDiscoveryContractTests
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SourceCapabilityContractTests
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

The full suite is not replaced by a targeted test. Any failed, skipped, or
unavailable required command remains visible in the final evidence.

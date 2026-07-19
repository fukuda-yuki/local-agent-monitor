---
name: aspire-local-ops
description: >-
  Explicitly operate and inspect this repository's existing local Aspire AppHost.
  Use only when the user asks to start, stop, inspect, wait for, or diagnose the
  local Aspire application, its resources, logs, traces, spans, dashboard, file
  locks, or port conflicts. Do not use for ordinary build/test work, AppHost
  authoring, adding integrations, deployment, or provisioning.
argument-hint: "Describe the local Aspire operation or diagnostic task."
user-invocable: true
disable-model-invocation: true
---

# Aspire Local Operations

Operate and inspect the repository's existing local Aspire AppHost without
changing its topology or deployment model.

## Scope

This skill covers only local runtime operations:

- Start, inspect, wait for, and stop the existing AppHost.
- Read local resource state, endpoints, logs, traces, and spans.
- Diagnose local file-lock, orphan-process, and port-conflict failures.

It does not authorize product or infrastructure changes. Before changing
AppHost code, resources, integrations, ServiceDefaults, browser telemetry,
deployment, or provisioning, update the governing specification and obtain the
confirmation required by `AGENTS.md`.

Do not use this skill for ordinary repository validation. Use the repository's
`validate` skill and its pinned build, Playwright, and test sequence.

## Before Running Commands

1. Read `AGENTS.md`.
2. Inspect `src/CopilotAgentObservability.AppHost/AppHost.cs` and the relevant
   current specification, especially
   `docs/specifications/layers/telemetry-ingestion.md`.
3. Confirm that the Aspire CLI is available with `aspire --version`.
   If it is unavailable, report the blocker. Do not install or upgrade it
   without explicit approval.

The AppHost is currently intentionally minimal. Do not infer or add resources
because an AppHost project exists.

## Command Workflow

Run commands from the repository root.

### Start

Use the Aspire CLI rather than `dotnet run` on the AppHost:

```powershell
aspire start --non-interactive
```

When working in a git worktree, or whenever shared local state could collide
with another session:

```powershell
aspire start --isolated --non-interactive
```

### Inspect

Prefer machine-readable state:

```powershell
aspire describe --format Json
aspire ps --format Json
```

If an expected proxy, helper, or migration resource is absent from the normal
view, retry inspection with `--include-hidden`. Do not assume a resource name
or endpoint; derive it from `describe` or `ps`.

Before interacting with a resource that must be ready:

```powershell
aspire wait <resource-name>
```

### Diagnose

Inspect state before editing code:

```powershell
aspire describe --format Json
aspire otel logs <resource-name>
aspire logs <resource-name>
aspire otel traces <resource-name>
aspire otel spans <resource-name>
```

Use only commands supported by the installed Aspire CLI. When uncertain about
a flag or AppHost API, consult the installed CLI help or current official
documentation rather than guessing.

For file-lock or port-conflict failures:

1. Inspect the current state.
2. Stop the owned AppHost if it is stale or holding build outputs or ports.
3. Retry the original pinned command.
4. Report the observed process or resource state; do not present a transient
   lock as a permanent build defect.

### Stop

Stop the AppHost when cleanup is required or when the operation is complete,
unless the user explicitly asked to leave it running:

```powershell
aspire stop
```

Do not restart the entire AppHost merely because one source file changed. Use
the existing resource/runtime watch path when available; restart only when the
AppHost model changed or recovery requires it.

## Prohibited Actions

Without an explicit user instruction and the required specification update, do
not:

- Run `aspire init`, add integrations, or add resource definitions.
- Add or update NuGet packages, workloads, ServiceDefaults, or deployment
  targets.
- Add browser telemetry or change captured-data exposure.
- Replace the pinned `validate` workflow with Aspire commands.
- Save or commit Aspire exports, dashboard login URLs or tokens, API keys, raw
  prompts/responses, tool arguments/results, or content-bearing telemetry.

## Completion Report

Report:

- Commands run and their exit/result state.
- Resource state and endpoints used, without secrets.
- Whether the AppHost was stopped or intentionally left running.
- Any unverified scope or exact command still required.

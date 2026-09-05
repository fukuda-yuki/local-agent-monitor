---
name: aspire-local-ops
description: Operate or diagnose the existing local Aspire AppHost on explicit request.
license: MIT
---

# Aspire Local Operations

The AppHost is intentionally empty. Do not infer or add resources, ServiceDefaults, integrations, deployment targets, or monitoring configuration. This Skill is for explicitly requested operations, not repository build/test work.

## Lifecycle and diagnosis

Run from the repository root and preserve the identity of the AppHost being operated:

1. Start with `aspire start --non-interactive`, adding `--isolated` in a worktree or when shared local state could collide. Do not use `dotnet run`.
2. Inspect with `aspire describe --format Json`; use `--include-hidden` only to locate an expected proxy/helper.
3. Use `aspire logs <resource>`, `aspire otel logs <resource>`, or `aspire otel traces <resource>` only for resources actually observed in describe output. Do not guess names, ports, endpoints, or APIs.
4. Use `aspire stop` only for the task-owned AppHost or a target whose stop the user explicitly authorized. A build lock or occupied port alone does not authorize stopping another process; identify ownership before acting.

## Safety and handoff

Do not save or share exports, dashboard tokens, captured prompts/responses, tool payloads, or sensitive telemetry in repository artifacts. Do not install the obsolete Aspire workload or change dependencies without the existing task/specification authority and user approval.

Report commands run, resources observed, whether the task-owned AppHost remains running, and unverified scope. These diagnostics are not a substitute for affected-contract validation. Use the existing repository workflow for shared execution boundaries.

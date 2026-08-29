---
name: aspire-local-ops
description: Use when explicitly asked to start, stop, or inspect this repository's existing Aspire AppHost, or to diagnose locks, ports, logs, traces, or resource state caused by a running AppHost.
license: MIT
---

# Aspire Local Operations

This repository contains an intentionally empty Aspire AppHost. Use it only as an existing local operations surface; do not infer or add resources, ServiceDefaults, integrations, deployment targets, or monitoring configuration.

## Scope guard

- Invoke this skill explicitly for the lifecycle or diagnostic operation in scope; it does not authorize adjacent implementation, testing, documentation, or cleanup.
- Read an owning specification or `docs/architecture.md` only when the requested operation actually depends on that contract or architecture decision.
- Adding resources, integrations, deployment behavior, or public configuration is outside this skill; stop and return to the task authority and owning specification.
- Do not use this skill for repository build or test work.

## Local lifecycle

Run commands from the repository root.

1. Start the AppHost with `aspire start --non-interactive`. In a git worktree or whenever shared local state could collide, add `--isolated`.
2. Inspect state with `aspire describe --format Json`. Add `--include-hidden` only when an expected proxy or helper resource is missing from the normal view.
3. Use `aspire logs <resource>`, `aspire otel logs <resource>`, and `aspire otel traces <resource>` only after the resource exists in `aspire describe` output.
4. Use `aspire stop` when cleanup is requested or when the running AppHost holds file or port locks.

## Safety rules

- Do not run the AppHost with `dotnet run`; use the Aspire CLI.
- Do not guess resource names, ports, endpoints, or AppHost APIs. Obtain them from `aspire describe` or current documentation.
- If a build reports locked files or a port conflict, stop Aspire before treating it as a code failure.
- Do not save, commit, paste, or share Aspire exports, dashboard tokens, captured prompts/responses, tool arguments/results, or other sensitive telemetry.
- Do not install the obsolete Aspire workload or add dependencies unless the current specifications require it and the user approves it.

## Completion report

State which Aspire commands ran, which resources were observed, whether the AppHost remains running, and any unverified scope. Aspire diagnostics do not replace validation required by the affected contract.

# Product

## Register

product

## Users

Copilot Agent Observability serves developers, prompt and skill implementers, maintainers, and reviewers who need to understand agent workflow behavior from OpenTelemetry traces. They work locally, often on Windows, while debugging VS Code GitHub Copilot Chat, GitHub Copilot CLI, and optionally Codex App runs. Their job is to inspect trace-level execution, compare baseline and variant behavior, find tool and model failure patterns, and decide whether a prompt, skill, wrapper, or workflow change is worth keeping.

## Product Purpose

The product collects agent telemetry, stores raw local evidence safely, normalizes it into reproducible datasets, and presents both detailed trace inspection and aggregate dashboard views. Success means a local user can see what happened in an agent run: which tools and MCP calls fired, where errors appeared, how tokens and cache usage changed, which model or agent was involved, and which deterministic diagnosis or improvement candidate follows from the evidence.

The ideal design direction is Grafana-like in density, scanability, and operational confidence, while preserving the repository's VS Code-local execution posture for the Local Ingestion Monitor. It is not a marketing site and not an org analytics dashboard.

## Brand Personality

Technical, calm, diagnostic.

The interface should feel like a trustworthy observability cockpit for agent work: dense enough for repeated debugging, quiet enough for long sessions, and explicit about safety boundaries. It should borrow Grafana's at-a-glance panel logic and incident-dashboard confidence without copying a dark neon monitoring template or abandoning the VS Code conventions already chosen for the monitor.

## Anti-references

- Not a Copilot adoption, billing, productivity, seat-usage, ranking, or employee-monitoring dashboard.
- Not a DLP, audit-log, or secret-scanning product.
- Not a replica of VS Code Agent Debug / Chat Debug View, and not based on VS Code internal logs, workspaceStorage, or chatSessions.
- Not a marketing SaaS landing page, hero-stat page, decorative card wall, or generic AI observability demo.
- Not a raw-content publishing surface. Raw prompt, response, tool arguments, tool results, source fragments, credentials, and sensitive paths must never appear in repository-safe outputs, static dashboards, logs, or GitHub Pages snapshots.
- Not a DADS-styled Local Monitor. The Local Monitor follows VS Code and local developer-tool conventions; the Static Dashboard remains independent.

## Design Principles

- **Make the run inspectable.** Show hierarchy, timing, tokens, cache behavior, status, and errors in the same mental model so a developer can explain an agent run without switching tools.
- **Density earns trust.** Prefer compact tables, tabs, panels, and progressive disclosure over decorative whitespace. The user is scanning operational evidence, not reading a brochure.
- **Preserve the data boundary.** Visual polish must never blur the distinction between sanitized JSON/API views and raw-bearing server-rendered surfaces.
- **Local-first confidence.** The UI should make loopback, offline, vendored, no-CDN operation feel intentional rather than provisional.
- **Decisions over spectacle.** Charts, colors, and interactions exist to support diagnosis, comparison, readiness, and follow-up action.

## Accessibility & Inclusion

Follow product-UI accessibility practices aligned with VS Code conventions: keyboard-visible focus, readable contrast on the dark theme, semantic tables and tabs, clear status text, and reduced-motion-friendly state changes. DADS-specific rules are explicitly not applied to the Local Monitor. Raw captured content is rendered as escaped, inert text and must not become live markup.

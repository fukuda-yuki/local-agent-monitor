---
name: project-dads-policy
description: >-
  Project-specific DADS adoption policy for copilot-agent-observability.
  USE FOR: resolving design decisions, checking whether a UI change complies
  with project DADS policy, determining which design authority to follow when
  skills or tools conflict, understanding current implementation context.
  DO NOT USE FOR: looking up specific DADS token values (use
  dads-foundations-core), performing UI quality audits (use dads-ui-review),
  backend-only tasks.
license: MIT
metadata:
  author: copilot-agent-observability maintainers
  version: "2.0.0"
---

# Project DADS Policy

This skill defines how the Digital Agency Design System (DADS) is adopted in
the copilot-agent-observability project.

Each rule is tagged with its authority level:

- **[official-must]** — DADS conformance requirement.
- **[official-guidance]** — DADS recommended practice; projects may adapt.
- **[project-decision]** — This project's convention, not a DADS requirement.
- **[reviewer-advice]** — Advisory from the dads-ui-review skill.

## Authority Order

When design guidance conflicts, resolve using this priority:

1. **DADS official site and official implementations** —
   https://design.digital.go.jp/dads/ is the authoritative source.
2. **docs/requirements.md, docs/spec.md, docs/specifications/** — product and
   architecture source of truth per AGENTS.md.
3. **This policy** — project-specific design conventions below.
4. **dads-foundations-core skill** — LLM-oriented DADS digest (supplementary,
   not authoritative).
5. **dads-ui-review skill** — UI quality reviewer (advisory, never overrides
   DADS or project architecture).

If dads-foundations-core and the DADS official site disagree, follow the
official site.

## Current Implementation Context

The current dashboard implementation uses:

- .NET (C#)
- Static HTML dashboard generation (index.html + dashboard-data.json), produced
  by `StaticDashboardGenerator.cs` — no Razor templates
- Plain CSS

The current implementation does not use React, Tailwind CSS, Storybook, or
other frontend frameworks. Introducing a frontend framework, CSS framework, or
build tool requires updating docs/architecture.md and docs/decisions.md first.

DADS tokens and patterns are currently implemented as CSS custom properties and
semantic HTML. When the official DADS React repository contains useful design
rules (accessibility, token usage, semantic HTML), extract the principle and
apply it to the current stack.

## DADS Application Rules

### [official-must] Accessibility baseline

- WCAG 2.2 Level AA, JIS X 8341-3:2016.
- Contrast >= 4.5:1 for all text (no large-text exception per DADS).
- Contrast >= 3:1 for non-text interactive elements.
- No color-only signaling — every state has text label or icon.
- No font-style: italic on Japanese text.
- No text-align: justify.
- Minimum font size 14px, body text 16px.
- All interactive elements must be keyboard-operable.
- Tab order must match visual order.
- Focus indicators per DADS: black outline + yellow background.
- Screen reader: no duplicate announcements, proper heading hierarchy,
  descriptive link text.
- Data tables: proper `<th>`, `<thead>`, `<tbody>` structure.
- Chart data must have text alternatives.
- Support `prefers-reduced-motion: reduce` for any animation.
- Overlay surfaces must have a solid border for Forced Color Mode.

### [official-must] Link distinguishability

Links must be visually distinguishable from surrounding text by more than
color alone. DADS recommends underline + distinct color as the standard
method.

### [official-guidance] Typography

DADS adopts Noto Sans JP and Noto Sans Mono. System fonts provided by the OS
or device are not prohibited by DADS.

### [official-guidance] Spacing

DADS uses an 8px grid scale as its standard spacing system. The important
principle is a consistent spacing scale.

### [official-guidance] Grid and breakpoints

DADS describes both 1-column and 12-column grid systems. The 768px breakpoint
is a standard example; services may define additional breakpoints as needed.

### [project-decision] Typography choice

The current generated dashboard CSS uses system fonts
(`"Segoe UI", system-ui, -apple-system, sans-serif`). DADS recommends Noto
Sans JP for body text and Noto Sans Mono for code and technical content (see
`[official-guidance] Typography` above). Adopting Noto requires updating
`docs/architecture.md`, `docs/decisions.md`, and
`StaticDashboardGenerator.cs` first.

### [project-decision] Spacing scale

This project uses the DADS 8px grid scale (8, 16, 24, 32, 64px) as its
spacing system.

### [project-decision] Color palette

This project uses the DADS color palette as its primary palette. Additional
colors for chart series or specialized observability semantics are permitted
when they meet the following requirements:

- An explicit semantic or chart-series role.
- Contrast validation (>= 4.5:1 for text, >= 3:1 for non-text).
- Color-universal-design validation (distinguishable under color vision
  simulations).
- Documentation as project tokens.

Do not use OKLCH color space (Impeccable default for new projects).

## Observability Dashboard Additions

- **[project-decision]** Trace tables, metric charts, span trees, filter
  panels, and detail panes do not have direct DADS equivalents. Design these as
  project-specific components while maintaining DADS foundations.
- **[project-decision]** Dense typography (Dns tokens) is appropriate for trace
  tables, metric panels, and other high-density observability views.
- **[project-decision]** Mono typography (Mono tokens) is appropriate for span
  IDs, trace IDs, durations, JSON payloads, and code-like content.
- **[official-must]** Semantic colors follow DADS: Success (green) for healthy
  spans, Error (red) for failed spans/errors, Warning (yellow/orange) for slow
  operations. Always pair with text labels or icons.
- **[official-must]** Charts and diagrams must have high-contrast text labels
  adjacent to low-contrast grid lines, and text alternatives for data
  visualizations.

## dads-ui-review Usage Policy

The dads-ui-review skill is a **reviewer**, not a designer.

### dads-ui-review MAY

- Audit accessibility, cognitive load, responsive behavior.
- Identify error states, empty states, and edge case handling.
- Suggest improvements to UI copy and error messages.
- Detect inconsistencies and AI-generated UI anti-patterns.

### dads-ui-review MUST NOT

- Override any [official-must] DADS rule.
- Make architecture decisions (framework, build tool, dependency changes).
- Reduce information density of dashboard views for aesthetic reasons.
- Auto-apply aesthetic changes without human review.

If dads-ui-review suggests a change that conflicts with a DADS [official-must]
rule, discard the suggestion and note the conflict.

## References

- DADS official: https://design.digital.go.jp/dads/
- DADS notices: https://design.digital.go.jp/dads/introduction/notices/
- Official React components: https://github.com/digital-go-jp/design-system-example-components-react
- Official HTML components: https://github.com/digital-go-jp/design-system-example-components-html
- DADS Tailwind plugin: https://github.com/digital-go-jp/tailwind-theme-plugin
- dads-foundations-core upstream: https://github.com/45deg/skills/tree/main/skills/dads-foundations-core

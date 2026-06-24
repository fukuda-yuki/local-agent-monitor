---
name: project-dads-policy
description: >-
  Project-specific DADS adoption policy for copilot-agent-observability.
  USE FOR: resolving design decisions, checking whether a UI change complies
  with project DADS policy, determining which design authority to follow when
  skills or tools conflict, understanding technology-stack constraints on DADS
  application.
  DO NOT USE FOR: looking up specific DADS token values (use
  dads-foundations-core), performing UI quality audits (use impeccable),
  backend-only tasks.
  READ THIS SKILL FIRST when any design-related skill or tool is activated.
license: MIT
metadata:
  author: copilot-agent-observability maintainers
  version: "1.0.0"
---

# Project DADS Policy

This skill defines how the Digital Agency Design System (DADS) is adopted in
the copilot-agent-observability project. Read this before applying any
design-related skill.

## Authority Order

When design guidance conflicts, resolve using this priority:

1. **DADS official site and official implementations** —
   https://design.digital.go.jp/dads/ is the authoritative source.
2. **This policy** — project-specific constraints below.
3. **dads-foundations-core skill** — LLM-oriented DADS digest (supplementary,
   not authoritative).
4. **impeccable skill** — UI quality reviewer (advisory, never overrides DADS).

If dads-foundations-core and the DADS official site disagree, follow the
official site.

## Technology Stack Constraints

This project uses:

- **.NET** (C#, Razor, potentially Blazor)
- **Static HTML dashboard** generation
- **CSS** (plain CSS or minimal preprocessor)
- **No React, no Tailwind CSS, no Storybook**

Consequences:

- Do not import React-specific DADS component implementations.
- Do not use Tailwind utility classes or the DADS Tailwind theme plugin.
- Do not reference Storybook stories or Vitest/Playwright test patterns from
  the official DADS React repository.
- Implement DADS tokens and patterns directly in CSS custom properties and
  semantic HTML.
- When the official DADS React repository contains useful design rules
  (accessibility, token usage, semantic HTML), extract the principle and
  apply it to the .NET/Razor/HTML stack.

## DADS Application Scope

### Always apply

- DADS color palette and contrast requirements (>= 4.5:1 for text,
  >= 3:1 for non-text).
- DADS typography: Noto Sans JP, Noto Sans Mono, weight 400/700 only.
- DADS spacing: 8px grid scale (8, 16, 24, 32, 64px).
- DADS 12-column grid system for desktop layouts.
- DADS link states: color + underline, focus ring (black outline + yellow bg).
- DADS icon rules: always paired with text labels, aria-hidden on decorative
  icons.
- DADS accessibility baseline: WCAG 2.2 Level AA, JIS X 8341-3:2016.
- No color-only signaling.
- No font-style: italic on Japanese text.
- No text-align: justify.
- Minimum font size 14px, body text 16px.

### Project-specific additions

- **Observability dashboard components** (trace tables, metric charts, span
  trees, filter panels, detail panes) do not have direct DADS equivalents.
  Design these as project-specific components while maintaining DADS
  foundations (color, typography, spacing, accessibility).
- **Dense typography** (Dns tokens) is appropriate for trace tables, metric
  panels, and other high-density observability views.
- **Mono typography** (Mono tokens) is appropriate for span IDs, trace IDs,
  durations, JSON payloads, and code-like content.
- **Semantic colors** follow DADS: Success (green) for healthy spans, Error
  (red) for failed spans/errors, Warning (yellow/orange) for slow operations
  or warnings. Always pair with text labels or icons.
- **Charts and diagrams** must follow DADS chart rules: high-contrast text
  labels adjacent to low-contrast grid lines, text alternatives for data
  visualizations.

### Do not

- Do not introduce custom design tokens that duplicate DADS tokens.
- Do not use OKLCH color space (Impeccable default for new projects). Use
  DADS hex palette values.
- Do not add decorative gradients, glassmorphism, or complex animations.
- Do not reduce information density for aesthetic reasons. Observability
  dashboards require data density.
- Do not override DADS link colors, focus styles, or contrast ratios.
- Do not use font families other than Noto Sans JP and Noto Sans Mono.

## Impeccable Usage Policy

The impeccable skill is permitted as a **reviewer**, not as a **designer**.

### Impeccable MAY

- Audit accessibility (contrast, keyboard navigation, screen reader
  compatibility, focus management).
- Review cognitive load and information hierarchy.
- Check responsive behavior across breakpoints.
- Identify error states, empty states, and edge case handling.
- Suggest improvements to UI copy and error messages.
- Detect inconsistencies and redundancies.
- Flag AI-generated UI anti-patterns.

### Impeccable MUST NOT

- Change fonts to anything other than Noto Sans JP / Noto Sans Mono.
- Introduce colors outside the DADS palette.
- Add decorative animations, gradients, shadows, or glassmorphism.
- Replace DADS-compliant components with alternative designs.
- Reduce information density of dashboard views.
- Override any DADS operating rule.

### Impeccable findings that conflict with DADS

If Impeccable suggests a change that conflicts with DADS, discard the
suggestion and note the conflict. DADS wins.

## Forced Colors and High Contrast

- All overlay surfaces (dialogs, dropdowns, tooltips) must have a solid
  border visible in Forced Color Mode.
- Do not rely on box-shadow alone for visual separation.
- Support `prefers-reduced-motion: reduce` for any animation.
- Support `forced-colors: active` by testing with Windows High Contrast mode.

## Keyboard and Screen Reader

- All interactive elements must be keyboard-operable.
- Tab order must match visual order.
- Focus indicators must follow DADS: black outline + yellow background.
- Screen reader announcements must not duplicate (no aria-label on icons
  paired with visible text).
- Data tables must use proper `<th>`, `<thead>`, `<tbody>` structure.
- Chart data must have text alternatives (data tables or descriptions).

## References

- DADS official: https://design.digital.go.jp/dads/
- DADS notices: https://design.digital.go.jp/dads/introduction/notices/
- Official React components: https://github.com/digital-go-jp/design-system-example-components-react
- Official HTML components: https://github.com/digital-go-jp/design-system-example-components-html
- DADS Tailwind plugin: https://github.com/digital-go-jp/tailwind-theme-plugin
- dads-foundations-core upstream: https://github.com/45deg/skills/tree/main/skills/dads-foundations-core

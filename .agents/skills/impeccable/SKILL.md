---
name: impeccable
description: >-
  DADS-constrained UI quality reviewer. Audits frontend interfaces for
  accessibility, cognitive load, responsive behavior, information hierarchy,
  UX copy, error states, and edge cases — all within DADS design system rules.
  USE FOR: reviewing static HTML dashboard output, auditing accessibility
  compliance, checking responsive behavior, reviewing UI copy and error
  messages, detecting AI-generated UI anti-patterns, validating keyboard
  navigation and screen reader compatibility.
  DO NOT USE FOR: establishing new design directions (DADS is the design
  authority), choosing colors/fonts/spacing (use dads-foundations-core),
  backend-only tasks.
  CONSTRAINT: This skill operates under project-dads-policy. All suggestions
  must comply with DADS. Suggestions that conflict with DADS are discarded.
license: Apache-2.0
metadata:
  author: pbakaus (original Impeccable), adapted for DADS-constrained use
  version: "1.0.0"
  upstream: https://github.com/pbakaus/impeccable
  upstream-version: "3.8.0"
---

# Impeccable (DADS-Constrained)

This skill reviews and improves frontend UI quality within the constraints of
the Digital Agency Design System (DADS). It is adapted from the Impeccable
agent skill for use in a DADS-compliant .NET observability dashboard project.

**This skill is a reviewer, not a designer.** Design authority belongs to DADS
and the project-dads-policy skill. Read project-dads-policy before applying
any suggestion from this skill.

## Activation

Use this skill when:

- A static HTML dashboard page needs quality review.
- Accessibility compliance needs auditing.
- Responsive behavior needs checking across breakpoints.
- UI copy, error messages, or empty states need improvement.
- Information hierarchy or cognitive load needs evaluation.
- Keyboard navigation or screen reader compatibility needs validation.

Do not use this skill to:

- Choose a color palette (use DADS palette via dads-foundations-core).
- Select fonts (DADS mandates Noto Sans JP / Noto Sans Mono).
- Establish spacing (DADS mandates 8px grid).
- Create new component designs (follow DADS patterns first).

## Constraint Gate

Before applying any suggestion from this skill, verify:

1. Does it comply with DADS operating rules?
2. Does it comply with project-dads-policy?
3. Does it maintain the required information density for observability views?

If any answer is no, discard the suggestion.

## Review Checklist

### Accessibility (Primary)

- [ ] All text meets DADS contrast requirement (>= 4.5:1, no large-text
      exception).
- [ ] Non-text interactive elements meet >= 3:1 contrast.
- [ ] No color-only signaling — every state has text label or icon.
- [ ] Links have underline + distinct color.
- [ ] Focus indicators: black outline + yellow background (DADS standard).
- [ ] Touch/click targets >= 24x24px (ideally 44x44px for primary actions).
- [ ] Keyboard tab order matches visual order.
- [ ] Screen reader: no duplicate announcements, proper heading hierarchy,
      descriptive link text.
- [ ] Data tables: proper `<th>`, `<thead>`, `<tbody>` structure.
- [ ] Charts/diagrams: text alternative or data table provided.
- [ ] `prefers-reduced-motion: reduce` supported for animations.
- [ ] `forced-colors: active` tested — overlay surfaces have solid borders.
- [ ] No `font-style: italic` on Japanese text.
- [ ] No `text-align: justify`.
- [ ] Text scalable to 200% without overlap or clipping.

### Cognitive Load & Information Hierarchy

- [ ] Visual hierarchy is clear: primary > secondary > tertiary information.
- [ ] Related elements are grouped closer together (proximity principle).
- [ ] Headings follow logical order (h1 > h2 > h3, no skipped levels).
- [ ] Dense views (trace tables, metrics) use Dense typography tokens.
- [ ] Code-like content (IDs, JSON, durations) uses Mono typography tokens.
- [ ] Text block width <= 40 full-width characters (80 half-width) for reading
      content. Dashboard data tables are exempt from this rule.
- [ ] Long content has navigation aids (table of contents, section headings).

### Responsive Behavior

- [ ] Desktop layout uses DADS 12-column grid.
- [ ] Mobile (< 768px) stacks to single column.
- [ ] Gutters >= 2x body font size (>= 32px at 16px base).
- [ ] No `overflow-x: hidden` on scrollable content.
- [ ] Horizontal scrollbars remain visible when needed.
- [ ] Headings do not overflow containers at any breakpoint.

### Error States & Edge Cases

- [ ] Empty states have clear messaging and suggested actions.
- [ ] Error states use DADS semantic colors (Error-1/Error-2) + text labels.
- [ ] Loading states provide feedback (not blank screens).
- [ ] Long text (trace names, file paths) truncates gracefully with tooltip
      or expandable display.
- [ ] Zero-data states are distinguishable from loading states.
- [ ] Large datasets have pagination or virtual scrolling.

### UX Copy

- [ ] Labels and headings are descriptive and specific.
- [ ] Error messages explain what happened and suggest resolution.
- [ ] Link text describes the destination (no "click here" or "details").
- [ ] Button labels describe the action (not just "OK" or "Submit").
- [ ] Japanese text uses appropriate register for the audience (developers).

### Observability-Specific

- [ ] Trace/span status uses DADS semantic colors with text labels.
- [ ] Duration values use Mono typography.
- [ ] Span trees maintain readable indentation at deep nesting levels.
- [ ] Filter controls are keyboard-accessible.
- [ ] Metric values have appropriate precision and units.
- [ ] Comparison views clearly distinguish baseline vs. variant.

## Prohibited Suggestions

The following Impeccable patterns are banned in this project:

- **Font changes**: Only Noto Sans JP and Noto Sans Mono are permitted.
- **Color changes outside DADS palette**: No OKLCH, no custom hues.
- **Decorative animations**: No bounce, elastic, or complex motion.
  `prefers-reduced-motion`-safe crossfades only.
- **Gradient text**: `background-clip: text` with gradient is banned.
- **Glassmorphism**: Blur/glass cards are banned.
- **Side-stripe borders**: `border-left`/`border-right` > 1px as accent is
  banned.
- **Over-rounded corners**: Cards max 12-16px border-radius.
- **Reduced information density**: Dashboard views require data density.
  Do not suggest reducing visible data for aesthetics.
- **Shadow + border combination**: Do not pair `border: 1px solid X` with
  `box-shadow: blur >= 16px` on the same element.
- **Stripe backgrounds**: `repeating-linear-gradient` stripe patterns are
  banned.

## Workflow

1. Read project-dads-policy to confirm current constraints.
2. Read dads-foundations-core if specific token values are needed.
3. Review the target HTML/CSS against the Review Checklist above.
4. For each finding:
   a. State the issue and which checklist item it violates.
   b. Propose a fix that complies with DADS.
   c. If the ideal fix would conflict with DADS, state the conflict and
      propose a DADS-compliant alternative.
5. Prioritize findings: accessibility issues first, then cognitive load,
   then responsive, then copy, then edge cases.
6. Do not auto-apply aesthetic changes. Report findings for human review.

## References

- Impeccable upstream: https://github.com/pbakaus/impeccable
- Impeccable site: https://impeccable.style/
- DADS official: https://design.digital.go.jp/dads/
- Project policy: [../project-dads-policy/SKILL.md](../project-dads-policy/SKILL.md)
- DADS foundations: [../dads-foundations-core/SKILL.md](../dads-foundations-core/SKILL.md)

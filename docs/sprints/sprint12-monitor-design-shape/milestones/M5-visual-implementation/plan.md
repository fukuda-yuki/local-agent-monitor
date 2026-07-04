# Sprint12 M5 - Visual Implementation (Plan)

## Goal

Apply the Sprint12 design shape to the Local Monitor UI.

Inputs:

- `DESIGN.md`
- `docs/sprints/sprint12-monitor-design-shape/milestones/M3-css-tokens/architecture.md`
- `docs/sprints/sprint12-monitor-design-shape/milestones/M4-page-layouts/spec.md`

## Scope

- Rewrite or extend `monitor.css` using the Sprint12 token architecture.
- Align the shared monitor layout with the sticky header, navigation, and content-area structure.
- Update Index, Traces, TraceDetail, Ingestions, and Diagnostics Razor markup where needed to use the shared components.
- Preserve existing routes, forms, links, raw sections, sanitized tab behavior, and SSE/API contracts.

## Non-goals

- No telemetry input changes.
- No SQLite schema or projection changes.
- No API field changes.
- No raw / sanitized boundary changes.
- No Canvas adapter contract changes.
- No Static Dashboard design changes.
- No new runtime or development dependencies.

## Implementation Notes

- Prefer existing Razor page structure and add classes only where the layout spec needs them.
- Keep raw content server-rendered and inert; do not introduce `Html.Raw`.
- Keep sanitized TraceDetail tabs available under `--sanitized-only`.
- Use the existing vendored fonts and vendor assets; do not add CDN references.

## Validation

M5 implementation is not complete until M6 validation has a passing record.

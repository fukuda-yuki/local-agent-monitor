---
name: "Copilot Agent Observability"
description: "Local-first agent workflow observability with a VS Code dark monitor and Grafana-like scan density."
colors:
  vscode-bg: "#1e1e1e"
  panel: "#252526"
  panel-raised: "#2d2d30"
  inset: "#1b1b1b"
  border: "#3c3c3c"
  border-subtle: "#2b2b2b"
  text: "#d4d4d4"
  text-strong: "#ffffff"
  text-muted: "#9d9d9d"
  accent-blue: "#4daafc"
  accent-blue-soft: "#8cc8ff"
  selection-blue: "#264f78"
  row-hover: "#2a2d2e"
  row-zebra: "#232325"
  error: "#f48771"
  error-bg: "#3a1d1d"
  success-teal: "#4ec9b0"
  warning-gold: "#cca700"
  flow-agent-purple: "#c586c0"
  flow-token-yellow: "#dcdcaa"
  graph-edge: "#5a5a5a"
typography:
  title:
    fontFamily: "Noto Sans JP, Segoe UI, system-ui, -apple-system, sans-serif"
    fontSize: "1.25rem"
    fontWeight: 600
    lineHeight: 1.25
    letterSpacing: "normal"
  body:
    fontFamily: "Noto Sans JP, Segoe UI, system-ui, -apple-system, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: "normal"
  label:
    fontFamily: "Noto Sans JP, Segoe UI, system-ui, -apple-system, sans-serif"
    fontSize: "0.78rem"
    fontWeight: 600
    lineHeight: 1.5
    letterSpacing: "0.03em"
  mono:
    fontFamily: "Noto Sans Mono, Cascadia Code, Consolas, Courier New, monospace"
    fontSize: "0.85rem"
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: "normal"
rounded:
  sm: "4px"
  md: "6px"
spacing:
  step-1: "4px"
  step-2: "8px"
  step-3: "16px"
  step-4: "24px"
  step-5: "32px"
components:
  nav-link:
    backgroundColor: "transparent"
    textColor: "{colors.text-muted}"
    typography: "{typography.body}"
    rounded: "{rounded.sm}"
    padding: "4px 16px"
  tab-selected:
    backgroundColor: "transparent"
    textColor: "{colors.text-strong}"
    typography: "{typography.body}"
    rounded: "{rounded.sm}"
    padding: "8px 16px"
  table-cell:
    backgroundColor: "{colors.vscode-bg}"
    textColor: "{colors.text}"
    typography: "{typography.body}"
    padding: "8px 16px"
  panel-container:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.text}"
    rounded: "{rounded.md}"
    padding: "16px 24px"
  row-toggle:
    backgroundColor: "{colors.panel-raised}"
    textColor: "{colors.text}"
    rounded: "{rounded.sm}"
    size: "1.6rem"
---

# Design System: Copilot Agent Observability

## 1. Overview

**Creative North Star: "The Local Agent Control Room"**

This design system is a local observability control room for agent workflows. It should feel close to Grafana in information density and operational scanability, but grounded in the current Local Ingestion Monitor posture: VS Code Dark+, vendored assets, no CDN, and a trusted single-user loopback workflow.

The system is intentionally quiet. It uses compact panels, sticky table headers, tabbed trace inspection, semantic status color, and progressive disclosure rather than large brand moments. It rejects marketing SaaS polish, decorative cards, and full-screen hero metrics because the user is diagnosing a run, not being sold the product.

**Key Characteristics:**

- Dense, table-first, and dashboard-like.
- VS Code dark surfaces with one primary blue accent.
- Grafana-like operational confidence without neon monitoring decoration.
- Raw/sanitized boundaries visible in structure, not hidden in copy.
- All runtime assets local and offline-safe.

## 2. Colors

The palette is a restrained VS Code Dark+ system with a single blue action accent and semantic status colors for errors, success, warning, and graph categories.

### Primary

- **Command Blue** (`accent-blue`): Used for links, focus rings, selected tabs, and current-selection affordances. It is the only general-purpose accent.
- **Selection Reservoir** (`selection-blue`): Used for selected segmented-control states and highlighted Timeline rows.

### Secondary

- **Agent Purple** (`flow-agent-purple`): Category color for agent invocation nodes in Flow Chart.
- **Cache Teal** (`success-teal`): Success color and LLM/cache category color.
- **Token Yellow** (`flow-token-yellow`): Tool-call category color in Flow Chart.
- **Warning Gold** (`warning-gold`): Hook or warning state color.

### Neutral

- **Editor Black** (`vscode-bg`): Page background and primary work surface.
- **Panel Graphite** (`panel`): Header, definition-list panels, and cache groups.
- **Raised Graphite** (`panel-raised`): Active navigation, table headers, compact controls.
- **Inset Black** (`inset`): Raw previews, graph canvas, cache metric tiles.
- **Workbench Border** (`border`, `border-subtle`): Thin structural separation.
- **Workbench Text** (`text`, `text-strong`, `text-muted`): Body, heading, and secondary metadata roles.

### Named Rules

**The One Accent Rule.** Command Blue is for action, selection, and focus. Do not add decorative accent colors to inactive surfaces.

**The Boundary Color Rule.** Raw-bearing areas use inset surfaces and muted text. Sanitized dashboard views use normal panel/table surfaces. The data boundary must be visible through layout and surface treatment.

## 3. Typography

**Display Font:** Noto Sans JP with Segoe UI and system fallbacks

**Body Font:** Noto Sans JP with Segoe UI and system fallbacks

**Label/Mono Font:** Noto Sans Mono with Cascadia Code, Consolas, and Courier New fallbacks

**Character:** Product UI typography is compact, plain, and operational. The same sans family carries labels, headings, navigation, and body text; monospace is reserved for ids, raw previews, token-like values, and trace metadata.

### Hierarchy

- **Title** (600, `1.25rem`, `1.25`): Page section headings such as Overview, Traces, Timeline, and Cache.
- **Subhead** (600, `1rem`, `1.25`): Dense panel headings and trace-detail subsection titles.
- **Body** (400, `14px`, `1.5`): Default UI text, table cells, explanatory status lines.
- **Label** (600, `0.78rem`, `0.03em`): Table headers and compact metadata labels.
- **Mono** (400, `0.85rem`, `1.5`): Trace ids, record ids, counters in definition lists, and raw preview content.

### Named Rules

**The No Display Type Rule.** This is a developer tool. Do not use oversized display headlines, decorative typefaces, or fluid hero typography.

**The Monospace Evidence Rule.** Use monospace only when the value behaves like evidence: ids, timestamps, counts, token-like fields, raw JSON, or code-shaped strings.

## 4. Elevation

The system is flat by default. Depth comes from tonal layering, borders, sticky headers, and inset panels rather than drop shadows. This is deliberate: operational dashboards need stable structure, not floating marketing cards.

### Named Rules

**The Tonal Layer Rule.** Use `panel`, `panel-raised`, and `inset` to create hierarchy. Do not add wide decorative shadows to tables, controls, or cards.

**The Border Is Structure Rule.** Borders separate dense information. They are 1px structural lines, not colored side accents.

## 5. Components

### Buttons

- **Shape:** Compact rectangle with small corners (`4px`).
- **Primary treatment:** Command Blue is reserved for selected state, focus, or true primary actions. Most controls stay graphite until selected.
- **Hover / Focus:** Hover shifts to `row-hover` or `panel-raised`; focus uses a 2px Command Blue outline.
- **Row toggle:** Fixed `1.6rem` square, graphite background, 1px border, plus/minus text.

### Chips

- **Style:** Use segmented controls for mode choices such as Timeline sort.
- **State:** Selected state uses `selection-blue`; inactive state uses `panel` and muted text.

### Cards / Containers

- **Corner Style:** Small corners only (`4px` to `6px`).
- **Background:** `panel` for grouped content, `inset` for raw/code/graph surfaces.
- **Shadow Strategy:** No resting shadows. Tonal layering and borders establish depth.
- **Border:** `border-subtle` by default; `border` for stronger component edges.
- **Internal Padding:** 16px for compact panels, 24px when a panel needs scan space.

### Inputs / Fields

- **Style:** Native checkbox and radio inputs are acceptable when they keep the product vocabulary familiar.
- **Focus:** Always keep visible blue focus outlines.
- **Error / Disabled:** Error text uses `error`; disabled or unavailable content uses `text-muted`.

### Navigation

- **Style:** Sticky top header, compact nav links, active page on raised graphite.
- **Typography:** 14px sans for normal navigation, no uppercase marketing labels.
- **Default / Hover / Active:** Muted text by default; hover uses raised graphite; active uses raised graphite plus strong text.

### Trace Views

- **Summary:** Server-rendered evidence table for trace-level rollups.
- **Timeline:** Flat, sortable, filterable span list. Errors-only filter and time/token sort stay visible.
- **Flow Chart:** Cytoscape/dagre DAG with category color and red error emphasis. Clicking a node moves the user into Timeline evidence.
- **Cache:** Request-group panels with cache hit rate, token breakdown, model, timestamp, and duration.

## 6. Do's and Don'ts

### Do:

- **Do** preserve the current VS Code Dark+ base tokens unless a source-of-truth spec changes.
- **Do** keep Grafana-like density: compact panels, tables, tabs, status text, and direct metric labels.
- **Do** make sanitized views and raw-bearing sections structurally distinct.
- **Do** keep focus visible and keyboard controls standard.
- **Do** use vendored local assets only for Local Monitor runtime UI.
- **Do** use progressive disclosure when a table has more columns than the primary task can scan.

### Don't:

- **Don't** turn this into a Copilot adoption, billing, productivity, ranking, or employee-monitoring dashboard.
- **Don't** show raw prompt, response, tool arguments, tool results, source fragments, credentials, or sensitive paths in repository-safe outputs, static dashboards, logs, or GitHub Pages snapshots.
- **Don't** copy VS Code Agent Debug / Chat Debug View or use VS Code internal logs, workspaceStorage, or chatSessions as inputs.
- **Don't** apply DADS styling to the Local Monitor.
- **Don't** use marketing hero sections, decorative card grids, gradient text, glass panels, neon dark-mode accents, or hero-stat layouts.
- **Don't** add broad shadows, 32px+ card radii, colored side-stripe borders, or decorative animation.
- **Don't** add CDN fonts, CDN graph libraries, or a frontend build step to the Local Monitor.

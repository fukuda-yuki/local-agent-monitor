# M3: monitor.css Token Architecture

DESIGN.md のトークンを実装ファイル `monitor.css` に落とし込むための構造仕様。

## 1. ファイルレイアウト

`wwwroot/monitor.css` は以下のセクション順で構成する:

```
1. @font-face declarations
2. CSS custom properties (global)
3. CSS reset / base
4. Layout primitives (nav, content area, sticky)
5. Component styles (tables, tabs, panels, filter, status, raw preview)
6. Utilities (visually-hidden, truncate, etc.)
7. Motion (transitions, reduced-motion)
```

## 2. @font-face

Noto Sans JP（6 weight variants）+ Noto Sans Mono（1 weight）。

```css
/* ── Noto Sans JP weights ── */
@font-face {
  font-family: 'Noto Sans JP';
  src: url('/vendor/fonts/NotoSansJP-Light.woff2') format('woff2');
  font-weight: 300;
  font-display: swap;
}
@font-face {
  font-family: 'Noto Sans JP';
  src: url('/vendor/fonts/NotoSansJP-Regular.woff2') format('woff2');
  font-weight: 400;
  font-display: swap;
}
@font-face {
  font-family: 'Noto Sans JP';
  src: url('/vendor/fonts/NotoSansJP-Medium.woff2') format('woff2');
  font-weight: 500;
  font-display: swap;
}
@font-face {
  font-family: 'Noto Sans JP';
  src: url('/vendor/fonts/NotoSansJP-SemiBold.woff2') format('woff2');
  font-weight: 600;
  font-display: swap;
}
@font-face {
  font-family: 'Noto Sans JP';
  src: url('/vendor/fonts/NotoSansJP-Bold.woff2') format('woff2');
  font-weight: 700;
  font-display: swap;
}

/* ── Noto Sans Mono ── */
@font-face {
  font-family: 'Noto Sans Mono';
  src: url('/vendor/fonts/NotoSansMono-Regular.woff2') format('woff2');
  font-weight: 400;
  font-display: swap;
}
```

## 3. CSS Custom Properties

全トークンを `:root` に集約。DESIGN.md §1 を完全に反映。

```css
:root {
  /* ── Surface ── */
  --monitor-bg:         oklch(0.19 0 0);
  --monitor-surface:    oklch(0.22 0 0);
  --monitor-surface-alt: oklch(0.25 0 0);
  --monitor-input-bg:   oklch(0.31 0 0);

  /* ── Border ── */
  --monitor-border:       oklch(0.28 0 0);
  --monitor-border-subtle: oklch(0.22 0 0);

  /* ── Ink ── */
  --monitor-ink:        oklch(0.85 0 0);
  --monitor-ink-subtle: oklch(0.70 0 0);
  --monitor-muted:      oklch(0.55 0 0);

  /* ── Accent (VS Code blue) ── */
  --monitor-accent:       oklch(0.54 0.17 255);
  --monitor-accent-hover: oklch(0.58 0.17 255);
  --monitor-accent-muted: oklch(0.35 0.06 255);

  /* ── Semantic ── */
  --monitor-success:    oklch(0.70 0.10 165);
  --monitor-warning:    oklch(0.72 0.10 95);
  --monitor-error:      oklch(0.55 0.18 25);

  --monitor-success-bg: oklch(0.22 0.03 165);
  --monitor-warning-bg: oklch(0.22 0.03 95);
  --monitor-error-bg:   oklch(0.22 0.04 25);

  /* ── Typography ── */
  --monitor-font-sans: 'Noto Sans JP', sans-serif;
  --monitor-font-mono: 'Noto Sans Mono', monospace;

  --monitor-text-xs:   0.6875rem;
  --monitor-text-sm:   0.75rem;
  --monitor-text-base: 0.8125rem;
  --monitor-text-md:   0.875rem;
  --monitor-text-lg:   1rem;
  --monitor-text-xl:   1.125rem;
  --monitor-text-2xl:  1.25rem;
  --monitor-text-3xl:  1.5rem;

  --monitor-leading-tight:  1.2;
  --monitor-leading-normal: 1.45;
  --monitor-leading-relaxed: 1.6;

  --monitor-weight-normal:   400;
  --monitor-weight-medium:   500;
  --monitor-weight-semibold: 600;
  --monitor-weight-bold:     700;

  /* ── Spacing ── */
  --monitor-space-1: 4px;
  --monitor-space-2: 8px;
  --monitor-space-3: 12px;
  --monitor-space-4: 16px;
  --monitor-space-5: 24px;
  --monitor-space-6: 32px;
  --monitor-space-7: 48px;

  /* ── Z-Index ── */
  --monitor-z-dropdown: 100;
  --monitor-z-sticky:   200;
  --monitor-z-overlay:  300;
  --monitor-z-tooltip:  400;
}
```

## 4. CSS Reset / Base

最小限のリセット。VS Code の慣習に従い、全要素で box-sizing、margin リセット。

```css
*,
*::before,
*::after {
  box-sizing: border-box;
  margin: 0;
  padding: 0;
}

html {
  font-family: var(--monitor-font-sans);
  font-size: 16px;                    /* ブラウザデフォルトに準拠、rem の基準 */
  -webkit-text-size-adjust: 100%;
  color-scheme: dark;
}

body {
  background: var(--monitor-bg);
  color: var(--monitor-ink);
  font-size: var(--monitor-text-base);
  line-height: var(--monitor-leading-normal);
  min-height: 100vh;
}

h1, h2, h3, h4, h5, h6 {
  font-weight: var(--monitor-weight-semibold);
  line-height: var(--monitor-leading-tight);
  text-wrap: balance;
}

h1 { font-size: var(--monitor-text-2xl); }
h2 { font-size: var(--monitor-text-xl); }
h3 { font-size: var(--monitor-text-lg); }

a {
  color: var(--monitor-accent);
  text-decoration: none;
}
a:hover {
  text-decoration: underline;
  color: var(--monitor-accent-hover);
}
a:visited {
  color: oklch(0.50 0.12 290);
}

code, pre, kbd {
  font-family: var(--monitor-font-mono);
}

pre {
  font-size: var(--monitor-text-sm);
  line-height: var(--monitor-leading-normal);
}

::selection {
  background: var(--monitor-accent-muted);
  color: var(--monitor-ink);
}

:focus-visible {
  outline: 2px solid var(--monitor-accent);
  outline-offset: 1px;
}
```

## 5. Layout Primitives

### 5.1 Sticky Header

```css
.monitor-header {
  position: sticky;
  top: 0;
  z-index: var(--monitor-z-sticky);
  background: var(--monitor-bg);
}

.monitor-nav {
  display: flex;
  align-items: center;
  gap: 0;
  height: 36px;
  padding: 0 var(--monitor-space-5);
  border-bottom: 1px solid var(--monitor-border);
  background: var(--monitor-surface);
}

.monitor-nav a {
  padding: 0 var(--monitor-space-3);
  height: 36px;
  display: flex;
  align-items: center;
  font-size: var(--monitor-text-sm);
  color: var(--monitor-muted);
}
.monitor-nav a:hover {
  color: var(--monitor-ink-subtle);
  text-decoration: none;
}
.monitor-nav a.active {
  color: var(--monitor-ink);
  box-shadow: inset 0 -2px 0 var(--monitor-accent);
}

/* Status indicator dot */
.monitor-nav-status {
  margin-left: auto;
  width: 8px;
  height: 8px;
  border-radius: 50%;
}
.monitor-nav-status.healthy  { background: var(--monitor-success); }
.monitor-nav-status.degraded { background: var(--monitor-warning); }
.monitor-nav-status.unhealthy { background: var(--monitor-error); }
```

### 5.2 Context Bar

```css
.monitor-context-bar {
  position: sticky;
  top: 36px;                        /* nav height */
  z-index: var(--monitor-z-sticky);
  display: flex;
  align-items: center;
  gap: var(--monitor-space-2);
  padding: var(--monitor-space-2) var(--monitor-space-5);
  background: var(--monitor-surface);
  border-bottom: 1px solid var(--monitor-border);
  flex-wrap: wrap;
}
```

### 5.3 Content Area

```css
.monitor-content {
  padding: var(--monitor-space-4) var(--monitor-space-5);
  /* 全幅、縦スクロール */
}
```

## 6. Component Styles

### 6.1 Data Tables

```css
.monitor-table-wrapper {
  overflow-x: auto;
  border: 1px solid var(--monitor-border);
  border-radius: 4px;
}

.monitor-table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--monitor-text-base);
  font-variant-numeric: tabular-nums;
}

.monitor-table th {
  position: sticky;
  top: 0;
  z-index: calc(var(--monitor-z-sticky) - 1);
  background: var(--monitor-surface);
  color: var(--monitor-muted);
  font-weight: var(--monitor-weight-medium);
  font-size: var(--monitor-text-sm);
  padding: var(--monitor-space-2) var(--monitor-space-3);
  border-bottom: 1px solid var(--monitor-border);
  text-align: left;
  white-space: nowrap;
  user-select: none;
}
.monitor-table th.sortable {
  cursor: pointer;
}
.monitor-table th.sortable:hover {
  color: var(--monitor-ink-subtle);
}
.monitor-table th[aria-sort="ascending"]::after {
  content: ' ▲';
}
.monitor-table th[aria-sort="descending"]::after {
  content: ' ▼';
}

.monitor-table td {
  padding: var(--monitor-space-2) var(--monitor-space-3);
  border-bottom: 1px solid var(--monitor-border-subtle);
  color: var(--monitor-ink);
  white-space: nowrap;
}
.monitor-table tr:hover td {
  background: var(--monitor-surface-alt);
}

/* Expandable rows */
.monitor-table tr.expanded td {
  background: var(--monitor-surface);
}
.monitor-table .row-detail {
  background: var(--monitor-surface);
  padding: var(--monitor-space-4);
  border-bottom: 1px solid var(--monitor-border);
  /* colspan set in Razor */
}
```

### 6.2 Metric Panels

```css
.monitor-metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: var(--monitor-space-3);
  margin-bottom: var(--monitor-space-5);
}

.monitor-metric-panel {
  background: var(--monitor-surface);
  border: 1px solid var(--monitor-border);
  border-radius: 4px;
  padding: var(--monitor-space-4);
}

.monitor-metric-panel .metric-label {
  font-size: var(--monitor-text-sm);
  color: var(--monitor-muted);
  margin-bottom: var(--monitor-space-1);
}

.monitor-metric-panel .metric-value {
  font-size: var(--monitor-text-3xl);
  font-weight: var(--monitor-weight-semibold);
  color: var(--monitor-ink);
  line-height: var(--monitor-leading-tight);
}
```

### 6.3 Tabs (TraceDetail)

```css
.monitor-tabs {
  display: flex;
  border-bottom: 1px solid var(--monitor-border);
  background: var(--monitor-surface);
}

.monitor-tab {
  padding: var(--monitor-space-2) var(--monitor-space-4);
  font-size: var(--monitor-text-sm);
  color: var(--monitor-muted);
  cursor: pointer;
  border: none;
  background: none;
  font-family: inherit;
  transition: color 150ms ease-out;
}
.monitor-tab:hover {
  color: var(--monitor-ink-subtle);
}
.monitor-tab.active {
  color: var(--monitor-ink);
  box-shadow: inset 0 -2px 0 var(--monitor-accent);
}

.monitor-tab-content {
  background: var(--monitor-surface);
  border: 1px solid var(--monitor-border);
  border-top: none;
  border-radius: 0 0 4px 4px;
  padding: var(--monitor-space-4);
}
```

### 6.4 Filter Inputs

```css
.monitor-select,
.monitor-input {
  height: 32px;
  padding: 0 var(--monitor-space-2);
  background: var(--monitor-input-bg);
  border: 1px solid var(--monitor-border);
  color: var(--monitor-ink);
  font-family: var(--monitor-font-sans);
  font-size: var(--monitor-text-sm);
  border-radius: 2px;
}
.monitor-select:focus,
.monitor-input:focus {
  outline: 2px solid var(--monitor-accent);
  outline-offset: 0;
}

.monitor-input::placeholder {
  color: var(--monitor-muted);
}
```

### 6.5 Buttons

```css
.monitor-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  height: 28px;
  padding: 0 var(--monitor-space-3);
  font-family: var(--monitor-font-sans);
  font-size: var(--monitor-text-sm);
  font-weight: var(--monitor-weight-medium);
  border: 1px solid var(--monitor-border);
  border-radius: 2px;
  background: var(--monitor-input-bg);
  color: var(--monitor-ink);
  cursor: pointer;
  transition: background 150ms ease-out, color 150ms ease-out;
}
.monitor-btn:hover {
  background: var(--monitor-surface-alt);
}
.monitor-btn:active {
  background: var(--monitor-border);
}

.monitor-btn-accent {
  background: var(--monitor-accent);
  border-color: var(--monitor-accent);
  color: #fff;
}
.monitor-btn-accent:hover {
  background: var(--monitor-accent-hover);
}
```

### 6.6 Raw Preview

```css
.monitor-raw-preview {
  background: var(--monitor-input-bg);
  border: 1px solid var(--monitor-border);
  border-radius: 4px;
  padding: var(--monitor-space-3);
  font-family: var(--monitor-font-mono);
  font-size: var(--monitor-text-sm);
  line-height: var(--monitor-leading-normal);
  color: var(--monitor-ink);
  white-space: pre-wrap;
  word-break: break-all;
  max-height: 400px;
  overflow-y: auto;
}
```

### 6.7 Status Text

```css
.monitor-status {
  font-weight: var(--monitor-weight-semibold);
}
.monitor-status.success  { color: var(--monitor-success); }
.monitor-status.warning  { color: var(--monitor-warning); }
.monitor-status.error    { color: var(--monitor-error); }

/* Tag / badge style */
.monitor-badge {
  display: inline-flex;
  align-items: center;
  padding: 1px var(--monitor-space-2);
  font-size: var(--monitor-text-xs);
  font-weight: var(--monitor-weight-medium);
  border-radius: 3px;
  line-height: var(--monitor-leading-tight);
}
.monitor-badge.success { background: var(--monitor-success-bg); color: var(--monitor-success); }
.monitor-badge.warning { background: var(--monitor-warning-bg); color: var(--monitor-warning); }
.monitor-badge.error   { background: var(--monitor-error-bg);   color: var(--monitor-error); }
```

### 6.8 Flow Chart (Cytoscape.js)

```css
.monitor-flow-chart {
  width: 100%;
  height: 500px;
  background: var(--monitor-bg);
  border: 1px solid var(--monitor-border);
  border-radius: 4px;
}

/* Cytoscape.js ノード色は JS 側で設定（デフォルトのみ定義） */
/* dagre layout は JS 側で初期化 */
```

## 7. Utilities

```css
.monitor-truncate {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.monitor-visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
}

.monitor-mono {
  font-family: var(--monitor-font-mono);
}

.monitor-text-right {
  text-align: right;
}
```

## 8. Motion

```css
/* ── Transitions ── */
.monitor-table tr,
.monitor-tab {
  transition: background 150ms ease-out, color 150ms ease-out;
}

.monitor-nav a {
  transition: color 150ms ease-out, box-shadow 150ms ease-out;
}

.monitor-btn {
  transition: background 150ms ease-out, color 150ms ease-out;
}

/* ── Reduced Motion ── */
@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    transition-duration: 0ms !important;
    animation-duration: 0ms !important;
  }
}
```

## 9. 既存コードの撤廃

現在の `monitor.css`（28行、Arial ベース）は全文削除し、上記アーキテクチャで書き直す。

依存する Razor Pages は、既存の class 名（`table`, `monitor-*` など）を上記セレクタに合わせて更新するか、
design token ベースのクラス名に移行する。

## 10. 検証方針

- `monitor.css` に lint エラーがないこと
- 全5ページで背景色・テキスト色・テーブル・タブの表示が一貫すること
- `--monitor-*` トークンが全セレクタで参照されていること（ハードコード色なし）

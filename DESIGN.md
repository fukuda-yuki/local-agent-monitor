# DESIGN.md

Local Monitor のビジュアルデザインシステム。VS Code Dark+ を基盤とし、Grafana の
情報密度・パネルレイアウトをレイアウトインスピレーションとして取り入れる。

## 1. Design Tokens

すべてのトークンは CSS custom properties で定義し、OKLCH 表記とする。

### 1.1 Color

```css
:root {
  /* ── Surface ── */
  --monitor-bg:         oklch(0.19 0 0);      /* ページ背景 #1e1e1e 相当 */
  --monitor-surface:    oklch(0.22 0 0);      /* パネル・カード・タブ背景 */
  --monitor-surface-alt: oklch(0.25 0 0);     /* ホバー行・選択行背景 */
  --monitor-input-bg:   oklch(0.31 0 0);      /* 入力要素背景 #3c3c3c 相当 */

  /* ── Border ── */
  --monitor-border:     oklch(0.28 0 0);      /* 基本境界線 */
  --monitor-border-subtle: oklch(0.22 0 0);   /* 微細境界線（テーブルセル内） */

  /* ── Ink ── */
  --monitor-ink:        oklch(0.85 0 0);      /* 本文（bg に対し ≥7:1） */
  --monitor-ink-subtle: oklch(0.70 0 0);      /* 補助的テキスト */
  --monitor-muted:      oklch(0.55 0 0);      /* 副次テキスト（≥4.5:1 vs bg） */

  /* ── Accent ── */
  --monitor-accent:     oklch(0.54 0.17 255);  /* VS Code blue #007acc 相当 */
  --monitor-accent-hover: oklch(0.58 0.17 255); /* ホバー時アクセント */
  --monitor-accent-muted: oklch(0.35 0.06 255); /* 控えめアクセント（選択行背景 tint） */

  /* ── Semantic ── */
  --monitor-success:    oklch(0.70 0.10 165);  /* 成功・正常 */
  --monitor-warning:    oklch(0.72 0.10 95);   /* 警告・degraded */
  --monitor-error:      oklch(0.55 0.18 25);   /* エラー・unhealthy */

  /* ── Semantic (muted / backgrounds) ── */
  --monitor-success-bg: oklch(0.22 0.03 165);  /* 成功背景（タグ・バッジ） */
  --monitor-warning-bg: oklch(0.22 0.03 95);   /* 警告背景 */
  --monitor-error-bg:   oklch(0.22 0.04 25);   /* エラー背景 */
}
```

**コントラスト保証**:
- `--monitor-ink` vs `--monitor-bg`: ≥7:1
- `--monitor-muted` vs `--monitor-bg`: ≥4.5:1
- `--monitor-accent` text vs `--monitor-bg`: ≥4.5:1
- カラー背景上のテキストは `--monitor-ink`（白）を使用

### 1.2 Spacing

8px ベースライングリッド。

```css
--monitor-space-1:  4px;    /* 0.5x — 最小間隔（inline 要素間） */
--monitor-space-2:  8px;    /* 1x   — 基本間隔 */
--monitor-space-3:  12px;   /* 1.5x — セクション内要素間 */
--monitor-space-4:  16px;   /* 2x   — セクション間、カードパディング */
--monitor-space-5:  24px;   /* 3x   — ページマージン、大セクション間 */
--monitor-space-6:  32px;   /* 4x   — ページトップマージン */
--monitor-space-7:  48px;   /* 6x   — メジャーセクション分割 */
```

### 1.3 Typography

フォント: **Noto Sans JP**（UI、本文）、**Noto Sans Mono**（コード、数値）。
`wwwroot/vendor/fonts/` に vendored。CDN 不使用。

```css
@font-face {
  font-family: 'Noto Sans JP';
  src: url('/vendor/fonts/NotoSansJP-Regular.woff2') format('woff2');
  font-weight: 400;
  font-display: swap;
}
/* 同様に Weight 300/400/500/600/700 を定義 */

@font-face {
  font-family: 'Noto Sans Mono';
  src: url('/vendor/fonts/NotoSansMono-Regular.woff2') format('woff2');
  font-weight: 400;
  font-display: swap;
}

:root {
  --monitor-font-sans: 'Noto Sans JP', sans-serif;
  --monitor-font-mono: 'Noto Sans Mono', monospace;

  /* Type scale (fixed rem, product UI) */
  --monitor-text-xs:   0.6875rem;   /* 11px — キャプション、バッジ */
  --monitor-text-sm:   0.75rem;     /* 12px — ラベル、補助情報 */
  --monitor-text-base: 0.8125rem;   /* 13px — 本文、テーブルセル */
  --monitor-text-md:   0.875rem;    /* 14px — 強調テキスト */
  --monitor-text-lg:   1rem;        /* 16px — サブ見出し */
  --monitor-text-xl:   1.125rem;    /* 18px — セクション見出し */
  --monitor-text-2xl:  1.25rem;     /* 20px — ページ見出し */
  --monitor-text-3xl:  1.5rem;      /* 24px — メトリクス数値 */

  --monitor-leading-tight: 1.2;
  --monitor-leading-normal: 1.45;
  --monitor-leading-relaxed: 1.6;

  --monitor-weight-normal: 400;
  --monitor-weight-medium: 500;
  --monitor-weight-semibold: 600;
  --monitor-weight-bold: 700;
}
```

## 2. Components

### 2.1 Navigation

```
┌──────────────────────────────────────────────────────┐
│ [Overview]  [Traces]  [Ingestions]  [Diagnostics]  ● │
└──────────────────────────────────────────────────────┘
```

- タブ型水平ナビゲーション。画面幅いっぱい。
- アクティブタブ: `--monitor-accent` の下線（2px） + テキスト色 `--monitor-ink`。
- 非アクティブ: `--monitor-muted`、ホバーで `--monitor-ink-subtle`。
- 右端にステータスインジケータ（●）: `--monitor-success` / `--monitor-warning` / `--monitor-error`。
- 高さ: 36px（`--monitor-space-4` + テキスト）。

### 2.2 Filter Bar

```
┌──────────────────────────────────────────────────────┐
│ [ClientKind ▼] [Status ▼] [Search...        ] [Reset]│
└──────────────────────────────────────────────────────┘
```

- 背景: `--monitor-surface`。border-bottom: 1px `--monitor-border`。
- `<select>` / `<input>` 要素: `--monitor-input-bg` 背景、border: 1px `--monitor-border`。
- フォーカス: `--monitor-accent` の 2px outline（inner）。
- 高さ: 32px の要素。パディング: `--monitor-space-4` 水平、`--monitor-space-2` 垂直。

### 2.3 Metric Panels (Overview)

```
┌──────────┐ ┌──────────────┐ ┌────────────────┐
│ Status   │ │ Proj Backlog │ │ Proj Lag       │
│ Healthy  │ │ 0            │ │ 2.3s           │
└──────────┘ └──────────────┘ └────────────────┘
```

- `display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: --monitor-space-3`。
- 各パネル: `--monitor-surface` 背景、border: 1px `--monitor-border`、border-radius: 4px。
- パディング: `--monitor-space-4`。
- ラベル: `--monitor-text-sm`、`--monitor-muted`。
- 値: `--monitor-text-3xl`、`--monitor-weight-semibold`、`--monitor-ink`。

### 2.4 Data Tables

```css
.monitor-table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--monitor-text-base);
}

.monitor-table th {
  position: sticky;
  top: 0;
  background: var(--monitor-surface);
  color: var(--monitor-muted);
  font-weight: var(--monitor-weight-medium);
  font-size: var(--monitor-text-sm);
  text-transform: none;              /* VS Code convention: no uppercase */
  letter-spacing: normal;
  padding: var(--monitor-space-2) var(--monitor-space-3);
  border-bottom: 1px solid var(--monitor-border);
  text-align: left;
  white-space: nowrap;
  cursor: pointer;                   /* sortable */
}

.monitor-table td {
  padding: var(--monitor-space-2) var(--monitor-space-3);
  border-bottom: 1px solid var(--monitor-border-subtle);
  color: var(--monitor-ink);
  font-variant-numeric: tabular-nums; /* 数値整列 */
  white-space: nowrap;
}

.monitor-table tr:hover td {
  background: var(--monitor-surface-alt);
}
```

- ソート矢印: `th` 内の `::after` 擬似要素。非ソート時は非表示、昇順 `▲`、降順 `▼`。
- 行ホバー: `--monitor-surface-alt`（oklch(0.25 0 0)）。
- テーブルラッパー: `overflow-x: auto`。横スクロール可能。

### 2.5 Tabs (TraceDetail)

```
┌──────────────────────────────────────────────────────┐
│ [Summary]  [Timeline]  [Flow Chart]  [Cache]          │
├──────────────────────────────────────────────────────┤
│  (JS-rendered sanitized content)                     │
└──────────────────────────────────────────────────────┘
```

- ナビゲーションタブと同じスタイルだが副次的階層のため少し小さく: フォント `--monitor-text-sm`。
- アクティブタブ: `--monitor-accent` 下線。
- コンテンツ領域: `--monitor-surface` 背景、border: 1px `--monitor-border`、border-top: none。
- パディング: `--monitor-space-4`。

### 2.6 Status Indicators

| 状態 | ドット | テキスト色 | 用途 |
|------|--------|-----------|------|
| Success / Healthy | `--monitor-success` | `--monitor-success` | 正常、成功 |
| Warning / Degraded | `--monitor-warning` | `--monitor-warning` | 警告、低下 |
| Error / Unhealthy | `--monitor-error` | `--monitor-error` | エラー、異常 |

- ドット: `width: 8px; height: 8px; border-radius: 50%; display: inline-block;`。
- テキストは色 + 太字（`--monitor-weight-semibold`）で強調。
- テーブルセル内ではテキスト色のみ（ドット省略、密度優先）。

### 2.7 Links

```css
a {
  color: var(--monitor-accent);
  text-decoration: none;
}
a:hover {
  text-decoration: underline;
  color: var(--monitor-accent-hover);
}
a:visited {
  color: oklch(0.50 0.12 290); /* 訪問済み紫（VS Code convention） */
}
```

### 2.8 Raw Preview

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

## 3. Layout Principles

### 3.1 Page Structure

```
┌─ Navigation (sticky top) ────────────────────────────┐
├─ Context Bar (filter/search, sticky) ─────────────────┤
├─ Content ─────────────────────────────────────────────┤
│  ┌─ Metric Panels ──────────────────────────────────┐│
│  ├─ Data Table ─────────────────────────────────────┤│
│  └─ ...                                             ┘│
└──────────────────────────────────────────────────────┘
```

- ナビゲーション + コンテキストバー: `position: sticky; top: 0; z-index: 10`。
- コンテンツ: 縦スクロール。水平方向はテーブルラッパーでスクロール。
- ページマージン: `--monitor-space-5`（24px）水平、`--monitor-space-4`（12px）垂直。
- 最大幅: 制限なし（全幅使用。データ密度優先）。

### 3.2 Responsive

- 画面幅 < 720px: ナビゲーションタブのパディング縮小（`--monitor-text-sm`）。
- テーブル: 横スクロールで対応（`overflow-x: auto`）。列の折りたたみは行わない。
- メトリクスパネル: `auto-fit, minmax(160px, 1fr)` で自動折り返し。
- フィルターバー: `auto-fit, minmax(140px, 1fr)` で自動折り返し。

### 3.3 Z-Index Scale

```css
--monitor-z-dropdown:  100;  /* select dropdown */
--monitor-z-sticky:    200;  /* sticky nav, sticky th */
--monitor-z-overlay:   300;  /* row-expand overlay */
--monitor-z-tooltip:   400;  /* tooltip */
```

## 4. Motion

- トランジション持続時間: 150ms（基本）、200ms（タブ切替）。
- イージング: `ease-out`（基本）。
- アニメーション対象: 行ホバー、タブ切替、ソート矢印回転、フィルター反映。
- ページロード時の演出なし。データは即時表示。
- `@media (prefers-reduced-motion: reduce)` で全トランジションを `0ms` に。

## 5. Accessibility

- WCAG AA 相当を確保。
- コントラスト: 本文 ≥ 4.5:1（実測 ≥ 7:1 を目標）、ラージテキスト ≥ 3:1。
- キーボード: タブナビゲーション、テーブルソート（Enter/Space）、フィルター選択。
- フォーカスインジケータ: `--monitor-accent` の 2px outline。`outline-offset: 1px`。
- ステータスは色 + テキスト/アイコンで伝達（色のみに依存しない）。
- `prefers-reduced-motion` 対応。
- フォントサイズは固定 rem。ブラウザズーム対応（200% まで）。

# DESIGN.md

Local Monitor のビジュアルデザインシステム。Sprint18 再設計（D042）で
`.claude/design_handoff_local_monitor/` のデザインハンドオフを正とする
**青みがかったダーク基調**へ更新した。開発者向けの情報密度と token コストの
可視化を最優先する Console 型 IA（208px サイドバー + master-detail）。

## 1. Design Tokens

すべてのトークンは CSS custom properties で定義する。**表記はハンドオフ §10 の
hex リテラルを authoritative とする**（ピクセル忠実再現の指示による。D042 C2。
旧 OKLCH 表記は廃止）。

### 1.1 Color

```css
:root {
  /* ── Surface ── */
  --monitor-bg:            #14171e;  /* ページ / アプリ背景 */
  --monitor-sidebar-bg:    #11141a;  /* サイドバー・インセット */
  --monitor-surface:       #1a1e28;  /* カード */
  --monitor-surface-inset: #12151c;  /* ドロワー・インスペクタ */
  --monitor-surface-panel: #161a22;  /* 一覧プレビュー列 */
  --monitor-selected-bg:   #1c2a3d;  /* 選択・アクティブ背景（llm カード兼用） */
  --monitor-segment-active:#2a3141;  /* セグメントアクティブ */
  --monitor-row-hover:     #212736;  /* 行ホバー */

  /* ── Border ── */
  --monitor-border:        #2a3141;  /* 基本境界線 */
  --monitor-border-divider:#232a38;  /* 区切り */
  --monitor-border-row:    #1c212c;  /* テーブル行 */
  --monitor-border-strong: #2f4a68;  /* 選択・強調 */

  /* ── Ink ── */
  --monitor-ink:        #e5e9f2;  /* 本文 */
  --monitor-ink-subtle: #9aa4b8;  /* 補助 */
  --monitor-muted:      #697487;  /* 弱 */

  /* ── Accent / Semantic ── */
  --monitor-accent:        #4da3e8;  /* 青: アクセント・llm・リンク・選択 */
  --monitor-accent-contrast:#0c1420; /* primary ボタン文字 */
  --monitor-token-gold:    #e0aa4e;  /* トークン金 */
  --monitor-warning:       #d9a942;  /* 警告琥珀 */
  --monitor-success:       #4cc38f;  /* 緑: 正常・キャッシュ */
  --monitor-success-dim:   #2c5844;  /* 緑バー淡色 */
  --monitor-error:         #e05e50;  /* 赤: エラー（旧 #e2604d は統一） */
  --monitor-agent:         #9d7bf0;  /* 紫: 実行中・agent */
  --monitor-parallel:      #56c8d8;  /* ティール: 並行分岐 */
  --monitor-parallel-dim:  #2e7d8c;  /* 並行 prefix */
  --monitor-elbow:         #2a3a4e;  /* エルボーコネクタ */

  /* ── Copilot ── */
  --monitor-copilot-ink:    #c0b6f2;
  --monitor-copilot-bg:     #2b2340;
  --monitor-copilot-border: #4a4080;
  --monitor-copilot-border-dim:  #3d3560;
  --monitor-copilot-border-hover:#8f8ffa;
}
```

**コントラスト保証**（WCAG AA、ダーク地で充足）:
- `--monitor-ink` vs `--monitor-bg`: ≥7:1
- `--monitor-muted` vs `--monitor-bg`: ≥4.5:1
- `--monitor-accent` text vs `--monitor-bg`: ≥4.5:1
- 状態は色 + 記号（● ■ ◆ ✓ ✕ ⑂ ├└）+ テキストで冗長化（色のみ禁止）

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

フォント: **Noto Sans JP**（UI、本文）、**Noto Sans Mono**（コード、数値、
`font-variant-numeric: tabular-nums`）。`wwwroot/vendor/fonts/` に vendored。
CDN 不使用。

> **Weight 600 の扱い（accepted deviation、D042 C7）**: デザインハンドオフの
> weight 600 指定は、vendored フォントに 600 が存在しないため CSS 上 700 へ
> マップする。

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

### 2.1 Shell（サイドバー + メインペイン）

```
┌──────────┬──────────────────────────────────────────┐
│ ■ Local  │  メインペイン (#14171e, padding 18-24px)  │
│  Monitor │                                          │
│  概要     │                                          │
│  トレース  n│                                          │
│          │                                          │
│ [● 正常 · │                                          │
│  受信中 ▸]│                                          │
│ 127.0.0.1│                                          │
└──────────┴──────────────────────────────────────────┘
```

- サイドバー: 幅 208px 固定、背景 `--monitor-sidebar-bg`、右境界 1px
  `--monitor-border-divider`、padding 16px 12px。
- ロゴ行: 10×10px 角丸 3px の青四角 `--monitor-accent` + 「Local Monitor」14px/700。
- ナビは **2 項目のみ**（概要 / トレース。トレースに件数バッジ Mono 11px）。
  アクティブ: 背景 `--monitor-selected-bg`、文字 `--monitor-ink`/700。
  「診断」はナビに置かず、最下部の受信ステータスバッジ → ポップオーバー →
  「詳細診断を開く」の段階的動線とする（D042 C1）。
- ステータスポップオーバー: 幅 340px、背景 `--monitor-popover-bg`、境界
  `--monitor-border-strong`、角丸 12px、影 `0 16px 48px rgba(0,0,0,0.6)`。
  パイプライン 4 行 + フッター 2 ボタン。Esc / 外側クリックで閉じる。

### 2.2 セグメントコントロール / 期間・ビュー切替

- 外枠: 境界 `--monitor-border`、角丸 7px、背景 `--monitor-surface-inset`、padding 2px。
- アクティブ: 背景 `--monitor-segment-active`、文字 `--monitor-ink`/700。
- 使用箇所: 概要の期間（今日/7日/30日）、詳細のフロー | waterfall、
  インスペクタの整形 | raw。

### 2.3 KPI / パネルカード

- カード: 背景 `--monitor-surface`、境界 `--monitor-border`、角丸 10px、
  padding 14-18px。カード間 gap 12-14px。
- KPI 値: Mono 28px/700（トークン金 `--monitor-token-gold`、エラー赤
  `--monitor-error`）。
- 積み上げバー配色: キャッシュ `--monitor-success-dim` / 入力
  `--monitor-accent` / 出力 `--monitor-token-gold`。

### 2.4 トレース一覧テーブル（master-detail）

- grid `minmax(0,1fr) 128px 148px 64px 64px 56px`
  （プロンプト / モデル / トークン / cache% / 所要 / 時刻）。
- 行 padding 9px 16px、行境界 `--monitor-border-row`、hover
  `--monitor-row-hover`、選択行 `--monitor-selected-bg`。
- トークン列: 5px 高の金ヒートバー + 値。既定はトークン降順（▼）。
- 右に 392px プレビューパネル（背景 `--monitor-surface-panel`）。
  行選択で遷移なしに更新し、「詳細を開く」primary ボタン
  （`--monitor-accent` 地 × `--monitor-accent-contrast` 文字/700）で詳細へ。

### 2.5 トレース詳細（フロー / waterfall + キャッシュ列）

- タブは廃止。1 画面に「実行の流れ」カード（flex:1）+ 右列 360px。
- フロー: 左レール 2px `--monitor-border-divider`（left 31px）。
  LLM ターン行 = ● 青 + カード（背景 `--monitor-selected-bg`、境界
  `--monitor-border-strong`、角丸 7px）。ツール分岐はエルボーコネクタ
  （2px `--monitor-elbow`、左下角丸 8px）。並行呼出は
  「⑂ 並行 N 件」（`--monitor-parallel`）+ 横並びカード（max-width 340px）。
- waterfall: grid `280px 1fr 76px 64px` + 25% 目盛。並行子行は
  prefix `├─`/`└─`（`--monitor-parallel-dim`）+ 開始位置揃え +
  左端 1px ティール影。tokens 列は llm のみ。
- 選択スパン: 境界 `--monitor-accent` +
  `box-shadow: 0 0 0 3px rgba(77,163,232,0.12)`、他要素 opacity 0.6。
- スパンインスペクタ: 右列に入替表示（幅は右列、背景
  `--monitor-surface-inset`、境界 `--monitor-border-strong`、角丸 10px）。
  整形タブ既定、raw タブで OTLP span JSON 全文。
- エラー解析モード: エラー要約ストリップ（背景 rgba(224,94,80,0.07)、境界
  #4a2a2a）、回復済み = 琥珀カード、未回復 = 赤カード + glow、右列は
  エラー一覧 / エラー詳細 / 入力トークン推移（128K 赤破線）の 3 カード。
- Copilot ドロワー: 幅 472px、右側スライドイン、背景
  `--monitor-surface-inset`、境界 `--monitor-copilot-border`、影
  `-24px 0 64px rgba(0,0,0,0.55)`。背面フローは opacity 0.55。
  必須コピー「ローカル SDK 経由 · raw はローカルから出ません」。

### 2.6 Status Indicators

| 状態 | 記号 | 色 | 用途 |
|------|------|----|------|
| 正常 / 成功 | ● + ピル | `--monitor-success` | ok、完了 |
| 回復済みエラー | ● + ピル | `--monitor-warning` | recovered |
| 未回復エラー | ✕ + ピル | `--monitor-error` | unrecovered、異常終了 |
| 実行中 / agent | ◆ | `--monitor-agent` | 開始マーカー、agent span |
| 並行分岐 | ⑂ / ├─ └─ | `--monitor-parallel` | 並行ツール呼出 |

- 状態は色 + 記号 + テキストの 3 点セット（色のみ禁止）。
- ピル背景は `rgba(色, 0.12)` 系（`--monitor-success-bg` など）。

### 2.7 Raw Preview / Mono ブロック

- `.monitor-raw-preview` / `.inspector-mono-block`: 背景
  `--monitor-bg` / `--monitor-surface-input`、境界 `--monitor-border`、
  Mono 10.5-12px、`white-space: pre-wrap`、max-height + overflow-y。
- captured content は常に escaped inert text（`Html.Raw` 不使用、JS は
  `createElement` / `textContent` のみ）。

## 3. Layout Principles

- デスクトップ 1440px 基準。最大幅制限なし（データ密度優先）。
- 8px グリッド準拠（カード padding 14-18px、gap 12-14px、ペイン
  padding 18-24px）。
- Radius: カード 10px / ポップオーバー・ドロワー 12px / 行カード・ボタン
  6-7px / バー 3-4px / ピル 99px。
- Elevation: ドロワー `-24px 0 64px rgba(0,0,0,0.55)` / ポップオーバー
  `0 16px 48px rgba(0,0,0,0.6)` / 選択 glow `0 0 0 3px rgba(色,0.08〜0.12)`。
- URL 状態: 詳細は `?view=flow|waterfall&span=<id>`、一覧は
  `?q&model&status&period&sort` を `history.replaceState` で反映。

## 4. Motion

- トランジション 150ms ease-out（基本）。ドロワー / ポップオーバーは 200ms。
- ページロード演出なし。データは即時表示。
- `@media (prefers-reduced-motion: reduce)` で全トランジション 0ms。

## 5. Accessibility

- WCAG AA。本文 ≥4.5:1、大文字 ≥3:1（§1.1 の Ink はダーク地で充足）。
- 状態は色 + 記号（● ■ ◆ ✓ ✕ ⑂ ├└）+ テキストで冗長化。
- キーボード: Esc（インスペクタ / ドロワー / ポップオーバーを閉じる）、
  Tab / Enter / Space でテーブル行・ソート・フィルター操作。
- フォーカスリング: `--monitor-accent` 2px outline、offset 2px。
- `prefers-reduced-motion` 対応。フォントサイズ固定 px/rem、ズーム 200% 対応。

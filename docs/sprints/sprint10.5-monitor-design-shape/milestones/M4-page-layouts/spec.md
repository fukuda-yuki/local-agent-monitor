# M4: Per-Page Layout Specifications

DESIGN.md と M3 トークンアーキテクチャに基づく、各ページの具体的なレイアウト仕様。

## 共通テンプレート: _Layout.cshtml

```
┌─ <html lang="en" class="monitor-root"> ──────────────┐
│ <head>                                               │
│  <meta charset>                                      │
│  <meta viewport>                                     │
│  <title>Local Monitor</title>                        │
│  <link rel="stylesheet" href="/monitor.css">          │
│ </head>                                              │
│ <body>                                               │
│  <div class="monitor-header">                        │
│   <span class="monitor-brand">Local Monitor</span>   │
│   <nav class="monitor-nav">                           │
│    [Overview] [Traces] [Ingestions] [Diagnostics]    ●│
│   </nav>                                              │
│   ── sticky top, z-index 200 ──                      │
│  </div>                                               │
│  <main class="monitor-content">                       │
│   @RenderBody()                                      │
│  </main>                                              │
│  <script src="/monitor.js" defer></script>            │
│ </body>                                               │
└───────────────────────────────────────────────────────┘
```

- `monitor-brand`: 画面左端。フォントサイズ `--monitor-text-md`、色 `--monitor-ink-subtle`。
- `monitor-nav`: `monitor-brand` の右隣。flex-gap: 0（タブ型密着配置）。
- ステータスドット: `margin-left: auto` で右端。未実装の場合は非表示。

## 1. Index（`/`）

### 目的
監視全体のダッシュボード。健康状態メトリクス + 直近のインジェスト一覧。

### レイアウト

```
┌─ .monitor-content ───────────────────────────────────┐
│                                                       │
│ <h2>Overview</h2>                                     │
│                                                       │
│ ┌─ .monitor-metrics ────────────────────────────────┐│
│ │ ┌──────────────┐ ┌──────────────┐ ┌────────────┐ ││
│ │ │ Status       │ │ Proj Backlog │ │ Proj Lag   │ ││
│ │ │ Healthy      │ │ 0            │ │ 2.3s       │ ││
│ │ └──────────────┘ └──────────────┘ └────────────┘ ││
│ │ ┌──────────────┐ ┌──────────────┐ ┌────────────┐ ││
│ │ │ ...          │ │ ...          │ │ ...        │ ││
│ │ └──────────────┘ └──────────────┘ └────────────┘ ││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ <h2>Recent Ingestions</h2>                            │
│ ┌─ .monitor-table-wrapper ──────────────────────────┐│
│ │ .monitor-table (columns: Time, TraceId,           ││
│ │   ClientKind, RecordCount, RawSize, ...)          ││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ (no pagination)                                       │
└───────────────────────────────────────────────────────┘
```

### メトリクスパネルマッピング

| `IndexModel` プロパティ | ラベル | 値の表示 | ステータス色 |
|--------------------------|--------|----------|--------------|
| `Status` | `Status` | Healthy/Degraded/Unhealthy | success/warning/error |
| `ProjectionBacklog` | `Proj Backlog` | 数値 | — |
| `ProjectionLagSeconds` | `Proj Lag` | `{n:F1}s` | warning if > 60s |
| その他（Model に追加された場合） | 各ラベル | 数値/文字列 | — |

### Razor 変更

```html
@* 現在の <dl> → <div class="monitor-metrics"> *@
<div class="monitor-metrics">
  <div class="monitor-metric-panel">
    <div class="metric-label">Status</div>
    <div class="metric-value monitor-status @(Model.Status.StatusClass)">
      @Model.Status
    </div>
  </div>
  <div class="monitor-metric-panel">
    <div class="metric-label">Proj Backlog</div>
    <div class="metric-value">@Model.ProjectionBacklog</div>
  </div>
  <div class="monitor-metric-panel">
    <div class="metric-label">Proj Lag</div>
    <div class="metric-value">@(Model.ProjectionLagSeconds.ToString("F1"))s</div>
  </div>
</div>
```

- テーブルには `class="monitor-table"` を付与。
- ラッパー `<div class="monitor-table-wrapper">` で囲む。

## 2. Traces（`/traces`）

### 目的
トレース一覧。フィルタリング + ソート可能なデータテーブル。

### レイアウト

```
┌─ .monitor-content ───────────────────────────────────┐
│                                                       │
│ <h2>Traces</h2>                                       │
│                                                       │
│ ┌─ .monitor-context-bar ────────────────────────────┐│
│ │ [ClientKind ▼] [Status ▼] [Search...     ] [Reset]││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ ┌─ .monitor-table-wrapper ──────────────────────────┐│
│ │ .monitor-table                                     ││
│ │  Time | Trace ID | ClientKind | Status | Spans    ││
│ │  (sortable) | (sortable) | ...    | ...    | ...  ││
│ │  ──────────────────────────────────────────────── ││
│ │  12:34 | abc123  | agent/v1  | OK    | 42        ││
│ │  12:33 | def456  | agent/v1  | ERR   | 15        ││
│ │  ...                                              ││
│ │  [▸ expand row detail]                            ││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ [Next page →]                                         │
└───────────────────────────────────────────────────────┘
```

### テーブル列定義（7列 + row expand）

| 列 | ヘッダー | ソート | 幅方針 |
|----|----------|--------|--------|
| FirstIngestionAt | `Time` | desc（デフォルト） | 160px |
| TraceId | `Trace ID` | ○ | 280px（mono） |
| ClientKind | `ClientKind` | ○ | 100px |
| Summary.Status | `Status` | ○ | 80px |
| TotalSpans | `Spans` | ○ | 64px（右寄せ） |
| Summary.FirstTurnAt | `First Turn` | ○ | 160px |
| Summary.LastTurnAt | `Last Turn` | ○ | 160px |

- Status 列: `.monitor-badge` で表示。
- TraceId: 各トレース詳細への `<a>` リンク。
- 行ホバー: `.monitor-table tr:hover td` で背景変更。

### フィルターバー

Server-rendered `<form method="get">`:

```html
<form class="monitor-context-bar" method="get">
  <select class="monitor-select" name="clientKind">
    <option value="">All ClientKinds</option>
    @foreach (var c in Model.ClientKinds)
    { <option value="@c" selected="@(c == Model.Filters.ClientKind)">@c</option> }
  </select>
  <select class="monitor-select" name="status">
    <option value="">All Statuses</option>
    <option value="OK">OK</option>
    <option value="ERROR">Error</option>
  </select>
  <input class="monitor-input" name="traceId"
         placeholder="Search Trace ID..."
         value="@Model.Filters.TraceId" />
  <button type="submit" class="monitor-btn">Filter</button>
  <a href="/traces" class="monitor-btn">Reset</a>
</form>
```

### 拡張可能行（将来）

`monitor-table tr` クリックで行展開。CSS クラス `expanded` を JS でトグル。
展開時 `.row-detail` を表示（span サマリー、トークン数など）。

```html
<tr class="expanded">
  <td colspan="7" class="row-detail">
    <!-- 追加情報 -->
  </td>
</tr>
```

## 3. TraceDetail（`/traces/{traceId}`）

### 目的
単一トレースの詳細。Sprint10 設計ビュー（Summary / Timeline / Flow Chart / Cache）を JS タブで提供。
下部に Razor レンダリングの Raw OTLP セクション。

### レイアウト

```
┌─ .monitor-content ───────────────────────────────────┐
│                                                       │
│ <a href="/traces">← Back to Traces</a>               │
│                                                       │
│ <h1>Trace <span class="monitor-mono">abc123</span>     │
│  <span class="monitor-badge success">OK</span>       │
│ </h1>                                                 │
│                                                       │
│ ┌─ .monitor-summary-table ──────────────────────────┐│
│ │ ClientKind │ Duration │ Spans │ Turns │ Errors    ││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ ┌─ .monitor-tabs ───────────────────────────────────┐│
│ │ [Summary] [Timeline] [Flow Chart] [Cache]          ││
│ ├─ .monitor-tab-content ────────────────────────────┤│
│ │  (JS-rendered sanitized content via               ││
│ │   GET /api/monitor/traces/{traceId}/spans)         ││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ <h2>Raw OTLP Payload</h2>                             │
│ ┌─ .monitor-raw-preview ────────────────────────────┐│
│ │ { "resourceSpans": [...] }                         ││
│ └───────────────────────────────────────────────────┘│
└───────────────────────────────────────────────────────┘
```

### サマリーテーブル（Razor）

キー/バリューの2列テーブル → `.monitor-table`:

| キー | 値 |
|------|-----|
| TraceId | `abc123`（mono） |
| ClientKind | `agent/v1` |
| RootSpanName | `CopilotAgent` |
| Status | `.monitor-badge` |
| FirstIngestionAt | `2025-06-15 12:34:56` |
| TotalSpans | `42` |
| TotalTurns | `3` |
| Duration | `12.3s` |
| TotalTokens | `input: 1200, output: 800` |
| ErrorCount | `0` |

### タブ UI（JS 実装、Sprint10 で実装）

```html
<div class="monitor-tabs" id="trace-tabs">
  <button class="monitor-tab active" data-tab="summary">Summary</button>
  <button class="monitor-tab" data-tab="timeline">Timeline</button>
  <button class="monitor-tab" data-tab="flowchart">Flow Chart</button>
  <button class="monitor-tab" data-tab="cache">Cache</button>
</div>
<div class="monitor-tab-content" id="trace-tab-content">
  <!-- Tab content rendered by JS -->
</div>
```

各タブの内容:

| タブ | データソース | レンダリング |
|------|-------------|-------------|
| Summary | spans API | ツリー表示、ツール呼び出し・結果・トークン・エラー |
| Timeline | spans API | 横軸: 時間、縦軸: span（ガントチャート風） |
| Flow Chart | spans API | Cytoscape.js + dagre（D025）で DAG 表示 |
| Cache | spans API | キャッシュヒット/ミス一覧 |

### 下段: Raw OTLP（Razor、変更なし）

```html
<h2>Raw OTLP Payload</h2>
@foreach (var raw in Model.RawRecords)
{
  <details>
    <summary>Record @raw.Sequence (@raw.CreatedAt)</summary>
    <pre class="monitor-raw-preview">@raw.BodyPreview</pre>
  </details>
}
```

- `<details>` + `<summary>` で折りたたみ。
- `<pre class="monitor-raw-preview">` でシンタックスハイライトなしの raw 表示。

## 4. Ingestions（`/ingestions`）

### 目的
生の受信ログ一覧。Raw レコードの時系列テーブル。

### レイアウト

```
┌─ .monitor-content ───────────────────────────────────┐
│                                                       │
│ <h2>Ingestions</h2>                                   │
│                                                       │
│ ┌─ .monitor-table-wrapper ──────────────────────────┐│
│ │ .monitor-table                                     ││
│ │  Time | TraceId | ClientKind | RawSize | Status   ││
│ │       | (link)  |            | (bytes) | (code)   ││
│ │  ──────────────────────────────────────────────── ││
│ │  12:34 | abc123  | agent/v1   | 4.2KB  | 200      ││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ [Next page]                                           │
└───────────────────────────────────────────────────────┘
```

### テーブル列

| 列 | ヘッダー | 内容 |
|----|---------|------|
| CreatedAt | `Time` | タイムスタンプ |
| TraceId | `Trace ID` | トレース詳細へのリンク |
| ClientKind | `ClientKind` | プレーンテキスト |
| RawSize | `Size` | フォーマット済みバイト数（`{n:F1} KB`） |
| HttpStatus | `Status` | `.monitor-badge`（2xx: success, 4xx: warning, 5xx: error） |
| ErrorMessage | `Error` | エラーメッセージ（該当時のみ） |
| — | `Raw` | Row Record 詳細へのリンク（`RawAvailable` 時のみ） |

## 5. Diagnostics（`/diagnostics`）

### 目的
ヘルスチェック結果。ヘルス/レディネスの現状を表示。

### レイアウト

```
┌─ .monitor-content ───────────────────────────────────┐
│                                                       │
│ <h2>Health / Readiness</h2>                           │
│                                                       │
│ ┌─ .monitor-metrics ────────────────────────────────┐│
│ │ ┌──────────────────┐ ┌──────────────────┐         ││
│ │ │ Overall Health   │ │ Overall Ready    │         ││
│ │ │ Healthy          │ │ Ready            │         ││
│ │ └──────────────────┘ └──────────────────┘         ││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ <h2>Checks</h2>                                       │
│ ┌─ .monitor-table-wrapper ──────────────────────────┐│
│ │ .monitor-table                                     ││
│ │  Name | Status | Duration | Error                  ││
│ │  ──────────────────────────────────────────────── ││
│ │  DB   | OK    | 1.2ms    | —                       ││
│ │  ...  | ...   | ...      | ...                     ││
│ └───────────────────────────────────────────────────┘│
│                                                       │
│ @if (Model.Readiness.DegradedReasons.Count > 0)       │
│  <div class="monitor-banner warning">                 │
│   Degraded: @reasons                                  │
│  </div>                                               │
└───────────────────────────────────────────────────────┘
```

### チェックテーブル

| 列 | ヘッダー | 表示 |
|----|---------|------|
| Name | `Check` | チェック名 |
| Status | `Status` | `.monitor-badge`（OK: success, Degraded: warning, Unhealthy: error） |
| Duration | `Duration` | 経過時間（`{n:F1}ms`） |
| Error | `Error` | エラー詳細（該当時。mono） |

### アラートバナー

```css
.monitor-banner {
  padding: var(--monitor-space-3) var(--monitor-space-4);
  border-radius: 4px;
  margin-top: var(--monitor-space-4);
  font-size: var(--monitor-text-sm);
}
.monitor-banner.warning {
  background: var(--monitor-warning-bg);
  color: var(--monitor-warning);
  border: 1px solid var(--monitor-warning);
}
.monitor-banner.error {
  background: var(--monitor-error-bg);
  color: var(--monitor-error);
  border: 1px solid var(--monitor-error);
}
```

## 実装優先度

1. **即時**: 共通テンプレート（`_Layout.cshtml` + `monitor.css` 共通部） — 全ページに影響
2. **高**: Index メトリクスパネル + テーブル — 最も視覚的インパクト
3. **高**: Traces テーブル + フィルターバー — 主要ビュー
4. **中**: Ingestions テーブル、Diagnostics メトリクス + テーブル
5. **Sprint10**: TraceDetail タブ UI（JS） + Flow Chart（Cytoscape.js）

## 参照

- `DESIGN.md` — 全トークン・コンポーネント定義
- `docs/sprints/sprint10.5-monitor-design-shape/milestones/M3-css-tokens/architecture.md` — CSS 実装構造
- `docs/sprints/sprint10-monitor-design-views/README.md` — TraceDetail タブアーキテクチャ（Sprint10）
- `docs/decisions.md` D024-D028

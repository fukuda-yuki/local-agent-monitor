# Sprint12: Local Monitor — Design Shape

Sprint12 は、Sprint10/Sprint11 後の Local Monitor visual foundation を整理し、
その設計を Local Monitor UI に反映する sprint である。

前半の M1-M4 は **Shape / documentation alignment** として、既存の Local Monitor
UI 方針、デザイントークン、ページレイアウト、および current source of truth に
昇格済みの仕様との整合を記録する。後半の M5-M6 は、その設計を `monitor.css` と
Razor pages に反映し、ブラウザ表示と既存境界を検証する。

## Decision

Sprint10 M1 の D027/D028 定義を整理し、Shape フェーズで以下を確認する：

- **D027 拡張**: VS Code Dark+ テーマの具体的カラートークンを OKLCH で定義。
  Grafana インスパイアのレイアウト方針（情報密度、パネル構成、フィルター配置）を
  明示的に Design Brief に組み込む。
- **D028 維持**: Noto Sans JP / Noto Sans Mono vendored。変更なし。

## Scope

M1-M4 はドキュメントのみ。M5 以降で Local Monitor の見た目を実装する。

- `docs/decisions.md` — D024–D028 追記
- `PRODUCT.md` — Visual Foundation セクション追加
- `docs/spec.md` — monitor views セクション更新
- `DESIGN.md` — 新規作成（VS Code Dark+ トークン、Noto タイポグラフィ、8px spacing、コンポーネント語彙）
- `monitor.css` トークンアーキテクチャ設計書
- 5ページレイアウト仕様書
- `monitor.css` 実装反映
- 共通レイアウト、Index / Traces / TraceDetail / Ingestions / Diagnostics の Razor 表示調整
- browser smoke / screenshot による主要ページ確認

## Milestones

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Docs & Decisions | `docs/decisions.md` D024–D028 追記、`PRODUCT.md` Visual Foundation 追加、`docs/spec.md` monitor views 更新 | Done |
| M2 DESIGN.md | `DESIGN.md` 新規作成。VS Code Dark+ カラートークン、Noto タイポグラフィ、8px spacing、コンポーネント語彙、semantic status colors | Done |
| M3 CSS Token Architecture | `monitor.css` のデザイントークン階層（colors / spacing / typography / components）の完全な設計書 | Done |
| M4 Page Layouts | 5ページ（Index / Traces / TraceDetail / Ingestions / Diagnostics）のレイアウト仕様書 | Done |
| M5 Visual Implementation | `DESIGN.md`、M3 CSS token architecture、M4 page layouts を `monitor.css` と Razor pages に反映する。telemetry 入力、SQLite schema、API field、raw / sanitized 境界、Canvas adapter contract は変更しない | Done |
| M6 Validation | build、Playwright Chromium bootstrap、full test、Local Monitor 主要ページ browser smoke / screenshot、raw 境界、`--sanitized-only` 表示を確認する | Done |

## M1-M4 Done

Shape / documentation alignment の4マイルストーンは完了。成果物:

| ファイル | サイズ | 内容 |
|----------|--------|------|
| `docs/decisions.md` D024–D028 | +2KB | 5件の設計判断を追記 |
| `PRODUCT.md` Visual Foundation | +1KB | テーマ・タイポグラフィ・カラー・スペーシング方針 |
| `docs/spec.md` monitor セクション | 改訂 | Sprint10 設計ビュー・vendor・テーマ追記 |
| `DESIGN.md` | 9,797B | 完全なデザインシステム（tokens / components / layout / motion / a11y） |
| `milestones/M3-css-tokens/architecture.md` | 14,187B | CSS 実装構造（@font-face / tokens / reset / components / utilities / motion） |
| `milestones/M4-page-layouts/spec.md` | 14,308B | 5ページレイアウト仕様 + 実装優先度 |

## M5-M6 Done

M5 で `monitor.css` を Sprint12 の OKLCH `--monitor-*` token architecture へ寄せ、
共通 layout、Traces、TraceDetail、JS 生成 Cache table の class を
`monitor-*` component vocabulary に揃えた。既存の telemetry 入力、SQLite schema、
API field、raw / sanitized 境界、Canvas adapter contract は変更していない。

M6 検証:

- `dotnet build CopilotAgentObservability.slnx` — Codex 実行で 0 warnings / 0 errors。
- `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests` — Codex 実行で 16 passing。
- `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorDesignViewPlaywrightTests` — Codex 実行で 2 passing。TraceDetail の Timeline / Flow Chart / Cache、sanitized-only、raw 非取得を確認。
- `pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium` — Codex 実行では timeout / user interrupt。ユーザー側実行で成功確認済み。
- `dotnet test CopilotAgentObservability.slnx` — Codex 実行で 555 passing（300 ConfigCli + 255 LocalMonitor）。ユーザー側実行でも成功確認済み。

## Non-goals

- Static Dashboard のデザイン変更（独立して維持）
- 新しい telemetry 入力、projection 列、スキーマ変更、API フィールド追加
- raw / sanitized 境界または Canvas adapter contract の変更
- DADS 適用

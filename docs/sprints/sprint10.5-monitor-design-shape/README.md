# Sprint10.5: Local Monitor — Design Shape

Sprint10.5 は Sprint10 M2（A3 Visual Polish）の **設計前工程（Shape）** である。
コード実装は行わず、以下のドキュメント成果物をアウトプットする。

Sprint10 M2 の実装（`monitor.css` 書き換え、Noto フォント vendor、全ページレイアウト反映）
は本スプリントの成果物を設計図として実行する。

## Decision

Sprint10 M1 の D027/D028 定義を拡張し、Shape フェーズで以下を確定する：

- **D027 拡張**: VS Code Dark+ テーマの具体的カラートークンを OKLCH で定義。
  Grafana インスパイアのレイアウト方針（情報密度、パネル構成、フィルター配置）を
  明示的に Design Brief に組み込む。
- **D028 維持**: Noto Sans JP / Noto Sans Mono vendored。変更なし。

## Scope

ドキュメントのみ。コード変更なし。

- `docs/decisions.md` — D024–D028 追記
- `PRODUCT.md` — Visual Foundation セクション追加
- `docs/spec.md` — monitor views セクション更新
- `DESIGN.md` — 新規作成（VS Code Dark+ トークン、Noto タイポグラフィ、8px spacing、コンポーネント語彙）
- `monitor.css` トークンアーキテクチャ設計書
- 5ページレイアウト仕様書

## Milestones

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Docs & Decisions | `docs/decisions.md` D024–D028 追記、`PRODUCT.md` Visual Foundation 追加、`docs/spec.md` monitor views 更新 | Done |
| M2 DESIGN.md | `DESIGN.md` 新規作成。VS Code Dark+ カラートークン、Noto タイポグラフィ、8px spacing、コンポーネント語彙、semantic status colors | Done |
| M3 CSS Token Architecture | `monitor.css` のデザイントークン階層（colors / spacing / typography / components）の完全な設計書 | Done |
| M4 Page Layouts | 5ページ（Index / Traces / TraceDetail / Ingestions / Diagnostics）のレイアウト仕様書 | Done |

## Done

全4マイルストーン完了。成果物:

| ファイル | サイズ | 内容 |
|----------|--------|------|
| `docs/decisions.md` D024–D028 | +2KB | 5件の設計判断を追記 |
| `PRODUCT.md` Visual Foundation | +1KB | テーマ・タイポグラフィ・カラー・スペーシング方針 |
| `docs/spec.md` monitor セクション | 改訂 | Sprint10 設計ビュー・vendor・テーマ追記 |
| `DESIGN.md` | 9,797B | 完全なデザインシステム（tokens / components / layout / motion / a11y） |
| `milestones/M3-css-tokens/architecture.md` | 14,187B | CSS 実装構造（@font-face / tokens / reset / components / utilities / motion） |
| `milestones/M4-page-layouts/spec.md` | 14,308B | 5ページレイアウト仕様 + 実装優先度 |

## Non-goals

- コード実装（CSS 書き換え、フォント vendor、Razor テンプレート変更） — Sprint10 M2 で実施
- Static Dashboard のデザイン変更（独立して維持）
- 新しい telemetry 入力、projection 列、スキーマ変更、API フィールド追加
- DADS 適用

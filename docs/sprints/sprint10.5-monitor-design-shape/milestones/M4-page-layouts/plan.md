# M4: Page Layout Specifications — 完了記録

## 成果物

`milestones/M4-page-layouts/spec.md` を新規作成（14,308 bytes）。

## 内容

全5ページ + 共通テンプレートのレイアウト仕様:

- **_Layout.cshtml**: sticky header + nav + content area の共通構造
- **Index（Overview）**: metric panels（grid）+ Recent Ingestions table。`<dl>` → `.monitor-metrics` 移行計画
- **Traces**: 7列テーブル（ソート可能）+ フィルターバー（server-rendered form）+ 拡張可能行仕様
- **TraceDetail**: サマリーテーブル + JSタブUI（Summary/Timeline/Flow Chart/Cache）+ Raw OTLP（`<details>` 折りたたみ）
- **Ingestions**: 7列テーブル + ページネーション
- **Diagnostics**: メトリクスパネル（Health/Ready）+ チェックテーブル + アラートバナー

### 追加コンポーネント

- `.monitor-banner`: 警告・エラーバナー（M3 architecture.md に未記載のため本 spec で定義）
- `.monitor-summary-table`: TraceDetail サマリー用テーブル

## 適合確認

- [x] 全ページが DESIGN.md のトークンを参照
- [x] テーブル、タブ、パネル、フィルターの一貫したコンポーネント使用
- [x] VS Code Dark+ 慣習準拠（リンク紫 visited、sortable th、no uppercase）
- [x] Razor 移行パス明示（class 名追加、`<dl>` → `.monitor-metrics`）
- [x] Sprint10 TraceDetail タブとの整合（サマリーテーブルは Razor で先行実装）

# M23: failure taxonomy / anti-pattern 定義

## 目的

trace-driven improvement loop の入口として、trace / metrics / M20 rubric の確認結果から人間が失敗要因と anti-pattern を分類できる taxonomy を定義する。

## 完了条件

- [x] failure taxonomy の分類軸と ID を定義する
- [x] agent anti-pattern の分類軸と ID を定義する
- [x] M20 rubric、M21 variant protocol、M22 report template との接続を定義する
- [x] 自動採用、自動実装、自動 repository 修正、自動勝敗決定を含めない境界を明記する
- [x] `docs/spec.md` に確定仕様を反映する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-06-03: `docs/sprints/sprint1-langfuse-poc/milestones/M23-failure-taxonomy/taxonomy.md` を追加し、failure category、anti-pattern、evidence requirements、severity、improvement target を定義した。
- 2026-06-03: `docs/spec.md` に M23 の taxonomy 仕様を反映した。
- 2026-06-03: `docs/task.md` を更新し、M23 を現行 milestone の完了項目へ移動した。
- 2026-06-03: 並列 Sub-Agent review の指摘に基づき、M17 `failure_type` との境界、`needs-review` 向け分類、M21 比較プロトコル由来の anti-pattern、M24 入力候補を補強した。
- 2026-06-03: documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

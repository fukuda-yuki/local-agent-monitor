# M20: 品質非劣化 rubric 定義

## 目的

baseline / variant 比較で使う人間評価用 rubric を定義する。

## 完了条件

- [x] 4 類型ごとの評価観点を定義する
- [x] `pass`、`fail`、`needs-review` の判断基準を定義する
- [x] rubric の記録様式を定義する
- [x] `docs/spec.md` に確定仕様を反映する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-06-01: M19 は `vscode-copilot-chat` 側 trace 取得 blocker により未完了だが、M20 は実測結果の採点ではなく人間評価 rubric の定義であるため先行着手可能と判断した。
- 2026-06-01: `docs/spec.md` に 4 類型ごとの評価観点、`pass` / `fail` / `needs-review` / `not-evaluated` の判定基準、評価記録の扱いを反映した。
- 2026-06-01: `rg` により `success_status`、M13 task 類型、M11-M22 の非スコープ境界に関する文書整合を確認した。
- 2026-06-01: documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

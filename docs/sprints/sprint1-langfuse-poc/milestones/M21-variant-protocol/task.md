# M21: variant / A-B 計測プロトコル

## 目的

baseline / variant 比較のための実験属性、実行順序、比較表の形式を定義する。

## 完了条件

- [x] `experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant` の使い分けを定義する
- [x] baseline / variant 実行プロトコルを定義する
- [x] 比較表の形式を定義する
- [x] `docs/spec.md` に確定仕様を反映する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-06-01: M19 は `vscode-copilot-chat` 側 trace 取得 blocker により未完了だが、M21 は実測ではなく比較プロトコルの仕様定義であるため先行完了可能と判断した。
- 2026-06-01: `docs/spec.md` に実験属性の使い分け、baseline / variant 実行順序、M17 台帳継承、M20 rubric に接続する比較表形式を反映した。
- 2026-06-01: documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

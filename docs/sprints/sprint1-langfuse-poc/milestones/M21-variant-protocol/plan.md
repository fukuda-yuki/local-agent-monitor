# Plan

## 2026-06-01

M21 は documentation-only milestone として、`docs/spec.md` に以下を確定する。

- `experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant` の比較プロトコル上の使い分け。
- baseline / variant を同一 task、client、prompt、fixture 条件で交互に実行する初期プロトコル。
- M17 の CSV 台帳、失敗・除外・sanitization 方針を variant 実行にも継承する方針。
- M20 の rubric に接続する baseline / variant 比較表の列定義。

実測 trace の取得、集計 CLI の変更、自動勝敗判定、改善案生成、自動採用、自動実装は M21 では扱わない。

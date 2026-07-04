# M24: trace-to-diagnosis MVP

## 目的

M23 taxonomy を使い、人間が分類した diagnosis record を安全に検証・整形できる MVP を追加する。

## 完了条件

- [x] diagnosis record の固定列と検証ルールを `docs/spec.md` に反映する
- [x] `validate-diagnoses` CLI を追加する
- [x] sanitized JSON input の top-level array と `{ "diagnoses": [...] }` を扱えるようにする
- [x] sanitized CSV input は固定 header を要求する
- [x] M17 `failure_type` と M23 `failure_category_id` の混同をエラーにする
- [x] raw content / credential / identity-bearing evidence を拒否する
- [x] synthetic fixture と deterministic tests を追加する
- [x] `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` を実行する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-06-03: `validate-diagnoses` CLI を追加し、M23 の failure category / anti-pattern / severity / improvement target / review status を検証するようにした。
- 2026-06-03: synthetic diagnosis fixture と tests を追加し、同一 trace 複数分類、`needs-human-review`、`F-COMPARISON` / `AP-CONFOUND`、不正 enum、空 evidence、危険 pattern、`failure_type` 混入を確認した。
- 2026-06-03: `docs/spec.md` と `docs/task.md` に M24 の現行仕様と milestone 状態を反映した。
- 2026-06-03: `dotnet build CopilotAgentObservability.slnx` 成功。
- 2026-06-03: Sub-Agent review の指摘に基づき、`evidence_summary` の安全判定、`task_run_index` の型検証、JSON string field の型検証、CSV 改行扱い、token metric evidence の過剰拒否を修正した。
- 2026-06-03: `dotnet test CopilotAgentObservability.slnx` 成功。66 tests passed。

# M26: proposal evaluator

## 目的

M25 の improvement proposal record を、人間承認 workflow に渡す前の deterministic pre-review として評価する最小 CLI を追加する。

## 完了条件

- [x] proposal evaluator の固定列と評価ルールを `docs/spec.md` に反映する
- [x] `evaluate-improvement-proposals` CLI を追加する
- [x] M25 proposal JSON / CSV を入力として扱う
- [x] proposal 入力を固定 schema と安全性ルールで検証する
- [x] `ready-for-human-approval` / `needs-revision` / `blocked` を deterministic に出力する
- [x] 自動採用、自動実装、repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定、改善効果判定を行わない
- [x] synthetic fixture と deterministic tests を追加する
- [x] `dotnet build CopilotAgentObservability.slnx` を実行する
- [x] `dotnet test CopilotAgentObservability.slnx` を実行する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-06-03: `evaluate-improvement-proposals` CLI を追加し、M25 proposal record を proposal evaluation record に変換するようにした。
- 2026-06-03: synthetic proposal fixture と tests を追加し、JSON / CSV 入力、3 種の evaluation status、空 input、安全性 validation、unknown column、missing output option を確認するようにした。
- 2026-06-03: `dotnet build CopilotAgentObservability.slnx` 成功。
- 2026-06-03: `dotnet test CopilotAgentObservability.slnx` 成功。90 tests passed。
- 2026-06-04: Sub-Agent review の指摘に基づき、proposal evaluator の非スコープ表現検出を採用、実装、repository 修正、patch / diff、commit / push / PR、勝敗決定、改善効果判定まで広げた。
- 2026-06-04: `dotnet test CopilotAgentObservability.slnx` 成功。101 tests passed。
- 2026-06-04: `dotnet build CopilotAgentObservability.slnx` 成功。
- 2026-06-04: Sub-Agent 再レビューで追加 finding なし。

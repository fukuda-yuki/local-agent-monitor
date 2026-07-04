# M25: improvement proposal generator

## 目的

M24 の検証済み diagnosis record から、人間が採否できる improvement proposal record を生成する最小 CLI を追加する。

## 完了条件

- [x] proposal record の固定列と生成ルールを `docs/spec.md` に反映する
- [x] `generate-improvement-proposals` CLI を追加する
- [x] M24 diagnosis JSON / CSV を入力として扱う
- [x] 入力 diagnosis を既存 validator で検証してから proposal を生成する
- [x] `review_status = accepted-for-proposal` の diagnosis だけを出力対象にする
- [x] deterministic template で proposal text を生成する
- [x] 自動採用、自動実装、repository 修正、patch / diff 生成を出力しない
- [x] synthetic fixture と deterministic tests を追加する
- [x] `dotnet build CopilotAgentObservability.slnx` を実行する
- [x] `dotnet test CopilotAgentObservability.slnx` を実行する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-06-03: `generate-improvement-proposals` CLI を追加し、M24 diagnosis record から proposal record を生成するようにした。
- 2026-06-03: synthetic diagnosis fixture と tests を追加し、JSON / CSV 入力、`accepted-for-proposal` filtering、空 output、安全性 validation、`failure_type` 混入を確認するようにした。
- 2026-06-03: `dotnet build CopilotAgentObservability.slnx` 成功。
- 2026-06-03: `dotnet test CopilotAgentObservability.slnx` 成功。77 tests passed。
- 2026-06-03: Sub-Agent review の指摘に基づき、proposal output 全体の安全性検証、CSV output assertion、複数 accepted diagnosis の proposal id 採番テストを追加した。

# M27: human approval workflow

## 目的

M26 の proposal evaluation record を入力として、人間による承認・却下・保留の判断を記録する最小 CLI を追加する。

## 完了条件

- [x] human approval workflow の固定列と検証ルールを `docs/spec.md` に反映する
- [x] `record-human-decisions` CLI を追加する
- [x] `generate-decision-template` CLI を追加する
- [x] M26 evaluation JSON / CSV を input reader で読み取る
- [x] human decision を fixed schema、proposal 存在確認、承認可否、安全性ルールで検証する
- [x] `approved` / `rejected` / `deferred` を記録し、`approved` は `ready-for-human-approval` のみ許可する
- [x] template は `ready-for-human-approval` の evaluation のみを対象に空行を生成する
- [x] 自動採用、自動実装、repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定、改善効果判定を行わない
- [x] synthetic evaluation fixture と deterministic tests を追加する
- [x] M23→M24→M25→M26→M27 E2E test を追加する
- [x] `dotnet build CopilotAgentObservability.slnx` を実行する
- [x] `dotnet test CopilotAgentObservability.slnx` を実行する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-06-04: `record-human-decisions` と `generate-decision-template` CLI を追加し、M26 evaluation record を human decision record に変換するようにした。
- 2026-06-04: `ProposalEvaluationInputReader`、`HumanDecisionInputReader`、`HumanDecisionValidator`、`HumanDecisionSafetyValidator`、`HumanDecisionOutputWriter`、`DecisionTemplateGenerator` を追加した。
- 2026-06-04: synthetic evaluation fixture と tests を追加し、JSON / CSV 入力、3 種の decision status、空 input、安全性 validation、unknown column、missing output option、proposal 存在確認、承認可否、E2E pipeline を確認するようにした。
- 2026-06-04: `dotnet build CopilotAgentObservability.slnx` 成功。
- 2026-06-04: `dotnet test CopilotAgentObservability.slnx` 成功。121 tests passed。

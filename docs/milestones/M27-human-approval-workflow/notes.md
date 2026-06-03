# M27 Notes

## 設計判断

- M26 evaluation の input reader は `ProposalEvaluationInputReader` として新規作成した。M26 の出力を M27 で読み取る必要があるが、M26 には output writer しかなかったため。
- `DecisionTemplateGenerator` は `ready-for-human-approval` の evaluation のみを対象にした。`needs-revision` / `blocked` は template に含めず、人間が修正・再評価してから decision workflow に入ることを期待する。
- `HumanDecisionValidator` は evaluations との cross-reference を行い、proposal 存在確認と承認可否を検証する。

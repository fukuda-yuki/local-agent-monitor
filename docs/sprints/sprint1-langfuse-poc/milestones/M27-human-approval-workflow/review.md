# M27 Review

## Self-review

2026-06-04

### 対象

- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/HumanApprovalWorkflowTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/evaluations.synthetic.json`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M27-human-approval-workflow/`

### 確認結果

- Spec compliance: `record-human-decisions` は M26 evaluation CSV / JSON と human decision CSV / JSON を読み取り、proposal 存在確認、decision status validation、承認可否、安全性を検証してから decision record を出力する。`generate-decision-template` は `ready-for-human-approval` evaluation のみを対象に空 template を生成する。自動採用、自動実装、repository 修正、patch / diff 生成、commit / push / PR 作成、自動勝敗決定、改善効果判定は実装していない。
- Functional correctness: `HumanDecisionValidator` は `approved` / `rejected` / `deferred` の 3 値を検証し、`approved` は `ready-for-human-approval` status のみ許可する。`needs-revision` / `blocked` の proposal への `approved` は拒否する。`rejected` / `deferred` は任意の evaluation status で許可する。`DecisionTemplateGenerator` は `ready-for-human-approval` の evaluation のみから template を生成する。
- Safety: decision record の全 string fields に `DiagnosisValidator.ContainsUnsafeMaterial` を適用し、raw content、credential、secret、token、identity-bearing material を拒否する。unknown column も拒否する。
- Tests: approved / rejected / deferred decisions、needs-revision / blocked proposal への approve 拒否、unknown proposal ID、invalid decision value、unsafe content、unknown column、missing output option、CSV input / output、JSON array input、empty input、decision template generation、no-ready template、E2E pipeline (M23→M24→M25→M26→M27) を確認している。
- Maintainability: M27 は既存の単一 CLI ファイル構成と M24-M26 の既存パターン（options record、input reader、validator、safety validator、output writer）に合わせて追加し、新しい dependency や workflow は追加していない。

### 検証

- `dotnet build CopilotAgentObservability.slnx` 成功。
- `dotnet test CopilotAgentObservability.slnx` 成功、121 tests passed。

### 残リスク

- `approved` は人間が proposal の方向性を承認した記録であり、改善の自動採用、自動実装、repository 修正を意味しない。実際の改善実装は M27 の scope 外である。

# M25 Review

## Self-review

2026-06-03

### 対象

- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/ImprovementProposalGenerationTests.cs`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M25-improvement-proposal-generator/`

### 確認結果

- Spec compliance: `generate-improvement-proposals` は M24 diagnosis reader / validator を通した後に `accepted-for-proposal` の行だけを proposal に変換している。自動採用、自動実装、repository 修正、patch / diff 生成、commit / push / PR 作成、自動勝敗決定は実装していない。
- Safety: proposal text は deterministic template のみで生成し、入力 `evidence_summary` は既存 M24 validator の unsafe evidence 判定を通る。さらに proposal output の string fields 全体に unsafe material 判定を適用する。
- Tests: JSON object input、top-level array input、CSV input / output、accepted-only filtering、複数 accepted diagnosis の採番、empty output、unsafe evidence rejection、unsafe copied metadata rejection、invalid taxonomy value、`failure_type` misuse、missing output option を確認している。
- Maintainability: M25 は既存の単一 CLI ファイル構成に合わせて追加し、新しい dependency や shared workflow は追加していない。

## Sub-Agent review

2026-06-03

- Functional review: blocking / major finding なし。追加推奨として CSV output 内容と複数 accepted diagnosis の proposal id 採番テストが挙がったため対応した。
- Spec / safety review: M25 の「出力に raw content 等を含めない」保証が `evidence_summary` 中心の検証に見えるという指摘があったため、proposal output の string fields 全体を unsafe material 判定する validator を追加した。
- Re-validation: `dotnet build CopilotAgentObservability.slnx` 成功。`dotnet test CopilotAgentObservability.slnx` 成功、77 tests passed。
- Re-review: functional review と spec / safety review の両方で新規 finding なし。先行指摘は解消済みと判断された。

### 残リスク

- proposal template は最小実装のため汎用的であり、M26 以降で proposal evaluator を作る場合は proposal text の評価観点を別途仕様化する必要がある。

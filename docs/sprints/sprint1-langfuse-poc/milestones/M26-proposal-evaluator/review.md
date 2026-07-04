# M26 Review

## Self-review

2026-06-03

### 対象

- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/ImprovementProposalEvaluationTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/proposals.synthetic.json`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M26-proposal-evaluator/`

### 確認結果

- Spec compliance: `evaluate-improvement-proposals` は M25 proposal CSV / JSON を読み取り、固定 schema、安全性、`human_review_status=needs-human-review` を検証してから evaluation record を出力する。自動採用、自動実装、repository 修正、patch / diff 生成、commit / push / PR 作成、自動勝敗決定、改善効果判定は実装していない。
- Functional correctness: evaluator は deterministic rule のみで `ready-for-human-approval`、`needs-revision`、`blocked` を出力する。M25 の「自動採用や patch / diff を行わない」という否定的な境界文言は ready 判定を妨げない。
- Safety: proposal input と evaluation output の string fields に unsafe material 判定を適用し、raw content、credential、secret、token、identity-bearing material を拒否する。
- Tests: JSON object input、top-level array input、CSV input / output、3 種の evaluation status、empty input、unsafe material rejection、invalid enum、unknown column、missing output option を確認している。
- Maintainability: M26 は既存の単一 CLI ファイル構成と既存 validator style に合わせて追加し、新しい dependency や workflow は追加していない。

### 検証

- `dotnet build CopilotAgentObservability.slnx` 成功。
- `dotnet test CopilotAgentObservability.slnx` 成功、90 tests passed。

### 残リスク

- evaluator は最小 deterministic pre-review であり、proposal の実質的な採否や効果確認は M27 human approval workflow 以降で人間が判断する必要がある。

## Sub-Agent review

2026-06-04

- Functional review: 非スコープ表現の `blocked` 判定が仕様より狭く、採用、実装、repository 編集、PR 作成、勝敗決定などの指示が `ready-for-human-approval` になり得るという major finding があった。妥当と判断し、blocked phrase と回帰テストを追加した。
- Spec / safety review: 同じ major finding に加え、M26 task / review の非スコープ境界に commit / push / PR 作成と改善効果判定が明示されていないという minor finding があった。妥当と判断し、文書に反映した。
- Main-Agent finding: M26 proposal validator が `failure_category_id` / `anti_pattern_id` の許可値を再検証していなかったため、M23 taxonomy の ID 検証と invalid ID tests を追加した。
- Re-validation: `dotnet test CopilotAgentObservability.slnx` 成功、101 tests passed。並列実行時に `dotnet build` が DLL lock で一度失敗したため、単独で再実行して成功した。
- Re-review: functional review と spec / safety review の両方で追加 finding なし。先行指摘は解消済みと判断された。

# M5: Langfuse 非依存 loop

## 目的

M4 の normalized dataset を使い、Langfuse が起動していなくても既存の改善支援 workflow に接続できることを確認する。

M5 は既存 CLI の接続確認を扱う。
trace からの自動診断、改善案の自動採用、自動実装、repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定は扱わない。

## 完了条件

- [x] synthetic raw OTLP fixture から `ingest-raw` を実行できる
- [x] raw store から `normalize-raw` を実行できる
- [x] normalized dataset と synthetic diagnosis record を使い、`validate-diagnoses` を実行できる
- [x] `generate-improvement-proposals` を実行できる
- [x] `evaluate-improvement-proposals` を実行できる
- [x] `generate-decision-template` または `record-human-decisions` を実行できる
- [x] E2E test は synthetic fixture と temp output だけで完結し、live Copilot / live Langfuse に依存しない
- [x] `diagnose` は人間分類 diagnosis record の validation に留め、trace から自動診断しない
- [x] `dotnet build CopilotAgentObservability.slnx` を実行する
- [x] `dotnet test CopilotAgentObservability.slnx` を実行する
- [x] 必要なレビューを `review.md` に記録する

## タスク分解

1. M4 output を既存 diagnosis / proposal / evaluation / decision workflow の入力にできる最小 fixture を用意する。
2. raw telemetry 由来の measurement と人間分類 diagnosis record の対応に必要な `trace_id` / `task_id` を確認する。
3. CLI chain の E2E test を追加し、Langfuse 未起動でも完結することを確認する。
4. 自動診断や repository 修正に見える処理が混入していないかレビューする。

## 検証記録

- 2026-06-08: `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/m5-diagnoses.synthetic.json` を追加し、M4 の `raw-otlp.synthetic.json` から生成される normalized row と `trace_id` / `task_id` / `client_kind` / `task_run_index` が一致する人間分類 diagnosis record を用意した。
- 2026-06-08: `LangfuseIndependentLoopTests.EndToEnd_RawStoreThroughHumanDecision_UsesSyntheticFixturesOnly` で `ingest-raw -> normalize-raw -> validate-diagnoses -> generate-improvement-proposals -> evaluate-improvement-proposals -> generate-decision-template -> record-human-decisions` を temp SQLite DB と synthetic fixture のみで通す E2E coverage を追加した。
- 2026-06-08: live Copilot / live Langfuse / network endpoint / Langfuse API key には依存しない。trace から failure category / anti-pattern を自動抽出する処理は追加していない。
- 2026-06-08: `dotnet build CopilotAgentObservability.slnx` は成功した。warning 0 / error 0。
- 2026-06-08: `dotnet test CopilotAgentObservability.slnx` は成功した。159 tests passed。

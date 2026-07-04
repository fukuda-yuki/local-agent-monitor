# M15: 集計 CLI / script MVP

## 目的

M14 で決めた入力形式を受け取り、M12 の measurement schema に従う CSV / JSON を生成する MVP を作る。

## 完了条件

- [x] 入力 fixture を用意する
- [x] CSV / JSON 出力を実装する
- [x] token、duration、tool call count、error count、欠損属性、未知 span 名を確認する
- [x] 自動テストを追加する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-24: `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/langfuse-legacy-traces.synthetic.json` に、実 trace / secret / 実ユーザーデータを含まない合成 Langfuse legacy trace fixture を追加した。
- 2026-05-24: `aggregate-measurements <input.json> --csv <output.csv> --json <output.json>` で M12 measurement schema の CSV / JSON を生成する MVP を追加した。
- 2026-05-24: 自動テストで Resource Attributes の snake_case 写像、`total_tokens` fallback、duration、暫定 tool call count、error count、欠損属性、未知 span 名、CSV escape、主要エラーケースを確認した。
- 2026-05-25: 観点別 Sub-Agent レビューで指摘された `observations` 欠損時の count 欠損表現、入力 I/O error message、output option 値検証、unknown metadata のコピー範囲、error count test gap を評価し、採用分を修正した。
- 2026-05-25: 再レビューで主要指摘の解消を確認し、軽微な残テストギャップとして挙がった `error` property 分岐と `prompt` / `source` 非伝播 assertion も追加した。
- 2026-05-24: `dotnet build CopilotAgentObservability.slnx` を実行し、成功した。
- 2026-05-25: `dotnet build CopilotAgentObservability.slnx` を実行し、成功した。
- 2026-05-25: `dotnet test CopilotAgentObservability.slnx` を実行し、45 件成功、失敗 0 件を確認した。

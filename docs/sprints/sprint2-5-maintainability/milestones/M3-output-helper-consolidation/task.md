# M3: Output helper consolidation

## 目的

重複した CSV / JSON helper を集約し、schema 追加時の修正漏れを減らす。

M3 は helper 集約を目的とし、CLI schema や出力 contract は変更しない。

## 完了条件

- [x] 重複している CSV escape 処理が洗い出されている
- [x] CSV cell escaping が `Shared/CsvEscaper.cs` などに集約されている
- [x] CSV line parsing の共通化が妥当な範囲で行われている
- [x] JSON output の共通パターンがある場合は `Shared/JsonOutput.cs` などに集約されている
- [x] measurement / diagnosis / proposal / evaluation / human decision の header と列順が変わっていない
- [x] CSV の空欄、JSON の `null`、末尾改行、indentation が変わっていない
- [x] invalid input error の意味が変わっていない
- [x] 同一の CSV escaping 実装が複数箇所に残っていない

## タスク分解

1. M2 後の責務別ファイルから、CSV escaping、CSV parsing、JSON serialization の重複を洗い出す。
2. 既存 behavior を保つ shared helper を追加する。
3. 各 output writer / input reader を shared helper へ最小差分で切り替える。
4. 出力 snapshot 相当の assertion または既存 assertion で互換性を確認する。
5. helper 集約による不要 code を削除する。

## 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`
- CSV escaping を含む既存 test または追加 test
- CSV / JSON output の header、列順、null / empty、末尾改行の互換性確認

## 注意事項

- 新しい CSV / JSON schema は追加しない。
- 出力形式の改善や error message の言い換えを目的にしない。
- M3 の変更は helper 集約に閉じる。

## 実施記録

- 2026-06-11: `MeasurementOutputWriter`、`DiagnosisOutputWriter`、`ImprovementProposalOutputWriter`、`ProposalEvaluationOutputWriter`、`HumanDecisionOutputWriter` の CSV escaping を `Shared/CsvEscaper.cs` に集約した。
- 2026-06-11: `DiagnosisInputReader`、`ImprovementProposalInputReader`、`ProposalEvaluationInputReader`、`HumanDecisionInputReader` の CSV line parsing を `Shared/CsvLineParser.cs` に集約した。
- 2026-06-11: measurement / diagnosis / proposal / evaluation / human decision output writer の indented JSON + trailing newline パターンを `Shared/JsonOutput.cs` に集約した。設定サンプル JSON は末尾改行 contract が異なるため対象外にした。
- 2026-06-11: `SharedOutputHelperTests` を追加し、CSV escaping、CSV parsing、JSON indentation / trailing newline を focused assertion で確認した。
- 2026-06-11: `rg -n "EscapeCsv|ParseCsvLine|new JsonSerializerOptions \{ WriteIndented = true \}" src\CopilotAgentObservability.ConfigCli tests\CopilotAgentObservability.ConfigCli.Tests` で production 側の CSV helper 重複が解消されたことを確認した。残存する `ConfigSamples` の JSON serialization は設定サンプル用、test-local `ParseCsvLine` は assertion helper であり、M3 集約対象外とした。

# M4: raw normalization

## 目的

raw store または raw OTLP JSON file から、M12 measurement schema に従う normalized dataset を CSV / JSON で生成する。

M4 は `normalize-raw` を扱う。
既存 `aggregate-measurements` は Langfuse export 風 JSON 向け command として維持し、責務を混ぜない。

## 完了条件

- [x] `config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]` を追加する
- [x] raw store 入力と raw JSON file 入力の両方を扱う
- [x] 出力列は M12 measurement schema に従う
- [x] 欠損値は CSV では空欄、JSON では `null` にする
- [x] unknown span / attribute は破棄せず、既存 measurement schema の補助 JSON 方針に従う
- [x] token、duration、tool call count、error count は M15 / M16 の集計方針に従う
- [x] `aggregate-measurements` の Langfuse export 入力処理を変更しない
- [x] `MeasurementRow` と `MeasurementOutputWriter.Columns` を measurement schema の単一 source of truth として使い、`normalize-raw` 用に出力列を複製しない
- [x] synthetic raw OTLP fixture と temp SQLite DB で normalization tests を追加する
- [x] unknown attribute sanitizer は既存方針と共有し、content / credential / identity-bearing data が output に混入しないことを確認する
- [x] `dotnet build CopilotAgentObservability.slnx` を実行する
- [x] `dotnet test CopilotAgentObservability.slnx` を実行する
- [x] 必要なレビューを `review.md` に記録する

## タスク分解

1. M12 measurement schema と既存 `aggregate-measurements` output writer の再利用可能範囲を確認する。
2. raw OTLP から Resource Attributes、usage、duration、error、tool call 情報を読み取る adapter を最小実装する。
3. raw OTLP adapter は Langfuse export aggregator と分け、出力 writer と sanitizer だけを共有する。
4. raw store 入力と raw JSON file 入力の分岐を command boundary に閉じ込める。
5. `aggregate-measurements` の既存 tests が regress しないことを確認する。

## 検証記録

- 2026-06-06: `normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]` を追加し、raw OTLP JSON file と SQLite raw store の両方から M12 measurement schema の CSV / JSON を生成できるようにした。
- 2026-06-06: raw OTLP adapter は `aggregate-measurements` の Langfuse export 入力処理とは分離し、`MeasurementRow` と `MeasurementOutputWriter.Columns` を共有する構成にした。
- 2026-06-06: synthetic raw OTLP fixture を拡張し、Resource Attributes、token fallback、duration、turn / tool / error count、unknown span、unknown Resource Attribute sanitizer を `RawNormalizationTests` で確認した。
- 2026-06-06: `dotnet build CopilotAgentObservability.slnx` は warning 0 / error 0 で成功した。
- 2026-06-06: `dotnet test CopilotAgentObservability.slnx` は 154 tests passed。

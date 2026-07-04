# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-25: M16 counting rules implementation review

### レビュー範囲

- `docs/spec.md`
- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/MeasurementAggregationTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/langfuse-legacy-traces.synthetic.json`
- `docs/sprints/sprint1-langfuse-poc/milestones/M16-counting-rules/`

### 観点

- `turn_count` が人間との会話ターン数ではなく trace 内 LLM round-trip 数として扱われていること。
- 明示 count がある場合に observation 分類結果より優先されること。
- VS Code 風の `chat` / `execute_tool` span 名と、CLI 風の GenAI attributes の両方を扱えること。
- permission / approval、hook、lifecycle event が count に混入しないこと。
- 未知 observation が破棄されず、かつ content capture 由来の raw payload をコピーしないこと。

### 結果

実装は既存 `aggregate-measurements` に閉じており、CSV / JSON の public schema、新規 dependency、Langfuse 接続方式は変更していない。
M15 の暫定 `tool_call_count` と固定 `turn_count=null` を、M16 の明示 count 優先と observation 分類 fallback に置き換えた。
合成 fixture は実 trace、credential、実ユーザーデータ、顧客データ、機密情報を含まない。

### 残リスク

実 Langfuse self-host response shape は合成 fixture ですべて網羅していない。
未知 observation は `unknown_spans_json` に保持し、M17 以降の実測で分類候補を追加できる余地を残す。

### 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`

### Sub-Agent レビュー後の修正

Sub-Agent レビューで、permission / approval / hook / lifecycle observation が tool / LLM 判定より後に評価されるため、重複属性を持つ observation が count に混入し得ると指摘された。
この指摘は採用し、分類順を non-counted observation 優先に変更した。
あわせて、`type=approval` かつ `kind=tool` と `gen_ai.tool.name` を持つ合成 observation を fixture に追加し、除外が tool 判定より強いことを既存 assertion で確認するようにした。

追加の観点別 Sub-Agent レビューで、tool と LLM 属性が重なる observation、汎用 `event` の扱い、`unknown_attributes_json` の過収集リスクが指摘された。
tool / LLM overlap は、tool を `turn_count` に含めない仕様を優先して tool call として分類するよう修正した。
汎用 `event` は一律除外せず unknown observation として保持し、明確な lifecycle event だけを非 count observation として除外する方針に修正した。
`unknown_attributes_json` は未知 Resource Attribute のうち content / credential 風 key を出力しないようにした。
これらに対応する synthetic fixture と assertion を追加した。

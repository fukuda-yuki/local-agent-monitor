# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-05-25: counting rules

M16 では、`turn_count` を人間との会話ターン数ではなく、trace 内の LLM round-trip 数として定義した。
VS Code GitHub Copilot Chat では `invoke_agent` root 配下に `chat` と `execute_tool` が並ぶため、`chat` を turn、`execute_tool` を tool call として扱う。
GitHub Copilot CLI は OTel GenAI conventions と vendor-specific metrics を出すため、`gen_ai.operation.name`、`gen_ai.tool.name`、`github.copilot.agent.turn.count`、`github.copilot.tool.call.count` を分類候補に含める。

分類の優先順位は、明示 count、既知 observation type / attribute、既知 span 名の順とする。
明示 count がある場合は、observation 数と食い違っていても明示 count を優先する。
permission / approval、hook、lifecycle event は、turn count と tool call count のどちらにも含めない。
tool と LLM の属性が重なった observation は、tool を `turn_count` に含めない方針を優先し、tool call として分類する。
汎用 `event` observation は一律に破棄せず、明確な lifecycle event だけを既知の非 count observation として除外する。

未知 observation は後続調査のため `unknown_spans_json` に残すが、content capture 由来の prompt、tool arguments、tool results、raw attributes はコピーしない。
保持するのは `id`、`name`、`type`、`kind` の最小識別情報だけとする。
未知 Resource Attribute は `unknown_attributes_json` に保持できるが、content または credential 由来と判断できる key は出力しない。

`observations` 欠損時は、明示 count がなければ `turn_count` / `tool_call_count` を欠損値として出力する。
`observations` が空配列の場合は、明示 count がなければ両方を `0` として出力する。

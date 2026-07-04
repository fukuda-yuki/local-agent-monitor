# M16: turn count / tool call count 算出ルール

## 目的

特定の span 名だけに依存しない turn count / tool call count の分類ルールを定義する。

## 完了条件

- [x] VS Code GitHub Copilot Chat と GitHub Copilot CLI の trace 差分を整理する
- [x] agent invocation、LLM call、tool call、permission / approval、file operation の分類方針を定義する
- [x] 未知 span 名の扱いを定義する
- [x] `docs/spec.md` と必要な実装に反映する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-25: `docs/spec.md` に、`turn_count` を trace 内 LLM round-trip 数、`tool_call_count` を trace 内 tool invocation 数として扱う正式分類ルールを追加した。
- 2026-05-25: `aggregate-measurements` の M15 暫定 count 実装を、明示 count 優先、observation 分類 fallback、未知 observation 保持の M16 ルールに更新した。
- 2026-05-25: 合成 fixture と xUnit tests で VS Code 風 span 名、CLI 風 GenAI attributes、明示 count 優先、permission / hook / event 除外、欠損 / 空配列の扱いを確認するよう更新した。
- 2026-05-25: `dotnet build CopilotAgentObservability.slnx` を実行し、成功した。
- 2026-05-25: `dotnet test CopilotAgentObservability.slnx` を実行し、45 件成功、失敗 0 件を確認した。

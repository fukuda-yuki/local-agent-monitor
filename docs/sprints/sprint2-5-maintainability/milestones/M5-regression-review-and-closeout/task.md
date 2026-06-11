# M5: Regression review and closeout

## 目的

Sprint2.5 の保守性改善が既存 behavior を壊していないことを確認し、review と closeout を記録する。

## 完了条件

- [ ] `dotnet build CopilotAgentObservability.slnx` が成功している
- [ ] `dotnet test CopilotAgentObservability.slnx` が成功している
- [ ] CLI help 出力が従来と変わっていないことを確認している
- [ ] 主要 command の exit code と stdout / stderr の互換性を確認している
- [ ] 分割によって不要な public 化、不要な dependency 追加、循環参照が発生していないことを確認している
- [ ] `review.md` に spec compliance、functional correctness、tests / regression risk、maintainability の観点を記録している
- [ ] Sprint2.5 README の milestone 状態を実績に合わせて更新している
- [ ] `docs/task.md` の Sprint2.5 状態を完了に更新している
- [ ] Sprint3 が Sprint2.5 完了後の候補として残っている

## タスク分解

1. M2-M4 の変更範囲、検証結果、残リスクを確認する。
2. full build / test を実行する。
3. CLI help と主要 command の互換性を確認する。
4. data handling と redacted E2E evidence に、保存禁止情報が含まれていないことを確認する。
5. `review.md` にレビュー結果、検証結果、残リスクを記録する。
6. Sprint2.5 README と `docs/task.md` を closeout 状態に更新する。

## 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`
- CLI help 出力
- 主要 command の focused smoke check
- repository diff の secret / identity / real content 混入確認

## 残リスクとして確認する事項

- Redacted real-trace E2E が synthetic coverage を置き換えていないこと。
- AppHost ローカルランチャー化が follow-up として分離されていること。
- Sprint3 の trace diagnosis が未実装の候補として残っていること。

# Plan

永続化する実装計画が必要になった場合に記録する。

## 2026-05-24 実施方針

M14 は documentation-only で実施する。
Langfuse export / API の候補を比較し、M15 の既定入力を Public API legacy trace / observation read response の保存 JSON として `docs/spec.md` に確定する。
認証情報や実 trace content は repository に保存しない。
集計 CLI 実装、依存追加、live Langfuse 確認は M14 では行わない。

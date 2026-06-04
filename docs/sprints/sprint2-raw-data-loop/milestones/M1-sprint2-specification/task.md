# M1: Sprint2 仕様化

## 目的

Sprint2 の raw telemetry store、normalized dataset、Langfuse 非依存改善ループについて、実装前に必要な要件、仕様、MVP 境界、タスク分割を確定する。

## 完了条件

- [x] Sprint2 MVP の入力方式を確定する
- [x] raw store の保存先、schema、migration 方針を確定する
- [x] normalized dataset 生成の入力と既存 measurement schema への接続方針を確定する
- [x] Langfuse UI の位置づけと Langfuse 非依存で動く範囲を確定する
- [x] retention、sanitization、repository に保存してよいデータ範囲を確定する
- [x] `docs/requirements.md` と `docs/spec.md` に確定内容を反映する
- [ ] Sprint2 の後続 milestone と task breakdown を作成する
- [x] 必要なレビューを `review.md` に記録する

## 次に行うこと

- Sprint2 MVP の実装順序を後続 milestone と task breakdown に分解する。
- 後続 milestone では、`ingest-raw`、SQLite raw store、`normalize-raw`、既存改善支援 CLI への接続を分けて扱う。
- 実装は、後続 milestone と task breakdown を作成してから開始する。

## 検証記録

- 2026-06-04: Sprint2 開始前調査で、現行 `docs/spec.md` と `docs/task.md` が Sprint2 を idea-level とし、schema / migration / CLI interface / 運用手順を未確定としていることを確認した。
- 2026-06-04: `dotnet test CopilotAgentObservability.slnx` で 121 件成功を確認した。
- 2026-06-04: `dotnet build CopilotAgentObservability.slnx` は並列実行由来の file lock 後、単独再実行で成功した。
- 2026-06-05: Sprint2 MVP の raw OTLP file-based ingest、SQLite raw store、`ingest-raw` / `normalize-raw`、Langfuse 非依存 loop、data handling を `docs/requirements.md` と `docs/spec.md` に反映した。

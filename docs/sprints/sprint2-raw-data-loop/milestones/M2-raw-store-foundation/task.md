# M2: raw store 基盤

## 目的

Sprint2 MVP の SQLite raw store schema version 1 を、実装前に `docs/spec.md` 5.17 と照合しながら最小構成で追加する。

M2 は storage 基盤だけを扱う。
`ingest-raw` CLI、`normalize-raw` CLI、Langfuse 非依存 loop の E2E 実行は後続 milestone で扱う。

## 完了条件

- [x] SQLite raw store schema version 1 を実装する
- [x] 既定 DB path `data/raw-store.db` を CLI から扱える形で定義する
- [x] raw record の固定列を `id`、`source`、`trace_id`、`received_at`、`resource_attributes_json`、`payload_json`、`schema_version` に限定する
- [x] `trace_id`、`received_at`、`source` の index を作成する
- [x] schema version は `1` のみとし、migration tool を追加しない
- [x] schema creation は同じ temp DB に対して複数回実行しても成功する
- [x] `source` は `raw-otlp`、`collector-output`、`langfuse-export` の値域に限定する
- [x] synthetic raw OTLP fixture を追加し、実 prompt / response content、credential、secret、Base64 header、実 user identity を含めない
- [x] `received_at` は test から固定できる形にし、temp SQLite DB を使う deterministic tests を追加する
- [x] `dotnet build CopilotAgentObservability.slnx` を実行する
- [x] `dotnet test CopilotAgentObservability.slnx` を実行する
- [x] 必要なレビューを `review.md` に記録する

## タスク分解

1. 現行 Config CLI の file / parser / writer の配置を確認し、raw store 用の最小責務を切る。
2. SQLite 利用に必要な dependency が既存にない場合は、追加可否と lockfile 影響を明示してから実装する。ORM や migration framework は追加しない。
3. `Program.cs` に DB 実装を直接積み上げず、raw store schema / record / repository の責務を最小の専用型に分ける。
4. temp DB で schema 作成、index 作成、record insert / read、複数回 schema creation、固定 `received_at` の最小テストを追加する。
5. raw payload の保存は synthetic fixture に限定し、repository に DB file を保存しないことを確認する。

## 検証記録

- `dotnet build CopilotAgentObservability.slnx`
  - 成功。0 warnings / 0 errors。
  - .NET preview SDK 使用メッセージ `NETSDK1057` は環境由来の informational message として表示された。
- `dotnet test CopilotAgentObservability.slnx`
  - 成功。128 tests passed。
  - 初回 test では temp SQLite DB が Windows 上で connection pooling により削除時ロックされたため、raw store と test helper の SQLite connection string に `Pooling=false` を設定して再実行した。

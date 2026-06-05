# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-06-05 M2 implementation review

### レビュー範囲

- Sprint2 M2 raw store 基盤実装。
- SQLite schema version 1、raw record model、repository、synthetic raw OTLP fixture、temp DB tests、M2 task 記録。

### 変更ファイル

- `src/CopilotAgentObservability.ConfigCli/CopilotAgentObservability.ConfigCli.csproj`
- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/RawTelemetryStoreTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json`
- `docs/sprints/sprint2-raw-data-loop/milestones/M2-raw-store-foundation/task.md`

### 指摘と対応

- 指摘: SQLite provider は .NET 標準だけでは利用できないため dependency 追加が必要。
  - 対応: `Microsoft.Data.Sqlite` `10.0.8` を Config CLI project に追加した。ORM / migration framework は追加していない。
- 指摘: Windows の temp DB 削除時に SQLite connection pooling が file lock を残す可能性がある。
  - 対応: raw store と test helper の SQLite connection string に `Pooling=false` を設定し、temp DB cleanup 前に pool clear する。
- 指摘: M2 の範囲を超えて `ingest-raw` / `normalize-raw` を実装しないこと。
  - 対応: CLI command は追加せず、M3 / M4 で接続可能な internal raw store API のみに限定した。

### 妥当性判断

- `docs/spec.md` 5.17 の fixed columns、allowed source、schema version `1`、index、既定 DB path、no migration tool の要件に適合している。
- synthetic fixture は raw OTLP `resourceSpans` / `scopeSpans` / `spans` envelope を使用し、実 prompt / response content、credential、secret、Base64 header、実 user identity を含まない。
- temp SQLite DB tests で schema creation idempotency、index、insert / read、nullable fields、固定 `received_at`、source constraint を確認している。

### 検証

- `dotnet build CopilotAgentObservability.slnx`
  - 成功。0 warnings / 0 errors。
- `dotnet test CopilotAgentObservability.slnx`
  - 成功。128 tests passed。

### 残リスク

- raw OTLP payload からの `trace_id` / Resource Attributes 抽出は M3 の範囲であり、M2 では未実装。
- raw store から measurement schema への normalization は M4 の範囲であり、M2 では未実装。

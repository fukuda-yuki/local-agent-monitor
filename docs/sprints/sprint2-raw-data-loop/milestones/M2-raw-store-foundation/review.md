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

## 2026-06-05 subagent review follow-up

### レビュー構成

- 仕様準拠 / M2 scope review: 指摘なし。
- SQLite 実装 / test review: 実装バグ指摘なし。schema metadata と non-UTC `received_at` の追加 test は有用な coverage gap と判断。
- data handling / dependency / documentation review: Low 指摘 1 件。

### Main-Agent 評価と対応

- Low: dependency 追加の lockfile 影響が記録されていない。
  - 妥当性: 妥当。M2 task は SQLite dependency 追加時に lockfile 影響を明示するとしている。
  - 対応: この review に、`Microsoft.Data.Sqlite` 追加理由と lockfile 影響を明記する。
  - lockfile 影響: repository に committed lockfile はなく、dependency 追加による lockfile 変更は発生していない。
  - 追加理由: `docs/spec.md` 5.17 が SQLite raw store を Sprint2 MVP の既定として定義しており、.NET 標準 library だけでは SQLite provider を利用できないため。
- Coverage gap: schema test が fixed column / index 名中心で、SQLite constraints の SQL shape を直接確認していない。
  - 妥当性: 妥当。M2 は schema foundation であり、constraint regression を捕捉する価値がある。
  - 対応: `raw_records` の `CREATE TABLE` SQL から `AUTOINCREMENT`、source CHECK、payload NOT NULL、schema_version CHECK を確認する test を追加。
- Coverage gap: non-UTC `received_at` の扱いが test されていない。
  - 妥当性: 妥当。実装は UTC normalization を行うため、意図を test で固定する。
  - 対応: non-UTC `DateTimeOffset` を insert し、UTC として read される test を追加。
- Coverage gap: malformed `payload_json` / non-object `resource_attributes_json` の validation がない。
  - 妥当性: M2 では非対応が妥当。raw store は raw payload 保持基盤であり、input validation は M3 `ingest-raw` の責務として扱う。
  - 対応: 実装変更なし。
- Robustness: `SqliteException` message text より error code assertion がよい。
  - 妥当性: 妥当。
  - 対応: source constraint test を SQLite constraint error code assertion に変更。

### 再レビュー後の追加対応

- Low: non-UTC `received_at` test が同一 instant の比較だけで、UTC offset 正規化を完全には固定できていない。
  - 妥当性: 妥当。`DateTimeOffset` equality は offset が異なっても同一 instant なら等価になり得る。
  - 対応: non-UTC `received_at` test に `record.ReceivedAt.Offset == TimeSpan.Zero` の assertion を追加。

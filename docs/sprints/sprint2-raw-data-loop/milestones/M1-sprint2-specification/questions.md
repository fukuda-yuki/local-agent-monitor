# M1 Questions

## 実装前に確定する事項

- Sprint2 MVP の入力方式は、Langfuse export、Collector output、raw OTLP のどれを既定にするか。
- SQLite を Sprint2 MVP の raw store として正式採用するか。
- SQLite を採用する場合、DB file の既定 path、削除手順、migration 方針をどうするか。
- raw record schema の列、型、id 生成、timestamp の意味、index をどう定義するか。
- raw payload に prompt / response / tool arguments / tool results / identity-bearing attributes が含まれ得る場合、どこまで保存し、どこから repository 保存禁止にするか。
- raw store から normalized dataset へ変換する CLI command 名と入出力形式をどうするか。
- 既存 `aggregate-measurements` は維持し、新しい raw-store normalizer を別 command にするか。
- `diagnose` は引き続き人間分類 record の validation とし、trace からの自動診断は Sprint2 MVP に含めないか。
- Langfuse が起動していない状態で成功とみなす最小 loop をどこまでにするか。
- README / getting-started に Langfuse 非依存フローを載せるタイミングをいつにするか。

# Sprint1: Langfuse PoC

Sprint1 は、GitHub Copilot Chat / GitHub Copilot CLI の OpenTelemetry データを Langfuse に取り込み、Agent / MCP / Skills / CLI の挙動を trace 単位で確認できるかを検証した PoC である。

この sprint は完了済みの参照資料として扱う。新しい実装判断は、上位要件である `../../requirements.md` と詳細仕様である `../../spec.md` を確認してから行う。

## 確認できたこと

- VS Code GitHub Copilot Chat と GitHub Copilot CLI の OTel 出力を扱うためのローカル検証手順を整理した。
- Langfuse self-host を Phase 1 baseline observability backend として使う方針を定義した。
- prompt、response、tool arguments、tool results、token usage、duration、error を確認するための設定と手順を整理した。
- `client.kind`、`experiment.id`、`task.id` などで trace を分類する方針を定義した。
- Langfuse export / API 由来のデータから研究用 CSV / JSON を生成する測定 schema と deterministic CLI 群を整備した。
- trace 由来の diagnosis、proposal、proposal evaluation、human decision までを扱う改善支援の土台を整理した。

## Langfuse の位置づけ

Sprint1 では Langfuse を baseline observability backend として扱う。
ただし、Langfuse は改善ループそのものに不可欠な実行エンジンではなく、trace を人間が確認する dashboard / trace viewer として再利用できる候補である。

Sprint2 では、raw telemetry / normalized dataset を source of truth とし、Langfuse に依存しない改善ループを検討する。

## ディレクトリ

- `milestones/`: M0-M28 と user-facing docs refresh までの task、plan、questions、notes、review
- `knowledge/`: Sprint1 中に得た cross-milestone の判断、調査、確認済み事項
- `archive/review/`: 旧 Phase 0 / Phase 1 の過去レビュー記録

## Sprint2 送り

- raw JSON を Langfuse 非依存で保持する最小基盤を検討する。
- raw data store から normalized dataset を生成し、既存 measurement schema と接続する。
- collect / normalize / diagnose / propose / evaluate / human decision の改善ループを raw / normalized dataset 入力に再構築する。
- Langfuse UI を source of truth ではなく dashboard / trace viewer として再位置づける。

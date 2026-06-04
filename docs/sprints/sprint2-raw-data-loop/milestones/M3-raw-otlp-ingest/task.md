# M3: raw OTLP ingest

## 目的

M2 の SQLite raw store に対して、Sprint2 MVP の既定入力である raw OTLP JSON file-based ingest を追加する。

M3 は `ingest-raw` だけを扱う。
raw store から measurement schema へ変換する `normalize-raw` は M4 で扱う。

## 完了条件

- [ ] `config-cli ingest-raw <raw.json> --db <raw-store.db>` を追加する
- [ ] 入力は保存済み raw OTLP JSON file に限定し、自前 HTTP receiver、常駐 process、独自 OTLP receiver を追加しない
- [ ] synthetic fixture は raw OTLP JSON の `resourceSpans` / `scopeSpans` / `spans` envelope と OTLP attribute value shape を使い、Langfuse export 風 `traces` schema で代用しない
- [ ] raw OTLP payload から取得できる `trace_id` と Resource Attributes を raw record に保存する
- [ ] `received_at` は取り込み時刻として保存し、payload 内の span start / end time と混同しない
- [ ] `source=raw-otlp` を既定として保存する
- [ ] malformed JSON、missing input、missing `--db`、unknown option を deterministic error として扱う
- [ ] synthetic raw OTLP fixture で ingest tests を追加する
- [ ] credential、secret、Base64 header、実 user identity を含む検証入力を使わない
- [ ] `dotnet build CopilotAgentObservability.slnx` を実行する
- [ ] `dotnet test CopilotAgentObservability.slnx` を実行する
- [ ] 必要なレビューを `review.md` に記録する

## タスク分解

1. `Program.cs` の command dispatch と option parser に `ingest-raw` を追加する。
2. raw OTLP JSON の最小 trace id / Resource Attributes 抽出ルールを実装し、`resourceSpans` が複数ある場合も fixture で確認する。抽出不能な場合は null として保存する。
3. CLI error message と exit code を既存 command の style に合わせる。
4. temp DB を使って、複数 span / 欠損 trace id / 欠損 Resource Attributes の保存結果を確認する。

## 検証記録

- 未実施。

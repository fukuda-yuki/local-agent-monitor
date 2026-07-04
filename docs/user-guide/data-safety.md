# Data Safety

Copilot Agent Observability は agent workflow の詳細を扱うため、raw telemetry には sensitive content が含まれ得ます。
この文書の境界を守ってください。

## 保存してよいもの

Repository に保存してよいもの:

- synthetic fixture。
- redacted summary。
- normalized aggregate dataset。
- sanitized dashboard dataset。
- trace id / candidate id / evidence ref 等の参照 ID。
- 実データ由来の aggregate metrics。
- `user.id` / `user.email` を含む分類属性。ただし共有・公開前に access control を確認すること。

## 保存してはいけないもの

Repository に保存してはいけないもの:

- raw prompt。
- raw response。
- system prompt の全文。
- tool arguments / tool results の全文。
- observed session 由来の source code fragment / file contents。
- credential、secret、token、API key、password。
- Base64 authorization header。
- sensitive bundle content。
- sensitive bundle local path。

## Local Runtime Paths

次の path は local runtime data として扱います。

- `data\`
- `tmp\`
- `artifacts/dashboard-input\*.json`
- raw OTLP payload file
- generated dashboard preview
- sensitive bundle output

`.gitignore` で多くは除外されていますが、commit 前に `git status --short` を確認してください。

## Dashboard 公開前チェック

GitHub Pages や共有場所に publish する前に、以下を確認します。

- repository / Pages access control。
- retention と snapshot 削除方法。
- masking / redaction 方針。
- `user.id` と `user.email` 表示の可否。
- 利用者周知。
- `dashboard-data.json` に raw content、credential、sensitive bundle path が含まれていないこと。

## 実データが混入した場合

1. 対象 raw payload を削除する。
2. 生成済み raw store、一時 CSV / JSON、dashboard artifact を削除する。
3. Langfuse に保存された対象 trace を削除する。
4. commit してしまった場合は、公開範囲と secret rotation の必要性を確認する。

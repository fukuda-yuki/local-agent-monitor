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

## Dashboard 共有前チェック

生成済み dashboard artifact を共有場所に置く前に、以下を確認します。

- 共有先の access control。
- retention と削除方法。
- masking / redaction 方針。
- `user.id` と `user.email` 表示の可否。
- 利用者周知。
- `dashboard-data.json` に raw content、credential、sensitive bundle path が含まれていないこと。

## Sanitized evidence を共有する

共有用 bundle の request で指定できるのは、作成時刻、選択条件、補助的な
forbidden marker だけです。snapshot、record、canonical bytes を request に
含めることはできません。payload は Local Monitor / producer 側の信頼済み
snapshot provider から取得され、#58 repository metadata projection、#59
instruction finding handoff、#80 sanitized alert receipt の frozen contract を
検証します。任意 JSON、free text、CSV、HTML、未実装の #73/#74/#84 payload
は指定できません。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export preview --request <request.json>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export export --request <request.json> --output <bundle.zip>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export result --bundle <bundle.zip>
```

現時点の production composition には #58/#59/#80 owner/store provider がまだ
接続されていないため、`preview` と `export` は
`snapshot_provider_unavailable` で fail-closed します。provider の統合が完了する
まで、実データ bundle の生成成功を想定しないでください。`result` は既存の
synthetic bundle の構造検証に使用できます。

`result` の成功は frozen v1 producer contract、entry identity / inventory、
checksum、generic scanner の再検証を示します。request で指定した
forbidden marker は生成時だけの補助検査で bundle に保存されないため、
`result` はその marker 検査を再実行したとは主張しません。これらは
source/store provenance を証明せず、
共有先の access control、retention、削除方法、
利用者周知を代替しません。失敗した bundle や `.partial` file を共有せず、
raw prompt、tool body、credential、PII、local path を request に含めないでください。

## 実データが混入した場合

1. 対象 raw payload を削除する。
2. 生成済み raw store、一時 CSV / JSON、dashboard artifact を削除する。
3. Langfuse に保存された対象 trace を削除する。
4. commit してしまった場合は、公開範囲と secret rotation の必要性を確認する。

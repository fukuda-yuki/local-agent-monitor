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

共有用 bundle の request で指定できるのは、schema version、作成時刻、選択
条件だけです。snapshot、record、canonical bytes、安全性 marker、出力先を
request に含めることはできません。`preview` と `export` は指定した既存
Local Monitor database から 1 回の read-only SQLite snapshot を取得し、#58
repository metadata projection、#59 instruction finding handoff、#80 sanitized
alert receipt の frozen contract を検証します。任意 JSON、free text、CSV、
HTML、#72/#73/#74/#83/#84 payload は v1 bundle に含められません。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export preview --database <monitor.db> --request <request.json>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export export --database <monitor.db> --request <request.json> --output <bundle.zip>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export result --bundle <bundle.zip>
```

`preview` は選択結果と capability を確認し、`export` は全検査に成功した場合
だけ同じ directory の一時 file から bundle を atomic に公開します。`result`
は既存 bundle の frozen v1 contract、entry identity / inventory、checksum、
fail-closed scanner を再検証します。この成功は archive の構造整合性を示す
だけで、source/store provenance、署名、作成者の権限を証明しません。

共有先の access control、retention、削除方法、利用者周知は別途必要です。
失敗した bundle や `.partial` file を共有せず、raw prompt、tool body、
credential、PII、local path を request や共有 artifact に含めないでください。

## Raw local replay は共有しない

`raw-local-replay` bundle は sanitized evidence ではありません。prompt、response、
tool data、personal data、secret を含み得るため、repository、Issue、PR、CI
artifact、static dashboard、共有 storage に保存しないでください。secret scan の
成功は bundle が安全であることを意味しません。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- raw-replay preview --database <monitor.db> --request <request.json>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- raw-replay export --database <monitor.db> --request <request.json> --output <raw-local-replay.zip>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- raw-replay result --bundle <raw-local-replay.zip>
```

export と Local Monitor replay には、preview digest、raw-data warning、固定確認文が
必要です。`--sanitized-only` では raw replay surface 全体が無効になります。Local
Monitor が replay 用に作る隔離 copy は capture からちょうど 7 日で既存 Retention
cleanup の対象になりますが、CLI が出力した caller-owned ZIP は自動削除されません。
不要になった ZIP と request file は利用者が明示的に削除してください。Local
Monitor の cleanup は retained child だけを削除し、その親 directory、siblings、
caller-owned ZIP を削除しません。

## Sanitized evidence を取り込む

取り込み前に `sanitized-import preview` または Local Monitor の
`/sanitized-import` で、新規・重複・競合、未解決依存関係、保存件数を確認します。
確定には preview が返した digest を使います。選択ファイルや現在の取り込み状態が
変わった場合は確定せず、再度 preview を取得してください。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import preview --database <monitor.db> --bundle <bundle.zip>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import import --database <monitor.db> --bundle <bundle.zip> --preview-digest <preview_digest>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import history --database <monitor.db> --limit 20
```

取り込みは #58 repository metadata projection、#59 instruction finding handoff、
#80 alert receipt の canonical sanitized bytes と graph / provenance だけを専用
`sanitized_import_*` table に保存します。raw telemetry、prompt / response、tool body、
Session、owner alert table、alert lifecycle、backup を復元せず、raw retention item も
作りません。取り込んだ sanitized record は source raw data の削除対象ではありません。

厳密な archive / checksum / scanner 検証に成功しても、bundle の作成者、署名、
権限、source store provenance は証明されません。信頼できる経路で受領した bundle
だけを選び、競合時に既存 record を上書きしないでください。

## 実データが混入した場合

1. 対象 raw payload を削除する。
2. 生成済み raw store、一時 CSV / JSON、dashboard artifact を削除する。
3. Langfuse に保存された対象 trace を削除する。
4. commit してしまった場合は、公開範囲と secret rotation の必要性を確認する。

# Raw Data Loop

Raw data loop は、saved raw OTLP JSON を SQLite raw store に取り込み、normalized measurement dataset を生成する流れです。
Langfuse UI が起動していなくても、file-based input だけで再現できます。
`raw-local-receiver` profile の実装後は、この repository の local receiver が受け取った telemetry も同じ raw data loop に接続します。

## 入力

入力は saved raw OTLP JSON です。
実 prompt、response、tool arguments / results、identity-bearing attributes が含まれ得るため、repository に commit しないでください。

Synthetic fixture:

```text
tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json
```

## Ingest

```powershell
New-Item -ItemType Directory -Force data | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --db data\raw-store.db
```

`data\raw-store.db` は local runtime data です。
commit しません。

## Normalize

```powershell
New-Item -ItemType Directory -Force tmp\raw-loop | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --csv tmp\raw-loop\measurements.csv --json tmp\raw-loop\measurements.json
```

Normalized measurement は dashboard dataset と candidate pipeline の主入力になります。

## 削除

不要になった local output を削除する例:

```powershell
Remove-Item data\raw-store.db -ErrorAction SilentlyContinue
Remove-Item tmp\raw-loop -Recurse -Force -ErrorAction SilentlyContinue
```

実データや秘密情報が混入した raw payload を扱ってしまった場合は、raw payload、raw store、一時 CSV / JSON output、必要に応じて Langfuse 側 trace も削除してください。

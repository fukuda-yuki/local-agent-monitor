# Getting Started

スターティングガイド。
詳しい利用者向け説明は [docs/user-guide.md](user-guide.md) を参照する。

## 1. Synthetic Dashboard を生成する

外部サービスや実データなしで、静的 dashboard の生成経路を確認できる。
これは `raw-only` profile の最小確認にも使える。

```powershell
New-Item -ItemType Directory -Force tmp\dashboard-demo | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site
```

生成物:

- `tmp\dashboard-demo\measurements.json`
- `tmp\dashboard-demo\dashboard.json`
- `tmp\dashboard-demo\site\index.html`
- `tmp\dashboard-demo\site\dashboard-data.json`

`tmp\` は local runtime output であり、commit しない。

## 2. Local Ingestion Monitor を使う（Langfuse 不要）

Docker Desktop や外部サービスなしで VS Code Copilot Chat / GitHub Copilot CLI の
テレメトリをリアルタイムに確認できます。

**ターミナル A — モニター起動：**

```powershell
New-Item -ItemType Directory -Force data | Out-Null
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor.db --url http://127.0.0.1:4320
```

ブラウザで `http://127.0.0.1:4320/` を開く。

**ターミナル B — VS Code 用環境変数を生成して適用し、VS Code を起動：**

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
# 出力結果を貼り付けて実行してから：
code .
```

VS Code で Copilot Chat に質問すると、ブラウザの `/ingestions` と `/traces` に受信結果が表示されます。

詳細は [Local Ingestion Monitor ユーザーガイド](user-guide/local-monitor.md) を参照する。

## 3. Live Trace Review を試す（Langfuse）

Live trace を確認する場合は `docker-desktop-langfuse` profile を使い、Langfuse self-host をローカルで起動して client を OTLP HTTP で Langfuse に送る。

代表コマンド:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-vscode-env
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-copilot-cli-env
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-codex-app-config
```

Langfuse key、Base64 authorization header、secret は repository に保存しない。
検証には synthetic data または公開してよい検証用 data だけを使う。

## 4. Collection Profile を選ぶ

Collection profile は telemetry routing mode を表す。

```powershell
$env:CAO_COLLECTION_PROFILE="raw-only"
```

最小 profile は `raw-only`、標準 full profile は `docker-desktop-langfuse`。
詳細は [collection profile specification](specifications/interfaces/collection-profiles.md) を参照する。

WARNING: `remote-managed-langfuse` と `remote-managed-collector` は、送信前に access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を決める必要がある。この repository は remote / shared endpoint の利用者同意 workflow を実装しない。

## 5. Raw Data Loop を試す

saved raw OTLP JSON がある場合は SQLite raw store に取り込み、normalized measurement dataset を生成する。

```powershell
New-Item -ItemType Directory -Force data,tmp\raw-loop | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --db data\raw-store.db
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --csv tmp\raw-loop\measurements.csv --json tmp\raw-loop\measurements.json
```

`data\raw-store.db`、raw payload、一時 CSV / JSON は local runtime data として扱い、commit しない。

## 6. Diagnosis / Improvement Support を試す

Synthetic diagnosis input から improvement proposal、evaluation、human decision template を生成する。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- validate-diagnoses tests\CopilotAgentObservability.ConfigCli.Tests\TestData\m5-diagnoses.synthetic.json --json tmp\raw-loop\validated-diagnoses.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-improvement-proposals tmp\raw-loop\validated-diagnoses.json --json tmp\raw-loop\proposals.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- evaluate-improvement-proposals tmp\raw-loop\proposals.json --json tmp\raw-loop\evaluations.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-decision-template tmp\raw-loop\evaluations.json --json tmp\raw-loop\decision-template.json
```

この loop は repository を自動修正しない。patch / diff / commit / push / pull request は生成しない。

## 7. 次に読むもの

- [利用者向け詳細ガイド](user-guide.md)
- [要件定義](requirements.md)
- [技術仕様索引](spec.md)
- [実装仕様](specifications/README.md)
- [Contributor Guide](contributor-guide.md)

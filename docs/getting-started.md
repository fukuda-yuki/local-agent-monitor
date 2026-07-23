# Getting Started

スターティングガイド。
詳しい利用者向け説明は [docs/user-guide.md](user-guide.md) を参照する。

## 1. Local Ingestion Monitor を起動してリアルタイム観測を開始する（推奨）

Docker Desktop や外部サービスなしで VS Code Copilot Chat / GitHub Copilot CLI の
テレメトリをリアルタイムに収集・可視化できます。

**ステップ A — モニターを起動する：**

```powershell
New-Item -ItemType Directory -Force data | Out-Null
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor.db --url http://127.0.0.1:4320
```

ブラウザで `http://127.0.0.1:4320/` を開きます。

**ステップ B — 観測用の環境変数を適用して VS Code を起動する：**

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
# 表示された環境変数設定コマンドを実行したターミナルから起動します：
code .
```

**ステップ C — 初回トレース（First Trace）の受領を確認する：**

VS Code 上で Copilot Chat に質問を送信し、ブラウザの `http://127.0.0.1:4320/`（概要）および `/traces`（トレース一覧）に最初のトレースが表示されることを確認します。  
※ 設定スクリプト等の出力で `success: true` が表示されても、それは静的な設定完了を意味します。実際に画面へトレースが反映されて初めてセットアップ完了となります。

詳細は [Local Ingestion Monitor ユーザーガイド](user-guide/local-monitor.md) を参照してください。

## 2. クイック体験：デモ用合成データで静的ダッシュボードを試す

実環境の Copilot や外部サービスを使わず、静的ダッシュボードの表示やデータ変換パイプラインの動作のみを試したい場合のクイック体験手順です（※実際のテレメトリ収集環境の構築ではありません）。

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

`tmp\` はローカルの試行用出力であるため、コミットには含めません。

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
- [トラブルシューティングガイド](user-guide/troubleshooting.md)
- [要件定義](requirements.md)
- [技術仕様索引](spec.md)
- [実装仕様](specifications/README.md)
- [Contributor Guide](contributor-guide.md)

# はじめかた

Copilot Agent Observability のクイックスタートガイドです。  
より詳しい説明は [ユーザーガイド (user-guide.md)](user-guide.md) を参照してください。

## 1. Local Ingestion Monitor を起動してリアルタイム観測を開始する（推奨）

Docker Desktop や外部サービスを使わずに、VS Code Copilot Chat や GitHub Copilot CLI の
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

## 2. デモデータでダッシュボードを試す（環境構築なし）

Copilot の実行や外部サービスは不要です。サンプルデータでダッシュボード画面の生成だけを試せます。  
※ この手順はテレメトリ収集の環境構築ではありません。

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

## 3. Langfuse でトレースを確認する

リアルタイムトレースを Langfuse で確認する場合は `docker-desktop-langfuse` プロファイルを使用し、ローカルで起動した Langfuse へ OTLP HTTP で送信します。

代表コマンド:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-vscode-env
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-copilot-cli-env
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-codex-app-config
```

※ Langfuse の API キーや認証ヘッダー、シークレット情報はリポジトリへコミットしないでください。

## 4. テレメトリの送信先を選ぶ

環境変数 `CAO_COLLECTION_PROFILE` で、テレメトリの送り先を指定します。

```powershell
$env:CAO_COLLECTION_PROFILE="raw-local-receiver"
```

最小限の構成は `raw-only`、すべての機能を使う構成は `docker-desktop-langfuse` です。  
詳細は [コレクションプロファイル仕様書](specifications/interfaces/collection-profiles.md) を参照してください。

> [!CAUTION]
> `remote-managed-langfuse` および `remote-managed-collector` はリモートサーバーへのデータ送信となります。社内ポリシーや同意設定を確認のうえ利用してください。

## 5. 保存データを集計する

保存済みのテレメトリデータ（raw OTLP JSON）がある場合は、集計用データセットに変換できます。

```powershell
New-Item -ItemType Directory -Force data,tmp\raw-loop | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --db data\raw-store.db
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --csv tmp\raw-loop\measurements.csv --json tmp\raw-loop\measurements.json
```

`data\raw-store.db` および一時出力ファイルはローカル実行用データのため、コミット対象外です。

## 6. 自動診断と改善提案を試す

サンプルデータから、失敗傾向の診断レポートや改善提案を自動生成します。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- validate-diagnoses tests\CopilotAgentObservability.ConfigCli.Tests\TestData\m5-diagnoses.synthetic.json --json tmp\raw-loop\validated-diagnoses.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-improvement-proposals tmp\raw-loop\validated-diagnoses.json --json tmp\raw-loop\proposals.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- evaluate-improvement-proposals tmp\raw-loop\proposals.json --json tmp\raw-loop\evaluations.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-decision-template tmp\raw-loop\evaluations.json --json tmp\raw-loop\decision-template.json
```

※ この分析ループはリポジトリのコードやファイルを自動修正・コミットしません。

## 7. 次のステップ

- [ユーザーガイド](user-guide.md)
- [トラブルシューティングガイド](user-guide/troubleshooting.md)
- [要件定義](requirements.md)
- [技術仕様索引](spec.md)
- [実装仕様](specifications/README.md)
- [Contributor Guide](contributor-guide.md)

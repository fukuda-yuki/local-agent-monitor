# Copilot Agent Observability

GitHub Copilot Chat、Copilot CLI、Codex App が出力する OpenTelemetry データを収集し、  
**エージェントが何をしたかを trace・集計・診断の三層で確認できる、Local-first な観測基盤**です。

---

## 何ができるようになるか

### Copilot が「何をしたか」をトレース単位で見る

Copilot は内部で多くのステップを踏んでいます。LLM を何回呼んだか、どのツールを呼んだか、何秒かかったか、どこでエラーが起きたか——通常これらはブラックボックスです。

このプロダクトを使うと、Copilot が出力する OpenTelemetry データをローカルに収集し、  
**ひとつひとつの実行ステップを span ツリーとして可視化**できるようになります。

- VS Code Copilot Chat のチャット実行、Copilot CLI のコマンド実行を計装なしで観測
- ツール呼び出しの階層（親子 span）・所要時間・引数と戻り値をその場で確認
- エラーが発生した span を即座に特定し、どのステップで失敗したかを調査
- 入力プロンプトでもセッションを識別できるため、「あのチャット実行」をすぐに見つけられる

### 傾向と改善候補を集計・俯瞰する

個別調査だけでなく、蓄積したトレースデータから傾向を把握できます。

- エラー率・実行時間・トークン使用量を集計した **Static Dashboard** を生成
- 失敗傾向や長時間実行をヒューリスティックで検出した **診断候補** を一覧表示
- baseline / variant / experiment ごとにデータセットを比較
- GitHub Pages へ snapshot として保存し、レビュワーと共有

### プロンプトや skill の改善サイクルを回す

「このプロンプトの変更でエラーが減ったか」「このツール呼び出しは本当に必要だったか」を  
再現可能なデータパイプラインで確認できます。

- saved raw OTLP JSON → SQLite raw store → normalized dataset → dashboard dataset の一貫した変換
- deterministic CLI による diagnosis / improvement / decision candidate 生成
- すべてのステップがコマンド一発で再実行可能

---

## Local Ingestion Monitor

VS Code Copilot Chat から直接テレメトリを受信し、ローカル DB に蓄積してブラウザで確認できる  
**ローカルのみで動く観測 UI** です（`http://127.0.0.1:4320`）。

### 概要ダッシュボード

取り込み全体の要約指標と最新セッション一覧を 1 画面で確認します。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor Overview" src="./docs/assets/screenshots/local-monitor-dashboard.png">
</p>

トレース総数・エラー数・エラー率・プロンプト/レスポンス文字数・最終取り込み日時を即時把握。  
下部の「最新の取り込み」から直近セッションを選択して詳細へジャンプできます。

### トレース一覧

保存されたすべての実行トレースを一覧表示し、調査対象を絞り込みます。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor Traces" src="./docs/assets/screenshots/local-monitor-traces.png">
</p>

開始時刻・クライアント・モデル・実行時間・ステータスを一覧表示。  
失敗トレースや異常なレイテンシを持つトレースを素早く見つけられます。  
入力プロンプトの冒頭テキストでもセッションを識別できます。

### Span Tree（トレース詳細）

エージェントが内部で踏んだステップの全階層を調査します。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor Span Tree" src="./docs/assets/screenshots/local-monitor-spantree.png">
</p>

- **Span Tree / Flow Chart**: ルートスパンから子スパンまでインデントツリーとウォーターフォールバーで表示。`▶` / `▼` で任意の階層を折りたたみ
- **プロンプト / レスポンス**: イベントや属性から抽出したユーザープロンプトとアシスタント応答を確認
- **Cache Explorer**: トレース内のキャッシュヒット率とトークン内訳を表示
- **生 JSON**: 属性・イベント全体の raw JSON をインスペクト

「なぜ意図しない回答をしたか」「どのツールがボトルネックか」「引数は正しく渡ったか」を根本原因レベルで調べられます。

### 診断候補

トレースデータから失敗傾向と潜在的な問題候補を自動抽出します。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor Diagnostics" src="./docs/assets/screenshots/local-monitor-diagnostics.png">
</p>

すべてのトレースを目視確認しなくても、エラー頻度や長時間実行のパターンをヒューリスティックで検出して一覧化します。

---

## 必要なもの

### 最低動作条件（raw-only モード）

Docker も Langfuse も不要。保存済みの raw OTLP JSON さえあれば、データパイプライン全体を試せます。

| 必要なもの | 用途 |
| --- | --- |
| .NET SDK | Config CLI のビルドと実行 |
| PowerShell | Windows 向けコマンド例の実行 |

合成データだけで dashboard まで試す:

```powershell
New-Item -ItemType Directory -Force tmp\dashboard-demo | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site
```

### ライブトレース収集（推奨）

Copilot の実行をリアルタイムで収集するには、テレメトリの送信先が必要です。

| 追加で必要なもの | 用途 |
| --- | --- |
| GitHub Copilot が使えるアカウント | Copilot Chat / CLI の実行 |
| VS Code + GitHub Copilot Chat 拡張 | VS Code 側テレメトリの発生源 |
| GitHub Copilot CLI | CLI 側テレメトリの発生源 |
| Docker Desktop または WSL2 Docker Engine | Langfuse をローカルで起動する場合 |

送信先は **collection profile**（環境変数 `CAO_COLLECTION_PROFILE`）で選択します。

| Profile | 用途 |
| --- | --- |
| `raw-local-receiver` | Docker 不要。このリポジトリの Local Ingestion Monitor へ直接送信 |
| `docker-desktop-langfuse` | Docker Desktop 上の Langfuse へ送信（標準フル構成） |
| `docker-desktop-collector-langfuse` | Collector 経由で Langfuse へ relay |
| `wsl2-docker-langfuse` | WSL2 Docker Engine 上の Langfuse へ送信 |
| `wsl2-docker-collector-langfuse` | WSL2 Docker Engine 上の Collector 経由で Langfuse へ relay |
| `remote-managed-langfuse` | 管理された remote Langfuse endpoint へ送信 |
| `remote-managed-collector` | 管理された remote Collector endpoint へ送信 |

### 任意

| 追加で必要なもの | 用途 |
| --- | --- |
| Codex App / app-server | Codex App のテレメトリも収集したい場合 |

---

## クイックスタート（Docker Desktop + Langfuse）

1. Docker Desktop を起動し、Langfuse self-host をローカルで起動する
2. Langfuse でプロジェクトと API key を作成する
3. Config CLI で VS Code / Copilot CLI 向けの OTel 設定を出力する
4. VS Code Copilot Chat または Copilot CLI を OTel 設定付きで起動する
5. 検証用または合成データのみで Copilot を実行する
6. Langfuse UI でトレースを確認する
7. saved raw OTLP JSON がある場合は raw data loop と static dashboard を生成する

```powershell
# VS Code / CLI 向け設定を出力
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile docker-desktop-langfuse
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-copilot-cli-env --profile docker-desktop-langfuse

# raw OTLP JSON を取り込んで dashboard まで生成
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw <raw.json> --db data\raw-store.db
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --json tmp\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\measurements.json --json tmp\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard.json --out-dir tmp\site
```

---

## データ安全境界

> [!WARNING]
> remote managed Langfuse / Collector endpoint、共有環境、実データ公開、GitHub Pages 公開、社内サーバー運用を行う場合は、送信前に access control・retention・削除方法・masking/redaction・利用者周知または同意・identity handling・credential handling を先に決めてください。このリポジトリは remote / shared endpoint の利用者同意ワークフローを実装しません。

**リポジトリに保存してよいもの:** 合成 fixture・要約・正規化集計データ・sanitized dashboard データセット・参照 ID（trace id / candidate id など）・実データ由来の集計メトリクス

**リポジトリに保存してはいけないもの:** raw プロンプト・raw レスポンス・system prompt 全文・tool 引数/戻り値の全文・observed session 由来のソースコード断片・credential・secret・token・API key・Base64 authorization header

詳細は[データ安全境界仕様](docs/specifications/security-data-boundaries.md)を参照してください。

---

## ドキュメント

| ドキュメント | 内容 |
| --- | --- |
| [利用者向け詳細ガイド](docs/user-guide.md) | セットアップから各機能の使い方まで |
| [要件定義](docs/requirements.md) | 製品要件の定義 |
| [技術仕様索引](docs/spec.md) | 実装仕様へのインデックス |
| [実装仕様](docs/specifications/README.md) | 各コンポーネントの詳細仕様 |
| [Architecture](docs/architecture.md) | コンポーネント構成と設計方針 |
| [Decisions](docs/decisions.md) | 設計判断の記録 |
| [Contributor Guide](docs/contributor-guide.md) | 開発・テスト手順 |
| [Roadmap / History](docs/task.md) | ロードマップと履歴 |

---

## 開発者向け検証

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

`dotnet test` には LocalMonitor の Playwright smoke test が含まれます。`dotnet build` 後に Playwright install を実行してください（スクリプトはビルド後に生成されます）。Linux CI では `install-playwright-chromium.ps1 -WithDeps` を使用します。

Collector example の構文確認（実 credential は不要）:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

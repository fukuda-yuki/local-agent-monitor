# Copilot Agent Observability

GitHub Copilot Chat・Copilot CLI の OpenTelemetry テレメトリを収集し、
**エージェントの動作をトレース・集計・診断の 3 つの視点で確認できる、ローカル完結型の観測ツール**です。

---

## できること

### トレース単位で Copilot の動きを見る

Copilot は内部で多くのステップを踏んでいます。LLM の呼び出し回数、使用したツール、所要時間、エラーの発生箇所——ふつうは見えません。

このツールを導入すると、Copilot の OpenTelemetry データをローカルに収集し、  
**実行ステップを span ツリーで可視化**できます。

- VS Code Copilot Chat のチャット実行、Copilot CLI のコマンド実行を計装なしで観測
- ツール呼び出しの階層（親子 span）・所要時間・引数と戻り値をその場で確認
- エラーが発生した span を即座に特定し、どのステップで失敗したかを調査
- 入力プロンプトでもセッションを識別できるため、「あのチャット実行」をすぐに見つけられる

### 傾向をつかみ、改善のヒントを得る

個別調査だけでなく、蓄積したトレースデータから傾向を把握できます。

- エラー率・実行時間・トークン使用量をまとめた **Static Dashboard** を生成
- 失敗傾向や長時間実行をヒューリスティックで検出した **診断候補** を一覧表示
- baseline / variant / experiment ごとにデータセットを比較
- GitHub Pages へ snapshot として保存し、レビュワーと共有

### プロンプトやスキルを継続的に改善する

「このプロンプトの変更でエラーが減ったか」「このツール呼び出しは本当に必要だったか」を  
再現可能なデータパイプラインで確認できます。

- 保存済み OTLP JSON → SQLite → 集計データセット → ダッシュボードデータの一貫した変換
- CLI による診断・改善候補・判断テンプレートの生成
- すべてのステップがコマンド一発で再実行可能

---

## Local Ingestion Monitor

VS Code Copilot Chat からテレメトリを直接受信し、ローカル DB に蓄積してブラウザで確認する  
**観測 UI** です（`http://127.0.0.1:4320`）。外部サーバーは不要です。

### リポジトリからセッションを調べる

`/` でリポジトリを選び、Session Explorer でセッションを探し、行を開いて詳細を確認します。
「すべてのセッション」と「リポジトリ未設定のセッション」からも調査できます。
ヘッダーのパンくずで戻り、受信状態と「設定」から管理操作を開きます。

セッション詳細では「トークン合計」（入力 / 出力）と「入力トークンの内訳」
（キャッシュから読み込み / 新規入力 / キャッシュ書き込み）を確認し、階層タイムラインから
ツール、スキル、サブエージェントの記録へ進めます。記録されていない値は 0 と扱いません。

### AI を使わずに比較する

リポジトリの Session Explorer で「比較を作成」を押し、「基準」と「比較対象」を明示的に選びます。
「比較を確認」で対象を確認してから作成すると、固定指標のセッションごとの中央値・範囲・
利用可能件数と、その根拠を調べられます。比較の計算に LLM や認証は必要ありません。

GitHub Copilot SDK による AI 分析は任意です。「設定」の「AI設定」で利用準備を確認し、
明示的に実行した場合に選択した内容が GitHub Copilot に送信されます。
AI の未設定や失敗で、セッション調査や比較が使えなくなることはありません。

### 設定とデータ管理

「設定」には状態、受信、AI設定、リポジトリ、アーカイブ、保存・バックアップ、診断をまとめています。
リポジトリとセッションのアーカイブは元に戻せる非表示操作で、削除や保存期間の延長ではありません。
`--sanitized-only` で起動すると受信・health・対応する machine API 専用となり、人向け UI は提供しません。

操作手順と技術的なトレース詳細・履歴インポート等の導線は
[Local Monitor ユーザーガイド](docs/user-guide/local-monitor.md) を参照してください。

---

## 準備するもの

### テレメトリのリアルタイム収集（推奨）

Copilot の実行をリアルタイムで収集・観測するには、テレメトリの送信先が必要です。

| 必要なもの | 用途 |
| --- | --- |
| .NET SDK | Local Monitor / Config CLI のビルドと実行 |
| PowerShell | Windows 向けセットアップスクリプトの実行 |
| GitHub Copilot が使えるアカウント | Copilot Chat / CLI の実行 |
| VS Code + GitHub Copilot Chat 拡張 | VS Code 側テレメトリの発生源 |
| GitHub Copilot CLI | CLI 側テレメトリの発生源 |
| Docker Desktop または WSL2 Docker Engine | Langfuse をローカルで起動する場合 |

テレメトリの送信先は、環境変数 `CAO_COLLECTION_PROFILE` で切り替えます。

| Profile | 用途 |
| --- | --- |
| `raw-local-receiver` | Docker 不要。Local Monitor へ直接送信（おすすめ） |
| `docker-desktop-langfuse` | Docker Desktop の Langfuse へ送信 |
| `docker-desktop-collector-langfuse` | Collector を経由して Langfuse へ送信 |
| `wsl2-docker-langfuse` | WSL2 の Docker 上で Langfuse へ送信 |
| `wsl2-docker-collector-langfuse` | WSL2 の Docker 上で Collector 経由で Langfuse へ送信 |
| `remote-managed-langfuse` | リモートの Langfuse サーバーへ送信 |
| `remote-managed-collector` | リモートの Collector サーバーへ送信 |

### デモデータで画面だけ試す

Docker、Langfuse、Copilot の実行は不要です。あらかじめ用意されたサンプルデータを使い、ダッシュボード生成の流れだけを試せます。

```powershell
New-Item -ItemType Directory -Force tmp\dashboard-demo | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site
```

> [!WARNING]
> Codex App Desktop integration は Issue #92 で `NO-GO` です。互換性維持の
> legacy sample generator は対応済み surface を意味せず、log-export profile
> は repository-safe な既定として使用できません。

---

## セットアップ手順（GitHub Copilot）

Config CLI によるセットアップでは、まず変更内容の確認（plan）を行い、表示された ID を指定して反映（apply）します。意図しない変更を防ぐ仕組みです。

```powershell
pwsh -ExecutionPolicy Bypass scripts\local-monitor\setup.ps1 plan --adapter github-copilot --target all
pwsh -ExecutionPolicy Bypass scripts\local-monitor\setup.ps1 apply --change-set <change-set-id>
pwsh -ExecutionPolicy Bypass scripts\local-monitor\setup.ps1 status --adapter github-copilot
pwsh -ExecutionPolicy Bypass scripts\local-monitor\setup.ps1 rollback --change-set <change-set-id>
```

> [!TIP]
> Windows 環境で PowerShell スクリプトの実行がブロックされる場合は、上記のように `-ExecutionPolicy Bypass` を付与して実行してください。詳細は [トラブルシューティングガイド](docs/user-guide/troubleshooting.md) を参照してください。

Windows x64 Release ZIP をお使いの場合は `.\scripts\setup.ps1` に同じ引数を渡します。ZIP に実行ファイルが含まれているため、.NET SDK のインストールは不要です。各コマンドは stdout に 1 個の `setup.v1` JSON を返します。

> [!IMPORTANT]
> スクリプト実行時の **`success: true` は設定ファイルの静的検証（生成・書き込み）が成功したことを意味します**。実際のテレメトリ受信完了を示すものではありません。
> 設定適用後、VS Code で Copilot Chat を実行し、Local Monitor 画面（`http://127.0.0.1:4320`）からセッションを開き、実行内容が反映されたことをもって環境構築完了と判定してください。

詳しい対象範囲とロールバック条件は [Local Ingestion Monitor ガイド](docs/user-guide/local-monitor.md) を参照してください。

---

## Docker Desktop + Langfuse を使うセットアップ

1. Docker Desktop を起動し、Langfuse self-host をローカルで起動します。
2. Langfuse 上でプロジェクトを作成し、API キーを発行します。
3. Config CLI で VS Code / Copilot CLI 向けの OTel 設定を出力します。
4. VS Code Copilot Chat または Copilot CLI を OTel 設定付きで起動します。
5. 検証用または合成データのみで Copilot を実行します。
6. Langfuse UI でリアルタイムにトレースを確認します。
7. 保存済みの OTLP JSON がある場合は、データ集計と Static Dashboard を生成します。

```powershell
# VS Code / CLI 向け設定を出力
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile docker-desktop-langfuse
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-copilot-cli-env --profile docker-desktop-langfuse

# OTLP JSON を取り込んでダッシュボードまで生成
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw <raw.json> --db data\raw-store.db
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --json tmp\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\measurements.json --json tmp\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard.json --out-dir tmp\site
```

---

## データの取り扱いルール

> [!WARNING]
> リモートの Langfuse / Collector サーバー、共有環境、実データ公開、GitHub Pages 公開、社内サーバー運用を行う場合は、送信前にアクセス制御・保持期限・削除方法・マスキング・利用者への周知または同意・認証情報の取り扱いを先に決めてください。このリポジトリはリモート / 共有環境の利用者同意ワークフローを実装しません。

**リポジトリに保存してよいもの:** 合成テストデータ・要約・正規化集計データ・ダッシュボード用データセット（サニタイズ済み）・参照 ID（trace id / candidate id など）・実データ由来の集計メトリクス

**リポジトリに保存してはいけないもの:** 生のプロンプト・生のレスポンス・システムプロンプト全文・ツールの引数/戻り値の全文・観測セッション由来のソースコード断片・認証情報（credential・secret・token・API key・Base64 ヘッダーなど）

詳細は[データ安全境界仕様](docs/specifications/security-data-boundaries.md)を参照してください。

---

## ドキュメント

| ドキュメント | 内容 |
| --- | --- |
| [ユーザーガイド](docs/user-guide.md) | セットアップから各機能の使い方まで |
| [トラブルシューティング](docs/user-guide/troubleshooting.md) | PowerShell 実行権限・ポート競合・環境変数等のトラブル対応 |
| [要件定義](docs/requirements.md) | 製品要件の定義 |
| [技術仕様索引](docs/spec.md) | 実装仕様へのインデックス |
| [実装仕様](docs/specifications/README.md) | 各コンポーネントの詳細仕様 |
| [Architecture](docs/architecture.md) | コンポーネント構成と設計方針 |
| [Decisions](docs/decisions.md) | 設計判断の記録 |
| [Contributor Guide](docs/contributor-guide.md) | 開発・テスト手順 |

---

## 開発者向け：ビルドとテスト

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

`dotnet test` には LocalMonitor の Playwright smoke test が含まれます。`dotnet build` 後に Playwright install を実行してください（スクリプトはビルド後に生成されます）。Linux CI では `install-playwright-chromium.ps1 -WithDeps` を使用します。

Collector example の構文確認（実際の認証情報は不要）:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

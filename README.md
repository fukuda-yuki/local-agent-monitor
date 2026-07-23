# Copilot Agent Observability

GitHub Copilot Chat・Copilot CLI・Codex App の OpenTelemetry テレメトリを収集し、  
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

> [!NOTE]
> 画面キャプションに掲載されているスクリーンショット画像は表示例（デモデータセット）です。実際の観測時は、画面右上等の受信ステータスバッジ（`受信中` / `未接続`）でリアルタイム接続状態を確認できます。

### 概要ダッシュボード

トークンコストの把握を最優先にした KPI ダッシュボードです。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor 概要" src="./docs/assets/screenshots/local-monitor-overview.png">
</p>

今日 / 7日 / 30日のトークン合計・実効入力換算・キャッシュ読取率・エラー trace 数を即時把握。  
モデル別トークン内訳とキャッシュ効率、高コスト trace TOP5、時間帯別分布、最近のトレースから
気になる trace へ直接ジャンプできます。

### トレース一覧（master-detail）

保存されたすべての実行トレースをテーブル + 右プレビューで絞り込みます。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor トレース一覧" src="./docs/assets/screenshots/local-monitor-trace-list.png">
</p>

プロンプト・モデル・状態（正常 / 回復済みエラー / 異常終了）・期間でフィルタし、
トークン・所要・時刻でソート。行を選ぶとページ遷移なしで右パネルにミニ KPI・
トークン構成・高コストスパン TOP3 が表示されます。各トレースは入力プロンプトで識別できます（既定）。

### トレース詳細（フロー / waterfall + キャッシュ列）

エージェントの実行の流れをターン単位で調査します。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor トレース詳細（フロー）" src="./docs/assets/screenshots/local-monitor-trace-detail-flow.png">
</p>

<p align="center">
  <img width="900" alt="Local Ingestion Monitor トレース詳細（waterfall）" src="./docs/assets/screenshots/local-monitor-trace-detail-waterfall.png">
</p>

- **フロー / waterfall 切替**: ターンカードの時系列表示と時間軸バー表示をワンクリックで切替。並行ツール呼出は「⑂ 並行 N 件」、失敗 → 再試行は回復ペアとして表現
- **常設キャッシュ列**: 読取率・実効入力換算・ターン別キャッシュ読取率を常時表示
- **スパンインスペクタ**: スパンをクリックすると整形（メッセージ構成・トークン内訳）と raw（OTLP span JSON 全文）を右パネルで確認
- **エラー解析モード**: エラーを含む trace ではエラー要約・エラー一覧（回復済み / 未回復）・入力トークン推移（128K 目安線）に自動で切替

「なぜ意図しない回答をしたか」「どのツールがボトルネックか」「キャッシュは効いているか」を根本原因レベルで調べられます。

### Copilot 解析ドロワー

captured raw trace をローカルの GitHub Copilot SDK で解析します。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor Copilot 解析ドロワー" src="./docs/assets/screenshots/local-monitor-copilot-drawer.png">
</p>

観点（トークン / キャッシュ / エラー / 遅延 / ツール利用 / エージェントの流れ）を選んで実行し、
所見にチャット形式で追い質問できます。raw データはローカルから出ません。

### 診断

テレメトリの受信状況を段階的に確認します。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor 診断" src="./docs/assets/screenshots/local-monitor-diagnostics.png">
</p>

サイドバーの受信ステータスバッジ → ポップオーバー → 詳細診断の順に、受信 / 書き込み /
Projection / 表示の 4 段の状態、readiness しきい値、取り込み履歴を確認できます。
診断ページの明示的なカードから `/historical-import` を開くと、選択した履歴 source の
metadata-only preview、confirmation、結果/履歴を確認できます。ページを開いただけで
source を検索・読み取ることはありません。現在の GitHub Copilot CLI / Claude Code profile は
format 未承認のため、content を読まず zero-candidate / import 不可を明示します。
詳細は [Local Monitor ユーザーガイド](docs/user-guide/local-monitor.md#履歴インポート) を参照してください。

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

### オプション（必要な場合のみ）

| 追加で必要なもの | 用途 |
| --- | --- |
| Codex App / app-server | Codex App のテレメトリも収集したい場合 |

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
> 設定適用後、VS Code で Copilot Chat を実行し、Local Monitor 画面（`http://127.0.0.1:4320`）に最初のトレース（First Trace）が表示されたことをもって環境構築完了と判定してください。

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
| [Roadmap / History](docs/task.md) | ロードマップと履歴 |

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

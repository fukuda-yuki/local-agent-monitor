# ユーザーガイド

目的に応じて各ガイドを参照してください。  
最短の体験手順のみを確認したい場合は [はじめかた](getting-started.md) を参照してください。

---

## 目次

### 1. はじめに・環境構築
- [データ安全ポリシー (Data Safety)](user-guide/data-safety.md)  
  ローカルデータの保護方針、収集データの範囲、外部に送らないデータのルールです。
- [テレメトリ収集と初期セットアップ (Telemetry Collection)](user-guide/telemetry-collection.md)  
  VS Code Copilot Chat・GitHub Copilot CLI などからテレメトリを収集する手順です。

### 2. 画面別操作マニュアル
- [Local Ingestion Monitor 利用ガイド](user-guide/local-monitor.md)  
  リアルタイムでトレースを監視するローカル UI（概要ダッシュボード、トレース一覧、フロー/Waterfall、スパンインスペクタ、エラー解析モード、Copilot解析ドロワー）の操作ガイドです。
- [Static Dashboard 利用ガイド](user-guide/static-dashboard.md)  
  蓄積データから生成された静的 HTML ダッシュボードの閲覧・フィルタリング方法を解説します。

### 3. タスク・ユースケース別ガイド
- [保存データの集計・変換](user-guide/raw-data-loop.md)  
  保存された OTLP データを取り込み、分析用データセットへ再現可能に集計・変換する手順です。
- [自動診断と改善提案](user-guide/diagnosis-improvement-loop.md)  
  失敗傾向やパフォーマンスのボトルネックを自動検出し、プロンプトやスキルの改善提案を生成・評価する手順です。

### 4. トラブルシューティング & 安全運用
- [トラブルシューティングガイド](user-guide/troubleshooting.md)  
  PowerShell スクリプトの実行権限エラー（ExecutionPolicy）、ポート4320衝突、VS Code環境変数引き継ぎ、企業プロキシ環境での解決方法をまとめています。

---

## やりたいことから探す

| タスク・目的 | 使う場面 | 主要コマンド / UI |
| --- | --- | --- |
| **1. Copilot の動きをリアルタイムに見る** | エージェントの挙動やトークンコストをその場で確認したい | Local Ingestion Monitor (`http://127.0.0.1:4320`) |
| **2. 1 件のチャット実行を深掘りする** | 1 件のチャット実行の全スパン・入出力を深掘りしたい | Local Monitor または Langfuse UI |
| **3. 蓄積したトレースをデータセットにする** | 蓄積されたトレースをデータセットへ変換したい | `ingest-raw`, `normalize-raw` |
| **4. 傾向をダッシュボードで共有する** | 複数セッションの傾向を一覧ダッシュボードで共有したい | `generate-static-dashboard` |
| **5. ボトルネックと改善提案を得る** | 失敗や遅延のボトルネックと改善提案を得たい | `generate-improvement-proposals` |
| **6. 機密を除いたトレースデータを安全に共有する** | サニタイズ済みトレースデータを安全に輸出入したい | `sanitized-export`, `sanitized-import`, Local Monitor `/sanitized-import` |

### 安全なトレースデータの共有と取り込み

機密情報を除去したトレースデータ（`.zip`）を出力・取り込む手順です。プロンプトや秘密情報を含まず、安全にやり取りできます。

**エビデンスデータの出力:**

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export preview --database data\local-monitor.db --request request.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export export --database data\local-monitor.db --request request.json --output bundle.zip
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export result --bundle bundle.zip
```

**受け取ったエビデンスデータの取り込み:**

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import preview --database data\local-monitor.db --bundle bundle.zip
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import import --database data\local-monitor.db --bundle bundle.zip --preview-digest <preview_digest>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import history --database data\local-monitor.db --limit 20
```

同じ操作は Local Monitor の `/sanitized-import` 画面からも行えます。取り込み前に件数や競合の有無を確認できます。

## Claude Code のセットアップ

Claude Code（2.1.207 以上）の設定手順です。対象プロジェクトのルートディレクトリで、まず変更内容の確認（plan）を行います。

```powershell
pwsh scripts\local-monitor\setup.ps1 plan --adapter claude-code --target cli
```

対話型 CLI と `claude -p`（非対話モード）は同じユーザー設定を共有します。デフォルトの計画では OpenTelemetry のプロンプト/ツール内容の出力変更は行われません。

> [!NOTE]
> 設定スクリプト実行直後の成功判定は設定ファイルの静的更新成功を意味します。実際のテレメトリ受信用設定の完了は、Local Monitor 画面上でのトレース受領確認（動的確認）によって判定してください。

## データ取り扱いの基本ルール

- 実 credential、secret、Base64 authorization header を repository に保存しない。
- raw prompt、raw response、tool arguments、tool results、source code fragment を commit しない。
- `data\`、`tmp\`、`artifacts/dashboard-input/*.json` は local runtime data として扱う。
- 生成済み dashboard artifact を共有場所に置く前に、access control、retention、削除方法、利用者周知を確認する。
- remote managed Langfuse / Collector endpoint に送信する前に、access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を決める。

## Local Ingestion Monitor の画面イメージ

<p align="center">
  <img width="900" alt="Local Ingestion Monitor 概要" src="./assets/screenshots/local-monitor-overview.png">
</p>

Local Ingestion Monitor は、VS Code や GitHub Copilot CLI から収集したテレメトリを可視化するローカル専用 UI です。トークンコスト、エラー率、モデル別キャッシュ効率をひと目で把握できます。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor トレース一覧" src="./assets/screenshots/local-monitor-trace-list.png">
</p>

保存された全トレースをプロンプト・モデル・実行状態（正常 / エラー / 回復済み）で絞り込み、右プレビューパネルでトークン構成や高コストスパンを即座に確認できます。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor トレース詳細（フロー）" src="./assets/screenshots/local-monitor-trace-detail-flow.png">
</p>

トレース内の各ステップ（LLM 呼び出し、ツール実行、並行処理）を時系列フローまたは Waterfall 表示で追跡し、ボトルネックやエラー原因を特定できます。

## Static Dashboard の画面イメージ

<p align="center">
  <img width="900" alt="Static dashboard overview" src="./assets/screenshots/static-dashboard-overview.png">
</p>

Dashboard は `dashboard-data.json` をブラウザで読み込んで表示します。サーバーは不要です。

<p align="center">
  <img width="900" alt="Static dashboard filters" src="./assets/screenshots/static-dashboard-filters.png">
</p>

## まず試すなら

サンプルデータだけでダッシュボードを生成してみましょう。外部サービスも実データも不要です。

```powershell
New-Item -ItemType Directory -Force tmp\dashboard-demo | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site
```

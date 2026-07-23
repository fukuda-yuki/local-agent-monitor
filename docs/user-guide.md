# 利用者向け詳細ガイド (User Guide Portal)

Copilot Agent Observability の利用者向け公式マニュアルポータルです。  
目的や学習フェーズに応じて、以下の体系的目次から各ガイドを参照してください。
最短の体験手順のみを確認したい場合は [Getting Started](getting-started.md) を参照してください。

---

## 全体目次 (Table of Contents)

### 1. はじめに・環境構築
- [データ安全ポリシー (Data Safety)](user-guide/data-safety.md)  
  ローカルデータ保護方針、収集データ範囲、外部送信を行わないデータセキュリティ境界について解説します。
- [テレメトリ収集と初期セットアップ (Telemetry Collection)](user-guide/telemetry-collection.md)  
  VS Code Copilot Chat、GitHub Copilot CLI、Codex App 等のテレメトリ収集手順とプロファイル設定を説明します。

### 2. 画面別操作マニュアル
- [Local Ingestion Monitor 利用ガイド](user-guide/local-monitor.md)  
  リアルタイムでトレースを監視するローカル UI（概要ダッシュボード、トレース一覧、フロー/Waterfall、スパンインスペクタ、エラー解析モード、Copilot解析ドロワー）の操作ガイドです。
- [Static Dashboard 利用ガイド](user-guide/static-dashboard.md)  
  蓄積データから生成された静的 HTML ダッシュボードの閲覧・フィルタリング方法を解説します。

### 3. タスク・ユースケース別ガイド
- [ローカルデータ集計・変換 (Raw Data Loop)](user-guide/raw-data-loop.md)  
  保存された raw OTLP データを SQLite raw store に取り込み、分析用データセットへ再現可能に集計・変換する手順です。
- [エラー・遅延の自動診断と改善サイクル (Diagnosis / Improvement Loop)](user-guide/diagnosis-improvement-loop.md)  
  失敗傾向やパフォーマンスボトルネックを自動検出し、プロンプトやスキルの改善提案を生成・評価する手順です。

### 4. トラブルシューティング & 安全運用
- [トラブルシューティングガイド](user-guide/troubleshooting.md)  
  PowerShell スクリプトの実行権限エラー（ExecutionPolicy）、ポート4320衝突、VS Code環境変数引き継ぎ、企業プロキシ環境での解決方法をまとめています。

---

## 目的別の利用モード

| タスク・目的 | 使う場面 | 主要コマンド / UI |
| --- | --- | --- |
| **1. リアルタイム観測** | エージェントの挙動やトークンコストをその場で確認したい | Local Ingestion Monitor (`http://127.0.0.1:4320`) |
| **2. トレース詳細調査** | 1 件のチャット実行の全スパン・入出力を深掘りしたい | Local Monitor または Langfuse UI |
| **3. データ集計・変換** | 蓄積されたトレースをデータセットへ変換したい | `ingest-raw`, `normalize-raw` |
| **4. 傾向の視覚化** | 複数セッションの傾向を一覧ダッシュボードで共有したい | `generate-static-dashboard` |
| **5. 改善候補の自動検出** | 失敗や遅延のボトルネックと改善提案を得たい | `generate-improvement-proposals` |
| **6. 安全なエビデンス共有** | サニタイズ済みトレースデータ（エビデンス）を安全に輸出入したい | `sanitized-export`, `sanitized-import`, Local Monitor `/sanitized-import` |

### サニタイズ済みエビデンスデータの共有と取り込み

データ保護が施されたトレースエビデンス（`.zip`）を出力・検証・取り込むコマンド手順です。プロンプトや機密情報を含まない安全な形式でデータをやり取りできます。

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

同じ取り込み操作は Local Monitor 画面の `/sanitized-import` タブからも行えます。事前にプレビュー情報（件数、競合の有無）を確認してから安全に取り込むことができます。

## Claude Code の guided setup

Claude Code 2.1.207 以上では、対象にする Claude project の root へ移動してから、設定値や path そのものを含まない redacted な member state、operation、対象 label を表示する plan を作成します。setup は実行 directory 直下の `.claude/settings.local.json` と `.claude/settings.json` だけを確認し、親 directory、子 directory、Git root、`--add-dir` の project は探索しません。plan だけでは設定を書き換えません。

```powershell
pwsh scripts\local-monitor\setup.ps1 plan --adapter claude-code --target cli
```

interactive CLI と `claude -p` は同じ user settings を使います。既定 plan は OTel の prompt / tool content gate を変更しませんが、mapper 対応済み Hook は raw-bearing event を取得し得るため、`claude_hooks_capture_raw_content` warning を確認してください。OTel content gate も明示的に有効化する場合だけ `--include-content-capture` を追加します。

WSL2 から実行する場合は、WSL 内の process から Local Monitor の loopback readiness に到達できることを確認し、`--allow-wsl2-routing` を明示します。Windows native ではこの option を指定しません。gateway / non-loopback fallback はありません。Agent SDK は `--target app-sdk` で Python / TypeScript の caller-managed guidance を確認できますが、setup はアプリケーションコードを書き換えません。plan 後の apply / rollback は返された change-set ID を使います。setup 完了は static configuration の確認であり、first real trace / Doctor の成功を意味しません。

## 最小の安全ルール

- 実 credential、secret、Base64 authorization header を repository に保存しない。
- raw prompt、raw response、tool arguments、tool results、source code fragment を commit しない。
- `data\`、`tmp\`、`artifacts/dashboard-input/*.json` は local runtime data として扱う。
- 生成済み dashboard artifact を共有場所に置く前に、access control、retention、削除方法、利用者周知を確認する。
- remote managed Langfuse / Collector endpoint に送信する前に、access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を決める。

## 画像で見る Local Ingestion Monitor

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

## 画像で見る Static Dashboard

<p align="center">
  <img width="900" alt="Static dashboard overview" src="./assets/screenshots/static-dashboard-overview.png">
</p>

Dashboard は `dashboard-data.json` を browser で読み込み、client-side で filter / search / sort します。
server-side API は不要です。

<p align="center">
  <img width="900" alt="Static dashboard filters" src="./assets/screenshots/static-dashboard-filters.png">
</p>

## 次の一歩

まず synthetic fixture だけで dashboard を生成してください。
外部サービスも実データも不要です。

```powershell
New-Item -ItemType Directory -Force tmp\dashboard-demo | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site
```

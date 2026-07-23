# トラブルシューティングガイド

Copilot Agent Observability のセットアップおよび運用時に発生しやすい問題と、その解決手順をまとめています。

---

## 1. PowerShell スクリプトの実行エラー (ExecutionPolicy 制限)

### 症状
Windows の PowerShell で `.ps1` スクリプト（`setup.ps1` や `install.ps1` 等）を実行した際、以下のエラーが発生してスクリプトの実行がブロックされる。

```text
このシステムではスクリプトの実行が無効になっているため、ファイル C:\...\setup.ps1 を読み込むことができません。
+ CategoryInfo          : SecurityError: (:) []、PSSecurityException
```

### 原因
Windows / PowerShell の既定のセキュリティポリシー（`ExecutionPolicy`）により、署名されていないローカルスクリプトの実行が企業端末等で制限されているためです。

### 解決策
以下のいずれかの方法でスクリプト実行権限を一時的に回避・許可してください。

#### 方法 A: コマンド実行時に `-ExecutionPolicy Bypass` フラグを付与する（推奨）
現在のコマンド実行時のみポリシーを一時的に回避します。

```powershell
pwsh -ExecutionPolicy Bypass scripts\local-monitor\setup.ps1 plan --adapter github-copilot --target all
# Release ZIP の場合
pwsh -ExecutionPolicy Bypass .\scripts\setup.ps1 plan --adapter github-copilot --target all
```

#### 方法 B: 現在の PowerShell セッションのみ実行を許可する
開いている PowerShell ウィンドウ内でのみ実行を一時許可します（管理者権限不要）。

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

---

## 2. ポート4320の衝突 (`Address already in use`)

### 症状
Local Ingestion Monitor の起動時（`dotnet run` または `start.ps1`）に、以下のエラーが発生してプロセスが終了する。

```text
System.IO.IOException: Failed to bind to address http://127.0.0.1:4320: address already in use.
```

### 原因
他のプロセス（別の Local Monitor インスタンスや別の開発サーバー）が既にポート `4320` を使用しているためです。

### 解決策

#### 方法 A: ポート番号を変更して起動する
`--url` オプションで使用されていないポート（例: `4321`）を指定して起動します。

```powershell
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor.db --url http://127.0.0.1:4321
```

※ ポート変更時は、VS Code 側等の設定環境変数 `OTEL_EXPORTER_OTLP_ENDPOINT` も `http://127.0.0.1:4321` に合わせて更新してください。

#### 方法 B: 既存の占有プロセスを特定して終了する
PowerShell でポート `4320` を使用しているプロセス ID (PID) を特定し、プロセスを停止します。

```powershell
# ポート4320を使用中の PID を検索
Get-NetTCPConnection -LocalPort 4320 | Select-Object LocalAddress, LocalPort, OwningProcess

# 該当プロセスを停止（<PID> は上で確認した数値）
Stop-Process -Id <PID> -Force
```

---

## 3. テレメトリが Local Monitor に届かない（VS Code 環境変数の非引き継ぎ）

### 症状
`setup.ps1` や `profile-vscode-env` を実行し、Local Monitor も起動しているのに、VS Code で Copilot Chat を実行してもトレースが Local Monitor 画面に反映されない。

### 原因
PowerShell ターミナルで `$env:OTEL_EXPORTER_OTLP_ENDPOINT` 等の環境変数を設定した後に、デスクトップショートカットやスタートメニューから別プロセスとして起動した VS Code にはターミナルの環境変数が引き継がれません。

### 解決策
環境変数を適用した **同じ PowerShell ターミナルから `code .` コマンドで VS Code を起動** してください。

```powershell
# 1. ターミナルで環境変数を生成・適用
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor

# 2. 同じターミナルから VS Code を起動
code .
```

すでに起動している VS Code がある場合は、一度すべての VS Code ウィンドウを閉じてから上記手順で再起動してください。

---

## 4. 企業ネットワーク・プロキシ環境での接続トラブル

### 症状
プロキシ環境下で OTLP テレメトリの送信が失敗する、または Langfuse 自宅ホスト等の外部エクスポートで通信エラーが発生する。

### 原因
社内プロキシや SSL/TLS 検査装置により、ローカルエンドポイント以外への通信が遮断または証明書検証エラーになるためです。

### 解決策
- **Local Ingestion Monitor (`raw-local-receiver`) を使用する（推奨）:**
  Local Monitor は完全ローカルのループバック通信 (`http://127.0.0.1:4320`) で動作するため、外部ネットワークやプロキシの影響を受けません。
- **プロキシ例外の設定:**
  必要に応じて `$env:NO_PROXY="127.0.0.1,localhost"` を環境変数に追加設定してください。

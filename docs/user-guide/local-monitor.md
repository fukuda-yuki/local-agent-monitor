# Local Ingestion Monitor

Local Ingestion Monitor（`CopilotAgentObservability.LocalMonitor`）は、VS Code GitHub
Copilot Chat や GitHub Copilot CLI から送られてくる OTLP HTTP/protobuf テレメトリを
ローカルで受け取り、ブラウザ UI でリアルタイムに確認するための単一プロセスツールです。

Langfuse、Docker Desktop、外部ネットワークは不要です。
ループバック（`127.0.0.1`）にバインドし、同一マシン内でのみ動作します。

## 何が確認できるか

| ページ / API | 内容 |
|---|---|
| `http://127.0.0.1:4320/` | 受信件数サマリー・ヘルス概要 |
| `http://127.0.0.1:4320/ingestions` | 受信した raw 取り込みの一覧（sanitized） |
| `http://127.0.0.1:4320/traces` | projection 済み trace 一覧（sanitized） |
| `http://127.0.0.1:4320/diagnostics` | readiness・キュー状態・projection lag |
| `GET /health/ready` | `200 ready` / `200 degraded` / `503 not_ready` |
| `GET /api/monitor/ingestions` | cursor 付き sanitized ingestion API |
| `GET /api/monitor/traces` | cursor 付き sanitized trace API |

既定ではすべてのページ・API・SSE は **sanitized metadata のみ** を返します。
raw prompt / raw response / tool arguments / tool results は表示しません。

## 必要なもの

- .NET SDK（`global.json` で固定されたバージョン）
- VS Code + GitHub Copilot Chat 拡張機能（VS Code source の場合）
- GitHub Copilot CLI（CLI source の場合）
- GitHub アカウント（Copilot サブスクリプション）

## 起動手順

### Step 1 — モニターを起動する

```powershell
New-Item -ItemType Directory -Force data | Out-Null
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor.db --url http://127.0.0.1:4320
```

起動したらブラウザで `http://127.0.0.1:4320/` を開いてください。
`/health/ready` が `200 ready` を返したら受信準備完了です。

オプション:

| オプション | 既定値 | 説明 |
|---|---|---|
| `--db` | `data/raw-store.db` | SQLite raw store のパス |
| `--url` | `http://127.0.0.1:4320` | ループバック bind URL（非ループバックは拒否） |
| `--enable-raw-view` | off | raw-detail route `GET /traces/{id}/raw` を有効化（same-origin 限定） |

### Step 2 — クライアントの環境変数を生成して適用する

**VS Code GitHub Copilot Chat の場合：**

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
```

出力された環境変数を現在の PowerShell セッションに適用し、同じシェルから VS Code を起動します。

```powershell
# 出力結果を貼り付けて実行してから：
code .
```

**GitHub Copilot CLI の場合：**

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-copilot-cli-env --profile raw-local-receiver
```

出力された環境変数を適用してから `gh copilot` コマンドを実行します。

### Step 3 — Copilot を使う

VS Code で Copilot Chat に質問する、または `gh copilot -- -p "..."` を実行します。
モニターはリアルタイムでテレメトリを受信し、ブラウザ UI が自動更新されます。

### Step 4 — ブラウザで確認する

`http://127.0.0.1:4320/ingestions` を開くと、受信した取り込み一覧が表示されます。
受信直後に projection が走り、`/traces` に集約された trace 行が現れます。

`/diagnostics` ではキューの埋まり具合・projection lag・readiness を確認できます。

## ポートとプロファイルの対応

| クライアント | 生成コマンド | 既定エンドポイント |
|---|---|---|
| VS Code Copilot Chat（monitor） | `profile-vscode-env --profile raw-local-receiver --target monitor` | `http://127.0.0.1:4320` |
| VS Code Copilot Chat（legacy receiver） | `profile-vscode-env --profile raw-local-receiver` | `http://127.0.0.1:4319` |
| GitHub Copilot CLI | `profile-copilot-cli-env --profile raw-local-receiver` | `http://127.0.0.1:4319` |

CLI の既定は `4319`（ConfigCli receiver）です。モニター（4320）に向けるには
`OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320` を上書きしてください。

## raw view（opt-in）

`--enable-raw-view` を付けて起動すると、ingestion 行に `raw` リンクが表示され、
`GET /traces/{rawRecordId}/raw` で生の OTLP JSON を確認できます。

- same-origin アクセスのみ（cross-site は `403`）
- `Cache-Control: no-store`
- HTML エスケープされた inert text として描画（スクリプト実行なし）

raw store や表示内容には prompt / response / tool 情報が含まれる場合があります。
raw store ファイル（`data\monitor.db` 等）を repository に commit しないでください。

## SSE によるリアルタイム更新

`GET /events`（`text/event-stream`）を購読すると、新しい取り込みが projection されるたびに
通知（`data: {}`）が届きます。ブラウザの `/ingestions` や `/traces` はこれを使って
自動的にカーソル API を再読み込みします。

通知には raw payload・PII を含みません。

## readiness の見方

`GET /health/ready` のレスポンス例：

```json
{
  "status": "ready",
  "checks": {
    "loopback_bound": true,
    "db_open": true,
    "migration_complete": true,
    "writer_running": true,
    "projection_worker_running": true,
    "ingestion_accepting": true,
    "projection_lag_seconds": 0,
    "projection_backlog": 0
  },
  "degraded_reasons": []
}
```

| status | HTTP | 意味 |
|---|---|---|
| `ready` | 200 | 全チェック通過 |
| `degraded` | 200 | 軽微な一時的状態（瞬間的なバックプレッシャーなど） |
| `not_ready` | 503 | 必須ゲートが未通過（DB 未接続・writer 停止など） |

## データ安全

- `data\monitor.db`、`data\monitor-*.db` は local runtime artifact です。repository に commit しないでください。
- raw view を有効にした場合、表示内容に prompt / response が含まれる可能性があります。スクリーンショットや画面共有の際は注意してください。
- モニターはループバックにのみバインドします。非ループバック URL は起動時に拒否されます。
- ログに raw prompt / response / tool arguments / results は出力しません。

詳細は [Data Safety](data-safety.md) と
[docs/specifications/security-data-boundaries.md](../specifications/security-data-boundaries.md) を参照してください。

## よくあるトラブル

| 症状 | 確認事項 |
|---|---|
| `http://127.0.0.1:4320/` に接続できない | `dotnet run` が起動しているか確認。ポート番号を確認。 |
| ingestion が増えない | 環境変数が正しく適用されているか確認。VS Code を環境変数を設定したシェルから起動したか確認。 |
| `degraded` が続く | `/diagnostics` で `projection_lag_seconds` と `projection_backlog` を確認。 |
| `dotnet run` がビルドエラーで失敗する | 既に同じプロジェクトのプロセスが動いている場合、DLL がロックされます。ビルド済み exe を直接実行してください：`src\CopilotAgentObservability.LocalMonitor\bin\Debug\net10.0\CopilotAgentObservability.LocalMonitor.exe --db data\monitor.db --url http://127.0.0.1:4320` |

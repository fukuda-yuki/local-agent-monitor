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
| `http://127.0.0.1:4320/traces` | projection 済み trace 一覧（sanitized + rollup） |
| `http://127.0.0.1:4320/traces/{traceId}` | agent-execution view（per-span ツール / MCP / sub-agent / トークン / 成否 + raw body inline） |
| `http://127.0.0.1:4320/diagnostics` | readiness・キュー状態・projection lag |
| `GET /health/ready` | `200 ready` / `200 degraded` / `503 not_ready` |
| `GET /api/monitor/ingestions` | cursor 付き sanitized ingestion API |
| `GET /api/monitor/traces` | cursor 付き sanitized trace API（rollup 列付き） |
| `GET /api/monitor/traces/{traceId}/spans` | cursor 付き sanitized span API |

API（`/api/monitor/*`）と SSE は **sanitized metadata のみ** を返します。
raw body（tool arguments / results、sub-agent instructions / responses、system prompt）と PII は
既定で trace-detail page に表示されます（server-rendered、inert text）。
`--sanitized-only` を付けて起動すると metadata-only モードになります。

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
| `--sanitized-only` | off | metadata-only モード。raw-bearing route を `404` にし PII を除外する。画面共有や health-check 時に使用 |

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

## agent-execution view

trace 一覧から trace を選ぶと、agent-execution view（trace-detail page）が表示されます。

確認できる情報：

- ツール / MCP 呼び出し（名前、種別、成否）
- sub-agent の起動（モデル、トークン使用量）
- turn 単位のトークン合計
- span のタイムラインと親子関係（sub-agent tree）
- Summary パネル（合計ツール呼び出し数 / トークン / エラー数 / 所要時間）

## raw body 表示（既定）

raw body（tool arguments / results、sub-agent instructions / responses、system prompt）と
PII（`user.id` / `user.email`）は **既定で表示されます**。trace-detail page に inline で
描画され、`GET /traces/{rawRecordId}/raw` でも個別の raw OTLP JSON を確認できます。

- same-origin アクセスのみ（cross-site は `403`）
- `Cache-Control: no-store`
- HTML エスケープされた inert text として描画（スクリプト実行なし）

`--sanitized-only` を付けて起動すると raw body と PII は非表示になります
（raw-bearing route は `404`）。画面共有や health-check 時に便利です。

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
- 既定で raw body（prompt / response / tool arguments / results）と PII が表示されます。スクリーンショットや画面共有の際は `--sanitized-only` を付けて起動してください。
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

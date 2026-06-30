# Local Ingestion Monitor

Local Ingestion Monitor（`CopilotAgentObservability.LocalMonitor`）は、VS Code GitHub
Copilot Chat や GitHub Copilot CLI から送られてくる OTLP HTTP/protobuf テレメトリを
ローカルで受け取り、ブラウザ UI でリアルタイムに確認するための単一プロセスツールです。

Langfuse、Docker Desktop、外部ネットワークは不要です。
ループバック（`127.0.0.1`）にバインドし、同一マシン内でのみ動作します。

## 何が確認できるか

| ページ / API | 内容 |
|---|---|
| `http://127.0.0.1:4320/` | ダッシュボード。ステータス、ハイライトカード（最新 / 最大トークン / エラーあり）、最近の取り込み一覧。各トレースは**入力プロンプト**で識別表示（既定） |
| `http://127.0.0.1:4320/traces` | trace 一覧。1 件 = 1 カードで、見出しは入力プロンプト（既定）、メトリクスをチップ表示 |
| `http://127.0.0.1:4320/traces/{traceId}` | agent-execution view（概要 / タイムライン / ツリー・フロー / キャッシュ の tab 表示。既定では下段に raw body inline） |
| `http://127.0.0.1:4320/diagnostics` | readiness・キュー状態・projection lag |
| `GET /health/ready` | `200 ready` / `200 degraded` / `503 not_ready` |
| `GET /api/monitor/ingestions` | cursor 付き sanitized ingestion API |
| `GET /api/monitor/traces` | cursor 付き sanitized trace API（rollup 列付き） |
| `GET /api/monitor/traces/{traceId}/spans` | cursor 付き sanitized span API |

> 旧 `/ingestions` ページは廃止し、取り込み一覧はダッシュボードに統合しました。

API（`/api/monitor/*`）と SSE は **sanitized metadata のみ** を返します（プロンプトを含みません）。
raw body（tool arguments / results、sub-agent instructions / responses、system prompt）と PII は
既定で trace-detail page に表示されます（server-rendered、inert text）。さらに、トレースを
不透明な TraceId ではなく入力プロンプトで識別できるよう、**ダッシュボードと trace 一覧でも
代表プロンプトを既定で表示**します（D032。raw store の OTLP から server 側で抽出した inert text。
same-origin / `Cache-Control: no-store` を強制）。`--sanitized-only` を付けて起動すると
metadata-only モードになり、TraceDetail の sanitized tab は残したまま raw section と full raw link、
およびダッシュボード / 一覧のプロンプト表示を非表示にし、短縮 TraceId に切り替えます。

## 必要なもの

- Release ZIP 利用時: `local-monitor-win-x64.zip`
- repository から起動する場合: .NET SDK（`global.json` で固定されたバージョン）
- VS Code + GitHub Copilot Chat 拡張機能（VS Code source の場合）
- GitHub Copilot CLI（CLI source の場合）
- GitHub アカウント（Copilot サブスクリプション）

## 起動手順

### Step 1A — Release ZIP から起動する

`local-monitor-win-x64.zip` を展開し、展開先で次を実行します。

```powershell
.\scripts\install.ps1
.\scripts\start.ps1 -Mode Published
.\scripts\status.ps1
```

Release ZIP は self-contained publish です。利用者端末で `dotnet run` /
`dotnet build` / `dotnet restore` を実行せず、.NET SDK / .NET Runtime /
ASP.NET Core Runtime の事前導入も前提にしません。

`install.ps1` は app 本体を次の install root にコピーするだけです。既定では起動も
Task Scheduler 登録もしません。

```text
%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\
```

今すぐ起動する場合は `start.ps1 -Mode Published` を実行します。次回ログオン時から
自動起動したい場合だけ、別途 Task Scheduler 登録を行います。

```powershell
.\scripts\install-startup-task.ps1 -Mode Published
.\scripts\set-startup-task.ps1 -Action Disable
.\scripts\set-startup-task.ps1 -Action Enable
```

停止・解除:

```powershell
.\scripts\stop.ps1 -Force
.\scripts\uninstall-startup-task.ps1 -StopRunning
```

uninstall は既定で DB / logs を保持します。明示的に削除したい場合のみ
`-RemoveData -Force` を付けます。

### Step 1B — repository からモニターを起動する

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
| `--sanitized-only` | off | metadata-only モード。TraceDetail の sanitized tab は表示し、raw section / full raw route と PII、およびダッシュボード / 一覧のプロンプト表示を除外する任意 opt-out。 |

### Windows logon startup

Windows では、Task Scheduler の user-level task として LocalMonitor をログオン時に
起動できます。Task Scheduler 登録は install とは別の明示操作です。

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -StartNow
.\scripts\local-monitor\status.ps1
```

既定では `http://127.0.0.1:4320` で起動し、DB / logs / state は
`%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下に保存します。
metadata-only の常時起動にしたい場合は `-SanitizedOnly` を付けます。

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -SanitizedOnly -StartNow
```

Task Scheduler 登録 script は VS Code 設定を書き換えません。クライアントを
monitor に向ける設定は、次の Step 2 の Config CLI 出力を使います。

停止・解除:

```powershell
.\scripts\local-monitor\stop.ps1 -Force
.\scripts\local-monitor\uninstall-startup-task.ps1 -StopRunning
```

登録済み startup の有効化・無効化:

```powershell
.\scripts\local-monitor\set-startup-task.ps1 -Action Disable
.\scripts\local-monitor\set-startup-task.ps1 -Action Enable
```

詳細は [Task Scheduler operation](../operations/local-monitor-task-scheduler.md) を参照してください。

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

`http://127.0.0.1:4320/`（ダッシュボード）を開くと、ステータスとハイライトカード、
最近の取り込み一覧が表示されます。受信直後に projection が走り、`/traces` に集約された
trace カードが現れます。各トレースは入力プロンプトで識別できます（既定）。

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
ページ上部にはパンくず（ダッシュボード › トレース › プロンプト）、入力プロンプトの見出し、
メトリクスカード（トークン入/出/合計・所要時間・エラー・ターン数）が並びます。その下は
sanitized なデータを 4 つの tab で表示し、既定では最下段に raw body が続きます。
`--sanitized-only` では tab は表示されたまま、raw section と PII、プロンプト見出しが非表示に
なります（見出しは短縮 TraceId に切替）。tab を切り替えても表示中の trace は変わりません。

| Tab | 内容 |
|---|---|
| **概要** | クライアント / モデル / エージェント呼出 / ツール呼出 / スパン数 / 初回・最終時刻（補足情報として折りたたみ表示） |
| **タイムライン** | span のフラット一覧。errors-only フィルタと tokens / time ソートで絞り込み・並べ替えできる |
| **ツリー / フロー** | span 階層をインデントツリー（左にウォーターフォールバー）で表示。トグルで DOM フローチャート（時系列ノード）に切替。カテゴリ別に配色（agent / llm / tool / hook）、エラー span は赤で強調 |
| **キャッシュ** | trace 内の chat turn を起点（`invoke_agent`）ごとにまとめ、cache-hit rate・cache-creation tokens・所要時間・モデル・時刻・token 内訳を表示 |

これらの tab はすべて、既存の sanitized span API（`GET /api/monitor/traces/{traceId}/spans`）
を client-side で描画したものです。新しい受信データや raw データは使いません。ツリー / フローは
**素の DOM** で描画し（D033。Cytoscape.js / dagre の同梱依存は廃止）、日本語フォント
（Noto Sans JP / Mono）はローカルに同梱しており、外部ネットワークへはアクセスしません。

> Cache-hit rate は `cache_read_tokens / input_tokens` で算出します（ゼロ除算はガード）。
> 「起点ごとのまとめ」は telemetry に user-request id が無いため、root `invoke_agent` span を
> 近似のグループ単位として扱っています。

> 画面は VS Code 風の dark テーマです（開発者向けツールのため。Static Dashboard とは別の
> デザインです）。

## raw body 表示（既定）

raw body（tool arguments / results、sub-agent instructions / responses、system prompt）と
PII（`user.id` / `user.email`）は **既定で表示されます**。trace-detail page に inline で
描画され、`GET /traces/{rawRecordId}/raw` でも個別の raw OTLP JSON を確認できます。
加えて、ダッシュボードと trace 一覧、trace 詳細の見出しでは、各トレースの**代表入力
プロンプト**を server 側で抽出して表示します（D032。プロンプトのみ raw 扱いで、他の列は
sanitized メタデータ。`/api/monitor/*` と SSE はプロンプトを含みません）。

raw を表示する全ページ（ダッシュボード / trace 一覧 / trace 詳細 / raw route）は次を満たします:

- same-origin アクセスのみ（cross-site は `403`）
- `Cache-Control: no-store`
- HTML エスケープされた inert text として描画（スクリプト実行なし）

`--sanitized-only` を付けて起動すると raw body と PII、プロンプト表示は非表示になります。
TraceDetail の sanitized tab shell は引き続き開けますが、raw section と full raw link は出ず、
ダッシュボード / 一覧 / 見出しのプロンプトは短縮 TraceId に切り替わり、
`GET /traces/{rawRecordId}/raw` は `404` です。metadata-only 表示が必要な場合に使用できます。

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
- Task Scheduler 起動時の既定 DB / logs / state は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下に保存されます。これらも repository に commit しないでください。
- 既定で raw body（prompt / response / tool arguments / results）と PII が表示されます。metadata-only 表示が必要な場合は `--sanitized-only` を付けて起動できます。
- モニターはループバックにのみバインドします。非ループバック URL は起動時に拒否されます。
- ログに raw prompt / response / tool arguments / results は出力しません。

詳細は [Data Safety](data-safety.md) と
[docs/specifications/security-data-boundaries.md](../specifications/security-data-boundaries.md) を参照してください。

## GitHub Copilot app Canvas adapter

Local Ingestion Monitor は GitHub Copilot app extension（Canvas adapter）経由で
Copilot CLI から参照できます。Canvas extension は
`.github/extensions/otel-monitor-canvas/extension.mjs` に配置された
project-scoped extension で、モニター UI を再実装せず、既存の
`/api/monitor/*` API と `/health/ready` から bounded action response を返します。

### Local Monitor 姿勢

Canvas adapter は通常起動の raw default Local Monitor と併用できます。
`--sanitized-only` は Canvas 用の必須設定ではなく、metadata-only にしたい場合の
任意モードです。このモードでは TraceDetail の sanitized tab（概要 / タイムライン /
ツリー・フロー / キャッシュ）は残りますが、raw section と full raw route が非表示になります。

### 必要なもの

- GitHub Copilot app（Canvas extension runtime をサポートするバージョン）
- Local Ingestion Monitor を loopback で起動済み

### 使い方

1. モニターを起動します。

   ```powershell
   dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor.db --url http://127.0.0.1:4320
   ```

   必要に応じて `--sanitized-only` を追加すると raw section / full raw route / PII は
   除外されます。

   `/health/ready` が `200 ready` を返すことを確認してください。

2. Copilot app で Canvas extension を開きます。Copilot app は
   `.github/extensions/otel-monitor-canvas/` を自動検出します。Canvas id は
   `otel-monitor` です。

3. `open()` が完了すると、拡張所有の loopback ヘルパーページ
   （`http://127.0.0.1:<port>/?t=<token>`）が開きます。このページに
   モニター健全性、trace ドロップダウン、focus セレクタ、
   "Analyze selected trace with Copilot" ボタンが表示されます。

4. ボタンを押すと、Copilot に bounded analysis 指示が送信されます。Copilot は
   Canvas actions（`monitor_health`、`list_recent_traces`、`get_trace_summary`、
   `get_trace_span_tree`、`get_cache_summary`）を呼び出して trace を分析します。

### Canvas actions

| Action | 入力 | 出力 |
|---|---|---|
| `monitor_health` | なし | モニター到達性・readiness 状態・Canvas adapter 診断メッセージ |
| `list_recent_traces` | `limit`（1..50）、`status?`（ok/error）、`model?` | 最近の trace の sanitized メタデータ一覧 |
| `get_trace_summary` | `traceId` | trace 全体サマリー・top spans・models・cache totals |
| `get_trace_span_tree` | `traceId` | span の親子階層（sanitized）または flat diagnostic |
| `get_cache_summary` | `traceId` | cache トークン指標・per-turn breakdown・cache hit rate |

全ての action response は bounded DTO です。raw prompt / response body、
tool arguments / results、PII、credential、token、local sensitive path、raw monitor
payload は含まれません。raw details は Local Monitor UI の loopback / same-origin
境界内で扱います。

### セキュリティ境界

- 拡張所有の HTTP server は `127.0.0.1` のみにバインドします。
- ヘルパーページとプロキシ route は per-launch token で保護されます。
- `onClose()` で server が閉じられます。
- 外部 CDN / remote fetch は行いません。
- 診断は `session.log()` を使用し（`console.log` 不使用）、stdout を JSON-RPC 専用に保ちます。

詳細は [docs/specifications/security-data-boundaries.md](../specifications/security-data-boundaries.md)
と [docs/decisions.md](../decisions.md) D029 を参照してください。

## よくあるトラブル

| 症状 | 確認事項 |
|---|---|
| `http://127.0.0.1:4320/` に接続できない | LocalMonitor process が起動しているか確認。ポート番号を確認。 |
| `published_app_not_installed` | Release ZIP 展開先で `.\scripts\install.ps1` を実行したか、`-InstallRoot` が正しいか確認。 |
| Release ZIP 起動後に startup 登録されていない | install は startup 登録を行いません。必要な場合だけ `install-startup-task.ps1 -Mode Published` を実行してください。 |
| ingestion が増えない | 環境変数が正しく適用されているか確認。VS Code を環境変数を設定したシェルから起動したか確認。 |
| `degraded` が続く | `/diagnostics` で `projection_lag_seconds` と `projection_backlog` を確認。 |
| `dotnet run` がビルドエラーで失敗する | 既に同じプロジェクトのプロセスが動いている場合、DLL がロックされます。ビルド済み exe を直接実行してください：`src\CopilotAgentObservability.LocalMonitor\bin\Debug\net10.0\CopilotAgentObservability.LocalMonitor.exe --db data\monitor.db --url http://127.0.0.1:4320` |

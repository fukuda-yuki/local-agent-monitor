# Local Ingestion Monitor

Local Ingestion Monitor（`CopilotAgentObservability.LocalMonitor`）は、VS Code GitHub
Copilot Chat や GitHub Copilot CLI から送られてくる OTLP HTTP/protobuf テレメトリを
ローカルで受け取り、ブラウザ UI でリアルタイムに確認するための単一プロセスツールです。

Langfuse、Docker Desktop、外部ネットワークは不要です。
ループバック（`127.0.0.1`）にバインドし、同一マシン内でのみ動作します。

## 何が確認できるか

Local Monitor v1 の基本操作は、リポジトリを選ぶ → Session Explorer でセッションを探す
→ セッション詳細を開く、または明示的に比較を作成する流れです。
観測・調査・比較は AI なしで利用できます。GitHub Copilot SDK の AI 分析は任意です。

ヘッダーにはパンくず、受信状態、「設定」があります。受信状態と「設定」は同じ設定モーダルを開きます。
常設サイドバー、概要 KPI ダッシュボード、一覧の右側プレビューはありません。

| 画面 | 開き方 | 内容 |
|---|---|---|
| リポジトリ選択 | `http://127.0.0.1:4320/` | リポジトリのカード、すべてのセッション、リポジトリ未設定のセッション |
| Session Explorer | `/repositories/{repositoryId}/sessions` | リポジトリ内のセッションを検索・絞り込み、直接開く |
| すべてのセッション / 未設定 | `/sessions` / `/sessions/unassigned` | リポジトリを指定しない調査、手動割り当て |
| セッション詳細 | `/sessions/{sessionId}`（任意で `execution` / `node` / `analysis` / `settings`） | 「セッションの概要」「最初の指示」、状態（**状態未観測** を含む）、**最終観測**、トークン、階層タイムライン、「技術情報」 |
| 比較 | Explorer の「比較を作成」→ `/repositories/{repositoryId}/comparisons/{comparisonId}` | 基準と比較対象の固定指標、利用可能件数、根拠への移動 |
| 設定 | ヘッダーの「設定」 | 状態 / 受信 / AI設定 / リポジトリ / アーカイブ / 保存・バックアップ / 診断 |

旧 `/traces` 一覧と `/historical-analysis` は廃止され、空 body の `404` と
`Cache-Control: no-store` を返します。これは後述する受信フォールバックの
`unsupported_endpoint` JSON とは別です。
`/traces/{traceId}` は正確なトレースを調べる技術情報の画面として残ります。
`/diagnostics`、`/historical-import`、`/backup-restore` は管理の詳細画面、
`/alerts`、`/costs`、`/sanitized-import` は個別の用途の画面として引き続き利用できます。
設定の「保持と削除」は `/diagnostics#retention-diagnostics` を開き、対象別の保持操作は
`/retention/{targetKind}/{targetId}` で行います。
Canvas、CLI と以下の既存 machine API は、この画面構成の変更では廃止されません。

製品の正本は [製品定義](../specifications/interfaces/local-monitor-v1-product-definition.md) と
[IA 仕様](../specifications/interfaces/local-monitor-v1-ia.md) です。

主な API は次のとおりです。

| API | 内容 |
|---|---|
| `GET /health/ready` | `200 ready` / `200 degraded` / `503 not_ready` |
| `GET /api/monitor/overview` | 概要 KPI（sanitized、`period` クエリ対応） |
| `GET /api/monitor/trace-list` | 一覧用 trace 行（sanitized、フィルタ / ソート対応） |
| `GET /api/monitor/ingestions` | cursor 付き sanitized ingestion API（取り込み履歴） |
| `GET /api/monitor/traces` | cursor 付き sanitized trace API（rollup 列付き） |
| `GET /api/monitor/traces/{traceId}/spans` | cursor 付き sanitized span API |
| `GET /api/alert-center/v1/alerts` | frozen receipt・lifecycle・根拠となる証拠・再発判定・抑制状況の sanitized snapshot |
| `GET /api/alert-center/v2/alerts` | receipt v1/v2、multi-Session cost scope、pricing evidence、budget suppression を含む version-aware sanitized snapshot |
| `POST /api/alert-center/v1/evaluations` | 特定の Session + trace を利用者が明示指定する評価。自動評価ではなく、現行 source manifest では receipt を作らず抑制状況を記録 |
| `/api/costs/v1/*` | Cost configuration preview/commit、明示 recalculation/poll、Session estimate history、bounded analytics（POST は CSRF header 必須） |
| `GET /traces/{traceId}/spans/{spanId}/detail` | スパンインスペクタ用の raw-bearing span 詳細（`--sanitized-only` では人向け route 不在のため空 `404` + `no-store`） |
| `/api/sanitized-import/v1/*` | sanitized bundle の preview / 明示取り込み / history（same-origin、POST は CSRF header 必須） |
| `POST /api/runtime-backup/v1/backups` | 稼働中 DB の online backup を作成（exact `{}`、CSRF header 必須） |
| `GET /api/runtime-backup/v1/backups/{backup_id}` | online backup の作成結果を取得 |
| `GET /api/runtime-backup/v1/backups/{backup_id}/archive` | process-owned backup archive をダウンロード |
| `POST /api/runtime-backup/v1/previews` | 選択した backup ZIP の互換性と offline restore 前提を preview（`application/zip`、CSRF header 必須） |
| `/api/doctor/*` | source に依存しない Doctor evaluation / verification の 5 route |
| `/api/doctor/ui/v1/*` | 診断画面向けの Doctor UI proxy。`--sanitized-only` では空 `404` + `no-store` |
| `/api/historical-import/v1/*` | 履歴 import の preview / confirmation / result / history / observation（sanitized、no-store） |

HTTP restore endpoint はありません。`/backup-restore` は作成と確認だけです。restore は
**意図した対象**を停止したあと、Config CLI の `runtime-backup restore` だけで実行します。
`--bundle` と `--database` は必須です。

凍結済みの `/api/monitor/*`、`/api/session-workspace/*` v1 と SSE は sanitized metadata を返します。
これに対し `/api/local-monitor/v1/*` はローカルの人向け UI 用で、raw 内容を含むため共有用 API ではありません。
記録された指示やツール入出力は、保存期間と取得状態が許す範囲でローカル画面から確認できます。
`--sanitized-only` は receiver-only です。health と `POST /v1/traces`、対応する machine API は
残ります。Razor Pages、human static assets、人向け画面、`/api/local-monitor/v1/*`、
Doctor UI、runtime-backup の Web route は登録せず、空 body の `404` と
`Cache-Control: no-store` を返します。画面ごとの縮退 UI はありません。
一致しない raw 風の path（例: `/traces/.../raw`）は、この空 404 ではなく、既存の
`unsupported_endpoint` JSON フォールバック（`Only /v1/traces is supported.`）を使います。

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

日常 instance の停止・解除は `-RuntimeRoot` を省略します。省略時の対象は既定の
`%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` であり、実装欠陥ではありません。
この例を隔離検証の手順として使わないでください。

```powershell
.\scripts\stop.ps1 -Force
.\scripts\uninstall-startup-task.ps1 -StopRunning
```

一時的に日常 instance と分離して検証する場合は、空でない完全修飾パスを同じ
`-RuntimeRoot` として `start.ps1`、`status.ps1`、`stop.ps1` に渡します。
隔離先の `-Url`、`-DbPath`、`-InstallRoot` も start に明示し、日常 instance の
`http://127.0.0.1:4320` や既定 DB と混ぜないでください。この指定は
既定の app、DB、logs、state、PID の場所をその実行だけ切り替え、Task Scheduler には
保存されません。

明示 root では state、PID、process、URL、DB / install path、mode、repository、
executable identity が一致する場合だけ稼働中と判定します。不一致は process を停止せず
state も削除せず `runtime_state_mismatch` で終了します。state がない場合、`status.ps1`
は通常ユーザー領域の既定 URL を probe しません。

```powershell
$runtimeRoot = 'C:\private\local-monitor-isolated'
$monitorUrl = 'http://127.0.0.1:4321'
$db = Join-Path $runtimeRoot 'raw-store.db'
$installRoot = Join-Path $runtimeRoot 'app'
.\scripts\start.ps1 -Mode Published -RuntimeRoot $runtimeRoot -Url $monitorUrl -DbPath $db -InstallRoot $installRoot
.\scripts\status.ps1 -RuntimeRoot $runtimeRoot
.\scripts\stop.ps1 -RuntimeRoot $runtimeRoot -Force
```

`-InstallRoot` は、その隔離 instance が実行する Published app を指します。隔離先へ
コピーした app でも、意図して共有する既存 install でも構いません。対象を省略した
`stop.ps1 -Force` は日常 instance を停止します。

uninstall は既定で DB / logs を保持します。明示的に削除したい場合のみ
`-RemoveData -Force` を付けます。

### Step 1B — repository からモニターを起動する

```powershell
New-Item -ItemType Directory -Force data | Out-Null
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor.db --url http://127.0.0.1:4320
```

起動したらブラウザで `http://127.0.0.1:4320/` を開いてください。
`/health/ready` が `200 ready` を返したら受信準備完了です。

> `dotnet run` は Web SDK の既定で作業ディレクトリをプロジェクトディレクトリに
> 設定するため、相対 `--db` は `src\CopilotAgentObservability.LocalMonitor\` 基準で
> 解決されます。DB の場所を固定したい場合は絶対パスを指定してください。

オプション:

| オプション | 既定値 | 説明 |
|---|---|---|
| `--db` | `data/raw-store.db` | SQLite raw store のパス |
| `--url` | `http://127.0.0.1:4320` | ループバック bind URL（非ループバックは拒否） |
| `--sanitized-only` | off | receiver-only。health と `/v1/traces`、対応する machine API は残し、Razor Pages、human static assets、人向け画面、Doctor UI、runtime-backup の Web route、`/api/local-monitor/v1/*` を登録しない任意 opt-out。画面ごとの縮退 UI ではない。 |
| `--pricing-registry-override <absolute-file>` | なし | estimated-cost 用の trusted local override registry。最大8回まで指定でき、指定順で bundled registry の後へ追加します。 |
| `--apply-root user_config=<absolute-directory>` | なし | proposal apply で使う明示登録済みのローカル user-config root |
| `--apply-root skill=<absolute-directory>` | なし | proposal apply で使う明示登録済みのローカル Skill root |
| `--apply-root repository=<absolute-directory>` | なし | proposal apply で使う明示登録済みの repository working-tree root |

Pricing override はローカルの通常ファイルだけを起動時に1回読みます。相対/UNC/
network/device path、symlink/reparse を含む親、重複、1 MiB 超、malformed/
credential-bearing registry は拒否され、最大64 document/4 MiB の catalog 上限も
適用されます。private contract data を含む override は repository に commit しないで
ください。path、document bytes、private rate/provenance は画面/API/logへ表示されません。
画面へ出るのは検証済みの `bundled` / `local_override` 区分、repository-safe source
label/identity、registry version、effective interval、review/stale date、currency、
catalog SHA、estimate metadata、inert な reviewed public source reference に限られます。
upload/fetch/edit はありません。変更を反映するには Local Monitor を再起動し、Cost
configuration を preview/commit してから明示 recalculation を実行します。ただし
override は catalog だけを変更し、positive source adapter の権限を追加しません。
現行 production manifest では genuine positive estimate は unavailable のままで、
synthetic success は live capability evidence ではありません。

Release ZIP と repository-local wrapper の one-shot 起動では、同じ順序の file を
`-PricingRegistryOverride <string[]>` で渡せます。one-shot 起動は path を保存しません。
Task Scheduler へ永続登録する場合だけ、同じ parameter を
`install-startup-task.ps1` へ明示指定します。この場合、private absolute path は
current-user Task Scheduler action arguments に保存され、同一ユーザーまたは管理者の
OS tooling から見えます。Local Monitor と wrapper はその path を log、state、画面、
API、evidence へ複製しません。DB/runtime backup にも locator は含まれないため、
restore 後は同じ reviewed file set/order を再指定するか、新しい catalog で
configuration を preview/commit してください。

### Canvas proposal をローカルへ適用する

この操作は既存 proposal を明示承認してから行う、Local Monitor のローカル専用操作です。
適用 root は推測されず、API から登録することもできません。起動時に必要な root だけを
絶対パスで明示指定します。以下の `<...>` は実在するローカルディレクトリに置き換える
ためのプレースホルダーです。

```powershell
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db <absolute-db-path> --url http://127.0.0.1:4320 `
  --apply-root user_config=<absolute-user-config-directory> `
  --apply-root skill=<absolute-skill-directory> `
  --apply-root repository=<absolute-repository-working-tree-directory>
```

指定した root そのもの、および volume root までの祖先に symlink / junction / reparse
point がある場合は起動を拒否します。対象にできるのは、設定済み root 配下にすでに存在する
通常ファイルだけです。ディレクトリ、作成、削除、名前変更、任意パスの登録はできません。

Canvas の Improve で既存 proposal を選び、**Apply locally** を開いた後の手順は次のとおりです。

1. token で保護された helper 画面で、下書きと差分全体を確認する。
2. ファイルまたは hunk を選択し、選択後の diff を確認する。
3. 選択内容を明示承認する。
4. 承認済み下書きだけを別操作で apply する。
5. apply 後に戻す必要がある場合だけ、現在のファイル hash が apply 直後の hash と一致するときに限り、一度だけ rollback する。

選択対象のいずれかの base hash が古くなっていれば、**選択した全ファイルに対して書き込みは
行われません**。snapshot / journal を使う起動時の recovery も fail-closed です。安全に
復旧できない未完了 transaction がある場合は、その root を推測で復旧せず、適用・rollback
を受け付けません。

パス、source、差分全体は token で保護された helper 画面と下書き表示の範囲内だけで扱われます。Canvas
action、`session.send()`、git branch / commit / push / PR 操作はファイルを適用しません。

### Windows logon startup

Windows では、Task Scheduler の user-level task として LocalMonitor をログオン時に
起動できます。Task Scheduler 登録は install とは別の明示操作です。

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -StartNow
.\scripts\local-monitor\status.ps1
```

既定では `http://127.0.0.1:4320` で起動し、DB / logs / state は
`%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下に保存します。
receiver-only で常時起動したい場合は `-SanitizedOnly` を付けます。人向け画面の縮退表示では
ありません。

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -SanitizedOnly -StartNow
```

pricing override をログオン起動へ保持する場合は、Task Scheduler arguments に private
absolute path が保存されることを確認したうえで、明示的に登録します。

```powershell
.\scripts\local-monitor\install-startup-task.ps1 `
  -PricingRegistryOverride @('<absolute-registry-a>','<absolute-registry-b>') `
  -StartNow
```

Task Scheduler 登録 script は VS Code 設定を書き換えません。クライアントを
monitor に向ける設定は、次の Step 2 の user environment script または Config CLI
出力を使います。

停止・解除（日常 instance。`-RuntimeRoot` 省略）:

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

### Step 2 — GitHub Copilot をガイド付きで設定する

生の設定値を含まない計画内容を確認し、返された `change_set_id` を指定して apply します。
Release ZIP では次の順に実行します。

```powershell
.\scripts\setup.ps1 plan --adapter github-copilot --target all
.\scripts\setup.ps1 apply --change-set <change-set-id>
.\scripts\setup.ps1 status --adapter github-copilot
.\scripts\setup.ps1 rollback --change-set <change-set-id>
```

リポジトリから実行する場合は、wrapper の場所だけが変わります。

```powershell
.\scripts\local-monitor\setup.ps1 plan --adapter github-copilot --target all
.\scripts\local-monitor\setup.ps1 apply --change-set <change-set-id>
.\scripts\local-monitor\setup.ps1 status --adapter github-copilot
.\scripts\local-monitor\setup.ps1 rollback --change-set <change-set-id>
```

> [!IMPORTANT]
> `setup.ps1 apply` の出力に含まれる `{"success": true}` は、設定ファイルの静的検証および書き込みが完了したことを示します。
> 設定完了の動的な証明は、VS Code で Copilot Chat を実行した際に Local Monitor 画面（`http://127.0.0.1:4320/`）へ最初のトレース（First Trace）が反映されることです。

`all` は VS Code Stable / Insiders の Default Profile、GitHub Copilot CLI、
呼び出し元が管理する App / SDK 向けガイダンスを計画します。App / SDK は sample contract のみで、
呼び出し元が所有する file は変更しません。apply 後は既に起動済みの VS Code、terminal、
Copilot CLI を target ごとの `restart_requirement` と `next_actions` に従って再起動してください。

各 command は stdout に 1 個の `setup.v1` JSON を返します。`success: true` は
設定ファイル／current-user environment の静的な検証結果であり、trace 到着の証拠では
ありません。この setup command 自体は初回 trace の受信確認を行わず、確認は後続の
First Trace 確認手順へ引き継ぎます。Claude adapter に対する変更 CLI apply が成功すると
`restart_claude_process` に続けて `run_first_trace_doctor` を返し、
`first-trace begin --adapter claude-code` へ引き継ぎます。これはテレメトリの受信証拠
ではありません。

Release ZIP の wrapper は `app/config-cli/` の self-contained executable を直接使うため、
.NET SDK / Runtime を必要としません。リポジトリ版 wrapper と引数、stdout、exit code の
契約は同じです。

### 代替 — クライアントの環境変数を手動で永続化する

Windows ユーザーで新しく起動する VS Code GitHub Copilot Chat と GitHub Copilot CLI
を常に monitor に向けるには、current user の永続環境変数を設定します。

Release ZIP:

```powershell
.\scripts\install-user-env.ps1
```

リポジトリ:

```powershell
.\scripts\local-monitor\install-user-env.ps1
```

この script は user scope（HKCU user environment）だけを更新し、管理者権限を要求しません。
`setx` は使わず、Windows の user environment API で値を保存して環境変更通知を送ります。
既に起動済みの VS Code、terminal、Copilot CLI には反映されないため、設定後に再起動してください。

設定を解除する場合:

```powershell
.\scripts\uninstall-user-env.ps1
```

リポジトリからは `.\scripts\local-monitor\uninstall-user-env.ps1` を使います。

user environment は VS Code と Copilot CLI で共有されるため、`OTEL_RESOURCE_ATTRIBUTES`
には `client.kind` を設定しません。クライアント種別より、同じ Windows ユーザーで起動する
全プロセスの常時収集を優先する運用です。

### 代替 — 一時的に現在のシェルだけへ適用する

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

出力された環境変数を適用した後、同じ shell で送信先を monitor に変更してから実行します。
この CLI 用 profile の既定は receiver の `4319` です。

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://127.0.0.1:4320"
copilot -p "動作確認用の短い質問"
```

### Step 3 — Copilot を使う

VS Code で Copilot Chat に質問する、または `copilot -p "..."` を実行します。
モニターはリアルタイムでテレメトリを受信し、ブラウザ UI が自動更新されます。

### Step 4 — ブラウザで確認する

1. `http://127.0.0.1:4320/` を開き、受信状態を確認します。
2. リポジトリのカードを開きます。見つからない場合は「すべてのセッション」または
   「リポジトリ未設定のセッション」を開きます。
3. 対象のセッション行を開き、取得元、開始時刻、指示、実行の記録を確認します。
   受信から表示まで projection の処理待ちが生じる場合があります。

設定の成功だけでなく、実行した内容がセッション詳細に反映されたことを確認してください。

## Alert Center

`http://127.0.0.1:4320/alerts`、または技術的なトレース詳細の「関連 Alert」から開きます。

一覧では重大度、lifecycle 状態、ルール、source、repository、workspace、期間、
完全性で絞り込めます。各行の詳細には receipt が固定した観測値・実効しきい値・
source/version・完全性と、正確に解決できた証拠リンクだけが表示されます。
一致結果は100件ずつ表示され、前へ/次へで全ページを移動できます。フィルターを
変更すると先頭ページへ戻ります。
見つからない、期限切れ、または identity が一致しない証拠は補完されず、
`missing` / `expired` / `unknown` としてリンクなしで表示されます。

画面の「Recurring patterns」は同じ rule/version、repository、workspace、
source/version、UTC 観測日、選択期間の組み合わせで集計し、2つ以上の異なる
Session がある場合だけ `supported` です。同じ Session の複数 receipt は
再発とはみなしません。
画面の「Coverage / suppressions」に表示される抑制 fact はアラートではありません。

取得上限に達して `snapshot_state: incomplete` になった場合、画面は取得範囲内の
結果として表示します。空でも全体の 0 件とは断定せず、
「Recurring patterns」も `incomplete_snapshot` のままです。

状態変更は詳細に表示された許可済み操作（acknowledge / dismiss / resolve / reopen）
から行います。画面は表示中の revision を送信し、競合した場合は上書きせず最新状態を
再読み込みします。Alert Center が独自の lifecycle 状態を持つことはありません。

評価は `POST /api/alert-center/v1/evaluations` への明示操作だけです。取り込み、起動、
ページ表示、GET では実行されません。canonical UUIDv7 Session と対象 trace を指定し、
その trace の全 span が持つ `raw_record_id` ごとに source 観測の
surface/application version が一致し、trace の span count と保存済み span 行数が
一致した場合だけ評価へ進みます。欠落、部分 projection、version 不明、混在は推測せず
拒否されます。既定の `raw-otlp` receiver は application version を持たないため、この
区分チェックで拒否され、adapter version から version を補いません。現行 source
capability manifest は frozen rule が
必要とする capability を認可していないため、本番評価は receipt を生成せず、
成功した exact な区分について10ルール分の `missing_required_capability` または
`source_not_applicable` を抑制状況として記録します。実際に発火した alert の表示は将来、
exact な source/version manifest と adapter が認可された後に限って生成されます。

Cost budget receipt は version-aware v2 一覧にも表示されます。Session scope は exact
Session、UTC-day / configured rolling-period scope は ordered multi-Session
membership と pricing estimate evidence へ遷移します。rolling period は明示した
midnight-UTC cutoff までの 2..366 個の完全な UTC calendar day です。aggregate receipt
はすでに一つの window を表すため、v1 の
Recurring patterns へ重ねて集計しません。取得上限、missing/expired evidence、
lifecycle revision conflict は v1 と同様に明示し、別の lifecycle は作りません。

## Estimated-cost analytics

`http://127.0.0.1:4320/costs` は個別の費用調査画面です。データがない場合も
Diagnostics の固定 Cost entry から開けます。Session または Alert Center
の文脈リンクでは exact Session / estimate に絞り込めます。次の順で使用します。

1. safe catalog route から bundled/local-override catalog の metadata と provider
   catalog state を確認する。cursor は opaque で、catalog が変わった場合は page one
   から再取得する。
2. source/billing route と、必要な場合は Session / UTC calendar day / configured
   rolling period budget の `USD` currency、warning/critical threshold、
   minimum coverage（0..10000 basis points）、window kind を明示して configuration
   を preview する。rolling period は `window_days`（2..366）も明示する。
3. selection/head/catalog が変わっていない preview だけを commit する。成功時の
   `Location` は immutable configuration read を指すため、lost response の retry でも
   exact commit receipt を確認できる。
4. exact Session と budget scope を指定して recalculation を明示開始し、完了を poll
   する。
5. durable な recalculation attempt/retry history、Session estimate
   history/predecessor delta、currency・registry version ごとに分離された range
   total と UTC daily trend、analytics component group、coverage、budget
   `receipt` / `suppression` / `no_match` を確認する。

画面は loading / empty / incomplete / running / failed / unavailable / stale を
分けて表示します。incomplete analytics は exact cap / lower-bound state を示し、
global zero / total / latest / top result として表示しません。
active estimate は timestamp が最新の行ではなく exact contiguous head だけです。
missing、partial、not-estimable、failed、unavailable、stale はゼロではなく、otherwise
eligible な Session の coverage denominator に残ります。ineligible Session は分母へ
入りません。explicit estimated zero だけが covered zero です。currency は
分離し、partial の既知 component subtotal は provisional であって lower bound や
actual cost ではありません。

Cost の catalog/configuration/estimate/analytics API は metadata-only で、
`--sanitized-only` でも利用できます。人向けの `/costs` 画面は raw-default だけです。
API はすべて valid loopback Host、same-origin、
`Cache-Control: no-store` を要求し、POST は strict JSON と
`x-monitor-csrf: local-monitor` が必要です。sanitized export v1 は pricing と alert-v2
を含めません。private runtime backup は pricing/alert-v2、および override から生成して
DB に保存した canonicalized catalog snapshot（private rate/provenance を含み得る）を
含みます。override path と original source-file bytes は含みません。このため runtime
backup は raw-bearing private artifact として扱ってください。

Budget rule は v1 currency `USD`、warning/critical threshold、minimum coverage
（0..10000 basis points）、および scope-specific window（`session`、UTC calendar
`utc_day`、または `rolling_period` + 2..366 `window_days`）を完全に明示するまで
disabled です。insufficient coverage、empty scope、no covered estimate、
unrepresentable amount は alert ではなく fixed suppression として表示します。
Estimated cost は invoice、billing/chargeback、quality improvement、effect verdict、
または automatic model recommendation ではありません。

## First-trace Doctor

First-trace Doctor は、12 個の明示的な fact 区分を評価し、固定された 20 state から
診断結果を返す、source に依存しない core です。直接呼び出し、Config CLI、Local Monitor
HTTP は同じ `doctor.v1` result を返します。人向けの Doctor UI は設定の「診断」から開き、
additive な `/api/doctor/ui/v1/*` proxy を使います。独立した primary 画面ではありません。

### Fact snapshot を評価する

`doctor evaluate` は 1 個の strict な `DoctorFactSnapshot` JSON file を読みます。リポジトリ
同梱の合成 fixture で CLI 面を確認する例です。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- doctor evaluate `
  --input tests\CopilotAgentObservability.Doctor.Tests\TestData\monitor-not-running.facts.json `
  --json
$LASTEXITCODE
```

stdout は 1 個の canonical `doctor.v1` JSON です。この fixture は有効な、ready ではない診断
なので exit code は `3` です。`success: true` と `code: evaluation_completed` は、入力を
正常に評価できたことだけを表します。telemetry が ready、または最初の実 trace を受信済み、
という意味ではありません。`evaluation.primary_state.state_code` を確認してください。

### Verification window を扱う

CLI verification は start / status / complete / cancel の 4 操作です。次の例は 5 分の
確認期間（window）を開始し、状態を読み、明示的に cancel します。

```powershell
$doctorDirectory = Join-Path $PWD 'tmp\doctor-smoke'
New-Item -ItemType Directory -Force $doctorDirectory | Out-Null
$doctorDatabase = Join-Path $doctorDirectory 'doctor.db'
$expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'")

$startJson = dotnet run --project src\CopilotAgentObservability.ConfigCli -- doctor verification start `
  --database $doctorDatabase `
  --source-surface github-copilot `
  --source-adapter vscode `
  --expires-at $expiresAt `
  --json
if ($LASTEXITCODE -ne 0) { throw 'Doctor verification start failed.' }

$start = $startJson | ConvertFrom-Json
$verificationId = $start.verification.verification_id
$revision = $start.verification.revision

dotnet run --project src\CopilotAgentObservability.ConfigCli -- doctor verification status `
  --database $doctorDatabase --verification-id $verificationId --json

dotnet run --project src\CopilotAgentObservability.ConfigCli -- doctor verification cancel `
  --database $doctorDatabase --verification-id $verificationId `
  --expected-revision $revision --json
```

complete の public CLI syntax は次のとおりです。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- doctor verification complete `
  --database <database-file> `
  --verification-id <uuid-v7> `
  --expected-revision <positive-integer> `
  --input <complete-input.json> `
  --json
```

complete input は `fact_snapshot` と 1..16 個の `accepted_evidence_refs` を持ち、
`fact_snapshot.observations` は空でなければなりません。参照先候補の class / kind /
source / adapter / timestamp / expiry は呼び出し元が指定せず、store/service が期限切れでない
既存候補から信頼できる観測を組み立てます。現時点の Doctor 公開面には候補用の command
や route はありません。GitHub Copilot / Claude Code 向けの source 別候補
生成、および proxy / UI は後続実装の範囲であり、現時点では実運用の手順としては
未検証です。

### Local Monitor HTTP routes

| Method / route | 入力 |
|---|---|
| `POST /api/doctor/evaluations` | 1 個の `DoctorFactSnapshot` |
| `POST /api/doctor/verifications` | `source_surface`、optional `source_adapter`、`expires_at` |
| `GET /api/doctor/verifications/{verificationId}` | canonical lowercase UUIDv7 path parameter |
| `POST /api/doctor/verifications/{verificationId}/complete` | `expected_revision`、empty observations の `fact_snapshot`、`accepted_evidence_refs` |
| `POST /api/doctor/verifications/{verificationId}/cancel` | `expected_revision` |

state-changing な start / complete / cancel は、同一 origin から送信し、exact header
`x-monitor-csrf: local-monitor` を付ける必要があります。値は case-sensitive です。browser
request の `Sec-Fetch-Site` は `same-origin` または `none` でなければならず、`Origin` を
送る場合は request 自身の scheme / host / port と一致しなければなりません。evaluation と
status は state を変更しないため、この CSRF header を要求しません。すべての Doctor
response（error を含む）は `Cache-Control: no-store` を持ち、sanitized metadata だけを
返します。raw telemetry、prompt / response / tool body、PII、credential、authorization、
local/database path、rejected body、exception detail は返しません。

setup の静的成功、`doctor evaluate` の処理成功、verification start による確認期間の作成は、
いずれも最初の実 trace を保証しません。既存の実 source 候補を明示選択した complete
が `verification_completed` となり、評価の primary state が `first_trace_ready` になった場合
だけが Doctor の first-trace verification 完了です。

## モックデータで試す

Copilot を使わなくても、リポジトリ同梱の合成モックデータで技術的なトレース詳細を試せます。
このデータは Local Monitor v1 の全セッション操作や比較を保証するものではありません。
合成モックや空の AI 履歴から、インストール済みの Session AI レポート復元成功とは
言えません。モックデータは完全な合成データで（trace id は `demo-` プレフィックス、
`user.email` はダミー値）、実プロンプトや PII を含みません。

```powershell
# ターミナル A — 使い捨て DB でモニターを起動
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db tmp\monitor-demo\monitor.db --url http://127.0.0.1:4320

# ターミナル B — モックデータを投入
pwsh scripts\demo\seed-monitor-mock-data.ps1 -MonitorUrl http://127.0.0.1:4320
```

投入されるのは 9 トレースです: 3 ターン + 並行ツール + キャッシュトークン入りの
リッチトレース（正常 / 回復済みエラー / 異常終了の 3 種）、エラー一覧用の最小回復ケース、
モデル・クライアント・トークン量を変えたトレース 4 件と単発トレースです。
`/traces/{traceId}` の技術的な詳細、スパンインスペクタなどの確認に使えます。

注意点:

- **1 つの DB につき投入は 1 回**にしてください。同じ DB へ再投入すると同一 trace の
  スパンが重複します。やり直すときは、新しい `--db` パスでモニターを起動し直してから
  再投入してください。
- Copilot 解析ドロワーの実行には、ローカルで利用可能な GitHub Copilot SDK
  （または BYOK provider 設定）が必要です。未設定の場合、解析 run は
  Failed で終了します（ドロワー UI 自体の表示は確認できます）。

## ポートとプロファイルの対応

| クライアント | 生成コマンド | 既定エンドポイント |
|---|---|---|
| VS Code / Copilot CLI（Windows user env） | `install-user-env.ps1` | `http://127.0.0.1:4320` |
| VS Code Copilot Chat（monitor） | `profile-vscode-env --profile raw-local-receiver --target monitor` | `http://127.0.0.1:4320` |
| VS Code Copilot Chat（legacy receiver） | `profile-vscode-env --profile raw-local-receiver` | `http://127.0.0.1:4319` |
| GitHub Copilot CLI | `profile-copilot-cli-env --profile raw-local-receiver` | `http://127.0.0.1:4319` |

CLI の既定は `4319`（ConfigCli receiver）です。モニター（4320）に向けるには
`OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320` を上書きしてください。

## 画面ガイド

### リポジトリを選ぶ

`/` のカードには表示名、アクティブな割り当て済みセッション数、最後に記録された時刻を表示します。
同名でも別のリポジトリとして扱います。「すべてのセッション」では全体を、
「リポジトリ未設定のセッション」では未割り当ての記録を探せます。

「リポジトリを追加」や設定の「リポジトリ」で管理し、Explorer の行の操作から手動で割り当てます。
手動割り当ての変更・解除・自動割り当てへの復帰は元のテレメトリを書き換えません。
Copilot CLI では exact native Session の bounded `workspace.yaml` にある明示的な CWD を Git の照会場所としてだけ使用します。GitHub remote が一意ならそれを使い、なければ Git common directory から path を保存しないローカル識別子を生成します。名前、CWD や path の文字列、指示文、時刻の近さから所属を推測することはありません。

### Session Explorer

一覧の列は「セッション / 状態 / 要約 / 入力・出力トークン / 開始 / 最終観測」です。
指示のラベル、スキル、ツールの検索に加え、「期間（開始 / 最終観測）」、取得元、モデル、
状態、記録の有無で絞り込み、「次のページ」で続きを開きます。行を開くとセッション詳細へ
移動します。検索語とモデル条件は現在のページだけに保持され、再読み込みや戻る・進むで
リセットされます。
ネイティブの開始が無い行は「最終観測」を示します。「観測活動」は確定ライフサイクルとは
別の観測区間です。「状態未観測」はネイティブ時刻が未観測な状態であり、観測活動や実行時間が
欠けていることではありません。

「要約」はスキル、ツール、サブエージェント、エラー、再試行の記録を示します。
未記録・取得元の非対応・記録の欠落・取得の安定性が未確認の状態を区別し、欠けた値を 0 にしません。
指示内容が取得できない、または保存期限を過ぎた場合は安全な日付ベースのラベル等を表示します。

### AI を使わずに比較する

1. リポジトリの Session Explorer で「比較を作成」を押します。通常の一覧には選択欄はありません。
2. 各セッションを「基準」または「比較対象」に指定します。それぞれ 1 件以上必要です。
3. 「比較を確認」で対象と除外を確認し、確認画面の「比較を作成」を押します。
   選択が古くなった場合や条件を満たさない場合は、表示された内容に従って選び直します。
4. 比較画面の「指標 / 基準 / 比較対象 / 差」を確認し、根拠のリンクから対象セッションや記録を開きます。

画面は「対象 / トークン / 入力トークンの内訳 / 時間・実行量 / スキル / ツール /
サブエージェント / エラー・再試行 / 比較条件」の順です。値のあるセッションの中央値、最小・最大、
利用可能件数と補足の合計を確認します。欠けた値は計算上の 0 ではありません。
スキル・ツール・サブエージェントの名前付き行は検索とページ送りですべて確認できます。
件数、取得元、モデル、記録範囲が異なる場合は「比較条件」も確認してください。

比較は固定された記録から決定的に計算され、LLM、スコア、順位、改善の判定は使いません。
スキル変更前後を調べる場合も、正確な履歴スナップショットの digest が根拠となる組を使い、
名前や時刻の近さだけで変更前後と判断しないでください。
作成前の選択は再読み込みで失われます。作成後は比較 URL から同じ snapshot を開けますが、
24 時間で期限切れとなり、恒久的な保存済み比較一覧やバックアップには入りません。

### セッション詳細

インスペクタの初期表示は「セッションの概要」です。「最初の指示」、状態（ネイティブ時刻が
未観測のときは **状態未観測**）、取得元、時刻（開始が無いときは **最終観測**）、
アーカイブ状態、取得範囲、「技術情報」を確認します。

「トークン合計」は入力と出力、「入力トークンの内訳」はキャッシュから読み込みと新規入力を示し、
取得できた場合はキャッシュ書き込みも別に表示します。ノードを選んでも上部の値はセッション全体です。

入力・出力が記録されていても、取得元が合計値やキャッシュ書き込みを送っていない場合、欠けた値は
0 にも足し算にもしません。上部の集計はセッション全体が対象のため、一部の実行に記録が欠けていると
欠落表示が残りますが、実行ごとの記録では取得できた入力・出力を確認できます。セッションの完了状態は
Hook の終了信号などの終了記録で判定する別の情報です。終了記録を受信しても、欠けたトークン値が
補われるわけではありません。

階層タイムラインには実行ごとの Agent、スキル、ツール、サブエージェント等を表示し、
最新の実行が初めに展開されます。ノードを選ぶとインスペクタで入出力、状態、関連する記録を確認できます。
親子関係や時刻が不明な場合は推測しません。「技術情報」から正確な証拠へ進めます。
スキルの実行時スナップショットと現在のファイルは別の内容として確認してください。
現在のファイルを過去の実行内容の代わりには扱いません。

タイムラインの `user.message` や「イベント内容」があっても、概要の「最初の指示」が取得済みとは
限りません。Copilot はラベル付きプロンプトをイベント内容として保存することがあります。
指示パートの取得可否とタイムラインのイベント内容は別です。ツール呼び出しだけの
assistant メッセージが非対応でも、応答が無いことではありません。Tool の内容と最終テキストは
別に取得できることがあります。記録状態の「この取得元では記録できません」は取得元の種類そのものでは
ありません。

VS Code の local-git Session は「リポジトリ未設定のセッション」になることがあります。
CLI で一意の GitHub remote が認められると自動割り当てされます。フォルダ名から GitHub
リポジトリを推測しません。

### 任意の AI 分析

「設定」→「AI設定」で GitHub Copilot の認証と利用準備を確認します。
準備ができると「AIで分析」等の操作が現れ、利用者が明示したときだけ実行します。
セッション全体と個別項目の分析では、分析画面でモデル一覧を読み込み、使うモデルを選びます。
一覧は GitHub Copilot の現在のアカウントから明示的に取得します。設定の接続確認や
`CopilotAnalysis:Model` の残存値、ヘルプに載っている例は、利用可能なモデルの証明ではありません。
残っている設定モデルは、一覧に同じ ID があるときだけ初期選択になります。無い場合は選び直します。
選んだモデルはその実行だけに記録され、共有 User Secrets の編集やモニター再起動は不要です。
接続確認の成功は、選択したモデルが使えることや分析成功を保証しません。
分析に失敗した実行は残ります。モデル一覧を更新して選び直し、新しい分析を開始してください。
明示的に AI 操作を開始したときに、選択した内容が GitHub Copilot に送信されます。SQLite ファイル全体や任意の SQL を渡す機能ではありません。

セッション全体のレポートは耐久的な履歴です。URL の `analysis=` は特定の run を指し、
`latest` ではありません。「再分析」は新しい snapshot と結果を作ります。過去の分析は
上書きされませんが、保存期限に従います。ノードの分析結果は一時的で、セッション履歴にも
runtime backup の恒久対象にもなりません。

リポジトリ範囲の AI と比較 AI は無効／延期です。決定的な比較そのものは AI なしで利用できます。
AI の所見と観測された事実は区別し、根拠リンクから確認します。AI の失敗でも通常の調査・比較は続けられます。
AI 実行が無い backup ZIP や合成モックから、インストール済みの Session AI レポート復元成功とは
言えません。

### アーカイブと復元

セッションやリポジトリをアーカイブすると通常の一覧・比較対象から除外されます。
「アーカイブ済みを含む」や「設定」→「アーカイブ」で確認・復元できます。
リポジトリのアーカイブは配下のセッション自体をまとめてアーカイブする操作ではありません。
直接のセッション URL は引き続き開け、新しいデータの到着だけでは復元されません。
アーカイブは削除・保存期間・ピン留めとは別で、内容の保存期限は延長しません。

### 統合された設定

ヘッダーの「設定」は「状態 / 受信 / AI設定 / リポジトリ / アーカイブ / 保存・バックアップ / 診断」を開きます。
受信状態からは同じモーダルの「受信」へ進みます。Escape または閉じる操作で元の画面へ戻れます。
受信の endpoint や起動・受信状況を確認し、詳細診断、保存・バックアップ等の個別操作へ進めます。
`/?settings=diagnostics` のように特定セクションを直接開くこともできます。

### 技術的なトレース詳細（フロー / waterfall + キャッシュ列）

`/traces/{traceId}` は正確なトレースの技術調査用です。以下はセッション詳細とは別の画面です。

trace を開くと、パンくず・プロンプト見出し・状態ピル（正常 / エラー · 回復済み /
エラー · 異常終了）・トークン合計（キャッシュ / 入力 / 出力の内訳）の下に、
「実行の流れ」が表示されます。旧タブ構成（概要 / タイムライン / ツリー・フロー /
キャッシュ）は廃止され、**フロー | waterfall** のセグメント切替 + 右列の常設
キャッシュ列という 1 画面構成になりました。

- **フロー**: ターンカード（ターン番号 · 意図ラベル · トークン · cache% · 所要）を
  時系列に並べ、ツール呼出をカードで表現します。時間の重なる並行ツールは
  「⑂ 並行 N 件」グループとして横並びに、失敗 → 再試行は「✕ 失敗 ·
  種別」「回復済み → 再試行あり」のペアとして表示します。
- **waterfall**: 時間軸に沿ったバー表示です。並行グループは `⑂ 並行 N 件` 見出しと
  `├─` / `└─` プレフィックスで表現し、tokens 列は LLM ターンにのみ値が入ります。
- **キャッシュ列**（エラーのない trace）: 読取率、キャッシュ読取 / 作成、
  未キャッシュ入力、実効入力換算、ターン別キャッシュ読取率のバーを常設表示します。
- ビュー選択とスパン選択は URL（`?view=waterfall&span=...`）に保存され、
  リロードや共有で復元されます。

### スパンインスペクタ

フローまたは waterfall のスパンをクリックすると、右列がスパンインスペクタに
切り替わります（✕ / Esc / 同一スパン再クリックで閉じて元の列に戻ります）。

- **整形タブ**（既定）: LLM スパンは入力の構成（メッセージ）とトークン内訳、
  ツールスパンは引数・結果のプレビュー、共通でスパン id / 親スパン / 開始・終了の
  メタを表示します。
- **raw タブ**: `GET /traces/{traceId}/spans/{spanId}/detail` から取得した
  OTLP span JSON 全文を表示します（「JSON をコピー」付き）。整形抽出が
  できないスパンでも raw タブは常に機能します。
- `--sanitized-only` では画面自体を登録せず、detail route も空 `404` + `no-store` です。

### エラー解析モード

エラーを含む trace を開くと、詳細画面がエラー解析モードになります。

- 見出し下の状態ピルが「エラー · 回復済み」または「エラー · 異常終了」になります。
  回復済み = 失敗の後に成功があった trace、異常終了 = 最後のスパンが失敗した trace です。
- エラー要約ストリップ（例: 「エラー 2件 — 1件は回復済み — 1件が原因でトレースが
  異常終了」）と「最初のエラーへ」ボタンが表示され、フローは「エラーのみ」表示が
  既定で ON になります。
- 右列はキャッシュ列の代わりにエラーパネルになり、エラー一覧（回復済み = 琥珀 /
  未回復 = 赤）、エラー詳細（span id・種別・発生ターン・モデル・例外メッセージ）、
  「原因の手がかり — 入力トークンの推移」（128K 上限の赤破線付きターン別バー）を
  表示します。エラー行をクリックするとフロー側の該当カードが選択されます。

### Copilot 解析ドロワー

詳細画面ヘッダーの「Copilot で解析」で右からドロワーが開きます（詳細は
[Copilot raw analysis](#copilot-raw-analysis) を参照）。観点（トークン / キャッシュ /
エラー / 遅延 / ツール利用 / エージェントの流れ / 指示診断）を選んで実行すると、所見を表示します。
明示的な解析実行時、選択した記録内容を GitHub Copilot へ送信します。所見に対しては
サジェストチップまたは自由入力でチャット形式の**追い質問**ができます。追い質問は
新規 analysis run として過去の Q&A を再送する方式（履歴再送。D045）で、会話履歴が
server に永続化されることはありません。

ドロワーには「明示的な解析実行時、選択した記録内容を GitHub Copilot へ送信します」という
データ境界の表示が常にあります。`--sanitized-only` では画面、ボタン、ドロワーを登録しません。

### サニタイズ済み証拠の取り込み

`/sanitized-import` では、出力されたサニタイズ済みエビデンス bundle（.zip）を選択し、厳密な archive / checksum /
scanner 検証と現在の database に対する差分を preview してから明示的に確定できます。
別の file を選ぶと既存 preview は無効になります。競合、または古くなった preview は確定できず、
既存 record を上書きしません。確定後は件数に上限のある取り込み履歴を同じ画面で確認できます。

この画面は raw telemetry、Session、alert lifecycle、backup を復元せず、サニタイズ済みのデータ構造と
由来情報 / graph だけを専用 table に保存します。選択した bundle bytes や
digest を browser storage に保存しません。画面と API は same-origin、`Cache-Control: no-store`、
Host ヘッダー検証を強制し、POST は CSRF header を必要とします。検証成功は archive の内部
整合性を示しますが、作成者、署名、権限、source store の由来を証明しません。

### 診断

ヘッダーの「設定」→「診断」から `/diagnostics` を開きます。
受信状態は同じ設定モーダルの「受信」で確認できます。
取り込み履歴は `/diagnostics#ingestion-history` からも開けます。

診断ページでは、パイプライン各段の詳細、コンポーネント確認（loopback bind / DB /
migration / writer / projection worker / ingestion queue）、readiness しきい値の実効値、
取り込み履歴（raw record と trace の対応、sanitized metadata のみ）を確認できます。
「リポジトリメタデータ診断」では、最近の受信データに含まれる属性キー、件数、
`resource` / `span` / `event` のスコープ、分類だけを確認できます。属性値、リポジトリ名、
URL、owner、ローカルパス、ユーザー情報は表示されません。`--sanitized-only` では
診断画面と Doctor UI を登録せず空 `404` + `no-store` を返しますが、対応する
sanitized machine API は利用できます。

状態は `metadata_present`、`url_fallback_used`、`metadata_not_present`、
`unsupported_candidate_present`、`unsafe_value_rejected` の 5 種類です。
`vcs.repository.name` が最優先です。これが存在せず、credential・query・fragment などを
含まない canonical GitHub HTTPS URL の `vcs.repository.url.full` だけがある場合に限り、
repository segment をラベルとして使用します。名前が危険な場合や、metadata 自体がない
場合に prompt、CWD、path、時刻の近さからラベルを推測することはありません。

### 履歴インポート

診断ページの明示的なカードから `/historical-import` を開きます。
ページを開いただけで source の検索や読み取りは行われません。利用者が
source と exact reference を選択し、表示された probe 範囲に同意した場合だけ、
preview が実行されます。

フローは「source 選択 → 同意 → preview → 明示確認 → 進行状態 → 結果」です。
preview は source / tier / adapter / version、読み取り risk、利用可能な件数と
`unavailable` の件数、duplicate / conflict、completeness / 欠落 capability、
merge 根拠、retention 影響、除外 reason を表示します。`unavailable` は 0 では
ありません。source から権威ある日時や Session / Event identity を得られない場合、
日付範囲と new Session / Event 数は `unavailable` のままです。

現在の GitHub Copilot CLI / Claude Code profile は exact な fixture 固定 format が未承認のため、
content を読まず `eligible = 0` を返します。この preview は正常な fail-closed
結果であり、confirmation / import ボタンは無効です。fixture や JSONL 形状から
対応可否を推測することはありません。

将来の対応済み metadata-only import は、Session / Run / Event / trace / 時刻を合成せず、
`partial` / `historical_summary_only` の historical observation として保存されます。
`content_state=not_captured` のため retention item は作られません。`live` tab は
既存 Session 一覧、`historical` tab は別の observation 一覧であり、identity は統合されません。
historical だけの trace 操作は無効です。exact に既存 Session へ結合された場合だけ、
その Session への移動リンクを表示できます。

workflow v1 は content import を受け付けません。将来 content を扱う場合も、別の
対応契約と同意を経て既存の `session_event_content` / retention workflow を使う必要があり、
履歴インポート専用の raw store や別の pin / delete 経路は追加しません。

`--sanitized-only` でも metadata-only の machine API は利用できますが、履歴 import
ページは登録されません。選択したローカルパス、raw source、候補 / source-record key、
confirmation / idempotency 値は、preview、結果、履歴、ログ、スクリーンショットに
表示されません。

### Claude Code の source diagnostics

Claude Code の取り込みでは、source surface / version / adapter / schema
fingerprint と、構造上の互換性状態が trace と Session の metadata に表示されます。
詳細な履歴は `GET /api/monitor/source-diagnostics?after&limit` で確認できます。
この endpoint は不透明な ID、件数に上限のある unknown 件数、reason code、次の操作だけを返し、
prompt / response / tool payload や例外本文は返しません。source の互換性状態は
`/health/ready` の status、しきい値、degraded reason を変更しません。

| 状態 | reason / 次の操作 |
|---|---|
| `supported` | reason なし / `none` |
| `supported_with_unknown_fields` | `unknown_fields_observed` / `review_unknown_fields` |
| `schema_drift_detected` | `schema_drift_detected` / `capture_fixture_and_review_mapping` |
| `unsupported_source_version` | `unsupported_source_version` / `use_compatible_source_or_update_adapter` |
| `recognized_record_drop_detected` | `recognized_record_drop_detected` / `restore_mapping_or_update_versioned_golden` |
| `adapter_failure` | `adapter_parse_failure` は `validate_payload_and_protocol`、`adapter_exception` は `inspect_sanitized_adapter_failure` |

検証済み version は evidence として記録されますが、受信を許可する allowlist では
ありません。未検証 version でも既知 fingerprint なら処理されます。新しい fingerprint
はデータを捨てず、`schema_drift_detected` として保持し、fixture と mapping を確認します。
既知の非互換、または必須 signal の欠落だけが `unsupported_source_version` です。

#### content の注意

`content_state` は `available`、`not_captured`、`redacted`、`unsupported` のいずれかです。
これは source が content を capture した状態であり、読み取り・転送・保存・表示の権限を
与えるものではありません。raw content は既存の loopback / same-origin / no-store /
retention / secret-filter 境界と `--sanitized-only` の制御に従う local runtime data です。
content が無い場合も値を推測して埋めません。

#### Claude の binding と未解決表示

OTel が trace/span の identity・親子関係・timing を、Hook が native session lifecycle と
明示的な event identity を所有します。binding は同一の native session ID、明示的な
resume / 引き継ぎ、またはバイト一致する trace context のいずれかだけで成立します。
repository、cwd、process、transcript path、timestamp の近さは binding 根拠ではありません。
親子関係が欠落または曖昧な Claude hierarchy は `unresolved` のまま表示されます。

現行 Session DTO は完全な trace context を持たず `trace_id` のみを扱うため、
バイト一致の trace-context binding は未完了です。共通 trace ID だけでは `exact_linked`
にせず、完全な DTO が追加されるまで `hook_only` / `otel_only` または未解決として扱います。

#### live validation の範囲

Claude Code の interactive、print、Agent SDK は独立した producer surface として
扱います。一つの surface の観測結果を別 surface の証拠に代用しません。現在の
検証済み範囲と blocker は [Claude Code source schema drift contract](../specifications/interfaces/source-schema-drift-claude-code.md)
および versioned source-capability inventory を正本とします。live validation では
raw payload、PII、credential、local path を repository-safe 出力へ記録しません。

## raw body 表示（既定）

raw body（tool arguments / results、sub-agent instructions / responses、system prompt）と
PII（`user.id` / `user.email`）は **既定で表示されます**。trace-detail page（スパン
インスペクタの raw タブと raw OTLP ペイロードセクション）に描画され、
`GET /traces/{rawRecordId}/raw` でも個別の raw OTLP JSON を確認できます。
Session Explorer とセッション詳細では、取得・保存状態が許す範囲で指示ラベルを表示します。
凍結済み `/api/monitor/*` と SSE はプロンプトを含みません。

raw を表示するページと route は次を満たします:


- same-origin アクセスのみ（cross-site は `403`）
- `Cache-Control: no-store`
- HTML エスケープされた、実行されない text として描画（スクリプト実行なし）

`--sanitized-only` を付けて起動すると receiver-only になります。health と
`POST /v1/traces`、対応する machine API は残ります。セッション画面 / trace 詳細を含む
Razor Pages、human static assets、Copilot 解析ドロワー、Doctor UI、runtime-backup の
Web route、`/api/local-monitor/v1/*` は登録されません。これらの人向け GET / HEAD は
空 body の `404` と `Cache-Control: no-store` を返します。画面ごとの縮退 UI はありません。
一致しない `/traces/.../raw` のような raw 風 path は、空 404 ではなく既存の
`unsupported_endpoint` JSON フォールバックを返します。

raw store や表示内容には prompt / response / tool 情報が含まれる場合があります。
raw store ファイル（`data\monitor.db` 等）を repository に commit しないでください。

## SSE によるリアルタイム更新

`GET /events`（`text/event-stream`）を購読すると、新しい取り込みが projection されるたびに
通知（`data: {}`）が届きます。この通知は変更の通知だけで、セッション本体や raw 内容を返しません。

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

## runtime backup と offline restore

`http://127.0.0.1:4320/backup-restore` では、稼働中の Local Monitor DB から
SQLite online backup を作成し、ダウンロードできます。選択した backup archive の
互換性と復元前条件も同じ画面で確認できますが、Web UI から restore は実行できません。
restore は **意図した対象**を停止したあとの CLI だけです。

runtime backup は prompt / response / tool arguments / results を含み得る raw backup です。
repository-safe ではなく、Retention cleanup の対象にもなりません。作成した ZIP は
operator-owned file として安全な private storage に保管し、不要になったら利用者が削除して
ください。`retention_backup_not_purged` はこの責任境界を示す固定 warning です。
inspect の `row_counts` で `local_ai_runs` が 0 の ZIP は、非 AI データの復元だけを示します。
Session AI レポートの復元成功ではありません。ノード分析は一時的で、この backup の恒久対象では
ありません。

展開した Release ZIP から、同梱の self-contained Config CLI を使う例。`--database` は
操作対象の DB を明示します。

```powershell
$cli = '.\app\config-cli\CopilotAgentObservability.ConfigCli.exe'
$db = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\raw-store.db'
& $cli runtime-backup create --database $db --output C:\private\local-monitor-backup.zip
& $cli runtime-backup inspect --bundle C:\private\local-monitor-backup.zip
& $cli runtime-backup preview --bundle C:\private\local-monitor-backup.zip --database $db
```

restore は意図した Local Monitor を停止してから実行します。既存 DB を置換する場合は、既定で
`runtime-backups/` に pre-restore backup が作られます。preview が
`requires_confirmation=true` を返すのは、現在は欠落している非終端 raw source を backup
から再導入する場合だけです。そのときだけ表示された `confirmation_digest` を同じ archive
に対して明示的に渡せます。現在の tombstone / read denial は confirmation で解除できず、
復元先へ必ず引き継がれます。`stop.ps1` は一時的な process state を削除するため、停止前に
同じ対象へ戻す `Url` / `DbPath` / `InstallRoot` / `SanitizedOnly` を明示してください。

次の例は **日常 instance の保守**です。`-RuntimeRoot` を省略した `stop.ps1 -Force` は
既定の日常 instance を停止します。隔離検証の手順として使わないでください。
start の `-Url` / `-DbPath` / `-InstallRoot` と restore の `--bundle` / `--database` は
同じ日常 instance を指します。

```powershell
$cli = '.\app\config-cli\CopilotAgentObservability.ConfigCli.exe'
$stopScript = '.\scripts\stop.ps1'
$startScript = '.\scripts\start.ps1'
$monitorUrl = 'http://127.0.0.1:4320'
$db = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\raw-store.db'
$installRoot = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\app'
$sanitizedOnly = $false # receiver-only instance を復元するときだけ $true
$startParameters = @{
    Mode = 'Published'
    Url = $monitorUrl
    DbPath = $db
    InstallRoot = $installRoot
    SanitizedOnly = $sanitizedOnly
    NoBrowser = $true
    WaitReady = $true
}

& $stopScript -Force
$stopExitCode = $LASTEXITCODE
if ($stopExitCode -ne 0) {
    exit $stopExitCode
}

& $cli runtime-backup restore --bundle C:\private\local-monitor-backup.zip --database $db
$restoreExitCode = $LASTEXITCODE
if ($restoreExitCode -ne 0) {
    exit $restoreExitCode
}

& $startScript @startParameters
$startExitCode = $LASTEXITCODE
if ($startExitCode -ne 0) {
    exit $startExitCode
}
```

隔離 instance の検証では、`start.ps1` / `status.ps1` / `stop.ps1` に同じ完全修飾
`-RuntimeRoot` を渡し、start の `-Url` / `-DbPath` / `-InstallRoot` と restore の
`--bundle` / `--database` が同じ隔離 DB を指すことを確認します。日常 instance の URL や
既定 DB を流用しないでください。

```powershell
$cli = '.\app\config-cli\CopilotAgentObservability.ConfigCli.exe'
$stopScript = '.\scripts\stop.ps1'
$startScript = '.\scripts\start.ps1'
$runtimeRoot = 'C:\private\local-monitor-isolated'
$monitorUrl = 'http://127.0.0.1:4321'
$db = Join-Path $runtimeRoot 'raw-store.db'
$installRoot = Join-Path $runtimeRoot 'app'
$bundle = 'C:\private\local-monitor-isolated-backup.zip'
$sanitizedOnly = $false # receiver-only instance を復元するときだけ $true
$startParameters = @{
    Mode = 'Published'
    RuntimeRoot = $runtimeRoot
    Url = $monitorUrl
    DbPath = $db
    InstallRoot = $installRoot
    SanitizedOnly = $sanitizedOnly
    NoBrowser = $true
    WaitReady = $true
}

& $stopScript -RuntimeRoot $runtimeRoot -Force
$stopExitCode = $LASTEXITCODE
if ($stopExitCode -ne 0) {
    exit $stopExitCode
}

& $cli runtime-backup restore --bundle $bundle --database $db
$restoreExitCode = $LASTEXITCODE
if ($restoreExitCode -ne 0) {
    exit $restoreExitCode
}

& $startScript @startParameters
$startExitCode = $LASTEXITCODE
if ($startExitCode -ne 0) {
    exit $startExitCode
}
```

`$LASTEXITCODE` は各コマンドの直後に保存します。restore が非 0 なら Published start は
実行しません。`-WaitReady` を指定した start は `/health/ready` が canonical `ready` または
許容される `degraded` を返した場合だけ成功し、`not_ready` または到達不能なら失敗します。

再導入を明示的に許可する場合のみ、restore 呼び出しへ
`--allow-resurrection --confirmation <confirmation-digest>` を追加します。別マシンでは対応する
Local Monitor release を先に install し、restore 後に setup を再実行してから起動し、
`status.ps1`、`/health/ready`、Doctor を確認してください。setup ownership、credentials、
実行ファイル、PID/state/log は host-bound または ephemeral なので backup には含まれません。

## データ安全

- `data\monitor.db`、`data\monitor-*.db` は local runtime artifact です。repository に commit しないでください。
- Task Scheduler 起動時の既定 DB / logs / state は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下に保存されます。これらも repository に commit しないでください。
- 既定で raw body（prompt / response / tool arguments / results）と PII が表示されます。画面を持たない receiver-only 運用が必要な場合は `--sanitized-only` を付けて起動できます。人向け画面の縮退表示ではありません。
- モニターはループバックにのみバインドします。非ループバック URL は起動時に拒否されます。
- ログに raw prompt / response / tool arguments / results は出力しません。

詳細は [Data Safety](data-safety.md) と
[docs/specifications/security-data-boundaries.md](../specifications/security-data-boundaries.md) を参照してください。

## Copilot raw analysis

この節は技術的なトレース詳細の既存解析機能です。セッションや比較の v1 AI 分析は
[任意の AI 分析](#任意の-ai-分析)を参照してください。以下の BYOK 設定は v1 の provider 選択機能ではありません。

raw default の Local Monitor では、trace 詳細の「Copilot で解析」ドロワーから
raw analysis run を開始できます。これは captured raw trace / raw record / span context を
.NET GitHub Copilot SDK analysis service に渡し、ローカル診断として分析する機能です。

### 使い方

1. `--sanitized-only` を付けずに Local Monitor を起動します。
2. `/traces/{traceId}` を開き、ヘッダーの「Copilot で解析」を押します。
3. 観点を選んで「解析を実行」します。
   - トークン（tokens）
   - キャッシュ（cache）
   - エラー（errors）
   - 遅延（latency）
   - ツール利用（tool-usage）
   - エージェントの流れ（agent-flow）
   - 指示診断（instruction-diagnosis）
4. 生成された run id の状態を Local Monitor が polling し、.NET SDK analysis
   result をローカル runtime data としてドロワー内に表示します。
5. 所見に対してサジェストチップまたは自由入力で追い質問できます。各追い質問は
   過去の Q&A を含めて再送する新規 run です（履歴再送。D045）。

実行にはローカルで利用可能な GitHub Copilot SDK（または下記 BYOK provider 設定）が
必要です。利用できない場合、run は Failed で終了します（UI は失敗メッセージを表示）。

スパンインスペクタの「このスパンを Copilot に聞く」からも、選択スパンを文脈にした
解析を開始できます。

`--sanitized-only` では raw analysis UI と routes は表示・提供されません。

### Copilot raw analysis BYOK

Local Monitor は .NET GitHub Copilot SDK の BYOK provider 設定を
`CopilotAnalysis:*` から読みます。Secret Manager で設定する例:

```powershell
dotnet user-secrets init --project src\CopilotAgentObservability.LocalMonitor
dotnet user-secrets set "CopilotAnalysis:Enabled" "true" --project src\CopilotAgentObservability.LocalMonitor
dotnet user-secrets set "CopilotAnalysis:Model" "<model-id>" --project src\CopilotAgentObservability.LocalMonitor
dotnet user-secrets set "CopilotAnalysis:Provider:Type" "openai" --project src\CopilotAgentObservability.LocalMonitor
dotnet user-secrets set "CopilotAnalysis:Provider:BaseUrl" "https://<endpoint>/v1" --project src\CopilotAgentObservability.LocalMonitor
dotnet user-secrets set "CopilotAnalysis:Provider:WireApi" "completions" --project src\CopilotAgentObservability.LocalMonitor
dotnet user-secrets set "CopilotAnalysis:Provider:ApiKey" "<api-key>" --project src\CopilotAgentObservability.LocalMonitor
```

`CopilotAnalysis:BaseDirectory` は Copilot SDK runtime state の書き込み可能な親
directory です。指定しない場合、Local Monitor は書き込み可能な一時的なローカル親
directory を使います。Local Monitor は run ごとに不透明な SDK 子 directory を作成し、
その子だけを SDK に渡します。cleanup は設定済みの親や兄弟を対象にしません。
API key は analysis events、UI、repository-safe summary には出力しません。

`CopilotAnalysis:TimeoutSeconds`（既定 `60`）は 1 回の解析実行に許容する SDK
send/wait タイムアウト秒です。実際の Copilot CLI トレースは raw payload が
大きく、reasoning 系 BYOK モデルでは既定 60 秒で完走しないことがあります。
その場合は例えば `600` を設定してください:

```powershell
dotnet user-secrets set "CopilotAnalysis:TimeoutSeconds" "600" --project src\CopilotAgentObservability.LocalMonitor
```

### 出力境界

- Raw analysis result は local runtime data です。
- GitHub Issue / docs / dashboard に出す場合は `safe-summary` route の
  repository-safe summary だけを使います。
- raw prompt / response / full tool arguments / full tool results / PII /
  credentials / local sensitive path は repository-safe summary に含めません。

## GitHub Copilot app Canvas adapter

Local Ingestion Monitor は GitHub Copilot app extension（Canvas adapter）経由で
Copilot CLI から参照できます。Canvas extension は
`.github/extensions/otel-monitor-canvas/extension.mjs` に配置された
プロジェクト単位の extension で、モニター UI を再実装せず、既存の
`/api/monitor/*` API と `/health/ready` から範囲を限った action response を返します。

### Local Monitor 姿勢

Canvas adapter は通常起動の raw default Local Monitor と併用できます。
`--sanitized-only` は Canvas 用の必須設定ではなく、receiver-only にしたい場合の
任意モードです。このモードでは Local Monitor の人向け画面、Doctor UI、runtime-backup
の Web route は空 `404` + `no-store` になりますが、Canvas adapter が使用する
sanitized machine API は利用できます。一致しない raw 風 path の `unsupported_endpoint`
JSON フォールバックは変わりません。

### 必要なもの

- GitHub Copilot app（Canvas extension runtime をサポートするバージョン）
- Local Ingestion Monitor を loopback で起動済み

### 使い方

1. モニターを起動します。

   ```powershell
   dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor.db --url http://127.0.0.1:4320
   ```

   必要に応じて `--sanitized-only` を追加すると receiver-only ホストになります。
   人向け画面の縮退表示ではなく、人向け route 自体が空 `404` + `no-store` です。

   `/health/ready` が `200 ready` を返すことを確認してください。

2. Copilot app で Canvas extension を開きます。Copilot app は
   `.github/extensions/otel-monitor-canvas/` を自動検出します。Canvas id は
   `otel-monitor` です。

3. `open()` が完了すると、拡張所有の loopback Session Workspace
   （`http://127.0.0.1:<port>/?t=<token>`）が開きます。左側でこの会話に
   exact に結び付いた Session、最近の Session、未紐付け Session を選べます。
   Review / Evidence / Improve / Compare の 4 tab があります。Compare では、適用済み
   proposal revision に対する効果比較を利用者が明示的に確定します。従来の trace 分析画面は
   `/analysis` にあります。

4. ボタンを押すと、Copilot に範囲を限った分析指示が送信されます。Copilot は
   Canvas actions（`monitor_health`、`list_recent_traces`、`get_trace_summary`、
   `get_trace_span_tree`、`get_cache_summary`）を呼び出して trace を分析します。

### Evidence tab

Evidence は選択 Session の run に exact に結び付いた trace だけを表示します。
各 trace の Agent forest は別々に保たれ、Agent / Subagent の親子関係、caller、
parallel、exact / 推定 / 判定不能は Local Monitor の Agent graph をそのまま使います。
Session event は run が trace に結び付いていても常に `Session / unowned` で、Agent
への所属を推測しません。

下部のタイムラインは sanitized OTel spans と Session event metadata を時刻順に
表示します。右の Inspector は選択した Agent、span、event の sanitized fields と
`content_state` を表示します。型付きの Skill 名/パス/バージョン、test/review 結果が
ない場合は「利用不可」です。tool 名や出力から合否や Skill を推測しません。

exact に結び付いた trace がない場合も Session event timeline は利用できます。Agent graph
は利用不可と明示されます。Monitor が `400` / `404` / `503` を返した trace は error
として表示され、別 trace や最新 trace への代替表示はありません。Agent graph
と spans は独立して取得されるため、一方だけ失敗した場合も、取得できた側の証拠は
残り、失敗した側だけにエラーが表示されます。

### Compare tab（効果比較）

Compare は、1 つの exact な proposal revision と、その revision に結び付く有効な
application receipt を対象に、利用者が明示確定した適用前 / 適用後の集団を比較します。
対象の application は `applied` 済みで pending / rollback ではなく、登録 root 内の全 target が
記録済みの適用後 SHA-256 と一致している必要があります。proposal revision の不一致、
復旧失敗、rollback、適用後 hash の古さのいずれでも比較結果は `insufficient_evidence`
です。path、source、diff、snapshot は Compare に表示・返却されません。

客観品質の receipt は、exact な Session / Run / trace と、同じ scope に解決する
証拠参照を固定して保存します。receipt には pass / fail、normal / severe、evaluator ID /
version、criterion、case key、recorded time が含まれます。repository、時刻近接、prompt 類似、
正規化 measurement の未紐付け `success_status` は証拠や Session の結合根拠にはなりません。

候補は参考提示です。候補を含めることも判定を作ることも自動では行われません。
利用者が含める Session を `pre` / `post` として case key 付きで確定し、除外する Session には
`not_comparable`、`wrong_case`、`missing_evidence`、`overlaps_application`、`user_excluded` のいずれかを
選びます。含める Session は exact に結び付き、終端状態で、`full` でなければならず、pre は
`applied_at` 以前に終了、post は `applied_at` 以後に開始している必要があります。境界をまたぐ
Session は含められません。case-key の詳細表示と summary は、同じ保存済み Session / 証拠行を
表示します。

判定には pre / post 各 3 Session 以上が必要です。含めた各 Session は人手評価
（`expected` / `problem`）または不変の客観 receipt による決定的な品質証拠を少なくとも
1 つ必要とし、missing、partial、conflicting、out-of-scope の証拠は補完せず
`insufficient_evidence` とします。客観評価の severe fail が post に 1 つでもあれば
`regressed` です。

判定は `improved`、`no_change`、`regressed`、`insufficient_evidence` の 4 種だけです。
まず severe と品質合格率を比較し、品質が同等のときだけ duration と total-token の
中央値を比較します。10% ちょうどの改善は実質的な改善、悪化は 10% より大きい場合だけ実質的な悪化です。
丸め表示は判定を変えません。品質より効率を優先したり、単一 score にまとめたりは
しません。

`improved` の効果 receipt 保存と proposal の `verified` 更新は 1 つの SQLite transaction です。
その後の rollback では receipt は履歴として残りますが `invalidated` となり、有効な改善として
表示されません。Compare は loopback / same-origin / CSRF / no-store の明示操作です。Canvas action、
`session.send()`、log、repository-safe な出力に集団 / 比較 payload を渡さず、自動の
Verified、file apply / rollback、git 操作を行いません。

### Canvas actions

| Action | 入力 | 出力 |
|---|---|---|
| `monitor_health` | なし | モニター到達性・readiness 状態・Canvas adapter 診断メッセージ |
| `list_recent_traces` | `limit`（1..50）、`status?`（ok/error）、`model?` | 最近の trace の sanitized メタデータ一覧 |
| `get_trace_summary` | `traceId` | trace 全体サマリー・上位 spans・models・cache 合計 |
| `get_trace_span_tree` | `traceId` | span の親子階層（sanitized）または平坦な診断結果 |
| `get_cache_summary` | `traceId` | cache トークン指標・ターン別内訳・cache hit rate |

全ての action response は範囲を限った DTO です。raw prompt / response body、
tool arguments / results、PII、credential、token、local sensitive path、raw monitor
payload は含まれません。raw の詳細は Local Monitor UI の loopback / same-origin
境界内で扱います。

### セキュリティ境界

- 拡張所有の HTTP server は `127.0.0.1` のみにバインドします。
- ヘルパーページとプロキシ route は起動ごとの token で保護されます。
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
| ingestion が増えない | `install-user-env.ps1` 後に VS Code / terminal / Copilot CLI を再起動したか確認。シェル一時適用の場合は、環境変数を設定したシェルから VS Code を起動したか確認。 |
| `degraded` が続く | 診断（ステータスバッジ → 詳細診断）で `projection_lag_seconds` と `projection_backlog` を確認。 |
| trace 詳細のスパンが重複して見える | 同じ trace id を同じ DB に複数回投入していないか確認（モックデータの再投入など）。新しい `--db` で起動し直すと解消します。 |
| Copilot 解析が Failed で終わる | ローカルで GitHub Copilot SDK が利用可能か、または `CopilotAnalysis:*` の BYOK 設定を確認。 |
| `dotnet run` がビルドエラーで失敗する | 既に同じプロジェクトのプロセスが動いている場合、DLL がロックされます。ビルド済み exe を直接実行してください：`src\CopilotAgentObservability.LocalMonitor\bin\Debug\net10.0\CopilotAgentObservability.LocalMonitor.exe --db data\monitor.db --url http://127.0.0.1:4320` |

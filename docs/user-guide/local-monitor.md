# Local Ingestion Monitor

Local Ingestion Monitor（`CopilotAgentObservability.LocalMonitor`）は、VS Code GitHub
Copilot Chat や GitHub Copilot CLI から送られてくる OTLP HTTP/protobuf テレメトリを
ローカルで受け取り、ブラウザ UI でリアルタイムに確認するための単一プロセスツールです。

Langfuse、Docker Desktop、外部ネットワークは不要です。
ループバック（`127.0.0.1`）にバインドし、同一マシン内でのみ動作します。

## 何が確認できるか

画面は左にサイドバー（**概要 / トレース** の 2 項目）、下部に受信ステータスバッジが
ある構成です。診断はバッジ → ポップオーバー →「詳細診断を開く」の順に開きます
（`/diagnostics` への直接アクセスも可能）。

画面は次の 11 個です。

| 画面 | 開き方 | 内容 |
|---|---|---|
| 概要 | `http://127.0.0.1:4320/` | トークン KPI（実消費 / 実効入力換算 / キャッシュ読取率 / エラー trace）、期間トグル（今日 / 7日 / 30日）、最新の critical alert、モデル別内訳、キャッシュ効率、高コスト trace TOP5、時間帯別トークン、最近のトレース |
| トレース一覧 | `/traces` | テーブル + 右プレビューパネルの一覧詳細構成。プロンプト / モデル / 状態 / 期間で絞り込み、トークン・所要・時刻でソート |
| トレース詳細 | `/traces/{traceId}` | 実行の流れを **フロー / waterfall** セグメントで切替表示。ターンカード、並行ツールのグループ表現、失敗 → 再試行の回復ペア、右列に常設のキャッシュ列 |
| スパンインスペクタ | 詳細画面でスパンをクリック | 右パネルに **整形 / raw** タブ。整形はメッセージ構成・トークン内訳・メタ、raw は OTLP span JSON 全文 |
| エラー解析モード | エラーを含む trace の詳細画面 | エラー要約ストリップ、エラー一覧（回復済み / 未回復）、エラー詳細、入力トークン推移（128K 上限の目安線付き） |
| Copilot 解析ドロワー | 詳細画面の「Copilot で解析」 | 観点を選んで raw trace をローカルの Copilot SDK で解析。チャット形式の追い質問（履歴再送）に対応 |
| Alert Center | 概要の最新 critical alert、トレース詳細の「関連 Alert」、または `/alerts` | frozen alert receipt とその lifecycle を一覧・絞り込みし、根拠となる証拠、再発グループ、抑制状況を確認。acknowledge / dismiss / resolve / reopen を明示操作 |
| 診断 | ステータスバッジ → ポップオーバー →「詳細診断を開く」 | 取り込みパイプライン 4 段の状態、コンポーネント確認、readiness しきい値、取り込み履歴、リポジトリメタデータ診断 |
| 履歴インポート | 診断ページの「履歴インポート」カード → `/historical-import` | 明示的に選択した GitHub Copilot CLI / Claude Code の履歴 source を preview し、対応済み source のみ確認後に import。live Session と historical observation は別 tab で表示 |
| サニタイズ済み証拠の取り込み | `/sanitized-import` へ直接アクセス | frozen v1 ZIP の preview、明示確定、取り込み結果、bounded history。raw telemetry は復元しない |
| runtime backup と restore | 概要の「runtime backup と restore」→ `/backup-restore` | raw-bearing な online backup の作成・ダウンロードと archive の復元前検査。restore は Local Monitor を停止して Config CLI から実行 |

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
| `POST /api/alert-center/v1/evaluations` | 特定の Session + trace を利用者が明示指定する評価。自動評価ではなく、現行 source manifest では receipt を作らず抑制状況を記録 |
| `GET /traces/{traceId}/spans/{spanId}/detail` | スパンインスペクタ用の raw-bearing span 詳細（`--sanitized-only` 時は 404） |
| `/api/sanitized-import/v1/*` | sanitized bundle の preview / 明示取り込み / history（same-origin、POST は CSRF header 必須） |
| `POST /api/runtime-backup/v1/backups` | 稼働中 DB の online backup を作成（exact `{}`、CSRF header 必須） |
| `GET /api/runtime-backup/v1/backups/{backup_id}` | online backup の作成結果を取得 |
| `GET /api/runtime-backup/v1/backups/{backup_id}/archive` | process-owned backup archive をダウンロード |
| `POST /api/runtime-backup/v1/previews` | 選択した backup ZIP の互換性と offline restore 前提を preview（`application/zip`、CSRF header 必須） |
| `/api/doctor/*` | source に依存しない Doctor evaluation / verification の 5 route（後述、UI なし） |
| `/api/historical-import/v1/*` | 履歴 import の preview / confirmation / result / history / observation（sanitized、no-store） |

HTTP restore endpoint はありません。restore は Local Monitor を停止し、Config CLI の
`runtime-backup restore` だけで実行します。

API（`/api/monitor/*`）と SSE は **sanitized metadata のみ** を返します（プロンプトを含みません）。
raw body（tool arguments / results、sub-agent instructions / responses、system prompt）と PII は
既定で trace 詳細ページに表示されます（server 描画、実行されない text）。さらに、トレースを
不透明な TraceId ではなく入力プロンプトで識別できるよう、**概要とトレース一覧でも
代表プロンプトを既定で表示**します（D032。raw store の OTLP から server 側で抽出した、実行されない text。
same-origin / `Cache-Control: no-store` を強制）。`--sanitized-only` を付けて起動すると
metadata-only モードになり、raw 由来の表示（プロンプト、インスペクタの raw タブ、
Copilot 解析ドロワー、raw route）を除外して短縮 TraceId に切り替えます。

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

> `dotnet run` は Web SDK の既定で作業ディレクトリをプロジェクトディレクトリに
> 設定するため、相対 `--db` は `src\CopilotAgentObservability.LocalMonitor\` 基準で
> 解決されます。DB の場所を固定したい場合は絶対パスを指定してください。

オプション:

| オプション | 既定値 | 説明 |
|---|---|---|
| `--db` | `data/raw-store.db` | SQLite raw store のパス |
| `--url` | `http://127.0.0.1:4320` | ループバック bind URL（非ループバックは拒否） |
| `--sanitized-only` | off | metadata-only モード。raw 由来の表示（プロンプト、インスペクタ raw タブ、Copilot 解析ドロワー、raw route）と PII を除外する任意 opt-out。 |
| `--apply-root user_config=<absolute-directory>` | なし | proposal apply で使う明示登録済みのローカル user-config root |
| `--apply-root skill=<absolute-directory>` | なし | proposal apply で使う明示登録済みのローカル Skill root |
| `--apply-root repository=<absolute-directory>` | なし | proposal apply で使う明示登録済みの repository working-tree root |

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
metadata-only の常時起動にしたい場合は `-SanitizedOnly` を付けます。

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -SanitizedOnly -StartNow
```

Task Scheduler 登録 script は VS Code 設定を書き換えません。クライアントを
monitor に向ける設定は、次の Step 2 の user environment script または Config CLI
出力を使います。

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

出力された環境変数を適用してから `gh copilot` コマンドを実行します。

### Step 3 — Copilot を使う

VS Code で Copilot Chat に質問する、または `gh copilot -- -p "..."` を実行します。
モニターはリアルタイムでテレメトリを受信し、ブラウザ UI が自動更新されます。

### Step 4 — ブラウザで確認する

`http://127.0.0.1:4320/`（概要）を開くと、トークン KPI と最近のトレースが表示されます。
受信直後に projection が走り、`/traces`（トレース一覧）に集約された trace 行が現れます。
各トレースは入力プロンプトで識別できます（既定）。

## Alert Center

概要の「最新の critical alert」、トレース詳細の「関連 Alert」、または
`http://127.0.0.1:4320/alerts` から開きます。サイドバーは従来どおり
**概要 / トレース** の2項目で、Alert Center は作業対象の文脈から移動します。

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
結果として表示します。空でも全体の 0 件とは断定せず、概要の項目を「最新」とは
表示せず、「Recurring patterns」も `incomplete_snapshot` のままです。

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

## First-trace Doctor

First-trace Doctor は、12 個の明示的な fact 区分を評価し、固定された 20 state から
診断結果を返す、source に依存しない core です。直接呼び出し、Config CLI、Local Monitor
HTTP は同じ `doctor.v1` result を返します。現時点の Doctor 範囲には proxy、
Razor / JavaScript / Canvas UI はありません。

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

Copilot を使わなくても、リポジトリ同梱の合成モックデータで全画面の動作を確認できます。
モックデータは完全な合成データで（trace id は `demo-` プレフィックス、
`user.email` はダミー値）、実プロンプトや PII を含みません。

```powershell
# ターミナル A — 使い捨て DB でモニターを起動
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db tmp\monitor-demo\monitor.db --url http://127.0.0.1:4320

# ターミナル B — モックデータを投入
pwsh scripts\demo\seed-monitor-mock-data.ps1 -MonitorUrl http://127.0.0.1:4320
```

投入されるのは 9 トレースです: 3 ターン + 並行ツール + キャッシュトークン入りの
リッチトレース（正常 / 回復済みエラー / 異常終了の 3 種）、エラー一覧用の最小回復ケース、
モデル・クライアント・トークン量を変えた一覧用トレース 4 件、概要用の単発トレースです。
概要 KPI、一覧のフィルタ・ソート、詳細のフロー / waterfall、キャッシュ列、
スパンインスペクタ、エラー解析モードがすべて点灯します。

注意点:

- **1 つの DB につき投入は 1 回**にしてください。同じ DB へ再投入すると同一 trace の
  スパンが重複します。やり直すときは、新しい `--db` パスでモニターを起動し直してから
  再投入してください。
- 投入直後は全データが「今日」の受信になるため、概要の期間トグル
  （今日 / 7日 / 30日）はどの期間でも同じ値になります（実運用の初日と同じ挙動です）。
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

### 概要

トークンコストの把握を最優先にした KPI ダッシュボードです。今日 / 7日 / 30日の
期間トグルと、次の KPI を表示します。

- **実消費トークン**: 未キャッシュ入力 + 出力（= 総量 − キャッシュ読取）。
  agent セッションは毎ターン履歴を再送するため、キャッシュ読取込みの総量は
  大半がキャッシュで占められます。ヒーロー数値は実際に新規処理された
  トークンとし、キャッシュ読取込みの総量とキャッシュ読取量は同カードの
  内訳行に表示します。前期間比較も実消費ベースです。
- **実効入力換算**: キャッシュ読取 = 0.1x 換算のコスト近似。
- **キャッシュ読取率**: キャッシュ読取トークン ÷ 入力トークン
  （入力トークンはキャッシュ読取分を含む値）。カードに分子 ÷ 分母の
  内訳行を表示するので、率の根拠を数値で確認できます。
- **エラー trace 数**: クリックでエラーのみの一覧へドリルダウン。

下段にはモデル別トークン内訳、モデル別キャッシュ効率、高コスト trace
TOP5、時間帯別トークン分布、最近のトレースが並びます。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor 概要" src="../assets/screenshots/local-monitor-overview.png">
</p>

### トレース一覧

テーブル + 右プレビューパネル（一覧と詳細を同じ画面で並べる構成）です。行はプロンプト / モデル /
トークン / cache% / 所要 / 時刻で構成され、既定はトークン降順です。上部の検索
（プロンプト・TraceId）、モデル / 状態（正常・エラー・回復済み・異常終了）/ 期間の
フィルタで絞り込めます。行をクリックすると、ページ遷移せずに右パネルへミニ KPI・
トークン構成・コストの大きいスパン TOP3 が表示され、「詳細を開く」で詳細画面へ
進めます。

> プロンプト検索は server 側の TraceId 部分一致 + client 側の読み込み済み行の
> prompt label フィルタです（読み込み済み以外を含む prompt 全文検索は未対応）。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor トレース一覧" src="../assets/screenshots/local-monitor-trace-list.png">
</p>

### トレース詳細（フロー / waterfall + キャッシュ列）

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

<p align="center">
  <img width="900" alt="Local Ingestion Monitor トレース詳細（フロー）" src="../assets/screenshots/local-monitor-trace-detail-flow.png">
</p>

<p align="center">
  <img width="900" alt="Local Ingestion Monitor トレース詳細（waterfall）" src="../assets/screenshots/local-monitor-trace-detail-waterfall.png">
</p>

### スパンインスペクタ

フローまたは waterfall のスパンをクリックすると、右列がスパンインスペクタに
切り替わります（✕ / Esc / 同一スパン再クリックで閉じて元の列に戻ります）。

- **整形タブ**（既定）: LLM スパンは入力の構成（メッセージ）とトークン内訳、
  ツールスパンは引数・結果のプレビュー、共通でスパン id / 親スパン / 開始・終了の
  メタを表示します。
- **raw タブ**: `GET /traces/{traceId}/spans/{spanId}/detail` から取得した
  OTLP span JSON 全文を表示します（「JSON をコピー」付き）。整形抽出が
  できないスパンでも raw タブは常に機能します。
- `--sanitized-only` では raw 由来の詳細は表示されず、sanitized なスパン情報のみに
  なります（detail route 自体が 404）。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor スパンインスペクタ" src="../assets/screenshots/local-monitor-span-inspector.png">
</p>

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

<p align="center">
  <img width="900" alt="Local Ingestion Monitor エラー解析モード" src="../assets/screenshots/local-monitor-error-mode.png">
</p>

### Copilot 解析ドロワー

詳細画面ヘッダーの「Copilot で解析」で右からドロワーが開きます（詳細は
[Copilot raw analysis](#copilot-raw-analysis) を参照）。観点（トークン / キャッシュ /
エラー / 遅延 / ツール利用 / エージェントの流れ / 指示診断）を選んで実行すると、captured raw
trace をローカルの .NET GitHub Copilot SDK で解析し、所見を表示します。所見に対しては
サジェストチップまたは自由入力でチャット形式の**追い質問**ができます。追い質問は
新規 analysis run として過去の Q&A を再送する方式（履歴再送。D045）で、会話履歴が
server に永続化されることはありません。

ドロワーには「ローカル SDK 経由 · raw はローカルから出ません」というデータ境界の
表示が常にあります。`--sanitized-only` ではボタンとドロワー自体が存在しません。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor Copilot 解析ドロワー" src="../assets/screenshots/local-monitor-copilot-drawer.png">
</p>

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

サイドバー下部の受信ステータスバッジ（「正常 · 受信中」等）をクリックすると
ポップオーバーが開き、`/health/ready` の結果と取り込みパイプライン 4 段
（① 受信 / ② 書き込みキュー / ③ Projection / ④ DB · migration）の状態を確認できます。
「詳細診断を開く」で `/diagnostics` へ、「取り込み履歴」で
`/diagnostics#ingestion-history`（履歴セクションが展開された状態）へ進みます。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor 受信ステータスポップオーバー" src="../assets/screenshots/local-monitor-status-popover.png">
</p>

診断ページでは、パイプライン各段の詳細、コンポーネント確認（loopback bind / DB /
migration / writer / projection worker / ingestion queue）、readiness しきい値の実効値、
取り込み履歴（raw record と trace の対応、sanitized metadata のみ）を確認できます。
「リポジトリメタデータ診断」では、最近の受信データに含まれる属性キー、件数、
`resource` / `span` / `event` のスコープ、分類だけを確認できます。属性値、リポジトリ名、
URL、owner、ローカルパス、ユーザー情報は表示されません。このキー専用一覧は
`--sanitized-only` でも利用できます。

状態は `metadata_present`、`url_fallback_used`、`metadata_not_present`、
`unsupported_candidate_present`、`unsafe_value_rejected` の 5 種類です。
`vcs.repository.name` が最優先です。これが存在せず、credential・query・fragment などを
含まない canonical GitHub HTTPS URL の `vcs.repository.url.full` だけがある場合に限り、
repository segment をラベルとして使用します。名前が危険な場合や、metadata 自体がない
場合に prompt、CWD、path、時刻の近さからリポジトリを推測することはありません。
診断ページでもナビは 2 項目のままです。

<p align="center">
  <img width="900" alt="Local Ingestion Monitor 診断" src="../assets/screenshots/local-monitor-diagnostics.png">
</p>

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

`--sanitized-only` でも metadata-only のページ/API は利用できます。選択したローカルパス、
raw source、候補 / source-record key、confirmation / idempotency 値は、preview、結果、
履歴、ログ、スクリーンショットに表示されません。

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

2026-07-12 の repository-safe inventory では、Windows 上の `claude` version `2.1.207`
を確認しました。ただし次の実 producer 証拠は未取得です。

- interactive: 認証済み TTY が無く、標準入出力が redirect されたため実行不可。
- `claude -p`: 範囲を限った実行は exit `0` でしたが、構造化された OTel / Hook telemetry を出力しませんでした。
- Agent SDK: 対応 package、runtime reference、認証情報が利用できず実行していません。

この結果は interactive / print / SDK の証拠を相互に代用しません。raw payload、PII、
credential、local path を記録しない、名前付きの後続確認が必要です。詳細な記録は
[Sprint22 M5 live-validation](../sprints/sprint22-source-drift-claude/milestones/M5-integration/live-validation.md)
を参照してください。

## raw body 表示（既定）

raw body（tool arguments / results、sub-agent instructions / responses、system prompt）と
PII（`user.id` / `user.email`）は **既定で表示されます**。trace-detail page（スパン
インスペクタの raw タブと raw OTLP ペイロードセクション）に描画され、
`GET /traces/{rawRecordId}/raw` でも個別の raw OTLP JSON を確認できます。
加えて、概要とトレース一覧、trace 詳細の見出しでは、各トレースの**代表入力
プロンプト**を server 側で抽出して表示します（D032。プロンプトのみ raw 扱いで、他の列は
sanitized メタデータ。`/api/monitor/*` と SSE はプロンプトを含みません）。

raw を表示する全ページ（概要 / trace 一覧 / trace 詳細 / raw route）は次を満たします:

- same-origin アクセスのみ（cross-site は `403`）
- `Cache-Control: no-store`
- HTML エスケープされた、実行されない text として描画（スクリプト実行なし）

`--sanitized-only` を付けて起動すると raw body と PII、プロンプト表示は非表示になります。
概要 / 一覧 / 見出しのプロンプトは短縮 TraceId に切り替わり、スパンインスペクタの
raw タブは sanitized なスパン情報のみになり、Copilot 解析ドロワーは表示されず、
`GET /traces/{rawRecordId}/raw` と `GET /traces/{traceId}/spans/{spanId}/detail` は
`404` です。metadata-only 表示が必要な場合に使用できます。

raw store や表示内容には prompt / response / tool 情報が含まれる場合があります。
raw store ファイル（`data\monitor.db` 等）を repository に commit しないでください。

## SSE によるリアルタイム更新

`GET /events`（`text/event-stream`）を購読すると、新しい取り込みが projection されるたびに
通知（`data: {}`）が届きます。ブラウザの概要や `/traces` はこれを使って
自動的に API を再読み込みします。

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

runtime backup は prompt / response / tool arguments / results を含み得る raw backup です。
repository-safe ではなく、Retention cleanup の対象にもなりません。作成した ZIP は
operator-owned file として安全な private storage に保管し、不要になったら利用者が削除して
ください。`retention_backup_not_purged` はこの責任境界を示す固定 warning です。

展開した Release ZIP から、同梱の self-contained Config CLI を使う例:

```powershell
$cli = '.\app\config-cli\CopilotAgentObservability.ConfigCli.exe'
$db = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\raw-store.db'
& $cli runtime-backup create --database $db --output C:\private\local-monitor-backup.zip
& $cli runtime-backup inspect --bundle C:\private\local-monitor-backup.zip
& $cli runtime-backup preview --bundle C:\private\local-monitor-backup.zip --database $db
```

restore は Local Monitor を停止してから実行します。既存 DB を置換する場合は、既定で
`runtime-backups/` に pre-restore backup が作られます。preview が
`requires_confirmation=true` を返すのは、現在は欠落している非終端 raw source を backup
から再導入する場合だけです。そのときだけ表示された `confirmation_digest` を同じ archive
に対して明示的に渡せます。現在の tombstone / read denial は confirmation で解除できず、
復元先へ必ず引き継がれます。`stop.ps1` は一時的な process state を削除するため、停止前に
同じ installed instance へ戻す `Url` / `DbPath` / `InstallRoot` / `SanitizedOnly` を明示して
ください。

```powershell
$cli = '.\app\config-cli\CopilotAgentObservability.ConfigCli.exe'
$stopScript = '.\scripts\stop.ps1'
$startScript = '.\scripts\start.ps1'
$monitorUrl = 'http://127.0.0.1:4320'
$db = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\raw-store.db'
$installRoot = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\app'
$sanitizedOnly = $false # metadata-only instance を復元するときだけ $true
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
- 既定で raw body（prompt / response / tool arguments / results）と PII が表示されます。metadata-only 表示が必要な場合は `--sanitized-only` を付けて起動できます。
- モニターはループバックにのみバインドします。非ループバック URL は起動時に拒否されます。
- ログに raw prompt / response / tool arguments / results は出力しません。

詳細は [Data Safety](data-safety.md) と
[docs/specifications/security-data-boundaries.md](../specifications/security-data-boundaries.md) を参照してください。

## Copilot raw analysis

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
dotnet user-secrets set "CopilotAnalysis:Model" "glm-5.2" --project src\CopilotAgentObservability.LocalMonitor
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
`--sanitized-only` は Canvas 用の必須設定ではなく、metadata-only にしたい場合の
任意モードです。このモードでも sanitized な画面（概要 / 一覧 / 詳細の sanitized
表示）は使えますが、raw 由来の表示と raw route は非表示になります。

### 必要なもの

- GitHub Copilot app（Canvas extension runtime をサポートするバージョン）
- Local Ingestion Monitor を loopback で起動済み

### 使い方

1. モニターを起動します。

   ```powershell
   dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor.db --url http://127.0.0.1:4320
   ```

   必要に応じて `--sanitized-only` を追加すると raw 由来の表示 / raw route / PII は
   除外されます。

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

# Detailed Specification

この文書は、`docs/requirements.md` を上位要件として、現在の実装・検証フェーズの詳細仕様を定義する。
README や既存実装に本書と異なる記述がある場合、`docs/requirements.md` と本書を優先する。

## 1. 現在のフェーズ

現在の主作業は Phase 1: ローカル Langfuse PoC である。

Phase 0: ローカル Aspire Dashboard 疎通確認は完了済み背景として扱う。
Phase 0 では、VS Code GitHub Copilot Chat から Aspire Dashboard へ OTLP HTTP 送信できることを確認し、Node/Electron 側のローカル開発証明書問題を避けるため `http` launch profile を主手順とした。

Phase 1 では、ローカル self-host Langfuse に VS Code GitHub Copilot Chat と GitHub Copilot CLI の OTel を直接送信し、Langfuse 上で trace、prompt、response、tool call、token usage を確認する。
Phase 1 の主目的は、VS Code GitHub Copilot Chat / GitHub Copilot CLI の公式 OTel 出力を Langfuse に取り込み、trace / prompt / response / tool call / token usage を確認することである。

M9 では、Langfuse 直接送信を Phase 1 baseline として維持したうえで、直接送信が不安定な場合や後続の組織展開候補に備え、ローカル OTel Collector 経由送信を次候補として仕様化し、最小サンプルを追加する。

M11 以降では、既存の M0-M10 を観測基盤として維持し、研究実施計画書に基づく研究計測化を進める。
研究計測化の責務は、trace を再現可能に集計し、baseline / variant 比較に必要なデータと人間が評価可能な rubric を用意することまでとする。
研究実施計画書は VS Code GitHub Copilot Chat を中心に記述しているが、この repository では Phase 1 で整備済みの GitHub Copilot CLI 観測も M11 以降の研究計測対象に含める。
改善案の自動実装、自動採用、勝敗の自動決定は扱わない。
改善案生成・評価の小規模デモを検討する場合は、M11-M22 完了後の後続候補として扱う。

VS Code Agent Debug / Chat Debug View は、開発者が個別セッションを調査するための手動デバッグ機能として扱う。
本リポジトリでは、同等機能の UI や VS Code 内部ログ解析機能を実装しない。

## 2. Phase 1 の既定構成

### 2.1 実行基盤

Phase 1 の既定 PoC 実行基盤は Docker Desktop 上の Langfuse self-host Docker Compose とする。
Langfuse self-host 構成は Langfuse v3 の公式 Docker Compose 手順を前提にする。

既定 URL は以下とする。

| 用途 | URL |
| --- | --- |
| Langfuse UI | `http://localhost:3000` |
| Langfuse OTLP endpoint | `http://localhost:3000/api/public/otel` |
| Langfuse OTLP traces endpoint | `http://localhost:3000/api/public/otel/v1/traces` |

Docker Desktop を既定とする。
WSL2 Docker は Windows 側 VS Code / Copilot CLI から `localhost:3000` に到達できることを別途確認できる場合の代替候補とする。
社内サーバーは複数端末検証や組織展開の候補であり、Phase 1 の既定にはしない。

### 2.2 送信方式

Phase 1 baseline では、VS Code GitHub Copilot Chat / GitHub Copilot CLI から Langfuse に直接 OTLP HTTP 送信する。
OTel Collector は Phase 1 baseline の必須構成にしない。

M9 の Collector 経由送信は、直接送信 baseline の代替候補であり、baseline を置き換えない。

Langfuse 向け OTLP 送信では HTTP を使用する。
Langfuse OTel integration は gRPC を未サポートとしているため、gRPC は Phase 1 の送信方式にしない。

### 2.3 認証

Langfuse の OTLP endpoint は Basic Auth を要求する。
Langfuse の public key と secret key を `public:secret` 形式で Base64 encode し、`OTEL_EXPORTER_OTLP_HEADERS` に設定する。

```powershell
$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("<public-key>:<secret-key>"))
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:3000/api/public/otel"
$env:OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
```

signal-specific 設定が必要な exporter では、trace endpoint と trace headers を使用する。

```powershell
$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("<public-key>:<secret-key>"))
$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT="http://localhost:3000/api/public/otel/v1/traces"
$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
```

認証情報は repository に保存しない。
API key、Base64 化済み header、Langfuse 管理者パスワード、Docker Compose の secret は commit してはならない。

### 2.4 M9 Collector 経由送信

M9 の Collector 経由送信では、ローカル Docker Desktop 上の OpenTelemetry Collector を中継する。

| 用途 | URL / port |
| --- | --- |
| Collector OTLP/gRPC receiver | `localhost:4317` |
| Collector OTLP/HTTP receiver | `http://localhost:4318` |
| Collector から Langfuse への exporter endpoint | `http://host.docker.internal:3000/api/public/otel` |

Collector は OTLP HTTP/gRPC receiver でクライアント telemetry を受け取り、OTLP HTTP exporter で Langfuse に送信する。
M9 の Collector example は trace pipeline のみを有効にし、metrics pipeline は扱わない。
Compose example では `4317` / `4318` を `127.0.0.1` に bind し、content capture を含む telemetry receiver を外部 interface に公開しない。
Langfuse 認証は `LANGFUSE_AUTH` 環境変数に `Base64(public-key:secret-key)` を設定して渡す。
Collector config、Docker Compose example、repository 内文書に public key、secret key、Base64 化済み header を保存しない。

起動時の環境変数設定例:

```powershell
$publicKey = "<public-key>"
$secretKey = "<secret-key>"
$env:LANGFUSE_AUTH = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${publicKey}:${secretKey}"))
$env:LANGFUSE_OTLP_ENDPOINT = "http://host.docker.internal:3000/api/public/otel"
docker compose -f infra\otel-collector\docker-compose.example.yml up
```

Compose 構文確認では、実 credential ではなく dummy の `LANGFUSE_AUTH` を使う。
`docker compose config` は展開後の環境変数値を出力するため、実 `LANGFUSE_AUTH` を設定した shell では実行しない。

M9 では以下を扱わない。

- masking / redaction
- TLS 終端
- SSO
- 共有環境運用
- Resource Attributes の Collector 側自動付与
- sampling

## 3. クライアント設定

Config CLI の汎用コマンド `vscode-settings`、`vscode-env`、`copilot-cli-env` は、Phase 1 の既定構成に合わせて Langfuse 直接送信用の値を出力する。
既定 endpoint は `http://localhost:3000/api/public/otel` とし、PowerShell 環境変数サンプルでは public key / secret key の placeholder から Basic Auth header を生成する。

`langfuse-*` コマンドは Langfuse 直接送信を明示する別名として維持する。
`collector-*` コマンドは M9 Collector 経由送信専用とし、送信先は `http://localhost:4318`、Langfuse 認証は出力しない。

### 3.1 VS Code GitHub Copilot Chat

VS Code GitHub Copilot Chat の OTel 設定では、Phase 1 の Langfuse OTLP endpoint を指定する。

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:3000/api/public/otel",
  "github.copilot.chat.otel.captureContent": true
}
```

認証 header は VS Code settings だけで表現できない場合があるため、環境変数で渡す。
VS Code GitHub Copilot Chat では環境変数が settings より優先されるため、手動ライブ確認では VS Code プロセスに渡された OTel 関連環境変数も確認対象にする。

```powershell
$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("<public-key>:<secret-key>"))
$env:COPILOT_OTEL_ENABLED="true"
$env:COPILOT_OTEL_ENDPOINT="http://localhost:3000/api/public/otel"
$env:COPILOT_OTEL_CAPTURE_CONTENT="true"
$env:OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT="http://localhost:3000/api/public/otel/v1/traces"
$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode-copilot-chat,experiment.id=baseline"
```

### 3.2 GitHub Copilot CLI

GitHub Copilot CLI では、以下の環境変数を使用する。

```powershell
$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("<public-key>:<secret-key>"))
$env:COPILOT_OTEL_ENABLED="true"
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:3000/api/public/otel"
$env:OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT="http://localhost:3000/api/public/otel/v1/traces"
$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT="true"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=copilot-cli,experiment.id=baseline"
```

必要に応じて、GitHub Copilot CLI の file exporter を診断用途に使用する。
file exporter は OTLP export 経路と Copilot CLI の OTel emit 自体を切り分けるための補助であり、Phase 1 の主送信経路ではない。

### 3.3 M9 Collector 経由送信

Collector 経由送信では、VS Code GitHub Copilot Chat / GitHub Copilot CLI の送信先を Collector の OTLP HTTP receiver に向ける。
Langfuse Basic Auth は Collector 側で付与するため、クライアント側には Langfuse の public key / secret key / Basic Auth header を設定しない。

VS Code GitHub Copilot Chat の settings 例:

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:4318",
  "github.copilot.chat.otel.captureContent": true
}
```

VS Code プロセスに渡す環境変数例:

```powershell
Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue
Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue
Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue
$env:COPILOT_OTEL_ENABLED="true"
$env:COPILOT_OTEL_ENDPOINT="http://localhost:4318"
$env:COPILOT_OTEL_CAPTURE_CONTENT="true"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode-copilot-chat,experiment.id=baseline"
```

GitHub Copilot CLI の環境変数例:

```powershell
Remove-Item Env:OTEL_EXPORTER_OTLP_HEADERS -ErrorAction SilentlyContinue
Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT -ErrorAction SilentlyContinue
Remove-Item Env:OTEL_EXPORTER_OTLP_TRACES_HEADERS -ErrorAction SilentlyContinue
$env:COPILOT_OTEL_ENABLED="true"
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4318"
$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT="true"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=copilot-cli,experiment.id=baseline"
```

## 4. Resource Attributes

Phase 1 でも Phase 0 と同じ必須 Resource Attributes を維持する。

```text
user.id
user.email
team.id
department
client.kind
experiment.id
```

`client.kind` の推奨値は以下とする。

```text
vscode-copilot-chat
copilot-cli
```

`experiment.id` の初期推奨値は `baseline` とする。

必要に応じて、以下の推奨属性を追加する。

```text
repo.name
workspace.name
task.id
task.run_index
experiment.condition
prompt.version
repo.snapshot
agent.variant
skill.version
mcp.profile
cli.wrapper.version
task.category
```

M11 以降の研究計測では、以下を追加の研究用属性として使用する。

| 属性 | 用途 |
| --- | --- |
| `task.id` | 模擬保守タスクを一意に識別する |
| `task.category` | `refactoring`、`bug-investigation`、`test-generation`、`code-review` のいずれかで分類する |
| `task.run_index` | 同一 task / prompt / condition の反復番号を表す |
| `experiment.condition` | `baseline` または variant 条件を表す |
| `prompt.version` | 入力 prompt の版を識別する |
| `agent.variant` | agent / instructions / skill などの変更条件を識別する |
| `repo.snapshot` | fixture または対象 repository snapshot を識別する |

## 5. 研究計測フェーズ

### 5.1 スコープ

M11 以降の研究計測フェーズでは、Langfuse に取り込まれた trace を研究用 dataset として集計し、baseline / variant 比較の準備を行う。
観測基盤として M0-M10 の Phase 1 Langfuse PoC、VS Code GitHub Copilot Chat 収集、GitHub Copilot CLI 収集、M9 Collector 最小サンプルを維持する。

研究計測対象は VS Code GitHub Copilot Chat と GitHub Copilot CLI の両方とする。
研究実施計画書は VS Code GitHub Copilot Chat を主対象としているが、Copilot CLI は同一の研究用 schema と比較プロトコルで扱える範囲に含める。

M11-M22 は、研究計測、集計、baseline / variant 比較、品質非劣化 rubric、研究計画書へ戻せるレポート雛形までを対象とする。
このフェーズは評価支援までを対象とし、改善案の自動実装、自動採用、勝敗の自動決定は対象外とする。
改善案生成・評価は、M11-M22 では実装対象にせず、M23 以降の後続候補として扱う。

### 5.2 measurement schema

研究用集計出力は CSV / JSON の両対応とし、以下の必須列を持つ。
必須列とは出力列として必ず存在する列を指し、取得元の欠損時は CSV では空欄、JSON では `null` を出力する。

| 列 | 型 / 値域 | 由来 | 用途 |
| --- | --- | --- | --- |
| `trace_id` | string または null | Langfuse trace id または OTel trace id | trace を一意に識別する |
| `experiment_id` | string または null | Resource Attribute `experiment.id` | baseline / variant / experiment を分類し、trace を絞り込む |
| `client_kind` | `vscode-copilot-chat`、`copilot-cli`、または null | Resource Attribute `client.kind` | VS Code GitHub Copilot Chat と GitHub Copilot CLI を識別する |
| `task_id` | string または null | Resource Attribute `task.id` | 模擬保守タスクを一意に識別する |
| `task_category` | `refactoring`、`bug-investigation`、`test-generation`、`code-review`、または null | Resource Attribute `task.category` | 4 類型のどれに属するかを分類する |
| `task_run_index` | integer または null | Resource Attribute `task.run_index` | 同一 task / prompt / condition の反復番号を表す |
| `experiment_condition` | string または null | Resource Attribute `experiment.condition` | baseline または variant 条件を表す |
| `prompt_version` | string または null | Resource Attribute `prompt.version` | 入力 prompt の版を識別する |
| `agent_variant` | string または null | Resource Attribute `agent.variant` | agent / instructions / skill などの変更条件を識別する |
| `repo_snapshot` | string または null | Resource Attribute `repo.snapshot` | fixture または対象 repository snapshot を識別する |
| `input_tokens` | integer または null | Langfuse usage または OTel GenAI token usage | 入力 token 数を記録する |
| `output_tokens` | integer または null | Langfuse usage または OTel GenAI token usage | 出力 token 数を記録する |
| `total_tokens` | integer または null | Langfuse usage、OTel GenAI token usage、または `input_tokens + output_tokens` | 合計 token 数を記録する |
| `turn_count` | integer または null | M16 で定義する分類ルール | trace 単位の turn 数を記録する |
| `tool_call_count` | integer または null | M16 で定義する分類ルール | trace 単位の tool call 数を記録する |
| `duration_ms` | integer または null | Langfuse trace / observation duration または OTel span duration | trace 単位の実行時間をミリ秒で記録する |
| `error_count` | integer または null | Langfuse observation / OTel span status / events | trace 単位の error 数を記録する |
| `success_status` | `pass`、`fail`、`needs-review`、`not-evaluated` | 人間評価または後続 milestone の評価記録 | 品質非劣化確認の状態を記録する |

dotted Resource Attribute 名は、研究用 schema では snake_case の列名に正規化する。
たとえば、`experiment.id` は `experiment_id`、`client.kind` は `client_kind`、`task.run_index` は `task_run_index` として出力する。

`success_status` の値は以下の 4 値とする。

```text
pass
fail
needs-review
not-evaluated
```

`success_status=not-evaluated` は、まだ人間評価が行われていない trace の既定値として使用する。
`pass`、`fail`、`needs-review` の詳細な判定基準は M20 の品質非劣化 rubric で定義する。

欠損値は CSV では空欄、JSON では `null` として表現する。
未知 span 名や未知属性は集計不能として破棄せず、取得できる場合は以下の任意列に保持する。
未知 span 名から `turn_count` や `tool_call_count` を算出する分類ルールは M16 で定義する。

手動評価と未知情報の補助情報として、以下を任意列として扱う。

| 列 | 型 / 値域 | 用途 |
| --- | --- | --- |
| `evaluator_id` | string または null | 評価者を識別する。個人情報を含める場合は PoC のデータ扱い方針に従う |
| `evaluation_notes` | string または null | 評価時の短い根拠、保留理由、確認メモを記録する |
| `evaluated_at` | ISO 8601 datetime string または null | 評価日時を記録する |
| `unknown_spans_json` | JSON array または null | M16 の分類ルールで未分類となった span 名や最小識別情報を記録する。CSV では JSON 文字列として出力する |
| `unknown_attributes_json` | JSON object または null | schema 列へ正規化しなかった属性のうち、後続調査に必要なものを記録する。CSV では JSON 文字列として出力する |
| `aggregation_notes` | string または null | 集計時の補足、欠損理由、暫定処理を記録する |

研究実施計画書で補助指標とされている編集受容 / 生存率、cache token、reasoning token、model ID、IDE / CLI version、推定コストまたは相対コスト指数は、対象バージョンの Copilot OTel または Langfuse export から取得できる場合、もしくは算出根拠を説明できる場合に限って任意列候補として扱う。
任意列候補の正式な列名、型、取得可否は M14 の Langfuse export / API 調査で確認し、集計実装は M15 で扱う。

OpenTelemetry GenAI Semantic Conventions は発展中であり、M12 では特定の固定バージョンに依存しない。
研究用 schema は、現在の公式 GenAI semantic conventions と Langfuse の OpenTelemetry attribute mapping を参照しつつ、Copilot OTel と Langfuse export から取得できる値を正規化して扱う。
GenAI semantic conventions や Langfuse 側の mapping が変更された場合は、M14 以降の取得方式または M15 の集計実装で吸収する。

### 5.3 模擬保守タスク

baseline 計測では、以下の 4 類型の模擬保守タスクを定義してから実行する。

- リファクタリング
- バグ調査・修正提案
- テスト生成
- コードレビュー支援

各タスクでは `task.id`、入力 prompt、対象 fixture、期待する品質確認方法を文書化する。
実データ、機密情報、顧客データは使わない。

### 5.4 Langfuse データ取得方式

Langfuse 上の trace / observation / usage / metadata を取得する方式は M14 で決定する。
候補には UI export、API、DB 直接参照、ClickHouse 参照が含まれうる。
候補の優先順位と M15 が利用する入力形式は M14 で調査・決定する。

### 5.5 集計 CLI / script

M15 では、M14 で決めた入力形式を受け取り、M11 以降の measurement schema に従う CSV / JSON を生成する。
合成 fixture で token、duration、tool call count、error count、欠損属性、未知 span 名を確認する。
M16 の分類ルール確定前に M15 を実施する場合、`turn_count` / `tool_call_count` は列の存在、欠損表現、暫定抽出の確認に留め、正式な分類精度は M16 で扱う。
live Copilot 実行は自動テストに含めず、手動ライブ確認として扱う。

### 5.6 turn count / tool call count

turn count と tool call count は、特定の span 名だけに依存しない分類ルールとして M16 で定義する。
VS Code GitHub Copilot Chat と GitHub Copilot CLI の trace 差分を吸収し、未知 span 名は集計不能ではなく未分類として扱えるようにする。

### 5.7 baseline 計測

baseline 計測は、同一 task、同一 prompt、同一 condition で反復実行する。
初期既定は `N=10`、`experiment.id=baseline`、合成 fixture のみとする。
M17 で実行前チェック、環境変数、Langfuse trace id 記録、失敗時の扱い、除外基準を定義する。
M18 では 1 類型 x 2 回の小規模 dry run を行い、M19 で 4 類型 x N 回の baseline 本計測を行う。

### 5.8 品質非劣化 rubric と variant 比較

M20 では、4 類型ごとに `pass`、`fail`、`needs-review` の人間評価用 rubric を定義する。
M21 では、`experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant` の使い分けと比較表の形式を定義する。
M22 では、研究計画書に戻せる Markdown レポート雛形を追加する。

### 5.9 後続候補: trace-driven improvement loop

M11-M22 の完了後、改善案生成基盤を検討する場合は、trace-driven agent improvement loop として別 milestone で扱う。
この loop は、trace / metrics / rubric を入力に、failure taxonomy / anti-pattern 分類、改善候補生成、variant 評価、人間承認を順に行う。
生成された改善候補は人間が採否する提案として扱い、自動採用や repository の自動修正は行わない。

初期候補は以下とする。

- M23: failure taxonomy / anti-pattern 定義
- M24: trace-to-diagnosis MVP
- M25: improvement proposal generator
- M26: proposal evaluator
- M27: human approval workflow

M23 以降でも、自動採用、自動 repository 修正、自動 commit、自動 push、自動 pull request、自動勝敗決定は既定スコープ外とする。
改善候補は、`prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` のいずれかに分類し、人間が採否できる提案として扱う。

## 6. セキュリティとデータ扱い

Phase 1 はローカル限定 PoC とし、Langfuse に投入するデータは合成データまたは検証用データを基本とする。
実データ、機密情報、顧客データ、秘密情報を含む prompt / response / tool arguments / tool results は投入しない。

Phase 1 では content capture を有効化するが、masking / redaction 実装は行わない。
masking / redaction が必要になる実データ検証や共有環境検証は Phase 1 の既定スコープ外とする。

Phase 1 の保持期間は、content capture データと full trace を 30 日上限の目安とする。
不要になったローカル Langfuse データは Docker volume の削除を含む手順で削除できる状態にする。

共有環境や社内サーバーで運用する場合は、アクセス権、削除方法、保持期間、利用者周知を別途定義してから実施する。

## 6. 非スコープ

Phase 1 の既定スコープでは以下を扱わない。

- Langfuse self-host Docker Compose ファイルの repository 追加
- M9 の最小サンプルを超える OTel Collector 運用構成
- PostgreSQL / Ingestion API による生 OTel データ保存
- Collector での masking / redaction、sampling、属性付与
- 端末常駐 Collector.Agent / Collector.Tray / Collector.Updater
- 社内サーバーまたは共有環境での Langfuse 運用
- TLS 終端、SSO、共有アクセス権
- 実データを使う検証
- 独自 OTLP receiver
- 独自ログ収集エージェント
- 生 OTel データの独自ストレージ
- 独自可視化 UI
- VS Code Agent Debug View 相当の UI
- VS Code workspaceStorage / chatSessions 監視を主方式にすること
- VS Code 内部ログやローカル履歴の解析
- M11-M22 における改善案生成
- 改善案の自動採用
- 改善案の自動実装
- 勝敗の自動決定
- 改善効果の自動判定
- patch / diff 生成
- commit / push / pull request 自動化

これらが必要になった場合は、実装前に `docs/spec.md` を更新する。

## 8. 検証方針

### 8.1 自動検証

Config CLI、AppHost、プロジェクトファイル、依存関係に触れた場合は、`dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` を実行する。
M9 で Collector example を変更した場合は、Docker が利用可能な環境で dummy `LANGFUSE_AUTH` を設定してから `docker compose -f infra/otel-collector/docker-compose.example.yml config` を実行し、Compose 構文を確認する。
M15 の集計 CLI / script を変更した場合は、合成 Langfuse export fixture で token、duration、tool call count、error count、欠損属性、未知 span 名を確認する。

### 8.2 ローカル起動確認

ローカル起動確認では以下を確認する。

- Docker Desktop が起動している。
- Langfuse self-host Docker Compose が起動する。
- `http://localhost:3000` で Langfuse UI に到達できる。
- 初期ユーザー、organization、project、API key を作成できる。
- 不要になったデータを Docker volume ごと削除できる手順を確認できる。
- M9 Collector 経由送信を確認する場合は、Langfuse 起動後に `infra/otel-collector/docker-compose.example.yml` で Collector を起動できる。

### 8.3 手動ライブ確認

Copilot 実行に依存する挙動は、自動テストだけで保証しない。
Phase 1 の手動ライブ確認では、Langfuse UI で以下を確認する。

- VS Code GitHub Copilot Chat の trace が取り込まれる。
- GitHub Copilot CLI の trace または metrics が取り込まれる。
- prompt、response、tool arguments、tool results が確認できる。
- token usage、duration、error が確認できる。
- `client.kind` と `experiment.id` で trace を識別できる。

M9 Collector 経由送信の手動ライブ確認では、クライアント送信先を `http://localhost:4318` に変更し、Langfuse UI で同じ確認項目を満たすことを確認する。

手動ライブ確認を実施した場合は、確認日時、実行環境、Langfuse 起動方式、設定値、Langfuse trace id または識別情報、確認できた項目、未確認項目を記録する。

M17 以降の baseline / variant 計測では、live Copilot 実行を自動テストで代替しない。
計測時は `task.id`、`task.category`、`task.run_index`、`experiment.condition`、`prompt.version`、`agent.variant`、`repo.snapshot`、Langfuse trace id、除外理由を記録する。

## 9. 参考資料

- [VS Code: Monitor agent usage with OpenTelemetry](https://code.visualstudio.com/docs/copilot/guides/monitoring-agents)
- [GitHub Docs: GitHub Copilot CLI command reference](https://docs.github.com/ja/enterprise-cloud%40latest/copilot/reference/copilot-cli-reference/cli-command-reference)
- [OpenTelemetry: GenAI Semantic Conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/)
- [Langfuse: OpenTelemetry for LLM Observability](https://langfuse.com/integrations/native/opentelemetry)
- [Langfuse: Docker Compose self-host deployment](https://langfuse.com/self-hosting/deployment/docker-compose)

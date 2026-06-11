# Detailed Specification

この文書は、`docs/requirements.md` を上位要件として、現在の実装・検証フェーズの詳細仕様を定義する。
README や既存実装に本書と異なる記述がある場合、`docs/requirements.md` と本書を優先する。

## 1. 現在のフェーズ

Sprint1: ローカル Langfuse PoC は完了済みである。
Sprint2: raw telemetry store と Langfuse 非依存改善ループ MVP は完了済みである。
Sprint2 MVP では raw OTLP の file-based ingest、SQLite raw store、raw store から normalized dataset への変換、既存改善支援 CLI への接続を扱った。

Sprint2.5: ConfigCli 保守性改善と redacted real-trace E2E 互換性確認は完了済みである。
Sprint2.5 では Sprint3 に進む前に、巨大化した Config CLI の責務分割、CSV / JSON 共通処理の集約、redacted input に限定した実 Copilot trace 由来データの最小 E2E 証跡、regression review and closeout を扱った。
Sprint2.5 は新機能追加ではなく、既存 CLI contract を維持した保守性改善と互換性確認であった。

Sprint3: Content-aware Trace Diagnosis and Auto-decision Foundation は要件定義段階である。
Sprint3 では、Sprint2.5 の結果を踏まえ、trace からの自動診断候補生成、content-aware evidence extraction、改善候補生成、自動採用判断の基盤を扱う。
実 repository 修正を伴う自動改善実装は Sprint3 の既定スコープには含めず、Sprint4 以降の候補として安全境界を定義してから扱う。

Phase 0: ローカル Aspire Dashboard 疎通確認は完了済み背景として扱う。
Phase 0 では、VS Code GitHub Copilot Chat から Aspire Dashboard へ OTLP HTTP 送信できることを確認し、Node/Electron 側のローカル開発証明書問題を避けるため `http` launch profile を主手順とした。

Phase 1 / Sprint1 では、ローカル self-host Langfuse に VS Code GitHub Copilot Chat と GitHub Copilot CLI の OTel を直接送信し、Langfuse 上で trace、prompt、response、tool call、token usage を確認した。
Phase 1 の主目的は、VS Code GitHub Copilot Chat / GitHub Copilot CLI の公式 OTel 出力を Langfuse に取り込み、trace / prompt / response / tool call / token usage を確認することである。

Langfuse は Sprint1 baseline observability backend である。
ただし、改善ループの必須依存とは限らない。
Sprint2 MVP では raw telemetry / normalized dataset を改善支援ループの主入力とし、Langfuse UI は dashboard / trace viewer の optional side path として扱う。

M9 では、Langfuse 直接送信を Phase 1 baseline として維持したうえで、直接送信が不安定な場合や後続の組織展開候補に備え、ローカル OTel Collector 経由送信を次候補として仕様化し、最小サンプルを追加する。

M11 以降では、既存の M0-M10 を観測基盤として維持し、研究実施計画書に基づく研究計測化を進める。
研究計測化の責務は、trace を再現可能に集計し、baseline / variant 比較に必要なデータと人間が評価可能な rubric を用意することまでとする。
研究実施計画書は VS Code GitHub Copilot Chat を中心に記述しているが、この repository では Phase 1 で整備済みの GitHub Copilot CLI 観測も M11 以降の研究計測対象に含める。
M11-M22 では、改善案の自動実装、自動採用、勝敗の自動決定は扱わない。
Sprint3 以降では、明示的に仕様化した範囲に限り、自動採用判断と自動改善実装への接続を扱う。

VS Code Agent Debug / Chat Debug View は、開発者が個別セッションを調査するための手動デバッグ機能として扱う。
本リポジトリでは、同等機能の UI や VS Code 内部ログ解析機能を実装しない。

### 1.1 利用者向け文書の位置づけ

利用者向けの入口は repository root の `README.md` と `docs/getting-started.md` とする。

`README.md` は、本リポジトリの目的、対象範囲、既定構成、必要な申請・導入物、最短利用手順、データ扱いの注意を説明する入口文書である。
`docs/getting-started.md` は、初めて利用する人向けに、Langfuse 起動、Config CLI による設定サンプル出力、VS Code GitHub Copilot Chat / GitHub Copilot CLI の OTel 設定、Langfuse UI での確認、よくある失敗をまとめる手順文書である。

これらの利用者向け文書は、`docs/requirements.md` と本書の詳細仕様を置き換えない。
README または getting started と本書が矛盾する場合は、本書を修正する必要がある仕様変更か、利用者向け文書の説明誤りかを確認してから更新する。

利用者向け文書には、実 credential、secret、Base64 化済み header、実 trace content、実 prompt / response content、実 user identity を保存しない。
Sprint3 以降の明示 opt-in sensitive local output は、この repository に保存する利用者向け文書とは別に扱う。
設定例では placeholder と synthetic / example identity のみを使用する。

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
Sprint3 以降の自動採用判断と自動改善実装の検討は、M11-M22 の研究計測フェーズとは別スコープで扱う。
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
`pass`、`fail`、`needs-review` の詳細な判定基準は M20 の品質非劣化 rubric に従う。

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

`task.category` は、研究用 schema と同じ `refactoring`、`bug-investigation`、`test-generation`、`code-review` の 4 値を使用する。

M13 の初期タスクセットは以下の 4 件とする。

| task.id | task.category | 概要 |
| --- | --- | --- |
| `maint-refactor-001` | `refactoring` | 小さな .NET CLI 処理の重複や責務混在を、外部仕様を変えずに整理する |
| `maint-bug-001` | `bug-investigation` | 合成入力で再現する設定値または集計値の不具合を調査し、原因と最小修正案を示す |
| `maint-test-001` | `test-generation` | 既存の小さな純粋ロジックに対して、正常系、境界、異常系の deterministic unit tests を追加提案する |
| `maint-review-001` | `code-review` | 小さな差分または疑似 PR patch を読み、仕様逸脱、テスト不足、保守リスクを指摘する |

各タスクでは、以下を文書化する。

- `task.id`
- `task.category`
- 入力 prompt
- 対象 fixture の説明
- 実行条件
- 成功時に残すべき evidence
- M20 の品質非劣化 rubric に接続する品質確認観点

ここで使用する `task.id`、`task.category`、`prompt.version`、`repo.snapshot` は Resource Attribute key を指す。
M12 の研究用 dataset では、それぞれ `task_id`、`task_category`、`prompt_version`、`repo_snapshot` に正規化される。

fixture はすべて合成データとし、実ユーザーデータ、顧客データ、秘密情報、実 credential、実運用ログを使わない。
fixture の既定形は、小さな synthetic .NET code fixture とする。
M13 では fixture の内容、入力条件、期待観点を文書化するだけに留め、実 fixture ファイル作成や集計 CLI 入力 fixture は M15 以降で扱う。

初期タスクの詳細は以下とする。

| task.id | 入力 prompt | 対象 fixture | 実行条件 | evidence | 暫定的な品質確認観点 |
| --- | --- | --- | --- | --- | --- |
| `maint-refactor-001` | `Synthetic .NET CLI fixture の外部仕様を変えずに、重複している環境変数出力処理と Resource Attribute 文字列組み立て処理を整理する変更案を示してください。振る舞いを変えないために確認すべきテストまたは手動確認も示してください。` | Config CLI 風の小さな synthetic .NET code fixture。複数 command が類似した OTel 環境変数サンプルを出力し、Resource Attribute 組み立て処理に重複がある状態を想定する。 | repository の実コードではなく synthetic fixture を対象にし、既存 CLI 出力のキー名、既定 endpoint、capture content 設定、Resource Attribute 名は変えない。 | 提案した変更概要、変更対象の関数または責務、振る舞い維持の確認方法、未確認事項を記録する。 | 外部出力形式を変えないこと、単一用途の過剰抽象化を避けること、既存テストで確認できる範囲を示すこと。 |
| `maint-bug-001` | `Synthetic measurement aggregation fixture で source の total_tokens が欠損している場合に、fallback の total_tokens が input_tokens + output_tokens と一致しないケースを調査し、原因、最小修正案、回帰確認方法を示してください。` | Langfuse export 風の合成 JSON または in-memory record。`input_tokens` と `output_tokens` は存在し、source の `total_tokens` は欠損しているが、fallback 合計処理により誤った合計が出る状態を想定する。 | live Langfuse や実 trace は使わず、合成入力だけで再現手順を説明する。M15 の集計実装が未作成の場合は、修正案を疑似コードまたは対象責務の説明に留める。source の `total_tokens` が存在する場合に常に `input_tokens + output_tokens` と一致すべき、という不変条件は M13 では定義しない。 | 再現条件、期待値、実際値、原因、最小修正案、回帰テスト観点を記録する。 | 原因と修正範囲が分離されていること、欠損値方針が M12 schema と矛盾しないこと、実データを要求しないこと。 |
| `maint-test-001` | `Synthetic Resource Attribute parser の純粋ロジックに対して、正常系、境界、異常系を含む deterministic unit tests を提案してください。テスト名、入力、期待値、狙いを示してください。` | `key=value,key2=value2` 形式の synthetic Resource Attribute 文字列を辞書または validation result に変換する小さな純粋関数を想定する。 | 外部サービス、時刻、ファイルシステム、ネットワークに依存しない。実装追加ではなく、必要なテストケース定義と期待結果を示す。必須 Resource Attribute が欠損した場合は、dataset 列を欠落させるのではなく、M12 の欠損値方針に従って null または空欄へ写像できることを確認する。 | 追加すべきテストケース一覧、各ケースの入力と期待値、既存仕様との対応を記録する。 | 正常系だけでなく空値、重複 key、不正 token、必須属性欠損を含めること。必須属性欠損を validation error に固定せず、M12 の null / 空欄表現と補足記録に接続できること。テストが実装詳細ではなく観測仕様に結びついていること。 |
| `maint-review-001` | `Synthetic PR patch をコードレビューし、仕様逸脱、テスト不足、保守リスクを重大度順に指摘してください。問題がない場合は残リスクと追加確認だけを述べてください。` | OTel 設定生成または aggregation schema 周辺の小さな疑似 diff。例として `client.kind` の値変更、content capture 設定の欠落、欠損値表現の変更、テスト削除を含む差分を想定する。 | レビュー姿勢で回答し、修正実装は行わない。指摘は仕様根拠、影響、確認方法を含める。 | 指摘一覧、重大度、根拠となる仕様、テスト不足、残リスクを記録する。 | 仕様違反と好みの指摘を分けること、M12 schema と M13 タスク定義に照らすこと、自動採用や勝敗判定をしないこと。 |

`prompt.version` の初期値は各タスクの初版として `v1` を使用する。
`repo.snapshot` は synthetic fixture の版を識別する値として使用し、初期値は `synthetic-dotnet-fixture-v1` とする。
反復回数、実行前チェック、Langfuse trace id の記録様式、除外基準は M17 で定義する。
`pass`、`fail`、`needs-review` の厳密な判定基準は M20 の品質非劣化 rubric に従う。

### 5.4 Langfuse データ取得方式

M14 では、Langfuse 上の trace / observation / usage / metadata を取得する方式を、M15 の集計 CLI / script が再現可能に扱える入力形式として確定した。

M15 の既定入力は、ローカル self-host Langfuse で再現可能な Public API の legacy trace / observation read response を保存した JSON とする。
保存 JSON は repository に commit する実データではなく、M15 の合成 fixture または手動で sanitized した API response snapshot として扱う。
入力 JSON には、研究用 schema へ写像するために trace、observation、usage、metadata、Resource Attributes を保持する。
欠損値は M12 の方針に従い、CSV では空欄、JSON では `null` に写像できる形で保持する。
`turn_count` / `tool_call_count` の正式な分類ルールは M16 で定義するため、M15 では列の存在、欠損表現、暫定抽出の確認に留める。

取得方式の優先順位は以下とする。

| 優先度 | 方式 | M14 の扱い |
| --- | --- | --- |
| 1 | Public API legacy trace / observation read | M15 の既定入力。self-host baseline で再現可能な JSON snapshot として扱う |
| 2 | UI export | 手動診断や one-off export の代替。M15 MVP の既定入力にはしない |
| 3 | Blob Storage export | scheduled export / 大量 export の候補。M15 MVP の最小入力にはしない |
| 4 | Observations API v2 | 新規 data extraction 向けの候補だが、M14 時点の公式 docs では Cloud-only のため self-host baseline の既定にはしない |
| 5 | ClickHouse 直接参照 | self-host の調査・復旧用の最後の候補。M15 MVP の既定入力にはしない |

Langfuse Public API docs では、新規 data extraction には v2 data APIs が推奨される一方、older trace / observation read APIs も利用可能とされている。
ただし、Observations API v2 は M14 時点で Cloud-only と明記されているため、ローカル self-host Langfuse PoC の既定取得方式にはしない。

API credential、Base64 化済み header、Langfuse 管理者パスワード、実 trace content は repository に保存しない。
M15 の fixture には secret、実ユーザーデータ、顧客データ、機密情報、実運用ログを含めない。

### 5.5 集計 CLI / script

M15 では、M14 で決めた入力形式を受け取り、M11 以降の measurement schema に従う CSV / JSON を生成する。
既定入力は、Public API の legacy trace / observation read response を保存した JSON 形状に合わせた合成 fixture または sanitized snapshot とする。
合成 fixture で token、duration、tool call count、error count、欠損属性、未知 span 名を確認する。
M16 の分類ルール確定前に M15 を実施する場合、`turn_count` / `tool_call_count` は列の存在、欠損表現、暫定抽出の確認に留め、正式な分類精度は M16 で扱う。
live Copilot 実行は自動テストに含めず、手動ライブ確認として扱う。

### 5.6 turn count / tool call count

turn count と tool call count は、特定の span 名だけに依存しない分類ルールとして扱う。
VS Code GitHub Copilot Chat と GitHub Copilot CLI の trace 差分を吸収し、未知 span 名は集計不能ではなく未分類として扱えるようにする。

`turn_count` は、trace 単位の LLM round-trip 数として扱う。
人間との会話ターン数ではなく、agent invocation 内で発生した LLM call observation の数を表す。
算出では、trace / observation / attributes に明示的な turn count がある場合はそれを優先する。
明示値がない場合は、`type=generation`、`type=chat`、`gen_ai.operation.name=chat|generate_content|text_completion`、または既知 span 名 `chat` の observation を数える。
`invoke_agent` root、tool、permission / approval、file operation、hook、lifecycle event は `turn_count` に含めない。

`tool_call_count` は、trace 単位の実 tool invocation 数として扱う。
算出では、trace / observation / attributes に明示的な tool call count がある場合はそれを優先する。
明示値がない場合は、`type=tool`、`kind=tool`、`category=tool`、`gen_ai.tool.name` を持つ observation、または既知 span 名 `execute_tool` の observation を数える。
file operation、shell command、search、MCP tool は、tool span として表現されている場合に `tool_call_count` に含める。
permission / approval、hook、lifecycle event は `tool_call_count` に含めない。
汎用 `event` observation は一律には破棄せず、明確な lifecycle event だけを既知の非 count observation として扱う。

`observations` が欠損しており、明示 count もない場合は、CSV では空欄、JSON では `null` を出力する。
`observations` が空配列の場合は、明示 count がない限り `turn_count=0`、`tool_call_count=0` とする。
未知 observation は破棄せず、`unknown_spans_json` に `id`、`name`、`type`、`kind` の最小識別情報だけを保持する。
prompt、tool arguments、tool results、raw attributes は content capture 由来情報を含み得るため、`unknown_spans_json` へコピーしない。
未知 Resource Attribute は `unknown_attributes_json` に保持できるが、prompt、response、content、tool arguments、tool results、credential、secret、token など content または credential 由来と判断できる key は出力しない。

### 5.7 baseline 計測

baseline 計測は、同一 task、同一 prompt、同一 condition で反復実行する。
初期既定は `N=10`、`experiment.id=baseline`、`experiment.condition=baseline`、`agent.variant=baseline`、`prompt.version=v1`、`repo.snapshot=synthetic-dotnet-fixture-v1`、合成 fixture のみとする。
`client.kind` は `vscode-copilot-chat` と `copilot-cli` を別条件として扱い、それぞれで反復数を数える。
M18 では 1 類型 x 2 client_kind x 2 runs の小規模 dry run を行い、M19 では 4 類型 x 2 client_kind x N=10 の baseline 本計測を行う。

baseline の実行単位は、以下の組み合わせで識別する。

```text
task.id + client.kind + task.run_index + experiment.condition + prompt.version + agent.variant + repo.snapshot
```

実行前チェックでは、以下を確認する。

- Langfuse self-host が起動しており、`http://localhost:3000` の UI と `http://localhost:3000/api/public/otel` の OTLP endpoint に到達できる。
- Langfuse public key / secret key から生成した Basic Auth header が、対象クライアントの OTel 環境変数に設定されている。
- content capture が有効である。
- `OTEL_RESOURCE_ATTRIBUTES` に `user.id`、`user.email`、`team.id`、`department`、`client.kind`、`experiment.id`、`task.id`、`task.category`、`task.run_index`、`experiment.condition`、`prompt.version`、`agent.variant`、`repo.snapshot` が含まれる。
- 入力 prompt と対象 fixture は M13 で定義した synthetic fixture に限定し、実ユーザーデータ、顧客データ、秘密情報、実 credential、実運用ログを使わない。
- 実 credential、Base64 化済み header、実 trace content、実ユーザーデータは repository に保存しない。

baseline 実行記録の既定は CSV 台帳とする。
Markdown には要約、判断理由、検証記録のみを残し、CSV 台帳ファイル自体は M18 / M19 の実測時に作成する。
CSV 台帳は以下の列を持つ。

| 区分 | 列 |
| --- | --- |
| 必須識別列 | `run_id`, `task_id`, `task_category`, `client_kind`, `task_run_index`, `experiment_id`, `experiment_condition`, `prompt_version`, `agent_variant`, `repo_snapshot` |
| 実行記録列 | `started_at`, `completed_at`, `operator_id`, `environment`, `resource_attributes`, `prompt_used` |
| Langfuse 記録列 | `langfuse_trace_id`, `langfuse_trace_url`, `trace_found`, `trace_checked_at` |
| 集計接続列 | `input_tokens`, `output_tokens`, `total_tokens`, `turn_count`, `tool_call_count`, `duration_ms`, `error_count`, `success_status` |
| 失敗・除外列 | `run_status`, `failure_type`, `exclusion_reason`, `retry_of_run_id`, `notes` |

CSV 台帳は repository に commit する成果物とはしない。
M18 / M19 で台帳内容を共有または repository に保存する必要が出た場合は、実 credential、実 trace content、実ユーザーデータ、実 prompt content、実 email address、Base64 化済み header、secret を含まないように sanitization してから扱う。
`operator_id` は実名や実 email address ではなく、ローカル計測用の仮名または空欄にする。
`resource_attributes` は raw 環境変数 dump ではなく、M17 で許可した Resource Attribute key の値だけを記録し、secret、token、credential、content 由来の key は記録しない。
`prompt_used` は M13 で定義した task id / prompt version / synthetic prompt 参照として記録し、実 prompt 本文や実データを含む prompt は記録しない。
`langfuse_trace_id` は、M12 schema の `trace_id` へ写像する。

`run_status` は以下の値を使用する。

| 値 | 意味 |
| --- | --- |
| `completed` | Copilot 実行が完了し、対応する Langfuse trace を確認できた |
| `failed` | Copilot 実行自体が失敗した |
| `trace-missing` | Copilot 実行は完了したが、対応する Langfuse trace を確認できない |
| `excluded` | 実行記録は残すが、baseline 集計から除外する |
| `not-run` | 予定された run が未実行である |

`trace_found` は `true`、`false`、空欄のいずれかにする。
未実行や trace 確認前は空欄を使用する。
`failure_type` は、`copilot-error`、`langfuse-unavailable`、`trace-missing`、`wrong-attributes`、`wrong-task`、`wrong-client-kind`、`real-data-risk`、`operator-error`、`environment-error`、`other`、空欄のいずれかにする。

手順違反、誤った task / client / Resource Attributes、実データ混入リスク、明らかな環境障害は `excluded` とし、`exclusion_reason` を必須にする。
`failed`、`trace-missing`、`excluded` は baseline の有効 N には数えない。
再実行時は新しい `run_id` を作成して `retry_of_run_id` に元 run の `run_id` を記録する。
実データ混入リスクがある run では、汚染された Langfuse trace を削除するか、必要に応じてローカル Langfuse volume を破棄する。
その場合でも台帳行自体は残すが、`prompt_used`、raw `resource_attributes`、`langfuse_trace_url`、content を含む `notes` は残さず、sanitized した `exclusion_reason` だけを記録する。
`success_status` は人間評価前の既定値として `not-evaluated` を使用する。
live Copilot 実行は自動テストで代替しない。

### 5.8 品質非劣化 rubric

M20 では、baseline / variant 比較で使用する人間評価用 rubric を定義する。
rubric は trace や回答の品質を人間が確認するための基準であり、自動採点、自動勝敗判定、改善案の自動採用には使わない。
評価者は、対象 trace の `task.id`、`task.category`、prompt version、対象 fixture、回答内容、必要に応じて token / turn / tool call / duration / error count を確認し、`success_status` と短い根拠を記録する。

`success_status` の判定は以下とする。

| 値 | 判定基準 |
| --- | --- |
| `pass` | task 類型ごとの必須観点を満たし、仕様違反、重大な誤り、主要な確認漏れがない |
| `fail` | 仕様違反、誤った結論、危険または過剰な提案、主要観点の欠落、実データや secret を要求する提案のいずれかがある |
| `needs-review` | 主要方針は妥当だが根拠や確認観点が不足している、軽微な仕様解釈の揺れがある、または評価者判断が割れる可能性がある |
| `not-evaluated` | 人間評価が未実施である |

`refactoring` では、外部仕様を変えない変更案であること、既存 CLI 出力の key、既定 endpoint、content capture 設定、Resource Attribute 名を維持すること、単一用途の過剰抽象化を避けること、振る舞い維持の確認方法を示すことを確認する。
これらを満たせば `pass`、出力形式や設定値を変える、不要な依存や大きな抽象化を導入する、確認方法がない場合は `fail`、変更方針は妥当だが確認範囲や影響説明が不足する場合は `needs-review` とする。

`bug-investigation` では、合成入力だけで再現条件、期待値、実際値、原因、最小修正案、回帰確認方法を分けて説明し、M12 の欠損値方針と矛盾しないことを確認する。
これらを満たせば `pass`、原因と修正案を混同する、欠損値方針と矛盾する、実データや live Langfuse を要求する、source の `total_tokens` が常に合計値と一致すべきだと誤って固定する場合は `fail`、再現や回帰確認の一部が不足する場合は `needs-review` とする。

`test-generation` では、正常系、境界、異常系を含む deterministic unit tests を提案し、テスト名、入力、期待値、狙いを示し、必須 Resource Attribute 欠損を validation error に固定せず M12 の null / 空欄表現へ接続できることを確認する。
これらを満たせば `pass`、外部サービス、時刻、ファイルシステム、ネットワークへ依存する、正常系だけに偏る、欠損属性で dataset 列を欠落させる前提にする場合は `fail`、主要カテゴリはあるが期待値や狙いの説明が不足する場合は `needs-review` とする。

`code-review` では、レビュー姿勢で回答し、仕様逸脱、テスト不足、保守リスクを重大度順に指摘し、各指摘に仕様根拠、影響、確認方法を含め、修正実装に進まないことを確認する。
これらを満たせば `pass`、明確に seed された主要な仕様違反を見落とす、好みの指摘と仕様違反を混同する、修正実装を始める、重大度や根拠がない場合は `fail`、軽微または期待観点外の不足、もしくは指摘は妥当だが重大度、根拠、残リスクの一部が不足する場合は `needs-review` とする。

評価記録では、M12 の任意列 `evaluator_id`、`evaluation_notes`、`evaluated_at` を使用できる。
`evaluation_notes` には、判定理由、未確認項目、`needs-review` の保留理由を短く記録する。
`evaluator_id` は実名や実 email address ではなく、ローカル計測用の仮名または空欄にする。
評価記録には実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を保存しない。

### 5.9 variant / A-B 計測プロトコル

M21 では、baseline / variant 比較の実験属性、実行順序、比較表の形式を定義する。
variant 比較は、同じ task、client、prompt、fixture 条件で baseline と variant を比較するための計測プロトコルであり、自動勝敗判定、自動採点、改善案生成、自動採用、自動実装、repository 自動修正は行わない。

実験属性は以下のように使い分ける。

| Resource Attribute | 用途 | 初期値または例 |
| --- | --- | --- |
| `experiment.id` | baseline / variant / experiment の分類と絞り込みに使う比較セット全体の識別子。単一 baseline 計測では `baseline`、variant 比較では比較名を入れる | `baseline`、`instructions-slim-v1-ab` |
| `experiment.condition` | 同一 `experiment.id` 内の比較条件を識別する | `baseline`、`variant` |
| `prompt.version` | M13 の task prompt の版を識別する。同一比較内では原則同じ値に固定する | `v1` |
| `agent.variant` | agent / instructions / skill などの介入内容を識別する | `baseline`、`instructions-slim-v1` |

variant 比較では、`task.id`、`task.category`、`client.kind`、`prompt.version`、`repo.snapshot` を baseline と variant で揃える。
prompt 変更そのものを比較対象にする場合だけ、`prompt.version` を介入軸として変えてよい。
その場合は、`agent.variant` ではなく `prompt.version` の差分として比較表に明記する。
baseline と variant の区別は `experiment.condition` で表し、同じ比較セットに属する run には同じ `experiment.id` を付与する。

初期既定の実行量は、baseline と variant の各 `N=10` とする。
`client.kind` は `vscode-copilot-chat` と `copilot-cli` を別条件として扱い、それぞれで有効 N を数える。
実行順序は、task / client ごとに baseline と variant を交互に実行する。
例として、同じ task / client では `baseline-1`、`variant-1`、`baseline-2`、`variant-2` の順に進める。
特定条件だけをまとめて実行した場合は、比較表または notes にその理由を記録する。

variant 実行記録の CSV 台帳は M17 の baseline 台帳列を継承する。
`run_status`、`failure_type`、`trace_found`、`retry_of_run_id`、sanitization 方針も M17 と同じ扱いにする。
`failed`、`trace-missing`、`excluded` は有効 N に数えず、再実行時は新しい `run_id` を作成して `retry_of_run_id` に元 run を記録する。
`success_status` は人間評価前の既定値として `not-evaluated` を使用し、評価後は M20 の rubric に従って `pass`、`fail`、`needs-review` のいずれかを記録する。

baseline / variant 比較表は、以下の列を既定とする。

| 区分 | 列 |
| --- | --- |
| 比較識別列 | `comparison_id`, `experiment_id`, `task_id`, `task_category`, `client_kind`, `baseline_prompt_version`, `variant_prompt_version`, `repo_snapshot` |
| 条件列 | `baseline_condition`, `variant_condition`, `baseline_agent_variant`, `variant_agent_variant` |
| 実行数列 | `baseline_valid_n`, `variant_valid_n`, `baseline_excluded_n`, `variant_excluded_n` |
| 指標列 | `baseline_total_tokens_median`, `variant_total_tokens_median`, `baseline_turn_count_median`, `variant_turn_count_median`, `baseline_tool_call_count_median`, `variant_tool_call_count_median`, `baseline_duration_ms_median`, `variant_duration_ms_median`, `baseline_error_count_total`, `variant_error_count_total` |
| 品質列 | `baseline_success_status_counts`, `variant_success_status_counts`, `quality_non_regression_status`, `evaluation_notes` |
| 注記列 | `comparison_notes` |

`comparison_id` は比較表の行を一意に識別する任意の安定 ID とし、少なくとも `experiment_id`、`task_id`、`client_kind`、`baseline_condition`、`variant_condition` を見れば対応する run 群を追跡できるようにする。
`baseline_success_status_counts` と `variant_success_status_counts` は、各条件の `pass`、`fail`、`needs-review`、`not-evaluated` の件数を保存する。
`quality_non_regression_status` は人間が M20 rubric を踏まえて記録する比較上の確認状態であり、値は `pass`、`fail`、`needs-review`、`not-evaluated` のいずれかとする。
この列は自動勝敗や自動採用を意味しない。
比較表は M21 時点の初期列であり、後続 milestone で指標を追加する場合も secret、credential、content、実 user identity を含めない。
比較表には実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を保存しない。

### 5.10 結果レポート雛形

M22 では、研究計画書に戻せる Markdown レポート雛形を追加する。
レポート雛形は実測結果そのものではなく、M12 measurement schema、M20 品質非劣化 rubric、M21 variant / A-B 計測プロトコルに基づく記録様式とする。
新しいコード API、CLI、CSV / JSON schema は追加しない。

レポート雛形は、少なくとも以下を記録できる形にする。

- レポート識別情報: `report_id`、`comparison_id`、`experiment_id`、`task_id`、`task_category`、`client_kind`、`repo_snapshot`、作成日。
- 実行範囲: baseline / variant の valid N、excluded N、除外理由サマリ。
- 指標サマリ: `total_tokens`、`turn_count`、`tool_call_count`、`duration_ms`、`error_count`、`success_status`。
- baseline / variant 比較: M21 の比較表列に対応する `baseline_*` / `variant_*` 指標、`quality_non_regression_status`、`evaluation_notes`、`comparison_notes`。
- 品質非劣化評価: M20 rubric の `pass`、`fail`、`needs-review`、`not-evaluated` を使った人間評価記録。
- 観察メモ: trace 確認所見、未確認項目、残リスク、研究計画書へ戻す要約。

レポートには実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を保存しない。
`quality_non_regression_status` は人間が M20 rubric を踏まえて記録する確認状態であり、自動勝敗判定、自動採点、自動採用を意味しない。

### 5.11 既存後続候補: trace-driven improvement loop

M11-M22 の完了後、改善案生成基盤を検討する場合は、trace-driven agent improvement loop として別 milestone で扱う。
この loop は、trace / metrics / rubric を入力に、failure taxonomy / anti-pattern 分類、改善候補生成、variant 評価、人間承認を順に行う。
この節は、M23-M27 で実装済みの人間承認型 loop の履歴仕様である。
Sprint3 以降では、この loop を拡張し、自動採用判断と自動改善実装への接続を扱う。

初期候補は以下とする。

- M23: failure taxonomy / anti-pattern 定義
- M24: trace-to-diagnosis MVP
- M25: improvement proposal generator
- M26: proposal evaluator
- M27: human approval workflow

M23-M27 では、自動採用、自動 repository 修正、自動 commit、自動 push、自動 pull request、自動勝敗決定はスコープ外であった。
Sprint3 では、自動採用判断を扱う。
実 repository 修正を伴う自動改善実装、自動 commit、自動 push、自動 pull request は Sprint3 の既定スコープ外とし、Sprint4 以降で安全境界を定義してから扱う。
改善候補は、`prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` のいずれかに分類する。

### 5.12 M23 failure taxonomy / anti-pattern

M23 では、trace-driven improvement loop の入口として、trace / metrics / M20 rubric の確認結果から人間が失敗要因と agent anti-pattern を分類できる taxonomy を定義する。
M23 は taxonomy 定義までを対象とし、trace-to-diagnosis 実装、改善候補生成、改善候補評価、改善採用、repository 修正は扱わない。

failure category は以下の ID を初期値とする。

| ID | 用途 |
| --- | --- |
| `F-SPEC` | 要件、仕様、milestone task との矛盾 |
| `F-SCOPE` | 非スコープの自動実装、自動採用、repository 修正、実データ利用などの提案 |
| `F-DATA` | secret、実 user identity、raw prompt / response content、tool result 本文などの保存または共有 |
| `F-MEASURE` | M12 measurement schema の列、欠損値、正規化名、値域との不整合 |
| `F-TASK` | M13 task category に必要な観点の欠落 |
| `F-RUBRIC` | M20 rubric に基づく品質評価根拠の不足 |
| `F-TRACE` | trace / observation / metrics に基づく根拠確認の不足 |
| `F-TOOL` | 不要または重複した tool / workflow による非効率 |
| `F-ERROR` | error、timeout、permission failure の未確認または無視 |
| `F-COMM` | 重大度、根拠、残リスク、検証結果の報告不足 |
| `F-COMPARISON` | `experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant`、実行順序、valid / excluded N の混同 |

agent anti-pattern は以下の ID を初期値とする。

| ID | 用途 |
| --- | --- |
| `AP-SILENT-SPEC` | 仕様の選択肢や矛盾を説明せずに決める |
| `AP-OVERREACH` | 求められていない実装、依存追加、workflow 変更、commit / PR を進める |
| `AP-RAW-CONTENT` | trace content、prompt、tool result、secret、identity を記録様式へ残す |
| `AP-SCHEMA-DRIFT` | M12 の列名、欠損値、値域を独自に変える |
| `AP-RUBRIC-FLAT` | task 類型ごとの根拠なしに `pass` / `fail` だけを置く |
| `AP-TRACE-SKIP` | 利用可能な trace / metrics を確認せずに診断する |
| `AP-TOOL-LOOP` | 同じ探索や tool call を繰り返し、追加根拠が増えない |
| `AP-ERROR-BLIND` | error、timeout、permission failure を結論から除外する |
| `AP-UNCLEAR-SEVERITY` | 指摘の重大度、影響、確認方法を分けない |
| `AP-AUTO-DECIDE` | variant の勝敗、改善採用、修正実行を自動決定のように表現する |
| `AP-CONFOUND` | 比較条件、prompt version、agent variant、実行順序、除外数の違いを無視して比較する |

M23 の failure category は、M17 の `failure_type` とは別用途である。
M17 の `failure_type` は Copilot 実行失敗、Langfuse unavailable、trace missing、wrong attributes、real data risk などの run / trace 取得・除外理由を記録する。
M23 の failure category は、取得済み trace や評価記録を人間が確認し、回答品質、診断可能性、比較プロトコル、改善対象を整理するために使う。
Sprint3 で仕様化する auto-approval は、Sprint3 の安全境界内で実行される限り `F-SCOPE` や `AP-AUTO-DECIDE` とは扱わない。
一方で、Sprint3 の範囲を超えた repository 修正、patch / diff 生成、commit / push / pull request、自動勝敗決定を行う提案は、引き続き `F-SCOPE` または `AP-AUTO-DECIDE` の対象とする。

M13 の task category ごとの代表的な anti-pattern は以下とする。

| ID | Task category | Anti-pattern | 判定の目安 |
| --- | --- | --- | --- |
| `AP-REF-CONTRACT-DRIFT` | `refactoring` | behavior contract drift | 既存 CLI 出力 key、既定 endpoint、content capture 設定、Resource Attribute 名など外部仕様を変える |
| `AP-REF-OVER-ABSTRACTION` | `refactoring` | over-abstraction | 単一用途の変更に不要な抽象化や依存を追加する |
| `AP-BUG-CAUSE-FIX-CONFLATION` | `bug-investigation` | cause-fix conflation | 再現条件、原因、修正案、回帰確認を分けずに説明する |
| `AP-BUG-MISSING-SYNTHETIC-REPRO` | `bug-investigation` | missing synthetic repro | 合成入力で期待値と実際値を示さず、実データや live Langfuse を必要条件にする |
| `AP-TEST-NONDETERMINISTIC` | `test-generation` | nondeterministic test plan | 外部サービス、時刻、ファイルシステム、ネットワークに依存するテストだけを提案する |
| `AP-TEST-MISSING-EDGE-CLASS` | `test-generation` | missing edge class | 正常系だけに偏り、境界値または異常系を欠く |
| `AP-REVIEW-MISSED-SEEDED-VIOLATION` | `code-review` | missed seeded violation | 明確な仕様違反、テスト不足、保守リスクを見落とす |
| `AP-REVIEW-PREFERENCE-OVER-SPEC` | `code-review` | preference over spec | 好みの指摘と仕様違反を混同する |

severity は `blocking`、`major`、`minor` の 3 値とする。
`blocking` は仕様違反、データ扱い違反、非スコープ逸脱、誤った結論につながるものに使用する。
`major` は結論や比較の信頼性を大きく下げるが、追加確認や軽微な修正で回復できるものに使用する。
`minor` は読みやすさ、記録粒度、補足根拠の不足に留まるものに使用する。

改善候補を後続 milestone で作る場合は、対象を `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` のいずれかに分類する。
この分類は人間採否のための提案ラベルであり、repository の自動修正や自動採用を意味しない。

M24 以降で diagnosis record を作る場合、1 行を 1 つの `(trace_id, failure_category_id, anti_pattern_id)` に対する分類記録として扱う。
同じ trace に複数の failure category や anti-pattern がある場合は、複数行で記録する。
列候補は `trace_id`、`task_id`、`task_category`、`client_kind`、`comparison_id`、`experiment_id`、`experiment_condition`、`prompt_version`、`agent_variant`、`task_run_index`、`failure_category_id`、`anti_pattern_id`、`severity`、`evidence_summary`、`recommended_improvement_target`、`review_status` とする。
`anti_pattern_id` は cross-cutting または task-specific の `AP-*` ID、または空欄とする。
`evidence_summary` には実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。
`review_status` は `needs-human-review`、`accepted-for-proposal`、`rejected` のいずれかとし、改善候補の採用、実装、勝敗判定を自動化するものではない。

M24 trace-to-diagnosis MVP では、M17 `failure_type` と M23 `failure_category_id` を混同しないこと、`fail` だけでなく `needs-review` の改善材料も分類できること、M21 の比較条件混同を扱えること、`evidence_summary` に実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めないことを確認する。

### 5.13 M24 trace-to-diagnosis MVP

M24 では、M23 taxonomy を使って人間が分類した diagnosis record を検証・整形する最小 CLI を追加する。
M24 は記録検証 MVP であり、trace からの自動診断、改善案生成、改善案評価、自動採用、自動 repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定は行わない。

Config CLI に以下を追加する。

```text
config-cli validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]
```

入力 JSON は top-level array、または `{ "diagnoses": [...] }` を許可する。
入力 CSV は固定 header を要求する。
出力 CSV / JSON は検証済み diagnosis record だけを出力する。

diagnosis record は、1 行を 1 つの `(trace_id, failure_category_id, anti_pattern_id)` に対する分類記録として扱う。
同じ trace に複数の failure category や anti-pattern がある場合は、複数行で記録する。

固定列は以下とする。

| 列 | 値 |
| --- | --- |
| `trace_id` | 対象 trace id |
| `task_id` | M13 task id または空欄 |
| `task_category` | `refactoring`、`bug-investigation`、`test-generation`、`code-review`、または空欄 |
| `client_kind` | `vscode-copilot-chat`、`copilot-cli`、または空欄 |
| `comparison_id` | M21 comparison id または空欄 |
| `experiment_id` | M12 / M21 `experiment_id` または空欄 |
| `experiment_condition` | M21 `experiment.condition` または空欄 |
| `prompt_version` | M21 `prompt.version` または空欄 |
| `agent_variant` | M21 `agent.variant` または空欄 |
| `task_run_index` | M12 / M21 `task_run_index` または空欄 |
| `failure_category_id` | M23 の `F-*` ID |
| `anti_pattern_id` | M23 の cross-cutting または task-specific `AP-*` ID、または空欄 |
| `severity` | `blocking`、`major`、`minor` |
| `evidence_summary` | raw content を含まない短い根拠 |
| `recommended_improvement_target` | `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` |
| `review_status` | `needs-human-review`、`accepted-for-proposal`、`rejected` |

検証ルールは以下とする。

- `failure_category_id` は M23 の failure category ID のみ許可する。
- `anti_pattern_id` は M23 の anti-pattern ID、または空欄のみ許可する。
- `severity`、`recommended_improvement_target`、`review_status` は上記の値域に固定する。
- `task_category` と `client_kind` は M12 / M13 / M21 の既存値に合わせる。
- `evidence_summary` は必須とし、raw prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を示す危険 pattern を含む場合はエラーにする。
- M17 `failure_type` は run / trace 取得・除外理由であり、M24 diagnosis record の列として受け付けない。

M24 の検証では synthetic fixture だけを使用し、live Langfuse 接続や実 trace content を必須にしない。

### 5.14 M25 improvement proposal generator

M25 では、M24 の検証済み diagnosis record から、人間が採否できる improvement proposal record を生成する最小 CLI を追加する。
M25 は proposal 生成までを対象とし、自動採用、自動実装、repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定は行わない。

Config CLI に以下を追加する。

```text
config-cli generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]
```

入力は M24 の diagnosis CSV / JSON と同じ形式とし、既存の diagnosis reader / validator を通して検証する。
`review_status` が `accepted-for-proposal` の diagnosis record だけを 1 行 1 proposal として出力する。
`needs-human-review` と `rejected` は proposal 出力対象外とする。

proposal record の固定列は以下とする。

| 列 | 値 |
| --- | --- |
| `proposal_id` | `proposal-0001` から出力順に採番する proposal id |
| `source_diagnosis_index` | 入力 diagnosis record の 1-based 行番号 |
| `trace_id` | 元 diagnosis の trace id |
| `task_id` | 元 diagnosis の task id または空欄 |
| `task_category` | 元 diagnosis の task category または空欄 |
| `client_kind` | 元 diagnosis の client kind または空欄 |
| `comparison_id` | 元 diagnosis の comparison id または空欄 |
| `experiment_id` | 元 diagnosis の experiment id または空欄 |
| `experiment_condition` | 元 diagnosis の experiment condition または空欄 |
| `prompt_version` | 元 diagnosis の prompt version または空欄 |
| `agent_variant` | 元 diagnosis の agent variant または空欄 |
| `task_run_index` | 元 diagnosis の task run index または空欄 |
| `failure_category_id` | 元 diagnosis の `F-*` ID |
| `anti_pattern_id` | 元 diagnosis の `AP-*` ID または空欄 |
| `severity` | 元 diagnosis の severity |
| `improvement_target` | 元 diagnosis の `recommended_improvement_target` |
| `evidence_summary` | 元 diagnosis の sanitized evidence summary |
| `proposal_title` | deterministic template で生成する短い提案名 |
| `proposal_summary` | failure category、anti-pattern、severity、evidence summary に基づく安全な要約 |
| `proposed_change` | improvement target に対する人間レビュー用の改善提案 |
| `acceptance_check` | 人間が採否前に確認する acceptance check |
| `human_review_status` | 常に `needs-human-review` |

`proposal_title`、`proposal_summary`、`proposed_change`、`acceptance_check` は deterministic template で生成し、LLM 呼び出しや外部サービス接続は行わない。
出力には実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。
`proposed_change` は具体的な patch、diff、repository file の編集内容、採用判断、優先順位、効果判定を表現しない。

### 5.15 M26 proposal evaluator

M26 では、M25 の improvement proposal record を入力として、人間承認前に必要な安全性、仕様整合、レビュー観点を deterministic に検証する最小 CLI を追加する。
M26 は proposal pre-review までを対象とし、改善案の採用、改善実装、repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定、改善効果判定は行わない。

Config CLI に以下を追加する。

```text
config-cli evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]
```

入力は M25 proposal CSV / JSON と同じ形式とする。
入力 JSON は top-level array、または `{ "proposals": [...] }` を許可する。
入力 CSV は M25 proposal 固定 header を要求する。
`human_review_status` は `needs-human-review` のみ許可する。

proposal evaluation record は、1 input proposal を 1 evaluation record として扱う。
固定列は以下とする。

| 列 | 値 |
| --- | --- |
| `proposal_id` | 元 proposal id |
| `source_diagnosis_index` | 元 proposal の source diagnosis index |
| `trace_id` | 元 proposal の trace id |
| `task_id` | 元 proposal の task id または空欄 |
| `task_category` | 元 proposal の task category または空欄 |
| `client_kind` | 元 proposal の client kind または空欄 |
| `comparison_id` | 元 proposal の comparison id または空欄 |
| `experiment_id` | 元 proposal の experiment id または空欄 |
| `experiment_condition` | 元 proposal の experiment condition または空欄 |
| `prompt_version` | 元 proposal の prompt version または空欄 |
| `agent_variant` | 元 proposal の agent variant または空欄 |
| `task_run_index` | 元 proposal の task run index または空欄 |
| `failure_category_id` | 元 proposal の `F-*` ID |
| `anti_pattern_id` | 元 proposal の `AP-*` ID または空欄 |
| `severity` | 元 proposal の severity |
| `improvement_target` | 元 proposal の improvement target |
| `proposal_title` | 元 proposal title |
| `proposal_evaluation_status` | `ready-for-human-approval`、`needs-revision`、`blocked` |
| `evaluator_findings` | sanitized evaluator finding |
| `required_human_checks` | 人間承認前に確認する項目 |
| `evaluator_notes` | 短い補足 |

`proposal_evaluation_status` は以下の意味で使用する。

| 値 | 意味 |
| --- | --- |
| `ready-for-human-approval` | proposal は schema、安全性、非スコープ境界、human review 前提を満たしている |
| `needs-revision` | proposal の方向性は評価可能だが、metadata または human review context が不足している |
| `blocked` | 自動採用、自動実装、patch / diff 生成、repository 修正、自動勝敗決定などの非スコープ表現がある |

M26 evaluator は deterministic rule のみを使用し、LLM 呼び出し、live Langfuse 接続、実 trace content、外部サービス接続は行わない。
出力には実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。
`ready-for-human-approval` は人間承認 workflow に渡せる pre-review 状態であり、proposal の採用、実装、優先順位、効果、勝敗を決めるものではない。

### 5.16 M27 human approval workflow

M27 では、M26 の proposal evaluation record を入力として、人間による承認・却下・保留の判断を記録する最小 CLI を追加する。
M27 は人間判断の記録までを対象とし、改善案の自動採用、自動実装、repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定、改善効果判定は行わない。

Config CLI に以下の 2 コマンドを追加する。

```text
config-cli record-human-decisions <evaluations.csv|evaluations.json> <decisions.csv|decisions.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-decision-template <evaluations.csv|evaluations.json> [--csv <output.csv>] [--json <output.json>]
```

#### generate-decision-template

M26 evaluation CSV / JSON を入力として、`proposal_evaluation_status` が `ready-for-human-approval` の evaluation record のみを対象に、空の human decision template を生成する。
`needs-revision` と `blocked` の evaluation は template に含めない。
入力 JSON は top-level array、または `{ "evaluations": [...] }` を許可する。
入力 CSV は M26 evaluation 固定 header を要求する。

#### record-human-decisions

M26 evaluation CSV / JSON と、人間が記入した decision CSV / JSON の 2 ファイルを入力として、以下を検証し、validated 出力を生成する。

- decision の `proposal_id` が evaluation に存在すること。
- `human_decision` が `approved`、`rejected`、`deferred` のいずれかであること。
- `approved` は `proposal_evaluation_status` が `ready-for-human-approval` の proposal にのみ許可する。`needs-revision` / `blocked` の proposal の `approved` は拒否する。
- `rejected` と `deferred` は、任意の `proposal_evaluation_status` の proposal に許可する。
- `decision_rationale` が空でないこと。
- decision record に安全でない content（実 prompt / response content、credential、secret、Base64 header、実 user identity）が含まれないこと。
- unknown column を拒否すること。

入力 JSON は top-level array、または `{ "decisions": [...] }` を許可する。
入力 CSV は human decision 固定 header を要求する。

#### human decision record schema

human decision record の固定列は以下とする。

| 列 | 型 / 値域 | 用途 |
| --- | --- | --- |
| `proposal_id` | string（必須） | 対象 proposal の ID |
| `human_decision` | `approved`、`rejected`、`deferred`（必須） | 人間の採否判断 |
| `decision_rationale` | string（必須） | 判断理由の sanitized summary |
| `approver_id` | string または null | 承認者の仮名または識別子。実名、実 email address は使用しない |
| `approved_at` | ISO 8601 datetime string または null | 判断日時 |
| `conditions_or_notes` | string または null | 条件付き承認の条件、保留理由、その他の補足 |

M27 の record / template 生成は deterministic rule のみを使用し、LLM 呼び出し、live Langfuse 接続、実 trace content、外部サービス接続は行わない。
出力には実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。
`approved` は人間が proposal の方向性を承認した記録であり、改善の自動採用、自動実装、repository 修正、patch / diff 生成を意味しない。

### 5.17 Sprint2 raw telemetry store MVP

Sprint2 MVP では、Langfuse に依存しない raw telemetry store と normalized dataset 生成を扱う。
目的は、Langfuse が起動していなくても、raw telemetry から既存 measurement schema と改善支援 CLI に接続できる最小 loop を作ることである。

#### 入力方式

Sprint2 MVP の既定入力は raw OTLP の file-based ingest とする。
保存済み raw OTLP JSON を Config CLI で取り込み、自前 HTTP receiver、常駐 process、独自 OTLP receiver は Sprint2 MVP に含めない。

Langfuse export は Sprint1 / M15 の `aggregate-measurements` 入力として維持するが、Sprint2 MVP の raw store 既定入力にはしない。
Collector output は将来候補とし、MVP では raw OTLP JSON と同じ file-based ingest で扱える範囲に留める。

#### raw store

raw store は SQLite をローカル PoC の既定とする。
既定 DB path は repository root 配下の `data/raw-store.db` とする。
`data/` は local runtime data として repository に commit しない。

Sprint2 MVP の schema version は `1` のみとし、migration tool は追加しない。
破壊的 schema 変更が必要になった場合、PoC の範囲では DB file の再作成を許容する。

raw record schema は以下を固定列とする。

| 列 | 型 / 値域 | 用途 |
| --- | --- | --- |
| `id` | SQLite `INTEGER PRIMARY KEY AUTOINCREMENT` | raw record の store-local identifier |
| `source` | `raw-otlp`、`collector-output`、`langfuse-export` | 入力元。MVP の既定は `raw-otlp` |
| `trace_id` | string または null | payload から取得できる trace id |
| `received_at` | ISO 8601 datetime string | raw store に取り込んだ時刻 |
| `resource_attributes_json` | JSON object string または null | Resource Attributes の raw JSON |
| `payload_json` | JSON string | 生 payload |
| `schema_version` | integer。MVP は `1` | raw store schema version |

`received_at` は取り込み時刻であり、payload 内の span start / end time とは区別する。
MVP では `trace_id`、`received_at`、`source` に index を付与する。

#### CLI interface

Config CLI に以下の command を追加する。

```text
config-cli ingest-raw <raw.json> --db <raw-store.db>
config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
```

`ingest-raw` は raw OTLP JSON file を SQLite raw store に取り込む。
`normalize-raw` は raw store または raw JSON file を入力として、M12 measurement schema に従う CSV / JSON を生成する。
`normalize-raw` の出力列、欠損値、unknown span / attribute の扱いは M12 / M15 / M16 の measurement schema と集計方針に従う。

既存 `aggregate-measurements` は Langfuse export 風 JSON 向け command として維持し、`normalize-raw` とは別 command にする。
両 command は同じ measurement schema に出力するが、入力形式と normalization adapter の責務は混ぜない。

#### Langfuse 非依存 loop

Sprint2 MVP で Langfuse が起動していない状態でも成功とみなす最小 loop は以下とする。

```text
ingest-raw
  -> normalize-raw
  -> validate-diagnoses
  -> generate-improvement-proposals
  -> evaluate-improvement-proposals
  -> generate-decision-template / record-human-decisions
```

最小 loop の automated verification は synthetic fixture で完結させる。
live Copilot / live Langfuse は手動確認として分離する。
README / `docs/getting-started.md` に Langfuse 非依存フローを追加するのは、Sprint2 MVP 実装と automated verification が完了した後とする。

#### data handling

PoC では raw payload の全量保存を許容する。
ただし、raw store は repository に commit しない。
実データ、顧客データ、credential、secret、Base64 header、実 user identity を含む payload は検証入力に使わない。
masking / redaction は Sprint2 MVP のスコープ外とし、共有環境または実データ検証前に別途仕様化する。

#### diagnosis boundary

Sprint2 MVP の `diagnose` は、引き続き人間分類 diagnosis record の validation として扱う。
trace から failure category / anti-pattern 候補を自動抽出する診断機能は Sprint2 MVP に含めない。
trace からの自動診断は Sprint3 で扱う。

### 5.18 Sprint2.5 ConfigCli maintainability and redacted real-trace E2E

Sprint2.5 では、Sprint3 の前提として Config CLI の保守性改善と実 Copilot trace 互換性の最小確認を行う。
この作業単位は GitHub Issue #24 を参照元とし、新しい product behavior ではなく既存 behavior の維持とレビュー容易性の改善を目的とする。

#### スコープ

Sprint2.5 で扱う内容は以下に限定する。

- `src/CopilotAgentObservability.ConfigCli/Program.cs` の責務分割。
- CSV / JSON 出力補助と CSV parsing / escaping の重複集約。
- 既存 CLI command 名、引数、出力形式、exit code の互換性維持。
- redacted input に限定した実 Copilot trace 由来データの raw data loop E2E 確認。
- Sprint2.5 の判断、検証結果、残リスクを sprint-local documents に記録すること。

Sprint2.5 では以下を扱わない。

- AppHost ローカルランチャー化。
- Aspire AppHost への resource 追加。
- trace-driven automatic diagnosis の実装。
- LLM 呼び出しによる診断・提案生成。
- 改善案の自動採用、自動実装、patch / diff 生成、repository 自動修正。
- 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity の repository 保存。

#### ConfigCli 分割方針

`Program.cs` は `Main` と最小 entry point に縮小する。
分割後も namespace は既存の `CopilotAgentObservability.ConfigCli` を維持する。
public API は追加せず、既存テストのためだけに不要な可視性拡大を行わない。

ファイル分割は 1 ファイル 1 主型を原則とし、ファイル名は主型名に一致させる。
ただし、`*ParseResult`、`*Defaults`、親型に密結合した nested record などの companion type は、親となる主型のファイルに同居させる。
`*ParseResult.cs` や `*Defaults.cs` を機械的に単独生成しない。
分割目的は型数を増やすことではなく、可読性、レビュー容易性、Sprint3 変更容易性を改善することである。

責務別の配置は以下を基本とする。

```text
src/CopilotAgentObservability.ConfigCli/
  Program.cs
  Cli/
    CliApplication.cs
    CliHelpText.cs
    Options/
  RawTelemetry/
  Measurements/
  Diagnosis/
  Improvements/
  Shared/
```

raw ingest / raw store / raw normalization は `RawTelemetry/`、measurement aggregation / output は `Measurements/`、diagnosis input / validation は `Diagnosis/`、improvement proposal / evaluation / human decision は `Improvements/`、横断的な補助型は `Shared/` に置く。

#### CSV / JSON 共通処理

CSV cell escaping、CSV line parsing、JSON output の共通処理は `Shared/` 配下の helper に集約する。
集約時も、既存の header、列順、CSV の空欄、JSON の `null`、末尾改行、indentation、invalid input error の意味を変更しない。
measurement / diagnosis / proposal / evaluation / human decision の出力差分は、既存 assertion または追加の focused assertion で確認する。

#### Redacted real-trace E2E

Sprint2.5 の実 Copilot trace 確認は、raw data loop が実 Copilot OTel 由来の形状に耐えるかを確認するための互換性チェックである。
これは Phase 1 / Sprint2 MVP の既定スコープを実データ収集へ広げるものではない。

repository に保存してよい evidence は redacted または sanitized された summary と手順記録に限定する。
実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity、実 content を含む raw payload は repository に保存しない。
redacted input を一時的に使用する場合も、保存可否を確認し、必要に応じて `tmp/` や `data/` など repository commit 対象外の場所に置く。

最小確認 flow は以下とする。

```text
redacted real Copilot trace
  -> ingest-raw
  -> raw-store.db
  -> normalize-raw
  -> measurement CSV/JSON
  -> diagnosis/proposal/evaluation/human decision
```

E2E evidence には、確認日時、環境、入力の redaction 方針、実行コマンド、生成物、確認済み項目、未確認項目を記録する。
measurement CSV / JSON では、取得可能な範囲で `trace_id`、`client_kind`、token、duration、error、tool call count が出力されることを確認する。

#### AppHost follow-up boundary

AppHost ローカルランチャー化は Sprint2.5 の実装対象外である。
別 Issue または後続 Sprint で扱う場合は、本書 9 の AppHost 方針を変更するかを先に確認し、AppHost を Phase 0 背景として維持するのか local launcher として再定義するのかを決める。
Aspire MCP 経由で logs / traces / environment を AI agent に公開する場合は、content capture 由来の sensitive telemetry を公開しない安全策を本書に明記してから実装する。

### 5.19 Sprint3 content-aware trace diagnosis and auto-decision foundation

Sprint3 では、Sprint2 raw data loop と Sprint2.5 maintainability の成果を前提に、trace content を含む観測データから診断候補、改善候補、自動採用判断へ接続する基盤を定義する。
Sprint3 は要件定義中であり、command contract、candidate schema、content evidence schema、auto-decision schema は sprint-local material で確定してから本書に反映する。

現時点で本書に確定する Sprint3 の対象は以下に限定する。

- trace-driven diagnosis candidate、improvement proposal candidate、auto-approval decision record を検討する。
- raw store、raw OTLP JSON、normalized measurement CSV / JSON を入力候補として扱う。
- 明示 opt-in 時に限り、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を sensitive local output に含めることを許容する。
- sensitive local output は repository に保存・commit しない。

Sprint3 では以下を扱わない。

- repository file の自動修正。
- patch / diff の生成。
- commit / push / pull request 作成。
- 自動勝敗決定。
- live service や network endpoint に依存する自動テスト。
- sensitive local output の repository 保存または commit。

実 repository 修正を伴う自動改善実装は Sprint4 以降の候補とする。
Sprint4 以降で扱う場合は、対象ファイル allowlist、dry-run、diff preview、rollback、テスト実行、commit 境界、失敗時の停止条件を先に仕様化する。
実装着手に必要な未決事項、初期 command 分割、schema 候補、起動確認結果は `docs/sprints/sprint3-trace-diagnosis/` に記録する。

## 6. セキュリティとデータ扱い

Phase 1 はローカル限定 PoC とし、Langfuse に投入するデータは合成データまたは検証用データを基本とする。
Sprint3 より前の既定手順では、実データ、機密情報、顧客データ、秘密情報を含む prompt / response / tool arguments / tool results は投入しない。
Sprint3 以降の content-aware diagnosis では、明示 opt-in の sensitive local output に限り、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を扱える。
これらの output は repository に保存・commit しない。

Phase 1 では content capture を有効化するが、masking / redaction 実装は行わない。
masking / redaction が必要になる実データ検証や共有環境検証は Phase 1 の既定スコープ外とする。

Phase 1 の保持期間は、content capture データと full trace を 30 日上限の目安とする。
不要になったローカル Langfuse データは Docker volume の削除を含む手順で削除できる状態にする。

共有環境や社内サーバーで運用する場合は、アクセス権、削除方法、保持期間、利用者周知を別途定義してから実施する。

## 7. 非スコープ

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
- Sprint2 MVP における自前 HTTP receiver
- 独自ログ収集エージェント
- Sprint2 MVP の SQLite raw store を超える生 OTel データの独自ストレージ
- 独自可視化 UI
- VS Code Agent Debug View 相当の UI
- VS Code workspaceStorage / chatSessions 監視を主方式にすること
- VS Code 内部ログやローカル履歴の解析
- M11-M22 における改善案生成
- M11-M22 における改善案の自動採用
- M11-M22 における改善案の自動実装
- 勝敗の自動決定
- 改善効果の自動判定
- Sprint3 以前における patch / diff 生成
- commit / push / pull request 自動化

これらが必要になった場合は、実装前に `docs/spec.md` を更新する。
Sprint3 では改善案の自動採用判断を扱う。
実 repository 修正を伴う自動改善実装と patch / diff 生成は Sprint4 以降の候補として扱う。

Sprint2 MVP の raw telemetry store は、本書 5.17 で定義した SQLite raw store に限定して扱う。

## 8. 検証方針

### 8.1 自動検証

Config CLI、AppHost、プロジェクトファイル、依存関係に触れた場合は、`dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` を実行する。
M9 で Collector example を変更した場合は、Docker が利用可能な環境で dummy `LANGFUSE_AUTH` を設定してから `docker compose -f infra/otel-collector/docker-compose.example.yml config` を実行し、Compose 構文を確認する。
M15 の集計 CLI / script を変更した場合は、合成 Langfuse export fixture で token、duration、tool call count、error count、欠損属性、未知 span 名を確認する。
Sprint2 の `ingest-raw` / `normalize-raw` または raw store schema を変更した場合は、合成 raw OTLP fixture と temp SQLite DB で ingest、normalize、欠損値、unknown span / attribute、content / credential 非保存対象の扱いを確認する。

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
計測時は `task.id`、`task.category`、`client.kind`、`task.run_index`、`experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant`、`repo.snapshot`、Langfuse trace id、除外理由を CSV 台帳に記録する。

## 9. Aspire AppHost の役割と使い分け

### 9.1 AppHost の現在の役割

本リポジトリの Aspire AppHost (`src/CopilotAgentObservability.AppHost`) は、Phase 0 でローカル Aspire Dashboard 疎通確認のために作成した最小構成である。
現在は空の AppHost（`CreateBuilder` → `Build().Run()` のみ）であり、resource は登録されていない。

AppHost は Phase 0 背景として維持する。
Phase 1 以降の主構成は Langfuse self-host Docker Compose であり、AppHost / Aspire Dashboard は observability backend としては使用しない。

### 9.2 AppHost への resource 追加方針

以下の理由により、AppHost に常駐 resource を追加しない。

| 対象 | 判断 | 理由 |
| --- | --- | --- |
| Config CLI | 追加しない | 設定サンプル出力・検証用の補助 CLI であり、長期実行プロセスではない。`AddExecutable` で AppHost に載せる具体的な価値がない。既存の `dotnet run` 手順を維持する |
| OTel Collector | 追加しない | `infra/otel-collector/docker-compose.example.yml` で管理済み。Aspire 側で重複管理する必然性がない |
| Langfuse | 追加しない | `tmp/langfuse/.env` + Docker Compose で管理済み。Phase 1 baseline を置き換えない |
| ServiceDefaults | 追加しない | ServiceDefaults を適用する具体的な Web アプリ / API / Worker project が存在しない |
| 新規 Web アプリ | 作成しない | 本リポジトリは Web アプリ repo ではなく、Copilot / CLI / OTel / Langfuse / 計測 CLI の検証 repo である |
| DB / Redis / Worker | 追加しない | 存在しない外部依存関係を推測で追加しない |

Config CLI を Aspire から呼び出す価値が明確になった場合は、`AddExecutable`、custom command、または通常の `dotnet run` 手順のいずれが適切かを評価してから追加する。

### 9.3 `aspire agent init` の採用と生成物の扱い

`aspire agent init` は、Aspire MCP server、skill files、agent 設定を生成するコマンドである。

M28 で `aspire agent init --workspace-root . --non-interactive --nologo` を実行し、以下の skill files が `.agents/skills/` に生成された。

| 生成された skill | 内容 | 本リポジトリでの扱い |
| --- | --- | --- |
| `aspire` | Top-level router。AppHost 検出、サブスキルへのルーティング | 維持する。AI agent が Aspire 関連操作を判断する際の入口として有用 |
| `aspire-orchestration` | start / stop / wait / describe / resource 操作 | 維持する。AppHost の起動・停止・状態確認に有用 |
| `aspire-monitoring` | logs / traces / otel / dashboard / export | 維持する。ローカル診断での telemetry 確認に有用 |
| `aspire-deployment` | deploy / publish / destroy / Azure / K8s | 削除した。本リポジトリでは本番デプロイを扱わない |
| `aspire-init` | 新規 Aspire project の作成 / skeleton drop | 削除した。本リポジトリには既に AppHost が存在する |

グローバル `~/.agents/skills/` にも同じ skill files がインストールされた。リポジトリローカルの `.agents/skills/aspire*` が存在する場合はローカル版が優先される。

生成された skill files は、AppHost が空であっても `aspire start` / `aspire describe` / `aspire logs` / `aspire otel` を通じた診断手順のガイダンスとして機能する。
ただし、skill files の指示はあくまで AI agent 向けのガイダンスであり、Phase 1 Langfuse baseline を置き換えるものではない。

### 9.4 Aspire MCP server の情報露出方針

Aspire MCP server は、AppHost の resource graph、logs、traces、environment を AI coding agent に公開する機能である。

現時点では AppHost が空であるため、MCP server 経由で露出する情報はない。
将来 resource を追加する場合は、以下を確認してから公開する。

- MCP 経由で AI に見せてよい resources / logs / traces の範囲を記録する。
- content capture 由来の prompt、response、tool arguments、tool results、credential、secret は MCP 経由で公開しない。
- sensitive resource / telemetry は `ExcludeFromMcp()` を適用する。
- 実 trace content、credentials、secrets、connection string を repository に保存しない原則は維持する。

### 9.5 Aspire を使う場面・使わない場面

| 場面 | Aspire を使うか | 理由 |
| --- | --- | --- |
| Phase 0 Aspire Dashboard 疎通確認 | 使用した（完了済み） | OTLP 受信と trace tree 確認の初期検証 |
| Phase 1 Langfuse PoC（主構成） | 使わない | Langfuse self-host Docker Compose が主構成 |
| OTel Collector 経由送信 | 使わない | `infra/otel-collector/docker-compose.example.yml` で管理 |
| 研究計測（M11-M22） | 使わない | Langfuse + 集計 CLI が主構成 |
| trace-driven improvement loop（M23-M27） | 使わない | Config CLI で完結 |
| AppHost のビルド確認 | 使う | `dotnet build CopilotAgentObservability.slnx` に含まれる |
| 将来の resource 追加時 | 検討する | resource を追加する判断が生じた場合に再評価する |
| 将来の Aspire MCP server 連携 | 検討する | AppHost に意味のある resource がある場合に再評価する |

Phase 1 Langfuse baseline を置き換えないことを明記する。
Aspire の採用判断は `docs/spec.md` のこのセクションに記録し、`AGENTS.md` からは参照だけする。

## 10. 参考資料

- [VS Code: Monitor agent usage with OpenTelemetry](https://code.visualstudio.com/docs/copilot/guides/monitoring-agents)
- [GitHub Docs: GitHub Copilot CLI command reference](https://docs.github.com/ja/enterprise-cloud%40latest/copilot/reference/copilot-cli-reference/cli-command-reference)
- [OpenTelemetry: GenAI Semantic Conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/)
- [Langfuse: OpenTelemetry for LLM Observability](https://langfuse.com/integrations/native/opentelemetry)
- [Langfuse: Docker Compose self-host deployment](https://langfuse.com/self-hosting/deployment/docker-compose)
- [Langfuse: Public API](https://langfuse.com/docs/api-and-data-platform/features/public-api)
- [Langfuse: Observations API](https://langfuse.com/docs/api-and-data-platform/features/observations-api)
- [Langfuse: Export from UI](https://langfuse.com/docs/api-and-data-platform/features/export-from-ui)
- [Langfuse: Export to Blob Storage](https://langfuse.com/docs/api-and-data-platform/features/export-to-blob-storage)
- [Aspire MCP server for AI coding agents](https://aspire.dev/get-started/aspire-mcp-server/)
- [AI と一緒に開発するときにも便利な Aspire の好きなところ](https://zenn.dev/microsoft/articles/aspire-favorite-points)
- [超dotnet new で「Aspire と GitHub Copilot の連携」というタイトルで登壇しました](https://zenn.dev/microsoft/articles/super-dotnet-new-aspire-copilot)

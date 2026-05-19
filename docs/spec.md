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
agent.variant
skill.version
mcp.profile
cli.wrapper.version
task.category
```

## 5. セキュリティとデータ扱い

Phase 1 はローカル限定 PoC とし、Langfuse に投入するデータは合成データまたは検証用データを基本とする。
実データ、機密情報、顧客データ、秘密情報を含む prompt / response / tool arguments / tool results は投入しない。

Phase 1 では content capture を有効化するが、masking / redaction 実装は行わない。
masking / redaction が必要になる実データ検証や共有環境検証は Phase 1 の既定スコープ外とする。

Phase 1 の保持期間は、content capture データと full trace を 30 日上限の目安とする。
不要になったローカル Langfuse データは Docker volume の削除を含む手順で削除できる状態にする。

共有環境や社内サーバーで運用する場合は、アクセス権、削除方法、保持期間、利用者周知を別途定義してから実施する。

### 5.1 Repository secret scan

M10 では repository hygiene として、GitHub Actions から gitleaks CLI による週次 secret scan を実行する。
これは Langfuse / OTel 観測データの DLP ではなく、repository に commit された secret を検知するための独立した workflow とする。

workflow は `.github/workflows/weekly-gitleaks-secret-scan.yml` とし、月曜 09:00 JST (`0 0 * * 1`) に実行する。
手動確認のため `workflow_dispatch` も有効にする。

scan 対象は repository の git 履歴全体とする。
`actions/checkout` は既存 workflow で使用している pinned SHA を使い、`fetch-depth: 0` で checkout する。
gitleaks CLI は `v8.30.1` の Linux x64 tarball を GitHub Releases から取得し、既知の SHA-256 checksum で検証してから実行する。
gitleaks-action は使用しない。

gitleaks は `gitleaks git --redact=100 --report-format=json --report-path <runner-temp-report> --exit-code 2 .` として実行する。
exit code `0` は finding なし、`2` は finding あり、その他は scan 失敗として扱う。
finding ありの場合は GitHub Issue を作成したうえで workflow を成功扱いにする。
scan 失敗、checksum 不一致、Issue 作成失敗は workflow 失敗とする。

Issue は検知のたびに毎回新規作成する。
Issue title は `[secret-scan] Gitleaks findings YYYY-MM-DD` とする。
Issue body には scan date/time JST、workflow run URL、repository、total finding count、RuleID、file、line、commit link、Fingerprint を記載する。
secret 値、match 文字列、secret の前後文脈、redacted report 全文は Issue body に記載しない。
gitleaks JSON report は runner temp のみに保存し、artifact upload しない。

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
- 改善案生成
- 改善効果判定
- patch / diff 生成
- commit / push / pull request 自動化

これらが必要になった場合は、実装前に `docs/spec.md` を更新する。

## 7. 検証方針

### 7.1 自動検証

Config CLI、AppHost、プロジェクトファイル、依存関係に触れた場合は、`dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` を実行する。
M9 で Collector example を変更した場合は、Docker が利用可能な環境で dummy `LANGFUSE_AUTH` を設定してから `docker compose -f infra/otel-collector/docker-compose.example.yml config` を実行し、Compose 構文を確認する。
M10 の weekly gitleaks workflow を変更した場合は、workflow YAML 構文と、合成 gitleaks JSON fixture による Issue body 生成を確認する。

### 7.2 ローカル起動確認

ローカル起動確認では以下を確認する。

- Docker Desktop が起動している。
- Langfuse self-host Docker Compose が起動する。
- `http://localhost:3000` で Langfuse UI に到達できる。
- 初期ユーザー、organization、project、API key を作成できる。
- 不要になったデータを Docker volume ごと削除できる手順を確認できる。
- M9 Collector 経由送信を確認する場合は、Langfuse 起動後に `infra/otel-collector/docker-compose.example.yml` で Collector を起動できる。

### 7.3 手動ライブ確認

Copilot 実行に依存する挙動は、自動テストだけで保証しない。
Phase 1 の手動ライブ確認では、Langfuse UI で以下を確認する。

- VS Code GitHub Copilot Chat の trace が取り込まれる。
- GitHub Copilot CLI の trace または metrics が取り込まれる。
- prompt、response、tool arguments、tool results が確認できる。
- token usage、duration、error が確認できる。
- `client.kind` と `experiment.id` で trace を識別できる。

M9 Collector 経由送信の手動ライブ確認では、クライアント送信先を `http://localhost:4318` に変更し、Langfuse UI で同じ確認項目を満たすことを確認する。

手動ライブ確認を実施した場合は、確認日時、実行環境、Langfuse 起動方式、設定値、Langfuse trace id または識別情報、確認できた項目、未確認項目を記録する。

## 8. 参考資料

- [VS Code: Monitor agent usage with OpenTelemetry](https://code.visualstudio.com/docs/copilot/guides/monitoring-agents)
- [GitHub Docs: GitHub Copilot CLI command reference](https://docs.github.com/ja/enterprise-cloud%40latest/copilot/reference/copilot-cli-reference/cli-command-reference)
- [Langfuse: OpenTelemetry for LLM Observability](https://langfuse.com/integrations/native/opentelemetry)
- [Langfuse: Docker Compose self-host deployment](https://langfuse.com/self-hosting/deployment/docker-compose)

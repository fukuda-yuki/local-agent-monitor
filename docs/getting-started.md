# 利用者向けガイド

この文書は、`copilot-agent-observability` を使って GitHub Copilot Chat / GitHub Copilot CLI の OpenTelemetry データをローカル Langfuse に送信し、trace を確認するための入口です。

詳細仕様の正は [docs/spec.md](spec.md) です。この文書は、初めて使う人が準備と確認の流れを把握しやすくするためのガイドです。

## 何のために使うか

このリポジトリは、Copilot の実行過程を観測するための PoC です。

確認したいもの:

- Agent invocation
- LLM call
- tool call
- prompt / response
- tool arguments / tool results
- token usage
- duration
- error
- VS Code GitHub Copilot Chat と GitHub Copilot CLI の挙動差分

目的ではないもの:

- Copilot の利用者数や利用回数の集計
- 課金・コスト配賦
- 勤務監視や個人別生産性評価
- DLP / 機密情報検査
- 改善案の自動実装
- repository の自動修正

## 全体像

現在の既定構成は、ローカル PC 上で Langfuse self-host を Docker Desktop で起動し、VS Code GitHub Copilot Chat と GitHub Copilot CLI から OTLP HTTP で直接送信する構成です。

```text
VS Code GitHub Copilot Chat
GitHub Copilot CLI
        |
        | OTLP HTTP
        v
Langfuse self-host
        |
        v
Langfuse UI
```

既定 URL:

| 用途 | URL |
| --- | --- |
| Langfuse UI | `http://localhost:3000` |
| Langfuse OTLP endpoint | `http://localhost:3000/api/public/otel` |
| Langfuse OTLP traces endpoint | `http://localhost:3000/api/public/otel/v1/traces` |

## 利用前チェック

申請や契約を確認するもの:

| 項目 | 確認内容 |
| --- | --- |
| GitHub Copilot | VS Code GitHub Copilot Chat を使えるアカウントまたは組織契約がある |
| GitHub Copilot CLI | CLI を認証済みで使える |
| Docker Desktop | 組織ポリシー上インストール・利用できる。必要ならライセンス条件を確認済み |

ローカルに必要なもの:

| 項目 | 用途 |
| --- | --- |
| VS Code | GitHub Copilot Chat の実行 |
| GitHub Copilot Chat extension | VS Code 側の OTel 送信対象 |
| GitHub Copilot CLI | CLI 側の OTel 送信対象 |
| Docker Desktop | Langfuse self-host と必要に応じた OTel Collector の起動 |
| .NET SDK | Config CLI の実行、build / test |
| PowerShell | 環境変数設定例の実行 |

現在のローカル PoC では不要なもの:

- Langfuse Cloud 契約
- Grafana / Tempo / Loki / Mimir
- 社内サーバー
- TLS 終端
- SSO
- 共有アクセス権設定

## 注意: content capture

Phase 1 では、Copilot の挙動を詳しく見るために content capture を有効化します。

Langfuse に保存され得るもの:

- user prompt
- response
- source code
- file contents
- tool arguments
- tool results
- system prompt
- tool schema
- path information
- repository information

ローカル PoC では、合成データまたは検証用データだけを使ってください。実データ、顧客データ、秘密情報、credential、secret、実 trace content は repository に保存しないでください。

## 1. Langfuse を起動する

Phase 1 の既定は Langfuse self-host Docker Compose です。Langfuse self-host の Compose ファイル自体は、このリポジトリには追加しません。公式手順に従ってローカルに配置し、Docker Desktop 上で起動します。

起動後、次を確認します。

1. Docker Desktop が起動している。
2. Langfuse self-host が起動している。
3. `http://localhost:3000` にブラウザでアクセスできる。
4. 初期ユーザー、organization、project を作成できる。
5. project の public key / secret key を取得できる。

public key / secret key は repository に保存しないでください。Base64 化済み header も保存しないでください。

## 2. Config CLI で設定サンプルを出す

このリポジトリには、VS Code / Copilot CLI に設定する値を出力する補助 CLI があります。

VS Code settings の例を出力:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-vscode-settings
```

VS Code プロセスに渡す環境変数の例を出力:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-vscode-env
```

GitHub Copilot CLI に渡す環境変数の例を出力:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-copilot-cli-env
```

Collector 経由送信を試す場合の設定サンプル:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- collector-vscode-settings
dotnet run --project src\CopilotAgentObservability.ConfigCli -- collector-vscode-env
dotnet run --project src\CopilotAgentObservability.ConfigCli -- collector-copilot-cli-env
```

Collector 経由送信は代替候補です。まずは Langfuse 直接送信で確認してください。

## 3. Langfuse 認証 header を作る

Langfuse の OTLP endpoint は Basic Auth を要求します。

PowerShell 例:

```powershell
$publicKey = "<public-key>"
$secretKey = "<secret-key>"
$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${publicKey}:${secretKey}"))
```

`${publicKey}:${secretKey}` のように変数名を区切る形にしてください。PowerShell の parser error を避けるためです。

## 4. VS Code GitHub Copilot Chat を設定する

VS Code settings の基本形:

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:3000/api/public/otel",
  "github.copilot.chat.otel.captureContent": true
}
```

認証 header は VS Code settings だけで表現できない場合があるため、VS Code プロセスへ環境変数で渡します。

```powershell
$env:COPILOT_OTEL_ENABLED="true"
$env:COPILOT_OTEL_ENDPOINT="http://localhost:3000/api/public/otel"
$env:COPILOT_OTEL_CAPTURE_CONTENT="true"
$env:OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT="http://localhost:3000/api/public/otel/v1/traces"
$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode-copilot-chat,experiment.id=baseline"
```

その PowerShell から VS Code を起動し、GitHub Copilot Chat で検証用の依頼を実行します。

```powershell
code .
```

## 5. GitHub Copilot CLI を設定する

GitHub Copilot CLI では、同じ Langfuse project の key から作った認証 header を使います。

```powershell
$env:COPILOT_OTEL_ENABLED="true"
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:3000/api/public/otel"
$env:OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT="http://localhost:3000/api/public/otel/v1/traces"
$env:OTEL_EXPORTER_OTLP_TRACES_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT="true"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=copilot-cli,experiment.id=baseline"
```

この状態で Copilot CLI を実行します。実行する依頼には、合成データまたは検証用データだけを使ってください。

## 6. Langfuse UI で確認する

`http://localhost:3000` を開き、対象 project の traces を確認します。

最低限確認するもの:

- trace が作成されている
- `client.kind=vscode-copilot-chat` または `client.kind=copilot-cli` が付いている
- `experiment.id=baseline` が付いている
- prompt / response が確認できる
- tool call が確認できる
- token usage が確認できる
- duration が確認できる
- error がある場合に確認できる

VS Code Chat と CLI の両方を確認する場合は、`client.kind` で区別します。

## 7. 研究用 dataset を作る場合

Langfuse から取得した trace / observation / usage / metadata を、content と identity-bearing 属性を除いた sanitized JSON にしてから集計します。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- aggregate-measurements <input.json> --csv <output.csv> --json <output.json>
```

出力は研究用 measurement schema に合わせた CSV / JSON です。実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めないでください。

## 8. Collector 経由送信を試す場合

通常は Langfuse 直接送信を使います。直接送信が不安定な場合や、後続の組織展開候補を試す場合だけ Collector 経由送信を使います。

Collector example は [infra/otel-collector/docker-compose.example.yml](../infra/otel-collector/docker-compose.example.yml) にあります。

構文確認では実 credential を使わないでください。

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

起動時は shell の環境変数で Langfuse 認証を渡します。`LANGFUSE_AUTH` の実値を repository に保存しないでください。

## よくある失敗

| 症状 | 確認すること |
| --- | --- |
| Langfuse UI が開けない | Docker Desktop と Langfuse container が起動しているか |
| trace が出ない | endpoint が `http://localhost:3000/api/public/otel` になっているか |
| 認証 error になる | public key / secret key と Basic Auth header が正しいか |
| VS Code だけ trace が出ない | VS Code を OTel 環境変数を設定した PowerShell から起動したか |
| CLI だけ trace が出ない | `COPILOT_OTEL_ENABLED` と `OTEL_EXPORTER_OTLP_*` が同じ shell に設定されているか |
| trace はあるが分類できない | `OTEL_RESOURCE_ATTRIBUTES` に `client.kind` と `experiment.id` が入っているか |
| 実データが混入した | 対象 trace を削除するか、必要に応じてローカル Langfuse volume を破棄する |

## どの文書を更新するか

利用手順や補足説明を足す場合:

- 入口の説明は [README.md](../README.md)
- 初回利用者向けの手順はこの文書
- 確定仕様や実装判断は [docs/spec.md](spec.md)
- 上位要件は [docs/requirements.md](requirements.md)
- repository 全体の milestone index は [docs/task.md](task.md)
- milestone ごとの作業記録は `docs/milestones/<milestone-slug>/`
- milestone を跨ぐ知見は [docs/knowledge/](knowledge/)

README やこの文書が [docs/spec.md](spec.md) と矛盾する場合は、仕様を先に確認してください。

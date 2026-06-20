# Telemetry Collection

この文書は Copilot clients から OpenTelemetry data を収集する利用者向け手順です。

## 全体像

```text
VS Code GitHub Copilot Chat
GitHub Copilot CLI
Codex App / app-server
        |
        | OTLP HTTP via collection profile
        v
Langfuse self-host / Collector / raw local receiver / saved raw OTLP JSON
```

Collection profile は `CAO_COLLECTION_PROFILE` で選びます。
最小 profile は `raw-only`、標準 full profile は `docker-desktop-langfuse` です。

Langfuse は標準 full profile の個別 trace viewer として使います。
Raw data loop と static dashboard は Langfuse UI に必須依存しません。

## Langfuse endpoint

既定 URL:

| 用途 | URL |
| --- | --- |
| Langfuse UI | `http://localhost:3000` |
| Langfuse OTLP endpoint | `http://localhost:3000/api/public/otel` |
| Langfuse OTLP traces endpoint | `http://localhost:3000/api/public/otel/v1/traces` |

Langfuse の public key / secret key と、それを Base64 化した authorization header は repository に保存しないでください。

PowerShell 例:

```powershell
$publicKey = "<public-key>"
$secretKey = "<secret-key>"
$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${publicKey}:${secretKey}"))
```

## VS Code GitHub Copilot Chat

VS Code settings の例:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-vscode-settings
```

VS Code process に渡す環境変数の例:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-vscode-env
```

代表値:

```powershell
$env:COPILOT_OTEL_ENABLED="true"
$env:COPILOT_OTEL_ENDPOINT="http://localhost:3000/api/public/otel"
$env:COPILOT_OTEL_CAPTURE_CONTENT="true"
$env:OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode-copilot-chat,experiment.id=baseline"
code .
```

## GitHub Copilot CLI

環境変数の例:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-copilot-cli-env
```

代表値:

```powershell
$env:COPILOT_OTEL_ENABLED="true"
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:3000/api/public/otel"
$env:OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic $auth,x-langfuse-ingestion-version=4"
$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT="true"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=copilot-cli,experiment.id=baseline"
```

## Codex App / app-server

Codex App の OTel routing config は user-level `$HOME\.codex\config.toml` に置きます。
project-local `.codex/config.toml` は routing source として扱いません。

TOML 例:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-codex-app-config
```

設定後は Codex App を再起動します。

## Collector 経由送信

Collector 経由送信は `docker-desktop-collector-langfuse`、`wsl2-docker-collector-langfuse`、`remote-managed-collector` profile で使います。
client 側から Langfuse credential を外し、Collector 側で Langfuse authorization header を付与できます。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- collector-vscode-env
dotnet run --project src\CopilotAgentObservability.ConfigCli -- collector-copilot-cli-env
dotnet run --project src\CopilotAgentObservability.ConfigCli -- collector-codex-app-config
```

Collector example を構文確認する場合:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

## WSL2 Docker Engine

`wsl2-docker-langfuse` と `wsl2-docker-collector-langfuse` では、Docker Engine と container は WSL2 distro 内で動き、VS Code / GitHub Copilot CLI / Codex App は Windows process として送信します。
そのため endpoint は WSL2 内からではなく、Windows client から到達できる host / port にしてください。

Profile-aware command の例:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile wsl2-docker-langfuse
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-copilot-cli-env --profile wsl2-docker-collector-langfuse
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-codex-app-config --profile wsl2-docker-langfuse
```

出力内の `<windows-reachable-wsl2-host>` は、Windows から到達できる値に置き換えます。
WSL2 localhost forwarding で publish 済み container port が Windows に公開される環境では `localhost` を使います。
それが使えない場合は live validation 時に現在の WSL2 distro address を解決し、machine-specific IP address は repository file に保存しないでください。

代表 endpoint shape:

```text
http://<windows-reachable-wsl2-host>:3000/api/public/otel
http://<windows-reachable-wsl2-host>:4318
```

## Live 確認項目

- trace が Langfuse に作成されている。
- `client.kind` と `experiment.id` が付いている。
- prompt / response / tool call / token usage / duration / error を確認できる。
- 実データや secret を repository に保存していない。

Remote managed Langfuse / Collector endpoint に送信する場合は、送信前に access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を決めてください。
この repository は remote / shared endpoint の利用者同意 workflow を実装しません。

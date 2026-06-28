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

Profile-aware VS Code environment の例:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile docker-desktop-langfuse
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

## Raw Local Receiver

`raw-local-receiver` profile は Langfuse や Collector を使わず、この repository の local receiver に telemetry を送ります。
Raw payload は prompt、response、tool arguments / results、identity attributes、credential-like strings を含み得るため、raw store と一時出力を repository に commit しないでください。

Receiver を foreground process として起動します。

```powershell
New-Item -ItemType Directory -Force data | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- serve-raw-local-receiver --db data\raw-store.db --url http://127.0.0.1:4319
```

別の shell で VS Code 用 environment を生成し、VS Code process に適用します。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver
```

代表値:

```powershell
$env:CAO_COLLECTION_PROFILE="raw-local-receiver"
$env:COPILOT_OTEL_ENABLED="true"
$env:COPILOT_OTEL_ENDPOINT="http://127.0.0.1:4319"
$env:COPILOT_OTEL_CAPTURE_CONTENT="true"
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://127.0.0.1:4319"
$env:OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode-copilot-chat,experiment.id=baseline"
code .
```

受信後は raw store を normalize します。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --json tmp\raw-local-receiver\measurements.json
```

Live VS Code direct telemetry は Sprint7 の未確認項目です。
検証時は VS Code version、GitHub Copilot Chat extension version、receiver command、raw store path、trace id または raw record id、confirmed / unconfirmed signals を記録してください。

## Local Ingestion Monitor（Sprint8）

LocalMonitor は loopback-only の単一 ASP.NET Core プロセスです。VS Code GitHub
Copilot Chat の OTLP HTTP/protobuf を `POST /v1/traces` で直接受信し、SQLite raw
store に永続化し、sanitized projection を生成し、ローカルブラウザ UI で取り込みの
健全性を確認できます。既定では **sanitized metadata のみ** を表示します。

仕様（正本）は次を参照してください。

- [docs/spec.md](../spec.md) Public Interfaces。
- [docs/specifications/layers/telemetry-ingestion.md](../specifications/layers/telemetry-ingestion.md)（receiver / port / health / UI / SSE）。
- [docs/specifications/security-data-boundaries.md](../specifications/security-data-boundaries.md)（raw / PII 境界）。
- [docs/decisions.md](../decisions.md) D020。

手順:

1. monitor を起動します（既定 loopback `127.0.0.1:4320`、Collector `4318` / CLI
   receiver `4319` を回避）。

   ```powershell
   dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\raw-store.db --url http://127.0.0.1:4320
   ```

2. 別 shell で VS Code を monitor へ向ける environment を生成します（`--target monitor`
   は endpoint を `4320` にします。既定 `--target receiver` は `4319` のまま）。

   ```powershell
   dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
   ```

   `--endpoint http://127.0.0.1:<port>` で非既定の loopback port を指定できます。

3. ブラウザで `http://127.0.0.1:4320/` を開きます。`/`（Overview）、`/ingestions`、
   `/traces`、`/diagnostics` の各ページと、通知専用 SSE `GET /events`、readiness
   `GET /health/ready` / `GET /health/live`、sanitized cursor API
   `GET /api/monitor/ingestions` / `GET /api/monitor/traces` を利用できます。
   既定ページ・API・SSE は sanitized metadata のみで、raw payload や PII を含みません。

raw body（tool arguments / results、sub-agent instructions / responses、system prompt）と
PII は **既定で表示されます**（server-rendered、inert text）。trace-detail page に inline
表示され、`GET /traces/{rawRecordId}/raw` でも個別の raw JSON を確認できます。
raw-bearing route は same-origin 限定（cross-site は `403`）、`Cache-Control: no-store`。

- `--sanitized-only` を付けて起動すると metadata-only モードになります（raw-bearing
  route は `404`、PII は除外）。画面共有や health-check 時に使用します。
- raw / PII は repository-safe artifacts には決して出力しません。raw store や一時出力
  を repository に commit しないでください。

VS Code GitHub Copilot Chat および GitHub Copilot CLI からの OTLP HTTP/protobuf 実機受信は
2026-06-27 に検証済みです。詳細は [Local Ingestion Monitor ユーザーガイド](local-monitor.md) を参照してください。

## GitHub Copilot CLI

環境変数の例:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-copilot-cli-env --profile docker-desktop-langfuse
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
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-codex-app-config --profile docker-desktop-langfuse
```

設定後は Codex App を再起動します。

## Collector 経由送信

Collector 経由送信は `docker-desktop-collector-langfuse`、`wsl2-docker-collector-langfuse`、`remote-managed-collector` profile で使います。
Remote managed Collector に送信する前に、access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を決めてください。
client 側から Langfuse credential を外し、Collector 側で Langfuse authorization header を付与できます。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile docker-desktop-collector-langfuse
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-copilot-cli-env --profile docker-desktop-collector-langfuse
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-codex-app-config --profile docker-desktop-collector-langfuse
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

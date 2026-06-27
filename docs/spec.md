# Technical Specification Index

この文書は Copilot Agent Observability の技術仕様入口である。
詳細は機能・レイヤーごとに分割した [docs/specifications/](specifications/) を正本とする。

## Source Of Truth

仕様判断は次の順で扱う。

1. [docs/requirements.md](requirements.md)
2. この文書
3. [docs/specifications/](specifications/)
4. [docs/architecture.md](architecture.md)
5. [docs/decisions.md](decisions.md)
6. 現行実装

`docs/sprints/` は履歴と根拠を残す場所であり、現行仕様の正本ではない。
Sprint 資料を根拠に製品仕様を変える場合は、先に `docs/requirements.md`、この文書、または `docs/specifications/` に反映する。

## Current Product Shape

Copilot Agent Observability は Local-first な Agent workflow observability 製品である。

現在の標準構成:

```text
Copilot clients
  -> OTLP HTTP
  -> Langfuse trace viewer
  -> saved raw OTLP JSON
  -> Config CLI
  -> SQLite raw store
  -> normalized measurements
  -> candidate records
  -> dashboard dataset
  -> static HTML dashboard
```

Collection profile は telemetry routing mode を表す public interface である。
Profile selector は `CAO_COLLECTION_PROFILE` とし、詳細は
[specifications/interfaces/collection-profiles.md](specifications/interfaces/collection-profiles.md) を正本とする。

最小構成は `raw-only` であり、Langfuse、Docker Desktop、WSL2 Docker Engine、Collector、remote endpoint、background process なしで saved raw OTLP JSON から raw data loop を実行する。
標準 full profile は `docker-desktop-langfuse` である。

`raw-local-receiver` は Langfuse なしで VS Code からこの repository の local receiver へ直接 telemetry を送信する profile であり、Sprint7 の実装対象とする。

Local Ingestion Monitor は、`raw-local-receiver` の telemetry をローカルで確認するための単一 ASP.NET Core プロセス（loopback-only）であり、Sprint8 の実装対象とする。Sprint7 の Config CLI receiver（`127.0.0.1:4319`）と並存し、別 loopback port（既定 `127.0.0.1:4320`、Collector の `4317`/`4318` と Sprint7 CLI receiver の `4319` を回避）で動作する。OTLP HTTP/protobuf を受信して SQLite raw store に永続化し、sanitized monitor projection、ローカル UI、health endpoint を提供する。既定では raw を表示せず、`--enable-raw-view` opt-in 起動時に限りローカル利用者が自分の raw / PII を loopback-only で閲覧できる。詳細は [specifications/layers/telemetry-ingestion.md](specifications/layers/telemetry-ingestion.md) と [specifications/security-data-boundaries.md](specifications/security-data-boundaries.md) を正本とする。

GitHub Pages publish は private repository と明示的な access control を前提にした候補である。
実データ由来 aggregate を publish する前に、Pages visibility、retention、削除方法、利用者周知を確認する。

## Specification Map

| Area | Spec |
| --- | --- |
| 実装仕様入口 | [specifications/README.md](specifications/README.md) |
| Telemetry ingestion | [specifications/layers/telemetry-ingestion.md](specifications/layers/telemetry-ingestion.md) |
| Raw store and normalization | [specifications/layers/raw-store-normalization.md](specifications/layers/raw-store-normalization.md) |
| Candidate pipeline | [specifications/layers/candidate-pipeline.md](specifications/layers/candidate-pipeline.md) |
| Dashboard publishing | [specifications/layers/dashboard-publishing.md](specifications/layers/dashboard-publishing.md) |
| Security and data boundaries | [specifications/security-data-boundaries.md](specifications/security-data-boundaries.md) |
| Collection profile interface | [specifications/interfaces/collection-profiles.md](specifications/interfaces/collection-profiles.md) |
| Config CLI interface | [specifications/interfaces/config-cli.md](specifications/interfaces/config-cli.md) |
| Normalized measurement dataset interface | [specifications/interfaces/measurement-dataset.md](specifications/interfaces/measurement-dataset.md) |
| Candidate record interfaces | [specifications/interfaces/candidate-records.md](specifications/interfaces/candidate-records.md) |
| Human-review record interfaces | [specifications/interfaces/human-review-records.md](specifications/interfaces/human-review-records.md) |
| Dashboard dataset interface | [specifications/interfaces/dashboard-dataset.md](specifications/interfaces/dashboard-dataset.md) |
| Contributor workflow | [contributor-guide.md](contributor-guide.md) |

## Public Interfaces

Publicly documented interfaces are:

- Config CLI command names, arguments, CSV / JSON output shape。
- Collection profile names and `CAO_COLLECTION_PROFILE` values。
- `OTEL_RESOURCE_ATTRIBUTES` keys and recommended values。
- Dashboard dataset JSON and CSV logical tables。
- Static dashboard artifact layout: `index.html` and `dashboard-data.json`。
- GitHub Pages snapshot layout: `/latest/` and `/YYYY-MM-DD/`。
- Data safety boundary for repository-stored files。
- Local Ingestion Monitor loopback endpoints: `POST /v1/traces`、`GET /api/monitor/ingestions`、`GET /api/monitor/traces`、`GET /health/live`、`GET /health/ready`、および SSE notification stream。`/api/monitor/*` と SSE は raw / PII を返さない（sanitized のみ）。`/health/ready` は飽和継続時に `503`（瞬間的 backpressure は `degraded` の `2xx`）を返し、`status` / `checks` / `degraded_reasons` を持つ機械可読 body を伴う。既定しきい値は ingestion-stall `10s` / projection-lag `60s`（設定可能）。
- Local Ingestion Monitor raw-detail route（opt-in）: `GET /traces/{rawRecordId}/raw`。server-rendered HTML で、raw / PII を返す唯一の経路（JSON raw API は提供しない）。`--enable-raw-view` 起動時のみ存在し（無効時は `404`）、same-origin 強制（cross-site は `403`）、`Cache-Control: no-store`。
- Local Ingestion Monitor run interface: loopback port（既定 `http://127.0.0.1:4320`）、`--port` / `--url`、`--enable-raw-view` opt-in flag、リクエスト本文サイズ上限 `--max-request-body-bytes`（既定 `31457280` bytes = 30 MiB、env `CAO_MONITOR_MAX_REQUEST_BODY_BYTES`）。`POST /v1/traces` は本文が上限を超えると `413` / `request_too_large` を返し raw を書かない。
- Local Ingestion Monitor client config: `config-cli profile-vscode-env --profile raw-local-receiver --target monitor`（または `--endpoint`）が monitor endpoint（既定 `http://127.0.0.1:4320`）向けの VS Code env を出力。`--target receiver` 既定は `4319` のまま。

Changing these requires updating the relevant specification file and tests.

## Validation

Code、project file、CLI behavior、workflow を変更した場合:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Collector example を変更した場合:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

User-facing docs を変更した場合:

- README と user guide のリンク先が存在すること。
- screenshot path が存在すること。
- 古い入口表現が直接入口文書に残っていないこと。

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

Local Ingestion Monitor は、`raw-local-receiver` の telemetry をローカルで確認するための単一 ASP.NET Core プロセス（loopback-only）であり、Sprint8 で実装した。Sprint7 の Config CLI receiver（`127.0.0.1:4319`）と並存し、別 loopback port（既定 `127.0.0.1:4320`、Collector の `4317`/`4318` と Sprint7 CLI receiver の `4319` を回避）で動作する。OTLP HTTP/protobuf を受信して SQLite raw store に永続化し、sanitized monitor projection、ローカル UI、health endpoint を提供する。Sprint9 で受信済み OTel テレメトリから per-span の sanitized projection（`monitor_spans`）を追加し、agent-execution view としてツール / MCP 呼び出し名、成否、sub-agent のモデル / トークン、turn 単位トークンを表示する。Sprint10 で TraceDetail ページに4つの設計ビュー（Summary / Timeline / Flow Chart / Cache）をタブ UI として追加し、Flow Chart は当初 vendored Cytoscape.js + dagre（MIT、`wwwroot/vendor/`、CDN 不使用）でレンダリングした（Sprint12 で素の DOM へ置換。後述）。全ビューは既存の sanitized spans API（`GET /api/monitor/traces/{traceId}/spans`）のみを消費し、raw 境界・PII 境界の変更はない。ビジュアルテーマは VS Code Dark+ を基盤とし（D027）、タイポグラフィは vendored Noto Sans JP / Noto Sans Mono（D028、`wwwroot/vendor/fonts/`、OFL）。raw body と PII は既定で表示する（server-rendered、inert text）。`--sanitized-only` フラグで metadata-only モードを復元できる。Sprint12 で UI を刷新し、ダッシュボード（`/`）とトレース一覧（`/traces`）に各トレースの代表ユーザープロンプトを server-rendered 表示してトレースをプロンプトで識別できるようにした（raw-bearing 拡張。same-origin / `Cache-Control: no-store` / `--sanitized-only` 除去を強制し、`/api/monitor/*`・SSE の sanitized 不変条件は維持。D032）。Flow Chart は Cytoscape を廃し、Span Tree（インデント + ウォーターフォールバー）と DOM フローの toggle 切替として素の DOM で再実装した（D033）。`/ingestions` ページは廃止してダッシュボードへ統合し、UI は日本語化、ナビは3項目（ダッシュボード / トレース / 診断）に集約した。Windows では `scripts/local-monitor/` の PowerShell scripts により user-level Windows Task Scheduler の logon startup を任意登録できる。詳細は [specifications/layers/telemetry-ingestion.md](specifications/layers/telemetry-ingestion.md) と [specifications/security-data-boundaries.md](specifications/security-data-boundaries.md) を正本とする。

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
- Local Ingestion Monitor loopback endpoints: `POST /v1/traces`、`GET /api/monitor/ingestions`、`GET /api/monitor/traces`、`GET /api/monitor/traces/{traceId}/spans`、`GET /health/live`、`GET /health/ready`、および SSE notification stream。`/api/monitor/*` と SSE は raw / PII を返さない（sanitized のみ）。`/health/ready` は飽和継続時に `503`（瞬間的 backpressure は `degraded` の `2xx`）を返し、`status` / `checks` / `degraded_reasons` を持つ機械可読 body を伴う。既定しきい値は ingestion-stall `10s` / projection-lag `60s`（設定可能）。
- Local Ingestion Monitor raw-bearing routes（既定表示）: trace-detail page（agent-execution view、bounded raw preview inline + full raw record link）、`GET /traces/{rawRecordId}/raw`（server-rendered HTML）、および ダッシュボード（`/`）と トレース一覧（`/traces`）。後者2つは各トレースの代表ユーザープロンプトを server-rendered で表示する（raw store の OTLP payload から抽出、truncated、escaped inert text。prompt ラベルのみ raw でその他列は sanitized metadata。D032）。raw-bearing route set の全 route で same-origin 強制（cross-site は `403`）、`Cache-Control: no-store`。`--sanitized-only` 起動時は raw-bearing route / raw section を除去（raw-detail route は `404`、dashboard / traces の prompt ラベルは省略し短縮 TraceId にフォールバック）、PII は除外。prompt ラベルは server-rendered のみで `/api/monitor/*` と SSE には含めない。JSON raw API は提供しない。
- Local Ingestion Monitor run interface: loopback port（既定 `http://127.0.0.1:4320`）、`--port` / `--url`、`--sanitized-only`（metadata-only モード。raw-bearing route を `404` にし PII を除外）、リクエスト本文サイズ上限 `--max-request-body-bytes`（既定 `31457280` bytes = 30 MiB、env `CAO_MONITOR_MAX_REQUEST_BODY_BYTES`）。`POST /v1/traces` は本文が上限を超えると `413` / `request_too_large` を返し raw を書かない。
- Local Ingestion Monitor Windows startup scripts: `scripts/local-monitor/start.ps1`、`stop.ps1`、`status.ps1`、`install-startup-task.ps1`、`uninstall-startup-task.ps1`。Task Scheduler task の既定名は `CopilotAgentObservability LocalMonitor`、trigger は current user logon、既定 URL は `http://127.0.0.1:4320`、既定 DB / logs / state は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下。Task 登録 script は client routing 設定を書き換えない。
- Local Ingestion Monitor client config: `config-cli profile-vscode-env --profile raw-local-receiver --target monitor`（または `--endpoint`）が monitor endpoint（既定 `http://127.0.0.1:4320`）向けの VS Code env を出力。`--target receiver` 既定は `4319` のまま。
- Canvas adapter は raw default の Local Monitor と併用できる。`--sanitized-only` は Canvas の必須起動条件ではなく、Local Monitor の任意 metadata-only opt-out である。Canvas actions は引き続き既存 sanitized `/api/monitor/*` と readiness を読む bounded DTO surface であり、action responses / logs / committed outputs に raw / PII を返さない。

Changing these requires updating the relevant specification file and tests.

## Validation

Code、project file、CLI behavior、workflow を変更した場合:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

LocalMonitor browser smoke test が solution test suite に含まれるため、build と test の間に Playwright chromium bootstrap が必要。wrapper は未指定時に `PLAYWRIGHT_BROWSERS_PATH` を repository-local の ignored `artifacts\playwright-browsers` に設定し、browser binaries と Playwright cache lock を writable workspace 内に置く。Linux CI では同じ script に `-WithDeps` を付ける。

Collector example を変更した場合:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

User-facing docs を変更した場合:

- README と user guide のリンク先が存在すること。
- screenshot path が存在すること。
- 古い入口表現が直接入口文書に残っていないこと。

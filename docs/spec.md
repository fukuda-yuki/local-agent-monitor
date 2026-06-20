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
| Config CLI interface | [specifications/interfaces/config-cli.md](specifications/interfaces/config-cli.md) |
| Normalized measurement dataset interface | [specifications/interfaces/measurement-dataset.md](specifications/interfaces/measurement-dataset.md) |
| Candidate record interfaces | [specifications/interfaces/candidate-records.md](specifications/interfaces/candidate-records.md) |
| Human-review record interfaces | [specifications/interfaces/human-review-records.md](specifications/interfaces/human-review-records.md) |
| Dashboard dataset interface | [specifications/interfaces/dashboard-dataset.md](specifications/interfaces/dashboard-dataset.md) |
| Contributor workflow | [contributor-guide.md](contributor-guide.md) |

## Public Interfaces

Publicly documented interfaces are:

- Config CLI command names, arguments, CSV / JSON output shape。
- `OTEL_RESOURCE_ATTRIBUTES` keys and recommended values。
- Dashboard dataset JSON and CSV logical tables。
- Static dashboard artifact layout: `index.html` and `dashboard-data.json`。
- GitHub Pages snapshot layout: `/latest/` and `/YYYY-MM-DD/`。
- Data safety boundary for repository-stored files。

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

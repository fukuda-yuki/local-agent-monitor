# Architecture

## 1. System Context

```text
VS Code GitHub Copilot Chat
GitHub Copilot CLI
Codex App / app-server
        |
        | OTLP HTTP via collection profile
        v
Langfuse self-host / Collector / raw local receiver / saved raw OTLP JSON
        |
        | raw telemetry / export / saved raw OTLP JSON
        v
Config CLI
        |
        +--> SQLite raw store
        +--> normalized measurements
        +--> diagnosis / improvement / auto-decision records
        +--> dashboard dataset
        v
Static HTML dashboard
        |
        v
GitHub Pages / gh-pages snapshots
```

Collection profile は telemetry routing mode を表す public interface とする。
`raw-only` は最小構成、`docker-desktop-langfuse` は標準 full profile である。

Langfuse は標準 full profile の個別 trace viewer として使う。
改善支援 loop と dashboard は Langfuse UI に必須依存せず、saved raw OTLP JSON、SQLite raw store、normalized dataset を主入力にできる。

## 2. Primary Components

### Clients

- VS Code GitHub Copilot Chat: 必須 telemetry source。
- GitHub Copilot CLI: 必須 telemetry source。
- Codex App / app-server: 任意 telemetry source。OTel routing config は user-level `~/.codex/config.toml` を source of truth とし、project-local `.codex/config.toml` には依存しない。

### Langfuse

- Standard full profile の local-first trace viewer。
- OTLP HTTP endpoint は `http://localhost:3000/api/public/otel`。
- gRPC 送信は使わない。
- Trace detail、prompt、response、tool call、token usage、duration、error の調査先として維持する。
- 改善 loop の唯一の source of truth にはしない。

### OpenTelemetry Collector

- Langfuse 直接送信が不安定な場合、または組織展開を見据えた required profile。
- Collector は Langfuse 認証を集約し、client 側には Langfuse credential を置かない構成を取れる。
- 初期 example は trace pipeline のみを扱う。
- masking、sampling、TLS、SSO、共有環境運用は事前決定が必要。

### Raw Local Receiver

- Langfuse なしで VS Code / Copilot clients から telemetry を直接受ける local receiver。
- `raw-local-receiver` profile の実装対象。
- 初期 required path は repository-local execution とし、packaged exe install は要求しない。
- IIS / IIS Express hosting は company-managed Windows PC で有効な場合の候補とする。
- Raw prompt、response、tool arguments、tool results、local path、identity attributes、credential-like values を受け取り得るため、出力は local runtime data とし repository に commit しない。
- Normalized measurement、candidate、dashboard dataset の schema は変更しない。

### Config CLI

Config CLI は repository-local な中核ツールである。

主な責務:

- VS Code / Copilot CLI / Codex App 向け OTel 設定サンプル出力。
- raw OTLP JSON の ingest。
- SQLite raw store への保存。
- raw store から normalized measurement への変換。
- diagnosis record の validation。
- improvement proposal / candidate generation。
- auto-decision generation。
- dashboard dataset generation。
- static HTML dashboard generation。

### SQLite Raw Store

- local raw telemetry store。
- saved raw OTLP JSON を file-based ingest する。
- `raw-local-receiver` profile から受信した raw telemetry も保存できる。
- PostgreSQL は共有環境、長期保持、検索性能が必要になった場合の将来候補に留める。

### Candidate Pipeline

Trace 由来の deterministic pipeline は以下を生成する。

- diagnosis candidate。
- improvement candidate。
- auto-decision record。
- existing human-review record への adapter output。
- sensitive bundle metadata。

Repository patch / diff の生成、file の自動修正、commit / push / PR 作成には接続しない。

### Static HTML Dashboard

- Agent workflow aggregate view。
- `generate-dashboard-dataset` の JSON を入力にする。
- `generate-static-dashboard` が `index.html` と `dashboard-data.json` を出力する。
- Server-side API、runtime service、network dependency を要求しない。
- Raw prompt / response / tool arguments / tool results の全文は表示しない。

### GitHub Actions And Pages

- `schedule` と `workflow_dispatch` で dashboard publish workflow を実行する。
- `artifacts/dashboard-input/measurements.json` があれば sanitized input として扱う。
- input がなければ synthetic fixture から preview dashboard を生成する。
- Publish layout は `/latest/` と `/YYYY-MM-DD/`。
- Daily snapshots は `gh-pages` branch と Pages artifact に保持し、main branch には generated snapshot を commit しない。

### Module / Solution Structure

Sprint8 M1 で共有コンポーネントを 2 つの class library に抽出した。依存方向は下層から上層への単方向とする。

```text
CopilotAgentObservability.Telemetry           OTLP decode / attribute 変換 / raw ingest / raw record model / measurement normalization / sanitization
        ^
        |
CopilotAgentObservability.Persistence.Sqlite  SQLite raw store access (RawTelemetryRecord を永続化)
        ^
        +------------------------------+
        |                              |
CopilotAgentObservability.ConfigCli    (将来) CopilotAgentObservability.LocalMonitor
```

- `Telemetry` と `Persistence.Sqlite` は `ConfigCli` を参照しない（単方向依存）。
- 抽出した型は internal のままとし、`InternalsVisibleTo` で `ConfigCli` / `ConfigCli.Tests`（および将来の `LocalMonitor`）にのみ可視とする。public な共有 API は M1 では定義しない。
- Sprint8 の Local Ingestion Monitor（ASP.NET Core host、M2 以降）はこれらの共有 module を再利用する前提とする。ConfigCli の外部動作・CLI 表面は M1 で変更しない。

## 3. Data Flows

### Live Trace Review

```text
Copilot client
  -> OTLP HTTP via docker-desktop-langfuse
  -> Langfuse
  -> human trace review
```

用途:

- OTel emit の確認。
- span tree、prompt、response、tool arguments、tool results、token usage、duration、error の確認。
- `client.kind` と `experiment.id` による識別。

### Raw Data Loop

```text
saved raw OTLP JSON
  -> ingest-raw
  -> SQLite raw store
  -> normalize-raw
  -> measurements CSV / JSON
```

用途:

- Langfuse UI に依存しない再現可能な集計。
- unknown span / missing attribute の検出。
- baseline / variant 比較の入力。

### Raw Local Receiver Loop

```text
Copilot client
  -> OTLP HTTP via raw-local-receiver
  -> repository-hosted local receiver
  -> SQLite raw store or saved raw OTLP JSON
  -> normalize-raw
  -> measurements CSV / JSON
```

用途:

- Langfuse なしで VS Code から直接 raw data loop に接続する。
- 追加インストールが難しい company-managed PC でも repository-local execution で検証できる path を提供する。

### Improvement Support Loop

```text
measurements
  -> diagnosis candidates
  -> improvement candidates
  -> auto-decisions
  -> human review records
```

用途:

- deterministic rule による診断候補生成。
- content-aware evidence extraction。
- 改善候補と自動採用判断 record の生成。
- human review pipeline との互換維持。

### Dashboard Publish

```text
measurements + optional raw/candidate outputs
  -> generate-dashboard-dataset
  -> generate-static-dashboard
  -> latest/ and YYYY-MM-DD/
  -> GitHub Pages
```

用途:

- Run Overview。
- Agent / Tool Behavior。
- Prompt / Skill / Instructions。
- Baseline vs Variant。
- Diagnosis / Improvement Loop。
- Collection Health。
- Outcome Linkage Candidate。

## 4. Storage Boundaries

Allowed in repository:

- synthetic fixture。
- redacted evidence summary。
- normalized measurement。
- sanitized dashboard dataset。
- intentionally published static dashboard artifact。
- reference id such as trace id, candidate id, evidence ref。

Not allowed in repository:

- raw prompt / response。
- tool arguments / tool results。
- source code fragment or file contents from observed sessions。
- credential, secret, token, password, API key。
- Base64 authorization header。
- sensitive bundle content。
- sensitive bundle local path。

Sensitive local output may be generated only by explicit opt-in commands.
It must include expiry metadata and delete target paths, but automatic deletion is not implemented.

## 5. Aspire AppHost Boundary

The Aspire AppHost is retained only as historical local dashboard connectivity background and build coverage.
It is currently empty and has no registered resources.

Do not add long-running resources to AppHost by default:

- Langfuse remains Docker Compose based.
- OTel Collector remains Docker Compose based.
- Config CLI remains a command-line tool.
- No ServiceDefaults, Web app, DB, Redis, or worker should be inferred.

If AppHost resources are added later, decide what may be exposed through Aspire MCP first.
Prompt, response, tool arguments, tool results, credentials, secrets, and sensitive telemetry must not be exposed through MCP by default.

## 6. Deployment Boundary

Current default is local-first use plus optional private GitHub Pages dashboard.
Shared or production deployment is not decided.

Remote managed Langfuse / Collector profiles are routing profiles only.
This repository documents warnings and placeholder configuration, but does not
implement user consent workflow or shared-service governance.

Before shared or production use, define:

- repository / Pages access control。
- retention。
- delete process。
- masking / redaction。
- user notice or consent。
- identity handling。
- secret handling。
- live workflow operation。
- snapshot growth monitoring。

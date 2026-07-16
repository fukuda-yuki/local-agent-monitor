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
```

Issue #51 adds a parallel local Session path inside the installed Local Monitor:

```text
Canvas/App SDK stream ----+
Copilot-compatible Hook --+--> Session event ingest --> Session subsystem
existing OTel projection -+--> exact-link enrichment --> Session workspace reads
                                      |
                                      +--> Canvas Evidence exact trace composition
                                           |-- agent-graph (sole ownership model)
                                           +-- all sanitized span pages
Session sanitized events -----------------> timeline as Session / unowned
```

This path does not replace or alter the OTLP receiver and existing monitor
projection.

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
- versioned user-scoped configuration ownership ledger and immutable setup
  plan/apply/rollback/status coordination.
- GitHub Copilot VS Code/CLI detection and bounded setup adapters; App/SDK
  remains caller-managed guidance.
- Claude Code CLI detection and bounded user-settings ownership for Windows
  native and explicitly opted-in WSL2; Agent SDK remains caller-managed
  Python/TypeScript guidance.

The setup framework lives in the Config CLI rather than Local Monitor HTTP/UI.
The Windows Release ZIP publishes the Config CLI beside the Local Monitor app
and the setup PowerShell wrapper invokes the same command/result contract used
in repository mode. This introduces no reverse reference from lower libraries
to Config CLI and no new Local Monitor route.

### SQLite Raw Store

- local raw telemetry store。
- saved raw OTLP JSON を file-based ingest する。
- `raw-local-receiver` profile から受信した raw telemetry も保存できる。
- PostgreSQL は共有環境、長期保持、検索性能が必要になった場合の将来候補に留める。

### Session Subsystem

- Issue #51 の Session / Run / Event identity、native ID mapping、event content、
  post-projection OTel enrichment cursor を所有する。
- additive tables は `sessions`、`session_native_ids`、`session_runs`、
  `session_events`、`session_event_content`、`session_projection_state`。
- `RawTelemetryStore.cs` にこの責務を追加しない。
- local Session / Run / Event ID は UUIDv7 string。merge は identical native
  session ID、explicit resume/handoff、exact trace context のみに限定し、
  repository / timestamp proximity は使わない。
- raw content は secret-filter 後に metadata と分離して保存し、capture から
  90 日で expiry。物理削除 / pin / delete-now は Issue #57 に残す。

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

### Session Workspace Loop

```text
Canvas ctx.sessionId / SDK persisted events / PascalCase Hooks
  -> POST /api/session-ingest/v1/events
  -> separate Session tables
existing monitor projection
  -> dedicated session_projection_state cursor
  -> exact-linked OTel enrichment
  -> sanitized /api/session-workspace reads
  -> same-origin no-store event content read
```

Canvas capture begins at first open. Missed earlier events are not reconstructed
and lower completeness. Ephemeral usage is aggregated; reasoning and deltas are
not persisted. OTel enrichment runs after existing projection and leaves the
OTLP receiver, trace/span schema, and readiness contract unchanged.

### Canvas Effect Verification Loop

```text
exact Session / Run / trace + objective or human quality evidence
  + pinned proposal revision + active application receipt
  -> user-confirmed pre/post/excluded cohort
  -> pure quality-first verdict engine
  -> immutable effect receipt
  -> atomic proposal Verified transition only for improved
```

Objective evaluation, cohort, and effect data live in additive Session-store
tables. They contain sanitized identifiers and numeric summaries only. The
verdict engine compares quality before efficiency and never reads raw content,
paths, diffs, or repository artifacts. A rollback preserves historical effect
evidence but invalidates it for active-effect display.

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

### Reversible Configuration Setup Loop

```text
GitHub Copilot or Claude Code target detection
  -> redacted immutable setup plan + base hashes
  -> explicit change-set apply
  -> flushed backup + per-file/per-env-member write-ahead intents
  -> atomic physical-file / recoverable current-user env mutation
  -> ownership ledger + static verification
  -> hash-guarded change-set rollback
```

VS Code policy/environment/user-setting precedence and Copilot CLI user
environment are observed through injected platform boundaries. Filesystem and
environment writes are serialized by a non-waiting exclusive setup lock.
Compensation runs in reverse journal-step order. `status` requests no new setup
mutation, but it can restore an interrupted transaction before projecting the
ledger. No setup result is evidence of a first trace. GitHub Copilot receipt
remains the downstream Doctor integration, while Claude Code first-real-trace
evidence is Issue #104.

Claude user settings use a dedicated ownership-aware renderer for nested
`env` and `hooks`. The private plan holds only the approved owned values and
the expected complete-state hash; the complete rendered document is transient
under the setup lock. Windows native uses the existing atomic file boundary.
WSL2 execution is detected from the Linux process, distro environment, and
kernel marker together and requires an explicit CLI opt-in plus a successful
loopback readiness probe from that same process. The architecture deliberately
does not add a Windows-to-WSL mutation bridge, gateway discovery, non-loopback
listener, or Local Monitor HTTP/UI management endpoint.

### Dashboard Artifact Generation

```text
measurements + optional raw/candidate outputs
  -> generate-dashboard-dataset
  -> generate-static-dashboard
  -> index.html + dashboard-data.json
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
- intentionally shared static dashboard artifact。
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

Issue #51 Session content is another local raw-bearing storage class. It is
secret-filtered, stored separately from Session metadata, and expires for reads
at `captured_at + 90 days`; expired reads report `expired_pending_deletion`.
Automatic physical deletion remains Issue #57 scope.

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

Current default is local-first use with local static dashboard artifacts.
Shared or production deployment is not decided.

Remote managed Langfuse / Collector profiles are routing profiles only.
This repository documents warnings and placeholder configuration, but does not
implement user consent workflow or shared-service governance.

Before shared or production use, define:

- shared artifact access control。
- retention。
- delete process。
- masking / redaction。
- user notice or consent。
- identity handling。
- secret handling。
- live operation。

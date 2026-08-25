# Architecture

## 1. System Context

```text
VS Code GitHub Copilot Chat
GitHub Copilot CLI
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

`Codex App / app-server` は Issue #92 `NO-GO` の planned / blocked candidate
であり、この active collection flow には含まれない。

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

Issue #154 separates immutable source-version observation from current
interpretation and current Skill claims. OTel and SDK enter the same claim/read
authority through different predicates:

```text
validated retained OTel input
  -> immutable trace-version observation
  -> SourceCompatibilityReconciler (decoder/registry revision only)
  -> append-only interpretation head + trace compatibility revision
  -> exact OTel Skill generation/frontier/queue
  -> Retention-fenced worker

exact SDK Session/Event + full compatibility tuple
  -> seven-authority Skill snapshot transaction (after #158 gate closure)
  -> independent SDK claim subspace (no trace generation)

OTel current claim + SDK current claim
  -> single current-valid Skill read service
  -> merge only on exact producer trace ID + span ID
```

Ordinary validated OTel ingestion and the reconciler call the same
transaction-aware generation participant. The worker cannot discover a
different raw set at run time, and a raw Skill snapshot cannot become claim
authority.

The installed Session ingest v1 keeps its frozen wire and supports
`skill.started | skill.completed`; `skill.invoked` is unsupported there. The
accepted Skill-only v2 writer, `skill_invocation_snapshot:1`, historical
body/path reader, discovery/current-file service and raw-local routes are
additive raw-default-only components. D083 closes the decision gate; production
registration remains blocked by the prerequisite implementation, focused,
platform, signed-in live, full-validation, and review gates in
[Skill Invocation Snapshot](specifications/interfaces/skill-invocation-snapshot.md).
There is no v1 fallback, compatibility writer or dual path.
Current-file discovery takes roots only from the exact repeatable startup CLI
options `--skill-discovery-project-path` and
`--skill-discovery-directory`. Successful preflight creates one immutable
retained-root generation; each request keeps its SDK discovery binding
request-local and holds that root lease, the immutable same-client runtime-
generation operation capability, #154's opaque current-generation capability,
and its Retention lease through fresh response buffering. An accepted discovery
result authorizes bytes only through the Windows or Linux native no-follow
retained-handle/fd walk. Historical path/CWD, Repository locator, prompt,
workspace or time never creates a root, and the historical path is never opened
directly.
The generic Session raw route performs its Event-type policy check and
Retention admission/selection on the same connection and transaction before
any raw materialization.

The sole SDK event transport acquires the callback-owning runtime-generation
capability, binds one bounded complete body to one single-use
`SkillRuntimeCapabilityBridgeV1` token, crosses the exact loopback v2 HTTP
route, and transfers only that capability through #119's immutable typed
handoff. There is no direct writer or arbitrary-loopback admission, and no
database or publication lock is held across the loopback HTTP I/O. Runtime
invalidation and terminal sealing govern every commit and response publication:
post-transfer v2 noncommit responses, commits, and equality-replay success use
`TrySealV2NonCommitResponse`, `TrySealCommit`, and `TrySealReplaySuccess`,
respectively. A pre-runtime current-file safe result uses Retention
`TryCompleteWithoutRaw` alone; a post-runtime safe error uses that Retention
completion and then runtime `TrySealResponse`; current-file raw success uses
runtime `TrySealResponse` and then Retention `TrySealRawResponse`. Retention
`lost|busy` aborts without a response, and every capability and lease is
released on terminal completion, invalidation, or abort.

Issue #79 adds a separate historical-observation path:

```text
explicit local source selection + one-probe consent
        |
        +--> #77/#78 bounded adapter --> strict producer result/candidate batch
                                           |
                                           v
                                typed trusted admission seam
                                           |
                      preview -> confirmation -> atomic commit
                                           |
                                           v
                              historical_import schema v1
                               |-- observations/provenance
                               |-- sanitized conflicts/receipts
                               +-- optional exact link to an existing Session
```

The path never reparses an adapter output, scans for sources, or writes a
Session, Run, Event, trace, span, or source timestamp. The existing Session
workspace remains the live read model; historical observations are separate.

Issue #102 adds a source-independent Doctor path shared by direct, CLI, and
HTTP callers:

```text
#103/#104 source fact and candidate producers --+
direct typed-observation caller -----------------+--> shared Doctor domain
                                                   |-- pure deterministic evaluation
                                                   |-- doctor.v1 JSON / human projection
                                                   +-- verification service interface
                                                            |
                                                            v
                                                SQLite Doctor v1 store
                                                   ^                 ^
                                                   |                 |
                                           Config CLI adapter   Local Monitor HTTP
```

Source-specific producers do not add Doctor states or reorder results. Config
CLI and Local Monitor consume the same `DoctorResult`. The later proxy/UI is an
Issue #105 consumer and is not a second domain. Doctor verification is separate
from D051 process readiness and does not gate monitor startup or ingestion.

Collection profile は telemetry routing mode を表す public interface とする。
`raw-only` は最小構成、`docker-desktop-langfuse` は標準 full profile である。

Langfuse は標準 full profile の個別 trace viewer として使う。
改善支援 loop と dashboard は Langfuse UI に必須依存せず、saved raw OTLP JSON、SQLite raw store、normalized dataset を主入力にできる。

## 2. Primary Components

### Clients

- VS Code GitHub Copilot Chat: 必須 telemetry source。
- GitHub Copilot CLI: 必須 telemetry source。
- Codex App / app-server: 宣言済みの任意 candidate surface だが、Issue #92 の Desktop production integration 判定は `NO-GO`。OTel routing config の将来の source of truth は user-level `~/.codex/config.toml` とし project-local `.codex/config.toml` には依存しない。Issue #93 の adapter / Setup / Doctor / UI は、別途承認された discovery retry と prerequisite configuration specification が Desktop-owned replayable evidence と安全な log-export contract を確立するまで開始しない。

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

### Local Monitor v1 Application

- One loopback-only, single-user raw-default human application over the existing
  receiver, Session, Retention and frozen machine-contract owners.
- Primary composition is Repository selection → Session Explorer → Session
  detail or deterministic Repository Session Compare. Unified Settings owns
  receiver, AI, Repository, archive, storage/backup and diagnostics entry.
- `/api/local-monitor/v1/*` is the human-UI composition namespace. It reuses the
  [exact route/Session Explorer transport](specifications/interfaces/local-monitor-v1-route-transport.md),
  [single Repository catalog/assignment authority](specifications/interfaces/local-repository-catalog.md),
  #133/#134 Workspace reads,
  [#157/#158 Skill snapshot/raw detail](specifications/interfaces/skill-invocation-snapshot.md),
  [#160/#161 D082 Local Archive](specifications/interfaces/local-archive.md),
  #165/#166 Compare and #162/#163/#164 AI owners. #134 alone maps and serializes
  `GET /api/local-monitor/v1/repositories`; #156, #161 and #134 share one
  `ILocalRepositoryScopeSnapshotService`. #161 supplies direct Session and
  Repository archive facts, #156 validates those facts and alone composes
  assignment-dependent effective eligibility/reason, and #161/#134 do not add
  catalog SQL, a second connection, or a parallel reader.
  #161 maps exactly the raw-default `GET /api/local-monitor/v1/archive`,
  `POST /api/local-monitor/v1/archive-actions`, and
  `GET /api/local-monitor/v1/archived-items` routes. Their application and
  contributor are absent from sanitized-only composition, runtime-backup
  validation remains independent, and D082 adds no UI or frozen-v1 change.
  The stateless body-bearing `POST /api/local-monitor/v1/sessions` is the sole
  accepted Session collection request transport; q/model search values remain
  transient and it has no GET or saved-search fallback. #133 does not yet fix
    Its exact success wire is frozen by #171. Only pure #136 parsers may precede the
  canonical #134 Workspace-read response contract; #134 alone maps and
  serializes the endpoint after that gate closes.
  It does not widen `/api/monitor/*`,
  `/api/session-workspace/*` v1 or SSE.
- AI-independent observation/investigation/Compare remains complete without an
  LLM. Optional GitHub Copilot SDK execution crosses an explicit bounded
  provider-egress boundary and never receives SQLite or arbitrary SQL access.
- `--sanitized-only` composes receiver/health/frozen-machine-API only; no Razor
  Pages, human static assets or `/api/local-monitor/v1/*` are registered.

### Config CLI

Config CLI は repository-local な中核ツールである。

主な責務:

- VS Code / Copilot CLI 向け OTel 設定サンプル出力。既存 Codex App command は互換性維持の legacy sample generator であり、Issue #92 `NO-GO` 下で対応済み surface または repository-safe log profile を意味しない。
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
- Explicit raw-local replay preview/export/archive inspection. Config CLI does
  not own import or durable replay staging.

The setup framework lives in the Config CLI rather than Local Monitor HTTP/UI.
The Windows Release ZIP publishes the Config CLI beside the Local Monitor app
and the setup PowerShell wrapper invokes the same command/result contract used
in repository mode. This introduces no reverse reference from lower libraries
to Config CLI and no new Local Monitor route.

### Doctor Domain

- Dependency-light source-independent project that owns `DoctorFactSnapshot`,
  the twenty-state catalog, pure evaluator, `DoctorResult` (`doctor.v1`),
  serialization/human projection, verification lifecycle types, and clock/store
  interfaces.
- Does not read SQLite, environment, process state, endpoints, telemetry, or
  current time. All observations and timestamps are validated input.
- Direct evaluation consumes source-neutral typed `DoctorObservation` values;
  persisted `DoctorEvidenceCandidate` rows remain a separate store carrier.
- Direct, Config CLI, and Local Monitor HTTP callers project one result; no
  adapter may re-evaluate facts or create a parallel DTO.
- Issues #103/#104 produce only the twelve shared fact families and shared
  source-neutral evidence candidates. Issue #105 owns proxy/UI consumption.

### Instruction Finding Contract Domain

- `CopilotAgentObservability.InstructionFindings` is a package-free,
  source-neutral lower-level project that owns the unchanged
  `instruction-finding-handoff.v1` wire types, serializer/deserializer,
  fixed-template catalog, identity/reference hashing, candidate reconstruction,
  and semantic validation. The decoded immutable canonical-wire resource, not
  a runtime reserialization of the older semantic fixture, pins producer bytes.
- Local Monitor is the producer and uses friend-assembly access to the same
  internal authority. Cross-component consumers receive only the public
  canonical-byte validator, whose successful result is a positive analysis-run
  ID and not provenance or historical raw-reference evidence.
- The project reads no SQLite, raw store, source path, telemetry, environment,
  or network. Caller-side trusted acquisition and producer-side
  pre-tokenization evidence resolution remain separate responsibilities.

### Doctor SQLite Store

- Implemented by `CopilotAgentObservability.Persistence.Sqlite` in the existing
  Local Monitor database with its own
  `schema_version(component='doctor', version=1)`.
- Owns `doctor_verifications` and `doctor_verification_evidence`, explicit
  candidate observation, lifecycle reads, compare-and-swap complete/cancel,
  and transactional migration/rollback.
- Completion callers select opaque candidate references only; the store/service
  resolves existing unexpired candidates into trusted observations and never
  accepts caller-supplied candidate class, kind, or source.
- Does not change monitor/session component versions. Store initialization
  failure returns `doctor_store_busy` or `doctor_store_unavailable` and degrades
  Doctor verification only; stateless evaluation, Local Monitor
  startup/ingestion, and D051 readiness remain available.

### SQLite Raw Store

- local raw telemetry store。
- saved raw OTLP JSON を file-based ingest する。
- `raw-local-receiver` profile から受信した raw telemetry も保存できる。
- PostgreSQL は共有環境、長期保持、検索性能が必要になった場合の将来候補に留める。

### Raw Local Replay Domain

- `CopilotAgentObservability.RawReplay` owns the versioned raw bundle, canonical
  archive/manifest inspection, deterministic normalization/projection/dashboard
  replay, preview/consent binding, credential rejection guard, and no-leak
  result contracts.
- `Persistence.Sqlite` supplies an exact read-only source snapshot under one
  composite Retention operation lease. Upper Config CLI and Local Monitor
  adapters cannot accept caller-supplied source rows or bypass that lease.
- Local Monitor durable staging delegates to the existing Retention
  `sensitive_bundle` capture and cleanup implementation. The replay domain adds
  no store kind, migration, queue, worker, deletion adapter, or live-database
  merge path.

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
  90 日で expiry。Retention catalog v1 が item-level physical cleanup を所有し、
  pin / delete-now は Issue #90 に残す。

### Source Compatibility Reconciliation And Skill Projection

- `source_trace_version_observations` remains immutable. The sole
  `SourceCompatibilityReconciler` appends exact interpretation revisions and
  updates heads; it offers no HTTP/manual repair surface.
- These objects advance the Monitor source-compatibility owner from exact v10
  to v11; `skill_projection:1` remains independent. Its OTel generation stores
  only the trace revision it consumed, and its SDK claim subspace stores no
  trace revision.
- Effective trace aggregation remains fail-closed:
  conflicting/multiple token, then unrecognised, then all-resolved-one-token,
  then missing. A separate resolved observation does not hide missing.
- `skill_projection:1` independently owns OTel generations, ordered input
  frontiers, queue/lease state, current pointers and sanitized rows, plus the
  non-generation-bound exact SDK Session/Event claim subspace. It is not a
  second raw store or Retention owner.
- An OTel compatibility or eligible-frontier change invalidates current OTel
  claims and schedules one generation in the same SQLite transaction. OTel
  publication rechecks all revision/frontier/projector/queue/Retention fences.
- SDK current validity uses the complete exact
  source-application/adapter/normalization/payload-schema/fingerprint registry
  tuple. Cross-arm merge requires exact producer trace and span IDs. D083 fixes
  the SDK wire and complete r0001 registry; #158 still owns their atomic writer
  implementation.
- The pre-release Skill tables are destructively retired without old-row
  backfill or deletion of unrelated Session/raw/Retention data.
- Canonical details are
  [source compatibility reconciliation](specifications/layers/source-compatibility-reconciliation.md)
  and [Skill projection](specifications/layers/skill-projection.md). The
  accepted transport/snapshot/read/discovery composition and all remaining
  blockers are canonical in
  [Skill Invocation Snapshot](specifications/interfaces/skill-invocation-snapshot.md).
  `skill_invocation_snapshot:1` owns index/metadata and equality receipts only;
  Session Event content remains the sole historical body/path-byte owner. The
  complete namespace is absent from sanitized export/import and emits no empty
  carrier.

### Historical Import Subsystem

- Issue #79 owns source-neutral preview, confirmation, stale revalidation,
  idempotent commit, result/history, and historical-observation reads.
- `CopilotAgentObservability.ConfigCli` owns the #77/#78 bounded source adapter
  facade. Local Monitor and Config CLI adapt transport into the same workflow
  contracts; neither accepts caller-supplied adapter/candidate bytes.
- `CopilotAgentObservability.Persistence.Sqlite` owns a distinct
  `schema_version(component='historical_import', version=1)` component in the
  Local Monitor database. It is not part of `RawTelemetryStore.cs`, the current
  Session schema v14, or retention catalog v1.
- Positive production admission can come only from a source-specific typed seam
  after exact profile tuple and stable source snapshot validation. Current
  GitHub Copilot CLI and Claude Code profiles remain zero-candidate and
  non-actionable.
- Imported metadata is a distinct historical observation. An exact source-
  specific binding may store a navigation-only relationship to an existing
  Session, but never mutates or coalesces that Session. Otherwise the
  observation stays `distinct_unbound`.
- Metadata-only observations are sanitized local runtime metadata with
  `content_state=not_captured`; they create no retention item. A later content
  profile must use existing `session_event_content` and #89/#90 rather than a
  new store kind.

### Retention Mutation Application Service

- The Local Monitor contains the Issue #90 retention mutation application
  service and its versioned `/api/retention/v1/*` API surface.
- It resolves exact Session/item targets through the #89 catalog, owns preview,
  confirmation, idempotency, and append-only audit orchestration, and hands
  physical deletion to the existing #89 worker. It adds no parallel lifecycle,
  queue entity, or deletion path.

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

Issue #102 adds a second independent lower-level domain and keeps dependency
direction acyclic:

```text
CopilotAgentObservability.Doctor               fact/result/lifecycle contracts + pure evaluator
        ^
        |
CopilotAgentObservability.Persistence.Sqlite   Doctor v1 store implementation
        ^                         ^
        |                         |
CopilotAgentObservability.ConfigCli    CopilotAgentObservability.LocalMonitor
```

`Doctor` references neither `Persistence.Sqlite`, `Telemetry`, Config CLI, nor
Local Monitor. Persistence may reference the Doctor contracts; upper adapters
may reference both. The existing Telemetry dependency direction remains
unchanged.

Issue #59 adds another independent lower-level domain without a dependency
cycle:

```text
CopilotAgentObservability.InstructionFindings  v1 canonical/semantic authority
        ^                                ^
        |                                |
CopilotAgentObservability.LocalMonitor   downstream sanitized consumers
```

`InstructionFindings` references no Local Monitor assembly or persistence
component. The Local Monitor producer excludes the shared source files from
its own compilation and delegates to the lower-level assembly; downstream
consumers must not copy the hash/template/schema logic.

Wave 2 adds source-neutral alert and export domains while preserving the same
acyclic direction:

```text
CopilotAgentObservability.Alerts          CopilotAgentObservability.InstructionFindings
                    ^                          ^
                     \                        /
                      CopilotAgentObservability.SanitizedExport
                                      ^
                                      |
                    CopilotAgentObservability.Persistence.Sqlite
                                ^                 ^
                                |                 |
           CopilotAgentObservability.ConfigCli   CopilotAgentObservability.LocalMonitor
```

`Alerts` owns deterministic rule/receipt and lifecycle domain contracts.
`SanitizedExport` depends only on those lower-level alert and instruction
carrier authorities. Persistence implements their SQLite stores and trusted
snapshot adapter; Config CLI and Local Monitor provide public adapters without
moving domain validation into an upper layer. Historical evidence extraction
is an internal Local Monitor application/persistence boundary and adds no HTTP
or shared lower-level dependency. Local Monitor host construction also
initializes the #73 store/read composition beside #72, but requires an explicit
provider to construct a runner and never configures provider credentials or
starts raw execution by default.

The Wave 3 alert compatibility repair keeps the same dependency direction.
`Alerts` additionally owns the source-neutral evaluate-and-append application
contract and the bounded query contract. The preserved v1 baseline in
`Persistence.Sqlite` implements the query contract over v1 rows in the
`alert_engine` owner tables and invokes the
shared `Alerts` strict receipt authority before returning exact bytes with the
fully typed #80-owned Alert Center projection. #84 remains an
upper-layer projection/UI consumer and receives neither arbitrary SQL nor a
source-specific snapshot adapter from this repair.

Issue #94 adds an independent repository-safe pricing domain:

```text
explicit provider/model/billing mode + provenance-bearing usage
                              |
                              v
CopilotAgentObservability.Pricing
  registry validation + exact route/effective-date selection
  deterministic component estimation + canonical estimate record/consumer
```

`Pricing` references neither Telemetry, Persistence.Sqlite, Config CLI, Local
Monitor, Alerts, nor provider SDKs. It performs no runtime network access.
Upper-layer consumers may supply exact, capability-authorized values later,
but #94 adds no database component, migration, HTTP/CLI/UI surface, or alert
dependency. Bundled pricing data is an embedded reviewed repository artifact;
local overrides remain separately labeled inputs and cannot silently replace
bundled entries.
Registry v1 admits USD with two minor units only. The consumer validates
canonical bytes and identity, then recalculates against its required exact
catalog snapshot. `Pricing` canonically serializes the bundled document followed
by caller-ordered local overrides as `pricing.catalog-snapshot.v1`, binds its
SHA-256 into every estimate, and exposes the strict snapshot reload boundary.
It owns no storage and does not treat a public hash as authenticity. #95 must
persist those exact bytes; reconstruction and latest-catalog substitution are
not equivalent.

Issue #95 adds an upper-layer cost application without changing that domain
direction:

```text
Telemetry / Session exact identities and reviewed #61 provenance
                           |
                           v
LocalMonitor cost application -> Pricing
           |                    (pure #94 engine/strict consumers)
           v
Persistence.Sqlite pricing component v1
  exact catalog/estimate bytes + configuration/recalculation/head ledgers
           |
           v
bounded analytics + #80 alert snapshot/config v2
           |
           v
same #80 evaluator/store -> same #83 lifecycle -> #84 version-aware read/UI
```

`Persistence.Sqlite` references `Pricing` only to validate exact bytes through
the #94 consumers; it does not copy the pricing parser or estimator. The cost
catalog provider composes the embedded bundled document with at most eight
identity-bound trusted local override files supplied at process startup; it
persists only canonical snapshot bytes and never a locator. The cost
application obtains positive estimate input only from an injected trusted
source adapter. Local Monitor supplies the default unavailable adapter until a
reviewed #61 mapping authorizes all required facts. #80 adds v2 contracts to the
existing evaluator/store, #83 keeps lifecycle v1 while accepting an engine-v2
parent, #84 owns version-aware presentation, #85 keeps export v1 receipt-only,
and #88 owns the appended storage/restore compatibility. No second evaluator,
store, lifecycle, invoice, provider-fetch, or legacy-dashboard projection is
introduced.

Issue #87 adds a raw-only sibling without changing the sanitized export domain:

```text
CopilotAgentObservability.Telemetry
                  ^
                  |
CopilotAgentObservability.RawReplay
                  ^
                  |
CopilotAgentObservability.Persistence.Sqlite
             ^                 ^
             |                 |
CopilotAgentObservability.ConfigCli   CopilotAgentObservability.LocalMonitor
```

The Local Monitor upper adapter alone coordinates isolated replay publication
with the existing Retention sensitive-bundle store. `RawReplay` never references
Config CLI or Local Monitor and does not mutate the source SQLite database.

`SanitizedImport` depends on `SanitizedExport` for the frozen archive
inspection authority and on the #59/#80 public consumer authorities for graph
projection. Its public boundary owns one private snapshot of caller archive
bytes, so inspection, parsing, and hashing cannot observe different inputs.
`Persistence.Sqlite` owns the independent `sanitized_import` v1 component and
one transactional application boundary that includes schema ensure/validation
and import writes. Config CLI and Local Monitor call that boundary; they never
write Session, monitor, raw, finding, alert, or retention-owner tables on behalf
of import.

- `Telemetry` と `Persistence.Sqlite` は `ConfigCli` を参照しない（単方向依存）。
- 抽出した型は internal のままとし、`InternalsVisibleTo` で `ConfigCli` / `ConfigCli.Tests`（および将来の `LocalMonitor`）にのみ可視とする。public な共有 API は M1 では定義しない。
- Sprint8 の Local Ingestion Monitor（ASP.NET Core host、M2 以降）はこれらの共有 module を再利用する前提とする。ConfigCli の外部動作・CLI 表面は M1 で変更しない。

## 3. Data Flows

### Wave 2/3 Evidence And Alert Flows

```text
exact-bound Session metadata + sanitized monitor facts + #59 handoffs
  -> coherent bounded historical snapshot
  -> raw-local + repository-safe canonical datasets
  -> insert-or-identical local persistence
persisted #72 raw-local/repository-safe pair
  -> registered #73 composition (explicit provider + current raw host mode)
  -> canonical-byte-isolated provider view
  -> owner-snapshot validation + per-Session #59 grounding
  -> canonical #59 handoff
  -> historical_instruction_analysis component v1
  -> exact #75 read DTO

exact canonical #72 repository-safe dataset
  -> deterministic #74 efficiency-driver registry
  -> canonical repository-safe receipt + exact evidence refs
  -> #75 presentation handoff

immutable #80 alert receipt
  -> append-only #83 lifecycle event
  -> derived current state / bounded sanitized API

caller-provided normalized alert snapshot
  -> frozen #80 registry/configuration + exact evidence resolver
  -> deterministic evaluation
  -> initialize + append through IAlertEngineStore
  -> bounded typed success outcome only after append success

trusted alert_engine query (v1-only methods or version-aware v1/v2 dispatch)
  -> alert-id/evaluation-id/suppression-ordinal cursor pages (1..100)
  -> version-specific strict #80 owner validation + sealed projections
  -> #84 server-side projection without direct SQL

explicit Session + trace evaluation request
  -> exact Session ownership + complete monitor-span rows
  -> exact #61 source observation for each raw_record_id
  -> unknown-capability #80 evaluate-and-append
  -> typed #80 receipt/suppression query + #83 lifecycle
  -> bounded #84 Alert Center API/UI and exact recurrence

trusted read-only SQLite snapshot (#58 + optional #59 / #80)
  -> deterministic #85 dependency closure + canonical members
  -> fail-closed scanner + checksums
  -> atomic CLI file or loopback HTTP archive publication

exact Retention-authorized raw/optional Session-content snapshot
  -> explicit warning + preview-bound #87 raw archive export
  -> strict whole-archive inspection + pinned deterministic replay
  -> isolated existing sensitive_bundle namespace (7-day policy)
  -> existing Retention lease / queue / cleanup / recovery path

frozen #85 archive bytes
  -> one private byte snapshot + exact #85 preflight + transaction-local reinspection
  -> carrier-scoped exact-identity v1 import projection
  -> preview digest bound to exact local dedup/conflict state
  -> one `sanitized_import` transaction for schema/records/origins/graph/declarations/history
  -> bounded CLI or loopback UI/history projection
```

The historical, lifecycle, and Alert Center flows do not mutate their parent evidence.
The Alert Center has no background evaluator and no independent state store;
its generic monitor facts remain unknown capability until a named #61
source/version adapter is promoted.
Sanitized bundle inspection proves structure and checksums, not origin or store
provenance.

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
- trace-scoped exact `client.kind` / `service.name` source attribution と
  `experiment.id` による識別。source version、agent/model、Repository、path、
  time は source-family fallback に使用しない。raw と同時に evidence /
  durable reconciliation identity を保存し、後続 conflict は対象 trace と
  contributing ingestion の source 表示を再調整する。

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

### OTel Skill Projection Reconciliation Loop

```text
ordinary exact ingest or accepted interpretation revision
  -> one atomic compatibility/frontier generation participant
  -> immediate current OTel-claim invalidation
  -> persisted exact generation frontier + queue
  -> composite Retention operation lease
  -> fenced deterministic publish
  -> current-valid OTel Skill claim read
```

An unresolved trace produces no current OTel claim. An
expired/deleted/read-denied frontier becomes `input_unavailable`; it is not
shortened or converted to an empty successful projection. Independent SDK
claims join the same read authority through the arm predicate described above,
not through this loop.

### Local Monitor v1 Investigation Loop

```text
exact Repository catalog/assignment
  -> #161 direct Session/full-catalog Repository archive facts
  -> #156 exact-set validation and eligibility/reason composition
  -> one coherent Repository/archive scope snapshot
  -> Repository selection
  -> bounded Session Explorer read
  -> exact Session snapshot
  -> fixed Session summary + hierarchical timeline + contextual inspector
  -> exact raw technical detail under Retention lease

two explicit Session cohorts
  -> immutable bounded Compare snapshot
  -> deterministic #165 formulas and exact drill-down
  -> optional AI interpretation of the accepted receipt
```

`local_repository_catalog:1` is the exact Repository identity and
Session-assignment component. It owns immutable locator/head/history,
observation provenance, manual overrides and revisions, durable operation
receipts and catalog-owned raw-expiry metadata. The complete DC156-01–19
contract is `READY_FOR_IMPLEMENTATION` under the [Repository catalog
authority](specifications/interfaces/local-repository-catalog.md) and its
[DC156-12–19 executable closure](specifications/interfaces/local-repository-catalog-executable.md).
Repository identity is never derived from name, path, CWD, prompt, time or
cardinality. Issue #152 remains unresolved.

Repository/Session/Run/Trace/Span/Event/raw-record identities remain opaque and
exact. Missing facts remain missing. Archive changes default eligibility only;
it is not Retention, pin or deletion. Its direct facts, composition seam and
consumer independence are fixed by
[Local Archive v1](specifications/interfaces/local-archive.md) and the
[Repository catalog executable closure](specifications/interfaces/local-repository-catalog-executable.md).
Whole-Session AI reports alone have
durable immutable history; node/Repository/Compare results are transient or
bounded operational state under #162.

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

### First-Trace Doctor Loop

```text
explicit known/unknown fact snapshot + typed observations (direct)
  -> shared pure evaluator
  -> ordered doctor.v1 result
  -> direct / Config CLI JSON or human / Local Monitor HTTP

explicit verification start (expected source + bounded expiry)
  -> internal #103/#104 candidate observation
  -> explicit accepted candidate references + expected revision
  -> store/service resolution to trusted typed observations
  -> atomic evaluate + evidence acceptance + terminal CAS
  -> completed/cancelled/derived-expired verification projection
```

Candidate selection never uses latest trace, repository/workspace/cwd,
trace-ID-only, or timestamp proximity. Synthetic probes may show receiver,
persistence, or projection health but do not establish a real first trace.
D059 schema drift unrelated to the exact evidence remains advisory.

### Dashboard Artifact Generation

```text
measurements + optional raw/candidate outputs
  -> generate-dashboard-dataset
  -> generate-static-dashboard
  -> index.html + dashboard-data.json
```

Issue #94 is a separate calculation flow:

```text
exact session time + explicit provider/model/mode/pricing route
  + provenance-bearing token/request/credit quantities
  + bundled registry [and optional labeled overrides]
  -> validate registry and exact effective-date match
  -> estimate independent unrounded components
  -> immutable pricing.estimate.v1 record
```

Issue #95 consumes that record through an exact persisted-history flow:

```text
explicit local Session UUID + trusted source adapter + exact configuration head
  -> exact #94 request/catalog snapshot/estimate canonical bytes
  -> strict snapshot and estimate consumer reload
  -> append-only pricing estimate + recalculation result + contiguous head
  -> active-head-only Session/UTC-day/rolling-period projections
  -> currency-separated complete total + provisional partial-component subtotal
     + exact coverage
  -> #80 alert snapshot/config/evaluation/receipt v2
  -> unchanged #83 lifecycle v1 + #84 version-aware Alert Center
```

Membership, predecessor selection, and evidence resolution use exact Session,
estimate, catalog, configuration, and head identities. They never use latest
timestamp, current catalog, repository/workspace/path, or model proximity.
Current source manifests leave genuine positive provider estimates unavailable;
synthetic fixtures prove execution only.

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
Its retention metadata and deletion authority remain private to Retention catalog
v1; automatic physical cleanup uses exact catalog ownership rather than paths
carried in an output artifact.

Raw local replay bundles are caller-owned local files and never cleanup targets.
Their replayed artifacts are isolated `sensitive_bundle` runtime items with the
existing 7-day policy; they never enter the live raw/Session/projection database
or repository-safe artifact flow.

Issue #51 Session content is another local raw-bearing storage class. It is
secret-filtered, stored separately from Session metadata, and expires for reads
at `captured_at + 90 days`; expired reads report `expired_pending_deletion`.
Retention catalog v1 owns irreversible denial and later physical cleanup.

Doctor storage is sanitized local runtime metadata. It contains expected
source/adapter tokens, UUIDv7 lifecycle/candidate identifiers, canonical UTC
timestamps, fixed evidence class/kind, bounded opaque references, accepted
ordinals, state, and revision only. It contains no raw telemetry, prompt,
response, tool body, PII, credential, authorization value, absolute/local path,
repository/workspace heuristic, or exception text.

Historical-import workflow state is local runtime data. Public preview,
progress, result, history, and observation projections contain only fixed
metadata, availability-wrapped counts, opaque IDs/digests, and sanitized
conflict evidence. The one selected source locator may be held only as
ephemeral private database state in `historical_import_previews` until
commit/rejection/expiry so separate CLI processes or a Local Monitor restart
can re-probe the exact selection; it is never copied to durable operation
history or repository-safe output.
Candidate/source-record keys remain internal. The component stores no source
body and creates no raw retention item in workflow v1.

## Runtime backup and restore

Issue #88 adds a separate `runtime_backup` SQLite component v1 after the #79
and #86 component migrations. It owns sanitized path-free operation receipts
only and does not own the current Monitor 11, Session 14, Retention 1, or the
five raw store kinds.

Issue #95 appends `pricing` component v1 after `runtime_backup` without changing
the accepted #79 -> #86 -> #88 subsequence. It owns exact private catalog and
estimate bytes plus append-only configuration/recalculation/head metadata.
Alert-engine v1 upgrades in place to v2 while preserving v1 canonical bytes;
alert-lifecycle remains v1. The fixed tail is
`historical_instruction_analysis -> historical_import -> sanitized_import ->
runtime_backup -> pricing`. Whole-database backup/restore validates both
component versions and pricing-owned objects; no additional archive member,
Retention kind, or raw-store kind exists.

```text
live WAL database
  -> SQLite online backup API -> closed snapshot database
  -> integrity/version/count/retention projection
  -> canonical manifest + stored ZIP -> atomic caller-selected publish

untrusted ZIP
  -> strict byte-level archive inspection -> sibling staging database
  -> checksum/schema/migration/tombstone/Doctor validation
  -> default pre-restore backup -> atomic database replace
  -> installed validation -> commit receipt
                         \-> failure -> atomic old-database rollback
```

The snapshot, archive partial, and non-restore inspection database each have a
same-directory flushed `runtime-backup-transient-owner.v1` marker created first.
Bounded startup/next-operation recovery derives only the exact random basename
from that marker, removes SQLite sidecars/raw bytes, and deletes the marker last;
it never sweeps unmarked paths. The restore staging database remains exclusively
owned by `runtime-restore-journal.v2`, so transient cleanup does not create a
second restore authority.

SQLite is the restore unit. The manifest inventories bounded external runtime
preconditions rather than silently losing them. Active DB-external raw stores
or proposal-apply private files block backup; setup ownership remains
host-bound and must be re-established on another machine. Current terminal
tombstones/read denial are reconciled into staging and cannot be waived by
confirmation. PID/state/log files, credentials, executables, and operator
backup files are excluded by contract. The HTTP/UI surface can create and
inspect but never restores a live database; restore authority is offline CLI
only. Details are canonical in
[Runtime Backup And Restore Interface](specifications/interfaces/runtime-backup-restore.md).

The relevant accepted dependency sequence is `monitor`, `session:14`,
`local_repository_catalog:1`, `local_archive:1`, `retention:1` and
`skill_projection:1`, followed by `skill_invocation_snapshot:1` and Workspace
projection only when each is separately released.
The catalog component backs up all catalog-owned tables and durable receipts;
an older absent component initializes empty while partial/newer/unknown shapes
fail closed. Catalog metadata may restore without source raw and reports
unavailable provenance without reconstructing content. The component is
excluded from sanitized evidence export/import. Its complete DC156-01–19
implementation contract is `READY_FOR_IMPLEMENTATION` under the [Repository
catalog authority](specifications/interfaces/local-repository-catalog.md) and
its [DC156-12–19 executable closure](specifications/interfaces/local-repository-catalog-executable.md).

A declared `local_archive:1` requires exact Session 14 and declared catalog v1;
it has no Session-13 parent exception. When catalog and archive are wholly
absent, the accepted older/absent Session path reaches Session 14, installs an
empty catalog, and then installs an empty archive before Retention. Current and
restore validation cover the exact two-table/two-index/six-trigger namespace,
all current/event chains and heads, exact Session/Repository parents, manifest
version and both row counts. Whole-database backup/restore preserves those
bytes without another ZIP member, merge, overlay, orphan repair/remap or
synthesized event. The component is absent from sanitized evidence; its exact
contract is [Local Archive v1](specifications/interfaces/local-archive.md).

The Skill invocation snapshot component backs up index/metadata and equality
receipts while existing Session Event content backs up body/path bytes exactly
once; older component-absent state initializes empty and partial/newer state
fails closed. Its registration remains blocked with #158.

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

## Retention catalog v1

Issue #89 adds a persistence-neutral retention contract and a later independent
catalog component in the Local Monitor database. It owns raw-item lifecycle and
physical cleanup; Session aggregate fields remain a frozen read projection and
cannot authorize deletion of any other store. Catalog adapters are exact-owner
components for the five closed store kinds, while safe summaries, projections,
receipts, and tombstones remain outside the raw deletion registry.

## D087 owned Skill content split

The current owned-session adapter validates typed SDK invocation content only
as a required well-formed auxiliary inventory. It re-proves the retained native
Skill under the existing root/lease authority and supplies exact proof content
explicitly to normalizer v2; the mapper cannot implicitly promote SDK content
to the persisted definition. Prepared bytes remain the sole post-terminal
import input, so current-file comparison continues to compare stored native
definition bytes with a fresh native read.

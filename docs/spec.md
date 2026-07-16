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

Local Ingestion Monitor は、`raw-local-receiver` の telemetry をローカルで確認するための単一 ASP.NET Core プロセス（loopback-only）であり、Sprint8 で実装した。Sprint7 の Config CLI receiver（`127.0.0.1:4319`）と並存し、別 loopback port（既定 `127.0.0.1:4320`、Collector の `4317`/`4318` と Sprint7 CLI receiver の `4319` を回避）で動作する。OTLP HTTP/protobuf を受信して SQLite raw store に永続化し、sanitized monitor projection、ローカル UI、health endpoint を提供する。Sprint9 で受信済み OTel テレメトリから per-span の sanitized projection（`monitor_spans`）を追加し、agent-execution view としてツール / MCP 呼び出し名、成否、sub-agent のモデル / トークン、turn 単位トークンを表示する。Sprint10 で TraceDetail ページに4つの設計ビュー（Summary / Timeline / Flow Chart / Cache）をタブ UI として追加し、Flow Chart は当初 vendored Cytoscape.js + dagre（MIT、`wwwroot/vendor/`、CDN 不使用）でレンダリングした（Sprint12 で素の DOM へ置換。後述）。全ビューは既存の sanitized spans API（`GET /api/monitor/traces/{traceId}/spans`）のみを消費し、raw 境界・PII 境界の変更はない。ビジュアルテーマは VS Code Dark+ を基盤とし（D027）、タイポグラフィは vendored Noto Sans JP / Noto Sans Mono（D028、`wwwroot/vendor/fonts/`、OFL）。raw body と PII は既定で表示する（server-rendered、inert text）。`--sanitized-only` フラグで metadata-only モードを復元できる。Sprint12 で UI を刷新し、ダッシュボード（`/`）とトレース一覧（`/traces`）に各トレースの代表ユーザープロンプトを server-rendered 表示してトレースをプロンプトで識別できるようにした（raw-bearing 拡張。same-origin / `Cache-Control: no-store` / `--sanitized-only` 除去を強制し、`/api/monitor/*`・SSE の sanitized 不変条件は維持。D032）。Flow Chart は Cytoscape を廃し、Span Tree（インデント + ウォーターフォールバー）と DOM フローの toggle 切替として素の DOM で再実装した（D033）。`/ingestions` ページは廃止してダッシュボードへ統合し、UI は日本語化、ナビは3項目（ダッシュボード / トレース / 診断）に集約した。Windows では `scripts/local-monitor/` の PowerShell scripts により user-level Windows Task Scheduler の logon startup を任意登録できる。Sprint14 で Windows x64 self-contained folder publish の Release ZIP 配布面を追加し、利用者端末では source build や .NET SDK / Runtime 事前導入を要求しない。Sprint18 で UI をデザインハンドオフ準拠の Console 型 IA へ全面再設計した（D042）: 208px 左サイドバー + 2 項目ナビ（概要 / トレース）、診断はステータスバッジ → ポップオーバー経由、概要ダッシュボード（期間別 token KPI / モデル別内訳 / キャッシュ効率 / 高コスト TOP5 / 時間帯分布 / 最近のトレース）、トレース一覧の master-detail 化（テーブル + 右プレビュー）、トレース詳細のタブ廃止（フロー | waterfall セグメント切替 + 常設キャッシュ列）、スパンインスペクタ（整形 / raw タブ、新規 raw-bearing route。D043）、エラー解析モード、Copilot 解析ドロワー（履歴再送チャット。D045）。デザイントークンはハンドオフ §10 の hex 値を正とする。これを支えるため monitor projection schema v4 で trace 単位 cache token rollup と `trace_status` を additive に追加し（D044）、sanitized 集計 endpoint `GET /api/monitor/overview` と `GET /api/monitor/trace-list` を新設した。既存 public routes の shape / ordering は変更していない。詳細は [specifications/layers/telemetry-ingestion.md](specifications/layers/telemetry-ingestion.md) と [specifications/security-data-boundaries.md](specifications/security-data-boundaries.md) を正本とする。

GitHub Copilot app Canvas adapter は、Sprint11 PoC（`/api/monitor/*` と `/health/ready` の sanitized data から bounded action DTO を返す薄い adapter、loopback / per-launch token / raw 非送出境界、D029 / D030）として実装した任意統合である。Sprint15 で、これを Local Monitor の既存 API / view model / projection を再利用した診断 surface へ引き上げる（D036）。Sprint15 ではまず表示境界を変えない子 A（拡張所有ヘルパーページの UX 改善：trace を status / model / span 数 / tool 数 / token / duration / time / 短縮 trace id を含む判断可能な一覧にし、focus / ボタン / 見出しを日本語化、health / error 状態と次操作を具体化、health 生レスポンスを既定折りたたみ）を実装する。子 B（Canvas dashboard view）は Local Monitor 側に sanitized 集計 endpoint `GET /api/monitor/summary` を追加して Razor Index と共用し、子 C（Canvas trace detail view）は既存 bounded action のロジックを再利用した選択トレースの要約カードをヘルパーページに追加する（いずれも D037 で設計確定し本スプリントで実装）。子 D（Canvas raw preview）は既存の raw-bearing route `GET /traces/{rawRecordId}/raw`（HTML エンコード済み固定フォーマット）から server-to-server で取得しそのまま埋め込む新規ページ遷移ルート `GET /raw-preview/:traceId/:spanId` として実装する（D038）。子 B 残作業として Canvas ヘルパーページに新規ルート `GET /api/summary` 経由の「Local Monitor 概要」カードを追加する（D038）。D037 で見送った OTel 単独の session-to-trace correlation は、Issue #51 の明示的な Session event input と exact-link 規則を前提に限って supersede する。Issue #51 は SDK stream / compatible Hook / exact OTel enrichment を別 Session subsystem へ取り込み、Canvas `ctx.sessionId` を native session ID として Session workspace を構築する。repository / timestamp proximity では結合せず、OTLP receiver と既存 monitor projection は変更しない（D051）。Sprint16 では `.github/extensions/otel-monitor-canvas/` を唯一の copyable extension distribution unit とし、既存 OTLP Resource Attributes `vcs.repository.name` / `workspace.name` / `repo.snapshot` から sanitized `repository_name` / `workspace_label` / `repo_snapshot` を Local Monitor projection と Canvas helper / bounded action DTO へ追加する（D040）。Sprint17 では helper page の `session.send()` 分析トリガーに requested profile / model / reasoning / timeout hint controls と dispatch metadata を追加するが、Local Monitor raw analysis runner へ置き換えず、per-message execution control がない値を effective execution metadata として主張しない。Canvas action response は bounded DTO のままで、Issue #51 が明示する Session ingest / workspace interfaces 以外の UI 再実装・telemetry input / schema / API field・Copilot actions への raw / PII 送出は行わない。Issue #51 は Issue #45 の `session.send()` behavior と Issue #49 の Agent ownership を変更しない。Session UI 着手前に Issue #52 の current-screen capture と承認済み four-tab prototype を必須 gate とする。

Issue #54 では Canvas Improve を `canvas-improvement-proposals.md` の local runtime proposal lifecycle として実装する。詳細分析は既存の token-gated `POST /analyze` -> `session.send()` に限定し、モデル応答を取得・保存・再利用しない。Canvas が保存するのは人間が明示入力した sanitized proposal fields と既存 Session / Evidence への opaque reference だけである。Candidate は1つ以上の citeable evidence、Recommended は2つ以上の distinct exact-bound Session の evidence と明示 promotion を要する。Verified は Issue #56 comparison だけが設定できる。direct apply、diff、file path、snapshot、rollback、git 操作は Issue #55 の責務である。

Issue #55 は `canvas-proposal-apply.md` の trusted-local apply boundary として、Issue #54 proposal の明示承認後に限る file mutation を追加する。Canvas action と `session.send()` は apply payload を扱わず、per-launch token を持つ helper screen が Local Monitor の loopback API を proxy する。Local Monitor は startup `--apply-root <kind>=<absolute-directory>` でのみ root を受け取り、opaque root ID / relative path / existing regular file の allowlist、non-reparse ancestry、base SHA-256 / hunk-selection digest / immutable approval digest を再検証する。全 target が書込み直前に一致しない `stale` の場合は no-write で停止する。snapshot + write-ahead journal + recovery により transaction は all-applied or all-restored とし、rollback は current-hash precondition を持つ。source/diff text と absolute path は helper local display 以外へ出さず、git 操作・任意 path・directory/delete/rename・automatic apply は追加しない。

Issue #56 は `canvas-effect-comparison.md` の exact-linked comparison boundary として、proposal revision と active application receipt に対する user-confirmed pre/post cohort を比較する。quality input は Session human evaluation または exact Session/Run/trace に結合した immutable objective evaluation receipt に限定し、missing/partial evidence を補完しない。pre/post 各3件以上、severe regression 無し、quality pass rate 同等以上を Verified の前提とし、quality が同等の場合だけ duration / total-token median の10% material threshold を使う。verdict は fixed 4-state、summary と case-key drill-down は同じ evidence rows を使い、`improved` effect receipt と proposal `verified` transition は1つの SQLite transaction で記録する。rollback 後は receipt を historical/inactive とし、Canvas action / `session.send()` / log / repository-safe output に cohort、raw content、path/source/diff を追加しない。

GitHub Pages publish workflow は現行スコープから削除した（D049）。Static dashboard は `generate-static-dashboard` で local artifact として生成する。

Issue #66/#67 adds a user-scoped configuration setup path beside the existing
manual profile generators and startup scripts. The Config CLI owns a versioned
configuration ownership ledger and immutable private plans under the platform
local-application-data root: `%LOCALAPPDATA%` on Windows,
`$HOME/Library/Application Support` on macOS, and absolute `XDG_DATA_HOME` or
`$HOME/.local/share` on Linux, followed by
`CopilotAgentObservability/LocalMonitor/setup/`. Public commands
are `setup plan`, `setup apply`, `setup rollback`, and `setup status`; the
Release ZIP carries a self-contained Config CLI plus a PowerShell wrapper that
forwards the same `setup.v1` JSON result. Apply is explicit and change-set based,
validates every base hash before writing, uses backup + same-directory atomic
replace for files and current-user environment APIs for environment values, and
flushes a write-ahead intent before each file/environment-member mutation or
restore. Recovery classifies prior/desired/third-party state per step,
compensates in reverse step order, and persists partial outcomes without
overwriting third-party state. Rollback is hash-guarded and has no force mode.
Ledger v1 persists each plan-time repository-safe status-target projection as
an immutable snapshot, including the then-current canonical expected-result
facts. Ledger-origin expected results remain valid when they satisfy the strict
v1 shape, closed codes, safety, and target/surface invariants; unlike a newly
created plan, they need not equal the currently embedded manifest. Status
combines the snapshot with freshly verified lifecycle, current-target,
ownership, and backup facts. All-no-op targets own no backup and are outside the
ownership quorum, but their fresh base-state guard still participates in the
same change-set-wide rollback preflight used by rollback itself. The complete
ledger retains its 1 MiB cap; finitely bounded snapshot fields must permit the
largest legal single change set to fit without adding a second cap or pruning.
Private-plan `desired_state` remains schema v1 and is a closed union. The
existing committed real ownership-ledger fixture remains unchanged and
restart-readable as ledger evidence. Before changing `SetupPlanStore`, task-04b
captures a separate committed production-serializer private-plan v1 fixture
with legacy inline-string `desired_state` and proves byte-identical
write-close-reopen behavior. Inline is the canonical v1 arm for historical
bytes and generic non-tagged file/TOML/opaque targets; a tagged
`jsonc_owned_values_v1` object is valid only for `SetupTargetKind.Json` records
owned by the `github-copilot` adapter with the two VS Code Default Profile
labels `vscode-stable-default-user-settings` and
`vscode-insiders-default-user-settings`. Tagged owned string values are exactly
1..2048 UTF-16 units and the expected SHA-256 hash is lowercase. This is a required v1
union, not a migration, fallback, or schema-v2 path. It deliberately does not
persist the complete JSONC document. Bounded Plan-time rendering may hold the
complete document solely to calculate operations/hash and must discard it
before persistence; apply-time adapter revalidation materializes complete
desired bytes only under the existing lock. The coordinator verifies record
identity/cardinality/hash, keeps bytes transient, and persists hashes only.
Recovery never rematerializes: it classifies every crash window from the
expected/journal hashes and flushed backup. Malformed union/carrier data fails
`recovery_required` before an artifact or target write. No-op records produce
no materialization but retain their generic base-state guard.
Results distinguish requested/created and recovered change-set IDs. No HTTP,
proxy, Canvas, or Local Monitor UI DTO is added.

Setup endpoint input is an explicit-port loopback HTTP origin only. Input may
normalize a case-equivalent host/scheme and one root slash; plans, ledger-safe
DTOs, and configured values use the canonical no-trailing-slash origin. Userinfo,
non-root paths, query, fragment, HTTPS, remote hosts, and implicit ports fail
closed.

The initial `github-copilot` adapter supports VS Code Stable and Insiders 1.128+
Default Profiles, terminal Copilot CLI 1.0.4+, and caller-managed .NET App/SDK
telemetry guidance. Non-default VS Code profiles are detected only to emit
`vscode_non_default_profiles_not_modified` and are never edited. Copilot
managed-settings channels use native > server > file precedence; the highest
present channel wins wholesale without merging. VS Code enterprise
`CopilotOtel*` policies are a separate read-only system and neither participate
in that channel order nor suppress Copilot server/file evaluation. A differing
observed constraint in either system blocks the plan; an unobservable
signed-in-account policy is `managed_policy_unverified`. The resolved
per-setting precedence is policy, environment, user setting, then default. VS
Code writes only the documented Copilot OTel
user settings. Terminal CLI detection is environment-only and always reports
managed policy as unverified. Its bounded current-user OTel environment
allowlist is writable on Windows; macOS/Linux plans remain inspectable but
apply returns `unsupported_target` without writing a shell profile or any
target. Existing `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` is detect-only: matching
`http/protobuf` is retained with a warning, while another value returns
`environment_override_conflict` and no plan. Content capture remains unchanged
unless the user selects the explicit sensitive option. App/SDK is no-write.
Setup recognizes Local Monitor only by
a bounded no-redirect `GET /health/live` response, using one 500 ms budget and
4096 payload bytes plus a sentinel byte (or valid `Content-Length`) for the
oversize boundary. Every connect/read/total timeout is a foreign-owner result.
VS Code settings reads are bounded at 1 MiB plus one sentinel byte during both
plan and revalidation; malformed/oversize data is `malformed_settings`. Its
new plans are tagged-only, and a different version that remains above the
minimum is version drift (`recovery_required`) rather than silently accepted.
Current-process environment reads use a distinct read-only platform surface;
the existing user-environment surface remains the only Windows persistent
writer. Apply revalidates endpoint, policy, VS Code/CLI version, extension
presence, and planned member semantics before creating mutation artifacts, and
never treats a static success as first-trace receipt. Applying a persisted plan
whose adapter is no longer registered
returns `unsupported_adapter` with no mutation artifact or state transition.
The complete contract is
[configuration setup](specifications/interfaces/configuration-setup.md) and the
security decision is D058.

## First-trace Doctor core

Issue #102 adds one source-independent Doctor domain shared by direct callers,
Config CLI, and Local Monitor HTTP. `DoctorFactSnapshot` keeps twelve nullable
fact families with explicit unknown values. The pure evaluator emits the fixed
twenty-state catalog as blockers only in blocking precedence when any blocker
exists, or as one terminal state followed by fixed applicable advisories when
no blocker exists. Each v1 reason code equals its state code. A partial fact
snapshot has `success=false`, a non-null evaluation, a null primary state, no
states, and nonempty canonically ordered missing families. The canonical
machine projection is `DoctorResult` with
`schema_version = doctor.v1`; CLI human output and HTTP serialize/project that
same already-evaluated result and do not reinterpret facts.

Direct evaluation supplies source-neutral typed `DoctorObservation` values
whose fixed class/kind distinguish real-source evidence from synthetic probes.
Explicit first-trace verification uses a server-generated lowercase UUIDv7,
expected source/optional adapter, a 1..30 minute UTC window, revision-based
compare-and-swap, and bounded persisted evidence candidates. A completion
caller selects opaque references only; the store/service resolves existing
unexpired candidates into trusted observations, so the caller cannot supply or
override their class, kind, or source. Candidate selection never guesses from
latest trace, repository/workspace/cwd, trace ID alone, or timestamp proximity.
Synthetic probe evidence may prove receiver, persistence, or projection health
only and cannot satisfy real-source receipt or exact Session binding. D059
remains in force: unrelated schema drift is advisory and does not by itself fail
exact verification.

Config CLI exposes evaluate and verification start/status/complete/cancel;
Local Monitor exposes the corresponding five `/api/doctor` routes. Inputs are
strict bounded JSON (64 KiB), outputs are sanitized/no-store, and state-changing
HTTP requests keep loopback/Host/same-origin/CSRF protection. Doctor v1
persistence is a separate component in the existing SQLite database. Its
busy/unavailable failures return `doctor_store_busy` /
`doctor_store_unavailable` and degrade verification operations only; monitor
startup, ingestion, stateless evaluation, and the D051 readiness
body/status/threshold contract are unchanged. The complete state/fact/result,
limit, CLI/HTTP, storage, migration, and #103/#104/#105 handoff contract is
[first-trace Doctor](specifications/interfaces/first-trace-doctor.md) and the
architecture decision is D060.

## Source capability semantic contract v1

`docs/specifications/contracts/source-capabilities/v1/source-capability-manifest.schema.json`
is the JSON Schema 2020-12 structural source of truth. The sibling `manifests/`
documents are the per-surface declared-capability source of truth; their
`contract_version` must match the schema major version (`v1`). Canonical
Markdown is the semantic source of truth for evidence authority, provenance,
deterministic completeness, data safety, and the later adapter handoff.

The v1 schema rejects unknown fields. Therefore a v1-compatible change may add
only a newly observed manifest value within an already declared capability
shape, without changing its meaning or lowering safety. Adding a manifest or
schema field (including an optional one), removing or renaming a field,
changing a type or enum, changing authority/precedence, or changing the
meaning of a completeness status/reason is breaking and requires a new
contract major with matching schema and manifests. A manifest/schema version
match and unknown field rejection are mandatory; a consumer must not silently
accept a mismatched or extended document.

This is a documentation and committed-contract release only. It does not
change the Issue #51 Session / Run / Event identity, the Issue #49 Agent
ownership interpretation, or any receiver, adapter, database, migration, HTTP,
proxy, or UI DTO. The semantic rules and canonical locations are defined by
[telemetry ingestion](specifications/layers/telemetry-ingestion.md),
[raw-store normalization](specifications/layers/raw-store-normalization.md),
[Canvas Session workspace](specifications/interfaces/canvas-session-workspace.md),
and [security data boundaries](specifications/security-data-boundaries.md).

## Source schema drift and Claude Code P0

Issue #62 stores immutable source-schema observations per committed ingest
batch. Session/schema status is derived from those observations rather than
becoming a second authority. Compatibility is fingerprint based: verified
application versions are evidence labels, an unverified version with a known
fingerprint remains processable, and a new fingerprint is retained and
reported as `schema_drift_detected`. A source version is unsupported only when
it is known incompatible or lacks a required signal.

Issues #63-#65 add Claude Code through a source-specific adapter without
changing source identifiers. OTel owns trace/span identity, parentage, and
timing. Hook data owns native lifecycle and explicit event identity and cannot
create spans, duration, tokens, or hierarchy. Exact Session binding uses only
identical native session identity, explicit resume/handoff, or byte-equivalent
trace context. Claude ownership views use exact source parentage only; missing
parentage remains unresolved. Public monitor DTO changes are additive and
sanitized. The canonical contract is
[source schema drift and Claude Code](specifications/interfaces/source-schema-drift-claude-code.md).

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
| Configuration setup interface | [specifications/interfaces/configuration-setup.md](specifications/interfaces/configuration-setup.md) |
| First-trace Doctor interface | [specifications/interfaces/first-trace-doctor.md](specifications/interfaces/first-trace-doctor.md) |
| Normalized measurement dataset interface | [specifications/interfaces/measurement-dataset.md](specifications/interfaces/measurement-dataset.md) |
| Candidate record interfaces | [specifications/interfaces/candidate-records.md](specifications/interfaces/candidate-records.md) |
| Human-review record interfaces | [specifications/interfaces/human-review-records.md](specifications/interfaces/human-review-records.md) |
| Dashboard dataset interface | [specifications/interfaces/dashboard-dataset.md](specifications/interfaces/dashboard-dataset.md) |
| Instruction diagnosis analysis interface | [specifications/interfaces/instruction-diagnosis-analysis.md](specifications/interfaces/instruction-diagnosis-analysis.md) |
| Source schema drift and Claude Code interface | [specifications/interfaces/source-schema-drift-claude-code.md](specifications/interfaces/source-schema-drift-claude-code.md) |
| Canvas Session workspace interface | [specifications/interfaces/canvas-session-workspace.md](specifications/interfaces/canvas-session-workspace.md) |
| Canvas Session Evidence interface | [specifications/interfaces/canvas-session-evidence.md](specifications/interfaces/canvas-session-evidence.md) |
| Canvas Effect Comparison interface | [specifications/interfaces/canvas-effect-comparison.md](specifications/interfaces/canvas-effect-comparison.md) |
| Canvas Improvement Proposal interface | [specifications/interfaces/canvas-improvement-proposals.md](specifications/interfaces/canvas-improvement-proposals.md) |
| Canvas Proposal Apply interface | [specifications/interfaces/canvas-proposal-apply.md](specifications/interfaces/canvas-proposal-apply.md) |
| Contributor workflow | [contributor-guide.md](contributor-guide.md) |

## Public Interfaces

Publicly documented interfaces are:

- Config CLI command names, arguments, CSV / JSON output shape。
- Config CLI configuration setup commands and `setup.v1` JSON result:
  `setup plan --adapter github-copilot --target <vscode|cli|app-sdk|all>`,
  `setup apply --change-set <uuid-v7>`, `setup rollback --change-set <uuid-v7>`,
  and `setup status [--adapter github-copilot]`. The configuration ownership
  ledger is user-scoped runtime data; command output is repository-safe and
  redacted. No setup HTTP/proxy/UI surface exists.
- Config CLI Doctor commands and shared `doctor.v1` result: `doctor evaluate`,
  `doctor verification start`, `doctor verification status`,
  `doctor verification complete`, and `doctor verification cancel`. CLI input
  is bounded to 64 KiB, JSON/human output is projected from the same shared
  result, database paths are never emitted, and exit categories are fixed by
  [first-trace Doctor](specifications/interfaces/first-trace-doctor.md).
- Collection profile names and `CAO_COLLECTION_PROFILE` values。
- `OTEL_RESOURCE_ATTRIBUTES` keys and recommended values。
- Dashboard dataset JSON and CSV logical tables。
- Static dashboard artifact layout: `index.html` and `dashboard-data.json`。
- Data safety boundary for repository-stored files。
- Local Ingestion Monitor loopback endpoints: `POST /v1/traces`、`GET /api/monitor/ingestions`、`GET /api/monitor/traces`、`GET /api/monitor/traces/{traceId}/spans`、`GET /api/monitor/summary`、`GET /api/monitor/overview?period=today|7d|30d`（期間別 token KPI / モデル別集計 / 時間帯分布。D042/D044）、`GET /api/monitor/trace-list?q&model&status&period&sort&offset&limit`（offset paging のトレース一覧 + cache 集計 + `trace_status`。`q` は TraceId 部分一致のみで prompt 本文は検索・返却しない。D042/D044）、`GET /health/live`、`GET /health/ready`、および SSE notification stream。`/api/monitor/*` と SSE は raw / PII を返さない（sanitized のみ）。`/health/ready` は飽和継続時に `503`（瞬間的 backpressure は `degraded` の `2xx`）を返し、`status` / `checks` / `degraded_reasons` を持つ機械可読 body を伴う。既定しきい値は ingestion-stall `10s` / projection-lag `60s`（設定可能）。
- Local Monitor Doctor endpoints: `POST /api/doctor/evaluations`,
  `POST /api/doctor/verifications`,
  `GET /api/doctor/verifications/{verificationId}`,
  `POST /api/doctor/verifications/{verificationId}/complete`, and
  `POST /api/doctor/verifications/{verificationId}/cancel`. They return the
  shared sanitized `doctor.v1` projection, enforce the 64 KiB JSON bound and
  fixed `200/201/400/404/409/410/422/503/500` mapping, use
  `Cache-Control: no-store`, and require same-origin plus
  `x-monitor-csrf: local-monitor` for writes. They do not change
  `GET /health/ready`.
- Local Ingestion Monitor raw-bearing routes（既定表示）: trace-detail page（agent-execution view、bounded raw preview inline + full raw record link）、`GET /traces/{rawRecordId}/raw`（server-rendered HTML）、`GET /traces/{traceId}/prompt-label`（JSON、D039）、`GET /traces/{traceId}/spans/{spanId}/detail`（スパンインスペクタ用 JSON: tool 呼出引数 / 結果末尾、llm メッセージ構成 / プレビュー、raw span JSON。D043）、および ダッシュボード（`/`）と トレース一覧（`/traces`）。後者2つは各トレースの代表ユーザープロンプトを server-rendered または same-origin prompt-label route fetch で表示する（raw store の OTLP payload から抽出、truncated、escaped inert text。prompt ラベルのみ raw でその他列は sanitized metadata。D032 / D039 / D042）。raw-bearing route set の全 route で same-origin 強制（cross-site は `403`）、`Cache-Control: no-store`。`--sanitized-only` 起動時は raw-bearing route / raw section を除去（raw-detail route は `404`、dashboard / traces の prompt ラベルは省略し短縮 TraceId にフォールバック）、PII は除外。prompt ラベルは `/api/monitor/*` と SSE には含めない。full-payload JSON raw API は提供しない。Canvas helper は、拡張所有 loopback server の token-gated local screen として、既存 raw-bearing span detail route から選択 trace の prompt / response preview を server-to-server 取得して表示してよく（D050）、同じ token-gated helper screen の `/api/traces` と `/api/summary` highlight trace label でも prompt label を表示してよい（D039 / D050）。Canvas action responses、`session.send()` prompts、logs、repository-safe outputs、static artifacts には raw prompt / response / prompt label を含めない。
- Local Ingestion Monitor run interface: loopback port（既定 `http://127.0.0.1:4320`）、`--port` / `--url`、`--sanitized-only`（metadata-only モード。raw-bearing route を `404` にし PII を除外）、リクエスト本文サイズ上限 `--max-request-body-bytes`（既定 `31457280` bytes = 30 MiB、env `CAO_MONITOR_MAX_REQUEST_BODY_BYTES`）。`POST /v1/traces` は本文が上限を超えると `413` / `request_too_large` を返し raw を書かない。

  Update (D039 / D042 / D050): Local Monitor の client-side overview / trace-list と Canvas helper は、same-origin / token-gated local screen 上で `GET /traces/{traceId}/prompt-label` を `fetch` し、prompt label を `textContent` 相当の inert text として表示してよい。これは full raw payload の client-side fetch 許可ではなく、`/api/monitor/*` と SSE は prompt-free のまま。
- Local Ingestion Monitor Windows startup scripts: `scripts/local-monitor/start.ps1`、`stop.ps1`、`status.ps1`、`set-startup-task.ps1`、`install-startup-task.ps1`、`uninstall-startup-task.ps1`、`install-user-env.ps1`、`uninstall-user-env.ps1`。Task Scheduler task の既定名は `CopilotAgentObservability LocalMonitor`、trigger は current user logon、既定 URL は `http://127.0.0.1:4320`、既定 DB / logs / state は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下。Task 登録 script は client routing 設定を書き換えない。user env script は別の明示操作として current user の永続環境変数に raw-local-receiver / monitor 向け OTLP routing を設定・解除する。startup 登録、enable / disable、今すぐ起動、user env install / uninstall は利用者が明示実行した場合のみ行う。
- Local Ingestion Monitor Release ZIP interface: `.github/workflows/local-monitor-release.yml` と `scripts/local-monitor/package-release.ps1` は Windows x64 self-contained folder publish を `local-monitor-win-x64.zip` として生成する。ZIP layout は `app/`、`scripts/`、`README.md`、`manifest.json`、notices を含む。ZIP scripts は `install.ps1`、`start.ps1`、`stop.ps1`、`status.ps1`、`set-startup-task.ps1`、`install-startup-task.ps1`、`uninstall-startup-task.ps1`、`install-user-env.ps1`、`uninstall-user-env.ps1` を含む。install root 既定は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\`、runtime root 既定は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\`。uninstall は DB / logs を既定保持し、`-RemoveData` 明示時のみ runtime data を削除する。
- Local Ingestion Monitor Copilot raw analysis routes（raw-default only）: `POST /traces/{traceId}/analysis`（CSRF header required; creates a queued local analysis run and dispatches the .NET GitHub Copilot SDK analysis service without embedding raw in the request。Sprint18 で optional `question` / `history`（過去 Q&A turns）を受け付け、runner が prompt に履歴ブロックを追記する history-resend 追い質問に対応。server 側に会話 session 状態は持たない。D045）、`GET /traces/{traceId}/analysis/runs/{runId}`（local raw-derived result; same-origin + `Cache-Control: no-store`）、`GET /traces/{traceId}/analysis/runs/{runId}/safe-summary`（repository-safe allowlist summary）。These routes are not under `/api/monitor/*`; `/api/monitor/*` and SSE remain sanitized-only. `--sanitized-only` removes the analysis routes.
- Local Monitor Copilot raw analysis configuration: `CopilotAnalysis:Enabled`（未設定時は enabled; `false` で local analysis runner を明示無効化）、`CopilotAnalysis:Model`（既定 `gpt-5`）、`CopilotAnalysis:TimeoutSeconds`（raw analysis runner が 1 回の SDK send/wait に許容する実行タイムアウト秒。正の整数。未設定・不正値は既定 `60`。Canvas options の timeout hint とは独立）、`CopilotAnalysis:BaseDirectory`（Copilot SDK runtime state directory; unset uses a writable temp-local LocalMonitor directory）、and optional BYOK provider keys `CopilotAnalysis:Provider:Type`、`BaseUrl`、`WireApi`（`completions` or `responses`）、`ApiKey`。These may be supplied through user-secrets or equivalent local configuration. `ApiKey` must not be logged, persisted in analysis events, or emitted to repository-safe outputs.
- Local Ingestion Monitor Canvas analysis options endpoint: `GET /api/analysis/options` returns sanitized profile/model metadata for the Canvas helper. Initial profiles are `fast` (60s timeout hint, default reasoning `low`), `standard` (180s, `medium`), and `deep` (600s, `high`). Models come from `CopilotAnalysis:Models:*` configuration, with `CopilotAnalysis:DefaultModel` / `CopilotAnalysis:Model` fallback. Returned model fields are id, display name, provider display name, reasoning-effort support, and default marker. Provider secrets, API keys, base URLs, local paths, raw telemetry, and PII are never returned. Timeout values are Canvas requested wait/display hints, not Local Monitor raw runner execution timeouts.
- Local Ingestion Monitor client config: `config-cli profile-vscode-env --profile raw-local-receiver --target monitor`（または `--endpoint`）が monitor endpoint（既定 `http://127.0.0.1:4320`）向けの VS Code env を出力。`--target receiver` 既定は `4319` のまま。
- Canvas adapter は raw default の Local Monitor と併用できる。`--sanitized-only` は Canvas の必須起動条件ではなく、Local Monitor の任意 metadata-only opt-out である。Canvas actions は引き続き既存 sanitized `/api/monitor/*` と readiness を読む bounded DTO surface であり、action responses / logs / committed outputs に raw / PII を返さない。
- Canvas helper analysis trigger: extension-owned `POST /analyze` remains a token-gated `session.send({ prompt })` fire-and-forget trigger. Payload includes `traceId`、optional `spanId`、`focus`、`profile`、`requestedModel`、`requestedReasoningEffort`、`requestedTimeoutSeconds`; response includes dispatch metadata (`analysis_trigger_id`、requested values、`prompt_template_version`、`dispatched_at`、and message id when the SDK exposes one). It does not call `/traces/{traceId}/analysis`, does not wait for a model response, and does not store final analysis result metadata.
- Canvas cross-repo metadata fields: `repository_name`、`workspace_label`、`repo_snapshot` は既存 OTLP Resource Attributes `vcs.repository.name`、`workspace.name`、`repo.snapshot` から生成する sanitized nullable projection fields である。`repo.name` は repository label source として扱わない。これらは `/api/monitor/*`、Canvas helper routes、bounded Canvas action DTO でのみ使用し、既存 projected rows は自動 backfill しない。
- Canvas Session workspace: `POST /api/session-ingest/v1/events`（schema/header version 1、batch 1..100、1 MiB、commit 後のみ `204`、固定 `400/413/415/503/504`）、sanitized `GET /api/session-workspace/sessions` / session detail / `resolve` / `status`、same-origin/no-store の `GET /sessions/{id}/events/{eventId}/content`、および installed `hook-forward --endpoint <loopback-url> --timeout-ms 250 [--source claude-code [--source-version <metadata-token>] [--schema-fingerprint <64-lowercase-hex>]]`。`--source` 省略は既存 Copilot mode、exact `--source claude-code` は Claude mode とする。provenance 引数は Claude mode だけで有効であり、Claude は out-of-band の信頼できる version または承認済み fingerprint を少なくとも一方要求する。Claude invocation の selector/provenance 欠落または不正時は payload shape で source を推測せず fail-open/silent で転送しない。完全な identity、merge、completeness、retention、response shape は [Canvas Session workspace interface](specifications/interfaces/canvas-session-workspace.md) を正本とする。
- Canvas Session Evidence: 選択 Session の `runs[].trace_id` を run 順で exact-only 合成し、各 trace の Issue #49 `agent-graph` と `spans?limit=200` 全ページを token-gated helper proxy 経由で表示する。Agent ownership は graph を唯一の正本とし、Session events は常に Session/unowned。詳細は [Canvas Session Evidence interface](specifications/interfaces/canvas-session-evidence.md) を正本とする。
- Canvas Improvement Proposals: exact-bound terminal Session の evidence を使う local-runtime `candidate` / `recommended` / `verified` lifecycle。詳細分析は既存 `session.send()` dispatch のままで、Canvas はモデル応答を取得しない。Candidate は citeable evidence、Recommended は2つ以上の distinct exact-bound Session と explicit promotion、Verified は Issue #56 comparison のみが設定する。direct apply / diff / path / rollback は Issue #55 の責務である。詳細は [Canvas Improvement Proposal interface](specifications/interfaces/canvas-improvement-proposals.md) を正本とする。
- Canvas Proposal Apply: Issue #54 proposal を明示承認後に限って、startup `--apply-root user_config|skill|repository=<absolute-directory>` に登録済みの root 内 existing regular file へ適用する。Canvas action / `session.send()` は file authority を持たず、token-gated helper display だけが full diff を扱う。apply は relative path / non-reparse target / all-target current SHA-256 / immutable approval digest を検証し、stale は no-write。snapshot + journal recovery は all-applied or all-restored、rollback は post-apply hash precondition と一回限りを保証する。詳細は [Canvas Proposal Apply interface](specifications/interfaces/canvas-proposal-apply.md) を正本とする。
- Canvas Effect Comparison: exact Session/Run/trace objective receipt と human evaluation、proposal revision、active application receipt を入力に、user-confirmed pre/post cohort を quality-first で判定する。pre/post 各3件以上、missing/partial/rollback/stale は不足証拠、quality を efficiency より優先し、quality 同等時だけ duration / total-token median の10%境界を使う。`improved` receipt と Verified は atomic、rollback 後は historical/inactive。詳細は [Canvas Effect Comparison interface](specifications/interfaces/canvas-effect-comparison.md) を正本とする。
- Copilot SDK raw analysis is hosted by the Local Monitor process as a .NET GitHub Copilot SDK service. Its internal tool set is `get_raw_trace`、`get_raw_record`、`get_raw_span_context`、`get_trace_summary`、`get_trace_span_tree`、`get_cache_summary`、`get_instruction_evidence`（instruction-diagnosis 向けの deterministic instruction-evidence extractor 出力。bounded `conversation_context` を含む。D047 / D048）; raw-returning tools are process-internal and remain separate from repository-safe summary generation.

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

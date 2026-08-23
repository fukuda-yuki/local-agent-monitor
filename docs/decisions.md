# Decisions

ユーザーとの決定ログを共有する．ディスカッションログです．
詳細仕様は [docs/specifications/](specifications/) を参照する。

## D001: 公式 OpenTelemetry 出力を主入力にする

Status: Accepted

VS Code 内部ログ、workspaceStorage、chatSessions を主入力にしない。
GitHub Copilot Chat / GitHub Copilot CLI / Codex App が emit する OpenTelemetry signals を使う。

Rationale:

- client が公式に出す trace / metrics / events を扱う方が再現性と保守性が高い。
- VS Code Agent Debug / Chat Debug View は手動デバッグ機能として残し、本製品では再実装しない。

Update (D021):

- 「本製品では再実装しない」は入力ソース（VS Code 内部ログ / workspaceStorage /
  chatSessions）と UI 複製を禁止する。受信済み OTel テレメトリから導出する sanitized
  agent-execution view は許可する。D001 の入力ソース制限は維持し、VS Code の内部ログや
  ストレージを入力にしない点は不変。

## D002: Langfuse は local trace viewer として使う

Status: Accepted

ローカル Docker Desktop 上の Langfuse self-host を標準 full profile の trace viewer とする。
Clients は OTLP HTTP で `http://localhost:3000/api/public/otel` に直接送信できる。

Consequences:

- Langfuse credential は環境変数で扱い、repository に保存しない。
- Langfuse UI は個別 trace viewer として使うが、改善 loop の唯一の source of truth にはしない。

## D003: OTel Collector は任意の代替経路にする

Status: Accepted

Collector は直接送信を置き換えず、直接送信が不安定な場合や組織展開候補として使う。
Sprint6 以降は Collector routing を collection profile の required support target として扱う。

Consequences:

- 初期 Collector example は trace pipeline のみに限定する。
- TLS、SSO、shared operation、masking、sampling は別途判断する。

## D004: Required Resource Attributes を固定する

Status: Accepted

必須属性:

```text
user.id
user.email
team.id
department
client.kind
experiment.id
```

Recommended `client.kind` values:

```text
vscode-copilot-chat
copilot-cli
codex-app
```

## D005: Content capture は明示的な安全境界内で扱う

Status: Accepted With Safety Boundary

Agent workflow の調査には prompt、response、system prompt、tool schema、tool arguments、tool results が必要になる。
ただし repository に raw content、credential、secret、Base64 authorization header、sensitive bundle content、sensitive bundle local path を保存しない。

共有環境や実データを使う場合は access control、retention、masking / redaction、利用者周知を先に決める。

## D006: Raw data loop は Langfuse UI に依存させない

Status: Accepted

Saved raw OTLP JSON から SQLite raw store と normalized dataset を作る。
Langfuse UI は trace viewer の optional side path として扱う。

## D007: Raw store は SQLite を既定にする

Status: Accepted

Local-first の raw store は SQLite とし、file-based ingest を使う。

Rejected for current scope:

- PostgreSQL as primary raw telemetry store。

Note:

- `raw-local-receiver` profile は D017 / D018 により別 Sprint の required support target として扱う。

## D008: Candidate pipeline は deterministic records までに留める

Status: Accepted

Trace から diagnosis candidate、improvement candidate、auto-decision record を生成する。
Existing human-review record との adapter / mapping compatibility を維持する。

Rejected for current scope:

- repository patch / diff generation。
- file auto-modification。
- commit / push / pull request automation。
- automatic pass / fail judgment of improvement effect。

## D009: Dashboard の第一候補は static HTML にする

Status: Accepted; Pages publish superseded by D049

Static HTML dashboard を常設 dashboard 第一候補にする。
Grafana JSON dashboard は将来候補または fallback として残す。

Consequences:

- `generate-static-dashboard` は `index.html` と `dashboard-data.json` を生成する。
- No server-side API, runtime service, or network dependency.
- GitHub Pages publish workflow は D049 で削除した。Static dashboard は local artifact として生成する。

## D010: Dashboard は raw content を表示しない

Status: Accepted

Dashboard は aggregate metrics、status distribution、trend、percentile、reference id、classification attributes を扱う。

Do not display:

- raw prompt。
- raw response。
- system prompt。
- tool arguments。
- tool results。
- source code fragments。
- credentials or secrets。
- sensitive bundle content or local path。

Allowed with access control:

- `user.id`。
- `user.email`。
- `client.kind`。
- `experiment.id`。
- `agent.variant`。
- `prompt.version`。
- `skill.version`。

## D011: Static dashboard の publish layout を固定する

Status: Superseded by D049

- GitHub Pages publish workflow、`gh-pages` branch snapshot、Pages artifact layout は現行スコープから削除した。
- Static dashboard の現行 artifact layout は `index.html` と `dashboard-data.json` のみ。

## D012: Outcome linkage は future candidate に留める

Status: Accepted

GitHub / Notion / issue / PR 等の outcome linkage は将来候補として扱う。
External API ingestion、identity mapping、HR system correlation、org usage / ROI dashboard は現在の scope に含めない。

## D013: Codex App の OTel config は user-level を source of truth にする

Status: Accepted

Codex App / app-server の OTel routing config は user-level `~/.codex/config.toml` に置く。
Project-local `.codex/config.toml` を OTel routing の source of truth として扱わない。

## D014: Aspire AppHost は orchestration surface にしない

Status: Accepted

Aspire AppHost は historical background と build coverage として維持する。
現在は空であり、resource は登録しない。

Do not add by default:

- Langfuse。
- OTel Collector。
- Config CLI。
- ServiceDefaults。
- Web app。
- DB / Redis / Worker。

## D015: Validation command を固定する

Status: Accepted

Code、project file、CLI behavior、workflow を変更した場合は以下を実行する。

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

LocalMonitor browser smoke test が solution test suite に含まれるため、build と test の間に Playwright chromium bootstrap が必要。wrapper は未指定時に `PLAYWRIGHT_BROWSERS_PATH` を repository-local の ignored `artifacts\playwright-browsers` に設定し、browser cache lock を writable workspace 内に置く。Linux CI では同じ script に `-WithDeps` を付ける。

Collector example を変更した場合は dummy credential で Compose config を確認する。

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

## D016: Production / shared use は未決にする

Status: Open

共有環境や実データ利用の前に以下を決める。

- access control。
- retention。
- deletion process。
- masking / redaction。
- user notice or consent。
- identity handling。
- shared artifact access control。
- live operation。

## D063: Retention catalog is the raw cleanup authority

Issue #89 adopts a single versioned catalog per Local Monitor database. Raw
read availability and physical cleanup are item-level catalog decisions based
on stable IDs, exact source references, and explicit ownership only. Existing
Session workspace v1 remains a frozen compatibility projection; it does not
become a cross-store deletion authority. This separates irreversible read denial
from eventual physical removal and prevents heuristic/path-based cleanup.

## D017: Collection profile を public interface にする

Status: Accepted

Telemetry routing mode は collection profile として明示する。
Profile selector は `CAO_COLLECTION_PROFILE` とする。

Required profiles:

```text
raw-only
docker-desktop-langfuse
docker-desktop-collector-langfuse
wsl2-docker-langfuse
wsl2-docker-collector-langfuse
remote-managed-langfuse
remote-managed-collector
raw-local-receiver
```

Consequences:

- `raw-only` は最小必須 profile とする。
- `docker-desktop-langfuse` は標準 full profile とする。
- Profile 差分は collection / routing / live viewer availability の差分とし、raw store、measurement、candidate、dashboard schema を分岐させない。
- Remote managed profiles は本 repository では WARNING と placeholder configuration までを扱う。
- 利用者同意 workflow は本 repository の対象外とする。

## D018: raw-local-receiver は別 Sprint で実装する

Status: Accepted

Langfuse なし構成として、この repository が VS Code から直接 telemetry を受け取る仕組みを `raw-local-receiver` profile とする。

Rationale:

- これは単なる profile 切り替えではなく、repository-hosted OTLP receiver / local agent surface を追加する作業である。
- Raw prompt、response、tool arguments、tool results、local path、identity attributes、credential-like values を受け取り得るため、安全境界と validation を先に決める必要がある。
- Company-managed PC では packaged exe install が blocked される可能性があるため、初期 required path は repository-local execution を優先する。

Consequences:

- Sprint6 は collection profiles と既存 routing paths を扱う。
- Sprint7 は `raw-local-receiver` の receiver、host model、raw store integration、VS Code direct telemetry validation を扱う。
- Tray app、packaged exe installer、Windows Service は初期 required path ではない。
- IIS / IIS Express は practical な常駐候補として Sprint7 で評価する。

## D019: 共有テレメトリ／永続化コンポーネントを別 project に抽出する

Status: Accepted

Sprint8 (issue #25) の Local Ingestion Monitor を ConfigCli と独立に構築できるよう、Sprint8 M1 で共有コンポーネントを 2 つの class library に抽出する。

- `CopilotAgentObservability.Telemetry`: OTLP decode / attribute 変換 / raw ingest / raw record model / measurement normalization / sanitization。
- `CopilotAgentObservability.Persistence.Sqlite`: SQLite raw store access。
- 依存方向は `Telemetry <- Persistence.Sqlite <- {ConfigCli, (将来) LocalMonitor}` の単方向とする。

Consequences:

- 抽出した型は internal のままとし、`InternalsVisibleTo` で friend assembly にのみ可視とする。M1 では public な共有 API を定義しない（unsafe / 未確定な型を solution 全体の契約にしないため）。
- NU1903 high-severity 警告を解消する。`MessagePack` を 2.5.302（AppHost）、`SQLitePCLRaw.bundle_e_sqlite3` を 3.0.3（Persistence.Sqlite、`lib.e_sqlite3` 3.50.3 を同梱）に明示 pin する。0 警告を M1 の exit criterion とする。
- B1 / B2 / B3（receiver host の堅牢性）は HttpListener host では修正せず、ASP.NET Core host（M2/M3）で吸収する既存決定を維持する。
- `RawTelemetryStore` は挙動を変えずに移設する。T5（schema-once / single writer）と T6（projection query）は behavior change のため M3/M4 で扱う。
- monitor summary sanitization 用の `Monitoring/` 区分は monitor projection が存在する M4 で作る。
- ConfigCli の外部動作・CLI 表面・既存テストは M1 で変更しない（291 tests green を維持）。

Update (D020):

- M1 時点の「monitor は sanitized 集約のみで raw を surface しない」という前提は、
  D020 の opt-in raw view（`--enable-raw-view`、既定 off、loopback-only）で更新された。
  `Telemetry/Monitoring/` の sanitization は引き続き既定表示の境界として有効である。
- D020 の `--enable-raw-view` 前提はさらに D023 で superseded された。現在の
  Local Monitor は raw body / PII を既定表示し、`--sanitized-only` は任意の
  metadata-only opt-out として残る。

## D020: Local Ingestion Monitor を opt-in raw 付きで実装する

Status: Accepted

Sprint8 (issue #25) の Local Ingestion Monitor を、Sprint8 replan
([docs/sprints/sprint8-local-raw-receiver-monitor/requirements-and-replan.md](sprints/sprint8-local-raw-receiver-monitor/requirements-and-replan.md))
の決定（DR1–DR6 / DD1–DD6）に基づき実装する。`/codex:adversarial-review` を複数
ラウンド経て確定した。

Decisions:

- **DR1 並存**: LocalMonitor は別 ASP.NET Core プロセス（loopback-only、別 port、
  既定 `127.0.0.1:4320`。Collector の `4317`/`4318` と CLI receiver の `4319` を
  回避）として追加し、Sprint7 の `serve-raw-local-receiver`（`127.0.0.1:4319`）は
  削除・非推奨にせず並存させる。port が既に bind 済みの場合は固定エラーで終了する。
  VS Code を monitor に向ける正規設定面は `profile-vscode-env --profile
  raw-local-receiver --target monitor`（既定 `--target receiver`=`4319`。custom
  port は `--endpoint`。他 profile との併用は固定エラー）。
- **DR2 並行 DB アクセス**: LocalMonitor 稼働中も `normalize-raw` / dashboard 生成 /
  診断（prompt 自己改善 loop）が同一 DB を読める。WAL、`busy_timeout`、read
  transaction、projection worker の `SQLITE_BUSY` retry を要件とする。
- **DR3 / DR4 opt-in raw / PII 表示**: 既定では sanitized metadata のみ。明示的な
  `--enable-raw-view` 起動時に限り、ローカル利用者が自分の raw prompt / response /
  tool content と PII（`user.id` / `user.email`）を loopback-only で閲覧できる。
  raw は id 指定で `raw_records` から都度取得し、default projection / list / SSE /
  log には載せない。raw を返す経路は server-rendered route
  `GET /traces/{rawRecordId}/raw` のみ（JSON raw API は設けない）。
  `--enable-raw-view` 無効時は当該 route 不在＝`404`、有効時も cross-site request は
  `403`、`Cache-Control: no-store`。`/api/monitor/*` と SSE は raw / PII を返さない。
  表示は必ずエスケープ済み inert テキストで描画する（UI フレームワーク既定エンコード。
  `Html.Raw` 不可、HTML / 属性 / script / URL 文脈へ live 反映しない）ので stored markup は
  実行されない。その上に重ねる追加機構（CSP / nosniff / payload sanitizer / XSS payload
  テスト群）は設けない（ローカル単一利用者ツールのため。下記 Consequences の受容リスク参照）。
- **DR5 live gate**: 実 VS Code Copilot Chat の HTTP/protobuf 受信 evidence
  （日時、環境、profile 値、endpoint、trace id / raw record id）を Sprint8 完了の
  hard gate とする。
- **DR6 ローカル信頼境界（明示 threat model）**: 単一の信頼するローカル利用者を
  対象とし、本人が自分の prompt / response をローカル UI で見ることは脅威ではない。
  防御対象は remote / non-loopback（loopback bind + `Host` header 検証）、browser
  経由の off-machine 送出（CORS 無効、strict same-origin（`Origin` /
  `Sec-Fetch-Site`）、CSRF）、log / repository への raw / PII 流出。**受容リスク
  （accepted risk）**: 同一ローカル利用者の別プロセスによる loopback 経由 raw 読取は
  対象外（raw store / OTLP payload / 既存 sensitive bundle が既に同一利用者から
  読める）。さらに `--enable-raw-view` は **unattended / background / 常駐を含む任意の
  起動モード**で許可し、raw / PII が process 生存中ずっと loopback 上で到達可能になる
  露出窓を製品オーナーが受容する（foreground-only 制限は検討のうえ不採用）。
  bearer-token を console に出す方式は採らない。表示は必ずエスケープ済み inert テキスト
  （既定エンコード、`Html.Raw` 不可）で行い stored markup は実行されない。その上の
  defense-in-depth（CSP / payload sanitizer 等）は設けず、既定エスケープを超える残余は
  受容リスクとする（ローカル単一利用者ツール）。
- **DD1–DD6**: HTTP `2xx` は commit 後のみ（queue full `503` / commit timeout
  `504` / shutdown `503` / DB busy `503`）。`schema_version` + idempotent additive
  migration（失敗時 `ready=false`）。`/v1/traces` のみ受理し他 signal は raw を
  書かず固定エラー。SSE は notification-only、gap recovery は cursor API。
  **`/health/ready` は sustained な queue-full / commit failure / projection-lag 超過時
  に非 2xx（`503`）を返す**（body flag だけでなく HTTP status を変える。瞬間的
  backpressure は `degraded` の `2xx`）。既定しきい値は ingestion-stall `10s` /
  projection-lag `60s`（lag は最古の未処理 `raw_records` の経過秒）で、CLI flag
  （`--ingestion-stall-threshold-seconds` / `--projection-lag-threshold-seconds`）＋ env
  で override 可。readiness は `status`（`ready`/`degraded`/`not_ready`）/ `checks` /
  `degraded_reasons` を持つ機械可読 body を `200`/`503` 双方で返し、`ready`・`degraded`
  =`200`、`not_ready`=`503`。既定値と override の両方を tests で固定する（監視契約のため
  正本に固定。表示の過剰防御とは別）。

Consequences:

- raw / PII の opt-in 表示は loopback-only の runtime surface であり、
  `docs/requirements.md` §8（repository 保存禁止）と §9（static dashboard 非表示）は
  緩和しない。
- D019 の「monitor は raw を surface しない」前提は本決定で更新される（上記 D019 の
  Update を参照）。
- 受容リスク（任意起動モードでの raw 露出窓、および既定エスケープを超える表示側
  defense-in-depth を設けないこと）は本 decision と
  [security-data-boundaries.md](specifications/security-data-boundaries.md) に明示
  記録する。表示は必ずエスケープ済み inert テキストで行う一方、重い CSP / anti-XSS 機構は
  ローカル単一利用者ツールには設けない。

Update (D023):

- Sprint9 で raw 表示の既定を反転する。`--enable-raw-view`（既定 off）は廃止し、
  raw body と PII は **既定で表示する**（server-rendered、inert text）。
  `--sanitized-only` フラグを新設し、metadata-only モードを復元する
  （raw-bearing route は `404`、PII は除外）。DR6 の cross-machine 防御（loopback
  bind、Host header 検証、CORS 無効、same-origin + `Cache-Control: no-store`）は
  不変。`/api/monitor/*` と SSE は引き続き sanitized metadata のみを返す。

## D021: Agent Debug View 非目的を絞り込む

Status: Accepted

`docs/requirements.md` §4 の「VS Code Agent Debug / Chat Debug View 相当の UI」非目的を
絞り込む。monitor は受信済み OTel テレメトリから導出する **sanitized agent-execution view**
を提示してよい。

禁止の対象:

- VS Code 内部ログ、`workspaceStorage`、`chatSessions` を入力ソースとすること。
- VS Code の in-editor Debug UI を複製すること。

許可する対象:

- monitor が受信した OTLP telemetry から per-span の sanitized projection を生成し、
  ツール / MCP 呼び出し名、成否、sub-agent のモデル / トークン、turn 単位トークンを
  表示する agent-execution view。

D001 は維持する。入力は公式 OpenTelemetry signals のみであり、VS Code 内部の
ストレージやログは入力にしない。D001 に Update note を追加済み。

## D022: Span-level sanitized projection

Status: Accepted

monitor projection に per-span のテーブル `monitor_spans` と、`monitor_traces` への
token / turn / agent rollup 列を追加する。

Sanitized metadata（既定表示面に載る）:

| Field | OTel source |
| --- | --- |
| operation (`invoke_agent` / `chat` / `execute_tool` / `execute_hook`) | `gen_ai.operation.name`、span name |
| logical category (`llm_call` / `tool_call` / `agent_invocation` / `hook` / `error` / `unknown`) | derived |
| tool name | `gen_ai.tool.name` |
| tool type (`function` / `extension`=MCP) | `gen_ai.tool.type` |
| MCP tool name | `github.copilot.tool.parameters.mcp_tool_name` |
| MCP server (hashed) | `github.copilot.tool.parameters.mcp_server_name_hash` |
| sub-agent name | `gen_ai.agent.name` |
| request / response model | `gen_ai.request.model` / `gen_ai.response.model` |
| input / output / total / reasoning / cache tokens | `gen_ai.usage.*` |
| status (ok / error) | span status code |
| error class | `error.type`（class token のみ。exception message は含めない） |
| finish reasons | `gen_ai.response.finish_reasons` |
| duration | span start / end |
| trace_id / span_id / parent_span_id / conversation_id | span / `gen_ai.conversation.id` |

Raw（server-rendered route でのみ提供。既定で表示、`--sanitized-only` で除外）:

- tool call arguments / results（`gen_ai.tool.call.arguments` / `.result`）。
- sub-agent instructions / responses（message content）。
- system prompt text（message content）。
- PII（`user.id` / `user.email`）。

Per-field sanitization policy:

- free-form name fields（`tool_name`、`mcp_tool_name`、`agent_name`、span `name`）は
  既存の `MeasurementSanitizer` unsafe-value guard を通し、pinned max length で
  truncate する。guard に失敗した値は drop（行の他列は保持）。
- `error.type` は class token のみ（`timeout`、`ECONNREFUSED` 等）。exception message
  や free-form error 属性は投入しない。同じ guard + max length を適用する。
- `finish_reasons` は enum-like token（`stop`、`length` 等）。unknown 値は guard +
  max length を適用する。
- `mcp_server_hash` は client 提供の hash をそのまま保存。unhash しない。
- reference id（`trace_id`、`span_id`、`parent_span_id`、`conversation_id`）は
  opaque reference id として扱う。`requirements.md` §5（session id / run id は収集
  対象）および §8（reference id は repository-allowed）と整合する。

Token rollup rule（二重計上禁止）:

- per-turn tokens = `chat` span 自身の `gen_ai.usage.*`（1 turn = 1 `chat` / LLM span）。
- per-trace total = trace の root `invoke_agent` usage（存在時）。複数の root
  `invoke_agent` が usage を持つ場合は root usage の合計。なければ `chat` span の
  合計（fallback）。
- `invoke_agent` total を `chat` per-call tokens に加算しない。sub-agent
  （child `invoke_agent`）usage はその sub-agent に帰属し、parent の trace total には
  parent 自身の agent-level total 経由でのみ含める（child の `chat` span を再合算しない）。
- token rollup は range-safe accumulator で計算し、公開 projection の nullable
  `int` token 欄の範囲を超える導出 / 合計値は wrap せず `NULL` とする。

Consequences:

- `monitor_spans` と `monitor_traces` rollup 列の allowlist schema は
  [raw-store-normalization.md](specifications/layers/raw-store-normalization.md) に
  定義する。
- per-field sanitization policy の negative test（email / path / secret-like values を
  name fields に inject し guard out を検証）は M2 / M4 / M6 で必須とする。

## D023: Raw body を既定表示し `--sanitized-only` 安全弁を設ける（D020 更新）

**Status: partly superseded by D075.** Raw-default display and frozen machine
contracts remain. The metadata-only human UI described here is retired:
`--sanitized-only` is receiver-only and registers no Razor Pages, human static
assets or `/api/local-monitor/v1/*`.

Status: Accepted

Sprint8 の姿勢（raw は `--enable-raw-view` opt-in）を反転する。単一ローカル利用者ツール
として、raw body と PII を **既定で表示する**（server-rendered、inert text、inline
rendering）。

変更点:

- `--enable-raw-view` は廃止（既定が raw 表示のため不要）。
- `--sanitized-only` フラグを新設。有効にすると raw-bearing route は `404`、PII は除外。
  必要な利用者が metadata-only モードを復元できる任意の opt-out。
- trace-detail page（agent-execution view）は raw body を inline 表示するため、
  既存の `GET /traces/{rawRecordId}/raw` と並ぶ **raw-bearing route** になる。
  raw-bearing route set の全 route で same-origin（`Origin` / `Sec-Fetch-Site` ⇒
  cross-site `403`）と `Cache-Control: no-store` を強制する。

不変（D020 / DR6 の cross-machine 防御）:

- loopback-only bind、`Host` header 検証。
- CORS 無効。state-changing action に CSRF + same-origin。
- raw / PII を log、repository-safe outputs、static dashboard、CI artifact に
  書かない。
- `/api/monitor/*` と SSE は sanitized metadata のみ（raw / PII を返さない）。
- captured content は escaped inert text で描画（framework 既定エンコード。`Html.Raw`
  不可）。追加の CSP / sanitizer / XSS payload-matrix 機構は設けない（ローカル単一利用者
  ツール。AGENTS.md Local-First Risk Posture 参照）。

受容リスクの拡張:

- raw / PII は起動フラグなしで loopback 上に到達可能（process 生存中ずっと）。
  単一利用者ローカルマシンのトレードオフとして product owner が受容。`--sanitized-only` は
  opt-out 安全弁。

Update (Issue #35):

- Canvas adapter 利用時も `--sanitized-only` は必須ではない。通常の raw default
  Local Monitor と併用できる。
- `--sanitized-only` は引き続き利用者が必要に応じて選ぶ metadata-only opt-out であり、
  Canvas 専用の安全姿勢ではない。

## D024: 設計ビュー deferred non-goal を Sprint10 でナローイング

Status: Accepted

Sprint9 の README と `docs/requirements.md` §4 は、グラフィカル Flow Chart、
Cache Explorer、ビジュアルポリッシュを「後続の設計スプリント」に延期していた。
Sprint10 がそのスプリントであり、non-goal を以下の範囲に絞る：

- Local Monitor は sanitized なクライアントサイドプレゼンテーションとして
  Flow Chart、Cache Explorer、ポリッシュされたテーマ、タイムラインフィルター/ソート UI
  を提供 **してよい**。すべて既存の spans API 上の sanitized 表示層である。
- **D001 と D021 は維持**: 入力はモニターが受信する公式 OTel 信号のまま。
  VS Code 内部ログ / `workspaceStorage` / `chatSessions` は非入力。
  VS Code の in-editor Debug UI の複製はしない。
- **D020 と D023 は維持**: raw 境界と sanitized JSON/SSE 不変条件は変更なし。

## D025: Cytoscape.js + dagre を vendored 可視化依存として許可

Status: Accepted

A1 Flow Chart はインタラクティブグラフ（pan/zoom、ノード選択、自動レイアウト）に
グラフライブラリと DAG レイアウトアルゴリズムを必要とする。
Cytoscape.js と dagre 拡張（cytoscape-dagre + dagre）を許可する。

- 3ファイルすべて **UMD 単一ファイルとして `wwwroot/vendor/` に vendored**
  （CDN 不使用。loopback-only / オフライン動作を維持）。
- MIT ライセンス。
- **sanitized spans JSON のみを消費** — raw / PII は扱わない。
- その他のインタラクティブ UI（フィルター、ソート、タブ、Cache Explorer）は
  Vanilla JS で実装。CSS フレームワーク、ビルドステップは追加しない。

## D026: Cache Explorer は sanitized-metrics-only、trace-internal 限定

Status: Accepted

A2 Cache Explorer はキャッシュヒット率、キャッシュ生成トークン、duration、model、
timestamp、per-turn トークン内訳を表示する。単一 trace 内に限定。

- VS Code の「連続リクエストの prefix diff」機能は **raw prompt body** を比較する
  ため **明示的にスコープ外**（D023 境界を維持）。
- `conversation_id` による **cross-trace stitching は deferred**（API 変更が必要）。

## D027: VS Code Dark+ テーマを採用。DADS は Local Monitor に非適用

Status: Accepted

Local Monitor は開発者向けデバッグツールである。そのビジュアルデザインは
VS Code の慣習に従う：

- **カラーパレット**: VS Code Dark+ を基盤（`#1e1e1e` 系背景、青アクセント）。
  Grafana のレイアウト・情報密度・パネル構成をレイアウトインスピレーションとして取り入れる。
- **タイポグラフィ**: D028 の vendored Noto Sans JP / Noto Sans Mono。
- **DADS（Digital Agency Design System）は非適用**。DADS アクセシビリティベースライン
  （`[official-must]` ルール）も非適用。アクセシビリティは VS Code 慣習に従う。
- DADS スキル（`dads-foundations-core`、`dads-ui-review`、`project-dads-policy`）は
  事前に削除済み。
- Static Dashboard は既存デザインを独立して維持する。

## D028: Noto Sans JP / Noto Sans Mono を vendored タイポグラフィとして採用

Status: Accepted

Local Monitor のタイポグラフィに Noto Sans JP（full weight set）と
Noto Sans Mono を採用する。

- `wwwroot/vendor/fonts/` に vendored（CDN 不使用）。
- 合計サイズ約 5–10 MB。ローカル専用ツールのため許容。
- ライセンス: OFL。
- システムフォントスタックは使用しない（vendored フォントに固定）。

## D029: Sprint11 M5 UI トリガーは拡張所有ヘルパーページ + `session.send` + token 保護付き monitor proxy で実装する

Status: Accepted

Sprint11 M5 の「Analyze selected trace with Copilot」UI トリガーは、
Canvas SDK の `session.send()` を公式の UI→Copilot トリガー経路として使い、
`open()` が返す URL を拡張所有の loopback ヘルパーページに置き換える。

- **ヘルパーページ**: `open()` は常に拡張が立てる loopback（`127.0.0.1`）の
  ヘルパーサーバを起動し、per-launch token を生成して
  `http://127.0.0.1:<port>/?t=<token>` を返す。M2-M4 の「monitor 直表示」挙動は
  ヘルパーページ内の monitor ページへのリンクに置き換わる。
  `open()` は冪等（同一 instanceId の再接続時は前回サーバを close して再起動）。
- **trace 選択 UI**: ヘルパーページは trace ドロップダウンを描画するため、
  拡張の loopback サーバが monitor の sanitized `/api/monitor/traces?limit=50`
  をプロキシする（`compactTrace` 形状のみ）。プロキシ route は per-launch token
  で保護し、不正 token は `401` を返す。CORS は無効のまま。
- **トリガー**: ヘルパーページの「Analyze selected trace with Copilot」ボタン押下で
  `POST /analyze`（token は `x-canvas-token` ヘッダ）を受け、検証済みの
  trace id・optional span id・focus（`latency` / `tokens` / `cache` / `errors`）から
  Copilot 指示文字列を構築して `session.send({ prompt })` を呼ぶ。
  `session.send()` は非同期 fire-and-forget とし、結果は Copilot chat 側で確認
  する。ヘルパーページは `{ ok: true, dispatched: true }` を返す。
- **payload 制限**: トリガー指示は trace id・span id・focus・action 名
  （`get_trace_summary` / `get_trace_span_tree` / `get_cache_summary`、focus 別に選択）
  だけを含む。raw details は Local Monitor UI 境界内のデータとして扱い、Canvas
  action responses、logs、committed files、static artifacts へコピーしない。monitor
  payload は指示に埋め込まない。
- **境界維持**: D020 / D023 / D030 と Sprint9/Sprint10 の sanitized JSON/SSE 不変条件
  は変更なし。拡張所有サーバは `127.0.0.1` のみ、`onClose()` で close、
  診断は `session.log()`（`console.log` 不使用）、CDN / remote fetch / 依存追加なし。
  新たな telemetry input / schema / endpoint / raw route は追加しない。
- **Canvas runtime live validation**: `extensions_manage` / `open_canvas` /
  `invoke_canvas_action` / `list_canvas_capabilities` は一部の surface で未提供
  のため、M5 の Canvas 実機検証は human-gated とし、代替証拠として contract test・
  静的 check・境界レビューを記録する。M6 で実環境検証を試みる。

## D030: Canvas adapter は raw-default Local Monitor と併用できる

Status: Accepted

Sprint11 の Canvas adapter は Local Monitor の任意表示統合であり、Local Monitor の
起動姿勢を Canvas 専用に変えない。Canvas adapter は通常の raw-default Local Monitor
と併用でき、`--sanitized-only` は Canvas 利用時の必須条件ではなく、利用者が必要に応じて
選ぶ metadata-only opt-out として残す。

不変:

- Canvas actions は既存の sanitized `/api/monitor/*` と `/health/ready` のみを読む。
- Canvas action responses、logs、committed outputs、static artifacts には raw prompt /
  response body、tool arguments / results、PII、credential、token、local sensitive path、
  raw OTLP payload を返さない。
- Sprint11 Canvas adapter 自体は新たな telemetry input / schema / API field /
  raw endpoint を追加しない。D051 は別 Session subsystem に限る後続 exception。
- Sprint16 で追加する sanitized repository metadata（D040）は、この禁止の
  scoped exception として扱う。raw endpoint や新規 telemetry input は追加しない。
- Local Monitor の raw-bearing server-rendered route は引き続き D020 / D023 の
  loopback、same-origin、`Cache-Control: no-store`、inert text rendering 境界に従う。

## D031: Windows Task Scheduler を LocalMonitor の user-level startup surface にする

Status: Accepted

Windows の簡易常時起動方式として、Windows Task Scheduler の user-level task を採用する。
これは単一のローカル利用者が自分の端末上で LocalMonitor をログオン時に起動するための
運用面であり、shared service や組織向け collector ではない。

決定:

- Task は current user の logon trigger とし、highest privileges は既定で不要とする。
- Task action は `scripts/local-monitor/start.ps1` 経由で既存
  `CopilotAgentObservability.LocalMonitor` を起動する。
- 既定 URL は `http://127.0.0.1:4320`、既定 DB / logs / state は
  `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下とする。
- Task 登録 script は VS Code / Copilot / Codex の client routing 設定を書き換えない。
  monitor へ向ける正規導線は既存 `profile-vscode-env --profile raw-local-receiver
  --target monitor` のまま。
- Task Scheduler 経由でも loopback-only bind、Host header validation、CORS 無効、
  same-origin、`Cache-Control: no-store`、`/api/monitor/*` と SSE の sanitized metadata 境界、
  raw / PII 非ログ出力を維持する。
- raw-default と `--sanitized-only` の既存挙動は変更しない。`install-startup-task.ps1
  -SanitizedOnly` で metadata-only 常時起動を選べる。

非採用:

- Windows Service、IIS / IIS Express、tray app、installer / MSI / winget、Docker /
  Langfuse / Collector の常時起動管理は本決定の対象外。

Consequences:

- PowerShell scripts は `scripts/local-monitor/` に置き、install / uninstall / start /
  stop / status を提供する。
- CI では script existence / parse / stable defaults / dry-run task shape を検証し、
  actual Task Scheduler registration と logon trigger は Windows 実機 validation evidence
  として扱う。

## D032: ダッシュボード / トレース一覧をプロンプト識別の raw-bearing 面に拡張（D023 更新）

**Status: narrowed by D075.** The prompt-label/raw-route security contract
remains frozen technical compatibility. The dashboard and `/traces` list are no
longer current Local Monitor v1 primary IA; `/` becomes Repository selection and
the list retires when Session Explorer ships.

Status: Accepted

単一ローカル利用者がトレースを不透明な TraceId ではなく「自分が入力したプロンプト」で
識別できるよう、ダッシュボード（`/`）とトレース一覧（`/traces`）に代表プロンプトを
server-rendered で表示する。これにより両ページは trace-detail page と
`GET /traces/{rawRecordId}/raw` に続く **raw-bearing route** になる。

変更点:

- ダッシュボードとトレース一覧を raw-bearing route set に加える。プロンプトは raw store の
  OTLP payload から server-side で抽出し、escaped inert text で表示する（`Html.Raw` 不可）。
- 両ページに既存 raw-bearing route と同一の制御を強制する: same-origin（`Origin` /
  `Sec-Fetch-Site` ⇒ cross-site `403`）、`Cache-Control: no-store`、`--sanitized-only` で
  プロンプト表示と raw リンクを除去し短縮 TraceId にフォールバック。
- プロンプト抽出・表示は当初 server-rendered Razor ページに限定する。
  D039 / D042 で短い prompt label route の same-origin client fetch は許可したが、
  full raw payload は JS で取得しない。`/api/monitor/*` と SSE は従来どおり
  sanitized metadata のみで、プロンプトを含めない。
- 旧 `/ingestions` ページは廃止し、取り込み一覧はダッシュボードへ統合する（route 削除）。

不変（D020 / D023 / DR6 の cross-machine 防御を維持）:

- loopback-only bind、`Host` header 検証、CORS 無効、state-changing action の CSRF + same-origin。
- `/api/monitor/*` と SSE は sanitized metadata のみ。projection schema / API field は追加しない。
- Sprint16 の sanitized repository metadata（D040）は、この不変条件を
  raw / PII 非送出のまま保つ scoped exception として扱う。
- raw / PII を log、repository-safe outputs、static dashboard、CI artifact に書かない。
- captured content は escaped inert text で描画。追加の CSP / sanitizer / XSS payload-matrix
  機構は設けない（AGENTS.md Local-First Risk Posture / D020）。

受容リスクの拡張:

- raw（プロンプト）が到達可能な server-rendered 面が trace-detail から
  ダッシュボード / トレース一覧へ広がる。単一利用者ローカルマシンの自己デバッグ利便性の
  トレードオフとして product owner が受容。`--sanitized-only` が opt-out 安全弁。

## D033: Flow Chart を素の DOM 実装に置換し Cytoscape / dagre vendored 依存を撤回（D025 更新）

Status: Accepted

trace-detail の可視化を Cytoscape.js + dagre による canvas グラフから **素の DOM 実装**に
置き換える。詳細ビューは「スパンツリー（インデント + ウォーターフォールバー）」と
「DOM フローチャート（時系列ノード + コネクタ）」を toggle で切替える。

変更点:

- `wwwroot/vendor/cytoscape.min.js` / `dagre.min.js` / `cytoscape-dagre.js` と
  `_Layout.cshtml` の読み込みを削除する。
- Span Tree / Flow Chart は Vanilla JS が sanitized spans API のみから DOM を構築する
  （`textContent` 描画、`innerHTML` / `Html.Raw` 不使用、`/raw` 非アクセス）。

不変:

- D026（Cache Explorer は sanitized-metrics-only）、D027（VS Code 風ダークテーマ、DADS 非適用）、
  D028（vendored Noto フォント、CDN 不使用）は維持。

## D034: LocalMonitor は Windows x64 self-contained folder ZIP を初期配布単位にする

Status: Accepted

LocalMonitor を、repository を clone して `dotnet run` する開発者向けツールだけでなく、
利用者端末に展開して起動できるローカル常駐診断ツールとして配布する。初期配布単位は
GitHub Actions が生成する Windows x64 self-contained folder publish の Release ZIP とする。

決定:

- Release ZIP 名は `local-monitor-win-x64.zip`。
- publish は `win-x64` self-contained folder publish とし、初期対応では single-file exe 化を必須にしない。
- ZIP は `app/`、`scripts/`、`README.md`、`manifest.json`、notices を含む。
- 利用者端末では `dotnet run` / `dotnet build` / `dotnet restore`、.NET SDK、.NET Runtime、ASP.NET Core Runtime の事前導入を要求しない。
- install root 既定は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\`。
- runtime DB / logs / state は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下に残し、app install root と責務を分ける。
- install、今すぐ起動、Task Scheduler startup 登録、startup enable / disable、stop、status、uninstall は分離した操作とする。
- Task Scheduler 登録は引き続き利用者が明示選択した場合のみ、current user / least privilege / AtLogOn / multiple instances IgnoreNew とする。
- uninstall は DB / logs を既定保持し、明示指定時のみ runtime data を削除する。
- Release ZIP、workflow logs、artifact metadata に raw prompt / response、tool arguments / results、PII、credentials、raw OTLP payload、runtime DB / logs / state を含めない。

非採用:

- Windows Service、IIS、machine-wide collector、Intune / MSI / winget、tray app、Docker / Langfuse / Collector の同梱。
- 初期対応での GitHub Release 作成、tag push、release asset 添付の自動化。

Consequences:

- `.github/workflows/local-monitor-release.yml` は build、Playwright Chromium bootstrap、test、package、artifact upload までを行う。
- `scripts/local-monitor/start.ps1` は `DotnetRun` と `Published` の両 mode を扱う。
- ZIP 利用者向け手順は user guide と operations guide に記録する。
## D035: Local Monitor から Copilot SDK raw analysis を実行する

Status: Accepted

Local Monitor の raw-default posture では、選択 trace / raw record / span を
.NET 版 GitHub Copilot SDK に渡して raw analysis を実行できる。これは Copilot /
Agent の観測ログを Copilot に再投入して診断するためのローカル診断機能であり、
raw を Copilot SDK analysis に渡すこと自体は禁止しない。

決定:

- SDK hosting は Local Monitor process 内の .NET GitHub Copilot SDK analysis
  service とする。Node ベースの project-scoped raw-analysis extension は使わない。
- Local Monitor は analysis run を作成し、raw を start request に埋め込まず、
  process-internal C# tool set から raw trace / raw record / raw span context を SDK
  session に渡す。
- raw analysis routes は `/traces/{traceId}/analysis/...` 配下に置き、`/api/monitor/*`
  と SSE は引き続き sanitized-only とする。raw-returning tool routes は公開しない。
- `--sanitized-only` では raw analysis UI / start route / result route を無効化する。
- raw analysis result markdown は local runtime data として保持してよい。
- GitHub Issue / docs / dashboard 向け出力は、raw 本文を含まない repository-safe
  summary として別 route で生成する。

不変:

- 既存 Canvas adapter は置き換えない。Canvas action responses / logs /
  committed outputs への raw / PII 非送出境界を維持する。
- repository、Issue、PR、static dashboard、CI artifact、
  repository-safe docs へ raw prompt / response / full tool arguments /
  full tool results / source fragment / credential / PII / local sensitive path を
  出してはならない。

## D036: Canvas adapter を Local Monitor 再利用型診断 surface に位置づける

Status: Accepted

Sprint11 の Canvas adapter は「Local Monitor UI を再実装しない薄い adapter」
（D029 / D030）として実装したが、ヘルパー UI は trace を英語の最小1行
（`trace_id — status — spans:N`）でしか出さず、focus / ボタン文言が内部語のまま、
接続エラー時の次操作も曖昧で、利用者が「どこを見て何を選び次に何をするか」を
判断しづらい。本 Epic は Canvas adapter を、Local Monitor の既存 API /
view model / projection を再利用した診断 surface へ引き上げる。second monitor の
二重実装はしない。

スプリント枠:

- 本 Epic は「Sprint12 親 Issue」として起票されたが、リポジトリの Sprint12
  （Monitor UX Redesign、D032 / D033）は完了済みであり、Sprint13 完了・Sprint14
  実装中である。番号衝突を避けるため、本 Epic は **Sprint15**
  （Canvas Diagnostic Surface）として新設する。

決定:

- Canvas 診断 surface は Local Monitor の sanitized `/api/monitor/*` /
  `/health/ready` / projection / view model を再利用して構成する。Canvas extension
  内に Local Monitor UI を再実装しない（D030 を維持）。
- 子 A（Canvas ヘルパー UX 改善）を**表示境界非変更**で実装する。対象は
  (a) trace 一覧を status / model / span 数 / tool 数 / token / duration / time /
  短縮 trace id を含む「判断できる一覧」にすること、(b) focus / ボタン / 見出し /
  posture note の日本語化（focus の enum 値 `latency` / `tokens` / `cache` /
  `errors` と action 名は不変）、(c) health / error 状態を
  `ready` / `not_ready` / `unreachable` に区別し、確認 URL・起動コマンド・
  設定確認・参照 monitor base URL など次操作を具体化すること、(d) health 生
  レスポンスの既定折りたたみ。
- 子 B（Canvas dashboard view）を将来実装する際は、Local Monitor 側に sanitized な
  集計 endpoint（例 `/api/monitor/summary`）を追加し、`MonitorTraceRollup` と
  既存 projection store を再利用して Razor Index と Canvas で共用する。公開
  interface 変更のため spec を先行更新する。本スプリントでは実装しない。
- 子 C（Canvas trace detail view）は既存 action（`get_trace_summary` /
  `get_trace_span_tree` / `get_cache_summary`）の bounded projection を Canvas 上に
  描画する。raw preview は含めない。本スプリントでは実装しない。
- 子 D（Canvas raw preview boundary）と子 E（session-to-trace correlation）は
  設計先行の独立した子 Issue とし、本スプリントでは実装しない。子 D は表示境界の
  設計判断を伴うため、子 A の UX / bounded detail を整えてから判断する。
- 子 A 着手前に、`docs/task.md` 技術負債 F8（Canvas 契約テストが文字列部分一致
  中心で構文エラーや helper-server 回帰を検出できない）へ対応する。`extension.mjs`
  から副作用のない純関数を `canvas-helpers.mjs` に抽出し、`node --check` と
  `node --test` による実行可能 smoke coverage を追加する。

不変:

- Canvas action response は bounded DTO のまま維持し、raw prompt / response body、
  tool arguments / results、PII、credential、token、local sensitive path、raw OTLP
  payload を返さない（D030 / security-data-boundaries を維持）。
- Canvas extension の loopback bind、per-launch token、`session.send` トリガー、
  log / committed output / static artifact への raw / PII 非送出を維持する。
- Canvas surface での prompt / response preview の可否は子 D の独立した境界設計
  判断に委ね、子 A では有効化しない。
- `--sanitized-only` を Canvas 利用の前提に戻さない（D030 を維持）。

## D037: Sprint15 子 B〜E の設計を確定する（D036 更新）

Status: Accepted

D036 で「設計のみ記録・実装は次スプリント」とした子 B〜E について、実装着手前に
不明点を解消する深堀り調査（Local Monitor 既存実装の調査、GitHub Copilot SDK
`rpc.ts` 生成型の調査、OTel 取り込み側の既存識別子調査）を行い、利用者確認を経て
以下のとおり確定する。

### 子 B（dashboard view）: 設計確定、本スプリントで実装着手

新規 sanitized endpoint `GET /api/monitor/summary?limit=N`（loopback-only、
`/api/monitor/*` の既存 allowlist 規約に従う）を追加し、Razor `Index` ページの
inline ハイライト計算と共用する新規共有サービスから返す。

- `limit`: 既定 50、範囲 1–200（既存 `/api/monitor/traces` の規約に合わせる）。
  cursor pagination は設けない（スナップショット集計であり、drill-down は既存の
  `/api/monitor/traces` を使う）。
- 集計は `IMonitorProjectionStore.ListMonitorTraces(0, limit)` で取得した
  window 内を C# 側でメモリ集計する（新規 SQL GROUP BY は追加しない。limit が
  小さく bounded であるため）。
- レスポンス形（確定）:
  ```json
  {
    "scope": { "limit": 50, "trace_count": 37 },
    "latest_trace": { ...既存 /api/monitor/traces の compactTrace 相当フィールド... } | null,
    "top_token_trace": { ... } | null,
    "error_trace": { ... } | null,
    "per_model_summary": [ { "model": "gpt-5", "trace_count": 12, "total_tokens": 84000, "error_count": 1 } ],
    "per_client_kind_summary": [ { "client_kind": "vscode-copilot-chat", "trace_count": 30, "total_tokens": 210000, "error_count": 2 } ]
  }
  ```
  `model` / `client_kind` が null の trace は `"unknown"` バケットに集計し、
  `per_model_summary` / `per_client_kind_summary` の `trace_count` 合計が
  `scope.trace_count` と一致するようにする。
- `readiness` はこのレスポンスに含めない（既存 `/health/ready` を正本のまま唯一の
  情報源とし、二重の情報源を作らない。子 A の Canvas ヘルパーも既に
  `/health/ready` を直接参照している）。
- 共有サービス（新規、例 `MonitorSummaryService`）を Local Monitor プロジェクト内に
  追加し、`Index.cshtml.cs` の既存 inline ハイライト計算（`TopTokenTrace` /
  `ErrorTrace` / `LatestTrace`）をこのサービス呼び出しに置き換える。Razor 側の
  見た目（既存カード）は変更しない。新しい per-model / per-client-kind サマリは
  まず API レスポンスとしてのみ提供し、Index ページへの新規パネル追加は本決定の
  スコープ外（必要なら別途 Issue 化する）。
- フィールドは `security-data-boundaries.md` の既存 allowlist（sanitized
  projection 列のみ、raw / PII 不可）の範囲内に限定する。

### 子 C（trace detail view）: 設計確定、本スプリントで実装着手

子 C を「Local Monitor の TraceDetail ページ全体（タブ4種）を Canvas に再実装する」
案ではなく、**最小の要約カード**として確定する（D030 の reuse-not-reimplement 原則
を維持）。

- Canvas 拡張所有 loopback ヘルパーサーバーに新規ルート
  `GET /api/trace-detail/:traceId`（既存 `/api/traces` と同じ
  `x-canvas-token` 認証パターン）を追加する。内部で既存の bounded action
  (`get_trace_summary` 相当の trace 行取得 + `get_cache_summary` 相当の
  span 集計ロジック)を呼び出し、`compactTrace` フィールド一式 + `cache_hit_rate`
  + `primary_model` のみを返す。span tree やターン別キャッシュ明細は返さない
  （それらは既存の "Copilotでこのトレースを分析" trigger 経由で Copilot 側に
  委ねる、現行方針を維持）。
- ヘルパーページに「選択したトレースの要約」カードを追加する
  （`renderHelperHtml` 拡張）。trace dropdown の選択変更時に
  `/api/trace-detail/:traceId` を fetch し、状態・主要モデル・トークン合計・
  所要時間・cache hit rate を表示し、`${monitorUrl}/traces/{traceId}` への
  「Local Monitorで詳細を見る」リンクを添える。
- 表示境界は子 A と同一（bounded DTO、raw 非送出、loopback、token 認証）。

### 子 D（Canvas raw preview boundary）: 設計確定、実装は次段階

利用者確認の結果、Local Monitor の既存 raw-bearing route 群（D020 DR3/DR4、D023、
D032）と同じ制御パターンを踏襲する設計を正式な方針として確定するが、**本スプリント
では実装に着手しない**。実装は別マイルストーン（利用者の明示的な go-ahead を要する）
とする。

確定した設計方針（将来の実装が従うべき制約）:

- raw preview は Canvas 拡張所有の loopback ヘルパーページ上で
  **server-rendered のみ**で提供する。Canvas の embedded HTTP server が
  Local Monitor の既存 raw-bearing route（例 `GET /traces/{rawRecordId}/raw`）
  から server-to-server で raw を取得し、ヘルパーページの HTML 内に
  `escapeHtml`（`canvas-helpers.mjs` の既存実装）で escape した inert text として
  埋め込む。クライアント側 JS（ヘルパーページの `<script>`）は full raw payload
  を JSON として一切受け取らない（D020/D023/D032 の raw payload fetch 禁止を
  踏襲。D039 の短い prompt-label route は別枠）。
- 同一の制御を強制する: same-origin（Canvas ヘルパーサーバー自身への
  same-origin。loopback token 認証は既存どおり維持）、`Cache-Control: no-store`、
  利用者の明示操作（trace 選択 + 明示的な「raw を表示」操作）を要求し、既定では
  raw を出さない。
- Canvas **action** response（`get_trace_summary` 等、Copilot agent が
  `invoke_canvas_action` で呼ぶもの）は本決定後も bounded DTO のまま変更しない。
  raw preview はヘルパーページの server-rendered HTML に限定し、Canvas action /
  ログ / Copilot へのプロンプト送出経路には一切流れない。
- `sanitizeDto()` の forbidden-key フィルタ（`raw|payload|prompt|content|
  argument|result|user|email|credential|secret` 正規表現）は action DTO に
  引き続き適用する。raw preview ルートはこのフィルタの対象外の別経路（直接
  server-rendered embed）として実装し、フィルタを緩めることでの誤った raw 露出を
  避ける。

この設計は「実装してよい」という承認ではなく、「実装するとすればこの形」という
確定済みテンプレートである。実装着手には別途利用者の明示的な go-ahead を要する。

### 子 E（session-to-trace correlation）: D037 では見送り（D051 が限定更新）

OTel 取り込み側を全面調査した結果、GitHub Copilot app session を Local Monitor の
trace と安定的に対応付けられる既存識別子は **存在しない**ことを確認した
（`client_kind` は client 種別のみで instance を識別しない、`conversation_id` は
span 単位で trace レベルの安定識別子ではない、`trace_id` はリクエスト単位で
session グルーピングを持たない）。GitHub Copilot SDK 側の
`CanvasProviderOpenRequest` / `CanvasProviderInvokeActionRequest` /
`CanvasProviderCloseRequest` には `sessionId: string`（"Target session
identifier"）フィールドが存在することを `github/copilot-sdk` の生成型
(`nodejs/src/generated/rpc.ts`) で確認したが、これは Copilot SDK 側の内部
session id であり、OTel 取り込み側のどの属性とも対応しない。

利用者確認の結果、D037 の範囲では自動相関のための新規 telemetry resource/span attribute 追加
（telemetry schema 変更、spec 先行更新、Copilot CLI/app 側が実際にそのような
属性を OTel として送出するかも未確認）は行わず、**子 E は見送る**。Canvas の
trace 選択は D037 の実装範囲では子 A の手動 dropdown 選択を維持する。
ヒューリスティック推定候補の提示も本決定では追加しない（過剰実装を避ける）。

D051 は、後から承認された明示 Session event input と exact-link 規則を使う別
Session subsystem に限って、この「新 input/schema を追加しない」「手動選択を
恒久化する」という絶対表現を更新する。repository / timestamp proximity を使う
heuristic correlation は D051 後も禁止する。

不変（D036 を維持）:

- Canvas action response は bounded DTO のまま。raw prompt / response body、
  tool arguments / results、PII、credential、token、local sensitive path、raw OTLP
  payload を Canvas action / ログ / 静的成果物へ返さない。
- 子 B の新規 endpoint は sanitized projection の allowlist 範囲内に限定する。
- 子 C の新規ルートは bounded DTO のみを返し、span tree / cache 明細など重い
  projection は返さない。
- 子 D は設計確定のみであり、実装（コード変更）は本決定の対象外。
- `--sanitized-only` を Canvas 利用の前提に戻さない。

## D038: ライブ検証のみ GitHub Copilot へ委譲する前提で子 D 実装を許可し、子 B 残作業を確定する（D037 更新）

Status: Accepted

利用者確認の結果、今後 Sprint15 の作業分担を次のとおり再整理する:

- **実装（コード作成・単体/契約テスト・`node --check`/`node --test`/`dotnet
  build`/`dotnet test` による検証）はすべて Claude（このリポジトリで作業する
  エージェント）が行う。** GitHub Copilot Canvas runtime ツール
  （`extensions_manage` / `open_canvas` / `invoke_canvas_action`）はこの
  Claude Code 環境に存在せず、今後も存在しない前提で計画する。
- **GitHub Copilot に委譲するのは、実装がすべて完了した後の「ライブ検証」
  1 ステップのみ**である。ライブ検証とは、実際に GitHub Copilot app 内で
  Canvas を開き、拡張の検出（`extensions_manage`）、ヘルパーページの実描画
  （`open_canvas`）、5 つの Canvas action の実エージェント経由呼び出し
  （`invoke_canvas_action`）を目視・実行確認することを指す。これは特定の
  子 Issue に固有の制約ではなく、Canvas に触れるすべての子 Issue（A〜D）に
  共通する、実装後の最終検証工程である。子 Issue ごとに個別のライブ検証
  pending 注記を書く代わりに、本決定以降は Sprint15 全体で 1 回の統合ライブ
  検証ハンドオフとして扱う（README の "Live validation handoff" 参照）。

この前提のもと、子 D と子 B 残作業（Canvas 側 consumer）を実装対象として
確定する。

### 子 D（Canvas raw preview）: 実装を許可し、具体的な実装方式を確定する（D037 更新）

D037 で「実装するとすればこの形」というテンプレートに留めていた子 D を、
以下の具体的な実装方式で **実装対象**に格上げする。

- Local Monitor の既存 raw-bearing route `GET /traces/{rawRecordId}/raw` は
  固定フォーマットの HTML（`<!DOCTYPE html>...<pre>{HtmlEncoder.Default.Encode
  済み payload}</pre></body></html>`、`MonitorHost.cs` 実装で確認済み）を返す。
  payload はすでに HTML エンコード済みであるため、Canvas 拡張はこの応答から
  最初の `<pre>` と最後の `</pre>` の間の部分文字列を抽出し、**再デコード・
  再エンコードせずそのまま**自分のヘルパーページの `<pre>` へ埋め込める
  （payload 自体がエンコード済みのため二重エンコード / 誤デコードのリスクが
  ない）。
- Canvas 拡張の Node プロセスがこの route を server-to-server で fetch する
  際、ブラウザではないため `Origin` / `Sec-Fetch-Site` ヘッダーを送出しない。
  `MonitorHost.IsCrossSiteRequest` はこれらのヘッダーが無い場合はブロックしない
  （ヘッダー不在 → cross-site 判定なし）ため、この fetch は同一ローカル利用者の
  別プロセスによる loopback 読み取りとして、`security-data-boundaries.md`
  記載の既存の受容済みリスク（「同一ローカル利用者の別プロセスが loopback 経由で
  raw を読む」）の範囲内に収まる。新たなリスクを追加しない。
- raw は **span 単位**（`raw_record_id` は span 行にのみ存在。trace 単位では
  複数の raw record にまたがりうる）。ヘルパーページの既存の任意 span id 入力
  欄（analyze 機能で既に存在する `#span` input）を流用し、trace + span id が
  指定されている場合にのみ「生データを表示（新しいタブ）」リンクを有効化する。
- 新規ルート `GET /raw-preview/:traceId/:spanId`（Canvas 拡張所有の loopback
  サーバー上、既存ルートと同じ `?t=token` クエリ認証。ブラウザの通常の
  リンククリック＝ページ遷移であり、fetch + JSON ではない）:
  1. `traceId`/`spanId` を既存の `TRACE_ID_PATTERN`/`matchesTraceId` で検証。
  2. 既存の `fetchSpanPage` 相当のロジックでトレースの span 一覧を取得し、
     `span_id` が一致する span の `raw_record_id` を探す。見つからなければ
     `404`。
  3. `fetchTextWithTimeout` で `GET {monitorUrl}/traces/{rawRecordId}/raw` を
     server-to-server fetch する。Local Monitor が `--sanitized-only` で
     raw route が `404` の場合は、その旨を明確に示す（壊れた画面ではなく
     「raw は利用できません（Local Monitor が --sanitized-only）」という文言）。
  4. 応答 HTML から `<pre>` 〜 `</pre>` の部分文字列を抽出し、拡張独自の
     固定 HTML テンプレート（`Cache-Control: no-store`、ヘルパーページへ戻る
     リンク付き）の `<pre>` へそのまま埋め込んで返す。
  5. クライアント側 JS は raw を JSON として一切受け取らない（このルート自体が
     HTML ページ全体を返す通常のページ遷移であり、fetch + `innerHTML` ではない）。
- 新規 Local Monitor endpoint は追加しない。既存の raw-bearing HTML route と
  既存の sanitized spans route のみを消費する。
- Canvas **action**（`invoke_canvas_action` 経由）は本決定後も一切変更しない。
  raw preview はこの新規ページ遷移ルートに限定する。

### 子 B 残作業（Canvas 側 consumer）: 実装対象として確定する

M2 で追加した `GET /api/monitor/summary` は Local Monitor 側の endpoint のみで
あり、Canvas 側の consumer（実際にこの集計を表示する画面）はまだ存在しない。
これを実装対象として確定する。設計はヘルパーページ全体を作り直さない最小追加
とする: 既存のヘルパーページに「Local Monitor 概要」カードを追加し、
拡張所有の loopback サーバーに新規ルート `GET /api/summary`（既存
`/api/traces` と同じ `x-canvas-token` 認証）を追加して
`GET {monitorUrl}/api/monitor/summary` を bounded にプロキシし、
`per_model_summary` / `per_client_kind_summary` の上位数件と
`latest_trace` / `top_token_trace` / `error_trace` を一覧表示する。新規
Canvas action は追加しない（ヘルパーページ own route のみ）。

不変（D036 / D037 を維持）:

- Canvas action response は bounded DTO のまま。raw prompt / response body、
  tool arguments / results、PII、credential、token、local sensitive path、raw
  OTLP payload を Canvas action / ログ / 静的成果物へ返さない。
- 子 D の raw preview はヘルパーページの server-rendered ページ遷移に限定し、
  Canvas action / Copilot プロンプト送出経路には一切流れない。
- 新規 Local Monitor endpoint は子 B 残作業（`/api/monitor/summary` は M2 で
  実装済み、追加の新規 endpoint は不要）・子 D いずれでも追加しない。
- `--sanitized-only` を Canvas 利用の前提に戻さない。
- ライブ検証（GitHub Copilot Canvas runtime）は本決定の実装スコープに含まない。
  実装完了後の別工程として扱う。

## D039: Canvas のトレース選択にプロンプトラベルを表示する（D035 の JSON raw-bearing route パターンを踏襲）

Status: Accepted

### 背景（利用者との議論）

Sprint15 M1（child A）のトレース選択ドロップダウンは、`compactTrace` 由来の
sanitized な決定支援ラインのみ（状態 / モデル / span 数 / tool 数 / token 数 /
時刻 / 所要時間 / 短縮 trace id）を表示する。利用者から、どのプロンプトの
トレースかをドロップダウン上で識別できないか（＝プロンプト自体を選択肢に
出せないか）という要望があった。

これに対して次の論点整理を行った。

- D020 DR6 の「同一ローカル利用者が自分の raw を loopback 経由で見ること自体は
  脅威ではない」という前提は維持される。今回の論点はそこではない。
- AGENTS.md の Local-First Risk Posture が明示的に defend 対象とする
  "other-origin browser-mediated exfiltration"（同一ブラウザで開いた別サイトが
  loopback 経由で raw を読み取り外部へ送出するケース）が、本来「JS は raw を
  取得しない」原則の対象である。
- ただし Canvas 拡張の own server（helper server）は、既存の全ルートが
  起動ごとのランダムトークン（`x-canvas-token` / `?t=`）で保護されている。
  このトークンを知らない第三者サイトの JS は、そもそも `/api/traces` を含む
  既存の JSON API も呼べない。したがって「JSON 経由で追加のフィールドを返す
  こと自体」が、Local Monitor 本体（same-origin チェックのみで守る、秘密
  トークンを持たない）と同じ意味で新たな穴になるとは限らない。
- 一方、D032 は「プロンプトラベルは server-rendered surface（`/` と
  `/traces`）でのみ表示し、`/api/monitor/*` と SSE には一切含めない」ことを
  明示していた。この制約をそのまま緩めるのではなく、既に別の目的で
  同種の JSON raw-bearing route を確立している **D035**（Local Monitor の
  raw analysis: `/traces/{traceId}/analysis/runs/{runId}` は
  `WriteJsonAsync` で raw を含む JSON を返す。same-origin チェック、
  `Cache-Control: no-store`、`--sanitized-only` で route 自体が不在になる、
  という 3 点で保護される）と同じパターンに乗せることで、新規の例外を
  作るのではなく既存パターンの拡張として位置づける。

### 決定事項

- Local Monitor に新規の raw-bearing JSON route
  `GET /traces/{traceId}/prompt-label` を追加する。`/api/monitor/*` の
  sanitized family には含めない（D032 の「`/api/monitor/*` と SSE は
  プロンプトを含めない」を維持する）。
  - 実装は D035 の raw analysis route 群と同じ `if (!options.SanitizedOnly)`
    ブロック内に置く（`--sanitized-only` では route 不在＝`404`）。
  - `MonitorHost.IsCrossSiteRequest` による same-origin チェック（cross-site
    は `403`）と `Cache-Control: no-store` を、既存の raw-bearing route と
    同様に必須にする。
  - 抽出ロジックは新規実装せず、既存の
    `MonitorPromptExtractor.ExtractPromptLabel(payloadJson, traceId)`
    （`internal static`、同一アセンブリ内なので可視性変更は不要）と
    `IMonitorProjectionStore.ListRawRecordsByTraceId` を shared
    `MonitorPromptExtractor.RecordScanLimit` で再利用する。prompt を含まない
    first raw record が先にある trace でも、後続 raw record の代表
    prompt label を抽出する（120 文字上限・空白正規化・trace 不一致時
    `null` は既存実装のまま）。
  - レスポンス形: `{ "trace_id": "...", "prompt_label": "..." | null }`。
    `prompt_label` が `null` になるのはエラーではなく「抽出できなかった」
    正常系（fallback は呼び出し側が担当）。
  - trace id の形式検証は行わない。D035 の `/traces/{traceId}/analysis/...`
    と同じく `traceId` を無制約の文字列として扱い、不正・未知の id は単に
    ストアから 0 件のレコードが返るだけなので `200` / `prompt_label: null`
    となる（エラーではなく正常系）。DB busy は既存の `persistence_busy`
    `503` パターンを踏襲する。
- Canvas 拡張の own server（`extension.mjs`）の `/api/traces` ルートと
  `/api/summary` highlight traces（Canvas action ではなく helper page 専用
  ルート。既に `sanitizeDto()` を通していない、M5 の raw-preview と同じ
  「helper page surface」区分）に、各 trace について
  `GET {monitorUrl}/traces/{traceId}/prompt-label` を server-to-server で
  fetch した結果を `prompt_label` として追加する。
  - 一覧は既存どおり最大 `MAX_TRACE_LIST_LIMIT`（50）件に bounded。50 件分の
    fetch は `Promise.all` で並列化する（loopback 通信のため許容範囲と判断。
    実測で問題が出た場合はバッチ API を別途検討する）。
  - `--sanitized-only` 時は route 自体が `404` になるため、Canvas 側は
    既存の fetch 失敗ハンドリングでそのまま `prompt_label: null` 相当に
    フォールバックする（特別分岐は追加しない）。
  - ヘルパーページのドロップダウン表示と概要 highlight 表示は、
    `prompt_label` が取得できた trace については
    `"${prompt_label} — ${既存の formatTraceLine 相当の行}"` の形式にし、
    取得できなかった trace は既存の決定支援ラインのみを表示する
    （情報を削除せず追加するフォールバック設計）。`/api/summary` は
    prompt label を運ぶため `Cache-Control: no-store` とする。

### 不変

- Canvas **action**（`invoke_canvas_action` 経由の5アクション）は本決定後も
  一切変更しない。`prompt_label` は helper page 専用ルート（`/api/traces` と
  `/api/summary` highlight traces）にのみ現れ、Canvas action response、
  `session.send()` に渡すプロンプト、ログ、静的成果物には一切流れない。
- `sanitizeDto()` の forbidden-key フィルタ（`prompt` を含む正規表現）は
  今回変更しない。`/api/traces` はもともとこのフィルタを通っていない
  （helper page 専用ルートのため）ので、フィルタを緩める必要はない。
- `/api/monitor/*` と SSE は引き続き sanitized metadata のみで、
  プロンプトを含めない（D032 を維持）。
- `--sanitized-only` 下では Local Monitor 本体のページ（`/` / `/traces`）と
  同様、Canvas 側でもプロンプトラベルは表示されない。
- 新規 endpoint は `prompt_label`（最大120文字、既存 truncation ロジック）
  のみを返す。full raw payload を返す新規 JSON route は追加しない
  （D020 の「JSON raw API を安易に増やさない」という慎重姿勢は、この
  スコープ限定によって維持される）。

Update (D042 / D050):

- Sprint18 の Local Monitor overview / trace-list と Canvas helper は、同じ
  `GET /traces/{traceId}/prompt-label` を same-origin / token-gated local
  screen で `fetch` し、prompt label を `textContent` 相当で表示してよい。
  これは D032 の「JS は raw を取得しない」を full raw payload に限定して
  解釈し直す更新であり、prompt label の短い JSON route 以外の raw JSON
  API 追加や `/api/monitor/*` への prompt field 追加は許可しない。

### 実装対象（次段階）

- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`:
  `GET /traces/{traceId}/prompt-label` を追加。
- `.github/extensions/otel-monitor-canvas/extension.mjs`:
  `/api/traces` ルートで `prompt_label` を並列 fetch して付加する
  `fetchHelperPromptLabels`（仮称）を追加。
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`:
  ドロップダウン表示ラベルを組み立てる純関数（`formatTraceLine` と
  `prompt_label` を合成する）を追加し、`node --test` で単体テストする。
- `tests/CopilotAgentObservability.LocalMonitor.Tests/`:
  新規 endpoint の same-origin / `--sanitized-only` / 正常系の契約テスト、
  および `CanvasExtensionContractTests.cs` への追加 fact。
- `docs/specifications/security-data-boundaries.md`: 本決定の内容を
  D032/D035 セクション付近に追記する。

本決定は設計の確定であり、コード実装は利用者の明示的な go-ahead を得てから
着手する（D037→D038 と同じ二段階の手順を踏む）。

## D040: Canvas cross-repo adapter の配布単位と sanitized repository metadata contract を固定する

Sprint16 では GitHub Copilot app Canvas adapter を他 repository へコピー可能な
extension distribution unit として整理する。配布の source of truth は
`.github/extensions/otel-monitor-canvas/` のみとし、mirror folder は作らない。
この sprint では runtime / development dependency、`package.json`、lockfile、
`node_modules` を追加しない。

Local Monitor projection と Canvas helper が repository / workspace を識別する
ため、既存の推奨 OTLP Resource Attributes だけを source にした sanitized
metadata を新規 trace から投影してよい。

| Projected / Canvas field | Source attribute | Boundary |
| --- | --- | --- |
| `repository_name` | `vcs.repository.name` | sanitized display label |
| `workspace_label` | `workspace.name` | sanitized display label; not an absolute path |
| `repo_snapshot` | `repo.snapshot` | sanitized branch / commit / snapshot label when present |

これらは D030 / D032 の「Canvas adapter は新たな schema / API field を追加しない」
という過去の不変条件に対する scoped exception である。許可範囲は sanitized
`/api/monitor/*`、Canvas helper routes、bounded Canvas action DTO に限る。
raw prompt / response body、tool arguments / results、PII、credential、token、
local sensitive path、raw OTLP payload を Canvas action / logs / repository-safe
output へ返す禁止は維持する。

既存 projected rows は自動 backfill しない。新しい nullable projection columns は
新規 ingestion または明示的な DB 再生成で埋まる。Canvas helper は metadata 欠落時
`unknown repository` を表示する。`repository_full_name`、`workspace_hash`、
`git_branch`、`git_commit_sha`、`source_kind` は、この sprint では追加しない。
CM-1 では repository label source を OpenTelemetry VCS semantic convention
に合わせて `vcs.repository.name` へ置き換える。`repo.name` 互換 fallback は
持たず、`vcs.repository.url.full` は Canvas helper / bounded action DTO へ
返さない。Issue #58 では resource-scoped `vcs.repository.name` を authoritative
のまま維持し、その key が absent の場合だけ canonical GitHub HTTPS
`vcs.repository.url.full` の sanitized repository segment を fallback として
許可する。unsafe name は fallback せず、raw URL / owner は projection に保存しない。
`/diagnostics` は Retention-gated な bounded key/count/scope/classification、fixed
5-state reason、label/fallback booleans だけを表示し、attribute value、identity、PII、
credential、path は表示・永続化しない。既存 API / SSE / Canvas DTO shape と
#72 / #85 の nullable projection handoff は変更しない。

## D041: Canvas analysis UX は session.send の requested controls として扱う

Status: Accepted

Sprint17 では Canvas helper の既存 `POST /analyze` → `session.send({ prompt })`
経路を維持する。Canvas helper は Local Monitor Copilot raw analysis runner を
起動せず、`/traces/{traceId}/analysis` も呼ばない。

決定事項:

- Local Monitor は sanitized `GET /api/analysis/options` で profile / model /
  reasoning / timeout hint metadata を提供してよい。
- Canvas helper はこの metadata を token-gated proxy で取得し、UI controls、
  generated prompt、dispatch metadata に使う。
- `model`、`reasoning effort`、`timeout` は per-message execution control ではなく
  requested values とする。`session.send()` が実行モデル・reasoning・実行 timeout
  を強制したとは UI / response / docs で主張しない。
- `sendAndWait` は Sprint17 では採用しない。idle 待機 timeout は in-flight agent
  work を abort しないため、analysis execution timeout と誤解されやすい。
- 最終分析結果 metadata は、後続 OTel telemetry から安全に相関できる設計ができる
  まで scope 外とする。

不変:

- Canvas action responses / logs / committed outputs / static artifacts には raw
  prompt / response body、tool arguments / results、PII、credential、token、local
  sensitive path、raw OTLP payload を返さない。
- Local Monitor raw analysis runner は引き続き Local Monitor 本体の raw-default
  local surface であり、Canvas helper analysis UX とは別経路である。

## D042: Local Monitor UI は Sprint18 デザインハンドオフの Console 型 IA / hex トークン / 7 画面へ再設計する

**Status: superseded for current product IA by D075.** This section is retained
as implementation history. Its permanent sidebar, Overview/KPI surface,
trace-first master-detail list and split flow/waterfall structure are not
current Local Monitor v1 authority.

Status: Accepted

Sprint18 では Local Ingestion Monitor の UI を
`.claude/design_handoff_local_monitor/README.md`（2026-07-03 確定版）に従い
全面再設計する。開発者を最優先ユーザー、token コストの把握・削減を最重要
シナリオとし、Console 型 IA（208px 左サイドバー + master-detail）を採用する。

決定事項:

- ナビゲーションは **2 項目のみ**（概要 / トレース。トレースに件数バッジ）。
  「診断」はナビから外し、サイドバー最下部の受信ステータスバッジ →
  ポップオーバー → 「詳細診断を開く」の段階的動線とする。`/diagnostics` への
  直接 URL アクセスは引き続き機能する。診断ページ自身でもナビは 2 項目とする
  （確定 IA テキストがカンバス A4 の 3 項目サイドバーより優先。C1）。
- 実装対象画面は 7 つ: 概要ダッシュボード / トレース一覧（master-detail）/
  トレース詳細（フロー・waterfall 切替 + キャッシュ列）/ スパンインスペクタ
  （詳細画面内パネル）/ エラー解析モード（詳細画面バリアント）/ Copilot 解析
  ドロワー / 診断。インスペクタ・エラーモード・ドロワーは route を増やさず
  トレース詳細ページ内の状態とする。
- デザイントークンはハンドオフ §10 の **hex 実測値を正**とし、`monitor.css`
  `:root` を OKLCH から hex リテラルへ書き換える。`DESIGN.md` も hex を
  authoritative と宣言する（ピクセル忠実再現の指示による。C2）。
- トレース詳細のタブ（Summary / Timeline / Flow Chart / Cache）は**廃止**し、
  フロー | waterfall セグメント切替 + 常設キャッシュ列の 1 画面構成にする（C3）。
- トレース一覧はカードリストからテーブル + 右プレビューパネル（392px）の
  master-detail へ変更する。route `/traces` は不変（C4）。
- 取り込み履歴は新 route を作らず、診断ページ下部の折りたたみセクション
  （既存 `GET /api/monitor/ingestions` を使用）とし、ポップオーバーの
  「取り込み履歴」ボタンは `/diagnostics#ingestion-history` へリンクする（C5）。
- 既存 public routes（`/api/monitor/*`、`/health/*`、`/events`、`/v1/traces`、
  既存 raw-bearing routes）は shape / ordering を変えない。新規需要はすべて
  **新規 endpoint** で満たす。`CanvasExtensionContractTests.cs` は無変更のまま
  green を維持する（C6）。
- Noto フォントの weight 600 は vendored されていないため、デザインの 600 は
  CSS 上 700 へマップする（記録済みの accepted deviation。C7）。
- プロンプト検索は server 側 TraceId 部分一致 + client 側での読み込み済み行の
  prompt label フィルタに限定する。全コーパスの prompt 全文検索は scope 外
  （documented limitation、`docs/task.md` の follow-up。C8）。

不変:

- sanitized / raw 境界（D020 / D023 / D032 / D035 / D039)、loopback bind、
  Host-header 検証、same-origin / no-store / `--sanitized-only` 除去、
  `createElement` / `textContent` による DOM 生成（`innerHTML` 不使用）、
  vendored fonts（CDN 不可）は維持する。
- readiness contract（既定しきい値、単位、設定名、HTTP status mapping、
  機械可読 body）は変更しない。

## D043: スパンインスペクタ用に raw-bearing JSON route `GET /traces/{traceId}/spans/{spanId}/detail` を追加する

Status: Accepted

Sprint18 のスパンインスペクタ（整形 / raw タブ）は span 単位の raw 由来
詳細（tool 呼出引数・結果末尾、llm メッセージ構成・プレビュー、OTLP span
JSON 全文）を必要とする。既存の `/api/monitor/*` は sanitized-only を維持
するため、D032 / D035 / D039 と同じ route-boundary パターンで **新規
raw-bearing JSON route** を追加する。

- `GET /traces/{traceId}/spans/{spanId}/detail` は `/api/monitor/*` 外の
  raw-bearing route とし、`!options.SanitizedOnly` ブロック内でのみ登録する
  （`--sanitized-only` 時は route 不在 = `404`）。
- `MonitorHost.IsCrossSiteRequest` による same-origin 強制（cross-site は
  `403`）と `Cache-Control: no-store` を適用する。未知の trace / span id は
  `404`。
- 抽出は新設 `SpanDetailExtractor`（`MonitorPromptExtractor` と同じく pure /
  exception-safe / best-effort）が行い、整形抽出が失敗しても raw span JSON
  は常に返す（raw タブは常に機能する）。
- 実ペイロードのキー名は live 検証まで未確定のため、抽出は defensive に実装し
  live-validation caveat を残す（D032 の prompt extractor と同じ扱い）。
- `/api/monitor/*` と SSE は引き続き raw / PII を返さない。

## D044: monitor projection schema v4 で cache token rollup と trace_status を追加する

Status: Accepted

Sprint18 の概要 KPI（実効入力換算、キャッシュ読取率）とトレース一覧
（cache% 列、状態フィルタ）は trace 単位の cache token 集計と回復状態を
必要とする。`monitor_traces` に additive migration（v3 → v4）で以下を追加
する。

- `cache_read_tokens INTEGER NULL` / `cache_creation_tokens INTEGER NULL`:
  既存 token 集計と同じ root-invoke-agent-else-chat の二重計上防止規則で
  合算する。
- `trace_status TEXT NULL`（`ok` | `recovered` | `unrecovered`）: エラー span
  なし → `ok`、最終 span（StartTime、同値時 SpanOrdinal fallback）がエラー →
  `unrecovered`、それ以外 → `recovered`。
- 既存行は backfill しない（D040 前例）。NULL は率計算から除外し、一覧の
  状態フィルタでは「unknown」として中立マーカー扱いする（documented
  limitation）。
- `MonitorSchemaVersion` を 3 から 4 へ上げる。migration は
  `AddColumnIfMissing` による additive-only とする。

## D045: Copilot ドロワーの追い質問は履歴再送（history resend）方式とする

**Status: narrowed by D075.** The installed trace-analysis transport remains a
frozen technical contract. Local Monitor v1 optional AI is Session-first; only
whole-Session reports have durable immutable history, while node,
Repository/Compare results and follow-up chat are non-permanent under #162.

Status: Accepted

Sprint18 の Copilot 解析ドロワーはチャット形式の追い質問を提供するが、
server 側に会話 session 状態を持たない。

- 各追い質問は**新規 analysis run** を作成し、その prompt に過去の Q&A
  transcript を埋め込んで再送する（history resend）。
- transcript はクライアント（ドロワー JS、trace 単位）が保持する。
  `monitor_analysis_runs` schema は変更しない。履歴は server 側へ永続化
  しない。
- `AnalysisStartPayload` に optional `Question` と `History`（Q&A turn の
  list）を追加し、runner の prompt 組み立てで既存 focus 指示に履歴ブロック +
  追い質問を追記する。raw の取り扱い・route 境界・CSRF / same-origin /
  no-store / `--sanitized-only` 無効化は D035 のまま変更しない。
- ドロワーには「ローカル SDK 経由 · raw はローカルから出ません」の
  データ境界コピーを必須表示する。

## D046: Copilot raw analysis に指示診断（instruction-diagnosis）focus を additive に追加する

Status: Accepted

Issue #46 Phase 1（Sprint19）として、既存 Local Monitor Copilot raw
analysis に、利用者が agent へ与えた実装指示を trace 証拠に基づいて
診断する analysis focus を 1 つ追加する。目的は「trace 由来の指示
フィードバックは一般的な prompt アドバイスに勝る」という Phase 1 の
価値仮説の検証である。

- additive な focus 拡張のみ: `MonitorAnalysisFocus` に新値 1 つと
  prompt template branch を追加する（`tool-usage` / `agent-flow` の
  D035 前例に従う）。新規 route / schema / API field は追加しない。
- wire value は `instruction-diagnosis`、ドロワーの日本語ラベルは
  「指示診断」とする（既存の短い名詞ラベル慣例に合わせる）。
- 証拠は trace 内部のみ: 追い指示・言い換え turn、error span、
  失敗 / 再試行 tool call、token 浪費。GitHub issue / commit /
  test evidence との相関はしない（D037 の trace 手動選択方針を踏襲）。
- 表示はドロワーのみ: Canvas helper focus set（`latency` / `tokens` /
  `cache` / `errors`、D036）は拡張しない。memory candidate 生成、
  採用ワークフロー、新規 repository-safe export も追加しない。
- taxonomy v1 は 5 分類（goal clarity / ambiguity / missing
  acceptance criteria / task size・split / missing
  context・constraints）とし、「分類は対応する trace 内証拠パターンと
  セットでのみ存在できる」を規律とする。正本は
  `docs/specifications/interfaces/instruction-diagnosis-analysis.md`。
- finding は固定 4 点形式: 分類 / trace 証拠引用（span、turn）/
  ギャップ説明 / 次回向け改善指示文。引用可能な証拠のない finding は
  出力禁止。finding ゼロは有効な結果であり、その旨を明示出力する。
- prompt-only で開始する: raw trace を既存 runner に投入し、prompt が
  span / turn 引用を要求する。実証済み証拠パターンの deterministic
  pre-extractor 化は後続 phase とする。引用ハルシネーションの持続は
  M5 gate 失敗であり、Phase 2 前に pre-extraction が必要という
  シグナルとして扱う。
- 不変: `--sanitized-only` は新 focus を含む raw analysis 面全体を
  無効化したままとする。D045 の履歴再送追い質問は新 focus でも機能
  する。raw / route 境界（D035、security-data-boundaries.md）は変更
  しない。`CanvasExtensionContractTests.cs` は無変更で green を維持
  する。

## D047: 指示診断に deterministic な証拠事前抽出を additive に追加する

Status: Accepted

Issue #46 Phase 2 step 1（Sprint20）として、`instruction-diagnosis`
focus に、解析開始時にコードで決定的（deterministic）に証拠を事前
抽出し、構造化された検証可能な証拠を LLM に渡す仕組みを追加する。
動機は Sprint19 M5 の GO 判定と 2 つの設計インプット、すなわち
「分類=証拠結合が最弱の契約要素だった（9 finding 中 2 件が実在証拠を
引用しつつ分類定義を拡大解釈した）」および「解析は trace 単位である
一方、Copilot CLI は起動ごとに 1 trace を発行し conversation id が
兄弟 trace を繋ぐ」である。

- extractor field set は `error_spans[]` / `retry_chains[]` /
  `turn_tokens[]` / `user_instruction` / `conversation` の 5 つと
  する。各 field の意味・包含規則・順序規則・決定性規則の正本は
  `docs/specifications/interfaces/instruction-diagnosis-analysis.md`
  の Evidence Extractor Output Contract とする。
- additive な process-internal tool `get_instruction_evidence` を
  既存 6 tool（`get_raw_trace` ほか、D035）の隣に 1 つ追加する。
  既存 6 tool は無変更で維持する（モデルによる検証経路として残す）。
- 読み取り専用の projection store query
  `ListConversationTraces(conversationId)` を 1 つ追加する。既存
  `monitor_spans.conversation_id` 列への read のみで、schema 変更・
  projection migration・新規 route・新規 API field は伴わない
  （additive 境界の黙約的拡張とならないようここに明記する）。
- prompt template v3: taxonomy 分類ごとに extractor field の引用を
  必須化する（per-category required-evidence 規則。正本は同 interface
  spec の Per-Category Required Evidence）。extractor 出力の外に
  根拠を持つ finding は、raw tool で明示検証した span id 引用が
  あり、その旨を finding 内に明記した場合のみ許可する
  （escape hatch。extractor が見えない証拠の発見可能性は維持する）。
- M5 A/B gate: Sprint19 の 3 基準（引用実在・trace 固有性・
  no-evidence-no-finding）に「全 finding が extractor field または
  明示 raw 検証済み span 引用に接地している」を加えた 4 基準とする。
  Sprint19 B1 finding 3 / 4 と等価形の再発は gate 失敗とする。有効
  finding 数が同一 trace 群で Sprint19 より実質的に減る場合は結合
  規則が強すぎるシグナルとして記録し、緩和を反復する。
- 不変: `--sanitized-only` は raw analysis 面全体を無効化したまま
  とする。D045 履歴ブロック、固定 4 点形式、no-evidence-no-finding
  規則、日本語出力規則（D046）は変更しない。Canvas focus set
  （D036）は拡張せず、`CanvasExtensionContractTests.cs` は無変更で
  green を維持する。extractor 出力に長い raw 本文を含めない（raw
  由来は上限付き `user_instruction` descriptor のみで、raw analysis
  面と共に `--sanitized-only` で消える）。

## D048: 指示診断に bounded conversation context を追加する

Status: Accepted

Issue #46 Phase 2 step 2（Sprint21）として、`instruction-diagnosis`
focus の deterministic extractor を拡張し、選択 trace を anchor にした
bounded same-conversation context を `get_instruction_evidence` の出力に
追加する。動機は Sprint20 の `conversation` field が sibling trace id・
順序・件数に留まり、Copilot CLI のように 1 起動 1 trace で
`conversation_id` が兄弟 trace を繋ぐ場合でも、LLM が前後 trace の
短い診断材料を参照できなかったことである。

- 選択 trace は常に anchor であり、sibling traces は補助証拠である。
  `conversation_id` がある場合のみ、既存の read-only
  `ListConversationTraces(conversationId)` ordering（earliest span start time、
  tie-break trace id）に従い、選択 trace の前後最大 2 trace ずつと
  選択 trace 自身を含む window を作る。emitted entries は最大 5 件。
- `conversation` metadata は維持し、additive に `conversation_context`
  を追加する。`conversation_context` は conversation id、trace count、
  analyzed trace index、window start/end index、before/after truncation、
  および trace summary list を返す。trace summary は trace id、
  relative position、analyzed-trace marker、first start time、上限付き
  first-line instruction descriptor、turn/token totals、error/retry summary
  counts、上限付き error span ids / retry tool names に限る。
- sibling raw-derived instruction descriptor は Local Monitor raw analysis
  面だけの local runtime data であり、長い raw prompt / response /
  tool body、PII、credential、provider URL、local sensitive path を
  extractor output、repository-safe summary、Issue / docs / dashboard /
  static artifact に出してはならない。descriptor は Sprint20 と同じ
  first-line 160 characters + `...` truncation posture を使う。
- 新規 public route、`/api/monitor/*` field、SSE change、projection
  migration、Canvas focus change、memory candidate、adoption workflow、
  repository-safe raw export は追加しない。既存 6 raw tools と
  `get_instruction_evidence` tool name は維持する。必要な I/O は既存
  projection store reads で行い、大きな conversation 全体の raw records
  を読まず、emitted bounded window 内だけを読む。
- prompt template v4 は `get_instruction_evidence` を最初に呼ぶことを
  要求し、analyzed-trace evidence と sibling-trace evidence を区別する。
  sibling evidence を使う finding は sibling `trace_id` と relative
  position を引用し、選択 trace との関係を説明しなければならない。
  `conversation_context.traces[]` 外の trace は引用禁止であり、必要な
  証拠が bounded window 外にある場合は、推測せず bounded evidence が
  insufficient であると述べる。
- M5 live validation gate は Sprint20 の citation existence、trace
  specificity、no-evidence-no-finding、extractor/raw grounding に加え、
  bounded-window compliance と sibling relationship clarity を確認する。
  repository evidence は sanitized observation だけを記録し、full
  analysis markdown や raw content は committed files に残さない。
- 不変: `--sanitized-only` はこの conversation scope を含む raw analysis
  面全体を無効化したままとする。D045 履歴ブロック、固定 4 点形式、
  no-evidence-no-finding 規則、日本語出力規則、Canvas focus set
  （D036）は変更しない。

## D049: GitHub Pages deploy surface を削除する

Status: Accepted

GitHub Pages があることで Local Monitor / Canvas の raw-default 判断と
repository-safe publishing 判断が混同されるため、Pages deploy surface を
現行スコープから削除する。

決定事項:

- `.github/workflows/static-dashboard-pages.yml` を削除する。
- `gh-pages` branch への snapshot commit / push、Pages artifact upload、
  `actions/deploy-pages` による deploy は行わない。
- Static dashboard generator は残す。出力契約は local artifact
  `index.html` / `dashboard-data.json` とする。
- Local Monitor と Canvas helper の raw-bearing local surfaces は引き続き
  単一信頼ローカル利用者向けに raw prompt / response を表示してよい。
- raw / PII を repository-safe outputs、static dashboard、CI artifact、logs、
  Issue、PR、docs へ出さない境界は維持する。

## D050: Canvas helper に選択 trace の prompt / response preview を表示する

Status: Accepted

単一信頼ローカル利用者向けの Local Monitor / Canvas helper では、利用者自身の
prompt / response を画面に表示してよい。D049 で Pages deploy surface を削除し、
repository-safe publishing と local screen の判断を分離したため、Canvas helper の
ローカル表示も Local Monitor と同じ raw-default posture に揃える。

決定事項:

- Canvas action responses、`session.send()` prompt、logs、committed outputs、
  static artifacts、Issue / PR / docs には raw prompt / response を出さない。
- Canvas-owned loopback helper server に token-gated `GET /api/trace-content/:traceId`
  を追加し、既存 raw-bearing route `GET /traces/{traceId}/spans/{spanId}/detail`
  から server-to-server で prompt / response preview を取得して表示する。
- Canvas helper の trace dropdown と「Local Monitor 概要」highlight trace は、
  同じ token-gated helper screen に限って prompt label を表示してよい。
- 新しい Local Monitor endpoint、`/api/monitor/*` field、SSE field、projection
  schema は追加しない。`--sanitized-only` では preview は表示されない。
- helper page の client-side rendering は `textContent` による inert text 表示を
  維持し、`innerHTML` で captured content を挿入しない。

## D051: Session foundation を独立 subsystem として追加する

Status: Accepted

Issue #51 は、D036 / D037 の「Canvas は新 telemetry input / schema / session
correlation を追加しない」という絶対表現を、ここで定義する範囲に限って更新する。
既存 Canvas actions の bounded DTO、loopback/token、raw 非送出、D020/D023 の local
raw boundary、Issue #45 `session.send()` behavior、Issue #49 Agent ownership は変更しない。

決定事項:

- `sessions`、`session_native_ids`、`session_runs`、`session_events`、
  `session_event_content`、`session_projection_state` を別 Session subsystem の
  additive tables とする。`RawTelemetryStore.cs` にこの責務を追加しない。
- local Session / Run / Event ID は UUIDv7 string。source uniqueness は SDK
  event ID、Hook canonical hash、OTel trace/span identity。merge は identical
  native session ID、explicit resume/handoff、exact trace context のみ許可し、
  repository / timestamp proximity は使わない。
- completeness は `unbound` / `partial` / `rich` / `full` の固定4値とし、定義は
  [Canvas Session workspace](specifications/interfaces/canvas-session-workspace.md)
  を正本とする。
- `POST /api/session-ingest/v1/events` は schema/header v1、adapter
  `copilot-sdk-stream|copilot-compatible-hook`、surface
  `copilot-sdk|copilot-cli|vscode|hook-unknown`、batch 1..100、1 MiB とする。
  `204` は commit 後のみ、failure は固定 `400/413/415/503/504` と
  `{ "error": "<code>" }` mapping を使う。
- sanitized reads は `/api/session-workspace` の sessions/detail/resolve/status。
  raw content read は same-origin/no-store、`--sanitized-only` で `404`、expiry 後
  `410` / `expired_pending_deletion`。
- raw content は secret-filter 後に分離保存し、
  `expires_at = captured_at + 90 days`。automatic physical deletion は Issue
  #89、user-controlled pin / unpin / delete-now は Issue #90。
- installed Local Monitor の `hook-forward --endpoint <loopback-url>
  --timeout-ms 250` は stdin JSON 1件を読み、invalid/network/timeout でも常に
  exit 0、stdout/stderr 無出力、Hook decision に影響しない。CLI/VS Code は同じ
  PascalCase Hooks を使い、曖昧 source は `hook-unknown`。environment、repository、
  tool name、transcript path、timestamp から推定しない。
- App/SDK は Canvas `ctx.sessionId` を native session ID として使う。persisted
  events は保存、ephemeral usage は集計、reasoning/delta は非永続。capture は最初の
  Canvas open から開始し、missed earlier events は復元せず completeness を下げる。
- OTel enrichment は既存 projection 後に専用 cursor で行う。exact OTel
  trace context は既存 event と byte-for-byte 一致する場合のみ link できる。
  `gen_ai.conversation.id` は既記録 native session ID と byte-for-byte 一致する場合
  のみ identical-native-ID rule で bind/enrich し、それ以外は `unbound`。
  `client_kind` は bind/merge に使わず、`hook-unknown` が `copilot-cli` か `vscode`
  かの確認だけに使える。既存 OTLP receiver、trace/span schema、readiness contract
  は変更しない。Session schema migration failure は startup host construction を失敗
  させ、readiness body / threshold / status mapping は拡張しない。
- Session UI 実装前に Issue #52 の current-screen capture と承認済み four-tab
  prototype を必須 gate とする。

direct apply、Compare、Agent graph、compatibility shim、dependency 追加、物理 raw
cleanup は Issue #51 に含めない。

## D052: Canvas Evidence は exact trace composition と #49 ownership のみを使う

Status: Accepted

Issue #53 は、選択 Session detail の `runs[].trace_id` に byte-for-byte で記録済みの
trace だけを run 順で合成する。null を除外し重複は最初の出現を残す。repository、
workspace、timestamp、conversation、latest、名称から trace を推測しない。

各 trace は独立した forest とし、Issue #49 `agent-graph` を hierarchy、caller、
ownership、parallel、presence、relationship の唯一の情報源とする。Canvas は ancestry
や time containment を再計算せず、span は `span_ownership` だけで Agent に付ける。
Session event は `run_id -> trace_id` が存在しても常に Session/unowned であり、Agent
ownership を推測しない。`none_detected` と `undeterminable`、exact / inferred /
unresolved は区別する。

Evidence は sanitized graph/spans と Session event metadata/content_state のみを表示し、
raw/event-content proxy、raw 再構成、test/review verdict や Skill identity の名称由来推測を
追加しない。`--sanitized-only` でも利用可能。Review gate は exact matching Session
event がある場合だけリンクし、なければ evidence unavailable とする。

## D053: Canvas Improve は明示入力した proposal lifecycle と session.send 分離を使う

Status: Accepted

Issue #54 は Canvas Improve の placeholder を、Session / Evidence の既存参照に基づく
local-runtime proposal lifecycle に置き換える。詳細分析の実行と proposal の構造化保存を
分離し、Issue #45 の `session.send()` boundary、Issue #51 identity/completeness、Issue #53
Evidence ownership を変更しない。

決定事項:

- 詳細分析は既存 token-gated `POST /analyze` -> `session.send({ prompt })` fire-and-forget
  のままとする。raw analysis runner を呼ばず、Copilot chat の応答を取得・scrape・保存
  しない。利用不可の内容を推測・復元しない。
- proposal は利用者が明示入力した target kind / opaque target label / sanitized rationale /
  expected effect / risk / opaque evidence references だけを local runtime SQLite に保存する。
  raw prompt/response、tool args/results、PII、credential、token、local sensitive path、
  source fragment は保存・action/log/prompt/repository-safe outputへ送出しない。
- lifecycle は `candidate` / `recommended` / `verified`。Candidate は citeable evidence を
  持つ。Recommended は2つ以上の distinct exact-bound Session の evidence と利用者の
  explicit promotion を必要とし、selected Session ごとに最大1件とする。Verified は Issue
  #56 の比較判定だけが設定できる。
- proposal write は loopback / same-origin / CSRF の明示操作に限る。auto-generation、
  auto-promotion、file/config/Skill/Agent/Instruction の自動変更はしない。
- direct apply、diff/path handling、snapshot、rollback、applied audit、git 操作は Issue #55
  の専有責務とする。

## D054: 人間承認済み proposal の local apply を root 制限 transaction に閉じる

Status: Accepted

Issue #55 は、Canvas から任意のローカル path を操作する機能ではなく、既存 Issue #54
proposal に紐付く human-approved local apply とする。Canvas action / `session.send()`
は filesystem authority を持たず、per-launch token を持つ helper screen が Local
Monitor の loopback surface を proxy する。これにより Canvas action response、log、
prompt、repository-safe output に source/diff/path を通さない。

決定事項:

- Local Monitor は明示的な startup `--apply-root user_config|skill|repository=<absolute-directory>`
だけを trusted root とする。既定 root、UI/API からの root 登録、任意 path は持たない。
root の canonical path は process 内部だけに保持し、Canvas には opaque root ID と kind
だけを返す。
- apply target は root 下の既存 regular file と normalized relative path に限る。
absolute/UNC/device/URI/`..`、directory/create/delete/rename/permission change、symlink/
junction/reparse point（root / ancestor / target）、root identity change は拒否する。
- 利用者は helper screen で full diff と hunk を選び、選択内容の digest を明示承認する。
approval は immutable であり、path/base hash/selection/replacement/root の変更は必ず
新しい selection + approval を必要とする。Issue #54 lifecycle は変更しない。
- 適用は全 target の root/reparse/base SHA-256/approval digest を write 前に検証する。
一つでも stale なら no-write。snapshot と write-ahead journal を flush した後に same-volume
atomic replace を行い、failure/uncommitted startup recovery は全 snapshot へ戻す。
durable state は all-applied または all-restored とする。
- rollback は apply 後 hash と current hash の一致を必要とし、external edit を clobber
しない。一度 rollback 済みの apply を再 rollback できない。
- uncommitted journal の opaque root ID を current startup root set から安全に解決できない
場合、記録済み absolute path を再探索・書込みしない。Local Monitor host construction を
fail-closed にし、mutation surface を公開しない。利用者は同じ trusted root を復元するか、
private local recovery record を解決してから起動する。partial transaction を受け入れた
状態で通常動作を続けることはしない。
- audit は opaque IDs、Session/proposal linkage、actor kind、state/error、timestamp、hash、
file count だけである。path、source/diff/replacement/snapshot、raw Session content、
credential/token、exception details は保存・返却・ログ出力しない。
- git の起動・branch/commit/push/PR 操作は構造的に追加しない。比較 verdict は Issue #56
の専有である。

正本は [Canvas Proposal Apply interface](specifications/interfaces/canvas-proposal-apply.md)
とする。

## D055: Compare は exact receipt と user-confirmed cohort を quality-first で判定する

Status: Accepted

Issue #56 は repository/timestamp proximity や単一総合 score による自動評価ではなく、
Issue #51 の exact Session/Run/trace、Issue #54 proposal revision、Issue #55 active
application receipt に結合した operational verification とする。

決定事項:

- objective quality は immutable local receipt とし、pass/fail、normal/severe、evaluator
  ID/version、criterion、case key、同一 Session/Run/trace の exact evidence refs を持つ。
  normalized measurement の unlinked `success_status` は直接利用しない。
- candidate は非権威的な提示に留め、pre/post/excluded cohort は利用者が明示確定する。
  repository または timestamp proximity だけで候補化・結合・分類しない。
- included Session は exact-bound / terminal / full かつ human または objective quality
  evidence を持つ。pre/post 各3件未満、missing/conflicting/partial evidence、rolled-back /
  stale application は `insufficient_evidence` とし、0・pass・success を補完しない。
- verdict は `improved` / `no_change` / `regressed` / `insufficient_evidence` の固定4値。
  severe と quality pass rate を先に比較し、quality 同等時だけ duration / total-token
  median の exactly-10%-improvement / greater-than-10%-worsening 境界を使う。
- summary と case-key drill-down は同じ persisted Session/evidence rows を投影する。
  raw content、path/source/diff、free-form note、単一0–100 scoreを保存・返却しない。
- `improved` effect receipt と proposal `verified` は同一 SQLite transaction で記録する。
  rollback 後は receipt を historical/inactive とし、active improvement として扱わない。
- Compare helper writes は loopback / same-origin / CSRF / no-store の明示操作とする。
  Canvas action、`session.send()`、log、repository-safe output、git 操作へ authority または
  comparison payload を追加しない。

正本は [Canvas Effect Comparison interface](specifications/interfaces/canvas-effect-comparison.md)
とする。

## D056: Source capability semantic contract v1

Status: Accepted

Issue #61 publishes a versioned producer-facing capability contract without
implementing a receiver, adapter, persistence, migration, HTTP, proxy, or UI
change. JSON Schema 2020-12 and the per-surface manifests are the
machine-readable structural/capability source of truth; canonical Markdown owns
the semantic rules below.

決定事項:

- A manifest `contract_version` and its schema major must match (`v1`), and the
  schema rejects unknown fields. A source capability observation may change
  within the declared shape only when it does not alter semantics or safety.
  Any added field (also optional), removed/renamed field, type/enum change,
  changed authority or completeness meaning, or acceptance of an unknown field
  is a breaking change and requires a new major schema plus matching manifests.
- available OTel identity/hierarchy/timing is authoritative for its field
  families when the existing exact-link rule applies. Hook/SDK native
  lifecycle/explicit event identity is authoritative for its field families.
  Historical summary allowlist-only fields are `model_tokens.*`,
  `retry_attempt.*`, and `errors`; they cannot affect identity, hierarchy,
  timing, lifecycle, or explicit event identity. Weak never overwrites strong,
  and missing values do not overwrite.
- Per-field provenance records the actual contributing adapter ID that supplied
  the field, such as `otel-http`, `copilot-compatible-hook`, or
  `copilot-sdk-stream`; source version or schema fingerprint; source event or
  trace/span identity; capture/content state; and normalization version. The
  composite `otel-http+copilot-compatible-hook` manifest label denotes
  registered paths only and is never per-field provenance. Missing provenance
  prevents authoritative promotion and does not authorize inference.
- Repository, workspace, and timestamp are context only. They are never
  identity evidence; no heuristic merge and no synthetic span are allowed. This
  preserves Issue #51 exact identity and leaves Issue #49 Agent ownership
  unchanged.
- Completeness is a deterministic pure decision over declared requirements and
  observed facts. It uses only `unbound`, `partial`, `rich`, and `full`, and the
  eleven ordered, de-duplicated reasons in the Session workspace specification.
  Ranks are `unbound < partial < rich < full`: calculate the base status from
  native ID, required lifecycle/input, and required content/terminal facts,
  then select the minimum of that base rank and every present reason maximum in
  the canonical reason-to-maximum-status table. Unknown reasons are invalid
  schema drift and are rejected rather than ignored. This selection rule makes
  overlapping reasons deterministic, including `unbound` plus `rich` and
  `partial` plus `rich`. Existing #51 unsupported-version and ingest-gap full
  blockers retain the table's `rich` maximum after the `partial` checks;
  `historical_summary_only` and `schema_drift_detected` are future
  adapter-handoff `partial` reasons with no distinct current calculator boolean.
  `historical_summary_only` never reaches `full`.
- The schema/manifests are repository-safe metadata. They contain no raw/PII
  and manifest grants no content authority. Existing raw/sanitized, loopback,
  same-origin, no-store, retention, and `--sanitized-only` policy remains the
  only content boundary.

The later adapter handoff checklist is: use a matching schema/manifest version;
declare observed rather than invented capability; reject unknown fields; emit
actual-adapter field provenance; apply authority/absence precedence; calculate
the fixed status/reason output deterministically; preserve raw/sanitized
boundaries; and propose a new major before a breaking change. This decision is
not an implementation authorization and does not change Issue #51 or #49.

正本は [telemetry ingestion](specifications/layers/telemetry-ingestion.md)、
[raw-store normalization](specifications/layers/raw-store-normalization.md)、
[Canvas Session workspace](specifications/interfaces/canvas-session-workspace.md)、
および [security data boundaries](specifications/security-data-boundaries.md)
とする。

## D057: Source compatibility is fingerprint-based and Claude hierarchy is exact-only

Status: Accepted

Issues #62-#65 implement the Issue #61 contract with the following boundaries:

- The immutable compatibility observation is recorded per committed ingest
  batch. Session-level schema state is derived and never becomes a second
  authority.
- Verified application versions are evidence labels, not a receive allowlist.
  An unverified version whose observed fingerprint matches a verified
  fingerprint is processed normally. A new fingerprint is retained and
  reported as `schema_drift_detected`; it is not silently dropped.
  `unsupported_source_version` is reserved for a known incompatibility or a
  missing required signal.
- Claude Code OTel owns source trace/span identity, parentage, and timing.
  Claude Hook events own native lifecycle and explicit event identity. Hook
  input cannot synthesize spans, duration, tokens, or hierarchy.
- Claude Session binding uses only identical native session ID, explicit
  resume/handoff, or byte-equivalent trace context. Repository, cwd,
  transcript path, process identity, and timestamp proximity are forbidden.
- Claude Agent ownership and UI hierarchy use exact source parentage only.
  Missing or ambiguous parentage remains unresolved; Issue #49 time-range
  inference is not applied to Claude records.
- If interactive CLI, `claude -p`, or Agent SDK live execution is unavailable,
  the exact blocker and a separate follow-up task are recorded. This does not
  replace fixture-backed implementation, security, migration, regression, or
  full-suite validation.
- D051 process readiness remains unchanged. Source compatibility is exposed by
  sanitized `GET /api/monitor/source-diagnostics`; it does not add a readiness
  check, reason, threshold, or status transition.
- OTLP structural inventory is captured before lossy normalization: directly
  from accepted JSON or while decoding the original protobuf wire message.
- A focused source-compatibility store owns observations, adapter failures,
  and diagnostic queries. A separate ingestion transaction coordinator commits
  raw record plus batch observation atomically; `RawTelemetryStore` and
  `IMonitorProjectionStore` do not gain source-specific diagnostic ownership.
- The Claude manifest registry label is
  `claude-code-otel+claude-code-hook`; actual stored provenance is only
  `claude-code-otel` or `claude-code-hook`.

The canonical field, state, storage, HTTP, UI, safety, and test contract is
[Source Schema Drift and Claude Code](specifications/interfaces/source-schema-drift-claude-code.md).

## D058: Guided setup は user-scoped ownership ledger と hash-guarded transaction に閉じる

Status: Accepted

Issues #66/#67 add reversible configuration setup without turning Local Monitor
into a general machine-management service. The same Config CLI implementation
serves repository mode and the self-contained Windows Release ZIP; PowerShell is
a thin argument/result wrapper.

決定事項:

- Public commands are `setup plan`, `setup apply`, `setup rollback`, and
  `setup status`. Plan persists a private immutable change set; apply and
  rollback require its UUIDv7 ID. Public output is the fixed repository-safe
  `setup.v1` JSON contract.
- The version-1 ownership ledger lives under the current user's Local Monitor
  runtime root: `%LOCALAPPDATA%` on Windows,
  `$HOME/Library/Application Support` on macOS, or absolute `XDG_DATA_HOME`
  with `$HOME/.local/share` fallback on Linux, followed by
  `CopilotAgentObservability/LocalMonitor/setup/`. This cross-platform private
  root lets macOS/Linux persist an inspectable plan before apply refuses its
  CLI target. The ledger stores fixed labels, timestamps, state/error codes,
  hashes, opaque backup references, and the immutable repository-safe plan-time
  target projection required by `status`. Exact values and paths are confined to
  private plans/backups/journals. Plans retain desired state but not previous
  values; exact previous state is captured only in apply-time backups. Version 1 is the first shipped schema; unknown versions
  fail closed and no synthetic v0 migration is invented. The complete ledger
  retains its 1 MiB cap; bounded snapshots add no second cap or automatic
  pruning, so finite history capacity is accepted. Private-plan
  `desired_state` is a closed v1 union, not a migration or fallback. The
  existing committed real ownership-ledger v1 fixture remains byte-identical
  and restart-readable as ledger evidence. Before serializer changes, task-04b
  captures a separate production-serializer private-plan v1 fixture containing
  the canonical legacy inline string and proves `SetupPlanStore`
  write-close-reopen byte identity. Inline remains canonical for historical
  bytes and generic non-tagged file/TOML/opaque targets; tagged
  `jsonc_owned_values_v1` is valid only for `SetupTargetKind.Json` records
  owned by `github-copilot` with the two VS Code Default Profile labels
  `vscode-stable-default-user-settings` and
  `vscode-insiders-default-user-settings`. Tagged string values are exactly
  1..2048 UTF-16 units and its expected state hash is lowercase. Unknown,
  malformed, or arm-mismatched
  union values fail `recovery_required`.
- One physical file or current-user environment allowlist is one ledger target
  with one base/applied hash and backup; setting changes are bounded members.
  Apply preflights every base hash and path before writing, flushes backups and
  a write-ahead journal, persists a flushed intent before each atomic file
  replacement or current-user environment member write, and compensates in
  reverse step order. Rollback uses the same pre-restore intent protocol. Every command recovers interrupted
  apply/rollback journals before normal work; unresolved recovery permits status
  only. Rollback is all-target hash guarded,
  one change set at a time, and has no force mode. Concurrency uses an exclusive
  non-waiting lock; tests use barriers/fault points rather than sleeps. A
  tagged JSONC target persists no full rendered document: bounded Plan-time
  rendering may hold complete bytes solely to derive operations/expected hash,
  then discards them before persistence; `SetupRevalidation` carries its
  complete desired bytes only under that lock. The coordinator validates exact
  record identity/cardinality/hash before it creates artifacts or writes.
  Ledger and journal retain hashes only. Recovery never calls the adapter or
  rematerializes JSONC; it uses expected/journal hashes and backups through
  every interruption window. No-op records add no materialization but retain
  their generic base-state guard.
- Apply verifies all desired file/member states again before commit. Every
  compensation or rollback restore reclassifies current state immediately
  before writing; a third-party state is preserved and makes the change set
  partial. Status reports target state and derives change-set state and rollback
  availability from all writable targets; guidance targets are not writable.
  Target status is lifecycle-relative through an explicit base/desired/previous/
  none reference. A third-party value preserved during a transaction is
  `diverged`, as is a safely classified aggregate target whose members mix
  desired and previous state. Classification failure is `unavailable` instead.
  Status rebuilds immutable detected/version/source/endpoint/manifest/guidance/
  redacted-member fields from the ledger snapshot, but freshly verifies
  reference/current/rollback facts from the lifecycle, private artifacts, and
  current target. A ledger-origin manifest is validated against the strict v1
  shape, closed codes, safety rules, and target/surface invariants without being
  compared to the currently embedded manifest; a newly produced plan must still
  match the current canonical manifest exactly. An all-`no-op` physical target
  grants no rollback ownership and needs no backup, but its fresh base-state
  check remains part of the change-set-wide rollback preflight. Drift in that
  unowned target therefore makes rollback unavailable, matching the rollback
  command's no-write `rollback_stale` behavior.
- Symlink/junction/reparse/path traversal, malformed structured configuration,
  machine-wide environment, `setx`, implicit administrator elevation, raw
  exception output, and DB/log/runtime-data deletion are excluded.
- The initial adapter ID is `github-copilot`. VS Code Stable and Insiders
  1.128+ write only documented Copilot OTel settings in each channel's Default
  Profile. Non-default profiles are never opened or edited and produce the
  fixed warning `vscode_non_default_profiles_not_modified`. Terminal Copilot
  CLI 1.0.4+ writes the exact bounded current-user OTel environment allowlist on
  Windows only. macOS/Linux detect and plan the CLI target, but apply returns
  `unsupported_target` without a shell-profile or target write. GitHub Copilot
  App/SDK is caller-managed guidance and performs no write. Current-process
  environment observation is a separate read-only platform interface; it never
  aliases the current-user persistent environment API or becomes a mutation
  target. VS Code `settings.json` reads are 1 MiB plus one sentinel byte in
  plan and revalidation; malformed/oversize input is `malformed_settings`.
- Copilot managed-settings channels use native > server > file precedence and
  the highest present channel wins wholesale without field merging. Its native
  sources are only Windows `HKEY_LOCAL_MACHINE\SOFTWARE\Policies\GitHubCopilot`
  and macOS `com.github.copilot`; Linux has no native channel. VS Code
  enterprise `CopilotOtel*` policies under `Software\Policies\Microsoft\VSCode`,
  macOS configuration profiles, and `/etc/vscode/policy.json` are a separate
  policy system. Both systems are read-only and resolved independently; an
  enterprise policy never suppresses Copilot server/file discovery. Any
  observed differing telemetry constraint blocks with
  `managed_policy_conflict`, while an equal constraint is managed/no-write.
  Signed-in-account server policy that an external CLI cannot prove is
  `managed_policy_unverified` even when an enterprise policy is observed;
  Copilot CLI uses environment-only detection and always reports the same
  warning.
- Content capture is preserved by default. Enabling it requires the independent
  `--include-content-capture` option, a separate member plan change, and a sensitive
  warning. Global `client.kind`, `OTEL_SERVICE_NAME`,
  `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_EXPORTER_OTLP_HEADERS`,
  `COPILOT_OTEL_SOURCE_NAME`, credentials, and unrelated resource attributes
  are not changed. Existing `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` is detect-only:
  exact `http/protobuf` is preserved with
  `cli_trace_protocol_override_not_modified`; any other value returns
  `environment_override_conflict` and no plan. It is never added to the write
  allowlist.
- Setup static verification does not prove telemetry receipt. First-trace
  diagnosis remains Issue #69. No HTTP, proxy, Canvas action, Razor UI, database,
  or AppHost resource is added.
- Public results separate the requested/created `change_set_id` from
  `recovered_change_set_id` and `recovery_operation`. Apply revalidates target
  OS support, version, VS Code Default Profile extension presence, managed
  state, exact logical members, and loopback endpoint ownership immediately
  before creating mutation artifacts. A changed version that remains supported
  is `recovery_required`, not an implicit update of the persisted contract.
  Applying a valid persisted plan after its
  adapter is removed from the registry is the allowed
  `apply`/`unsupported_adapter` result and leaves the existing plan/ledger
  unchanged. Status is bounded to 100 entries with
  recovery-blocking states prioritized, and may perform mandatory recovery
  before projection.
- Local Monitor recognition is exactly a no-redirect
  `GET <origin>/health/live` under one 500 ms total timeout and a 4096-byte body
  cap. The probe reads at most 4096 payload bytes plus one sentinel byte unless
  a trustworthy `Content-Length` already proves oversize. Only HTTP 200 and an
  exact JSON object containing solely string `status=live` is accepted.
  Refused/no-listener is `monitor_not_running`; every connect/read/total
  timeout, redirect, non-200, oversize, malformed/non-object, or different JSON
  response is `port_owned_by_foreign_process`.
- Environment notification is attempted after an uninterrupted final state.
  Recovery may replay it because exactly-once delivery cannot be proven across
  a process crash without an acknowledgement protocol.

The canonical interface is
[configuration setup](specifications/interfaces/configuration-setup.md), and
its repository-safe/private-data split is fixed in
[security data boundaries](specifications/security-data-boundaries.md).

## D059: Claude exact session binding is gated on its own evidence, not adapter promotion

Status: Accepted (2026-07-16)

Issue #108 asked whether the exact native-session-ID resolver should stay
gated on `source_adapter == claude-code-otel` promotion, which made it
unreachable for real traces because promotion never completes for the
currently drifted producer shape (Issue #99's open `any_value.int`-as-`double`
field question).

- The exact native-session-ID resolver is reachable on its own evidence alone:
  a single unambiguous `session.id` attribute on the OTel span whose UTF-8
  bytes equal exactly one persisted `claude-code` Hook native session ID with
  binding kind `Native`, `ExplicitResume`, or `ExplicitHandoff`. It no longer
  requires `claude-code-otel` adapter promotion; a span still labeled
  `raw-otlp` binds on this evidence.
- Adapter promotion continues to gate every other promoted `claude-code-otel`
  behavior, including the promoted `ProcessClaude` span classification
  semantics (D057, Issue #101). Only the exact-binding capability is
  decoupled.
- Binding never rewrites provenance: evidence stored from a non-promoted span
  keeps its actual source labels, and the raw `session.id` is still not
  persisted as a new native ID.
- Rationale: the drifted `any_value.int` field has no logical relationship to
  `session.id` byte-equality. Requiring promotion made a fully-specified,
  independently verifiable binding capability wait on an unrelated open
  question. The Issue #99 promotion decision remains open and unaffected by
  this decoupling.

The canonical field and gating contract is
[Claude Code exact binding](specifications/contracts/source-capabilities/v1/claude-code/exact-binding.md),
mirrored in
[Source Schema Drift and Claude Code](specifications/interfaces/source-schema-drift-claude-code.md).

## D060: First-trace Doctor は共有 domain と明示 verification に固定する

Status: Accepted (2026-07-16)

Issue #102 は GitHub Copilot / Claude Code ごとの診断実装を先に分岐させず、direct、
Config CLI、Local Monitor HTTP が共有する source-independent Doctor domain を
追加する。正本は
[First-Trace Doctor Interface](specifications/interfaces/first-trace-doctor.md)
とする。

決定事項:

- `DoctorFactSnapshot` の 12 個の explicit-known/unknown fact families、20 個の
  fixed state catalog、severity / retryability / next action、blocking precedence、
  terminal/advisory ordering、v1 reason-code equality、`DoctorResult`
  (`doctor.v1`) serialization/human projection を 1 つの shared Doctor domain が
  所有する。evaluator は pure であり、Config CLI と HTTP は同じ評価済み result を
  投影し、state selection や fallback interpretation を再実装しない。blocker が
  1つでもあれば blocker だけを precedence 順で返し、terminal/advisory は返さない。
  blocker がなければ terminal を1つ返した後に applicable advisory を固定順で返す。
  `partial_fact_snapshot` は `success=false`、non-null evaluation、null primary、
  empty states、nonempty ordered missing families に固定する。
- direct evaluation は source-neutral typed `DoctorObservation`
  (`real_source|synthetic_probe` と fixed evidence kind) を fact snapshot に含める。
  persisted `DoctorEvidenceCandidate` は別 carrier とし、verification complete caller は
  opaque reference だけを選択する。store/service が既存 unexpired candidate を解決して
  trusted observation を構築するため、caller は candidate class/kind/source を上書き
  できない。public `ObserveCandidate` route/command は追加しない。
- first-trace verification は server-generated lowercase UUIDv7、expected
  source/optional adapter、1..30 minute UTC expiry、revision、既存の bounded opaque
  evidence candidates を明示的に使う。complete/cancel は compare-and-swap transaction
  とし、evidence acceptance と completed transition は atomic にする。latest trace、
  latest Session、repository、workspace、cwd、trace ID 単独、timestamp proximity は
  candidate selection evidence ではない。source が opaque verification ID を運べない
  場合も、後続 slice は explicit user selection を使い、推測しない。
- synthetic probe は receiver / raw persistence / projection health のみを証明する。
  real-source receipt、exact Session binding、completeness/content を満たさず、
  synthetic-only selection は `first_trace_ready` にならない。
- Doctor は D051 `GET /health/ready` から独立した product diagnostic である。
  Doctor state、source compatibility、verification transition、Doctor schema/store
  failure によって readiness check/reason/threshold/config/body/status を変更せず、
  Local Monitor startup、ingestion、projection、stateless evaluation を失敗させない。
  Doctor store busy/unavailable は verification route/command だけを
  `doctor_store_busy` / `doctor_store_unavailable` に degrade する。
- Issues #103/#104 は shared fact snapshot と source-neutral evidence candidate
  (`real_source|synthetic_probe` と fixed evidence kind) の producer である。
  source-specific Doctor state/reason/severity/action enum を追加しない。proxy DTO、
  Razor、JavaScript、Canvas、UI とその live workflow は Issue #105 の所有であり、
  Issue #102 は facsimile を追加しない。
- D059 を維持する。exact source/session evidence と論理的に無関係な
  `schema_drift_detected` 単独では exact verification を失敗させない。
- Doctor v1 persistence は既存 SQLite 内の separate
  `schema_version(component='doctor', version=1)`、`doctor_verifications`、
  `doctor_verification_evidence` に閉じる。schema creation と terminal transition は
  transactional/restart-safe/idempotent とし、monitor/session component version を
  変更しない。migration failure は close/reopen 後に exact pre-Doctor schema/rows を
  復元し、新しい schema version を downgrade/fallback しない。

Consequences:

- Config CLI の five commands と Local Monitor の five `/api/doctor` routes は
  fixed bounds、exit/HTTP mapping、loopback/Host/same-origin/CSRF/no-store/sanitized
  boundary を共有する。
- public/storage output に raw telemetry、prompt/response/tool body、PII、credential、
  authorization value、absolute/local path、rejected body、exception text を含めない。
- source-specific live first-trace evidence と proxy/UI の検証は #103/#104/#105 の
  handoff とし、Issue #102 完了の証拠に見せかけない。
## D061: Claude guided setup owns bounded user settings and requires explicit WSL2 routing

Status: Accepted (2026-07-16)

Issue #68 extends the D058 transaction rather than adding another setup
service. The `claude-code` adapter uses the existing private plan, immutable
ledger projection, apply/rollback journal, stale guard, compensation, and
exclusive non-waiting lock.

- The public plan command selects `cli`, `app-sdk`, or `all`. CLI covers the
  interactive executable and `claude -p` because they share the same user
  settings. Agent SDK is no-write Python/TypeScript caller guidance.
- The writable boundary is the Claude user settings `env` object plus the
  approved mapper-compatible Hook entries. Unrelated JSON, comments, newline,
  Hook order, and non-owned Hook entries are preserved. An owned
  `hook-forward --source claude-code` entry that differs from the planned
  command/args/timeout is a no-write conflict; setup does not take ownership of
  another command.
- A new closed private-plan v1 arm,
  `claude_settings_owned_values_v1`, stores the expected complete-state hash,
  ordered owned env values, and event-specific command/args/timeout. It does
  not change the existing inline or `jsonc_owned_values_v1` bytes. Complete
  rendered settings are transient. Public DTOs, ledger, journal, logs, and
  repository-safe evidence contain no raw setting value, path, Hook command,
  credential, or token.
- Windows native may apply and roll back through the D058 file transaction.
  WSL2 requires a Linux process, `WSL_DISTRO_NAME`, a Microsoft kernel marker,
  explicit `--allow-wsl2-routing`, and successful loopback readiness from
  that process. Gateway discovery, non-loopback binding, Host-header
  relaxation, and NAT fallback are rejected. Windows native and other adapters
  reject the WSL option as `invalid_arguments`; native macOS/Linux installation
  remains outside Issue #68.
- Default planning enables Claude telemetry/export routing but preserves the
  three OTel content gates. Explicit content capture manages all three
  together. The approved default Hook set can itself observe raw prompt/tool
  events, so `claude_hooks_capture_raw_content` is always surfaced separately
  from the OTel content option.
- Static setup ends with process-restart guidance and does not emit
  `run_first_trace_doctor`. First real trace and Doctor integration remain
  Issue #104. No HTTP route, proxy DTO, UI, DB schema, remote collector,
  shell-profile mutation, or non-loopback exposure is added.

The complete command, settings, Hook, WSL2, storage, and evidence contract is
[configuration setup](specifications/interfaces/configuration-setup.md).

## D062: Claude changed setup apply hands off to first-trace Doctor

Status: Accepted (2026-07-18). This decision supersedes only the stale
`run_first_trace_doctor` emission sentence in D061; D061 remains the historical
decision for the Issue #68 guided-setup boundary.

The current Claude setup contract emits `restart_claude_process` followed by
`run_first_trace_doctor` after a successful changed CLI apply. The latter is a
handoff to `first-trace begin --adapter claude-code`; it is not telemetry
evidence and does not assert that a first real trace exists. Already-correct
no-op applies and rollback do not emit this changed-apply handoff.

Issue #104 owns the source-neutral first-trace orchestration, Claude fact
mapping, candidate observation, exact-binding firewall, deterministic matrix
evidence, and frozen feature-branch candidate. Issue #106 owns live Claude
producer execution, including the #110 check of whether `user_prompt` is
present when telemetry is enabled and `OTEL_LOG_USER_PROMPTS` is off. A #104
feature-branch closeout does not claim that live result or main integration.

## D063: Issue #90 user-controlled retention mutation uses the #89 catalog

Status: Accepted (2026-07-20)

Issue #90 adds one user-controlled mutation slice for `pin`, `unpin`, and
`delete_now` over the existing Issue #89 retention catalog. It does not create
a parallel lifecycle state machine, catalog, worker, queue entity, or physical
deletion path. The Local Monitor retention mutation application service owns
exact target resolution, deterministic preview, explicit confirmation,
idempotency, and append-only audit; the existing #89 worker owns physical
deletion.

- Session targets are restricted in v1 to the exact
  `session_event_content.source_item_id -> session_events.event_id` join whose
  persisted `session_id` equals the requested Session ID and passes the #89
  ownership proof. All other store kinds are item-target-only. Repository,
  workspace, trace, path, timestamp, prompt, proximity, and query matching
  never select a mutation target.
- `delete_now` supersedes a pinned item only through one preview and a bound
  explicit confirmation. The confirmed transaction clears the derived pin
  through the existing `retained_by_policy -> expiring` seam and then executes
  only the sequential #89 forward transitions to `deletion_queued`; it never
  introduces a new lifecycle edge or performs a separate unpin round trip.
- A consumed confirmation retry remains HTTP `409` with the exact one-property
  `retention_confirmation_consumed` body. When stored operation linkage exists,
  a same-origin relative `Location` header points to the versioned mutation
  status read; the response never reissues a token or embeds the stored result.
- The Canvas Session workspace may provide only a navigation link carrying the
  selected exact local Session ID to the Local Monitor retention page. Canvas
  adds no retention action, fetch, proxy, mutation state, or raw-bearing field.

The complete public contract is [retention mutation](specifications/interfaces/retention-mutation.md).

## D064: Cross-surface Doctor remains inside the existing diagnostics screen

Status: Accepted (2026-07-21)

Issue #105 integrates GitHub Copilot and Claude Code first-trace journeys
without adding a Local Monitor v1 primary destination. Doctor controls, exact
Session summary, and source-diagnostic targeting live in the focused
`/diagnostics` flow opened from Unified Settings. No independent Doctor or Session-detail
screen is added.

- The stable source registry is `github-copilot-vscode`,
  `github-copilot-cli`, `github-copilot-app-sdk`, and `claude-code`.
- Config CLI, Release ZIP, and the additive `/api/doctor/ui/v1` proxy project
  the same `FirstTraceEnvelope` and embedded `doctor.v1` result.
- Exact evidence navigation is a separate sanitized server projection. It may
  link to the existing trace screen or to exact Session/source-diagnostic
  sections in `/diagnostics`; it never parses or reverses an opaque evidence
  reference and never falls back to latest/time/repository/workspace matching.
- Existing `/api/doctor` routes, D051 readiness, raw routes, and same-origin/
  Host/CSRF boundaries remain unchanged.

The complete public and security contract is
[first-trace Doctor](specifications/interfaces/first-trace-doctor.md).

## D065: Instruction findings use a closed repository-safe receipt boundary

Status: Accepted (2026-07-22)

Issue #59 validates every model-submitted reference against the exact raw-local
evidence index before any receipt is created. Source Session, trace, and span
IDs are then replaced with kind-specific domain-separated opaque tokens; raw
IDs and model free text never enter the carrier. The taxonomy and safe text
templates are closed and versioned. Only a final `supported` finding is
candidate-eligible; `weak` and `incomplete` remain ineligible receipts, and an
empty finding/candidate handoff is valid. Successful analysis result and
`instruction-finding-handoff.v1` persistence are atomic. #72 and #73 consume
rather than redefine v1; the handoff grants no effect, apply, export, file, or
promotion authority. The canonical contract is
[instruction diagnosis analysis](specifications/interfaces/instruction-diagnosis-analysis.md).

Compatibility update (Issue #59, 2026-07-22): the unchanged v1 producer and
all downstream consumers use one source-neutral validation authority. Its
public surface accepts only canonical UTF-8 carriers at or below 1048576 bytes
and JSON depth 16, validates the closed schema plus every derived identity,
template, association, order, and reference token, and returns only the
positive analysis-run identity. It neither reads raw data nor grants capture,
export, effect, apply, or promotion authority. This additive API does not
change any v1 schema string, field, byte, ID, template, order, or persistence
schema; copied consumer-side hash/template validators are prohibited.

The validator establishes only canonical structure and semantic
self-consistency. Deterministic tokens/hashes/templates are not provenance or
authenticity evidence, so the caller still acquires bytes from a trusted
owner/store and only the producer pipeline may claim pre-tokenization raw
reference resolution. The 1048576-byte/depth-16 envelope, single-pass JSON
read, and finite reconstruction are the complete consumer work/cardinality
bound; the closed categories yield at most eight reconstructed candidates.
Frozen v1 has no per-draft/finding/evidence-ref ceiling and explicitly supports
duplicate collapse, so this compatibility repair intentionally leaves those
collections and producer draft admission unchanged rather than silently
rejecting previously reachable carriers.

The accepted byte golden is the unchanged owner serializer output (default
`System.Text.Json` non-ASCII escaping), pinned as SHA-256
`ede92634f2a3417e7cac1a8d841fa77dfbd34a2cb39cb9c85adb20a72ca08821` in an
immutable base64 wire resource. The older literal JSON fixture remains an
unchanged semantic/JSON-Schema example; its literal Japanese bytes and terminal
newline have SHA-256
`b6a632df8d2a33e743dcf359bad7ee522e6c549b1287af695b62a406fc150987`, were
not the accepted serializer output, and are noncanonical at the strict public
boundary. Tests may not conceal this distinction by
parse/reserializing the semantic fixture into expected bytes.

Issue #72 implementation note (2026-07-22): the frozen #59 validator and
nullable reference semantics are consumed by a separate bounded historical
evidence dataset. One coherent Session snapshot produces paired raw-local and
repository-safe canonical forms with exact evidence resolution,
insert-or-identical persistence, and independent checksums. This does not add
history discovery/import, export authority, LLM execution, proposals, effects,
or inferred Session relations. The canonical child contract is
[historical evidence extraction](specifications/interfaces/historical-evidence-extraction.md).

## D066: Historical source import is profile-bound, consented, and fail-closed

Status: Accepted (2026-07-22)

Issue #76 admits only product-owned versioned Tier A artifacts or Tier B
producer formats bound to exact fixture SHA, schema fingerprint, application
version, and golden tests. Tier C private/heuristic stores are excluded. The
current GitHub Copilot CLI and Claude Code profiles have empty support sets and
allowlists; detector evidence alone authorizes no content read or candidate.
A later #77/#78 exact profile promotion is a D056-compatible technical
revision. Production candidates contain only 1–10 actually observed
allowlisted leaves with exact ordered provenance; missing values are never
synthesized. #79 rejects repository fixture markers and zero eligible
candidates, preserves partial / `historical_summary_only`, exact merge, and
#89/#90 retention boundaries. The canonical policy is
[historical source import](specifications/interfaces/historical-source-import.md).

Issue #79 implementation clarification (2026-07-23): the strict producer
preview v1 remains unchanged, while a separate
`historical-import-workflow/v1` family owns explicit preview, confirmation,
source/database stale revalidation, idempotent transaction, result/history,
and observation reads across Local Monitor API/UI and Config CLI. Current real
profiles remain non-actionable. Positive production flow is reachable only
through a typed post-admission seam created after exact profile/snapshot
validation; HTTP/CLI payloads and repository fixtures cannot construct it.
Accepted metadata is stored in a dedicated `historical_import` schema as a
distinct observation/receipt, never as a synthesized Session/Run/Event/trace/
timestamp. An exact binding may add only a relationship and navigation target
to an existing Session. Date and new Session/Event counts remain explicitly
unavailable without authoritative evidence. Metadata-only creates no retention
item, and content remains blocked in workflow v1 pending an existing
`session_event_content`/#89 mapping. Unified Settings diagnostics/integration
links explicitly to the dedicated import page,
whose live and historical tabs use separate read models and never union
identity. This supersedes only the pre-#79 seven-screen count in D064; D064's
Doctor-in-diagnostics ownership remains while D075 owns current navigation.

## D067: Alert evaluation uses capability-gated deterministic receipts

Status: Accepted (2026-07-22)

Issue #80 evaluates versioned source-neutral snapshots with a compiled rule
registry. Missing, unknown, or unavailable capability is an explicit bounded
suppression, never an inferred zero. Accepted matches become immutable
canonical `alert.receipt.v1` values carrying exact evidence and config/input/
evaluation hashes; sensitive comparable labels use private keyed HMAC tokens.
The `alert_engine` SQLite component owns only its schema-v1 evaluation,
receipt, and suppression tables. #81/#82 add rules through the frozen registry,
#83 owns a separate lifecycle component, #84 owns Alert Center reads/UI/
aggregation, and #85 reads canonical receipts for sanitized export; none may
rewrite engine receipt bytes or tables. The canonical contracts are
[alert rule engine](specifications/interfaces/alert-rule-engine.md) and
[alert lifecycle](specifications/interfaces/alert-lifecycle.md).

The additive consumer-compatibility repair keeps those producer/store bytes and
schema v1 unchanged while making `AlertReceiptConsumerV1` the single strict
public byte-validation boundary. It uses consumer-owned semantic invariants,
byte-compares against the canonical serializer, recomputes only the derivable
alert ID through the behavior-identical engine helper, returns a five-field
bounded identity projection, and maps every rejection to one no-leak failure.
Its 8 MiB and exact-shape depth ceilings apply only to consumers/exporters;
larger persisted receipts remain byte-compatible but are unavailable to this
consumer and require a named future consumer/profile revision rather than
truncation or permissive fallback. Evaluation/input/config hashes cannot be
recomputed from receipt-only bytes and are shape-checked only; success proves no
origin, signature, authorization, store provenance, or historical evidence
resolution. It also cannot bind summary/threshold/capability/source/completeness
claims to the absent registry, configuration, and snapshot; a self-consistent
fabricated receipt can recompute its alert ID. Trusted store acquisition and
downstream scanning remain separate. Existing serializer/evaluator/store
admission remains unchanged, including the unchanged serializer-only golden
with its fabricated alert ID.

The Wave 3 Alert Center compatibility repair is also additive. One
source-neutral application object owns a construction-time registry,
configuration, evidence resolver, and `IAlertEngineStore`; it accepts only an
already-normalized snapshot and returns an immutable typed success outcome with
ordered receipt IDs, suppression facts, and rejected-match facts only after the
exact completed evaluation is appended. Initialization busy/unavailable, append
busy/unavailable/conflict, and contract rejection remain distinguishable and
no source mapping, background analysis, lifecycle mutation, or evidence
inference enters this boundary. `IAlertEngineQueryStore` reads the unchanged
schema-v1 engine tables through 1..100 cursor pages ordered by alert ID,
evaluation ID, or suppression ordinal. SQLite receipt enumeration reuses the
existing strict receipt authority before returning exact bytes with a new
sealed fully typed #80-owned Alert Center projection; the existing five-field
consumer API is unchanged. It fails the whole page closed on invalid bytes.
This trusted local acquisition does not add
signing, authentication, or provenance attestation, and #84 receives no direct
SQL authority.

Issue #84 consumes this authority without creating a parallel parser or state
store. Evaluation occurs only after an explicit same-origin/CSRF request names
one exact Session and trace; every monitor-span raw-record owner must resolve to
one agreeing #61 source/application-version observation. Generic projection
rows bind the deterministic input as unknown-status evidence but cannot promote
semantic rule capabilities. Reads use bounded typed #80 projections plus #83
lifecycle, and evidence navigation requires persisted row identity together
with the exact Session/trace/span/time/source tuple. Ingestion, startup, GET,
navigation, and browser code never evaluate alerts. The canonical behavior is
[Alert Center](specifications/interfaces/alert-center.md).

## D068: Sanitized export separates trusted capture from structural inspection

Status: Accepted (2026-07-22)

Issue #85 accepts only a strict control request and acquires one trusted,
read-only SQLite snapshot internally. It selects bounded #58 safe projections
and exact #59/#80 canonical carriers through their owner validators, resolves
dependencies before serialization, then creates deterministic manifest/member
bytes, checksums, and archive bytes. A bounded fail-closed scanner runs before
publication; any capture, validation, scan, serialization, archive, or atomic
publish failure produces no partial-success artifact.

Bundle `result` inspection is deliberately independent from snapshot capture.
It proves the frozen v1 archive shape, canonical members, dependency inventory,
checksums, and scanner result, but without signing it does not prove producer
origin, store provenance, or authorization. The request cannot supply snapshot
or carrier bytes, a safety marker, or a server output path. V1 exports only the
closed #58/#59/#80 profiles; #72 datasets and #83 lifecycle events require a
named future profile. The interface is
[sanitized evidence export](specifications/interfaces/sanitized-evidence-export.md).

## D069: Raw replay is explicit, isolated, deterministic, and retention-owned

Status: Accepted (2026-07-23)

Issue #87 uses a separate `raw-local-replay` profile rather than extending the
repository-safe sanitized bundle. Export and replay each require a persistent
raw-data warning, exact preview binding, and fixed confirmation phrase. Exact
Session/trace/raw-record/source/time selection is materialized all-or-none under
one Retention operation lease; original identities, timestamps, and observed
source/adapter/schema/content provenance are preserved without repository,
workspace, path, time-proximity, prompt, or similarity inference.

Replay validates the complete deterministic archive, pins normalization,
projection, and dashboard target versions, and binds observed adapter/schema
version evidence to the exact members before publishing an isolated namespace.
The namespace reuses the existing `sensitive_bundle` item,
`sensitive-bundle-7d` policy, reserve-to-complete capture journal, operation
leases, queue, cleanup adapter, retry, and recovery. It adds no store kind,
migration, worker, or deletion path; caller archives are not cleanup targets.
Replay never mutates or merges into live raw, Session, projection, analysis, or
evidence stores and performs zero external-model calls. Same replay ID plus the
same archive/options/versions is idempotent; differing input is a conflict.
`--sanitized-only` rejects the whole surface before raw access. The canonical
interface is [raw local replay](specifications/interfaces/raw-local-replay.md).

## D070: Sanitized import is exact, component-owned, and transactional

Status: Accepted (2026-07-23)

Issue #86 imports only the frozen Issue #85 v1 archive after the exact #85
strict inspection authority succeeds. It uses exact record identity and exact
canonical bytes for deduplication, rejects same-ID/different-bytes conflicts,
and preserves source IDs in a deterministic evidence graph without repository,
workspace, timestamp, text, or proximity inference. Preview is bound to one
private archive-byte snapshot and current imported-record state; commit
revalidates it inside one transaction that includes schema-component setup and
writes records, origins, graph, and history all-or-nothing. Every failure rolls
back component/version mutation too. The archive also passes strict preflight
before database access, with actual member CRC32 recomputation and strict
round-tripping UTF-8 filename bytes. #59 opaque
references and #80 full evidence tuples use separate carrier-specific identity
domains; neither bare child IDs nor cross-carrier namespace reuse is accepted.
Global exact-definition state is separate from immutable per-import
missing/external declarations and edge resolution. Replay success requires an
exact completeness check. Same-archive preview and every prior owner receipt
consulted for duplicate/conflict/definition/resolution/promotion receive the
same integrity treatment; corrupt graph is never repaired or adopted. Public
preview reports manifest declarations separately from current destination
unresolved state, and counts distinguish new, updated, skipped, rejected,
duplicate, conflict, and graph-state-update outcomes.

The data belongs to an independent `sanitized_import` schema component v1.
Using Session v14 was rejected because the frozen carriers do not create or
change Session identity, and a Session bump would break #85's monitor v8 /
Session v13 capture anchor. Imported rows are retained sanitized outputs, not
raw store kinds, create no retention catalog items, and are not Issue #90
mutation targets. Raw replay, backup restore, heuristic conflict resolution,
origin attestation, and new carriers remain separate concerns. The interface
is [sanitized evidence import](specifications/interfaces/sanitized-evidence-import.md).

PO124-A supersedes only D070's current destination/capture-version pin: the
runtime and #85 anchor are current Monitor v11 / Session v14. The ownership
decision remains intact: `sanitized_import` stays component v1, preserves
`monitor_schema=8` and `session_schema=13` when they are immutable metadata
from an older valid source bundle, and owns no Monitor/Session migration,
Session row/fact validation, mutation, or carrier.

## D071: Runtime restore uses a strict SQLite restore unit and authoritative tombstone reconciliation

Status: Accepted (2026-07-23)

Issue #88 defines `local-runtime-backup` as a raw-bearing private profile,
separate from sanitized evidence export. A live database is captured only by
SQLite's online backup API. One canonical manifest and the closed snapshot are
the only stored ZIP members; strict byte-level layout, checksums, SQLite
integrity, schema vectors, and retention summaries are validated before atomic
publication or extraction.

SQLite is the v1 restore unit. Product-owned state outside it is never silently
assumed: active external raw stores and proposal-apply private files block a DB-
only backup, setup ownership is reported as a host-bound prerequisite, and
ephemeral runtime state is excluded. Operator-selected backup files remain
outside #89 cleanup and always carry `retention_backup_not_purged`; no sixth
Retention kind or backup inventory/purge claim is created.

Restore is offline CLI only. It read-only preflights every component/version
before invoking a production migrator, stages and migrates a candidate,
reconciles current tombstones/read denial plus exact raw-source removal and
audit state, creates a pre-restore backup by default, atomically replaces the
target, revalidates readiness and Doctor storage, and restores the exact old
database on failure. Confirmation can never drop a current tombstone and
applies only to a bound non-terminal missing-source reintroduction. Restore
never resets capture, policy, expiry, deletion, or TTL timestamps.
`runtime_backup` component v1 stores only sanitized path-free receipts. The
receipt DDL and row validator require canonical UUIDv7/UTC/SHA types, bounded
counters, and backup/restore cross-field consistency. Raw snapshot, partial,
and inspection transients use a separate fixed-content path-free owner marker;
bounded recovery deletes only its exact nonce-derived sibling and leaves
unmarked/malformed/active owners untouched. The restore crash journal remains
the sole owner of restore staging outside the database being swapped. The canonical
interface is
[runtime backup and restore](specifications/interfaces/runtime-backup-restore.md).

## D072: Codex App Desktop production integration is NO-GO

Status: Accepted (2026-07-24)

Issue #92 is `NO-GO` for Codex App Desktop production integration. The
validated tuple contains Desktop package `26.715.10079.0` and Codex
CLI/app-server `0.145.0`, but those versions were detected independently. The
Desktop-bundled producer binary was present, yet direct terminal execution was
blocked by WindowsApps access control. The live producer was instead a
standalone app-server driven through its public protocol. That successful
standalone run does not replace the blocked Desktop-owned retry, and no
evidence proved that the Desktop package owned its process, Session, window,
thread, or turn.

A content-disabled probe used per-command overrides only and a disposable
loopback receiver. It observed OTLP JSON traces with source trace/span IDs,
parent fields, and timing. Producer version `0.145.0` came from the public CLI
version command; the observed `service.version` key value was not retained.
Parent references outside the
exported batch remain unresolved. A native protocol thread ID returned by
`thread/start` was absent from the matching OTel span, and generic
instrumentation `thread.id` is not accepted as the native Codex thread ID.
Therefore the standalone attestation has exact OTel trace-to-span identity and
an observed unbound native thread relation, but no authority for a `codex-app`
adapter. Native turn correlation is `unverified` because no turn ran.
Repository, workspace, cwd, process, timestamp, prompt, and arrival-order joins
remain forbidden.

A safe read-only OS process-tree diagnostic projected only process IDs, parent
IDs, and executable paths and observed a package-root `codex.exe` process whose
OS parent executable was also under that package root. It emitted no ID, path,
or hash value and did not read command lines. It did not identify the child role
or prove app-server identity, Desktop-owned OTel execution, an App
Session/window relationship, or merge authority. An earlier command-line-
reading attempt was invalidated and excluded; none of its values were retained.

The v1 manifest cannot label a capability as standalone-only while excluding
Desktop ownership. It therefore promotes only the independently observed
source-version detector; trace and all Desktop-specific capabilities remain
`unknown`. The standalone structural inventory is retained separately for a
future approved discovery retry only. A manifest capability grants no content
authority. Official Advanced Configuration and the pinned `rust-v0.145.0`
implementation show tool-result log attributes for arguments, output, and error
text even when prompt logging is disabled. Those values can be content- or
path-bearing, so `log_user_prompt = false` alone is not a repository-safe log
profile. Content-enabled capture was not
authorized and no raw payload, identifier value, resource-attribute value,
private App state, or machine path is committed.

Several existing Codex App full-routing, Langfuse, and Collector configuration
samples enable log export. Tool-result logs may include content- or path-bearing arguments,
output, and error text even when prompt logging is disabled, so these generated log-export profiles are not
established as repository-safe. Issue #92 does not silently change production
samples. Safe default redesign or an explicitly non-content log mechanism,
exact detection, and separate authorization for content-bearing profiles are a
high-severity prerequisite blocker.

Issue #92 implements no adapter, Setup, Doctor, or UI. Issue #93 production
adapter, Setup, Doctor, UI, trace-manifest promotion, and future-registry
activation remain blocked and must not start from this attestation. A separately
approved discovery retry and prerequisite configuration specification must
first establish Desktop-owned execution, a retained repository-safe replay
harness, exact configuration/detection, safe log policy, source
identity/parentage, and exact-or-explicitly-unbound native correlation.
Codex CLI and generic standalone app-server remain out of scope.
The future-surface registry remains `not_available`; activation requires a
later production adapter and executable tests only after those prerequisites
have been satisfied.

## D073: Pricing is an exact effective-dated domain, not a dashboard lookup

Status: Accepted (2026-07-24)

Issue #94 introduces a standalone `CopilotAgentObservability.Pricing` domain.
The registry is versioned, effective-dated, explicitly currency-bearing, and backed by
reviewed source references. Lookup requires an exact provider, billing mode,
canonical model ID or declared exact alias, exact pricing route, and
session-effective timestamp.
Case folding, trimming, fuzzy matching, inferred plan/contract, runtime
scraping, and currency conversion are rejected because they can turn unknown
billing evidence into a false monetary statement.
Registry v1 deliberately accepts only USD with two minor units; supporting a
different currency profile requires a later contract version. Canonical
estimate reload uses the pricing-owned strict consumer, including identity and
exact-catalog byte recalculation, rather than a persistence-owned parser.
The exact catalog is exported as canonical `pricing.catalog-snapshot.v1`
bytes in bundled-first, caller-ordered override/document/entry order; no sorting
occurs. Its SHA-256 is bound into every estimate identity, and the strict
snapshot consumer is the only reload authority. A later persistence owner must
retain those exact bytes and cannot reconstruct them or substitute the current
catalog.
Because the hash is publicly recomputable, it is not an authenticity boundary.

Catalog production and consumption share the ceiling of 64 ordered documents
and 4 MiB of canonical snapshot bytes; canonical estimate production and
consumption share the 1 MiB ceiling. Both strict consumers enforce depth 32.
Public source references are at most 4,096 UTF-16 code units and use well-formed
exact lowercase `https://` syntax with fixed lexical, percent-decoding, host,
and credential-shape rejection. All admitted strings are well-formed UTF-16,
and request collections are snapshotted once before validation so caller
mutation cannot change the calculation after admission.

Bundled revisions and separately labeled local overrides are append-only
records. A new non-overlapping exact tuple may be appended without a
predecessor; overlapping applicable entries require an explicit supersession
key and fail registry construction without a unique supersession path.
Recalculation creates a new deterministic `pricing.estimate.v1` record and may
name the prior record; it never updates that prior record in place. Amounts are
computed as independent decimal components with no intermediate rounding.
Missing categories stay missing and produce `partial` or `not-estimable`;
explicit zero usage remains distinct from missing usage. Zero incremental cost
is allowed only by an exact included-plan registry rule.
Rates, multipliers, and fractional credit quantities have normalized scale at
most six, but static magnitude bounds do not imply every product is
representable. Each component and the aggregate are checked exactly and fail
closed rather than round when System.Decimal cannot represent the result.

This domain has no SQLite component in #94. It neither changes the frozen Issue
#80 receipt/lifecycle schemas nor authorizes #95 UI, budget alerts,
notifications, invoice reconciliation, enterprise price inference, purchases,
quality claims, or effect verdicts. The existing `sprint4-m2-v1` static
dashboard unit-price calculator remains a legacy compatibility surface and is
not promoted to canonical pricing authority.

## D074: Cost analytics persists exact history and extends the existing alert stack

Status: Accepted (2026-07-24)

D074 supersedes only D067's schema-v1-only, no-migration, and v1-only-query
ceiling for the additive #95/#80 v2 path. Every D067 v1 byte, identity, golden,
method, query, route, and lifecycle semantic remains accepted and unchanged.

Issue #95 owns one local metadata-only consumer of the frozen Issue #94 domain.
It persists the exact catalog snapshot and estimate canonical bytes in a new
`pricing` SQLite component v1 whose durable business history is append-only,
and reloads them only through the
#94 strict consumers. A public hash proves integrity, not authenticity, so
catalogs and positive requests enter only through trusted in-process providers.
The sole owner-delete exception is a bounded transient configuration-preview
receipt, removed only at its fixed 15-minute expiry or successful consumption;
catalog/configuration/commit/recalculation/estimate/head history is never
deleted or rewritten.
The optional repeated startup-only `--pricing-registry-override` admits at most
eight identity-bound local regular documents after the bundled registry. The
provider reads once with no-follow/reparse/TOCTOU/path-leak guards; no HTTP
upload, watcher, network fetch, persisted locator, or permissive fallback
exists.
The current #61 manifests do not authorize all required provider/model/mode/
route/quantity facts; the default production adapter therefore remains
unavailable. Synthetic positive adapters are test evidence, not live support.

Configuration, recalculation, and active selection are immutable ledgers.
Recalculation binds exact Session, configuration, catalog, and captured head
identities, appends a new estimate with its exact predecessor, and never
overwrites history. The active estimate is the highest contiguous explicit head
revision, not a current catalog or maximum timestamp. Aggregation uses active
heads only, keeps currencies separate, counts only fully estimated amounts in
the total, exposes partial known components only as a provisional subtotal
with reasons and no lower-bound claim, and retains every
eligible partial/not-estimable/missing/failed/unavailable/stale Session in the
coverage denominator. Explicit estimated zero remains covered.

Daily and period budget receipts cannot fit the one-Session v1 alert carrier.
Issue #80 therefore owns additive alert snapshot/configuration/evaluation/
receipt/canonical-json v2 contracts and an `alert_engine` schema-v2 migration
inside the existing evaluator/store. V1 types, bytes, hashes, goldens, store
methods, queries, and routes remain unchanged. Issue #83 accepts the engine-v2
parent while keeping lifecycle v1. Issue #84 owns a version-aware v2 read/API/UI
projection; aggregate cost receipts are not fed into the existing recurrence
grouping. Issue #85 keeps sanitized export v1 frozen and selects receipt-v1 rows
only. No second evaluator, store, lifecycle, or receipt parser is created.

Issue #88 appends `pricing` after `runtime_backup`, preserving the accepted
#79 -> #86 -> #88 migration subsequence. The complete tail is
`historical_instruction_analysis -> historical_import -> sanitized_import ->
runtime_backup -> pricing`. Whole-database backup/restore validates and carries
pricing privately; no archive member, Retention kind, or raw store is added.

The Local Monitor exposes bounded no-store cost projections and explicit
same-origin/CSRF-protected configuration/recalculation actions at `/costs` and
`/api/costs/v1/*`. It does not accept canonical bytes or local overrides over
HTTP. Under D075 this remains a focused detail flow opened contextually from
Unified Settings, not a permanent primary destination. The legacy
`sprint4-m2-v1` dashboard cost fields remain frozen and non-authoritative rather
than receiving an implicit projection.

Estimated cost is not invoice reconciliation, billing/chargeback, currency
conversion, purchase/quota mutation, notification, quality improvement,
verified effect, or automatic model recommendation. Content-enabled capture
remains separately unauthorized.

## D075: Local Monitor v1 is Repository-first, AI-independent at core, and receiver-only when sanitized

Status: Accepted (2026-07-30)

Local Monitor v1 is organized around Repository selection, Session Explorer,
Session detail and deterministic two-cohort Repository Session Compare. It has
no permanent sidebar and no generic aggregate/KPI dashboard. Breadcrumbs and
contextual search own normal navigation; receiver state and Settings open one
Unified Settings modal. The route, page, state, dimensions and binding
terminology authority is
`docs/specifications/interfaces/local-monitor-v1-ia.md`.

The core works without an LLM, provider authentication or API key. Compare is
fully deterministic and delegates all formula/snapshot semantics to #165; it
has no quality-evidence section, score, ranking, anomaly judgement or
improvement/effect verdict. Optional v1 AI uses GitHub Copilot SDK only after an
explicit user action and delegates snapshot/tool/storage/history semantics to
#162. Whole-Session reports alone have durable immutable history. Node,
Repository-selection and Compare results are transient or bounded operational
state, and follow-up chat is not persisted.

Repository and Session archive are reversible local visibility/selection
metadata. Archive is neither deletion, retention nor pin; it does not cascade,
extend retention or auto-restore on new ingest. Complex backup, restore,
retention, diagnostics and historical-import operations remain focused detail
flows reachable from Unified Settings rather than permanent navigation.

`--sanitized-only` is receiver-only under #159. In that posture the host keeps
ingestion, health and accepted frozen machine APIs but does not register Razor
Pages, human static assets or `/api/local-monitor/v1/*`; there is no
per-screen metadata-only fallback. Raw-default human surfaces are loopback,
Host-validated, same-origin, no-store, retention-authorized, bounded and inert
text. Provider egress is a separate explicit boundary.

The raw-local closed set includes authorized instruction labels/content; Tool
input/result/error; exact Sub-agent input; historical Skill body/path and the
separately validated current file; canonical Repository locator management;
Session AI reports and transient AI results; and exact raw technical detail.
The complete closed enumeration is
`docs/specifications/security-data-boundaries.md`.

D075 supersedes D042 as current product IA and supersedes D023's metadata-only
human UI. It narrows D032 and D045 to frozen installed technical contracts.
Existing `/api/monitor/*`, `/api/session-workspace/*` v1, SSE, Canvas
stores/behavior, raw ingestion, export/import, replay, backup, retention and
technical evidence routes remain unchanged. Historical records are retained;
they do not regain current product authority. Sentence-level Japanese copy is
deferred to #169.

## D076: Source observations stay immutable and Skill claims use fenced generations

Status: Accepted (2026-07-31)

Issue #154 adopts DC154-01. A trace-scoped source-version base observation is an
immutable capture fact. It is neither updated nor neutralized by an unrelated
later observation. Effective aggregation remains fail-closed: conflicting or
multiple exact tokens, then unrecognised, then all-resolved with one token,
then missing. Consequently `missing + resolved = missing`.

Only the internal `SourceCompatibilityReconciler` may append an interpretation
revision for the exact `(source_observation_id, trace_id)`. Decoder revision may
recover an exact version from the same retained bytes and is the only way to
resolve missing. Registry revision may recognise an already retained exact
token. There is no HTTP/manual repair surface, version-entry fallback or
generic repository mutation. Base and ledger rows are protected from direct
update/delete and parent-cascade deletion.

One SQLite transaction appends a meaningful revision, moves its exact head,
increments the trace compatibility revision, invalidates the current OTel Skill
claim, persists one exact ordered input frontier/generation/queue row and saves
the idempotency receipt. Ordinary validated OTel ingestion uses the same
transaction-aware generation participant. The worker processes only that
frontier under renewable exact Retention operation leases and publishes only
after compatibility, resolved state, desired generation, queue lease,
Retention leases, frontier and projector version still match. Raw expiry
produces `input_unavailable`, never a shortened or empty successful projection.
The source-compatibility ledger/head/revision/receipt and immutability guards
advance exact Monitor v10 to v11. `skill_projection:1` is independent and does
not duplicate the current trace revision authority.

Skill claims are owned by the independent `skill_projection:1` component and
one current read service. Its closed source-arm seam is
`otel_trace_span | sdk_session_event`. OTel alone uses trace
SourceCompatibility generations. The SDK arm is a non-generation-bound exact
Session/Event claim that carries local Session/Event identity, producer Event
identity, source adapter/surface/application version, adapter/normalization
versions, payload schema/fingerprint/digest and nullable producer trace/span.
The current registry accepts the complete exact tuple; trace/span are not
required for SDK claim validity. Arms merge only when producer trace ID and span
ID both exactly match. Otherwise same-Session positive observations are not
added: count is `null` and state is `certification_pending`.
#157/#158 own the SDK transport/snapshot writer and accepted registry seed, may
not create a second projection authority, and remain blocked until those
contracts are fixed. A raw snapshot cannot resurrect a stale claim, and
name/path/time/cardinality cannot link the arms.

The pre-release Skill projection has no compatibility or backfill obligation.
The recognized transition drops obsolete Skill-owned tables/markers, creates
one empty current component, and removes the old reader/writer. It preserves
unrelated Session, raw, source-observation, span and Retention data. Older
supported backups may initialize an empty component; partial/newer/unknown
intermediate state fails closed. Full contracts are
`docs/specifications/layers/source-compatibility-reconciliation.md` and
`docs/specifications/layers/skill-projection.md`.

This decision does not implement or resolve Issue #152 and does not modify
frozen `/api/monitor/*`, `/api/session-workspace/*` v1 or SSE bytes.

## D077: Repository catalog and assignment use one exact gated authority

Status: Accepted (2026-07-31)

Local Monitor v1 adopts
`docs/specifications/interfaces/local-repository-catalog.md` as the canonical
Repository identity, GitHub locator, observation provenance, Session
assignment, mutation, scope and backup contract. Exact opaque identities and
the accepted locator grammar replace every name/path/CWD/prompt/time/
cardinality heuristic. V1 observes only `vcs.repository.url.full` and
`copilot_chat.repo.remote_url`; Issue #152 unknown attribute-key drift remains
unresolved.

The independent future component is `local_repository_catalog:1`. It keeps
immutable locator rows and movable heads, exact observation provenance, manual
overrides and assignment revisions, append-only history and durable exact-byte
idempotency receipts. Catalog-owned canonical locator, fingerprint, display
casing, safe label and bounded provenance survive source raw expiry without
reconstructing source raw. The component is excluded from sanitized evidence
export/import.

#134 is the sole HTTP owner of
`GET /api/local-monitor/v1/repositories`. #156 owns the catalog/assignment core
and only its five management/action routes after their gates. #161 composes
archive eligibility and #134 consumes the result through the same
`ILocalRepositoryScopeSnapshotService`; neither adds direct catalog SQL or a
second reader. Archive meaning remains #160/#161-owned.

D081 clarifies and supersedes only D077's statement that #161 composes
archive eligibility. #161 supplies direct Session and full-catalog Repository
archive state/revision facts; #156 validates both complete exact sets and alone
composes effective archive eligibility and its scalar reason from those facts
plus exact current assignment. #134 continues to consume the one completed
`ILocalRepositoryScopeSnapshotService` result; #161 and #134 add no catalog SQL
or second reader. Archive meaning remains #160/#161-owned.

The complete DC156-01–19 contract is `READY_FOR_IMPLEMENTATION` under
[Local Repository Catalog and Session Assignment](specifications/interfaces/local-repository-catalog.md)
and its [DC156-12–19 executable closure](specifications/interfaces/local-repository-catalog-executable.md).
Implementations consume those authorities without inventing intermediate
tables, history actions, wire carriers, compatibility paths, fallback readers
or permissive parsers.

The future backup dependency order places `local_repository_catalog`
immediately after Session and before `local_archive`, Retention, Skill
projection/snapshot and Workspace projection. Older component-absent state may
initialize empty; partial, newer or unknown state fails closed. Frozen
`/api/monitor/*`, `/api/session-workspace/*` v1 and SSE bytes remain unchanged,
and the human routes remain raw-default-only.

## D078: Skill invocation snapshot foundation is accepted while production v2 stays blocked

Status: Accepted foundation / production v2 blocked (2026-07-31)

Issues #119, #157 and #158 adopt DC158-01 through DC158-11 through
`docs/specifications/interfaces/skill-invocation-snapshot.md`.
The frozen `POST /api/session-ingest/v1/events` supported set contains
`skill.started` and `skill.completed`; `skill.invoked` is unsupported and uses
the existing unsupported-event path. This corrects only event membership. It
does not change the v1 route, header/body version, envelope/event shape,
adapter/surface enum, 1 MiB body or 1..100 batch limit, content-state
vocabulary, validation/status/error entity bytes, queue/commit/`204` behavior,
or `/api/session-workspace/*` response bytes.

The accepted additive direction is raw-default-only
`POST /api/session-ingest/v2/events` for exactly one SDK `skill.invoked` Event,
the independently versioned `skill_invocation_snapshot:1` component,
historical body/path reads, an explicit current-file POST using configuration
and discovery authority, and backup/raw-local/sanitized composition. Snapshot
metadata and equality receipts do not copy historical body/path bytes already
owned by Session Event content. The writer is one atomic seven-authority
transaction; partial Event, content, Retention, snapshot, claim, receipt or
invalid-claim state cannot remain.

The repeatable startup CLI options `--skill-discovery-project-path` (0..16) and
`--skill-discovery-directory` (0..32) are the sole discovery-root authority.
`ServerSkillsApi.DiscoverAsync` receives only those validated roots. Historical
path/CWD, Repository locator, prompt, workspace label, timestamp and out-of-root
results never create a root, and the service never opens the historical path
directly. Only an accepted discovery result may enter the platform no-follow
handle walk; exact name/path comparison and filesystem-identity proof stay in
the decision gate.

Issue #154 remains the sole current-valid Skill claim authority. SDK claim validity
uses exact Session/Event/source identity and the complete current-registry
compatibility tuple without requiring trace/span. OTel and SDK arms merge only
when producer trace ID and span ID both match exactly. Trace-only, name, path,
time, cardinality, Session co-membership and discovery output do not link
claims. A raw snapshot cannot create or resurrect a stale/invalid claim, and
OTel-only `not_captured` produces no snapshot row.

Production v2 parsing/persistence, `skill_invocation_snapshot:1` migration,
raw-local routes and host registration remain `BLOCKED_DECISION` until the
canonical interface fixes all of:

1. exact outer envelope/event property inventory, order, nullability and
   SDK/local/provenance mapping;
2. complete validation/status/error contract, exact entity/media/`405` bytes
   and insert-or-identical conflict behavior;
3. checked-in producer schema bytes, fingerprint domain/value, registry seed
   and revision behavior;
4. equality receipt key/framing/input/result byte domains and conflicts;
5. payload digest/size and Session content/body/path storage byte domains plus
   backup validation;
6. total multi-fault precedence, nullable matrix, path/name rules,
   projection-validity mapping and historical-read errors;
7. success schema literals/presence, discovery root framing/hash,
   current-file media/parameters and method precedence; and
8. normalized historical name/path-to-discovery comparison, root/relative
   handle-walker identity, persistence choice and Windows/Unix file identity
   proof.

No blocked value is inferred from the SDK, runtime reflection, v1 DTOs,
serializer defaults, encounter order, secret-filtered content or
implementation convenience. No v1 retry, fallback, compatibility writer,
permissive parser or dual transport exists. `--sanitized-only` registers no v2
writer, snapshot/current-file service or snapshot raw-local route. Sanitized
export/import excludes the complete namespace and emits no empty carrier.
Issue #152 remains unresolved, and frozen Monitor/Workspace/SSE contracts remain
unchanged.

## D079: Session outcome is reduced from immutable source-scoped terminal facts

Status: Accepted (2026-08-09)

Issue #124 adopts PO124-A. Session schema `14` persists one private
`terminal_outcome` / `terminal_policy_version=1` fact pair on each exact
recognized SDK/Hook terminal signal. The closed clean/failed/neutral table is
owned by
[Canvas Session Workspace](specifications/interfaces/canvas-session-workspace.md).
`PostToolUseFailure`, `Stop`, `StopFailure`, `subagent.failed`, child error
state/text, and every Claude OTel event are nonterminal; recoverable error
evidence cannot synthesize Session failure.

Aggregate precedence is `failed > clean > neutral > no fact`, projected through
the frozen public statuses as `failed`, `completed`, `unknown`, and `active`.
`ended_at` is the maximum instant of every terminal fact, including neutral and
losing facts. Exact replay is a no-op; any durable event/fact mismatch aborts
the transaction. Retention may remove content but never a fact or aggregate.
The atomic v13-to-v14 migration reclassifies every retained event through the
same policy using only authorized content and recomputes every Session; current
v14 opens validate without content reads, repair, or reclassification.

D079's fixed private-copy migration remains consistent with the accepted
runtime-restore resurrection contract: only a post-gate pre-restore safety copy
uses Retention's existing restorable-coverage profile, which preserves exact
`Missing` source proof but rejects mismatch, extra source, or malformed state.
It cannot synthesize or terminalize data, change Retention facts, affect the
live target, or weaken strict startup, ordinary backup, restore-staging, or read
validation.

For an extracted archive with the complete exact current component vector,
restore staging therefore uses select-only current validation and restorable
coverage instead of rerunning Retention writable adoption/backfill. Missing or
older components still require the strict writable migration path; present
owner/receipt mismatch, extra source bytes, malformed state, and foreign-key
failure remain incompatible.

Only genuine Copilot-compatible `SessionEnd(reason=complete|user_exit)` has
controlled-live evidence on candidate `6a313fa61ac2bf161a6c6c4c0cb4ce4a6a311103`.
The other exact table rows are accepted producer-vocabulary Product policy,
not claims of live observation. Additional failed/neutral/live source evidence
remains useful `AWAITING_LIVE` characterization but is not an implementation
gate and cannot widen or override the table without a new accepted decision.

Public Session wire shapes, enum strings, property order, routes, headers,
status/error bytes, monitor health/SSE, and content states remain frozen. A
neutral fact counts for completeness, while current proposal/objective/
comparison/cost eligibility requires exact public status `completed|failed`,
`full` completeness, and a Session-scoped fact; `active|unknown`, non-full,
and type-only `Stop` evidence are ineligible. Later reclassification does not
rewrite immutable historical proposal, objective, effect, or estimate rows.

## D080: Local Monitor human routes are strict and Session search uses one body-bearing read

Status: Accepted (2026-08-09)

Issue #136 adopts PO136-A2b through
`docs/specifications/interfaces/local-monitor-v1-route-transport.md`.
Primary human paths are exact lowercase, slashless templates with canonical
local UUIDv7 identities, except timeline nodes use `node-` plus 32 lowercase
hexadecimal characters. Matched malformed identity/query input fails closed;
literal/case/slash near-path aliases are empty no-store 404. There is no
redirect, compatibility parser, first-wins query handling, name-based repair,
latest-object fallback or alternate primary path.

Exact `/sessions/unassigned` has reserved static precedence over the Session-ID
template. Case variants of that reserved literal are near-path empty 404, never
malformed Session-ID 400.

The unimplemented #133 Session collection GET is replaced before production by
one raw-default closed read:

```text
POST /api/local-monitor/v1/sessions
```

#133 keeps the Workspace semantic row requirements but does not yet define a
complete exact Session-collection success wire. A later canonical #134
Workspace-read response contract must fix it before #134 alone maps, reads and
serializes the endpoint or a consuming primary page is registered. Until then
only pure request/query/cursor/URL parsers may proceed. `q` and dynamic `model`
values are transient current-page/POST-body state. They never enter URLs,
history, browser storage, reusable cache, application logs, errors or cursors.
Non-default limit is also transient; URL cursor eligibility requires exact
q=null/model=[]/limit=null/default 50. Pagination uses an exact process-keyed
HMAC-bound 110-byte cursor and the exclusive
`sort_group ASC, sort_instant_utc DESC, session_id DESC` keyset, so neither raw
values nor an unkeyed low-entropy digest are exposed. The POST has no GET alias,
saved-search handle, server-side search session, fallback or second Workspace
reader.

The strongest objection is that q/model searches cannot be bookmarked or
restored after reload/back. That loss is accepted because a raw-URL exception
would put user-derived search/model text into request targets and common browser
history/log surfaces, while a server-side handle would add sensitive
persistence, cleanup, restart, backup and stale-handle contracts outside v1.

#133 owns local execution/node identity, #162 owns AI run identity and #165 owns
comparison identity. Their human-route representations are closed by the route
transport specification; #136 does not create a parallel identity store.
Known 24-hour comparison expiry uses #165/#166's minimal append-only
`(comparison_id, repository_id, expired_at)` runtime-database tombstone so the
same URL remains deterministic `410 comparison_expired` after operational
content deletion. Unknown or Repository-mismatched IDs are 404. Tombstones
contain no cohort, Session, filter, receipt, evidence, metric, hash, content,
model or path. Runtime backup validates and transactionally drops the exact
tombstone table only from its private staging copy after SQLite backup and
before inventory/hash/archive; source is untouched, manifest/restore omit the
table, and accepted restore startup creates it empty. Sanitized export/import
never queries it. Future #166 operational tables receive no implicit exclusion
and require their own exact runtime-backup amendment before shipping.

The strongest tombstone objection is database-lifetime row growth. It is
accepted as the smallest deterministic contract: pruning would make a known
expired URL later change from 410 to 404, while retaining comparison facts
would violate the operational-only boundary. A restored database intentionally
has neither comparison content nor tombstone and returns 404.

Human error sentence/HTML bytes remain #137/#169-owned. Only status, headers,
closed state/recovery tokens and nonreflection are fixed. The old `/traces`
list retires atomically with functional #138 Explorer integration and is then
an empty no-store 404 for every method, without `Allow`; technical
`/traces/{traceId}` stays under its frozen owner. `/historical-analysis`
retires only with #164. Pure route/parser helpers may precede data owners, but
no placeholder or substitute data route may be registered. Active Session
collection/page registration also waits for the later canonical #134 exact
response contract.

D080 refines D075's route/browser boundary without changing the primary IA,
raw-default-only posture, frozen `/api/monitor/*`,
`/api/session-workspace/*` v1, SSE, Canvas, exact identity/provenance,
missing-to-zero prohibition or Issue #152 scope.

## D081: Repository scope composes direct archive facts and owns exact target existence

Status: Accepted (2026-08-09)

Issue #156 adopts PO156-A for DC156-19. #160 owns archive meaning. #161 owns
`local_archive:1` storage and schema validation, archive queries and
state-machine validation, mutation, public archive routes and archive backup
validation. #156 owns catalog SQL, exact current assignment, virtual-scope
composition, the complete Repository catalog read, direct-fact boundary
validation, effective archive eligibility/reason composition and Repository
target existence. #134 consumes one completed
`ILocalRepositoryScopeSnapshotService` result and issues no catalog or archive
SQL. #161 issues no catalog SQL and opens no second connection inside either
handoff.

The precomposed `LocalArchiveEligibilityContribution` is replaced by these
internal direct-fact types:

```csharp
internal interface ILocalArchiveFactSnapshotContributor
{
    ValueTask<LocalArchiveFactContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryArchiveInput input,
        CancellationToken cancellationToken);
}

internal enum LocalArchiveState
{
    Active,
    Archived,
}

internal sealed record LocalArchiveSessionFact(
    string SessionId,
    LocalArchiveState State,
    long Revision);

internal sealed record LocalArchiveRepositoryFact(
    string RepositoryId,
    LocalArchiveState State,
    long Revision);

internal sealed record LocalArchiveFactContribution(
    IReadOnlyList<LocalArchiveSessionFact> Sessions,
    IReadOnlyList<LocalArchiveRepositoryFact> Repositories);
```

`LocalRepositoryArchiveInput(SessionIds, RepositoryIds)` remains. Before the
archive phase, #156 freezes the complete canonical ordinally sorted Session ID
set returned by #134, within the existing 10,000-Session bound, and the complete
canonical ordinally sorted full Repository catalog, not merely assigned or
candidate IDs. Immediately after the catalog phase and before the requested-
Repository check, archive input construction or #161 call, #156 validates and
freezes the complete Repository sequence. IDs must be canonical and strictly
increasing under `StringComparer.Ordinal`. A noncanonical, duplicate or
non-strictly ordered catalog row fails with
`InvalidOperationException("local_repository_catalog_snapshot_invalid")` and
the contributor is not called. The one frozen Repository sequence is reused
for input, exact-set validation, assignment composition and projection.

#161 returns exactly one Session fact and one full-catalog Repository fact for
every corresponding input ID. A missing `local_archive_current` row
materializes as `Active, revision 0`. Output order is not semantic: reversed or
independently shuffled collections remain valid. #156 joins by exact canonical
ID and never zips facts positionally.

#156 copies and validates both collections independently before composition.
The contribution, lists and items must be non-null; cardinality and exact-set
identity must match the inputs; every ID must be a canonical lowercase UUIDv7
present exactly once; and `State` must be defined. The only valid state/revision
pairs are:

```text
Active, 0
Active, positive even revision
Archived, positive odd revision
```

`Archived,0`, archived/even, active/positive-odd, negative revision and an
undefined state are invalid. Missing, extra, duplicate and same-count-
substituted IDs are invalid. Any invalid contribution throws the fixed internal
`InvalidOperationException("local_archive_fact_contribution_invalid")` without
target or row data and returns no partial snapshot. Cancellation is checked
while freezing each collection and before composition.

Contributor-owned `IReadOnlyList` instances are hostile mutable carriers. For
each list, #156 captures `Count` exactly once, requires the expected count and
reads each indexed item exactly once into a new #156-owned fact record.
Validation, lookup, reason selection and snapshot construction then use only
the owned copies, closing validation/reread time-of-check/time-of-use behavior.

The completed internal snapshot records are:

```csharp
internal sealed record LocalRepositoryCatalogSnapshot(
    string RepositoryId,
    string DisplayName,
    long Revision,
    string? CurrentLocatorId,
    long AssignmentConflictCount,
    LocalArchiveState ArchiveState,
    long ArchiveRevision);

internal sealed record LocalRepositoryScopeSessionSnapshot(
    string SessionId,
    ILocalRepositorySessionSnapshotRow Session,
    long AssignmentRevision,
    LocalRepositoryScopeAssignmentState AssignmentState,
    LocalRepositoryScopeAssignmentAuthority AssignmentAuthority,
    string? RepositoryId,
    IReadOnlyList<string> CandidateRepositoryIds,
    bool IsAllScopeMember,
    bool IsUnassignedScopeMember,
    bool IsRequestedScopeMember,
    LocalArchiveState ArchiveState,
    long ArchiveRevision,
    bool IsEffectivelyEligible,
    string? ArchiveExclusionReason);
```

Repository and Session rows receive their own direct state/revision facts; this
bounded seam adds no timestamps. After exact assignment resolution, #156 alone
computes:

```text
session_archived = session_fact.state == Archived

assigned_repository_archived =
    exact current RepositoryId is non-null
    AND repository_fact[RepositoryId].state == Archived

IsEffectivelyEligible =
    NOT session_archived AND NOT assigned_repository_archived

ArchiveExclusionReason =
    session_archived              ? "session_archived" :
    assigned_repository_archived ? "repository_archived" :
                                   null
```

When both direct facts are archived, the Session is ineligible and the scalar
reason is `session_archived` while both facts/revisions remain visible.
Restoring only one side leaves the other side's reason on a fresh snapshot.
Manual and automatic exact assignments use the same predicate. Conflict,
unassigned and explicitly-unassigned Sessions have no exact current Repository
and ignore all candidate Repository archive facts. `IsRequestedScopeMember` is
solely scope membership; `IsEffectivelyEligible` is solely archive eligibility
and is never ANDed with membership. `active_only` consumers require both
membership and effective eligibility; `include_archived` retains membership
and exposes direct facts and reason.

#156 also owns one stateless synchronous target-existence authority:

```csharp
internal interface ILocalRepositoryTargetExistenceAuthority
{
    IReadOnlyList<string> ReadExisting(
        SqliteConnection openConnection,
        SqliteTransaction exactTransaction,
        IReadOnlyList<string> canonicalRepositoryIds,
        CancellationToken cancellationToken);
}
```

The synchronous signature supersedes only the
`ReadExistingAsync -> ValueTask` signature in the noncanonical repaired PO161-A
packet. D082 must promote this signature and must not retain dual synchronous
and asynchronous authorities. The concrete Repository-persistence
implementation applies this exact precedence:

1. reject null arguments with normal BCL guards;
2. require an open connection, a non-null active transaction connection and
   `ReferenceEquals(exactTransaction.Connection, openConnection)`, otherwise
   throw
   `InvalidOperationException("local_repository_target_existence_transaction_invalid")`;
3. require 1..200 IDs, copy each once, and require canonical lowercase UUIDv7
   values strictly increasing under `StringComparer.Ordinal`, otherwise throw
   `ArgumentException("local_repository_target_ids_invalid", nameof(canonicalRepositoryIds))`;
4. check cancellation;
5. execute exactly one query on the supplied transaction; and
6. validate and freeze the complete result before returning.

The single dynamically parameterized statement is equivalent to:

```sql
SELECT repository_id, typeof(repository_id)
FROM local_repositories
WHERE repository_id IN ($repository_id_000, ..., $repository_id_NNN)
ORDER BY repository_id COLLATE BINARY;
```

All 1..200 placeholders bind exact text; values are never interpolated. The
authority performs no schema probe, PRAGMA, second query, N+1 read, retry,
alternate lookup or connection replacement, and does not open, begin, commit,
roll back or dispose caller resources. SQLite exclusive-lock contention is
attempted once. The original `SqliteException` with primary code 5
(`SQLITE_BUSY`) or 6 (`SQLITE_LOCKED`) propagates unchanged without wrapping,
mapping, sleeping or retrying, and the caller's connection and transaction
remain open, reference-equal and caller-owned.

The result is a new frozen, canonical, distinct, strictly ordinally increasing
exact subset of the frozen input and contains only `repository_id`. A non-text,
noncanonical, duplicate, out-of-input or out-of-order row throws
`InvalidOperationException("local_repository_target_existence_result_invalid")`.
Cancellation or failure returns no partial result. #161 public callers map only
busy/locked to `persistence_busy`; every other non-cancellation authority
exception maps to no-detail `archive_store_unavailable`. An empty valid subset
is `target_not_found`. Runtime-backup source/staging validation maps any
non-cancellation exception or returned-set inequality to `restore_incompatible`.

Exact `GET /api/local-monitor/v1/archive?target_kind=repository&target_id=...`
and Repository mutation each supply one ID on their exact transaction and
require set equality before archive-current or revision/state evaluation.
Runtime-backup validation keyset-pages distinct Repository target
IDs in nonempty ordinal pages of at most 200, requires equality for each page,
has no overall target-count cap and makes no empty call. The private dynamic
catalog-mutation `TargetExists` helper is not this authority and is not exposed.

The composite snapshot retains one connection, one deferred read transaction,
first-read snapshot pin, sequential phases, no overlapping reader, revocable
contributor capability, catalog-table denial for contributors, cancellation
disposal and one `persistence_busy` mapping:

```text
#134 Session contributor
  -> #156 catalog reads
  -> #161 direct archive fact contributor
  -> #156 validation/composition
  -> return one complete snapshot
```

There is no fallback or second snapshot. The raw-default host dependency is
`ILocalArchiveFactSnapshotContributor`; no fake/default #134 or #161
contributor is registered. Exactly one stateless
`ILocalRepositoryTargetExistenceAuthority` singleton is registered in the
raw-default Repository composition block and no sanitized-only human
composition. Runtime backup explicitly uses the same concrete implementation
outside human-host DI, including sanitized receiver/runtime-backup posture.

D081 selects two typed direct-fact collections with Session-first scalar
precedence over precomposed #161 eligibility, Repository-first precedence,
multi-reason/combined-enum expansion, or treating simultaneous archive as
invalid. It selects the #156-owned caller-connection/caller-transaction bounded
existence authority over #161 catalog SQL, a path-bound store, widening
`ILocalRepositoryReadTransaction`, or cross-component foreign keys/copied
parents. These alternatives either retain two composition authorities, alter
accepted v1 consumers, lose transaction coherence, duplicate schema authority
or broaden SQL capability.

The strongest objection to Session-first precedence is that its scalar can hide
the Repository cause when both predicates are true. The scalar is only the
deterministic primary explanation: both direct facts and revisions remain
mandatory, so a multi-reason carrier would only expand frozen accepted
contracts. The strongest objection to exposing
`SqliteConnection`/`SqliteTransaction` is SQLite coupling. Both components
already share the SQLite persistence boundary, while mutation atomicity and
restore staging require the caller's exact transaction; a generic abstraction
would conceal that proof or expose broader query authority.

The fail-closed counterexamples are binding. An archived candidate never
excludes a conflict/unassigned Session; simultaneous archive retains both facts
and selects `session_archived`; impossible `Active,1` is rejected; a staging
transaction cannot accidentally prove existence in the live database;
membership cannot suppress independent archive truth; shuffled or mutating
carriers cannot cause positional/second-read misbinding; and lock contention
cannot trigger retry or transaction replacement.

D081 adds no `local_archive` tables, schema, routes, public DTO/SSE bytes,
archive timestamps, Repository archive columns, #161 catalog SQL, generic SQL
capability, candidate archive filtering, dual reason, cascade/ingest restore,
Retention/pin/delete-now change, compatibility carrier, permissive parser,
fallback, retry, sanitized-only human registration or Issue #152 resolution.

Canonical promotion follows D079 -> D080 -> D081. D080/#136 is only the
specification/history integration base and is not a #156 runtime, type, schema
or test dependency. The binding runtime rollout is:

```text
#124 PO124-A specification + Session 14 migration/validation
  -> #156 D081 direct-fact and Repository-existence seam
  -> #161 D082 local_archive:1 storage/mutation/routes/backup
  -> #158 D083 Skill snapshot/current-file authority
  -> #134 D084 Workspace projection/read serialization
```

#124 establishes exact current Session 14 with no Session 13/14 dual branch.
#156 lands no archive storage. #161 consumes D081 and inserts
`local_archive:1` after catalog and before Retention; `local_archive:1` plus
`session:13` is incompatible. #134 starts only after #156/#161/#158 and creates
no placeholder facts, direct SQL, second reader or fallback projection.

## D082: Local archive uses append-only state/history and head-adjacent semantic retry

Status: Accepted (2026-08-09)

Issue #161 adopts PO161-B and the singular executable
[Local Archive v1](specifications/interfaces/local-archive.md) authority.
`local_archive:1` owns reversible direct Session/Repository archive state,
append-only history, mutation/read/list application, the #161 implementation of
D081's direct-fact contributor, three exact raw-default routes and whole-database
runtime backup/restore validation. Archive remains visibility/selection
metadata, not deletion, Retention, pinning, Session status, assignment or ingest
state.

The selected contract consists of seven decisions.

1. Archive mutation uses two-table state plus head-adjacent semantic retry and
   persists no request or response receipt.
2. The schema contains exactly one current table, one event table, two indexes
   and six append-only/identity guards. Logical absence is `Active,0`; every
   stored current row has a complete contiguous alternating event chain whose
   head agrees with current. Revision, not timestamp or event UUID order, is
   history authority.
3. D081's synchronous `ILocalRepositoryTargetExistenceAuthority.ReadExisting`
   proves Repository targets on the caller's exact open connection and owning
   transaction. #161 issues no catalog SQL, opens no second connection and
   performs no retry or alternate lookup.
4. D081's `ILocalArchiveFactSnapshotContributor` receives the complete exact
   Session set and full Repository catalog and returns direct state/revision
   facts only. A missing archive-current row materializes as `Active,0`; the
   contributor reads no archive event/head history and returns no eligibility
   or exclusion reason.
5. D081 remains the sole authority that validates those complete fact sets and
   composes assignment-dependent effective eligibility. When both the Session
   and its exact assigned Repository are archived, both facts/revisions remain
   visible and D081's scalar primary reason is `session_archived`. D082 does not
   re-decide or duplicate that composition.
6. The public contract is three exact raw-default routes with closed method,
   query, media, UTF-8, JSON, cursor, success and fixed no-echo error behavior.
   Framework-generated binding/error bodies, aliases and permissive parsing are
   not authorities.
7. `local_archive:1` is an ordinary component of the single SQLite restore unit
   immediately after `local_repository_catalog:1` and before Retention. It is
   validated at every existing source, staging, pre-swap, safety-backup and
   installed fence and is wholly absent from sanitized evidence export/import.

Let `M = 9,223,372,036,854,775,807`. For a desired state/action and one valid
target, classification is exact:

```text
apply
  current.revision == expected
  AND current state differs from desired
  AND current.revision < M

no_op
  current.revision == expected
  AND current state already equals desired

semantic_retry
  expected < M
  AND current.revision == expected + 1
  AND current state equals desired
  AND the unique current head has the same action
  AND that head has the exact expected -> current revision pair

revision_exhausted
  current.revision == expected == M
  AND current state differs from desired

stale
  every other valid combination
```

Only the adjacent current head qualifies as semantic retry. It has no TTL or
count limit while that head remains current; a later restore/rearchive makes
the old request stale. `no_op` is not semantic retry, and success is freshly
serialized from the current fact rather than replaying durable response bytes.
`Idempotency-Key` is not required, interpreted, rejected, persisted or echoed
and cannot change these semantics.

Session mutation freezes original request order for the response and a separate
canonical-ID order for proof, locking, validation and writes. For all 1..200
targets, precedence is:

1. prove every exact Session parent; any absence is `404 target_not_found`
   before any archive current/event read;
2. validate all complete current/history/head facts; any contradiction is
   `503 archive_store_unavailable`;
3. classify every target; any `stale`, or any batch containing both `apply` and
   `semantic_retry`, is `409 revision_conflict` with no write;
4. otherwise any `revision_exhausted` is
   `503 archive_store_unavailable` with no write; and
5. `apply + no_op`, `semantic_retry + no_op`, all-apply, all-no-op and
   all-semantic-retry batches succeed.

Repository mutation has exactly one target and uses the same classification.
Every successful applied target appends one distinct canonical UUIDv7 event and
advances current once; all applies in one request use one captured UTC instant.
No-op/retry facts retain their existing timestamps. The complete success entity
is canonically serialized and copied before commit and emitted only after a
successful commit. Writer failure, empty bytes, cancellation before commit or
commit failure rolls back and returns no entity. Once commit succeeds, durable
success wins without another cancellation or database/clock read; transport
loss is recovered only by a fresh semantic retry.

Mutation proof, validation, classification, writes, pre-commit serialization
and commit use one connection and one `BEGIN IMMEDIATE` transaction. Direct and
list reads use one deferred transaction. SQLite primary code 5/6 encountered
before successful commit maps once to `503 persistence_busy`; there is no
retry/fallback. Schema/current/event/chain/head contradiction, revision
exhaustion and every other non-busy, non-cancellation route-local store or
parent-authority failure map to fixed `503 archive_store_unavailable`. Failure
never appends a partial event or changes a current row.

The exact public paths are:

```text
GET  /api/local-monitor/v1/archive
POST /api/local-monitor/v1/archive-actions
GET  /api/local-monitor/v1/archived-items
```

The two GET paths accept only GET and the action path accepts only POST. A
matched unsupported method returns fixed 405 with exact route `Allow`; HEAD has
the same representation length and headers but zero entity bytes. Valid-Host
matched responses use `Content-Type: application/json; charset=utf-8` and
`Cache-Control: no-store`; they emit no CORS, redirect, cookie, ETag or input/
exception detail. Global loopback/Host validation is first, exact machine path
classification precedes the human fallback, matched routes enforce the existing
same-origin decision, and POST additionally requires exactly one effective
`x-monitor-csrf: local-monitor` value.

Queries, cursor frames and POST bodies use the closed bounds and canonical bytes
owned by the executable archive specification. Session actions contain 1..200
distinct canonical lowercase UUIDv7 targets; Repository actions contain exactly
one. Archived-item limits are canonical decimal `1..200`, default 50, and list
order is `archived_at DESC, target_id DESC` with `limit+1` lookahead and a
cursor for the last emitted item. The closed non-HEAD error set is exactly:

```text
400 invalid_host
400 invalid_request
400 invalid_cursor
403 csrf_rejected
404 target_not_found
405 method_not_allowed
409 revision_conflict
413 request_too_large
415 unsupported_media_type
503 archive_store_unavailable
503 persistence_busy
```

The schema artifact owns exactly `local_archive_current`,
`local_archive_events`, two named indexes and six named triggers. There is no
seeded active row, third head/receipt table, operation ID, response BLOB, view,
generated column or compatibility namespace. Current facts are positive
revisions: archived is odd with non-null `archived_at`; stored active is even
with null `archived_at`. Every history starts `archive 0->1`, alternates action,
advances by one, and matches current state/revision/timestamps at its head.
Backward timestamps are valid because revision alone orders history. Corrupt or
partial state never degrades to logical `Active,0`, conflict or a partial page.

Runtime backup records `local_archive:1` in this order:

```text
monitor
session:14
local_repository_catalog:1
local_archive:1
retention:1
skill_projection:1
skill_invocation_snapshot:1       # only when separately released
local_workspace_projection:1      # only when separately released
```

A declared archive requires exact Session 14 and catalog 1; declared archive
with Session 13 has no compatibility exception. When archive is wholly absent,
D079's complete older/absent Session migration matrix remains valid: Session is
first brought to 14, catalog is installed/validated as required, then empty
archive v1 is installed. This is an archive-absent migration path, not a dual
archive reader or parent. Validation streams every scalar, chain and head,
proves Session and Repository parents in nonempty pages of at most 200 on the
exact transaction, has no total-target cap, and requires exact manifest version
and both table row counts.

Backup and restore remain one whole-database replacement. There is no archive
ZIP member, merge, overlay, orphan drop/remap, synthesized repair event, queue,
alternate collision resolver or archive-specific Retention reconciliation.
Archive history/current bytes, including an empty namespace, are excluded
entirely from sanitized evidence export/import. `--sanitized-only` still runs
database initialization/backup validation, but registers no archive route,
application, contributor, page, script or human scope service and returns the
existing empty no-store 404 for every archive path.

Rejected retry/storage alternatives are durable request/response receipts,
same-state-as-retry, every-behind-is-stale and an operation/batch ID or response
BLOB on events. Receipts add a third lifetime/backup/public-key contract and can
replay an old response after later state changes; broader same-state retry loses
revision authority, while rejecting every behind request prevents deterministic
recovery of a lost post-commit response. Current-only state cannot prove retry
history; event-only fold-on-read is unbounded; a third head table duplicates
current; wall-clock or UUID order fails under backward local time.

Rejected ownership/composition alternatives are #161 catalog SQL, a path-bound
catalog store, a generic SQL capability, cross-component foreign keys/copy
tables, precomposed eligibility, Session-only or assigned-Repository-only facts,
and another #134/#161 join. Rejected simultaneous-archive alternatives are
Repository-first, null/error, a combined enum and an ordered reason array.
Rejected wire/backup alternatives are framework-default 405/JSON binding,
permissive media, unbounded cursor/body, route aliases, frozen API/SSE fields, a
separate ZIP member, component merge/overlay, orphan repair/remap, synthesized
events, a dual Session parent or a sanitized carrier.

The strongest binding counterexample combines all critical boundaries:

1. Session `S` is directly archived at revision 3.
2. Its exact assigned Repository `R` is directly archived at revision 5.
3. Archived conflict candidate `R2` is not the exact assignment.
4. A prior batch lost its HTTP response and a new batch mixes that adjacent
   semantic retry with a fresh apply.
5. Restore staging database B contains archive target `R`, B's catalog does
   not, and live database A happens to contain the same Repository ID.
6. An archived Repository page at limit 200 has a 201st lookahead whose parent
   is absent from the catalog.
7. The contributor returns reversed fact lists, then changes a value on a
   second carrier read.
8. Another SQLite connection holds an exclusive lock during Repository proof.

D082 closes the example without another table or composition authority. #161
returns direct `S`, `R` and `R2` facts; D081/#156 alone joins exact assignment
`R`, retains both direct causes, selects scalar `session_archived` and ignores
`R2` for eligibility. Apply plus semantic retry conflicts with no write. The
synchronous Repository proof uses exact staging B and rejects the orphan. The
201 rows are split into nonempty <=200-ID proofs on the same transaction and an
unequal union returns 503 before any entity or cursor. #156 copies each hostile
carrier element once and joins only by exact ID. Lock contention is attempted
once and propagates original primary code 5/6 without replacing the transaction.

#161 adds no UI: no Razor page/model, static asset, navigation, Settings section,
button, dialog, visual state or sentence-level copy. It does not cascade between
Repository and Session, restore on ingest, alter Session status/completeness/
assignment, extend Retention, substitute pin/delete-now, or change frozen
`/api/monitor/*`, `/api/session-workspace/*` v1 or SSE bytes. There is no
fallback reader, compatibility carrier, permissive parser, second Skill
authority or Issue #152 resolution.

Canonical promotion follows D079 -> D080 -> D081 -> D082. Runtime rollout is:

```text
#124 reviewed Session-14 implementation
  -> #156 D081 direct-fact + synchronous Repository-existence code
  -> #161 D082 dormant schema/store
  -> #161 runtime-backup installation/validation

#136 reviewed LocalMonitorV1 parser delta
  -> required before archive route activation, not before #156 code

converged #161 archive storage/backup + D080 parser foundation
  -> #161 raw-default contributor/routes
  -> #158 D083 Skill snapshot/current-file authority
  -> #134 D084 Workspace projection/read serialization
```

#124 Session-14 code and the disjoint #136 parser delta may be reviewed in
parallel, but both are integrated before archive route activation. Runtime
backup changes remain serialized in the exact order Session -> catalog ->
archive -> Retention -> Skill. #134 begins only after the D081 seam and D082
contributor are integrated, consumes one completed #156 snapshot, and adds no
placeholder fact, direct SQL, second reader or fallback projection.

## D083: Skill invocation v2 uses one capability-bound transport and fresh semantic responses

Status: Accepted (2026-08-11)

Issues #119/#157/#158 adopt the complete contract in
[Skill Invocation Snapshot](specifications/interfaces/skill-invocation-snapshot.md).
The former eight `BLOCKED_DECISION` groups are closed. This decision authorizes
tracked canonical promotion and, after mandatory live-Issue reconciliation,
the nonregistered #119 parser/handoff. It does not claim that #158 persistence,
routes, platform readers, host activation, release, or any prerequisite
implementation has landed.

D083 selects the following eight decisions.

1. The additive v2 request is one exact capability-bound normalized envelope.
   Its payload is the SDK 1.0.4 nine-property surface: required `name`, `path`,
   and `content`, plus optional `allowedTools`, `description`, `pluginName`,
   `pluginVersion`, `source`, and `trigger`. There is no `model`, compatibility
   alias, reflection-defined schema, or direct callback writer.
2. Status, media, method, body-bound, error-byte, and precedence rules are
   closed. The v2 route and current-file route own exact request limits;
   response JSON is compact UTF-8/no-store, 204 is derived and empty, and each
   matched wrong method including HEAD/OPTIONS uses the fixed 405 contract.
3. One immutable checked-in payload schema and one contiguous complete
   compatibility-registry history own producer admission. The greatest
   mechanically valid complete revision is the only current authority. A
   previous revision may prove the history of an explicit revoke but is never
   an admission fallback.
4. Exact `(source_adapter,source_event_id)` receipt lookup is first. The
   29-field binary semantic fingerprint binds the selected native Session and
   optional Run graph plus payload/classification/content facts. Equality
   validates the current complete graph and freshly derives 204; no status,
   headers, or response entity are stored or replayed.
5. `payload_sha256` and `payload_bytes` cover the exact received payload-token
   byte slice. The sole raw carrier is the canonical
   `session-event-content.skill-invoked.v1` base64 document in
   `session_event_content`. There is no second raw item, body/path copy,
   normalized-JSON carrier, response cache, or sanitized carrier.
6. Classification is total and independent of property encounter order:
   malformed, then missing, then binary, then oversized, then
   `available/none`, with the exact reason precedence and nullability matrix in
   the owning specification. #154 alone supplies the point diagnostic
   `current|stale|invalid|unavailable` and the separate opaque current-
   authorization capability; #158 adds no claim/registry reader.
7. Metadata, historical-content, and current-file documents use fixed schema
   tokens/property order/explicit nulls and are freshly serialized from facts
   validated for that request. Current-file `same|changed` is exact byte
   equality, never digest equality. Ingest success remains an empty derived
   204.
8. Discovery authority is request-memory only and is configured solely by
   repeatable `--skill-discovery-project-path` (0..16) and
   `--skill-discovery-directory` (0..32) argv options. Zero roots means no
   current-file service/POST. Windows and Linux have independent certified
   retained-root/native no-follow gates; unsupported or uncertified platforms
   receive no path fallback, and no root/discovery/path identity is persisted,
   backed up, logged, measured, or returned.

The immutable r0001 identities are binding:

```text
payload schema          980 bytes  8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c
schema sidecar           65 bytes  3f6b076bb7329662088c0b055a81e5f3d9789cd654ddde27bf3b1877d32ba123
registry                431 bytes  3ae5d255647edad6e23f077c3e9042be50d593211cd9a90d6c9f7210c53bfdda
snapshot SQL          9,213 bytes  502f787c28b13363826aeccde96979ed22dc89c8ee137593922b106528935d7c
Session child registry 1,019 bytes 0b5f7782a9686791c2ce9bcff8638dccf1de44833303c0932f05e2ae57259c64
receipt frame           726 bytes  5698c710512676dab263596e169be6e73746525a695f67b7929866fbc502cfb7
generic denial           43 bytes  9efd316487e88e9c4ca2440f058d7097518cd01205e5ed1788bd37010f758855
```

The only r0001 producer composition is SDK package/assembly `1.0.4/1.0.4.0`,
the application-bundled same-client Copilot CLI status `1.0.65`, protocol `3`,
adapter `copilot-sdk-dotnet-1.0.4+cao-skill-v2.1`, and normalizer
`github-copilot-sdk.skill-invoked.normalize.v1`. The application launches only
its co-located, headless, non-updating bundle. An explicit path/URI, PATH,
environment override, global CLI, compatible-higher protocol, runtime
substitution, or later SDK/CLI does not satisfy r0001.

Every SDK callback first acquires its exact immutable runtime-generation
capability. `SkillRuntimeCapabilityBridgeV1` then binds one random 32-byte,
43-character unpadded-base64url token to the complete body length/digest and
that same capability for exactly 30 monotonic seconds, with at most 64 pending
entries. The dedicated sender targets only the already-bound numeric loopback
HTTP/1.1 listener and has no proxy, redirect, cookie, credentials, ambient
trace headers, retry, or resend. The token is consumed once before body read;
it and its digest/count/generation/expiry are never logged, measured,
persisted, backed up, fingerprinted, or returned. Arbitrary loopback callers
cannot borrow a current generation. The sole event topology is:

```text
SDK callback
  -> normalized bounded body
  -> single-use capability/body-binding token
  -> loopback POST /api/session-ingest/v2/events
  -> immutable #119 capability-bearing handoff
  -> one #158 transaction participant
```

There is no direct callback-to-writer path, second HTTP transport, query/cookie
token, previous-generation fallback, or v1 compatibility handoff.

Session 14 retains one core fingerprint. Exact
`skill_invocation_snapshot:1` activates one compile-time child-trigger registry
entry; the exact two Session-target trigger tuples are proved and filtered
before the unchanged parent fingerprint, then the complete two-table/eight-
trigger child graph is validated independently. Install validates exact
Session 14, Retention 1, and Skill Projection 1, creates the child objects,
inserts the component stamp last, reruns both parent and child validation, and
commits once. There is no object-only/stamp-only state, adoption, repair,
pending-install bypass, or Session 13/14 dual validator.

First write resolves the exact native Session binding with cardinality 0/1/>1.
An existing `native`, `explicit_resume`, or `explicit_handoff` binding is
preserved; `trace_context`, ambiguity, or orphan state fails closed. A nonnull
Run natural key uses the same exact 0/1/>1 rule. The selected native Session ID
is stored privately. Every v2 parent Event has `content_state=available`, even
when payload classification is nonavailable, because the canonical raw Event
document exists. One transaction clock sample supplies all first-write times
and the checked 90-day expiry. A receipt hit creates no Session, Run, Event,
Retention item, claim, snapshot, or ID.

The generic Session content route treats every existing `skill.invoked` Event
as missing and returns the exact 43-byte 404 before a Retention lease, content
selection, base64 decode, or materialization. Missing/type policy and the
existing non-Skill Retention admission/selector execute in one Session-owned
`BEGIN IMMEDIATE` transaction with no nested transaction or intervening
commit. The raw route buffers a non-Skill response and uses the shared
store-backed terminal seal before response start; terminal loss/busy discards
it and aborts without a status/header/entity.

Raw HTTP authorization has two independent total orders. Retention owns the
live-lease/clock-backed `TryCompleteWithoutRaw` and `TrySealRawResponse` result;
runtime generation owns its terminal response/commit seals. A pre-runtime safe
current-file result needs Retention completion only. A post-runtime safe error
needs Retention completion followed by the runtime response seal. Raw current-
file success wins the runtime response seal first and the Retention raw seal
second. Retention `lost|busy` always discards buffers and aborts with no
substitute response. Runtime invalidation can substitute only the fixed
discovery-unavailable response where the Retention completion rule permits it.
No SQLite/publication lock is held across HTTP I/O.

The current-file consumer receives one fixed, nonrenewing two-minute Retention
operation grant and one original expiry notification. It holds the exact root-
set lease, #154 current-authorization capability, and runtime-generation
capability through discovery, native walk/read/re-proof, fresh serialization,
and terminal completion. Normal shutdown closes root/runtime admission and
drains already admitted work; mismatch/reconnect invalidates unsealed runtime
work without transferring it to a newer generation.

Windows requires certified local NTFS/ReFS plus retained-root, every-segment
`NtCreateFile` proof. Linux requires kernel 5.8+, certified local
ext4/xfs/btrfs, `openat2` with the exact beneath/no-symlink/no-magic-link/no-xdev
flags, and the complete required `statx` masks. macOS, BSD, other systems, and
uncertified filesystems register no current-file POST. The SDK is called once
with the complete role arrays; its materialized result is scanned fully and
all eight documented facts participate in ambiguity. Missing/null/empty SDK
`Skills` normalize to one successful empty inventory and return not-discovered;
a top-level null or observable enumeration/DTO failure is unavailable.

Raw-local replay retains its single explicit-consent authority. It validates
the complete nonraw graph before selecting the canonical document, then
validates base64/digests/classification and scans decoded tokens/names/string
values with the existing credential matcher before any publication. It adds no
snapshot/receipt/claim/native-selection/root/discovery carrier and never
reconstructs a live graph. `--sanitized-only` rejects before lookup or grant.

Receiver-only composition registers no live producer/forwarder/bridge, v2
route, #158 writer, current-file service, or any of the three Skill raw routes.
Existing OTel-only #154 claims remain `snapshot_id=null` /
`snapshot_state=not_captured` and create no snapshot row. Sanitized evidence
contains no snapshot namespace, empty marker, raw document, receipt, selected
binding, runtime/root/discovery fact, or reconstruction path. Component
installation/validation remains an independent database authority and must not
be inferred from host posture.

Canonical decision order is D079 -> D080 -> D081 -> D082 -> D083. The binding
implementation DAG is:

```text
tracked D083 promotion
  -> authorized #117/#119/#124/#156/#157/#158/#161 Issue reconciliation/readback
  -> nonregistered #119 strict parser + immutable handoff

#124 exact Session 14 implementation
  -> #156 D081 carrier/composition implementation
  -> #161 D082 local_archive:1 implementation/backup/restore

#124 exact Session 14 implementation
  -> Retention pinned-read/terminal/equality-replay implementation

#119 + #124 + #156/#161 + Retention, all integrated and green
  -> #158 child/schema/persistence/readers/routes/platform composition
```

The remote reconciliation is a dispatch gate, not implementation evidence. It
must remove stale `model`, `SkillDiscovery.ProjectPaths`,
`SkillDiscovery.SkillDirectories`, weaker Unix `openat`, and conflicting
dependency text from the affected Issues, then read back all seven Issues and
prove the exact DAG and absence of stale tokens. It requires separately
authorized remote writes.

Host activation and release remain gated by exact artifact/parser/transaction/
route RED/GREEN evidence, Session/archive/Retention integration, Windows and
Linux native matrices, signed-in same-bundled-client Version `1.0.65` /
ProtocolVersion `3` live evidence, repository full validation, independent
review, and the derived public workflow update only after the flags exist. The
currently observed application/global CLI 1.0.75 is deliberately not admitted
by r0001 and is a runtime NO-GO fact, not a product-decision contradiction.

D083 adds no compatibility entry, adoption, backfill, dual reader/write,
response-byte storage, runtime reflection, direct historical path read,
inferred join, second Skill/#154/Retention authority, raw-content logging,
repository-safe raw carrier, #152 resolution, or claim that any engineering or
release gate is complete.

## D086: Skill snapshots use only Local Monitor-owned completed analysis sessions

Status: Accepted (2026-08-23)

Issue #158 supersedes only D083 Group 5's producer/topology clauses and the
raw-analysis rule that disabled every Skill. D083 remains authoritative for the
wire, parser/handoff, schema, receipt, persistence, classification, raw owner,
read routes, native current-file proof, Retention, and security contracts.

External GitHub Copilot CLI and VS Code sessions remain unavailable and
unobserved. The product does not enumerate, resume, attach to, or read history
from a foreign session. `ResumeSessionAsync` mutates session configuration and
does not prove exclusive ownership or the resumed runtime identity. The sole
producer is a raw-default Local Monitor raw-analysis session created and
exclusively owned through completion by one admitted `CopilotClient`.

Before r0002 or producer startup code, versioned gate T0b must prove on that
same signed-in bundled client: exact status Version and integer
ProtocolVersion, matching `SessionStartData.CopilotVersion`, callback
registration before both session creations, prompt-free probe inventory,
execution `DisabledSkills`, retained-root-only execution inventory/invocation,
and exact task completion. Deterministic T0b alone resolves the exact admitted
enabled/user-invocable retained-skill command through the SDK commands API,
invokes it with `executionSession.Rpc.Commands.InvokeAsync`, requires a prompt-
producing result, sends that exact returned prompt with `AgentMode.Autopilot`,
and proves an exact matching typed retained `SkillInvoked` followed by an exact
typed task-complete event. T0b is expected to certify exact CLI
versions `1.0.65` and `1.0.75`, SDK package/assembly `1.0.4/1.0.4.0`, protocol
`3`, adapter `copilot-sdk-dotnet-1.0.4+cao-skill-v2.1`, and normalizer
`github-copilot-sdk.skill-invoked.normalize.v1`; immutable contiguous r0002 may
admit only complete exact tuples actually proved. Failure stops r0002, startup
code, integration, and release. r0001 remains byte-identical historical
authority and is not an admission fallback.

An admitted analysis creates one current-file-invisible candidate containing
the exact client, certified identity, and retained directory scope. With
explicit `--skill-discovery-directory` roots, it enables Skills, disables
configuration discovery, skips custom instructions, supplies no plugin or
instruction directories, and uses only exact retained Skill directories.
Those roots are the sole allowed Skill provenance. On the same certified client
it first creates and disposes a prompt-free inventory-probe Session whose
callback is registered before creation. The probe retains only the existing
exact source-qualified custom raw-analysis tool entries and does not require
either admitted built-in. It rejects collisions and missing or
unverifiable paths, then freezes every non-retained Skill name into
`SessionConfig.DisabledSkills` for a distinct owned execution Session, also
with its callback registered before creation. Before any prompt, execution
inventory must show no enabled non-retained Skill and no inventory drift or
inability to disable. Before prompt, every retained inventory path is proved
with the existing native retained-root opener and lease; each later invocation
path is re-proved when invoked, never trusted from SDK path strings. Only the
execution Session produces callbacks/import. With retained roots it preserves
the exact custom entries and adds exactly `builtin:skill` and
`builtin:task_complete`. Wildcards, every other built-in, MCP tools, plugins,
ambient instruction/config discovery, and widening by a retained Skill's
`allowed-tools` metadata remain forbidden. Production sends the ordinary
requested prompt with `AgentMode.Autopilot`, never forces an arbitrary retained
Skill invocation, and uses the exact typed task-complete event as terminal.
With zero roots, analysis runs with Skills disabled, retains no generation, and
the current-file service/POST remains absent (outer `404`).

The candidate registers its callback before session creation and, under one
lock, accepts exactly one matching SessionStart, zero through 64 SkillInvoked
callbacks in assigned ordinal order, then exact same-session task completion.
Each callback prepares one complete one-event v2 UTF-8 body from the immutable
candidate identity. The process-memory-only aggregate is capped at 8,388,608
complete body bytes. Any malformed, out-of-order, mismatched, 65th, oversized,
post-terminal, cancellation, root, identity, or lease failure poisons it.

Before exact completion there is no HTTP send or persistence. The
**owned-session post-completion buffer/import** is synchronous and non-durable:
zero invocations perform no v2 or v1 writes. For one or more invocations, after
completion it sends prepared v2 bodies sequentially with same-candidate
capabilities and fresh body-bound tokens, without reserialization or retry;
then it awaits one-event v1 SessionStart and one-event v1 task-complete writes.
Any failure stops immediately, releases capabilities exactly once, fails the
analysis, disposes the failed candidate, preserves only the valid already-
committed prefix, and leaves the preceding current generation in place. There
is no durable queue or importer receipt, startup recovery, or automatic retry.

Only after all required imports succeed and the SDK session is disposed may
the exact candidate publish atomically as current; publication order, not
analysis start order, defines the latest successful generation. Zero Skill
invocations write no snapshot, receipt, or Session events, but with configured
roots a successful completed analysis may still publish its generation for
current-file discovery. With roots but no published generation, the existing
current-file route returns exact `503 skill_current_file_discovery_unavailable` after its
earlier gates. Replacement, failure, refusal, lease loss, and shutdown reject
new capabilities, cancel unsealed work, drain capabilities, dispose the client,
then dispose the retained directory scope, exactly once.

D086 adds no option, wire/schema/receipt/storage shape, #154 authority,
foreign producer, direct writer, fallback, compatibility path, backfill,
durable importer state, or external CLI/VS Code capture. Implementation,
platform/live evidence, full validation, independent review, and release remain
pending.

The current canonical decision order is D079 -> D080 -> D081 -> D082 -> D083
-> D085 -> D086. D086 is placed adjacent to the D083 text it narrowly
supersedes; this placement does not reorder D085's Retention lock authority.

## D085: Exact admitted lease tuple is the Retention publication-lock order

Status: Accepted (2026-08-15)

Issue #170 closes the undefined lock order for composite Retention owners that
persist no semantic frontier ordinal. It also prevents owners that do persist
such an ordinal from creating a second global lock-order authority.

D085 selects the following eight decisions.

1. Before any publication lock, Retention constructs a lock-only permutation
   ascending by exact immutable admitted `(store_instance_id, item_id,
   lease_kind_rank, owner, generation)`, where both IDs and owner use ordinal
   case-sensitive order, `lease_kind_rank` is `access=0`, `operation=1`,
   `deletion=2`, and generation uses signed integer numeric order. Persisted
   generation must be positive and canonical before lock acquisition.
2. This five-tuple is both the sole global publication-lock sort key and the
   publication-lock identity; a persisted semantic frontier ordinal cannot
   override either authority.
3. Owner/caller/selector order remains the semantic frontier for selection,
   returned values, output serialization, digests, and owner-visible
   processing. Retention stores the permutation and does not reorder
   owner-visible results.
4. One admitted tuple resolves to one in-memory publication state and lock
   authority for the handle lifetime. No wrapper, batch, renewal, terminal
   path, cleanup path, or owner adapter may manufacture an independently
   lockable grant for that tuple. Duplicate object references and distinct
   objects carrying the same tuple are duplicate members and are rejected
   before the first publication lock, alongside invalid generation,
   contradictory duplicates, and unproven alias uniqueness.
5. Retention computes the complete publication-lock permutation before the
   first publication lock. While publication locks are held, it never
   discovers, adds, or reorders a member or its publication lock, and it
   performs no `await`, HTTP, or file I/O. The exact live-lease,
   persisted-expiry, and admitted-capability proofs that bind an already-locked
   member's published state remain inside their publication scope.
6. Every publication lock is acquired in ascending tuple order and released in
   exact reverse acquisition order.
7. Single-member scopes follow the same rule trivially.
8. #154 and any future owner may retain a persisted semantic frontier ordinal
   for its own graph or replay authority, but that ordinal is not a second
   lock-order authority. Existing all-or-none terminal, grant, and renewal
   behavior plus cleanup, read-taxonomy, public-shape, and frozen-wire
   contracts remain unchanged.

Rejected alternatives are requiring a persisted ordinal for every owner,
because it adds unnecessary schema/API surface and prevents generic batch use;
using caller/selector order, because reversed input permits lock inversion; and
using an owner ordinal when present with tuple fallback otherwise, because the
same grants could then participate in different global lock orders and the
deadlock proof would remain open.

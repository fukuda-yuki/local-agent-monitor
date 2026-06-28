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

## D009: Dashboard の第一候補は static HTML + GitHub Pages にする

Status: Accepted

Static HTML dashboard を常設 dashboard 第一候補にする。
Grafana JSON dashboard は将来候補または fallback として残す。

Consequences:

- `generate-static-dashboard` は `index.html` と `dashboard-data.json` を生成する。
- No server-side API, runtime service, or network dependency.
- GitHub Actions が daily snapshot を publish する。

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

Status: Accepted

Publish layout:

```text
latest/index.html
latest/dashboard-data.json
YYYY-MM-DD/index.html
YYYY-MM-DD/dashboard-data.json
```

Daily snapshots are retained and not automatically deleted.
Generated snapshots go to `gh-pages` and Pages artifacts, not to `main`.

Open follow-up:

- repository size monitoring。
- GitHub Pages access control validation。
- first live workflow result。

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
dotnet test CopilotAgentObservability.slnx
```

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
- Pages visibility。
- live workflow operation。
- snapshot growth monitoring。

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

Status: Accepted

Sprint8 の姿勢（raw は `--enable-raw-view` opt-in）を反転する。単一ローカル利用者ツール
として、raw body と PII を **既定で表示する**（server-rendered、inert text、inline
rendering）。

変更点:

- `--enable-raw-view` は廃止（既定が raw 表示のため不要）。
- `--sanitized-only` フラグを新設。有効にすると raw-bearing route は `404`、PII は除外。
  health-check や画面共有時に metadata-only モードを復元する安全弁。
- trace-detail page（agent-execution view）は raw body を inline 表示するため、
  既存の `GET /traces/{rawRecordId}/raw` と並ぶ **raw-bearing route** になる。
  raw-bearing route set の全 route で same-origin（`Origin` / `Sec-Fetch-Site` ⇒
  cross-site `403`）と `Cache-Control: no-store` を強制する。

不変（D020 / DR6 の cross-machine 防御）:

- loopback-only bind、`Host` header 検証。
- CORS 無効。state-changing action に CSRF + same-origin。
- raw / PII を log、repository-safe outputs、static dashboard、GitHub Pages snapshot に
  書かない。
- `/api/monitor/*` と SSE は sanitized metadata のみ（raw / PII を返さない）。
- captured content は escaped inert text で描画（framework 既定エンコード。`Html.Raw`
  不可）。追加の CSP / sanitizer / XSS payload-matrix 機構は設けない（ローカル単一利用者
  ツール。AGENTS.md Local-First Risk Posture 参照）。

受容リスクの拡張:

- raw / PII は起動フラグなしで loopback 上に到達可能（process 生存中ずっと）。
  単一利用者ローカルマシンのトレードオフとして product owner が受容。`--sanitized-only` は
  opt-out 安全弁。

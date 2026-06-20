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

## D002: Langfuse は local trace viewer として使う

Status: Accepted

ローカル Docker Desktop 上の Langfuse self-host を標準 trace viewer とする。
Clients は OTLP HTTP で `http://localhost:3000/api/public/otel` に直接送信できる。

Consequences:

- Langfuse credential は環境変数で扱い、repository に保存しない。
- Langfuse UI は個別 trace viewer として使うが、改善 loop の唯一の source of truth にはしない。

## D003: OTel Collector は任意の代替経路にする

Status: Accepted

Collector は直接送信を置き換えず、直接送信が不安定な場合や組織展開候補として使う。

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
- custom OTLP HTTP receiver。
- long-running local telemetry agent。

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

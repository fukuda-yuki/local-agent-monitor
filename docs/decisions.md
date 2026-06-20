# Decisions

この文書は、新しいリポジトリへ引き継ぐための軽量な decision log である。
Status は現時点の引き継ぎ判断を表す。

## D001: 公式 OpenTelemetry 出力を主入力にする

Status: Accepted

VS Code 内部ログ、workspaceStorage、chatSessions を主入力にしない。
GitHub Copilot Chat / GitHub Copilot CLI / Codex App が emit する OpenTelemetry signals を使う。

Rationale:

- client が公式に出す trace / metrics / events を扱う方が再現性と保守性が高い。
- VS Code Agent Debug / Chat Debug View は手動デバッグ機能として残し、本基盤では再実装しない。

## D002: Phase 1 baseline は Langfuse 直接送信にする

Status: Accepted

ローカル Docker Desktop 上の Langfuse self-host を baseline observability backend とする。
Clients は OTLP HTTP で `http://localhost:3000/api/public/otel` に直接送信する。

Rationale:

- LLM agent trace の調査に必要な span tree、prompt、response、tool call、token usage を確認しやすい。
- 初期 PoC では Grafana より Langfuse の方が目的に合う。

Consequences:

- Langfuse credential は環境変数で扱い、repository に保存しない。
- Langfuse UI は個別 trace viewer として使うが、改善 loop の唯一の source of truth にはしない。

## D003: OTel Collector は任意の代替経路にする

Status: Accepted

Collector は baseline を置き換えず、直接送信が不安定な場合や組織展開候補として使う。

Rationale:

- Collector は認証集約、fan-out、将来の masking / sampling に有用。
- 初期 PoC では構成を増やしすぎない方がよい。

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

Rationale:

- client、user、team、experiment で trace を分類できないと比較や dashboard が成立しない。

## D005: PoC では content capture を有効化する

Status: Accepted With Safety Boundary

PoC では prompt、response、system prompt、tool schema、tool arguments、tool results の取得を前提にする。

Safety boundary:

- raw content、credential、secret、Base64 authorization header、実 trace content、実 user identity を repository に保存しない。
- 実データ検証や共有環境では access control、retention、masking / redaction、利用者周知を先に決める。

## D006: Raw data loop は Langfuse UI に依存させない

Status: Accepted

Saved raw OTLP JSON から SQLite raw store と normalized dataset を作る。
Langfuse UI は trace viewer の optional side path として扱う。

Rationale:

- dashboard / measurement / candidate pipeline は再現可能な file-based input で検証できるべきである。

## D007: Raw store は SQLite を既定にする

Status: Accepted

Local PoC の raw store は SQLite とし、file-based ingest を使う。

Rejected for initial scope:

- PostgreSQL as primary raw telemetry store
- custom OTLP HTTP receiver
- long-running local telemetry agent

Rationale:

- local PoC と deterministic test には SQLite が十分である。
- 自前 receiver は security / operation surface を増やす。

## D008: Candidate pipeline は deterministic records までに留める

Status: Accepted

Trace から diagnosis candidate、improvement candidate、auto-decision record を生成する。
Existing human-review record との adapter / mapping compatibility を維持する。

Rejected for current scope:

- repository patch / diff generation
- file auto-modification
- commit / push / pull request automation
- automatic pass / fail judgment of improvement effect

Rationale:

- 改善判断の材料生成と repository 修正は安全境界が違う。

## D009: Dashboard の第一候補は static HTML + GitHub Pages にする

Status: Accepted

Sprint5 以降の常設 dashboard は GitHub Pages 向け static HTML を第一候補にする。
Grafana JSON dashboard は将来候補または fallback として残す。

Rationale:

- Enterprise Grafana は導入、認証、data source、運用調整が重い。
- Static HTML は初期 dashboard として運用面の負担が小さい。

Consequences:

- `generate-static-dashboard` は `index.html` と `dashboard-data.json` を生成する。
- No server-side API, runtime service, or network dependency.
- GitHub Actions が daily snapshot を publish する。

## D010: Dashboard は raw content を表示しない

Status: Accepted

Dashboard は aggregate metrics、status distribution、trend、percentile、reference id、classification attributes を扱う。

Do not display:

- raw prompt
- raw response
- system prompt
- tool arguments
- tool results
- source code fragments
- credentials or secrets
- sensitive bundle content or local path

Allowed with access control:

- `user.id`
- `user.email`
- `client.kind`
- `experiment.id`
- `agent.variant`
- `prompt.version`
- `skill.version`

Rationale:

- Dashboard は overview であり、trace detail viewer ではない。
- Raw content が必要な調査は Langfuse trace viewer、raw store、sensitive bundle へ drill down する。

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

- repository size monitoring
- GitHub Pages access control validation
- first live workflow result

## D012: Outcome linkage は future candidate に留める

Status: Accepted

GitHub / Notion / issue / PR 等の outcome linkage は将来候補として扱う。
External API ingestion、identity mapping、HR system correlation、org usage / ROI dashboard は初期 scope に含めない。

Tier policy:

- Tier 0: 初期 dashboard に含めない。
- Tier 1: sanitized / manual reference による future planning candidate。
- Tier 2: product / security decision 後のみ実装可能。
- Tier 3: 明示的な非目的。

## D013: Codex App の OTel config は user-level を source of truth にする

Status: Accepted

Codex App / app-server の OTel routing config は user-level `~/.codex/config.toml` に置く。
Project-local `.codex/config.toml` を OTel routing の source of truth として扱わない。

Rationale:

- Codex App は repository を開いていても user-level config を読む。

## D014: Aspire AppHost は orchestration surface にしない

Status: Accepted

Aspire AppHost は Phase 0 背景として維持する。
現在は空であり、resource は登録しない。

Do not add by default:

- Langfuse
- OTel Collector
- Config CLI
- ServiceDefaults
- Web app
- DB / Redis / Worker

Rationale:

- Phase 1 以降の主構成は Langfuse Docker Compose と Config CLI である。
- AppHost に存在しない runtime resource を推測で追加しない。

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

- access control
- retention
- deletion process
- masking / redaction
- user notice or consent
- identity handling
- Pages visibility
- live workflow operation
- snapshot growth monitoring

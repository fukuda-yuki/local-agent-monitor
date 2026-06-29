# Requirements

この文書は Copilot Agent Observability の製品要件を定義する。
詳細な実装仕様は [docs/spec.md](spec.md) と [docs/specifications/](specifications/) を参照する。

## 1. 目的

Copilot Agent Observability は、GitHub Copilot Chat、GitHub Copilot CLI、Codex App から出力される OpenTelemetry data を収集し、Agent workflow の挙動を trace 単位と集計単位の両方で確認できる状態を作る。

利用者が判断できるようにするもの:

- agent invocation、LLM call、tool call、permission、file / shell operation の実行過程。
- prompt、response、tool arguments、tool results、token usage、duration、error の trace-level 調査。
- VS Code GitHub Copilot Chat、GitHub Copilot CLI、Codex App の挙動差分。
- baseline、variant、experiment、task ごとの比較。
- raw telemetry から normalized dataset、diagnosis candidate、improvement candidate、auto-decision record、dashboard dataset を再現可能に生成する流れ。
- Agent workflow の健全性、失敗傾向、コスト見積もり、改善候補を俯瞰する static dashboard。

## 2. 利用者

主な利用者:

- Copilot agent workflow の挙動を調査する開発者。
- prompt、skill、MCP、CLI wrapper の改善効果を比較する実装者。
- trace 由来の失敗傾向や改善候補を確認する maintainer。
- dashboard snapshot を確認する reviewer。

対象外の利用者像:

- 個人別の勤務評価やランキングを作りたい管理者。
- Copilot seat / billing / adoption analytics を管理したい管理者。
- DLP、監査ログ、機密情報検査の本番基盤を求める管理者。

## 3. 機能範囲

必須機能:

- VS Code GitHub Copilot Chat の OTel trace / metrics / events 収集。
- GitHub Copilot CLI の OTel trace / metrics 収集。
- collection profile による telemetry routing mode の明示的な切り替え。
- raw-only minimum profile。Langfuse、Docker Desktop、WSL2 Docker Engine、Collector、remote endpoint、background process なしで saved raw OTLP JSON から raw data loop を実行できること。
- Docker Desktop + Langfuse standard full profile。ローカル Langfuse trace viewer による個別 trace review と raw data loop の両方を扱えること。
- Docker Desktop + Collector + Langfuse profile。
- WSL2 Docker Engine + Langfuse profile。
- WSL2 Docker Engine + Collector + Langfuse profile。
- remote managed Langfuse profile。
- remote managed Collector profile。
- repository-hosted raw local receiver profile。Langfuse なしで VS Code からこの repository の local receiver へ telemetry を送信し、raw data loop に接続できること。
- Local Ingestion Monitor。VS Code GitHub Copilot Chat から OTLP HTTP/protobuf を直接受信し、SQLite raw store に永続化し、loopback-only のローカル UI で取り込みの健全性（受信、永続化、projection、エラー有無、health / readiness）を確認できること。さらに、受信済み OTel テレメトリから per-span の sanitized projection を生成し、agent-execution view として、どのツール / MCP を呼び出したか（名前単位）、各呼び出しの成否、sub-agent のモデル / トークン使用量、turn 単位のトークン合計を表示できること。raw body（tool call arguments / results、sub-agent instructions / responses、system prompt）と PII（`user.id` / `user.email`）は既定で表示する（server-rendered、inert text）。`--sanitized-only` フラグで metadata-only モードを復元し、raw section / full raw route を除外して PII を除外できること。ただし sanitized な TraceDetail tab shell は `--sanitized-only` でも利用できること。さらに、この sanitized span projection を、graphical Flow Chart（span 階層の DAG 表示）、Cache Explorer（trace 内の cache-hit rate / token breakdown）、timeline filter/sort（errors-only filter、tokens / time sort）、VS Code 風テーマの UI として client-side で提示できること。これらは既存 spans API（`GET /api/monitor/traces/{traceId}/spans`）上の **presentation のみ**であり、新たな telemetry 入力・schema・API field・raw 境界変更を伴わない。
- Langfuse による個別 trace viewer。ただし Langfuse は standard full profile の viewer であり、raw-only minimum profile の必須要素ではない。
- saved raw OTLP JSON の file-based ingest。
- SQLite raw store。
- raw store から normalized measurement dataset への変換。
- deterministic CLI による diagnosis / improvement / auto-decision candidate generation。
- static HTML dashboard と dashboard dataset generation。
- GitHub Actions による static dashboard snapshot generation。

任意機能:

- Codex App / app-server の OTel trace / logs / metrics 収集。
- GitHub Copilot app Canvas adapter。Local Ingestion Monitor の既存 sanitized
  monitor context を Copilot app side panel から参照する任意統合として扱う。
  Canvas-safe posture は Local Monitor を `--sanitized-only` で起動することとし、
  Canvas adapter は既存 `/api/monitor/*` と sanitized TraceDetail tab shell の
  範囲だけを扱う。
- Grafana JSON dashboard fallback。

参考のみ:

- Claude Code の observability 事例。
- Visual Studio 系 client。
- GitHub / Notion / issue / PR 等の external outcome linkage。

## 4. 非目的

本製品では以下を扱わない。

- Copilot の利用者数、利用回数、日次アクティブユーザーの集計。
- 個人別の生産性評価、勤務監視、ランキング。
- 経営向け利用状況 dashboard、課金、コスト配賦。
- DLP、機密情報検査、監査ログ基盤。
- VS Code 内部ログ、workspaceStorage、chatSessions を入力ソースにした解析、および VS Code の in-editor Debug UI の複製。ただし受信済み OTel テレメトリから導出する sanitized agent-execution view は許可する（D021）。
- Langfuse / Collector / Grafana / Pages の共有運用決定。
- remote managed Langfuse / Collector の利用者同意 workflow。
- trace から repository patch / diff を生成すること。
- repository file の自動修正。
- commit / push / pull request の自動作成。
- 改善効果の自動合否判定。
- GitHub / Notion / HR system との本番 ETL。
- Local Ingestion Monitor への Digital Agency Design System（DADS）適用（D027。Monitor は VS Code 慣習に従う開発者向けツール。Static Dashboard は対象外）。
- Cache Explorer での raw prompt body の prefix-diff、および `conversation_id` による cross-trace stitching（D026。前者は raw-bearing route を増やすため、後者は API 変更を要するため）。
- GitHub Copilot app Canvas adapter で Local Monitor UI を再実装すること、新たな telemetry input / schema / API field / raw endpoint を追加すること、raw prompt / response body、tool arguments / results、PII、credential、token、local sensitive path を Copilot actions へ返すこと。

## 5. Data Requirements

収集対象:

- trace / span / span attributes / span events。
- metrics / events。
- prompt content。
- response content。
- system prompt。
- tool schema。
- tool arguments。
- tool results。
- token usage。
- model information。
- duration。
- error information。
- session id / run id。
- user id / user email。
- team id / department。
- client kind。
- experiment id / experiment condition。

Span 名は client 実装や version により変化し得るため、特定 span 名だけには依存しない。
正規化後は、agent invocation、LLM call、tool call、permission / approval、file operation、shell command、error、user interaction などの論理カテゴリで扱う。

## 6. Resource Attributes

必須 Resource Attributes:

```text
user.id
user.email
team.id
department
client.kind
experiment.id
```

`client.kind` の推奨値:

```text
vscode-copilot-chat
copilot-cli
codex-app
```

推奨 Resource Attributes:

```text
repo.name
workspace.name
task.id
task.category
task.run_index
experiment.condition
prompt.version
repo.snapshot
agent.variant
skill.version
mcp.profile
cli.wrapper.version
```

## 7. Collection Profile Requirements

Collection profile は telemetry routing mode を表す public interface とする。

Profile selector:

```text
CAO_COLLECTION_PROFILE
```

必須 profile:

| Profile | 要件 |
| --- | --- |
| `raw-only` | 最小必須 profile。保存済み raw OTLP JSON を入力にし、Langfuse / Docker / Collector / remote endpoint / background process なしで raw data loop を実行する。 |
| `docker-desktop-langfuse` | 標準 full profile。Docker Desktop 上の local Langfuse へ OTLP HTTP で送信し、live trace review と raw data loop を接続する。 |
| `docker-desktop-collector-langfuse` | Docker Desktop 上の Collector へ送信し、Collector から Langfuse へ relay する。 |
| `wsl2-docker-langfuse` | WSL2 上の Docker Engine で動く Langfuse へ Windows client から送信する。 |
| `wsl2-docker-collector-langfuse` | WSL2 上の Docker Engine で動く Collector へ Windows client から送信し、Collector から Langfuse へ relay する。 |
| `remote-managed-langfuse` | 管理された remote Langfuse endpoint へ送信する。 |
| `remote-managed-collector` | 管理された remote Collector endpoint へ送信する。 |
| `raw-local-receiver` | この repository が提供する local receiver へ VS Code から直接 telemetry を送信し、raw data loop に接続する。 |

Profile 差分は collection / routing / live viewer availability の違いとして扱う。
Profile により raw store schema、normalized measurement schema、candidate schema、dashboard dataset schema を分岐させてはならない。

`remote-managed-langfuse` と `remote-managed-collector` は、本 repository では WARNING と placeholder configuration までを扱う。
remote managed endpoint へ送信する前に、access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を別 decision として決める。

## 8. Data Safety Requirements

Repository に保存してよいもの:

- synthetic fixture。
- redacted summary。
- normalized aggregate dataset。
- sanitized dashboard dataset。
- trace id / candidate id / evidence ref 等の参照 ID。
- 実データ由来の aggregate metrics。
- `user.id` / `user.email` を含む分類属性。ただし共有・公開前に access control を確認すること。

Repository に保存してはならないもの:

- raw prompt / raw response。
- system prompt の全文。
- tool arguments / tool results の全文。
- observed session 由来の source code fragment / file contents。
- credential、secret、token、API key、password。
- Base64 authorization header。
- sensitive bundle content。
- sensitive bundle local path。

Local Ingestion Monitor の raw / PII 表示は loopback-only の runtime surface であり、ここで定義する repository 保存禁止や §9 の static dashboard 非表示とは別物である。raw body（tool call arguments / results、sub-agent instructions / responses、system prompt）と PII（`user.id` / `user.email`）は **既定で表示する**（server-rendered、inert text）。`--sanitized-only` フラグで metadata-only モードを復元し、TraceDetail の raw section と full raw route を除外して PII を除外できる（D023 / D030）。`--sanitized-only` でも TraceDetail の sanitized tab shell は表示される。既定・`--sanitized-only` いずれの場合も raw / PII を repository-safe outputs、static dashboard、ログ、GitHub Pages snapshot へ出力してはならない。`/api/monitor/*` と SSE は常に sanitized metadata のみを返す。この表示は単一のローカル利用者が自分のデータを閲覧する用途に限り、cross-machine な露出（remote / non-loopback、browser 経由の off-machine 送出）から防御する。

GitHub Copilot app Canvas adapter は Local Monitor の任意表示統合であり、Canvas actions と Canvas 表示は sanitized data boundary の外へ出てはならない。Canvas adapter を使う場合は Local Monitor を `--sanitized-only` で起動することを Canvas-safe posture とし、adapter は既存 sanitized `/api/monitor/*` API と sanitized TraceDetail tab shell だけを読む。Canvas action responses、logs、committed outputs には raw prompt / response body、tool arguments / results、PII、credential、token、local sensitive path、raw OTLP payload を含めてはならない。

共有環境、実データ、社内サーバー、GitHub Pages publish を扱う場合は、アクセス権、保持期間、削除方法、masking / redaction、利用者周知を先に決める。
remote managed Langfuse / Collector endpoint を使う場合は、送信前に access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を確認する。

## 9. Dashboard Requirements

Static HTML dashboard は Agent workflow 改善判断のための aggregate view とする。
個別 trace の詳細調査は Langfuse trace viewer、raw store、または明示 opt-in の sensitive bundle へ drill down する。

初期 view:

- Run Overview。
- Agent / Tool Behavior。
- Prompt / Skill / Instructions。
- Baseline vs Variant。
- Diagnosis / Improvement Loop。
- Collection Health。
- Outcome Linkage Candidate。

初期 client-side interaction:

- filter。
- sort。
- search。

初期 filter 軸:

- date。
- user。
- client。
- experiment。
- variant。
- status。

Dashboard に raw prompt / response / tool arguments / tool results の全文を表示してはならない。
`user.id` と `user.email` は表示および filter / search 対象に含めてよいが、publish 先の access control を先に確認する。

## 10. Validation Requirements

Code、project file、CLI behavior、workflow を変更した場合は以下を実行する。

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

`dotnet test CopilotAgentObservability.slnx` includes Local Ingestion Monitor
Playwright smoke tests. The browser install step is therefore part of the
required validation bootstrap and installs browser binaries outside tracked
source.

Collector example を変更した場合は、実 credential ではなく dummy `LANGFUSE_AUTH` で Compose 構文を確認する。

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

Copilot 実行に依存する挙動は自動テストだけで保証しない。
live validation では、確認日時、実行環境、設定値、trace id または識別情報、確認項目、未確認項目を記録する。
Docker Desktop、WSL2 Docker Engine、remote managed endpoint、raw local receiver の各 profile は、それぞれ profile value、client kind、endpoint、trace id または raw record identifier を live validation evidence に含める。

## 11. Open Product Decisions

以下は実装前または共有運用前に決める。

- GitHub Pages access control の具体設定。
- 初回 live workflow 実行結果の確認。
- 日次 snapshot の repository size monitoring。
- email / display name mapping。
- shared dashboard の access control、retention、利用者周知。
- external outcome linkage の採否。
- 実 GitHub / Notion ingestion の product / security decision。
- 実データを扱う場合の masking / redaction 方針。
- remote managed Langfuse / Collector の利用者同意 workflow。

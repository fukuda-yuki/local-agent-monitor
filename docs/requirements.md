# Requirements

## 1. 目的

本基盤の目的は、GitHub Copilot Chat / GitHub Copilot CLI / 任意の Codex App から出力される OpenTelemetry data を収集し、Agent / MCP / Skills / CLI の挙動を trace 単位で確認、集計、比較できる状態を作ることである。

本基盤は、以下の判断材料を提供する。

- agent invocation、LLM call、tool call、permission、file / shell operation の実行過程
- prompt、response、tool arguments、tool results、token usage、duration、error の確認
- VS Code GitHub Copilot Chat と GitHub Copilot CLI の挙動差分
- baseline / variant / experiment / task ごとの trace 分類と比較
- raw telemetry から normalized dataset、diagnosis candidate、improvement candidate、auto-decision record、dashboard dataset を再現可能に生成する流れ
- Agent workflow の健全性、失敗傾向、コスト見積もり、改善候補を俯瞰する dashboard

## 2. 対象範囲

必須対象:

- VS Code GitHub Copilot Chat の OpenTelemetry trace / metrics / events
- GitHub Copilot CLI の OpenTelemetry trace / metrics
- Langfuse による個別 trace viewer
- saved raw OTLP JSON の file-based ingest
- SQLite raw store
- raw store から normalized measurement dataset への変換
- deterministic CLI による diagnosis / improvement / auto-decision candidate generation
- static HTML dashboard と dashboard dataset

任意対象:

- Codex App / app-server の OpenTelemetry trace / logs / metrics
- ローカル OpenTelemetry Collector 経由送信
- Grafana JSON dashboard fallback

参考のみ:

- Claude Code の observability 事例
- Visual Studio 系 client
- GitHub / Notion / issue / PR 等の external outcome linkage

## 3. 非目的

本基盤では以下を扱わない。

- Copilot の利用者数、利用回数、日次アクティブユーザーの集計
- 個人別の生産性評価、勤務監視、ランキング
- 経営向け利用状況 dashboard、課金、コスト配賦
- DLP、機密情報検査、監査ログ基盤
- VS Code Agent Debug / Chat Debug View 相当の UI
- VS Code 内部ログ、workspaceStorage、chatSessions を主入力にした解析
- Langfuse / Collector / Grafana / Pages の本番運用決定
- trace から repository patch / diff を生成すること
- repository file の自動修正
- commit / push / pull request の自動作成
- 改善効果の自動合否判定
- GitHub / Notion / HR system との本番 ETL

## 4. 収集要件

PoC では詳細分析を優先し、content capture を有効化する。

収集対象:

- trace / span / span attributes / span events
- metrics / events
- prompt content
- response content
- system prompt
- tool schema
- tool arguments
- tool results
- token usage
- model information
- duration
- error information
- session id / run id
- user id / user email
- team id / department
- client kind
- experiment id / experiment condition

Span 名は client 実装や version により変化し得るため、特定 span 名だけには依存しない。
正規化後は、agent invocation、LLM call、tool call、permission / approval、file operation、shell command、error、user interaction などの論理カテゴリで扱う。

## 5. Resource Attributes

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

研究計測や baseline / variant 比較では、`task.id`、`task.category`、`task.run_index`、`experiment.condition`、`prompt.version`、`agent.variant`、`repo.snapshot` を可能な限り付与する。

## 6. Data And Security Requirements

Repository に保存してよいもの:

- synthetic fixture
- redacted summary
- normalized aggregate dataset
- sanitized dashboard dataset
- trace id / candidate id / evidence ref 等の参照 ID
- 実データ由来の aggregate metrics
- `user.id` / `user.email` を含む分類属性。ただし公開先 access control を確認すること

Repository に保存してはならないもの:

- raw prompt / raw response
- system prompt の全文
- tool arguments / tool results の全文
- source code fragment / file contents from observed sessions
- credential、secret、token、API key、password
- Base64 authorization header
- sensitive bundle content
- sensitive bundle local path

PoC の保持期間目安:

| Data | Retention |
| --- | ---: |
| prompt / response / tool arguments / tool results | 30 days |
| full trace | 30 days |
| span metadata | 90 days |
| aggregate metrics | 1 year |
| analysis notes | manual deletion or separately defined |

共有環境、実データ、社内サーバー、GitHub Pages publish を扱う場合は、アクセス権、保持期間、削除方法、masking / redaction、利用者周知を先に決める。

## 7. Dashboard Requirements

Static HTML dashboard は Agent workflow 改善判断のための aggregate view とする。
個別 trace の詳細調査は Langfuse trace viewer、raw store、または明示 opt-in の sensitive bundle へ drill down する。

初期 view:

- Run Overview
- Agent / Tool Behavior
- Prompt / Skill / Instructions
- Baseline vs Variant
- Diagnosis / Improvement Loop
- Collection Health
- Outcome Linkage Candidate

初期 client-side interaction:

- filter
- sort
- search

初期 filter 軸:

- date
- user
- client
- experiment
- variant
- status

Dashboard に raw prompt / response / tool arguments / tool results の全文を表示してはならない。
`user.id` と `user.email` は表示および filter / search 対象に含めてよいが、publish 先の access control を先に確認する。

## 8. Validation Requirements

コード、project file、CLI behavior、workflow を変更した場合は以下を実行する。

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Collector example を変更した場合は、実 credential ではなく dummy `LANGFUSE_AUTH` で Compose 構文を確認する。

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

Copilot 実行に依存する挙動は自動テストだけで保証しない。
live validation では、確認日時、実行環境、設定値、trace id または識別情報、確認項目、未確認項目を記録する。

## 9. Carry-over Open Items

新しいリポジトリへ持ち越す未決事項:

- GitHub Pages access control の具体設定
- 初回 live workflow 実行結果の確認
- 日次 snapshot の repository size monitoring
- email / display name mapping
- shared dashboard の access control、retention、利用者周知
- external outcome linkage の Tier 1 以降の採否
- 実 GitHub / Notion ingestion の product / security decision
- 実データを扱う場合の masking / redaction 方針

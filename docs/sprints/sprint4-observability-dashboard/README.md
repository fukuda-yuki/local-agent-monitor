# Sprint4: Agent Workflow Observability Dashboard

Sprint4 は、既存の OTel 収集、Langfuse trace viewer、raw telemetry store、normalized measurement、Sprint3 candidate pipeline を前提に、Agent workflow 改善判断に使う dashboard 要件を定義する。

この sprint の dashboard は、組織全体の Copilot 利用状況監視や個人別の生産性評価ではない。
目的は、Agent / MCP / Skills / CLI の実行傾向、失敗傾向、コスト、改善候補を俯瞰し、trace review と human review の優先順位を決めることである。

## Source Material

- `docs/requirements.md`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint3-trace-diagnosis/`
- Speaker Deck: 価格.comをAI駆動で全面刷新する slide 22
- Docswell: Claude Code / Codex の全社展開とAI観測基盤の設計 page 12 and related pages
- [Microsoft Learn: Monitor AI coding agents with Grafana](https://learn.microsoft.com/en-us/azure/managed-grafana/grafana-opentelemetry-app-insights)
- `C:\Users\mwam0\Documents\deep-research-report.md`

## Goals

- dashboard の目的、非目的、利用者、意思決定対象を定義する。
- Run Overview、Agent / Tool Behavior、Prompt / Skill / Instructions、Baseline vs Variant、Diagnosis / Improvement Loop、Collection Health、Outcome Linkage Candidate の初期 view set を定義する。
- 各 view の panel、metric、dimension、filter、drilldown 先を定義する。
- normalized measurement、diagnosis candidate、improvement candidate、auto-decision record から dashboard dataset を作るための contract を定義する。
- raw prompt / response / tool arguments / tool results を dashboard dataset に既定保存しない安全境界を定義する。
- Grafana 等の可視化基盤を採用する場合の最小 data contract を定義する。
- TTFT、trace duration、tool duration、retry、subagent / nested agent、approval、stuck session を分けて観測する要件を定義する。
- 集計 dashboard と Langfuse trace viewer / sensitive bundle への drilldown の責務分担を定義する。
- M4 prototype path では Grafana-first dashboard + Langfuse drilldown を第一候補として比較する。

## Users and Decisions

Primary users:

- Agent / MCP / Skills / CLI improvement owners.
- Developer experience / platform engineering owners.
- Baseline / variant measurement and evaluation owners.
- Sprint3 candidate pipeline human reviewers.

Decision targets:

- Which trace / workflow / task should be reviewed first.
- Which tool / MCP / CLI operation should be investigated as an improvement candidate.
- Which diagnosis / improvement / auto-decision candidate should go to human review.
- Which baseline / variant difference should be treated as a regression candidate.
- Which collection / normalization / candidate generation gap should be fixed first.
- Which M4 prototype path should be selected.

## Non-goals

- 独自 Web UI の実装。
- Grafana Cloud / Tempo / Loki / Mimir / Datadog / New Relic の本番採用決定。
- 組織全体の Copilot 利用量把握。
- 利用者数、利用回数、日次アクティブユーザーの集計。
- 個人別の生産性評価、勤務監視、ランキング。
- 経営向け利用状況 dashboard。
- raw prompt / response / tool arguments / tool results の一覧表示。
- GitHub / Notion / HR system との本番連携。
- 外部 API 用 ETL、認証、個人 identity mapping の実装。
- repository file 自動修正、patch / diff 生成、commit / push / pull request 作成。

## Milestones

| Milestone | Status | Scope |
| --- | --- | --- |
| [M1: dashboard requirements](milestones/M1-dashboard-requirements/task.md) | complete | dashboard の目的、非目的、view set、metric inventory、dimension / filter、drilldown、data source 境界を確定する |
| [M2: dashboard dataset contract](milestones/M2-dashboard-dataset-contract/task.md) | complete | normalized measurement / candidate outputs から dashboard dataset を生成する CSV / JSON schema を定義した |
| [M3: synthetic dashboard data](milestones/M3-synthetic-dashboard-data/task.md) | complete | synthetic fixture から dashboard dataset を生成し、metric 欠損と PII 非混入を確認した |
| [M4: dashboard prototype path](milestones/M4-dashboard-prototype-path/task.md) | complete | Grafana JSON dashboard を第一候補とし、static report、repository-local preview と比較した |
| [M5: review and handoff](milestones/M5-review-and-handoff/task.md) | complete | Sprint4 の要件と prototype 方針を review し、Sprint5 以降の実装範囲を分離した |

## Initial View Set

| View | Primary questions |
| --- | --- |
| Run Overview | 実行数、成功 / 失敗、duration、TTFT、token、estimated cost、LLM call、tool call、stuck session がどう推移しているか |
| Agent / Tool Behavior | どの tool / MCP / CLI 操作で error、timeout、遅延、反復呼び出し、retry、approval 待ち、subagent 待ちが起きているか |
| Prompt / Skill / Instructions | prompt version、skill version、agent variant の変更が token、duration、tool call、error、candidate distribution に影響しているか |
| Baseline vs Variant | baseline と variant を同じ task / client / repo snapshot で success、duration、TTFT、token、tool failure、candidate count により比較できるか |
| Diagnosis / Improvement Loop | diagnosis candidate、improvement candidate、auto-decision、human-review backlog がどこに溜まっているか |
| Collection Health | OTel、Resource Attributes、normalization、candidate generation の欠損や失敗を検知できるか |
| Outcome Linkage Candidate | GitHub / Notion / issue / PR 等の成果側指標を将来どう並べるか |

## Initial Panel Set

| View | Panels |
| --- | --- |
| Run Overview | Run volume and status、Latency distribution、Token and cost trend、Stuck and long-running runs |
| Agent / Tool Behavior | Top tools by count、Top tools by total duration、Tool reliability、Subagent and approval waits |
| Prompt / Skill / Instructions | Variant cost and token impact、Variant failure and candidate impact |
| Baseline vs Variant | Matched task comparison、Regression candidate list |
| Diagnosis / Improvement Loop | Candidate distribution、Human review queue |
| Collection Health | Attribute completeness、Normalization and mapping health |
| Outcome Linkage Candidate | External outcome placeholders |

tool 改善の優先度は call count だけでは決めない。
count、total duration、error、retry、approval wait、subagent wait、estimated cost を分け、回数が多いだけの操作と、実際に時間・失敗・費用を押し上げている操作を区別する。

## Safety Boundary

dashboard dataset は集計と参照を主とし、raw content の保存場所にしない。
詳細調査は Langfuse trace id、OTel trace id、candidate id、auto-decision id、evidence ref から既存 trace viewer や sensitive bundle へ移動する。

共有環境、実データ、社内サーバー検証、個人識別属性の dashboard 表示が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。

利用量、アクティブユーザー数、チーム別コスト、ROI、品質・安全性評価、GitHub / Notion / issue / PR 等の成果接続は将来候補として扱う。
Sprint4 初期 dashboard は、Agent workflow 改善判断に必要な aggregate / sanitized 指標と drilldown reference に限定する。

## Prototype Direction

M4 では Grafana-first dashboard prototype を第一候補として扱う。
Grafana は Run Overview、Agent / Tool Behavior、Baseline vs Variant、Collection Health などの aggregate / percentile / status distribution 表示に使う。

Langfuse は置き換えない。
Grafana panel は sanitized reference を持ち、個別 trace の span tree、prompt / response、tool arguments / results、token usage、error は Langfuse trace viewer、raw store、sensitive bundle へ drilldown して調査する。

Grafana Cloud、Azure Managed Grafana、self-host Grafana、Application Insights、Tempo、Loki、Mimir、Azure Monitor Exporter、OTel Collector の本番採用は Sprint4 M1 では決めない。

## M2 Dataset Contract

M2 では [dashboard-dataset-contract.md](milestones/M2-dashboard-dataset-contract/dashboard-dataset-contract.md) で、Grafana-first prototype と static report が共有する dashboard dataset contract を定義した。
dataset は `dashboard_run_summary`、`dashboard_operation_summary`、`dashboard_candidate_summary`、`dashboard_collection_health` の 4 logical table とし、raw prompt / response / tool arguments / tool results、credential、secret、Base64 header、実 user identity は保存しない。

TTFT は直接属性がある場合のみ使用し、ない場合は first generation event / span からの fallback 導出または null とする。
estimated cost は token 数と model 別 unit price table による観測用概算であり、実課金額として扱わない。
time bucket の既定粒度は `day`、long-running / stuck 判定は M2 contract の既定閾値を初期値とする。
`client_kind=codex-app` は任意 source の予約値であり、M2 / M3 の必須 fixture 対象にはしない。

## M3 Synthetic Dataset Generator

M3 では `generate-dashboard-dataset` を追加し、normalized measurement、raw OTLP synthetic fixture、diagnosis candidate、improvement candidate、auto-decision record から M2 の 4 logical table を生成できることを確認した。
JSON output は単一 object、CSV output は table ごとの file とする。

```text
config-cli generate-dashboard-dataset <measurements.csv|measurements.json>
  [--raw <raw-store.db|raw-otlp.json>]
  [--diagnosis-candidates <input.csv|input.json>]
  [--improvement-candidates <input.csv|input.json>]
  [--auto-decisions <input.csv|input.json>]
  [--time-bucket <day|hour|week>]
  [--csv-dir <output-dir>]
  [--json <output.json>]
```

M3 generator は TTFT fallback、estimated cost、operation timing、retry、approval wait、subagent wait、stuck / long-running flags、candidate status distribution、collection health を synthetic data で検証する。
backlog age は既存 candidate schema に generated timestamp がないため nullable とし、M3 では null が dashboard generation を阻害しないことを確認した。
Grafana JSON dashboard、static report、repository-local preview の選定は M4 で完了した。

## M4 Dashboard Prototype Path

M4 では [prototype-path.md](milestones/M4-dashboard-prototype-path/prototype-path.md) で、Grafana JSON dashboard、static report、repository-local preview を同じ基準で比較する。
既定の prototype path は Grafana JSON dashboard とし、M3 synthetic dashboard dataset を初期検証入力にする。

Static report は Grafana data source が準備できない場合の deterministic fallback として維持する。
Repository-local preview は最小の視覚確認候補に留め、独自 trace viewer や product UI には広げない。

## M5 Review and Handoff

M5 では [task.md](milestones/M5-review-and-handoff/task.md) に従い、Sprint4 の要件、dataset contract、synthetic dataset generator、prototype path decision を review した。
handoff では Grafana JSON dashboard 実装、static report fallback、repository-local preview の扱い、Outcome Linkage Candidate の Tier 分けを Sprint5 以降の候補として分離した。

M5 review は [review.md](milestones/M5-review-and-handoff/review.md) に記録した。
Sprint5 以降の第一候補は Grafana JSON dashboard artifact と synthetic dashboard data による import / data source validation workflow である。
Static report は deterministic fallback、repository-local preview は Grafana setup が基本的な視覚確認を阻害する場合の last-resort visual aid として扱う。
Outcome Linkage Candidate は placeholder / future planning candidate に留め、外部 API 連携、identity mapping、組織利用状況 dashboard は別途 product / security decision があるまで実装しない。

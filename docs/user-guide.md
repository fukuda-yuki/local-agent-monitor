# 利用者向け詳細ガイド

このガイドは Copilot Agent Observability を使う人向けの入口です。
最短手順だけを確認したい場合は [Getting Started](getting-started.md) を参照してください。

## 何を確認できるか

Copilot Agent Observability は、Copilot agent workflow の実行過程を次の単位で確認します。

- Collection profile: `CAO_COLLECTION_PROFILE` で telemetry routing mode を選ぶ。
- 個別 trace: Langfuse で span tree、prompt、response、tool call、token usage、duration、error を調査する。
- Raw data loop: saved raw OTLP JSON を SQLite raw store に取り込み、normalized measurement dataset にする。
- Candidate loop: normalized dataset から diagnosis / improvement / auto-decision / human decision record を作る。
- Static dashboard: Agent workflow の傾向を filter、search、sort できる HTML dashboard として見る。

## 読む順番

1. [Data Safety](user-guide/data-safety.md)
2. [Telemetry Collection](user-guide/telemetry-collection.md)
3. [Local Ingestion Monitor](user-guide/local-monitor.md)
4. [Raw Data Loop](user-guide/raw-data-loop.md)
5. [Static Dashboard](user-guide/static-dashboard.md)
6. [Diagnosis / Improvement Loop](user-guide/diagnosis-improvement-loop.md)

## 利用モード

最小 profile は `raw-only`、標準 full profile は `docker-desktop-langfuse` です。
Profile の一覧は [collection profile specification](specifications/interfaces/collection-profiles.md) を参照してください。

| Mode | 使う場面 | 主要コマンド / UI |
| --- | --- | --- |
| Live Trace Review | 1 件の agent run を詳しく調べる | Langfuse UI, `langfuse-*` config commands |
| Raw Data Loop | trace を再現可能に集計したい | `ingest-raw`, `normalize-raw` |
| Static Dashboard | 複数 run の傾向を俯瞰したい | `generate-dashboard-dataset`, `generate-static-dashboard` |
| Diagnosis / Improvement Support | 失敗傾向と改善候補を整理したい | `generate-diagnosis-candidates`, `generate-improvement-candidates`, `generate-auto-decisions` |

## 最小の安全ルール

- 実 credential、secret、Base64 authorization header を repository に保存しない。
- raw prompt、raw response、tool arguments、tool results、source code fragment を commit しない。
- `data\`、`tmp\`、`artifacts/dashboard-input/*.json` は local runtime data として扱う。
- GitHub Pages や共有場所に publish する前に、access control、retention、削除方法、利用者周知を確認する。
- remote managed Langfuse / Collector endpoint に送信する前に、access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を決める。

## 画像で見る Static Dashboard

<p align="center">
  <img width="900" alt="Static dashboard overview" src="./assets/screenshots/static-dashboard-overview.png">
</p>

Dashboard は `dashboard-data.json` を browser で読み込み、client-side で filter / search / sort します。
server-side API は不要です。

<p align="center">
  <img width="900" alt="Static dashboard filters" src="./assets/screenshots/static-dashboard-filters.png">
</p>

## 次の一歩

まず synthetic fixture だけで dashboard を生成してください。
外部サービスも実データも不要です。

```powershell
New-Item -ItemType Directory -Force tmp\dashboard-demo | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site
```

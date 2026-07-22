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
| Sanitized Evidence Sharing | scanner 検証済みの evidence bundle を共有したい | `sanitized-export preview`, `sanitized-export export`, `sanitized-export result` |
| Sanitized Evidence Import | scanner 検証済みの evidence bundle をローカルへ取り込みたい | `sanitized-import preview`, `sanitized-import import`, `sanitized-import history`, Local Monitor `/sanitized-import` |

Sanitized Evidence Sharing の `preview` / `export` では、既存 Local Monitor
database と `sanitized-export-control.v1` request を明示します。request に
snapshot、record bytes、output path は含めません。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export preview --database data\local-monitor.db --request request.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export export --database data\local-monitor.db --request request.json --output bundle.zip
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-export result --bundle bundle.zip
```

`result` は archive の canonical carrier、inventory、checksum を検証しますが、
artifact の origin / provenance を証明するものではありません。

受け取った bundle は、まず現在の取り込み状態に対する preview を確認してから、
返された `preview_digest` を指定して明示的に取り込みます。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import preview --database data\local-monitor.db --bundle bundle.zip
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import import --database data\local-monitor.db --bundle bundle.zip --preview-digest <preview_digest>
dotnet run --project src\CopilotAgentObservability.ConfigCli -- sanitized-import history --database data\local-monitor.db --limit 20
```

同じ操作は Local Monitor の `/sanitized-import` でも行えます。取り込み対象は
#58 / #59 / #80 の frozen sanitized carrier だけで、raw telemetry、Session、
alert lifecycle、backup は復元しません。構造検証の成功は bundle の内部整合性を
示しますが、作成者、署名、権限、source store provenance を証明しません。

## Claude Code の guided setup

Claude Code 2.1.207 以上では、対象にする Claude project の root へ移動してから、設定値や path そのものを含まない redacted な member state、operation、対象 label を表示する plan を作成します。setup は実行 directory 直下の `.claude/settings.local.json` と `.claude/settings.json` だけを確認し、親 directory、子 directory、Git root、`--add-dir` の project は探索しません。plan だけでは設定を書き換えません。

```powershell
pwsh scripts\local-monitor\setup.ps1 plan --adapter claude-code --target cli
```

interactive CLI と `claude -p` は同じ user settings を使います。既定 plan は OTel の prompt / tool content gate を変更しませんが、mapper 対応済み Hook は raw-bearing event を取得し得るため、`claude_hooks_capture_raw_content` warning を確認してください。OTel content gate も明示的に有効化する場合だけ `--include-content-capture` を追加します。

WSL2 から実行する場合は、WSL 内の process から Local Monitor の loopback readiness に到達できることを確認し、`--allow-wsl2-routing` を明示します。Windows native ではこの option を指定しません。gateway / non-loopback fallback はありません。Agent SDK は `--target app-sdk` で Python / TypeScript の caller-managed guidance を確認できますが、setup はアプリケーションコードを書き換えません。plan 後の apply / rollback は返された change-set ID を使います。setup 完了は static configuration の確認であり、first real trace / Doctor の成功を意味しません。

## 最小の安全ルール

- 実 credential、secret、Base64 authorization header を repository に保存しない。
- raw prompt、raw response、tool arguments、tool results、source code fragment を commit しない。
- `data\`、`tmp\`、`artifacts/dashboard-input/*.json` は local runtime data として扱う。
- 生成済み dashboard artifact を共有場所に置く前に、access control、retention、削除方法、利用者周知を確認する。
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

# Static Dashboard

Static Dashboard は、収集したトレースデータの概要を一覧で確認するための静的な HTML 画面です。
個別トレースの詳細は Langfuse や Local Monitor のトレース詳細画面で確認します。

## 生成フロー

```text
normalized measurements
  -> generate-dashboard-dataset
  -> dashboard dataset JSON
  -> generate-static-dashboard
  -> index.html + dashboard-data.json
```

サンプルデータでの生成例：

```powershell
New-Item -ItemType Directory -Force tmp\dashboard-demo | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site --title "Copilot Agent Observability"
```

## 画面

<p align="center">
  <img width="900" alt="Static dashboard overview" src="../assets/screenshots/static-dashboard-overview.png">
</p>

初期 view:

- Run Overview。
- Agent / Tool Behavior。
- Prompt / Skill / Instructions。
- Baseline vs Variant。
- Diagnosis / Improvement Loop。
- Collection Health。
- Outcome Linkage Candidate。

## Filter / Search / Sort

<p align="center">
  <img width="900" alt="Static dashboard filters" src="../assets/screenshots/static-dashboard-filters.png">
</p>

初期 filter:

- date。
- user。
- client。
- experiment。
- variant。
- status。

Search は trace id、task、user、candidate などを対象にします。
Table header をクリックすると sort できます。

## Static Artifact

`generate-static-dashboard` は指定した `--out-dir` に以下を生成します。

```text
index.html
dashboard-data.json
```

生成済み artifact は local output です。必要な場合だけ、アクセス制御された共有場所へ手動で置いてください。

実データ由来 aggregate を共有する前に確認すること:

- 共有先の access control。
- dashboard dataset に raw prompt / response / tool arguments / tool results が入っていないこと。
- `user.id` と `user.email` の表示が許容されていること。
- retention と削除手順。
- 利用者への周知。

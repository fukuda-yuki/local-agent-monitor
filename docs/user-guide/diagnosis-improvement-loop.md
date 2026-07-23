# 自動診断と改善提案

トレースデータから失敗の傾向を分析し、改善候補を一覧として出力します。
この機能がリポジトリのファイルを変更することはありません。

## レビューフロー（従来方式）

既存の診断レコードから、改善提案・評価結果・判断テンプレートを生成します。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- validate-diagnoses tests\CopilotAgentObservability.ConfigCli.Tests\TestData\m5-diagnoses.synthetic.json --json tmp\raw-loop\validated-diagnoses.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-improvement-proposals tmp\raw-loop\validated-diagnoses.json --json tmp\raw-loop\proposals.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- evaluate-improvement-proposals tmp\raw-loop\proposals.json --json tmp\raw-loop\evaluations.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-decision-template tmp\raw-loop\evaluations.json --json tmp\raw-loop\decision-template.json
```

Human decision を記録する場合:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- record-human-decisions tmp\raw-loop\evaluations.json <decisions.json> --json tmp\raw-loop\human-decisions.json
```

`<decisions.json>` には synthetic または sanitized された decision record だけを指定してください。

## 候補パイプライン

集計済みデータから診断候補を生成します。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-diagnosis-candidates tmp\raw-loop\measurements.json --json tmp\raw-loop\diagnosis-candidates.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-improvement-candidates tmp\raw-loop\diagnosis-candidates.json --json tmp\raw-loop\improvement-candidates.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-auto-decisions tmp\raw-loop\improvement-candidates.json --json tmp\raw-loop\auto-decisions.json
```

機密情報を含む証拠の抽出は、明示的にオプトインした場合のみ使えます。
出力先は repository 外、または ignored local path にしてください。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-diagnosis-candidates tmp\raw-loop\measurements.json --raw data\raw-store.db --include-sensitive-content --sensitive-output-dir tmp\sensitive-output --json tmp\raw-loop\diagnosis-candidates.json
```

Sensitive bundle content と local path は dashboard dataset や repository-stored docs に含めません。

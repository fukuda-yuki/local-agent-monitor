# Diagnosis / Improvement Loop

この loop は trace 由来の失敗傾向と改善候補を deterministic record として整理します。
Repository の file を自動修正せず、patch / diff / commit / push / pull request も作成しません。

## Legacy Human Review Flow

既存 diagnosis record から proposal、evaluation、human decision template を生成します。

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

## Candidate Pipeline

Normalized measurement から diagnosis candidate を生成します。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-diagnosis-candidates tmp\raw-loop\measurements.json --json tmp\raw-loop\diagnosis-candidates.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-improvement-candidates tmp\raw-loop\diagnosis-candidates.json --json tmp\raw-loop\improvement-candidates.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-auto-decisions tmp\raw-loop\improvement-candidates.json --json tmp\raw-loop\auto-decisions.json
```

Sensitive content を含む evidence extraction は明示 opt-in の場合だけ使います。
出力先は repository 外、または ignored local path にしてください。

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-diagnosis-candidates tmp\raw-loop\measurements.json --raw data\raw-store.db --include-sensitive-content --sensitive-output-dir tmp\sensitive-output --json tmp\raw-loop\diagnosis-candidates.json
```

Sensitive bundle content と local path は dashboard dataset や repository-stored docs に含めません。

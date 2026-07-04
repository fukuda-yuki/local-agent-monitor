# M4: Daily GitHub Actions Publish Workflow

## Goal

GitHub Actions で dashboard dataset と static dashboard artifact を日次生成し、GitHub Pages artifact として publish する。

## Implemented Scope

- `.github/workflows/static-dashboard-pages.yml` を追加した。
- Workflow は `schedule` と `workflow_dispatch` で起動する。
- `dotnet test CopilotAgentObservability.slnx` を publish 前に実行する。
- `artifacts/dashboard-input/measurements.json` が存在する場合は、その normalized measurement から dashboard dataset を生成する。
- optional input として `raw-otlp.json`、`diagnosis-candidates.json`、`improvement-candidates.json`、`auto-decisions.json` を同じ input directory から読み取る。
- `measurements.json` が存在しない場合は既存 synthetic raw OTLP fixture から preview dashboard を生成する。
- Pages artifact は `latest/` と `YYYY-MM-DD/` の両方を含む。
- Workflow は `gh-pages` branch に generated snapshot を commit / push し、過去の日次 snapshot を保持する。
- Workflow は repository secret、Langfuse credential、OTLP authorization header を要求しない。

## Verification

- `dotnet test CopilotAgentObservability.slnx`
- Workflow の主要生成 command を local shell で同順序に実行した。

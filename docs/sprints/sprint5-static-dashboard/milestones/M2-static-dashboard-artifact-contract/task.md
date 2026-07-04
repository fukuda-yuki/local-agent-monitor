# M2: Static Dashboard Artifact Contract

## Goal

Static HTML dashboard generator、Pages layout、snapshot policy、client-side interaction の artifact contract を定義する。

## Command Contract

```text
config-cli generate-static-dashboard <dashboard-dataset.json> --out-dir <output-dir> [--snapshot-date <YYYY-MM-DD>] [--title <title>]
```

Rules:

- 入力は `generate-dashboard-dataset --json` が出力する dashboard dataset JSON とする。
- `--out-dir` は必須とする。
- `--snapshot-date` を省略した場合は UTC 今日の日付を使用する。
- `--title` を省略した場合は `Agent Workflow Observability` とする。
- 出力は UTF-8 の static files のみとし、runtime dependency と network dependency を追加しない。

## Output Contract

Generator は `--out-dir` に以下を出力する。

| Path | Purpose |
| --- | --- |
| `index.html` | self-contained static dashboard shell |
| `dashboard-data.json` | input dashboard dataset の sanitized copy |

GitHub Actions は `gh-pages` branch と publish artifact root に以下を配置する。

```text
public/
  latest/
    index.html
    dashboard-data.json
  YYYY-MM-DD/
    index.html
    dashboard-data.json
```

Daily snapshot は `gh-pages` branch 上で保持し、自動削除しない。

## Client-side Interaction

Initial controls:

- filter by date
- filter by user
- filter by client
- filter by experiment
- filter by variant
- filter by status
- free text search
- table sort

Initial sections:

- Run Overview
- Agent / Tool Behavior
- Prompt / Skill / Instructions
- Baseline vs Variant
- Diagnosis / Improvement Loop
- Collection Health
- Outcome Linkage Candidate placeholder

## Sanitization Contract

`dashboard-data.json` と `index.html` には以下を含めない。

- raw prompt
- raw response
- system prompt
- tool arguments
- tool results
- source code fragments or file contents from observed sessions
- credentials, secrets, tokens, API keys, passwords
- Base64 authorization headers
- sensitive bundle content
- sensitive bundle local paths

Generator は input dashboard dataset を信頼しすぎず、known risky key names and text patterns を含む JSON property を output dataset から除外する。
除外対象が見つかった場合は non-zero exit ではなく、除外した sanitized output を生成する。
ただし、入力 JSON として不正な場合、必須 root tables が存在しない場合、出力先に書き込めない場合は non-zero exit とする。

## Actions Contract

Workflow は以下を満たす。

- `schedule` と `workflow_dispatch` で起動する。
- `.NET SDK` を `global.json` に従って setup する。
- `dotnet test CopilotAgentObservability.slnx` を実行する。
- input がある場合は `generate-dashboard-dataset` を実行する。
- input がない場合は synthetic fixture から preview dashboard dataset を生成する。
- `generate-static-dashboard` を実行する。
- `gh-pages` branch に `latest/` と日次 snapshot を commit / push する。
- `actions/upload-pages-artifact` と `actions/deploy-pages` を使用する。
- repository secret、Langfuse credential、OTLP authorization header を要求しない。

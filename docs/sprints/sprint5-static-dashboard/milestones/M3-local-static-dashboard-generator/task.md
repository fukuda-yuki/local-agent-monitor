# M3: Local Static Dashboard Generator

## Goal

Sprint4 dashboard dataset JSON から GitHub Pages 向け static dashboard artifact を生成する。

## Implemented Scope

- `config-cli generate-static-dashboard <dashboard-dataset.json> --out-dir <output-dir> [--snapshot-date <YYYY-MM-DD>] [--title <title>]` を追加した。
- Generator は `index.html` と `dashboard-data.json` を出力する。
- `index.html` は外部 CDN や server-side API に依存しない static HTML とする。
- `dashboard-data.json` は risky key / credential-like string を除外した sanitized copy とする。
- 初期 view は Run Overview、Agent / Tool Behavior、Prompt / Skill / Instructions、Baseline vs Variant、Diagnosis / Improvement Loop、Collection Health、Outcome Linkage Candidate placeholder とする。
- 初期操作は date、user、client、experiment、variant、status filter、free text search、table sort とする。

## Related Dataset Change

Sprint5 仕様に合わせ、dashboard dataset row に `user_id` と `user_email` を追加した。
値は raw OTLP の resource attributes から取得できる場合のみ設定し、取得できない場合は null とする。

## Verification

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`
- synthetic raw OTLP から `normalize-raw`、`generate-dashboard-dataset`、`generate-static-dashboard` を順に実行し、local artifact を生成した。


# Sprint3: Content-aware Trace Diagnosis and Auto-decision Foundation

Sprint3 は、Sprint2 MVP と Sprint2.5 で除外していた trace からの自動診断を、content-aware evidence、改善候補生成、自動採用判断まで拡張して扱うための要件定義 sprint である。

この文書は Sprint3 の作業方針を記録する。
正式な product behavior は `../../requirements.md` と `../../spec.md` を source of truth とする。
未確定の command contract、candidate schema、content evidence schema、auto-decision schema はこの sprint-local material に留め、確定後に `../../spec.md` へ反映する。

## 背景

Sprint2 MVP では、raw OTLP file ingest、SQLite raw store、normalized dataset、Langfuse 非依存 loop を整備した。
Sprint2.5 では、Config CLI の責務分割と redacted real-trace E2E 互換性確認を完了した。

既存の M23-M27 loop は、taxonomy 定義、人間分類 diagnosis record validation、改善候補生成、proposal evaluation、人間判断記録までを扱っていた。
ただし、trace content からの自動診断、自動採用判断、自動改善実装は扱っていなかった。

Sprint3 ではこの境界を変更し、自動採用判断までを扱う。
実 repository 修正を伴う自動改善実装は Sprint3 の既定スコープには含めず、Sprint4 以降の候補として安全境界を定義してから扱う。

## Sprint3 Scope

- raw store または raw OTLP JSON から trace-driven diagnosis candidate を生成する。
- normalized dataset から trace id、task id、client kind、experiment、token、turn、tool call、duration、error などの集計値を diagnosis candidate に接続する。
- 明示 opt-in 時に、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を content-aware evidence として sensitive local output に含める。
- diagnosis candidate から improvement proposal candidate を生成する。
- deterministic rule による auto-approval candidate を生成する。
- auto-approval candidate を、後続の自動改善実装に渡せる判断 record として扱う。

## Non-scope for Sprint3

- repository file の自動修正。
- patch / diff の生成。
- commit / push / pull request 作成。
- 自動勝敗決定。
- live service や network endpoint に依存する自動テスト。
- sensitive local output の repository 保存または commit。

## Sensitive Output Policy

Sprint3 の sensitive local output は、ローカル検証用の一時成果物であり、repository に保存しない。
保存先は `tmp/` など git 管理外の場所を既定とする。

実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含む出力は、command 名または option 名で sensitive output であることを明示する。
repository に保存してよい文書・fixture・review record には、これらの sensitive content を含めない。

## Initial Decisions

| 論点 | Sprint3 の初期判断 |
| --- | --- |
| 入力 | raw store、raw OTLP JSON、normalized measurement CSV / JSON を扱う |
| raw content 利用 | 明示 opt-in 時だけ許可する |
| diagnosis 出力 | M24 diagnosis record とは別の candidate 専用 command / candidate 専用 schema を先に定義する |
| auto decision | M27 human decision record とは別の auto-decision 専用 schema を先に定義する |
| 自動改善実装 | Sprint3 では repository 修正せず、Sprint4 以降の候補にする |
| テスト | synthetic fixture で automated verification を完結させる |

## Startup Check

Sprint3 の content-aware diagnosis は、既存起動パスと raw data loop が動くことを前提にする。
実装着手前に、少なくとも以下を確認する。

- Config CLI が起動し、help を出力できる。
- solution が build できる。
- AppHost が Aspire CLI で起動できる。
- AppHost 起動確認後に Aspire CLI で停止できる。

2026-06-12 時点では、`dotnet run --project src\CopilotAgentObservability.ConfigCli -- --help`、`dotnet build CopilotAgentObservability.slnx`、`aspire start --non-interactive --format Json`、`aspire stop --non-interactive` が成功している。
AppHost は空の resource graph であり、Sprint3 の primary execution path ではない。

## Open Questions

- content-aware evidence の最小 schema をどう定義するか。
- auto-approval rule の初期 rule set をどこまで deterministic にするか。
- Sprint4 で repository file 自動修正を扱う場合、allowlist、dry-run、diff preview、rollback、test 実行、commit 境界をどう定義するか。

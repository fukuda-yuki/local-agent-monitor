# Sprint3: Content-aware Trace Diagnosis and Auto-decision Foundation

Sprint3 は、Sprint2 MVP と Sprint2.5 で除外していた trace からの自動診断候補生成を、deterministic rule、content-aware evidence、改善候補生成、自動採用判断を含む auto-decision record まで拡張して扱う sprint である。

この文書は Sprint3 の作業方針を記録する。
正式な product behavior は `../../requirements.md` と `../../spec.md` を source of truth とする。
未確定の command contract、candidate schema、content evidence schema、auto-decision schema はこの sprint-local material に留め、確定後に `../../spec.md` へ反映する。

## 背景

Sprint2 MVP では、raw OTLP file ingest、SQLite raw store、normalized dataset、Langfuse 非依存 loop を整備した。
Sprint2.5 では、Config CLI の責務分割と redacted real-trace E2E 互換性確認を完了した。

既存の M23-M27 loop は、taxonomy 定義、人間分類 diagnosis record validation、改善候補生成、proposal evaluation、人間判断記録までを扱っていた。
ただし、trace content からの自動診断、自動採用判断、自動改善実装は扱っていなかった。

Sprint3 ではこの境界を変更し、trace から deterministic に診断候補を作り、自動採用判断を含む auto-decision record までを扱う。
実 repository 修正を伴う自動改善実装は Sprint3 の既定スコープには含めず、Sprint4 以降の候補として安全境界を定義してから扱う。

## Sprint3 Scope

- raw store または raw OTLP JSON から trace-driven diagnosis candidate を生成する。
- normalized dataset から trace id、task id、client kind、experiment、token、turn、tool call、duration、error などの集計値を diagnosis candidate に接続する。
- span attribute、span event、tool result、prompt / response fragment に対する deterministic content-aware rule を定義する。
- 明示 opt-in 時に、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を content-aware evidence として sensitive local output に含める。
- sensitive bundle の manifest / evidence schema、fragment 粒度、逆引き方法、期限情報を定義する。
- diagnosis candidate から improvement proposal candidate を生成する。
- deterministic rule による auto-decision record を生成する。
- 既存 M24-M27 human-review pipeline は互換性維持対象として残し、Sprint3 candidate pipeline から既存 record へ渡す adapter / mapping contract を実装前に決める。
- synthetic automated verification に加え、ユーザー協業による redacted real-trace E2E を実施し、GitHub Copilot CLI と GitHub Copilot Chat の candidate pipeline 入力互換性を確認する。

## Milestones

| Milestone | 状態 | 内容 |
| --- | --- | --- |
| [M1: candidate schema and command boundary](milestones/M1-candidate-schema-and-command-boundary/task.md) | 完了 | command 名、入力、出力列、sensitive output 保存先、synthetic fixture 方針を sprint-local に定義した |
| [M2: deterministic rule and evidence contract](milestones/M2-deterministic-rule-and-evidence-contract/task.md) | 未着手 | diagnosis rule id、decision rule id、content-aware pattern、M24-M27 接続方針、sensitive bundle read / manual delete contract を確定し、実装前の blocking question を潰す |
| [M3: diagnosis candidate implementation](milestones/M3-diagnosis-candidate-implementation/task.md) | 未着手 | synthetic fixture で `generate-diagnosis-candidates` を実装し、metadata rule と content-aware rule の最小出力を検証する |
| [M4: improvement and auto-decision implementation](milestones/M4-improvement-and-auto-decision-implementation/task.md) | 未着手 | `generate-improvement-candidates` と `generate-auto-decisions` を実装し、`auto-approved` / `needs-human-review` / `blocked` を出力する |
| [M5: human-review pipeline connection](milestones/M5-human-review-pipeline-connection/task.md) | 未着手 | M2 で決めた adapter / mapping contract を文書または code に反映し、未接続 pipeline を残さない |
| [M6: collaborative real-trace E2E](milestones/M6-collaborative-real-trace-e2e/task.md) | 未着手 | GitHub Copilot CLI と GitHub Copilot Chat の redacted real-trace 入力で candidate pipeline を確認し、agent / user の作業分担と evidence を記録する |

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
bundle は schema version、作成日時、期限、削除対象 path、evidence index を持つ `manifest.json` と、candidate ごとの `evidence/*.json` で構成する。
standard output から bundle content へは `evidence_ref` を通じて逆引きする。
Sprint3 では sensitive bundle の自動削除 command は実装しない。
期限切れ bundle を読む command は warning を出し、削除は `manifest.json` の `delete_target_paths` を確認したユーザーが手動で実施する。

実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含む出力は、command 名または option 名で sensitive output であることを明示する。
repository に保存してよい文書・fixture・review record には、これらの sensitive content を含めない。

## Initial Decisions

| 論点 | Sprint3 の初期判断 |
| --- | --- |
| 入力 | raw store、raw OTLP JSON、normalized measurement CSV / JSON を扱う |
| raw content 利用 | 明示 opt-in 時だけ許可する |
| diagnosis 出力 | M24 diagnosis record とは別の candidate 専用 command / candidate 専用 schema を先に定義する |
| auto decision | M27 human decision record とは別の auto-decision schema を先に定義する |
| M24-M27 との関係 | 既存 command / schema は置換せず互換性維持対象とし、Sprint3 candidate output から既存 human-review record への adapter / mapping を実装前に確定する |
| 自動改善実装 | Sprint3 では repository 修正せず、Sprint4 以降の候補にする |
| 自動テスト | synthetic fixture で deterministic behavior を検証する |
| real-trace 検証 | GitHub Copilot CLI は agent が可能な範囲で実施し、GitHub Copilot Chat の prompt 送信などユーザー実施の方が低コストな作業はユーザー協業で行う。repository へ保存する evidence は redacted summary に限定する |

M1 の詳細は [command-boundary.md](milestones/M1-candidate-schema-and-command-boundary/command-boundary.md) を参照する。

## Startup Check

Sprint3 の content-aware trace diagnosis and auto-decision pipeline は、既存起動パスと raw data loop が動くことを前提にする。
実装着手前に、少なくとも以下を確認する。

- Config CLI が起動し、help を出力できる。
- solution が build できる。
- solution の regression test が通る。
- AppHost が Aspire CLI で起動できる。
- AppHost 起動確認後に Aspire CLI で停止できる。

2026-06-12 時点では、`dotnet run --project src\CopilotAgentObservability.ConfigCli -- --help`、`dotnet build CopilotAgentObservability.slnx`、`dotnet test CopilotAgentObservability.slnx`、`aspire start --non-interactive --format Json`、`aspire stop --non-interactive` が成功している。
AppHost は空の resource graph であり、Sprint3 の primary execution path ではない。

## Open Questions

- Sprint4 で repository file 自動修正を扱う場合、allowlist、dry-run、diff preview、rollback、test 実行、commit 境界をどう定義するか。

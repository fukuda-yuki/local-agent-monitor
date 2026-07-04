# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-05-26: copilot-cli 側部分実施

M19 の full scope は 4 類型 x 2 `client.kind` x N=10、合計 80 valid completed runs である。
この時点では `copilot-cli` 側 4 類型、計 40 valid completed runs まで実施した。
`vscode-copilot-chat` 側 40 runs は未完了である。

実行済みの概要:

| task_id | client_kind | valid completed | failed retry rows | trace missing |
| --- | --- | ---: | ---: | ---: |
| `maint-refactor-001` | `copilot-cli` | 10 | 0 | 0 |
| `maint-bug-001` | `copilot-cli` | 10 | 3 | 0 |
| `maint-test-001` | `copilot-cli` | 10 | 0 | 0 |
| `maint-review-001` | `copilot-cli` | 10 | 2 | 0 |
| all tasks | `vscode-copilot-chat` | 0 | 0 | 0 |

ローカル作業用 artifact は ignored な `tmp/m19-baseline-measurement/` に置いた。
`ledger.csv` には completed run と failed retry / interruption 履歴を記録した。
`langfuse-m19-copilot-cli-traces.sanitized.json` には 40 completed trace だけを、M12 schema への接続に必要な最小情報へ sanitization して保存した。
`measurements.copilot-cli.csv` と `measurements.copilot-cli.json` は `aggregate-measurements` で生成した 40 行の partial dataset である。

sanitization では、credential、Base64 header、実 email、`user.id` / `user.email`、prompt / response content、tool arguments / results、raw `gen_ai.tool.definitions` を保存しない方針を確認した。
tracked docs には trace content、prompt body、response body、tool arguments / results、credential、Base64 header、実 user identity を記録していない。

`maint-review-001` / `copilot-cli` / `task_run_index=1` の初回実行と direct retry は異常長になり、計測を停止した。
停止した run は `run_status=failed`、`failure_type=operator-error`、`trace_found=false` の non-valid rows として台帳に記録し、有効 N に数えない。
その後、同じ M13 prompt 本文に synthetic PR patch を native document fixture として添付し、`task_run_index=1..10` の valid completed traces を取得した。
`maint-bug-001` の 3 failed rows は、`task_run_index=5` の attempt 0 / retry 1 と、`task_run_index=8` の attempt 0 に分散しており、各 planned unit の retry は最大 2 回以内だった。

再開時は `tmp/m19-baseline-measurement/ledger.csv` と Langfuse 上の `<local-user-id>` / M19 Resource Attributes を確認し、既存 40 completed trace を再利用して `vscode-copilot-chat` 側 40 runs から進める。
`tmp/m19-baseline-measurement/run-copilot-cli-measurement.ps1` は local execution machinery であり、sanitized artifact や共有対象ではない。
`tmp/m19-baseline-measurement/run-copilot-cli-review-resume.ps1` と synthetic fixture attachment も local execution machinery であり、sanitized artifact や共有対象ではない。

## 2026-05-26: vscode-copilot-chat 実行経路 blocker

`code chat --help` で VS Code 1.121.0 の `chat` subcommand が利用可能であり、`--mode agent`、`--add-file`、`--new-window`、`--reuse-window` を受け付けることを確認した。
`code agent --help` は agent host session 管理用であり、M13 prompt を直接投入する経路ではない。

`vscode-copilot-chat` / `maint-refactor-001` / `task_run_index=1` について、M19 の Resource Attributes を付けて `code chat --mode agent` から実行した。
Langfuse には VS Code 由来の `chat gpt-4o-mini-2024-07-18` traces が入ったが、M18 と M19 plan で代表 trace と定めた `invoke_agent GitHub Copilot Chat` は取得できなかった。
`--reuse-window` retry と既定 profile の `--new-window` retry でも代表 trace は取得できなかったため、3 行を `run_status=trace-missing`、`failure_type=trace-missing` として `ledger.csv` に記録した。
各 retry は同じ planned unit の retry とし、新しい `run_id` を使い `retry_of_run_id` で前回 attempt に接続した。

このため、`vscode-copilot-chat` 側の valid completed traces は 0 件のままである。
M19 は 80 valid completed traces に到達しておらず、`measurements.csv` / `measurements.json` の最終 80 行 dataset は未生成である。
再開時は VS Code UI から M18 と同じ agent invocation trace を発生させる手順、または `code chat` で `invoke_agent GitHub Copilot Chat` を発生させる設定差分を先に確認する。

追加で、VS Code User settings に `github.copilot.chat.otel.*` の Langfuse direct export 設定を一時的に入れて `baseline-smoke` として `code chat --mode agent` を再確認した。
この場合も Langfuse に入るのは `chat ...` traces で、代表 trace の `invoke_agent GitHub Copilot Chat` は取得できなかった。
一時設定は content capture を含むため、確認後に元の User settings へ戻した。

`code agent host` も確認したが、これは agent host server を起動する経路であり、今回の M13 prompt を投入して `vscode-copilot-chat` 代表 trace を得る直接経路にはならなかった。
起動した agent host は確認後に停止した。

公式 VS Code docs では `code --agents` が Agents Window を開く経路として説明されているため、同じ OTel env を付けて smoke 起動した。
Agents Window の起動だけでは M13 prompt を投入できず、Langfuse に新しい代表 trace も発生しなかった。

## 2026-05-27: blocker audit

再開後に `tmp/m19-baseline-measurement/ledger.csv` を再確認し、現状態は 48 rows、40 completed、5 failed、3 trace-missing であることを確認した。
`vscode-copilot-chat` の valid completed traces は 0 件であり、`maint-refactor-001` / `task_run_index=1` は M17/M19 の retry 上限 2 回まで `trace-missing` になっている。

VS Code User settings には一時設定した `github.copilot.chat.otel.*` が残っていないことを確認した。
`code agent ps` では running agent host がないことを確認した。

この blocker は、M18/M19 で代表 trace とした `invoke_agent GitHub Copilot Chat` を現在の shell-driven route から発生させられないことである。
再開条件は、VS Code UI で M18 と同じ foreground agent invocation を発生させる手順を人手で確立すること、または `code chat` / agent host / Agents Window から同じ代表 trace を発生させる supported automation route を確認することである。

## 2026-06-03: 80 valid completed traces 達成

VS Code foreground Chat UI 経路で `vscode-copilot-chat` 側 40 runs を取得し、既存の `copilot-cli` 側 40 runs と合わせて 80 valid completed traces に到達した。
`vscode-copilot-chat` では各 run ごとに VS Code process を起動し直し、process env 由来の Resource Attributes が `task.id` / `task.run_index` ごとに分離されるようにした。
`maint-review-001` では M13 の前提どおり、送信前に synthetic PR context を添付した。

最終 completed 件数:

| task_id | copilot-cli | vscode-copilot-chat |
| --- | ---: | ---: |
| `maint-refactor-001` | 10 | 10 |
| `maint-bug-001` | 10 | 10 |
| `maint-test-001` | 10 | 10 |
| `maint-review-001` | 10 | 10 |

`ledger.csv` の non-valid rows は、`failed` 5 rows、`trace-missing` 4 rows、`excluded` 6 rows である。
`failed`、`trace-missing`、`excluded` は有効 N に数えず、retry rows は新しい `run_id` と `retry_of_run_id` で接続した。
途中で Computer Use により既存 VS Code Chat session context が混入し得る run を検出したため、その attempt は `excluded` として台帳に残し、有効 N から外した。

最終 local artifacts は ignored な `tmp/m19-baseline-measurement/` に置いた。
`langfuse-m19-traces.sanitized.json` は 80 completed traces だけを M12 schema 接続に必要な最小情報へ sanitization した snapshot である。
`measurements.csv` と `measurements.json` は `aggregate-measurements` で生成した 80 行 dataset であり、全行 `success_status=not-evaluated` である。

sanitization では、credential/header、identity-bearing Resource Attributes、prompt / response body、tool arguments / results、raw trace content を保存しない方針を確認した。
tracked docs には実 trace content、実 user identity、prompt body、response body、tool arguments / results、credential/header を記録していない。
`unknown_spans_json` は M16 の方針どおり未知 observation を最小識別情報として保持する。後続比較前に、sanitized metadata レベルで未知 observation 名が count 分類へ追加すべきものかを確認する余地がある。

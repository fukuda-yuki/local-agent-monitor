# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-26: copilot-cli 部分実施後レビュー

### レビュー範囲

- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/plan.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/notes.md`
- ignored local artifacts under `tmp/m19-baseline-measurement/`

### 確認結果

M19 の実行計画は M17 baseline protocol と M18 dry run の trace 選定方針に沿っている。
`copilot-cli` 側では `maint-refactor-001`、`maint-bug-001`、`maint-test-001`、`maint-review-001` の各 10 valid completed traces を取得できた。
`maint-bug-001` の異常長 run では failed retry rows を台帳に残し、最終的に有効 trace を取得した。
`maint-bug-001` の failed rows は `task_run_index=5` の attempt 0 / retry 1 と `task_run_index=8` の attempt 0 であり、各 planned unit の retry は最大 2 回以内だった。
`failed` rows は有効 N に数えず、M17 の retry 方針に沿って扱った。
`maint-review-001` / `copilot-cli` / `task_run_index=1` の初回実行と direct retry の異常長停止は、台帳に `run_status=failed`、`failure_type=operator-error`、`trace_found=false` の non-valid rows として追記した。
その後、同じ M13 prompt 本文に synthetic PR patch を native document fixture として添付し、`task_run_index=1..10` の有効 trace を取得した。

`aggregate-measurements tmp/m19-baseline-measurement/langfuse-m19-copilot-cli-traces.sanitized.json --csv tmp/m19-baseline-measurement/measurements.copilot-cli.csv --json tmp/m19-baseline-measurement/measurements.copilot-cli.json` により、40 行の partial dataset を生成できた。
全 40 行で `success_status=not-evaluated` になっており、M20 rubric を先取りしていない。

### データ扱い

sanitized snapshot について、credential/header、identity-bearing Resource Attributes、tool definition / argument / result、prompt / response body、実行用 synthetic email が含まれないことを確認した。
tracked docs には件数、状態、判断理由だけを記録し、credential、Base64 header、prompt body、response body、tool arguments/results、実 trace content、実 user identity は記録していない。
`tmp/m19-baseline-measurement/run-copilot-cli-measurement.ps1` は local execution machinery であり、sanitized artifact や共有対象ではない。
`tmp/m19-baseline-measurement/run-copilot-cli-review-resume.ps1` と synthetic fixture attachment も local execution machinery であり、sanitized artifact や共有対象ではない。

### 残リスク / Blocker

M19 は未完了である。
`copilot-cli` 側 40 valid completed traces は取得済みである。
この terminal から `code chat --mode agent` で VS Code GitHub Copilot Chat に prompt を投入すると Langfuse に `chat ...` traces は入るが、M18/M19 で代表 trace とする `invoke_agent GitHub Copilot Chat` が取得できない。
`maint-refactor-001` / `vscode-copilot-chat` / `task_run_index=1` は retry 上限 2 回まで確認し、3 attempts を `trace-missing` として台帳に記録した。
`vscode-copilot-chat` 側の valid completed traces は 0 件である。
VS Code User settings に `github.copilot.chat.otel.*` を一時設定した smoke、`code agent host`、`code --agents` も確認したが、いずれも `invoke_agent GitHub Copilot Chat` を得る実行経路にはならなかった。
一時設定と agent host は確認後に戻した。

残作業は以下である。

- VS Code UI または別の supported route で `invoke_agent GitHub Copilot Chat` を発生させる手順を確立する。
- `vscode-copilot-chat` で 4 類型 x N=10 の valid completed traces を取得し、M18 と同じく `invoke_agent GitHub Copilot Chat` を代表 trace として台帳に記録する。
- 80 valid completed traces の sanitized snapshot から M12 schema の CSV / JSON dataset を生成する。
- M19 の完了レビューを実施する。

## 2026-05-27: blocked audit

### 確認結果

M19 は引き続き未完了である。
`copilot-cli` 側 40 valid completed traces と partial dataset は保持されている。
`vscode-copilot-chat` 側は `maint-refactor-001` / `task_run_index=1` で 3 attempts が `trace-missing` となり、M17/M19 の retry 上限に達している。

追加確認として、VS Code OTel User settings が元に戻っていること、`code agent ps` で running agent host がないことを確認した。
これ以上の M19 進行には、`invoke_agent GitHub Copilot Chat` 代表 trace を発生させる VS Code UI 手順または supported automation route が必要である。

### 判断

`failed`、`trace-missing`、`excluded` を有効 N に数えない M17/M19 方針により、現在の `vscode-copilot-chat` traces を M19 dataset に採用しない。
80 valid completed traces の M12 schema dataset は未生成のままとする。

## 2026-06-03: 完了レビュー

### レビュー範囲

- `tmp/m19-baseline-measurement/ledger.csv`
- `tmp/m19-baseline-measurement/langfuse-m19-traces.sanitized.json`
- `tmp/m19-baseline-measurement/measurements.csv`
- `tmp/m19-baseline-measurement/measurements.json`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/plan.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/review.md`

### 確認結果

`copilot-cli` と `vscode-copilot-chat` の両方で、4 task x N=10 の valid completed traces を取得した。
最終 ledger の completed は 80 rows であり、各 `task_id + client_kind` が 10 completed である。
non-valid rows は `failed` 5 rows、`trace-missing` 4 rows、`excluded` 6 rows として台帳に残し、有効 N には含めていない。

`tmp/m19-baseline-measurement/langfuse-m19-traces.sanitized.json` から `aggregate-measurements` を実行し、`measurements.csv` と `measurements.json` に 80 rows/items を生成した。
全行で `success_status=not-evaluated` であり、M20 rubric の品質判定を先取りしていない。
M12 必須列、`trace_id`、`client_kind`、`task_id`、`task_category`、`task_run_index`、token count、turn count、tool call count、duration、error count は dataset に接続できる。

### データ扱い

sanitized snapshot と final dataset には、credential/header、identity-bearing Resource Attributes、prompt / response body、tool arguments / results、raw trace content を含めないことを確認した。
tracked docs には件数、判断理由、検証結果、レビュー記録だけを残し、実 trace content や実 user identity は記録していない。
final artifacts は ignored な `tmp/m19-baseline-measurement/` に置き、repository commit 対象にしない。

### 残リスク

`vscode-copilot-chat` 側は foreground VS Code Chat UI の手動送信経路で取得したため、完全な shell-only automation route は確立していない。
ただし、各 run は VS Code process を起動し直し、Langfuse 上の representative trace と Resource Attributes を確認して台帳化したため、M19 の baseline dataset として採用できる。
`unknown_spans_json` は M16 の方針どおり保持しているが、後続比較前に sanitized metadata レベルで未知 observation 名の count 分類要否を確認する余地がある。

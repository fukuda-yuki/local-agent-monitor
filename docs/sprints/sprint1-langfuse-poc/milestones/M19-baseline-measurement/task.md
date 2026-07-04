# M19: baseline 本計測

## 目的

4 類型 x 2 client_kind x N=10 の baseline trace を取得し、研究計測用 dataset を作成する。

## 完了条件

- [x] baseline 本計測を実施する
- [x] 欠損 trace、取得失敗、手動除外を記録する
- [x] M12 schema に従う dataset を生成する
- [x] baseline 結果を後続比較に使える状態にする
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-26: M17 protocol と M18 dry run の結果に従い、`docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/plan.md` に本計測の実行マトリクス、artifact 置き場、retry 方針、sanitization 方針、review 観点を固定した。
- 2026-05-26: `copilot-cli` 側の `maint-refactor-001`、`maint-bug-001`、`maint-test-001` で各 10 件、計 30 件の有効 trace を取得した。`maint-bug-001` では異常長 run のため 3 件の failed retry 履歴を台帳に残した。
- 2026-05-26: 30 trace から content と identity-bearing 属性を除いた sanitized snapshot を作成し、`aggregate-measurements` で 30 行の CSV / JSON 集計を生成できることを確認した。
- 2026-05-26: `maint-review-001` / `copilot-cli` / `task_run_index=1` の実行が異常長で停止したため、non-valid failed row として台帳に記録し、M19 は未完了として一時停止した。`vscode-copilot-chat` 側 40 runs も未実行。
- 2026-05-26: `maint-review-001` / `copilot-cli` は synthetic PR patch を native document fixture として添付し、`task_run_index=1..10` の有効 trace を取得した。`copilot-cli` 側は 4 類型 x N=10、計 40 valid completed traces まで完了した。
- 2026-05-26: 40 trace から content と identity-bearing 属性を除いた sanitized snapshot を作成し、`aggregate-measurements` で 40 行の CSV / JSON 集計を生成できることを確認した。`vscode-copilot-chat` 側 40 runs は未実行。
- 2026-05-26: `code chat --mode agent` を使った `vscode-copilot-chat` / `maint-refactor-001` / `task_run_index=1` の実行経路を検証したが、M18/M19 で代表 trace とする `invoke_agent GitHub Copilot Chat` が取得できず、retry 上限 2 回まで `trace-missing` として台帳に記録した。`vscode-copilot-chat` 側の有効 trace は 0 件のため、M19 は未完了として一時停止する。
- 2026-05-27: 再開後も同じ blocker が継続していることを確認した。`code chat`、VS Code OTel User settings の一時設定、`code agent host`、`code --agents` では `invoke_agent GitHub Copilot Chat` を発生させる自動実行経路を確立できなかったため、M19 は 80 valid completed traces 未達のまま blocked とする。
- 2026-06-03: VS Code foreground Chat UI 経路で `vscode-copilot-chat` 側 40 valid completed traces を取得し、`copilot-cli` 側 40 traces と合わせて 80 valid completed traces に到達した。`ledger.csv` には completed 80、failed 5、trace-missing 4、excluded 6 を記録した。
- 2026-06-03: `tmp/m19-baseline-measurement/langfuse-m19-traces.sanitized.json` を生成し、`aggregate-measurements` で `measurements.csv` / `measurements.json` の 80 行 dataset を生成した。全行 `success_status=not-evaluated` であることを確認した。
- 2026-06-03: ledger と dataset の各 `task_id + client_kind` が 10 completed / 10 rows であること、M12 必須列が欠けていないこと、tracked docs と sanitized artifacts に credential/header、identity-bearing attributes、prompt/response body、tool arguments/results、raw content が含まれないことを確認した。

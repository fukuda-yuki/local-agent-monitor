# Plan

## 2026-05-26: baseline 本計測実行計画

M19 では、M17 の baseline protocol と M18 dry run の結果に従い、4 類型 x 2 `client.kind` x N=10 の baseline 本計測を実施する。

実行マトリクスは以下で固定する。

| 軸 | 値 |
| --- | --- |
| task order | `maint-refactor-001`, `maint-bug-001`, `maint-test-001`, `maint-review-001` |
| client order | `copilot-cli`, `vscode-copilot-chat` |
| run index | `1` から `10` |
| fixed attributes | `experiment.id=baseline`, `experiment.condition=baseline`, `agent.variant=baseline`, `prompt.version=v1`, `repo.snapshot=synthetic-dotnet-fixture-v1` |

各 run の prompt は `docs/spec.md` の M13 task table にある prompt 文をそのまま使う。
入力は synthetic fixture 条件に限定し、実 repository 内容、実ユーザーデータ、外部データ、秘密情報は使わない。

ローカル成果物は `tmp/m19-baseline-measurement/` に置く。
`ledger.csv` は M17 の CSV 台帳列を使い、`prompt_used` は `<task_id>/v1` の参照だけを記録する。
Langfuse snapshot は集計前に content と identity-bearing 属性を除いた sanitized JSON に変換し、`measurements.csv` と `measurements.json` は有効な `completed` trace のみから生成する。

`vscode-copilot-chat` で 1 run から複数 trace が出た場合は、M18 と同じく `invoke_agent GitHub Copilot Chat` trace を代表 trace とする。
該当 trace が見つからない場合は、その run を `trace-missing` として台帳に記録する。

`failed`、`trace-missing`、`excluded` は有効 N に数えない。
再実行時は新しい `run_id` を作り、`retry_of_run_id` に元の `run_id` を記録する。
各 planned unit の retry は最大 2 回までとし、なお有効 trace が取れない場合は M19 を一時停止して blocker として記録する。

M20 の品質非劣化 rubric は未定義のため、M19 では `success_status=not-evaluated` を既定値とし、`pass`、`fail`、`needs-review` は付与しない。

実行後レビューでは、仕様整合、台帳完全性、sanitization、集計行数、VS Code 代表 trace 選定を確認する。

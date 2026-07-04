# M18: baseline 計測の小規模 dry run

## 目的

`maint-refactor-001` の 1 類型 x 2 client_kind x 2 runs の小規模 dry run で schema と集計が成立するか確認する。

## 完了条件

- [x] M17 の手順で小規模 dry run を実施する
- [x] Langfuse trace id と集計結果を記録する
- [x] schema、集計、rubric 前提の不足を洗い出す
- [x] 必要な仕様・タスク更新を行う
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-25: preflight として Docker Desktop を起動し、Langfuse self-host UI が `http://localhost:3000` で HTTP 200 を返すことを確認した。
- 2026-05-25: `maint-refactor-001` / `copilot-cli` / `task_run_index=1,2` の dry run を実施し、Langfuse trace 2 件を確認した。
- 2026-05-25: CLI 側 2 trace から content と identity-bearing 属性を除いた sanitized snapshot を作成し、`aggregate-measurements` で 2 行の CSV / JSON 集計を生成できることを確認した。
- 2026-05-26: `maint-refactor-001` / `vscode-copilot-chat` / `task_run_index=1,2` の dry run を実施し、Langfuse trace 2 件を確認した。
- 2026-05-26: 4 trace から content と identity-bearing 属性を除いた sanitized snapshot を作成し、`aggregate-measurements` で 4 行の CSV / JSON 集計を生成できることを確認した。
- 2026-05-26: M18 の 1 類型 x 2 client_kind x 2 runs が完了し、`success_status` は M20 rubric 未定義のため全 run `not-evaluated` とした。

# M12: 研究用 measurement schema 定義

## 目的

Langfuse trace から baseline / variant 比較に使う研究用 measurement schema を定義する。

## 完了条件

- [x] 必須列と任意列を定義する
- [x] `experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant` の扱いを整理する
- [x] 欠損値、未知属性、手動評価値の記録方針を定義する
- [x] `docs/spec.md` に確定仕様を反映する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-24: `docs/spec.md` に measurement schema の必須列、任意列、Resource Attribute からの写像、欠損値、未知属性、手動評価値の方針を反映した。
- 2026-05-24: `rg -n "measurement schema|success_status|experiment\\.id|experiment\\.condition|prompt\\.version|agent\\.variant|turn_count|tool_call_count|M14|M16|M20|M21" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M12-measurement-schema docs/requirements.md` で文書整合を確認した。
- 2026-05-24: `rg -n "自動採用|自動実装|勝敗の自動決定|Langfuse export|API|算出ルール|rubric|A/B" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M12-measurement-schema` でスコープ境界の記述を確認し、本文レビューで M12 が実装手順、算出ルール、rubric 判定、A/B 実行順序を定義していないことを確認した。
- 2026-05-24: 並列 sub-agent レビューで出た指摘を評価し、`client.kind` 例の不整合、未知情報の保持先、必須列の nullability、任意列候補の表現、M14/M15/M16 境界、検証記録の表現を修正した。
- 2026-05-24: documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

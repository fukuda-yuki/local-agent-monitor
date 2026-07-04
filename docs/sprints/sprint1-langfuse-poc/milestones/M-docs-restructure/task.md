# M-docs-restructure: docs 構造再編

## 目的

Agent が実装時に必要な仕様、タスク、計画、質問、レビュー、メモを milestone 単位で追跡できる docs 構造へ再編する。

## 完了条件

- [x] `docs/task.md` を全体 index に縮小する
- [x] `docs/sprints/sprint1-langfuse-poc/milestones/` 配下に M11-M22 の milestone 作業束を作成する
- [x] 旧トップレベル knowledge file を `docs/sprints/sprint1-langfuse-poc/knowledge/` 配下へ分割する
- [x] `AGENTS.md` を新しい docs 構造と GitHub Issue 方針に合わせて更新する
- [x] `README.md` のドキュメント説明を新構造に合わせて更新する
- [x] 旧参照の残存確認を行う
- [x] 自己レビューを `review.md` に記録する

## 検証記録

- 旧参照検索で、現行手順として残る旧参照がないことを確認した。検出された旧参照は `docs/sprints/sprint1-langfuse-poc/archive/review/` の履歴のみであり、現行手順の根拠としては扱わない。
- 新構造検索で、`AGENTS.md`、`README.md`、`docs/task.md` が `docs/sprints/sprint1-langfuse-poc/milestones/` と `docs/sprints/sprint1-langfuse-poc/knowledge/` を参照していることを確認した。

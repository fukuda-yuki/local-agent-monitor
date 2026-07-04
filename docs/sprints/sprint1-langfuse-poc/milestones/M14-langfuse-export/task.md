# M14: Langfuse export / API 調査

## 目的

Langfuse 上の trace / observation / usage / metadata を研究用 dataset に取り出す方式を決定する。

## 完了条件

- [x] Langfuse export / API の候補を調査する
- [x] M15 が利用する入力形式を決定する
- [x] 認証情報を repository に保存しない運用を確認する
- [x] `docs/spec.md` に確定仕様を反映する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-24: 公式 docs の Public API、Observations API、Export from UI、Export to Blob Storage を確認し、self-host baseline では Public API legacy trace / observation read response の保存 JSON を M15 の既定入力にする方針を決定した。
- 2026-05-24: `rg -n "M14|Langfuse export|Observations API|Public API|ClickHouse|Blob Storage|M15" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M14-langfuse-export docs/sprints/sprint1-langfuse-poc/knowledge` で M14 の反映箇所を確認した。
- 2026-05-24: `rg -n "public-key|secret-key|Authorization=Basic|LANGFUSE_AUTH" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M14-langfuse-export docs/sprints/sprint1-langfuse-poc/knowledge` で credential 例が placeholder / 方針記述に留まり、実 secret を追加していないことを確認した。

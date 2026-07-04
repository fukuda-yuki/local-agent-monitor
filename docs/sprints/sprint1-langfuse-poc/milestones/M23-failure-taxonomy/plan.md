# Plan

## 2026-06-03

M23 は documentation-only milestone として、M24 trace-to-diagnosis MVP の前提になる failure taxonomy / anti-pattern 定義を追加する。

- `docs/sprints/sprint1-langfuse-poc/milestones/M23-failure-taxonomy/taxonomy.md` を追加し、failure category、agent anti-pattern、evidence requirements、severity、improvement target を定義する。
- `docs/spec.md` に M23 の確定仕様を反映する。
- `docs/task.md` で M23 を Future Backlog から現行 milestone へ移す。
- M17 `failure_type` との境界を明記し、run / trace 取得失敗と回答品質・診断可能性の taxonomy を混同しない。
- 自動採用、自動実装、自動 repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定は含めない。
- 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity は taxonomy 記録に保存しない。

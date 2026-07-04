# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-06-03: M23 failure taxonomy / anti-pattern レビュー

### レビュー範囲

- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M23-failure-taxonomy/taxonomy.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M23-failure-taxonomy/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M23-failure-taxonomy/plan.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M23-failure-taxonomy/notes.md`

### 確認観点

- M23 が failure taxonomy / anti-pattern 定義に閉じていること。
- M20 rubric、M21 variant protocol、M22 report template と接続できること。
- 自動採用、自動実装、自動 repository 修正、自動勝敗決定を含めないこと。
- 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を保存しないこと。
- M24 trace-to-diagnosis MVP に渡せる分類 ID と記録候補があること。

### 指摘と対応

- Sub-Agent 指摘: M23 を Future Backlog から現行 milestone に切り出し、標準の milestone file set を用意する必要がある。
  - 評価: 妥当。
  - 対応: `docs/task.md` を更新し、`docs/sprints/sprint1-langfuse-poc/milestones/M23-failure-taxonomy/` に `task.md`、`plan.md`、`questions.md`、`notes.md`、`review.md` を追加した。
- Sub-Agent 指摘: M23 taxonomy と M17 `failure_type` の境界を明記しないと、run / trace 取得失敗と回答品質 anti-pattern が混ざる。
  - 評価: 妥当。
  - 対応: `taxonomy.md`、`notes.md`、`docs/spec.md` に、M17 `failure_type` は実行・trace 取得・除外理由、M23 taxonomy は回答品質・診断可能性・比較プロトコルの分類であると明記した。
- Sub-Agent 指摘: `needs-review` 向けの軽量カテゴリが必要。
  - 評価: 妥当。
  - 対応: `Status Link` を追加し、`needs-review` は `major` または `minor` の failure category と追加確認根拠に接続する形にした。
- Sub-Agent 指摘: M21 比較プロトコル由来の anti-pattern を拾う必要がある。
  - 評価: 妥当。
  - 対応: `F-COMPARISON` と `AP-CONFOUND` を追加した。
- Sub-Agent 指摘: M13 task category ごとの anti-pattern が必要。
  - 評価: 妥当。
  - 対応: `Task-specific Anti-pattern` を追加し、`refactoring`、`bug-investigation`、`test-generation`、`code-review` ごとの代表的 anti-pattern を定義した。
- 再レビュー指摘: task-specific anti-pattern に安定 ID がなく、M24 で `anti_pattern_id` として記録できない。
  - 評価: 妥当。
  - 対応: task-specific anti-pattern に `AP-REF-*`、`AP-BUG-*`、`AP-TEST-*`、`AP-REVIEW-*` の安定 ID を追加し、`anti_pattern_id` は cross-cutting または task-specific の `AP-*` ID と定義した。
- 再レビュー指摘: `F-COMPARISON` / `AP-CONFOUND` を記録するための comparison context 列が不足している。
  - 評価: 妥当。
  - 対応: diagnosis record の列候補に `comparison_id`、`experiment_id`、`experiment_condition`、`prompt_version`、`agent_variant`、`task_run_index` を追加した。
- 再レビュー指摘: diagnosis record の記録単位が未定義。
  - 評価: 妥当。
  - 対応: 1 行を 1 つの `(trace_id, failure_category_id, anti_pattern_id)` に対する分類記録とし、同じ trace に複数分類がある場合は複数行で記録すると定義した。
- 再レビュー指摘: M24 handoff の sanitization 条件が M23 本文より狭い。
  - 評価: 妥当。
  - 対応: M24 handoff と `docs/spec.md` の `evidence_summary` 条件を、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない表現に揃えた。
- Sub-Agent 指摘: M23 は `failure-taxonomy.md` などの別成果物を増やさず、`docs/spec.md` だけに入れるのが最小。
  - 評価: 一部妥当だが不採用。
  - 理由: M23 は taxonomy 表が大きく、M22 の `report-template.md` と同様に milestone-local artifact として分ける方が読みやすく、後続 M24 から参照しやすい。確定仕様の要点は `docs/spec.md` に反映した。

### 検証

- `rg` により、M23 taxonomy の ID、非スコープ境界、sanitization 境界、M17 `failure_type` との境界、M20 / M21 接続を確認する。
- documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

### Sub-Agent 再レビュー

- 仕様準拠 / scope control: blocking / non-blocking 指摘なし。M23 は ready と判断された。
- M24 handoff completeness: task-specific anti-pattern ID、comparison context、記録単位、sanitization 条件に blocking 指摘あり。いずれも妥当と判断し修正した。
- M24 handoff completeness 再確認: 4 件の blocking 指摘はすべて resolved。remaining blocking issue はなく、M23 は ready と判断された。

### 残リスク

- M23 は分類体系の定義であり、実 trace への分類適用、diagnosis record の実装、改善候補生成は後続 milestone で確認する。

# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-06-01: M22 結果レポート雛形レビュー

### レビュー範囲

- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/report-template.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/plan.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/notes.md`

### 確認観点

- M12 measurement schema の `total_tokens`、`turn_count`、`tool_call_count`、`duration_ms`、`error_count`、`success_status` と矛盾しないこと。
- M20 rubric の `pass`、`fail`、`needs-review`、`not-evaluated` を使うこと。
- M21 の baseline / variant 比較表の列と接続できること。
- 新しいコード API、CLI、CSV / JSON schema を追加していないこと。
- 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を保存しないこと。

### 指摘と対応

- 指摘: レポート雛形の `quality_non_regression_status` が自動勝敗判定に見える可能性がある。
  - 対応: 人間が M20 rubric を踏まえて記録する確認状態であり、自動勝敗判定、自動採点、自動採用を意味しないことを明記した。
- 指摘: 研究計画書へ戻す要約に raw content が混入する可能性がある。
  - 対応: レポート全体の冒頭と `docs/spec.md` に、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を記録しない方針を明記した。
- Sub-Agent 指摘: `評価者 ID` に仮名または空欄の制約が明示されていない。
  - 評価: 妥当。
  - 対応: `report-template.md` の `評価者 ID` 欄に「仮名または空欄」を明記した。
- Sub-Agent 指摘: 指標サマリの `median / total` 表現が列ごとに曖昧である。
  - 評価: 妥当。
  - 対応: 指標サマリの列名を M21 比較表に合わせて `*_median`、`error_count_total`、`success_status_counts` に変更した。
- Sub-Agent 指摘: Section 1 と Section 4 の識別情報が重複し、転記ずれが起きやすい。
  - 評価: 妥当。
  - 対応: Section 4 の重複識別列は M21 比較表との接続用に残し、値欄を「Section 1 と同じ」にして二重入力を避ける形にした。
- Sub-Agent 指摘: `docs/spec.md` の章番号が `## 6.` で重複している。
  - 評価: 妥当。M22 起因ではないが、同じ文書を今回編集しており、内容変更なしで補正できる。
  - 対応: `非スコープ` の章番号を `## 7.` に補正した。

### 検証

- `rg -n "total_tokens|turn_count|tool_call_count|duration_ms|success_status|pass|fail|needs-review|not-evaluated" docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/report-template.md docs/spec.md`
- `rg -n "baseline_total_tokens_median|variant_total_tokens_median|quality_non_regression_status|comparison_notes" docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/report-template.md docs/spec.md`
- `rg -n "prompt / response content|tool arguments / results|credential|secret|Base64 header|user identity" docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/report-template.md docs/spec.md`

documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

### Sub-Agent 再レビュー

- 仕様準拠 / functional correctness: blocking / non-blocking 指摘なし。M22 は commit ready と判断された。
- tests / validation / edge cases / regression risk: blocking / non-blocking 指摘なし。自由記述欄への raw content 混入は運用時の残リスクだが、M22 の documentation-only 範囲では許容と判断された。
- maintainability / readability / scope control: blocking / non-blocking 指摘なし。初回指摘はすべて解決済みと判断された。

### 残リスク

実測結果の記入、評価者間の判定ばらつき確認、研究計画書本文への転記は M22 では未実施であり、後続の実測・報告作業で確認する。

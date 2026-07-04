# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-06-01: M21 variant / A-B 計測プロトコルレビュー

### レビュー範囲

- `docs/spec.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M21-variant-protocol/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M21-variant-protocol/plan.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M21-variant-protocol/notes.md`
- `docs/task.md`

### 確認観点

- M12 の measurement schema と Resource Attribute 名に矛盾しないこと。
- M17 の CSV 台帳、失敗・除外・sanitization 方針を破らないこと。
- M20 の `success_status` と品質非劣化 rubric に接続できること。
- M11-M22 の非スコープである改善案生成、自動勝敗判定、自動採用、自動実装に踏み込んでいないこと。

### 指摘と対応

- 指摘: `experiment.id` と `experiment.condition` の役割が重なると、baseline / variant の絞り込み単位が曖昧になる。
  - 対応: `experiment.id` は比較セット全体、`experiment.condition` は同一比較内の条件として分離した。
- 指摘: prompt 変更を variant として扱う場合、`agent.variant` と混同される可能性がある。
  - 対応: prompt 変更自体を比較対象にする場合だけ `prompt.version` を介入軸として変え、比較表に明記する方針を追加した。
- 指摘: 比較表の `quality_non_regression_status` が自動勝敗判定に見える可能性がある。
  - 対応: 人間が M20 rubric を踏まえて記録する確認状態であり、自動勝敗や自動採用を意味しないことを明記した。
- 指摘: 実測 content や identity-bearing な値が比較表に混入する可能性がある。
  - 対応: 比較表に実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を保存しない方針を明記した。

### 検証

- `rg -n "experiment\\.id|experiment\\.condition|prompt\\.version|agent\\.variant|quality_non_regression_status|自動勝敗|自動採用|自動実装" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M21-variant-protocol docs/task.md`
- `rg -n "run_status|failure_type|retry_of_run_id|success_status|not-evaluated|pass|fail|needs-review" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M21-variant-protocol`

documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

### 残リスク

実測 variant trace の取得、評価者間のばらつき確認、比較表への実データ反映、M22 レポート雛形への転記は M21 では未実施であり、後続作業で確認する。

## 2026-06-01: Sub-Agent 観点別レビュー

### レビュー観点

- 仕様準拠と functional correctness。
- tests / validation / edge cases / regression risk。
- maintainability / readability / terminology consistency / scope control。

### 指摘評価と対応

- 指摘: prompt 変更を variant として扱う場合、単一の `prompt_version` 列では baseline / variant それぞれの prompt version を表現できない。
  - 評価: 妥当。
  - 対応: 比較表の識別列を `baseline_prompt_version` / `variant_prompt_version` に分けた。
- 指摘: `experiment.id` を比較セット全体とする定義が、上位要件の「baseline / variant / experiment を分類」と誤読される可能性がある。
  - 評価: 妥当。
  - 対応: `experiment.id` は分類と絞り込みに使う比較セット全体の識別子であり、baseline / variant の区別は同じ `experiment.id` 内の `experiment.condition` で表すことを明記した。
- 指摘: `comparison_id` と `baseline_success_status_counts` / `variant_success_status_counts` の表現形式が未定義である。
  - 評価: 妥当。
  - 対応: `comparison_id` は比較表の行を一意に識別する安定 ID とし、status counts は 4 つの `success_status` 値の件数を保存することを明記した。
- 指摘: 比較表の指標列が将来変更に弱い可能性がある。
  - 評価: 一部妥当。ただし M21 の完了条件は比較表の初期形式定義であり、後続指標の詳細な拡張設計までは不要。
  - 対応: 比較表は M21 時点の初期列であり、後続 milestone で指標追加する場合も secret、credential、content、実 user identity を含めない方針を明記した。

### 再レビュー方針

修正後に同じ観点で Sub-Agent に read-only 再レビューを依頼し、追加の blocking 指摘がなければ M21 変更だけを commit / push する。

# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-06-01: M20 rubric 定義レビュー

### レビュー範囲

- `docs/spec.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M20-quality-rubric/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M20-quality-rubric/notes.md`
- `docs/task.md`

### 確認観点

- M12 の `success_status` 値域と矛盾しないこと。
- M13 の 4 類型タスクに対応した評価観点になっていること。
- M17 の記録様式と sanitization 方針に反していないこと。
- M11-M22 の非スコープである改善案生成、自動採用、自動実装、自動勝敗判定に踏み込んでいないこと。

### 指摘と対応

- 指摘: M19 が未完了のまま M20 を完了扱いにすると、M20 が実測結果の採点まで行ったと誤読され得る。
  - 対応: M20 は rubric 定義であり、M19 の実測 trace 採点や再集計は範囲外であることを `notes.md` に明記した。
- 指摘: `fail` の基準が広すぎると、軽微な根拠不足まで失敗扱いになり評価が粗くなる。
  - 対応: 重大な仕様違反や主要観点欠落を `fail`、方針は妥当だが根拠や確認観点が不足する場合を `needs-review` として分離した。
- 指摘: 評価記録に trace content や identity-bearing な値が混入する可能性がある。
  - 対応: `evaluation_notes` などに実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を保存しない方針を `docs/spec.md` に明記した。
- Sub-Agent 指摘: `code-review` の `fail` 条件である「仕様違反を見落とす」が、軽微な見落としまで含むのか曖昧だった。
  - 妥当。`docs/spec.md` では、明確に seed された主要な仕様違反の見落としを `fail`、軽微または期待観点外の不足を `needs-review` として分離した。
- Sub-Agent 指摘: M19 の未コミット差分は M20 commit から除外すべきである。
  - 妥当。M20 commit では `docs/sprints/sprint1-langfuse-poc/milestones/M19-baseline-measurement/*` を stage しない。`docs/task.md` の M19 status 行は、M20 先行完了の整合性を示す index 更新として M20 commit に含める。

### 検証

- `rg -n "success_status|pass|fail|needs-review|not-evaluated|rubric|自動採点|自動勝敗|自動採用|自動実装" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M20-quality-rubric docs/task.md`
- `rg -n "maint-refactor-001|maint-bug-001|maint-test-001|maint-review-001|refactoring|bug-investigation|test-generation|code-review" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M20-quality-rubric`

documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

### 残リスク

実測 trace の個別採点、評価者間のばらつき確認、variant 比較表への反映は M20 では未実施であり、M21 以降または実評価時に確認する。

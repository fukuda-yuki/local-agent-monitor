# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-24 Self Review

### レビュー範囲

- `docs/spec.md`
- `docs/requirements.md`
- `docs/sprints/sprint1-langfuse-poc/knowledge/research-measurement.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M11-scope-alignment/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M11-scope-alignment/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M11-scope-alignment/questions.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M11-scope-alignment/review.md`

### 観点

- 研究実施計画書と repository source of truth のスコープ整合。
- M12-M22 へ渡す未決事項の明確さ。
- 自動採用、自動実装、自動 repository 修正、勝敗の自動決定が M11-M22 に入っていないこと。

### 結果

- 重大な指摘なし。
- VS Code GitHub Copilot Chat と GitHub Copilot CLI の両方を研究計測対象にする方針を `docs/spec.md` に明記した。
- 研究実施計画書の自己改善デモは、自動採用を含めず、改善案生成・評価の提案として M23 以降候補に分離した。
- 補助指標は取得可能性または算出根拠を条件に M12 の任意列候補へ送る扱いにした。
- sub-agent レビューで指摘された編集受容 / 生存率の転記漏れ、M13 / M15 / M20 / M22 の handoff 補足、`docs/task.md` Future Backlog の非スコープ制約再掲を反映した。

### 残リスク

- 研究実施計画書は外部 URL を参照しているため、将来内容が変わる可能性がある。M11 では 2026-05-24 時点の参照先を原本として扱った。
- GenAI Semantic Conventions の採用バージョンと Langfuse export で取得できる補助指標は M12 / M14 で追加確認が必要。

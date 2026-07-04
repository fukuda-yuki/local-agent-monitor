# M13: 模擬保守タスクセット定義

## 目的

baseline / variant 比較に使う小さく再現可能な模擬保守タスクセットを定義する。

## 完了条件

- [x] タスク類型を定義する
- [x] 合成 fixture と実行条件を定義する
- [x] 機密データや実ユーザーデータを使わないことを確認する
- [x] `docs/spec.md` に確定仕様を反映する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-24: `docs/spec.md` に `maint-refactor-001`、`maint-bug-001`、`maint-test-001`、`maint-review-001` の 4 件を M13 初期タスクセットとして反映した。
- 2026-05-24: `docs/spec.md` で `task.category` を `refactoring`、`bug-investigation`、`test-generation`、`code-review` の 4 値に固定し、M12 measurement schema と整合することを確認した。
- 2026-05-24: fixture は synthetic .NET code fixture とし、実ユーザーデータ、顧客データ、秘密情報、実 credential、実運用ログを使わない方針を `docs/spec.md` と `notes.md` に記録した。
- 2026-05-24: `prompt.version=v1`、`repo.snapshot=synthetic-dotnet-fixture-v1` を初期値として定義し、追加タスク数、反復回数、除外基準、rubric 確定は後続 milestone に委譲した。
- 2026-05-24: `rg -n "maint-(refactor|bug|test|review)-001|task.category|prompt.version|repo.snapshot|自動採用|自動実装|勝敗" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M13-maintenance-task-set docs/sprints/sprint1-langfuse-poc/milestones/M12-measurement-schema` で文書整合を確認した。
- 2026-05-24: documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

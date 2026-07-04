# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-24 Self-review

### レビュー範囲

- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M13-maintenance-task-set/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M13-maintenance-task-set/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M13-maintenance-task-set/review.md`

### 観点

- `docs/requirements.md` と `docs/spec.md` の研究計測フェーズに沿っていること。
- M12 measurement schema の `task.id`、`task.category`、`prompt.version`、`repo.snapshot` と矛盾しないこと。
- M13 が実装、live 計測、fixture ファイル作成、rubric 確定、改善案生成、自動採用、自動勝敗判定に踏み込んでいないこと。
- 合成 fixture のみを対象にし、実ユーザーデータ、顧客データ、秘密情報、実 credential、実運用ログを使わないこと。

### 指摘と対応

- 指摘: 4 類型だけでは後続 milestone が `task.id` と prompt を参照できない。
  - 対応: `docs/spec.md` に 4 件の初期タスク、入力 prompt、fixture、実行条件、evidence、暫定品質確認観点を追加した。
- 指摘: M20 rubric の領域まで定義すると M13 のスコープを越える。
  - 対応: M13 では暫定的な品質確認観点に留め、`pass`、`fail`、`needs-review` の厳密な判定基準は M20 に委譲した。
- 指摘: fixture ファイルを M13 で作ると M15 の入力 fixture 作成と責務が重なる。
  - 対応: M13 では fixture の内容、入力条件、期待観点の文書化だけに留めると明記した。
- 指摘: `docs/task.md` で M13 を完了扱いにしたが、レビュー範囲に `docs/task.md` が含まれていない。
  - 対応: レビュー範囲に `docs/task.md` を追加し、top-level milestone index の M13 完了更新を確認対象に含めた。
- 指摘: `maint-bug-001` の `total_tokens` 不一致条件が M12 の usage 由来方針と誤読され得る。
  - 対応: source の `total_tokens` が欠損している場合の fallback 合計不具合に限定した。
- 指摘: `maint-test-001` の必須属性欠損が validation error 固定と誤読され得る。
  - 対応: M12 の null / 空欄表現と補足記録に接続できることを確認する観点として明記した。
- 指摘: M13 の dotted Resource Attribute 名と M12 の snake_case dataset 列の境界が読み取りづらい。
  - 対応: M13 の dotted 名は Resource Attribute key であり、M12 dataset では snake_case 列に正規化されることを `docs/spec.md` に追記した。

### 妥当性判断

M13 の完了条件であるタスク類型、合成 fixture と実行条件、機密データ不使用、`docs/spec.md` 反映、レビュー記録を満たしている。
`docs/task.md` の top-level milestone index で M13 が完了に更新されていることも確認した。
documentation-only のため、`dotnet build` / `dotnet test` は実行対象外で妥当。

### 残リスク

- 実 fixture ファイルの具体的な形は M15 で確定するため、M15 実装時に M13 の fixture 説明を必要に応じて精緻化する可能性がある。
- `pass` / `fail` / `needs-review` の評価一貫性は M20 の rubric 定義に依存する。

### Sub-Agent 再レビュー

2026-05-24 に、schema / downstream usability 観点と review record / auditability 観点で Sub-Agent 再レビューを実施した。

- schema / downstream usability: `total_tokens` fallback 条件、必須 Resource Attribute 欠損時の null / 空欄写像、dotted Resource Attribute key から snake_case dataset 列への正規化について、前回指摘は解消済みと判断された。
- review record / auditability: `docs/task.md` をレビュー範囲に含めたこと、top-level milestone index の M13 完了更新を確認したことが記録済みであり、前回指摘は解消済みと判断された。
- 新しい actionable issue は指摘されなかった。

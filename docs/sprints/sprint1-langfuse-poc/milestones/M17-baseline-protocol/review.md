# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-25: Self-review

### レビュー範囲

- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M17-baseline-protocol/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M17-baseline-protocol/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M17-baseline-protocol/review.md`

### 観点

- M12 measurement schema、M13 maintenance task set、M16 counting rules と整合しているか。
- M18 / M19 に実測や品質判定の責務を漏らしていないか。
- secret、実データ、credential を保存しない方針が維持されているか。
- CSV 台帳が M18 dry run と M19 本計測の両方で使えるか。

### 結果

指摘なし。

M17 の変更は baseline 実行手順と記録様式の仕様化に閉じており、Copilot / Langfuse の live 実行、品質非劣化判定、variant 比較、集計 CLI 実装には踏み込んでいない。
`client.kind` 別に反復数を数える方針は、VS Code GitHub Copilot Chat と GitHub Copilot CLI を研究計測対象に含める既存仕様と整合している。
CSV 台帳には M12 の measurement schema へ接続する token / count / duration / status 系列と、M17 固有の失敗・除外記録を含めた。

### 残リスク

M18 の dry run で、実際の Langfuse trace 検索や trace URL 記録に不足列が見つかる可能性がある。
その場合は M18 の検証記録に不足を残し、必要に応じて M17 仕様へ小さく戻す。

## 2026-05-25: Sub-Agent review and remediation

### レビュー範囲

3 つの read-only Sub-Agent で観点別レビューを実施した。

- 仕様整合 / milestone 境界
- CSV 台帳の実行可能性 / M18-M19 readiness
- データ扱い / security / validation record integrity

### 指摘と判断

- 指摘: M18 / M19 の `task.md` が M17 で確定した `client.kind` 別の実行量と不整合だった。
  - 妥当。M18 / M19 の目的行を M17 仕様に同期した。
- 指摘: `failed` と `trace-missing` の retry / 有効 N への counting が未定義だった。
  - 妥当。`failed`、`trace-missing`、`excluded` は baseline の有効 N に数えず、再実行時は新しい `run_id` と `retry_of_run_id` を使う方針を追加した。
- 指摘: `trace_found` と `failure_type` の値域が未定義だった。
  - 妥当。`trace_found` は `true` / `false` / 空欄、`failure_type` は限定された理由コードまたは空欄にした。
- 指摘: `langfuse_trace_id` から M12 dataset の `trace_id` への写像が暗黙だった。
  - 妥当。M17 台帳の `langfuse_trace_id` を M12 schema の `trace_id` へ写像する方針を追加した。
- 指摘: M18 の 1 類型が未指定だった。
  - 妥当。M18 dry run の対象を `maint-refactor-001` に固定した。
- 指摘: 実データ混入時に、除外記録だけでは汚染 trace / 台帳参照の削除・最小化が不足していた。
  - 妥当。汚染 trace の削除またはローカル Langfuse volume 破棄、台帳には sanitized exclusion reason だけを残す方針を追加した。
- 指摘: `operator_id`、`resource_attributes`、`prompt_used` の保存内容と commit 可否が未定義だった。
  - 妥当。CSV 台帳は repository commit 成果物ではないこと、共有や保存時は sanitization すること、各列の最小化方針を追加した。
- 指摘: 既存 knowledge にデモ用ログイン値が literal に残っていた。
  - M17 差分の直接ブロッカーではないが、現行 secret 方針との誤読を避けるため採用した。デモ用初期ログインという記述に丸めた。

### 再レビュー結果

採用した指摘については、`docs/spec.md`、M17 notes、M18 task、M19 task、Phase 1 knowledge に反映済み。
M17 の責務は引き続き baseline 手順と記録様式の仕様化に閉じており、live Copilot 実行、Langfuse 実測、品質非劣化判定、variant 比較は後続 milestone に残している。

## 2026-05-25: Sub-Agent re-review

### レビュー範囲

採用指摘の反映後、2 つの read-only Sub-Agent で再レビューを実施した。

- 仕様整合 / M18-M19 readiness
- データ扱い / security

### 結果

再レビューでは、いずれの観点でも actionable blocker は残っていないと判断された。

確認された点:

- M18 / M19 の実行量は M17 仕様、M17 notes、M18 task、M19 task で同期されている。
- `failed`、`trace-missing`、`excluded` は baseline の有効 N に数えず、再実行は新しい `run_id` と `retry_of_run_id` で追跡する。
- `trace_found`、`failure_type` の値域と `langfuse_trace_id` から M12 schema の `trace_id` への写像が明記されている。
- CSV 台帳の非 commit 方針、共有時の sanitization、`operator_id` / `resource_attributes` / `prompt_used` の最小化、実データ混入時の trace 削除または volume 破棄が明記されている。

残リスクは M18 dry run で実際の台帳記入、trace 検索、集計接続を確認する性質のものに限定される。

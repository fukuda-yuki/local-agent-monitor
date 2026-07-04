# M17: baseline 実行手順と記録様式

## 目的

baseline 計測の実行前チェック、環境変数、Langfuse trace id 記録、失敗時の扱い、除外基準を定義する。

## 完了条件

- [x] baseline 実行手順を定義する
- [x] 記録様式を定義する
- [x] 失敗時、欠損 trace、手動除外の扱いを定義する
- [x] `docs/spec.md` に確定仕様を反映する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-25: `docs/spec.md` に、baseline 実行前チェック、実行単位、`client.kind` 別の反復数、CSV 台帳列、`run_status`、除外 run の再実行方針を定義した。
- 2026-05-25: `docs/sprints/sprint1-langfuse-poc/milestones/M17-baseline-protocol/notes.md` に、判断理由、CSV 台帳テンプレート、失敗・欠損 trace・除外の扱い、実行前チェックを記録した。
- 2026-05-25: M17 は documentation-only のため、Copilot / Langfuse の live 実行と自動テストは実施していない。
- 2026-05-25: `rg` による文書整合確認を実施した。
- 2026-05-25: 仕様整合、CSV 台帳 readiness、データ扱いの 3 観点で Sub-Agent レビューを実施し、採用した指摘を `docs/spec.md`、M17 notes、M18 task、M19 task、Phase 1 knowledge に反映した。

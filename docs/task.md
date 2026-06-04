# Task Index

この文書は repository 全体の milestone index である。
詳細なチェックリスト、検証記録、作業メモ、レビュー記録は `docs/milestones/<milestone-slug>/` に置く。

プロダクト仕様・実装判断の正は `docs/spec.md` である。
ただし、`docs/requirements.md` を上位要件とし、`docs/spec.md` はその詳細仕様として扱う。
GitHub Issue は既定の作業単位にしない。ユーザーが明示的に作成・参照を指示した場合だけ使う。

## 完了済み概要

| Milestone | 状態 | 概要 |
| --- | --- | --- |
| M0-M4 | 完了 | Phase 0: ローカル Aspire Dashboard 疎通確認 |
| M5 | 完了 | Phase 1 ドキュメント再編 |
| M6 | 完了 | Langfuse ローカル起動 |
| M7 | 完了 | Phase 1 クライアント設定 |
| M8 | 完了 | Phase 1 手動ライブ確認 |
| M9 | 完了 | OTel Collector 経由送信の仕様化と最小実装 |
| M10 | 完了 | Phase 1 M-docs-restructure |

M0-M10 の詳細な履歴は `docs/archive/review/` と `docs/knowledge/phase1-langfuse.md` を参照する。

## 現行 Milestones

| Milestone | 状態 | 詳細 |
| --- | --- | --- |
| M11: 研究計画書とのスコープ整合 | 完了 | [`docs/milestones/M11-scope-alignment/task.md`](milestones/M11-scope-alignment/task.md) |
| M12: 研究用 measurement schema 定義 | 完了 | [`docs/milestones/M12-measurement-schema/task.md`](milestones/M12-measurement-schema/task.md) |
| M13: 模擬保守タスクセット定義 | 完了 | [`docs/milestones/M13-maintenance-task-set/task.md`](milestones/M13-maintenance-task-set/task.md) |
| M14: Langfuse export / API 調査 | 完了 | [`docs/milestones/M14-langfuse-export/task.md`](milestones/M14-langfuse-export/task.md) |
| M15: 集計 CLI / script MVP | 完了 | [`docs/milestones/M15-aggregation-cli/task.md`](milestones/M15-aggregation-cli/task.md) |
| M16: turn count / tool call count 算出ルール | 完了 | [`docs/milestones/M16-counting-rules/task.md`](milestones/M16-counting-rules/task.md) |
| M17: baseline 実行手順と記録様式 | 完了 | [`docs/milestones/M17-baseline-protocol/task.md`](milestones/M17-baseline-protocol/task.md) |
| M18: baseline 計測の小規模 dry run | 完了 | [`docs/milestones/M18-baseline-dry-run/task.md`](milestones/M18-baseline-dry-run/task.md) |
| M19: baseline 本計測 | 完了 | [`docs/milestones/M19-baseline-measurement/task.md`](milestones/M19-baseline-measurement/task.md) |
| M20: 品質非劣化 rubric 定義 | 完了 | [`docs/milestones/M20-quality-rubric/task.md`](milestones/M20-quality-rubric/task.md) |
| M21: variant / A-B 計測プロトコル | 完了 | [`docs/milestones/M21-variant-protocol/task.md`](milestones/M21-variant-protocol/task.md) |
| M22: 結果レポート雛形 | 完了 | [`docs/milestones/M22-report-template/task.md`](milestones/M22-report-template/task.md) |
| M23: failure taxonomy / anti-pattern 定義 | 完了 | [`docs/milestones/M23-failure-taxonomy/task.md`](milestones/M23-failure-taxonomy/task.md) |
| M24: trace-to-diagnosis MVP | 完了 | [`docs/milestones/M24-trace-to-diagnosis/task.md`](milestones/M24-trace-to-diagnosis/task.md) |
| M25: improvement proposal generator | 完了 | [`docs/milestones/M25-improvement-proposal-generator/task.md`](milestones/M25-improvement-proposal-generator/task.md) |
| M26: proposal evaluator | 完了 | [`docs/milestones/M26-proposal-evaluator/task.md`](milestones/M26-proposal-evaluator/task.md) |
| M27: human approval workflow | 完了 | [`docs/milestones/M27-human-approval-workflow/task.md`](milestones/M27-human-approval-workflow/task.md) |
| M28: Aspire AppHost 再評価と AI 診断 workflow | 作業中 | [`docs/milestones/M28-aspire-ai-diagnostics/task.md`](milestones/M28-aspire-ai-diagnostics/task.md) |

## Future Backlog

M29 以降は M28 完了後に別 milestone として切り出す。
M29 以降候補も人間採否の提案・評価までを既定とし、自動採用、自動実装、repository 自動修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定は含めない。

## Follow-up

- 共有環境、実データ、社内サーバー検証が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。

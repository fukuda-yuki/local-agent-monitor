# Task Index

この文書は repository 全体の milestone index である。
状態別に「現行」「完了済み」「Backlog」を分けず、すべての作業単位を一つの表で確認できるようにする。

詳細なチェックリスト、検証記録、作業メモ、レビュー記録は `docs/milestones/<milestone-slug>/` に置く。
古い完了記録と過去レビューは `docs/archive/review/` に置く。

プロダクト仕様・実装判断の正は `docs/spec.md` である。
ただし、`docs/requirements.md` を上位要件とし、`docs/spec.md` はその詳細仕様として扱う。
GitHub Issue は既定の作業単位にしない。ユーザーが明示的に作成・参照を指示した場合だけ使う。

## Milestone Index

| Milestone | 状態 | 概要 / 詳細 |
| --- | --- | --- |
| M0-M4 | 完了 | Phase 0: ローカル Aspire Dashboard 疎通確認。詳細履歴は `docs/archive/review/` と `docs/knowledge/phase0-aspire.md` を参照 |
| M5 | 完了 | Phase 1 ドキュメント再編 |
| M6 | 完了 | Langfuse ローカル起動 |
| M7 | 完了 | Phase 1 クライアント設定 |
| M8 | 完了 | Phase 1 手動ライブ確認 |
| M9 | 完了 | OTel Collector 経由送信の仕様化と最小実装 |
| M10 | 完了 | Phase 1 M-docs-restructure |
| M11 | 完了 | [研究計画書とのスコープ整合](milestones/M11-scope-alignment/task.md) |
| M12 | 完了 | [研究用 measurement schema 定義](milestones/M12-measurement-schema/task.md) |
| M13 | 完了 | [模擬保守タスクセット定義](milestones/M13-maintenance-task-set/task.md) |
| M14 | 完了 | [Langfuse export / API 調査](milestones/M14-langfuse-export/task.md) |
| M15 | 完了 | [集計 CLI / script MVP](milestones/M15-aggregation-cli/task.md) |
| M16 | 完了 | [turn count / tool call count 算出ルール](milestones/M16-counting-rules/task.md) |
| M17 | 完了 | [baseline 実行手順と記録様式](milestones/M17-baseline-protocol/task.md) |
| M18 | 完了 | [baseline 計測の小規模 dry run](milestones/M18-baseline-dry-run/task.md) |
| M19 | 完了 | [baseline 本計測](milestones/M19-baseline-measurement/task.md) |
| M20 | 完了 | [品質非劣化 rubric 定義](milestones/M20-quality-rubric/task.md) |
| M21 | 完了 | [variant / A-B 計測プロトコル](milestones/M21-variant-protocol/task.md) |
| M22 | 完了 | [結果レポート雛形](milestones/M22-report-template/task.md) |
| M23 | 完了 | [failure taxonomy / anti-pattern 定義](milestones/M23-failure-taxonomy/task.md) |
| M24 | 完了 | [trace-to-diagnosis MVP](milestones/M24-trace-to-diagnosis/task.md) |
| M25 | 完了 | [improvement proposal generator](milestones/M25-improvement-proposal-generator/task.md) |
| M26 | 完了 | [proposal evaluator](milestones/M26-proposal-evaluator/task.md) |
| M27 | 完了 | [human approval workflow](milestones/M27-human-approval-workflow/task.md) |
| M28 | 完了 | [Aspire AppHost 再評価と AI 診断 workflow](milestones/M28-aspire-ai-diagnostics/task.md) |
| User-facing docs refresh | 完了 | README を入口として再構成し、`docs/getting-started.md` に初回利用者向けの準備・起動・設定・確認手順を追加 |
| M29 以降 | 未着手候補 | M28 完了後に必要に応じて別 milestone として切り出す。人間採否の提案・評価までを既定とし、自動採用、自動実装、repository 自動修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定は含めない |

## Follow-up

- 共有環境、実データ、社内サーバー検証が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。
- 利用者向けの申請・導入・初回利用手順は `README.md` と `docs/getting-started.md` を入口にする。仕様値や既定構成を変える場合は、先に `docs/spec.md` を更新してから利用者向け文書へ反映する。

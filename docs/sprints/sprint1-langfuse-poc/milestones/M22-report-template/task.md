# M22: 結果レポート雛形

## 目的

研究計画書に戻せる Markdown レポート雛形を追加する。

## 完了条件

- [x] token、turn count、tool call count、duration、success status、観察メモを記録できる雛形を作る
- [x] baseline / variant 比較結果を記録できる形にする
- [x] M12 schema と M20 rubric と整合させる
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-06-01: `docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/report-template.md` を追加し、M12 / M20 / M21 と整合する Markdown レポート雛形を定義した。
- 2026-06-01: `docs/spec.md` に M22 のレポート雛形仕様を反映した。
- 2026-06-01: `rg` により、M22 雛形が `total_tokens`、`turn_count`、`tool_call_count`、`duration_ms`、`success_status`、M20 status 値、M21 比較列、secret / credential / raw content を保存しない注意書きを含むことを確認した。
- 2026-06-01: 観点別 Sub-Agent review の指摘に基づき、評価者 ID の仮名制約、指標サマリの集計単位、識別情報の重複入力回避、`docs/spec.md` の章番号を修正した。
- 2026-06-01: 同じ 3 観点で Sub-Agent 再レビューを実施し、blocking / non-blocking 指摘なしを確認した。
- 2026-06-01: documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

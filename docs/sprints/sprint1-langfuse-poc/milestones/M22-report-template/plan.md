# Plan

## 2026-06-01

M22 は documentation-only milestone として、研究計画書へ転記しやすい Markdown レポート雛形を追加する。

- `docs/sprints/sprint1-langfuse-poc/milestones/M22-report-template/report-template.md` を追加し、レポート識別情報、実行範囲、指標サマリ、baseline / variant 比較、品質非劣化評価、観察メモを固定セクションとして定義する。
- `docs/spec.md` に M22 のレポート雛形仕様を追加し、M12 measurement schema、M20 rubric、M21 comparison protocol との接続を明記する。
- 新しいコード API、CLI、CSV / JSON schema は追加しない。
- 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity はレポートに保存しない方針を明記する。
- M19 の進行中変更には触れない。

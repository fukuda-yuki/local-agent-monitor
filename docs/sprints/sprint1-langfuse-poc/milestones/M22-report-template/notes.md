# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-06-01

- M19 は `vscode-copilot-chat` 側 trace 取得 blocker により進行中だが、M22 は実測結果ではなく記録様式の定義であるため先行完了可能と判断した。
- レポート雛形は M12 の集計列、M20 の `success_status` / `quality_non_regression_status`、M21 の baseline / variant 比較表に合わせた。
- レポートには実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を保存しない方針を採用した。

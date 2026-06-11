# M1 Questions

## Resolved

| Question | Decision |
| --- | --- |
| diagnosis candidate と M24 diagnosis record を同一 command で扱うか | 分ける。`generate-diagnosis-candidates` は candidate 専用 schema を出力し、M24 record への変換は後続で扱う |
| auto-decision record を M27 human decision record の拡張にするか | 分ける。`generate-auto-decisions` は auto-decision 専用 schema を出力する |
| sensitive output はどこに置くか | 既定は `tmp/sprint3-sensitive/<run_id>/`。repository に保存・commit しない |
| synthetic fixture は sensitive content を含むか | 含めない。synthetic secret-like placeholder は使えるが、実 credential / secret は使わない |

## Remaining

| Question | Handling |
| --- | --- |
| candidate pipeline と M24-M27 の接続方法 | M5 で mapping または obsolescence 判断を決める |
| Sprint4 repository file 自動修正の allowlist / rollback / diff preview | Sprint4 planning で扱う |

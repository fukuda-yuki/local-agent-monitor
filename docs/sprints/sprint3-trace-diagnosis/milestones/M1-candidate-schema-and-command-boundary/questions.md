# M1 Questions

## Resolved

| Question | Decision |
| --- | --- |
| diagnosis candidate と M24 diagnosis record を同一 command で扱うか | 分ける。`generate-diagnosis-candidates` は candidate 専用 schema を出力し、M24 record への変換は後続で扱う |
| auto-decision record を M27 human decision record の拡張にするか | 分ける。`generate-auto-decisions` は auto-decision 専用 schema を出力する |
| sensitive output はどこに置くか | 既定は `tmp/sprint3-sensitive/<run_id>/`。repository に保存・commit しない |
| synthetic fixture は sensitive content を含むか | 含めない。synthetic secret-like placeholder は使えるが、実 credential / secret は使わない |
| candidate pipeline と M24-M27 の関係 | M24-M27 は置換せず互換性維持対象とする。Sprint3 は前段 candidate pipeline とし、M2 で adapter / mapping contract を確定する |

## Remaining

| Question | Handling |
| --- | --- |
| candidate pipeline から M24 diagnosis record への列 mapping | M2 で実装前に確定し、M5 で反映する |
| Sprint4 repository file 自動修正の allowlist / rollback / diff preview | Sprint4 planning で扱う |

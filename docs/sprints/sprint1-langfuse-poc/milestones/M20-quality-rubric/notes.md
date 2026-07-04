# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-06-01

M20 は M19 の baseline 本計測結果そのものを採点する milestone ではなく、後続の baseline / variant 比較で使う人間評価用 rubric を定義する milestone として扱った。
M19 は `vscode-copilot-chat` 側の representative trace 取得 blocker により未完了だが、M20 の入力は M12 measurement schema、M13 の 4 類型タスク、M17 の記録様式で足りるため、先行着手してよいと判断した。

`success_status` は M12 の 4 値を維持し、以下の意味に固定した。

| 値 | 意味 |
| --- | --- |
| `pass` | 類型ごとの必須観点を満たし、仕様違反や重大な確認漏れがない |
| `fail` | 仕様違反、誤った結論、危険または過剰な提案、主要観点の欠落がある |
| `needs-review` | 方針は妥当だが根拠や確認観点が不足し、人間の追加判断が必要 |
| `not-evaluated` | 人間評価前の既定値 |

4 類型の rubric は M13 のタスク意図に合わせた。
`refactoring` は外部仕様維持と過剰抽象化回避、`bug-investigation` は再現条件・原因・最小修正案・回帰確認の分離、`test-generation` は deterministic な正常系・境界・異常系、`code-review` は重大度順の仕様根拠付き指摘を重視する。

評価記録には M12 の任意列 `evaluator_id`、`evaluation_notes`、`evaluated_at` を使えることにした。
ただし、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity は保存しない。

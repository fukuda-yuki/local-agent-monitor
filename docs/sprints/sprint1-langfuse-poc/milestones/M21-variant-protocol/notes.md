# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-06-01

M21 は、M19 の baseline 本計測結果を使った実比較ではなく、後続比較のための計測プロトコル定義として扱った。
M19 は `vscode-copilot-chat` 側の representative trace 取得 blocker により未完了だが、M21 の入力は M12 measurement schema、M17 の記録様式、M20 の品質非劣化 rubric で足りるため、先行完了してよいと判断した。

`experiment.id` は比較セット全体、`experiment.condition` は同一比較セット内の条件、`prompt.version` は task prompt の版、`agent.variant` は agent / instructions / skill などの介入内容として分離した。
これにより、同じ `experiment.id` の中で `baseline` と `variant` を比較しつつ、prompt 変更を比較対象にする場合だけ `prompt.version` を介入軸として明示できる。

実行順序は task / client ごとに baseline と variant を交互にする。
これは、実行時刻やローカル環境状態の偏りを片方の条件に寄せないための最小ルールである。

比較表は中央値を主に使う形式にした。
N=10 の小規模計測では異常長 run や外れ値の影響を受けやすいため、平均値よりも中央値を既定にする。
ただし、品質の最終判断は M20 rubric に基づく人間評価であり、自動勝敗判定や自動採用には使わない。

Sub-Agent review では、prompt 変更を比較対象にする場合に単一の `prompt_version` 列では baseline / variant の差分を構造化できない点、`experiment.id` と `experiment.condition` の役割が上位要件から誤読され得る点、`comparison_id` と success status counts の表現が未定義である点が指摘された。
いずれも妥当と判断し、比較表を `baseline_prompt_version` / `variant_prompt_version` に分け、同一比較セットでは同じ `experiment.id` を付与する方針と、`comparison_id` / status counts の最小定義を `docs/spec.md` に追加した。

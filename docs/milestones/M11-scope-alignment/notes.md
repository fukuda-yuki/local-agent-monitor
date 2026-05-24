# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 研究実施計画書との差分確認

入力原本:

- <https://github.com/fukuda-yuki/misc/blob/main/05_output/00_%E7%A0%94%E7%A9%B6%E5%AE%9F%E6%96%BD%E8%A8%88%E7%94%BB%E6%9B%B8.md>

M11 では、上記の研究実施計画書と `docs/requirements.md` / `docs/spec.md` を照合した。

| 論点 | 研究実施計画書 | repository での扱い |
| --- | --- | --- |
| 観測基盤 | OpenTelemetry / Langfuse / OTel Collector を研究用に整備する | M0-M10 の Phase 1 Langfuse PoC、VS Code 収集、Copilot CLI 収集、M9 Collector 最小サンプルを観測基盤として維持する |
| 実測対象 | GitHub Copilot、特に VS Code Copilot Chat を中心に記述している | ユーザー決定により、VS Code Copilot Chat と GitHub Copilot CLI の両方を研究計測対象に含める |
| タスク類型 | リファクタリング、バグ調査・修正提案、テスト生成、コードレビュー支援の 4 類型 | 現行 `docs/spec.md` と整合しているため維持する |
| 主指標 | input / output token 合計、turn 数、tool 呼び出し数、実行時間、成功率 | M12-M16 で schema と算出ルールを確定する。現行の必須列と整合している |
| 補助指標 | 編集受容 / 生存率、cache token、reasoning token、model ID、IDE 拡張バージョン、推定コストまたは相対コスト指数 | Copilot OTel または Langfuse export で取得できる場合、もしくは算出根拠を説明できる場合に限って M12 の任意列候補にする |
| baseline | 同一タスク、同一 prompt、同一条件で N 回計測し、初期値 N=10 | 現行 `docs/spec.md` と整合しているため維持する |
| 品質非劣化 | 4 類型ごとに人間評価基準を定義する | M20 で `pass` / `fail` / `needs-review` の rubric として定義する |
| variant / A-B | 介入軸を `experiment.id` でラベルし、条件間比較する | M21 で `experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant` の使い分けを確定する |
| 自己改善デモ | 改善案を生成・評価・自動採用する小規模デモを成果候補に含めている | 自動採用、自動実装、自動 repository 修正は M11-M22 の範囲外。改善案生成・評価の提案は M23 以降の後続候補に分離する |

## 判断

- 研究計測フェーズはゼロベースで作り直さず、M0-M10 の観測基盤を継承する。
- M11-M22 は研究計測、集計、baseline / variant 比較、品質非劣化 rubric、レポート雛形までを扱う。
- GitHub Copilot CLI は研究実施計画書の中心対象ではないが、Phase 1 で既に観測対象としているため、研究計測対象に含める。
- 改善案生成・評価は M11-M22 の実装対象にしない。扱う場合は M23 以降の trace-driven improvement loop として切り出す。
- 自動採用、自動実装、patch / diff 生成、commit、push、pull request 作成、勝敗の自動決定は引き続き非スコープとする。

## 観点別レビュー結果

- 2026-05-24: 3 つの read-only sub-agent で、仕様整合、後続 milestone 影響、非スコープ逸脱の観点別レビューを実施した。
- 仕様整合レビューでは、編集受容 / 生存率が研究実施計画書の補助指標から M12 任意列候補へ転記されていない点を指摘し、M11 文書と `docs/spec.md` に反映した。
- 後続 milestone 影響レビューでは、M13 / M15 / M20 / M22 の詳細未決事項は各 milestone task で扱うことを M11 handoff に補足する方針とした。
- 非スコープ逸脱レビューでは、`docs/task.md` の Future Backlog 単独読みで M23 以降の制約が弱く見える点を指摘し、非スコープ制約を再掲した。

# Questions

ユーザー確認事項が複数ある場合に記録する。

## M11 で確定した事項

- 研究実施計画書の原本は <https://github.com/fukuda-yuki/misc/blob/main/05_output/00_%E7%A0%94%E7%A9%B6%E5%AE%9F%E6%96%BD%E8%A8%88%E7%94%BB%E6%9B%B8.md> とする。
- M11-M22 の研究計測対象には、VS Code GitHub Copilot Chat に加えて GitHub Copilot CLI も含める。
- 研究実施計画書の自己改善デモは、自動採用まで含めて M11-M22 に入れない。改善案生成・評価の提案は M23 以降の後続候補として扱う。

## 後続 milestone へ渡す未決事項

- M12: OpenTelemetry GenAI Semantic Conventions の採用バージョンを決める。
- M12: 編集受容 / 生存率、cache token、reasoning token、model ID、IDE / CLI version、相対コスト指数を任意列に含める条件を決める。
- M12-M16: VS Code GitHub Copilot Chat と GitHub Copilot CLI の属性差分を schema と分類ルールでどう吸収するかを決める。
- M13 / M15 / M20 / M22: 模擬保守タスク、集計 MVP、品質 rubric、レポート雛形の詳細は各 milestone task で未決として扱う。
- M14: Langfuse export / API から補助指標を取得できるかを確認する。
- M21: `experiment.id` と `experiment.condition` の役割分担を、研究計画書の A/B 計測と矛盾しない形で確定する。
- M23 以降: 改善案生成・評価デモを扱う場合、自動採用、自動実装、自動 repository 修正を含めない境界を改めて定義する。

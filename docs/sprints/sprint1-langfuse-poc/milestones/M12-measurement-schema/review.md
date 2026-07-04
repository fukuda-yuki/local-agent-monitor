# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-24: M12 measurement schema 定義レビュー

### 範囲

- `docs/spec.md`
- `docs/requirements.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M12-measurement-schema/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M12-measurement-schema/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M12-measurement-schema/review.md`
- `docs/task.md`

### 観点

- `docs/requirements.md` と `docs/spec.md` の上位要件に沿っていること。
- M12 が measurement schema 定義に閉じ、M14 / M15 / M16 / M20 / M21 の作業を先取りしていないこと。
- 必須列、任意列、欠損値、未知属性、手動評価値の方針が dataset 生成時に曖昧にならないこと。

### 指摘と対応

- 指摘: 既存 spec の 11 列だけでは、M17-M19 の反復計測で必要な task / condition / prompt / variant / snapshot の識別情報が dataset 上で必須か任意か不明確だった。
  - 対応: `task_id`、`task_category`、`task_run_index`、`experiment_condition`、`prompt_version`、`agent_variant`、`repo_snapshot` を必須列に追加した。
- 指摘: dotted Resource Attribute と CSV / JSON 列名の対応規則が未定義だった。
  - 対応: schema では snake_case に正規化する方針を追加した。
- 指摘: 欠損値、未知 span / 属性、手動評価前の状態が未定義だった。
  - 対応: CSV は空欄、JSON は `null`、手動評価前は `success_status=not-evaluated`、未知情報は `unknown_spans_json`、`unknown_attributes_json`、`aggregation_notes` に保持する方針を追加した。

### 妥当性判断

M12 の変更は documentation-only であり、repository の実行挙動、public runtime interface、依存関係、Langfuse 接続方式を変更しない。
`turn_count` / `tool_call_count` の算出アルゴリズム、Langfuse export / API の実取得可否、品質 rubric、A/B 実行順序は後続 milestone へ明示的に残しているため、M12 のスコープを越えていない。

### 検証

- `rg -n "measurement schema|success_status|experiment\\.id|experiment\\.condition|prompt\\.version|agent\\.variant|turn_count|tool_call_count|M14|M16|M20|M21" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M12-measurement-schema docs/requirements.md`
- `rg -n "自動採用|自動実装|勝敗の自動決定|Langfuse export|API|算出ルール|rubric|A/B" docs/spec.md docs/sprints/sprint1-langfuse-poc/milestones/M12-measurement-schema`
- `rg -n "client.kind=vscode(,|$)" docs/requirements.md docs/spec.md`
- documentation-only のため、`dotnet build` / `dotnet test` は実行対象外。

### 残リスク

- 任意列の実取得可否は M14 の Langfuse export / API 調査まで確定しない。
- `turn_count` / `tool_call_count` の具体的な分類精度は M16 の算出ルール定義に依存する。
- `pass` / `fail` / `needs-review` の評価一貫性は M20 の rubric 定義に依存する。

## 2026-05-24: 並列 sub-agent レビュー後の再レビュー

### 実施内容

仕様整合、後続 milestone 境界、文書品質 / 検証記録の 3 観点で read-only sub-agent レビューを実施した。
Main-Agent は各指摘を source of truth と M12 スコープに照らして評価し、妥当な指摘のみ修正した。

### 採用した指摘

- `client.kind` の上位要件例に `vscode` が残り、M12 schema の `vscode-copilot-chat` と不整合だった。
  - 対応: `docs/requirements.md` の例を `vscode-copilot-chat` に更新した。
- 未知 span / 未知属性の保持先が schema 列として未定義だった。
  - 対応: `unknown_spans_json`、`unknown_attributes_json`、`aggregation_notes` を任意列として追加した。
- 必須列が「列として必須、値は欠損可」であることが読み取りづらかった。
  - 対応: 必須列の説明と型 / 値域を修正した。
- 補助指標を「任意列」と呼んでいたが、列名・型は M14 まで未確定だった。
  - 対応: `任意列候補` に修正し、正式な列名・型・取得可否は M14 に委譲した。
- M14 の取得方式優先順位、M15 と M16 の count 系列責務、検証記録の表現が強すぎた。
  - 対応: M14 で優先順位を決定する表現に弱め、M15 では M16 前の count 系列を列存在・欠損表現・暫定抽出確認に留める旨を追記し、検証記録を実際に確認した内容へ修正した。

### 棄却または保留した指摘

- M16 を M15 より前に並べ替える案は採用しない。
  - 理由: milestone index の順序変更は M12 の schema 定義を超える。M15 側で暫定扱いを明記すれば実装者判断は残らない。

### 再レビュー結果

M12 は schema 定義、写像、欠損・未知・手動評価の記録方針に閉じている。
Langfuse export / API の取得方式、集計実装、count 算出ルール、rubric 判定基準、A/B 実行プロトコルは後続 milestone に残っており、M12 の完了条件を満たす。

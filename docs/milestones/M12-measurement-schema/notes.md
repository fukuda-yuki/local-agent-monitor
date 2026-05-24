# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## M12 measurement schema

M12 では、研究用 CSV / JSON dataset の schema、Resource Attribute からの写像、欠損・未知・手動評価の記録方針を `docs/spec.md` に定義した。
実装、Langfuse export / API 調査、turn / tool count 算出ルール、rubric 判定基準、A/B 実行プロトコルは後続 milestone に委譲する。

必須列は、既存の trace / token / duration / count / status 系列に、研究計測で反復や条件を識別するための `task_id`、`task_category`、`task_run_index`、`experiment_condition`、`prompt_version`、`agent_variant`、`repo_snapshot` を加える方針にした。
これにより、M17-M19 の baseline 計測時に記録する属性が dataset 上でも欠損として明示できる。

dotted Resource Attribute 名は schema では snake_case に正規化する。
例として、`experiment.id` は `experiment_id`、`client.kind` は `client_kind`、`task.run_index` は `task_run_index` として扱う。

必須列は出力列として必ず存在する列を指し、取得元の欠損時は CSV では空欄、JSON では `null` に統一する。
未知 span 名や未知属性は破棄せず、取得できる場合は `unknown_spans_json`、`unknown_attributes_json`、`aggregation_notes` に保持する。

`success_status` は `pass`、`fail`、`needs-review`、`not-evaluated` の 4 値を維持する。
手動評価前の既定値は `not-evaluated` とし、評価補助の任意列として `evaluator_id`、`evaluation_notes`、`evaluated_at` を定義した。
`pass` / `fail` / `needs-review` の詳細な判定基準は M20 の品質非劣化 rubric で扱う。

研究実施計画書の補助指標である編集受容 / 生存率、cache token、reasoning token、model ID、IDE / CLI version、推定コストまたは相対コスト指数は、Copilot OTel または Langfuse export/API から取得できる場合、もしくは算出根拠を説明できる場合だけ任意列候補として扱う。
正式な列名、型、取得可否の確認は M14、集計実装は M15 に委譲する。

OpenTelemetry GenAI Semantic Conventions は発展中であるため、M12 では特定の固定バージョンに依存しない。
現在の公式 GenAI semantic conventions と Langfuse OpenTelemetry attribute mapping を参照前提とし、変更が必要になった場合は M14 以降の取得方式または M15 の集計実装で吸収する。

## 後続 milestone への境界

- M14: Langfuse export / API で取得できる列、任意列、metadata の実データ形状を確認する。
- M15: M12 schema に従う CSV / JSON 生成を実装する。
- M16: `turn_count` / `tool_call_count` の分類ルールと未知 span 名の扱いを具体化する。
- M20: `pass` / `fail` / `needs-review` の人間評価 rubric を定義する。
- M21: `experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant` の比較プロトコル上の使い分けを定義する。

## レビュー指摘への対応

- 2026-05-24: `docs/requirements.md` の `client.kind=vscode` 例を `client.kind=vscode-copilot-chat` に揃え、M12 schema の `client_kind` 値域と上位要件例の不整合を解消した。
- 2026-05-24: 未知 span / 未知属性の保持先として `unknown_spans_json`、`unknown_attributes_json`、`aggregation_notes` を任意列に追加した。
- 2026-05-24: 必須列は「列として必須、値は欠損可」であることを明記した。
- 2026-05-24: 補助指標は M12 で確定した任意列ではなく、M14 で列名・型・取得可否を決める任意列候補として表現を修正した。
- 2026-05-24: M14 の取得方式優先順位を M14 で決定する表現に弱め、M15 と M16 の count 系列責務境界を追記した。

# M22: 結果レポート雛形

この雛形は、M12 measurement schema、M20 品質非劣化 rubric、M21 variant / A-B 計測プロトコルに基づき、研究計画書へ戻せる要約を Markdown で記録するために使う。

実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity は記録しない。

## 1. レポート識別情報

| 項目 | 値 |
| --- | --- |
| `report_id` |  |
| `comparison_id` |  |
| `experiment_id` |  |
| `task_id` |  |
| `task_category` |  |
| `client_kind` |  |
| `repo_snapshot` |  |
| 作成日 |  |
| 作成者 ID | 仮名または空欄 |

## 2. 実行範囲

| 条件 | valid N | excluded N | 除外理由サマリ |
| --- | ---: | ---: | --- |
| baseline |  |  |  |
| variant |  |  |  |

## 3. 指標サマリ

| 条件 | `total_tokens_median` | `turn_count_median` | `tool_call_count_median` | `duration_ms_median` | `error_count_total` | `success_status_counts` |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| baseline |  |  |  |  |  |  |
| variant |  |  |  |  |  |  |

`success_status_counts` は `pass`、`fail`、`needs-review`、`not-evaluated` の件数サマリとして記録する。

## 4. Baseline / Variant 比較

| 項目 | 値 |
| --- | --- |
| `comparison_id` | Section 1 と同じ |
| `experiment_id` | Section 1 と同じ |
| `task_id` | Section 1 と同じ |
| `task_category` | Section 1 と同じ |
| `client_kind` | Section 1 と同じ |
| `baseline_prompt_version` |  |
| `variant_prompt_version` |  |
| `repo_snapshot` | Section 1 と同じ |
| `baseline_condition` |  |
| `variant_condition` |  |
| `baseline_agent_variant` |  |
| `variant_agent_variant` |  |
| `baseline_valid_n` |  |
| `variant_valid_n` |  |
| `baseline_excluded_n` |  |
| `variant_excluded_n` |  |
| `baseline_total_tokens_median` |  |
| `variant_total_tokens_median` |  |
| `baseline_turn_count_median` |  |
| `variant_turn_count_median` |  |
| `baseline_tool_call_count_median` |  |
| `variant_tool_call_count_median` |  |
| `baseline_duration_ms_median` |  |
| `variant_duration_ms_median` |  |
| `baseline_error_count_total` |  |
| `variant_error_count_total` |  |
| `baseline_success_status_counts` |  |
| `variant_success_status_counts` |  |
| `quality_non_regression_status` |  |
| `evaluation_notes` |  |
| `comparison_notes` |  |

## 5. 品質非劣化評価

| 項目 | 記録 |
| --- | --- |
| `quality_non_regression_status` |  |
| M20 rubric で確認した観点 |  |
| `pass` の根拠 |  |
| `fail` または `needs-review` の根拠 |  |
| `not-evaluated` の理由 |  |
| 評価者 ID | 仮名または空欄 |
| 評価日時 |  |

`quality_non_regression_status` は人間が M20 rubric を踏まえて記録する確認状態であり、自動勝敗判定、自動採点、自動採用を意味しない。

## 6. 観察メモ

### Trace 確認所見

- 未記入

### 未確認項目

- 未記入

### 残リスク

- 未記入

### 研究計画書へ戻す要約

- 未記入

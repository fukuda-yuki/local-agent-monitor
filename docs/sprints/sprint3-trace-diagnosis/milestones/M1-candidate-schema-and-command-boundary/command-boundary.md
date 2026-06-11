# Command Boundary

この文書は Sprint3 M1 の command 名、入力、出力列、sensitive output 保存先、synthetic fixture 方針を定義する。
正式な product behavior として実装する前に、必要な範囲を `../../../../spec.md` へ反映する。

## Pipeline

```text
normalized measurements + optional raw telemetry
  -> generate-diagnosis-candidates
  -> generate-improvement-candidates
  -> generate-auto-decisions
```

既存の M24 / M25 / M27 command は変更しない。
candidate pipeline は既存 human-review pipeline の前段であり、既存 command への変換や接続は後続 milestone で扱う。

## Command 1: generate-diagnosis-candidates

```text
config-cli generate-diagnosis-candidates <measurements.csv|measurements.json>
  [--raw <raw-store.db|raw-otlp.json>]
  [--include-sensitive-content]
  [--sensitive-output-dir <dir>]
  [--csv <output.csv>]
  [--json <output.json>]
```

### 入力

| 入力 | 必須 | 用途 |
| --- | --- | --- |
| `<measurements.csv|measurements.json>` | 必須 | M12 measurement schema の normalized dataset。candidate の trace metadata と aggregate metrics を取得する |
| `--raw <raw-store.db|raw-otlp.json>` | 任意 | raw span / event / content から evidence を補う |
| `--include-sensitive-content` | 任意 | raw prompt / response / tool arguments / tool results / identity / credential を sensitive bundle に含める |
| `--sensitive-output-dir <dir>` | 条件付き | `--include-sensitive-content` 使用時の保存先。省略時は `tmp/sprint3-sensitive/<run_id>/` |

`--include-sensitive-content` を指定しない場合、raw content は standard output にも sensitive bundle にも出力しない。
`--include-sensitive-content` を指定する場合、`--raw` も指定する。

### 出力列

| 列 | 値 |
| --- | --- |
| `diagnosis_candidate_id` | `diagcand-0001` から出力順に採番 |
| `trace_id` | measurement または raw telemetry の trace id |
| `task_id` | measurement の `task_id` または空欄 |
| `task_category` | measurement の `task_category` または空欄 |
| `client_kind` | measurement の `client_kind` または空欄 |
| `comparison_id` | 比較文脈がある場合の id、なければ空欄 |
| `experiment_id` | measurement の `experiment_id` または空欄 |
| `experiment_condition` | measurement の `experiment_condition` または空欄 |
| `prompt_version` | measurement の `prompt_version` または空欄 |
| `agent_variant` | measurement の `agent_variant` または空欄 |
| `task_run_index` | measurement の `task_run_index` または空欄 |
| `rule_id` | candidate を生成した deterministic rule id |
| `failure_category_id` | M23 `F-*` ID |
| `anti_pattern_id` | M23 `AP-*` ID または空欄 |
| `severity` | `blocking`、`major`、`minor` |
| `recommended_improvement_target` | `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` |
| `evidence_summary` | candidate の短い根拠。standard output では full raw content を入れない |
| `evidence_ref` | raw span id、observation id、または sensitive bundle 内の reference |
| `content_included` | `true` / `false` |
| `sensitive_bundle_path` | sensitive bundle file path または空欄 |
| `confidence` | `high`、`medium`、`low` |
| `required_human_checks` | 人間が確認すべき残項目 |
| `candidate_status` | `candidate`、`auto-eligible`、`blocked` |

## Command 2: generate-improvement-candidates

```text
config-cli generate-improvement-candidates <diagnosis-candidates.csv|diagnosis-candidates.json>
  [--csv <output.csv>]
  [--json <output.json>]
```

### 入力

`generate-diagnosis-candidates` の CSV / JSON 出力を入力にする。
`candidate_status=blocked` の diagnosis candidate は improvement candidate にしない。

### 出力列

| 列 | 値 |
| --- | --- |
| `improvement_candidate_id` | `impcand-0001` から出力順に採番 |
| `source_diagnosis_candidate_id` | 元 diagnosis candidate id |
| `trace_id` | 元 diagnosis candidate の trace id |
| `task_id` | 元 diagnosis candidate の task id または空欄 |
| `task_category` | 元 diagnosis candidate の task category または空欄 |
| `client_kind` | 元 diagnosis candidate の client kind または空欄 |
| `comparison_id` | 元 diagnosis candidate の comparison id または空欄 |
| `experiment_id` | 元 diagnosis candidate の experiment id または空欄 |
| `experiment_condition` | 元 diagnosis candidate の experiment condition または空欄 |
| `prompt_version` | 元 diagnosis candidate の prompt version または空欄 |
| `agent_variant` | 元 diagnosis candidate の agent variant または空欄 |
| `task_run_index` | 元 diagnosis candidate の task run index または空欄 |
| `failure_category_id` | 元 diagnosis candidate の `F-*` ID |
| `anti_pattern_id` | 元 diagnosis candidate の `AP-*` ID または空欄 |
| `severity` | 元 diagnosis candidate の severity |
| `improvement_target` | 元 diagnosis candidate の recommended target |
| `proposal_title` | candidate の短い題名 |
| `proposal_summary` | full raw content を含まない短い提案要約 |
| `proposed_change_kind` | `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` |
| `evidence_ref` | 元 diagnosis candidate の evidence ref |
| `sensitive_bundle_path` | 元 diagnosis candidate の sensitive bundle path または空欄 |
| `candidate_status` | `candidate`、`auto-eligible`、`blocked` |

## Command 3: generate-auto-decisions

```text
config-cli generate-auto-decisions <improvement-candidates.csv|improvement-candidates.json>
  [--csv <output.csv>]
  [--json <output.json>]
```

### 入力

`generate-improvement-candidates` の CSV / JSON 出力を入力にする。
`candidate_status=blocked` の improvement candidate は `blocked` decision として出力する。

### 出力列

| 列 | 値 |
| --- | --- |
| `auto_decision_id` | `autodec-0001` から出力順に採番 |
| `source_improvement_candidate_id` | 元 improvement candidate id |
| `source_diagnosis_candidate_id` | 元 diagnosis candidate id |
| `trace_id` | 元 candidate の trace id |
| `decision_status` | `auto-approved`、`needs-human-review`、`blocked` |
| `decision_rule_id` | decision を生成した deterministic rule id |
| `decision_reason` | 判断理由の短い説明 |
| `confidence` | `high`、`medium`、`low` |
| `blocking_risk_checks` | blocked または human review が必要な理由 |
| `sensitive_content_included` | `true` / `false` |
| `sensitive_bundle_path` | sensitive bundle file path または空欄 |
| `implementation_target` | `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` |
| `next_action` | `handoff-to-implementation`、`request-human-review`、`do-not-implement` |

`auto-approved` は Sprint4 以降の自動改善実装候補に渡せる状態を意味する。
Sprint3 内で repository file の修正、patch / diff 生成、commit、push、pull request 作成は行わない。

## Sensitive Output

既定保存先は以下とする。

```text
tmp/sprint3-sensitive/<run_id>/
```

`run_id` は local timestamp と短い suffix で生成する。
例:

```text
tmp/sprint3-sensitive/20260612T153000Z-diagcand/
```

Sensitive bundle の候補構成:

```text
tmp/sprint3-sensitive/<run_id>/
  manifest.json
  evidence/
    diagcand-0001.json
    diagcand-0002.json
```

`manifest.json` には run id、生成 command、入力 file path、生成日時、content included flag、削除対象 path を記録する。
`evidence/*.json` には、明示 opt-in 時だけ full prompt / response / tool arguments / tool results / identity / credential を含められる。

Sensitive output は repository に保存・commit しない。
Sprint-local docs、review record、自動テスト fixture には sensitive bundle の実 content を貼り付けない。

## Synthetic Fixture Policy

M1 時点の実装 fixture 方針は以下とする。

- repository に保存する fixture は synthetic のみとする。
- 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。
- secret-like string は `synthetic-secret-placeholder` など明示的な synthetic 値だけを使う。
- raw OTLP fixture は既存 `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json` を拡張または近接 file で追加する。
- candidate output fixture は、metadata-only case、error-count case、tool-loop case、missing-attribute case、sensitive-opt-in manifest shape case を含める。
- sensitive-opt-in fixture でも repository に full sensitive content を保存せず、bundle path / ref の shape だけを検証する。

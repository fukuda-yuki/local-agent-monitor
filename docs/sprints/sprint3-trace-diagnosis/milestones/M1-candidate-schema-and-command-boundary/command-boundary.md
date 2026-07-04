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
candidate pipeline は既存 human-review pipeline の前段であり、既存 M24-M27 command / schema は Sprint3 でも互換性維持対象とする。
Sprint3 は M24-M27 を置換しない。
candidate output から既存 human-review record へ渡す adapter / mapping contract は M2 で実装前に確定し、M5 で文書または code に反映する。
Sprint3 の auto-decision は改善候補の自動採用判断を deterministic に記録する。
ただし、Sprint3 の auto-decision は repository file の自動修正、patch / diff 生成、commit、push、pull request 作成を実行しない。

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

### 初期 rule inventory

M1 では以下を初期 rule inventory として扱う。
M2 で literal / regex / field predicate のレベルまで判定条件を確定した。
M3 の最小実装は、M2 の [rule-and-evidence-contract.md](../M2-deterministic-rule-and-evidence-contract/rule-and-evidence-contract.md) で確定した rule だけを実装対象とする。

| rule_id | 入力 | 条件 | 出力候補 |
| --- | --- | --- | --- |
| `DIAG-METRIC-ERROR-COUNT-V1` | normalized measurement | `error_count > 0` | `F-ERROR`、severity `major`、target `workflow` |
| `DIAG-METRIC-TOOL-LOOP-V1` | normalized measurement | `tool_call_count >= 10` かつ `success_status != pass` | `F-TOOL` / `AP-TOOL-LOOP`、severity `major`、target `workflow` |
| `DIAG-CONTENT-ERROR-MESSAGE-V1` | raw span / event content | M2 の error field predicate または error text pattern に一致する | `F-ERROR` / `AP-ERROR-BLIND`、severity `major`、target `workflow` |
| `DIAG-CONTENT-SENSITIVE-LEAK-V1` | raw prompt / response / tool arguments / tool results | M2 の sensitive key predicate、sensitive text regex、または Base64 credential predicate に一致する | `F-DATA` / `AP-RAW-CONTENT`、severity `blocking`、target `workflow` |
| `DIAG-METADATA-MISSING-TRACE-CONTEXT-V1` | normalized measurement | `trace_id` または分類に必要な client / experiment metadata が欠損している | `F-MEASURE` / `AP-SCHEMA-DRIFT`、severity `major`、target `eval` |

content-aware rule は raw content を LLM で解釈しない。
M2 で span attribute、span event、tool result、prompt / response fragment に対する deterministic pattern matching と分類条件を確定した。

### 出力列

| 列 | 値 |
| --- | --- |
| `diagnosis_candidate_id` | `diagcand-0001` から出力順に採番 |
| `trace_id` | measurement または raw telemetry の trace id |
| `source_record_ref` | input file、row number、raw record id など、元 record へ戻るための reference |
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

measurement の `task_id`、`task_category`、`client_kind`、`experiment_id`、`experiment_condition`、`prompt_version`、`agent_variant`、`task_run_index` などの context は、standard candidate output へ carry-through しない。
必要な場合は `trace_id` と `source_record_ref` で元 measurement / raw record に join する。

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

improvement candidate も measurement context columns を carry-through しない。
context が必要な後続処理は `source_diagnosis_candidate_id`、`trace_id`、元 diagnosis candidate の `source_record_ref` を使って join する。

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
| `next_action` | `record-for-sprint4-planning`、`request-human-review`、`do-not-implement` |

`auto-approved` は、改善候補を Sprint4 以降の実装計画候補として記録できることを意味する。
Sprint3 内の出口は auto-decision record と、M5 / M6 の review evidence または Sprint4 planning handoff note への記録である。
Sprint3 内に repository を変更する consumer command は作らない。
Sprint4 以降で repository file 自動修正を扱う場合は、allowlist、dry-run、diff preview、rollback、test 実行、commit 境界、失敗時の停止条件を別途仕様化する。
Sprint3 内で repository file の修正、patch / diff 生成、commit、push、pull request 作成は行わない。

### 初期 decision rule set

M2 で以下の初期 rule set を確定した。
M4 は M2 の [rule-and-evidence-contract.md](../M2-deterministic-rule-and-evidence-contract/rule-and-evidence-contract.md) の decision rule set に従って `generate-auto-decisions` を実装する。

| decision_rule_id | 条件 | decision_status | next_action |
| --- | --- | --- | --- |
| `DEC-AUTO-APPROVE-SAFE-METADATA-V1` | source candidate が sensitive content を含まず、severity が `minor` または `major` で、implementation target が `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` のいずれかであり、blocked 条件に該当しない | `auto-approved` | `record-for-sprint4-planning` |
| `DEC-HUMAN-REVIEW-SENSITIVE-CONTENT-V1` | source candidate が sensitive content included で、blocked 条件に該当しない | `needs-human-review` | `request-human-review` |
| `DEC-BLOCK-SCOPE-OVERREACH-V1` | proposal が repository 修正、patch / diff、commit / PR、自動勝敗決定を要求する | `blocked` | `do-not-implement` |
| `DEC-HUMAN-REVIEW-DEFAULT-V1` | auto-approved 条件にも blocked 条件にも該当しない | `needs-human-review` | `request-human-review` |

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

### Sensitive bundle read contract

M2 で以下の schema version 1 を確定した。
Bundle を読む command や test fixture は M2 の [rule-and-evidence-contract.md](../M2-deterministic-rule-and-evidence-contract/rule-and-evidence-contract.md) に従う。

`manifest.json` は以下の top-level fields を持つ。

| field | 値 |
| --- | --- |
| `schema_version` | `1` |
| `bundle_id` | `run_id` と同じ値 |
| `created_at_utc` | UTC timestamp |
| `expires_at_utc` | 既定で `created_at_utc` から 7 日後 |
| `generated_by_command` | 生成 command 名 |
| `source_inputs` | 入力 file path と sha256 の配列 |
| `content_included` | `true` |
| `delete_target_paths` | bundle 削除時に削除する path の配列 |
| `evidence_index` | evidence reference の配列 |

`evidence_index` の各要素は以下を持つ。

| field | 値 |
| --- | --- |
| `evidence_ref` | standard output に出す stable reference |
| `diagnosis_candidate_id` | 対応する candidate id |
| `trace_id` | 対象 trace id |
| `source_locator` | span id、event name、observation id、raw json path など |
| `evidence_file` | bundle root からの相対 path |
| `content_kinds` | `prompt`、`response`、`tool_arguments`、`tool_results`、`identity`、`credential`、`secret`、`base64_header` の配列 |
| `fragment_count` | evidence file 内 fragment 数 |

`evidence/*.json` は以下の top-level fields を持つ。

| field | 値 |
| --- | --- |
| `schema_version` | `1` |
| `evidence_ref` | manifest の reference と一致 |
| `diagnosis_candidate_id` | 対応する candidate id |
| `trace_id` | 対象 trace id |
| `source_locator` | span id、event name、observation id、raw json path など |
| `fragments` | content fragment の配列 |

fragment は source span / event / field 単位を基本粒度とし、raw trace 全体を丸ごと保存しない。
各 fragment は `fragment_id`、`content_kind`、`source_path`、`sequence`、`value`、`sha256` を持つ。
bundle からの逆引きは、standard output の `evidence_ref` から `manifest.json` の `evidence_index` を引き、対応する `evidence_file` を読む手順に固定する。
期限切れの bundle を読む command は warning を出せるが、Sprint3 では自動削除 command は実装しない。
削除はユーザーが `manifest.json` の `delete_target_paths` を確認して手動で行う。
PowerShell では、対象が repository root 配下の `tmp\sprint3-sensitive\<run_id>` であることを確認してから、以下を実行する。

```powershell
Get-ChildItem -LiteralPath tmp\sprint3-sensitive
Remove-Item -LiteralPath tmp\sprint3-sensitive\<run_id> -Recurse -Force
```

削除 evidence には、実 content ではなく bundle id、削除日時、削除対象 path、削除実施または保留理由だけを記録する。

## Synthetic Fixture Policy

M1 時点の実装 fixture 方針は以下とする。

- repository に保存する fixture は synthetic のみとする。
- 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。
- secret-like string は `synthetic-secret-placeholder` など明示的な synthetic 値だけを使う。
- raw OTLP fixture は既存 `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json` を拡張または近接 file で追加する。
- candidate output fixture は、metadata-only case、error-count case、tool-loop case、missing-attribute case、sensitive-opt-in manifest shape case を含める。
- sensitive-opt-in fixture でも repository に full sensitive content を保存せず、bundle path / ref の shape だけを検証する。

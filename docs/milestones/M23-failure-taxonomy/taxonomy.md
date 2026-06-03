# M23 Failure Taxonomy / Anti-pattern

この文書は、trace-driven improvement loop の入口で使う failure taxonomy と agent anti-pattern を定義する。
分類は人間が trace、集計指標、M20 rubric の評価結果を確認して記録するためのものであり、自動採用、自動実装、自動 repository 修正、自動勝敗決定を意味しない。

## 入力

分類時は、以下の sanitized 情報だけを参照・記録対象にする。

| 入力 | 用途 |
| --- | --- |
| `trace_id` | 対象 trace の識別 |
| `task_id` | M13 の模擬保守タスク識別 |
| `task_category` | `refactoring`、`bug-investigation`、`test-generation`、`code-review` の分類 |
| `client_kind` | `vscode-copilot-chat` または `copilot-cli` |
| `experiment_id` / `experiment_condition` | baseline / variant 比較条件 |
| `success_status` | M20 rubric の人間評価結果 |
| `total_tokens` / `turn_count` / `tool_call_count` / `duration_ms` / `error_count` | M12 / M16 の集計指標 |
| sanitized trace observation summary | raw content を含まない根拠要約 |

実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity は保存しない。

## Failure Category

M23 の failure category は、M17 の `failure_type` とは別用途である。
M17 の `failure_type` は `copilot-error`、`langfuse-unavailable`、`trace-missing`、`wrong-attributes`、`real-data-risk` など、run / trace 取得・除外理由を記録する。
M23 の failure category は、取得済み trace や評価記録を人間が確認し、回答品質、診断可能性、比較プロトコル、改善対象を整理するために使う。

| ID | 名前 | 判定の目安 | 主な根拠 |
| --- | --- | --- | --- |
| `F-SPEC` | 仕様逸脱 | `docs/requirements.md`、`docs/spec.md`、milestone task と矛盾する回答や提案がある | rubric の `fail`、仕様根拠、対象文書 |
| `F-SCOPE` | スコープ逸脱 | 非スコープの自動実装、自動採用、repository 修正、実データ利用などを提案する | 非スコープ記述、提案内容の要約 |
| `F-DATA` | データ扱い違反 | secret、実 user identity、raw prompt / response content、tool result 本文などの保存や共有を要求する | sanitized した該当箇所の要約 |
| `F-MEASURE` | 計測 schema 不整合 | M12 の必須列、欠損値、正規化名、`success_status` 値と矛盾する | 欠損列、誤った型、誤った値域 |
| `F-TASK` | タスク類型不一致 | M13 の task category に必要な観点を外している | task id、category、欠落した観点 |
| `F-RUBRIC` | 品質評価根拠不足 | M20 rubric に照らした `pass` / `fail` / `needs-review` の根拠が不足する | 評価メモ、未確認項目 |
| `F-TRACE` | trace 根拠不足 | trace / observation / metrics から確認すべき根拠を確認していない | 未確認の trace 要素、欠損理由 |
| `F-TOOL` | tool / workflow 非効率 | 不要な tool call、重複探索、過剰な手順により token、turn、duration が増えている | tool call count、turn count、duration |
| `F-ERROR` | 実行エラー未処理 | error count、失敗 observation、timeout、permission error を無視して結論を出している | error count、status、sanitized error summary |
| `F-COMM` | 報告品質不足 | 重大度、根拠、残リスク、検証結果が読み手に伝わらない | review notes、report notes |
| `F-COMPARISON` | 比較条件混同 | `experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant`、実行順序、valid / excluded N を混同する | M21 比較表、実行台帳、comparison notes |

## Agent Anti-pattern

| ID | 名前 | 説明 | 関連 failure |
| --- | --- | --- | --- |
| `AP-SILENT-SPEC` | Silent spec choice | 仕様の選択肢や矛盾を説明せずに決める | `F-SPEC`, `F-SCOPE` |
| `AP-OVERREACH` | Scope overreach | 求められていない実装、依存追加、workflow 変更、commit / PR を進める | `F-SCOPE` |
| `AP-RAW-CONTENT` | Raw content leakage | trace content、prompt、tool result、secret、identity を記録様式へ残す | `F-DATA` |
| `AP-SCHEMA-DRIFT` | Schema drift | M12 の列名、欠損値、値域を独自に変える | `F-MEASURE` |
| `AP-RUBRIC-FLAT` | Flat rubric judgment | `pass` / `fail` だけを置き、task 類型ごとの根拠を示さない | `F-RUBRIC`, `F-TASK` |
| `AP-TRACE-SKIP` | Trace skip | 利用可能な trace / metrics を確認せずに診断する | `F-TRACE` |
| `AP-TOOL-LOOP` | Tool loop | 同じ探索や tool call を繰り返し、追加根拠が増えない | `F-TOOL` |
| `AP-ERROR-BLIND` | Error blind conclusion | error、timeout、permission failure を結論から除外する | `F-ERROR` |
| `AP-UNCLEAR-SEVERITY` | Unclear severity | 指摘の重大度、影響、確認方法を分けない | `F-COMM` |
| `AP-AUTO-DECIDE` | Automatic decision framing | variant の勝敗、改善採用、修正実行を自動決定のように表現する | `F-SCOPE`, `F-RUBRIC` |
| `AP-CONFOUND` | Comparison confound | 比較条件、prompt version、agent variant、実行順序、除外数の違いを無視して比較する | `F-COMPARISON` |

## Task-specific Anti-pattern

M13 の task category ごとに、M20 rubric の `fail` / `needs-review` に接続しやすい anti-pattern を以下のように扱う。

| ID | Task category | Anti-pattern | 判定の目安 |
| --- | --- | --- | --- |
| `AP-REF-CONTRACT-DRIFT` | `refactoring` | behavior contract drift | 既存 CLI 出力 key、既定 endpoint、content capture 設定、Resource Attribute 名など外部仕様を変える |
| `AP-REF-OVER-ABSTRACTION` | `refactoring` | over-abstraction | 単一用途の変更に不要な抽象化や依存を追加する |
| `AP-BUG-CAUSE-FIX-CONFLATION` | `bug-investigation` | cause-fix conflation | 再現条件、原因、修正案、回帰確認を分けずに説明する |
| `AP-BUG-MISSING-SYNTHETIC-REPRO` | `bug-investigation` | missing synthetic repro | 合成入力で期待値と実際値を示さず、実データや live Langfuse を必要条件にする |
| `AP-TEST-NONDETERMINISTIC` | `test-generation` | nondeterministic test plan | 外部サービス、時刻、ファイルシステム、ネットワークに依存するテストだけを提案する |
| `AP-TEST-MISSING-EDGE-CLASS` | `test-generation` | missing edge class | 正常系だけに偏り、境界値または異常系を欠く |
| `AP-REVIEW-MISSED-SEEDED-VIOLATION` | `code-review` | missed seeded violation | 明確な仕様違反、テスト不足、保守リスクを見落とす |
| `AP-REVIEW-PREFERENCE-OVER-SPEC` | `code-review` | preference over spec | 好みの指摘と仕様違反を混同する |

## Status Link

M23 taxonomy は M20 の `success_status` を置き換えない。

| M20 status | M23 での扱い |
| --- | --- |
| `pass` | 原則として failure category は不要。ただし改善余地を記録する場合は `minor` として扱う |
| `fail` | `blocking` または `major` の failure category を少なくとも 1 つ記録する |
| `needs-review` | `major` または `minor` の failure category を記録し、追加確認が必要な根拠を残す |
| `not-evaluated` | taxonomy 分類前の状態として扱い、failure category を確定しない |

## Severity

| 値 | 判定基準 |
| --- | --- |
| `blocking` | 仕様違反、データ扱い違反、非スコープ逸脱、誤った結論につながる |
| `major` | 結論や比較の信頼性を大きく下げるが、追加確認や軽微な修正で回復できる |
| `minor` | 読みやすさ、記録粒度、補足根拠の不足に留まる |

`blocking` は M20 rubric の `fail` に接続しやすい。
`major` は内容により `fail` または `needs-review` に接続する。
`minor` は通常 `needs-review` または評価メモの改善対象として扱う。

## Improvement Target

改善候補を後続 milestone で作る場合は、対象を以下のいずれかに分類する。

| 値 | 用途 |
| --- | --- |
| `prompt` | task prompt や評価 prompt の改善候補 |
| `instruction` | agent instructions の改善候補 |
| `skill` | skill の手順や境界の改善候補 |
| `tool schema` | MCP / tool schema、引数、戻り値説明の改善候補 |
| `workflow` | 作業順序、確認順序、review loop の改善候補 |
| `eval` | rubric、評価記録、comparison report の改善候補 |

この分類は人間採否のための提案ラベルであり、repository の自動修正や自動採用を意味しない。

## 記録様式

M24 以降で diagnosis record を作る場合は、1 行を 1 つの `(trace_id, failure_category_id, anti_pattern_id)` に対する分類記録として扱う。
同じ trace に複数の failure category や anti-pattern がある場合は、複数行で記録する。

少なくとも以下の列を候補にする。

| 列 | 値 |
| --- | --- |
| `trace_id` | 対象 trace id |
| `task_id` | M13 task id |
| `task_category` | M12 `task_category` |
| `client_kind` | M12 `client_kind` |
| `comparison_id` | M21 comparison id または空欄 |
| `experiment_id` | M12 / M21 `experiment_id` |
| `experiment_condition` | M21 `experiment.condition` |
| `prompt_version` | M21 `prompt.version` |
| `agent_variant` | M21 `agent.variant` |
| `task_run_index` | M12 / M21 `task_run_index` |
| `failure_category_id` | `F-*` ID |
| `anti_pattern_id` | cross-cutting または task-specific の `AP-*` ID、または空欄 |
| `severity` | `blocking`、`major`、`minor` |
| `evidence_summary` | raw content を含まない短い根拠 |
| `recommended_improvement_target` | `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` |
| `review_status` | `needs-human-review`、`accepted-for-proposal`、`rejected` |

`review_status` は分類結果の人間確認状態を表す。
改善候補の採用、実装、勝敗判定を自動化するものではない。

## M24 Handoff

M24 trace-to-diagnosis MVP がこの taxonomy を使う場合、少なくとも以下を確認できることを受け入れ条件にする。

- M17 `failure_type` と M23 `failure_category_id` を混同しない。
- `fail` だけでなく `needs-review` の改善材料も分類できる。
- `task_category` ごとの task-specific anti-pattern を記録できる。
- M21 の比較条件混同を `F-COMPARISON` / `AP-CONFOUND` として扱える。
- `evidence_summary` に実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含めない。

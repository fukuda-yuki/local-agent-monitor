# Sprint5: Static HTML Observability Dashboard

Sprint5 は、Sprint4 で定義した Agent workflow observability dashboard を、Grafana ではなく GitHub Pages 向け static HTML dashboard として実装するための要件定義と実装準備を扱う。

## Decision

Sprint5 の第一候補は GitHub Pages 向け static HTML dashboard とする。

Grafana は Enterprise 利用時の導入、認証、data source、運用調整が重いため、Sprint5 初期実装の主経路にはしない。
Grafana JSON dashboard は将来候補または fallback として残す。

Static HTML dashboard は Grafana 代替の常設 dashboard として利用する。
GitHub Pages は private repository と明示的な access control を前提にした公開先候補である。

## Data Scope

Dashboard は実データ由来の集計値、参照 ID、分類属性を扱う。

Dashboard に表示してよいもの:

- aggregate metrics
- status distribution
- trend and percentile values
- `trace_id` / `langfuse_trace_id` 等の参照 ID
- candidate / auto-decision / evidence reference
- `user.id`
- `user.email`
- `client.kind`
- `experiment.id` / `experiment.condition`
- `agent.variant`
- `prompt.version`
- `skill.version`

Dashboard に表示しないもの:

- raw prompt
- raw response
- system prompt
- tool arguments
- tool results
- source code fragments or file contents from observed sessions
- credentials, secrets, tokens, API keys, passwords, or Base64 authorization headers
- sensitive bundle content or local sensitive bundle paths

`user.email` は実値をそのまま表示する。
将来的に email と表示名の mapping を入力し、email 表示と display name 表示を切り替えられるようにする。

## Generation Flow

GitHub Actions が毎日実行され、raw store / normalized dataset から dashboard dataset を再生成する。

```text
scheduled GitHub Actions
  -> raw store / normalized dataset を取得
  -> generate-dashboard-dataset
  -> static HTML dashboard を生成
  -> HTML と JSON dataset を publish directory に配置
  -> GitHub Pages に deploy
```

HTML と JSON dataset は repository または Pages branch に保存してよい。

## Pages Layout

GitHub Pages には以下を配置する。

- `/latest/`
- `/YYYY-MM-DD/`

日次 snapshot は保持し、自動削除しない。

## Initial Views

初期 view は Sprint4 の dashboard view set を踏襲する。

- Run Overview
- Agent / Tool Behavior
- Prompt / Skill / Instructions
- Baseline vs Variant
- Diagnosis / Improvement Loop
- Collection Health
- Outcome Linkage Candidate

Outcome Linkage Candidate は placeholder / future candidate とし、外部 API 連携、identity mapping、組織利用状況 dashboard は別途 product / security decision があるまで実装しない。

## Client-side Interaction

初期 dashboard は client-side filter、sort、search を持つ。

初期 filter 軸:

- date
- user
- client
- experiment
- variant
- status

## Milestone Candidate

| Milestone | 内容 |
| --- | --- |
| M1 static dashboard requirements and source boundary | 完了。static HTML dashboard の目的、非目的、公開範囲、data source 境界を確定した |
| M2 static dashboard artifact contract | 完了。HTML / JSON artifact、Pages layout、snapshot policy、client-side filter contract を定義した |
| M3 local static dashboard generator | 完了。dashboard dataset から local static HTML を生成する `generate-static-dashboard` を追加した |
| M4 daily GitHub Actions publish workflow | 完了。raw store / normalized dataset から日次 dashboard を生成し GitHub Pages へ publish する workflow を追加した |
| M5 real-data snapshot validation | 完了。実データ由来入力の staging contract、user 表示、snapshot metadata、raw content / credential / sensitive path 非表示を検証した |
| M6 review and handoff | 完了。Sprint5 の実装、運用境界、残課題を review して後続へ渡した |

## Open Questions

- Actions が参照する raw store / normalized dataset の初期配置は `artifacts/dashboard-input/` とする。JSON 入力は accidental commit を避けるため git ignored とし、M5 では staging contract と sanitizer coverage を検証した。
- Published JSON dataset は `gh-pages` branch と GitHub Pages artifact に保持する。main branch には生成済み snapshot を commit しない。
- email / display name mapping の入力形式は後続の product / security decision 後に扱う。
- 日次 snapshot が長期蓄積した場合の repository size monitoring 方法は後続運用課題として残す。
- GitHub Pages access control の具体設定と、実 repository 上での初回 workflow 実行結果は環境側の handoff task として残す。

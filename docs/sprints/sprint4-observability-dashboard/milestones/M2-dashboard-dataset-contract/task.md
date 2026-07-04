# M2: Dashboard Dataset Contract

## Status

Complete.

## Objective

Sprint4 dashboard の view / panel / metric 要件を、Grafana-first dashboard prototype と static report の両方で使える dashboard dataset contract に落とし込む。

## Scope

- dashboard dataset の CSV / JSON schema を定義する。
- normalized measurement、diagnosis candidate、improvement candidate、auto-decision record、human review records から dashboard dataset に渡す列を定義する。
- Run Overview、Agent / Tool Behavior、Prompt / Skill / Instructions、Baseline vs Variant、Diagnosis / Improvement Loop、Collection Health、Outcome Linkage Candidate の各 view が必要とする列を対応付ける。
- `trace_id`、candidate id、auto-decision id、`evidence_ref` などの drilldown reference を sanitized reference として定義する。
- raw prompt / response / tool arguments / tool results、credential、secret、Base64 header、実 user identity を dashboard dataset に既定保存しないことを schema レベルで明記する。
- Grafana JSON dashboard prototype に渡しやすい time bucket、dimension、metric、status distribution の列を定義する。
- TTFT、estimated cost、stuck / long-running threshold、time bucket granularity、Codex App の任意 `client_kind` 予約値を contract default として定義する。
- Outcome Linkage Candidate は M5 handoff で Tier 分けする将来候補として扱い、M2 required schema には含めない。

## Non-goals

- dashboard dataset 生成 CLI の実装。
- Grafana JSON dashboard の実装。
- synthetic dashboard data の作成。
- 本番 Grafana / Azure Managed Grafana / Application Insights / Tempo / Loki / Mimir の採用決定。
- GitHub / Notion / HR system との外部 ETL 実装。
- raw content の一覧表示。

## Acceptance Criteria

- CSV header と JSON object shape が定義されている。
- 各列の source、型、nullable 可否、PII / sensitive 扱いが定義されている。
- 各 view / panel が必要とする列との対応表が定義されている。
- normalized measurement と Sprint3 candidate outputs から導出できる列、M3 以降で fixture 追加が必要な列、将来候補の列が分離されている。
- Grafana-first dashboard prototype で使う time series / table / status distribution に必要な最小列が定義されている。
- Langfuse trace viewer / raw store / sensitive bundle への drilldown reference と、dashboard dataset に保存しない raw content の境界が定義されている。
- TTFT は直接属性または fallback 導出の nullable 指標として定義され、取得元を表す `ttft_source` がある。
- estimated cost は unit price table に基づく概算として定義され、実課金額ではないことと `cost_source` が明記されている。
- long-running / stuck 判定の既定閾値と `time_bucket` の既定粒度が定義されている。
- `client_kind=codex-app` は任意 source の予約値であり、M2 / M3 の必須 fixture 対象ではないことが明記されている。
- Outcome Linkage の優先順位付けは M5 handoff に送られている。

## Verification

- Documentation review only.
- Run `git diff --check`.
- No product code, dependency, build, test, live Langfuse, Grafana, Copilot, external API, or network validation is required for M2.

## Deliverables

- `dashboard-dataset-contract.md` defines the four logical dashboard dataset tables:
  `dashboard_run_summary`, `dashboard_operation_summary`, `dashboard_candidate_summary`, and `dashboard_collection_health`.
- `docs/requirements.md` and `docs/spec.md` record the M2 qualifiers for TTFT, estimated cost, thresholds, time bucket granularity, Codex App, and Outcome Linkage handoff.

## M3 Handoff

- Implement or fixture TTFT fallback, operation timing, retry, approval wait, subagent wait, stuck / long-running flags, estimated cost, and backlog age.
- Validate that synthetic dashboard data does not include raw prompt / response / tool arguments / tool results, credentials, Base64 headers, sensitive evidence, or real identity values.
- Keep Outcome Linkage as placeholder-only until M5 tiers future work.

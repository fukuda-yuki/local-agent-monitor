# M1: Dashboard Requirements

## Status

Complete.

## Objective

Agent workflow observability dashboard の目的、非目的、view set、metric inventory、dimension / filter、drilldown、data source 境界を確定する。

## Scope

- `docs/requirements.md` に Sprint4 dashboard 要件と成功条件を追加する。
- `docs/spec.md` に Sprint4 dashboard の詳細仕様候補を追加する。
- `docs/task.md` に Sprint4 を active sprint として追加する。
- sprint-local README に goals、non-goals、milestones、initial view set、safety boundary を記録する。

## Acceptance Criteria

- dashboard が Agent workflow 改善判断のための観測ビューであり、利用監視や個人評価ではないことが明記されている。
- 初期 view set が定義されている。
- 各 view で必要になる metric / dimension / filter の初期 inventory が定義されている。
- raw prompt / response / tool arguments / tool results を dashboard dataset に既定保存しない方針が明記されている。
- normalized measurement、diagnosis candidate、improvement candidate、auto-decision record が dashboard data source として扱われている。
- M4 prototype path では Grafana-first dashboard + Langfuse drilldown を第一候補として比較する方針が明記されている。
- Outcome Linkage Candidate は将来候補に留め、本番 GitHub / Notion / HR system 連携は scope 外と明記されている。

## Verification

- Documentation review only.
- No product code or dependency changes are expected in M1.

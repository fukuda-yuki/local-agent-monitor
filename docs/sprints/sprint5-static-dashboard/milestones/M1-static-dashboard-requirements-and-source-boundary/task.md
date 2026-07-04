# M1: Static Dashboard Requirements and Source Boundary

## Goal

GitHub Pages 向け static HTML dashboard の目的、非目的、公開範囲、入力 source 境界、実装前の安全条件を確定する。

## Scope

- Sprint4 dashboard dataset の 4 logical table を static dashboard の主入力にする。
- `generate-dashboard-dataset` の JSON output を static dashboard generator の初期入力にする。
- Dashboard に表示してよい値は aggregate metrics、参照 ID、分類属性、`user.id`、`user.email` に限定する。
- Dashboard に raw prompt / response / system prompt / tool arguments / tool results、source code fragment、credential、secret、Base64 authorization header、sensitive bundle content、sensitive bundle local path を表示しない。
- GitHub Pages は private repository と明示的な Pages access control を前提にした publish 先とする。

## Source Boundary

初期実装では、static dashboard generator は既存 dashboard dataset JSON を入力にする。

```text
generate-dashboard-dataset
  -> dashboard.json
  -> generate-static-dashboard
  -> index.html + dashboard-data.json
```

GitHub Actions の初期入力候補は以下とする。

| Input | Required | Purpose |
| --- | --- | --- |
| `artifacts/dashboard-input/measurements.json` | no | normalized measurement input |
| `artifacts/dashboard-input/raw-otlp.json` | no | raw operation fallback input |
| `artifacts/dashboard-input/diagnosis-candidates.json` | no | diagnosis candidate input |
| `artifacts/dashboard-input/improvement-candidates.json` | no | improvement candidate input |
| `artifacts/dashboard-input/auto-decisions.json` | no | auto-decision input |

`measurements.json` が存在する場合、Actions は `generate-dashboard-dataset` を実行して dashboard dataset を再生成する。
`measurements.json` が存在しない場合、Actions は repository の synthetic fixture から preview dashboard dataset を生成する。
この fallback は workflow と Pages artifact の構文検証用であり、実データ dashboard 完了証跡として扱わない。

## Non-goals

- 独自 Web アプリ、server-side API、認証機能を実装しない。
- GitHub / Notion / HR system 等の外部 API ingestion を実装しない。
- email と display name の mapping 入力は初期実装では実装しない。
- snapshot 自動削除、repository size monitoring は初期実装では実装しない。
- GitHub Pages access control の組織設定変更は repository 外の運用判断とする。

## Verification

- Static dashboard artifact に raw content / credential / sensitive path が含まれないことを automated test で確認する。
- `user.id` / `user.email` が filter / search 対象として表示されることを automated test で確認する。
- Local generator は synthetic dashboard dataset だけで deterministic に実行できることを確認する。


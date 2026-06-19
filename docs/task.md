# Sprint Index

この文書は repository 全体の sprint / roadmap index である。

プロダクト仕様・実装判断の正は `docs/spec.md` である。
ただし、`docs/requirements.md` を上位要件とし、`docs/spec.md` はその詳細仕様として扱う。
GitHub Issue は既定の作業単位にしない。ユーザーが明示的に作成・参照を指示した場合だけ使う。

## Sprint Index

| Sprint | 状態 | 概要 / 詳細 |
| --- | --- | --- |
| Sprint1: Langfuse PoC | 完了 | [docs/sprints/sprint1-langfuse-poc/](sprints/sprint1-langfuse-poc/) に M0-M28 と user-facing docs refresh までの PoC 資料を集約した |
| Sprint2: Raw Data Loop | 完了 | [docs/sprints/sprint2-raw-data-loop/](sprints/sprint2-raw-data-loop/) で raw OTLP file ingest、SQLite raw store、normalized dataset、Langfuse 非依存改善ループ、docs and release check まで完了した |
| Sprint2.5: ConfigCli Maintainability | 完了 | [docs/sprints/sprint2-5-maintainability/](sprints/sprint2-5-maintainability/) で ConfigCli 保守性改善、CSV / JSON 共通処理集約、redacted real-trace E2E 確認、regression review and closeout まで完了した |
| Sprint3: Content-aware Trace Diagnosis and Auto-decision Foundation | 完了 | [docs/sprints/sprint3-trace-diagnosis/](sprints/sprint3-trace-diagnosis/) で trace content を含む deterministic 診断候補生成、改善候補生成、自動採用判断を含む auto-decision record の基盤を実装した |
| Sprint4: Agent Workflow Observability Dashboard | 完了 | [docs/sprints/sprint4-observability-dashboard/](sprints/sprint4-observability-dashboard/) で Agent workflow 改善判断に使う dashboard の目的、非目的、view、metric、dimension、drilldown、data source 境界を定義した |
| Sprint5: Static HTML Observability Dashboard | 進行中 | [docs/sprints/sprint5-static-dashboard/](sprints/sprint5-static-dashboard/) で GitHub Pages 向け static HTML dashboard を Grafana 代替の常設 dashboard 第一候補として扱う |

## Roadmap

- Sprint1 は完了済みの参照資料として扱う。
- Sprint2 は M1 で MVP 仕様を `docs/requirements.md` と `docs/spec.md` に反映し、M2-M6 の後続 milestone と task breakdown を作成している。
- Sprint2 は M2 raw store 基盤、M3 raw OTLP ingest、M4 raw normalization、M5 Langfuse 非依存 loop、M6 docs and release check まで完了している。
- Sprint2.5 は GitHub Issue #24 を正式な参照元とし、Sprint3 に進む前の技術負債整理として完了した。
- Sprint2.5 は M1 planning、M2 ConfigCli responsibility split、M3 output helper consolidation、M4 redacted real-trace E2E、M5 regression review and closeout まで完了している。
- Sprint3 は完了済みであり、Sprint2 MVP と Sprint2.5 で除外した trace からの自動診断候補生成を、deterministic rule、content-aware evidence、改善候補生成、自動採用判断を含む auto-decision record まで拡張した。
- Sprint3 M1 は `candidate-schema-and-command-boundary` とし、candidate 専用 command / schema、auto-decision 専用 schema、sensitive output 保存先、synthetic fixture 方針を sprint-local に確定した。
- Sprint3 M2 は `deterministic-rule-and-evidence-contract` とし、deterministic rule、content-aware pattern、sensitive bundle schema v1、M24-M27 adapter mapping、`auto-approved` の Sprint3-local exit を確定し、`docs/spec.md` に反映した。
- Sprint3 M3 は `diagnosis candidate implementation` とし、`generate-diagnosis-candidates` の synthetic fixture 検証まで完了した。
- Sprint3 M4 は `improvement and auto-decision implementation` とし、`generate-improvement-candidates` と `generate-auto-decisions` の synthetic fixture 検証まで完了した。
- Sprint3 M5 は `human-review pipeline connection` とし、`adapt-diagnosis-candidates` で candidate pipeline を既存 M24-M27 human-review pipeline に接続した。
- Sprint3 M6 は、ユーザー協業による redacted real-trace E2E として GitHub Copilot CLI / GitHub Copilot Chat 両方の candidate pipeline 入力互換性確認まで完了した。
- 実 repository 修正を伴う自動改善実装は Sprint3 と Sprint4 の既定スコープ外とし、Sprint5 以降の候補として安全境界を定義してから扱う。
- Sprint4 は Agent workflow observability dashboard を扱う。初期 goal は、独自 Web UI や本番 Grafana 採用ではなく、既存 normalized measurement と Sprint3 candidate outputs を dashboard dataset / view に接続する要件定義である。
- Sprint4 M1 `dashboard requirements` は完了した。
- Sprint4 M2 `dashboard dataset contract` は完了した。normalized measurement、diagnosis candidate、improvement candidate、auto-decision record、human review records から Grafana-first dashboard prototype と static report に渡せる 4 logical table の CSV / JSON schema を定義した。
- Sprint4 M3 `synthetic dashboard data` は完了した。`generate-dashboard-dataset` で synthetic fixture から M2 dashboard dataset を生成し、TTFT fallback、estimated cost、operation timing、retry、approval wait、subagent wait、stuck / long-running flags、nullable backlog age、PII 非混入を確認した。
- Sprint4 M4 `dashboard prototype path` は完了した。Grafana JSON dashboard を第一候補とし、static report、repository-local preview と同じ基準で比較した。
- Sprint4 M5 `review and handoff` は完了した。Sprint4 の要件と prototype 方針を review し、Sprint5 以降の実装範囲を分離した。
- Sprint5 planning では、Enterprise Grafana の導入・運用負荷を避けるため、GitHub Pages 向け static HTML dashboard を Grafana 代替の常設 dashboard 第一候補に変更した。
- Sprint5 static dashboard は GitHub Actions で毎日生成し、raw store / normalized dataset から dashboard dataset を再生成して、HTML と JSON dataset を GitHub Pages に publish する。
- Sprint5 static dashboard は `/latest/` と `/YYYY-MM-DD/` の日次 snapshot を持ち、snapshot 履歴は自動削除しない。
- Sprint5 static dashboard は実データ由来の集計値、参照 ID、分類属性、`user.id`、`user.email` を表示・filter 対象に含める。ただし raw prompt / response / tool arguments / tool results の全文は表示しない。
- Sprint5 static dashboard の初期 view は Sprint4 を踏襲し、初期 client-side filter は date、user、client、experiment、variant、status とする。
- Sprint5 M1 `static dashboard requirements and source boundary` は完了した。static dashboard の表示可能データ、非表示データ、入力 source 境界を sprint-local に確定した。
- Sprint5 M2 `static dashboard artifact contract` は完了した。`generate-static-dashboard` command、`index.html` / `dashboard-data.json` artifact、Pages layout、Actions contract を sprint-local に確定した。
- Sprint5 M3 `local static dashboard generator` は完了した。`generate-static-dashboard` で dashboard dataset JSON から static HTML と sanitized JSON dataset を生成する。
- Sprint5 M4 `daily GitHub Actions publish workflow` は完了した。scheduled / manual GitHub Actions で test、dataset 生成、static artifact 生成、Pages artifact deploy を行う。
- Sprint5 M5 `real-data snapshot validation` は完了した。`artifacts/dashboard-input/` の staging contract、real-data-shaped static dashboard sanitization coverage、user 表示、snapshot metadata、raw content / credential / sensitive path 非表示を検証した。

## Follow-up

- Langfuse UI は source of truth ではなく dashboard / trace viewer の optional side path として扱う。
- Sprint3 では、trace から failure category / anti-pattern 候補を deterministic に自動抽出し、改善候補と自動採用判断を含む auto-decision record に接続した。
- Sprint3 の sensitive local output は、明示 opt-in 時に実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含められる。ただし repository には保存しない。
- Sprint3 の existing M24-M27 human-review command / schema は置換せず互換性維持対象とし、candidate pipeline からの adapter / mapping contract を確定して実装した。
- Sprint3 の sensitive bundle は自動削除 command を実装せず、`manifest.json` の `delete_target_paths` を確認したユーザーが手動削除する。
- Sprint3 の real-trace E2E は、agent が GitHub Copilot CLI で進められる作業を担当し、GitHub Copilot Chat への prompt 送信などユーザー実施の方が低コストな作業はユーザー協業で完了した。
- Sprint4 dashboard は raw prompt / response / tool arguments / tool results の一覧表示を目的にしない。raw content が必要な調査は trace id、candidate id、evidence ref から既存 trace viewer / sensitive bundle へ drilldown する。
- Sprint5 static dashboard も raw prompt / response / tool arguments / tool results の一覧表示を目的にしない。GitHub Pages には実データ由来の集計値、参照 ID、分類属性を publish し、`user.email` は実値を表示してよい。
- Sprint5 static dashboard では、将来 email と表示名の mapping による表示切替を可能にする。
- Sprint5 M5 では、実データ由来入力の既定 staging path を `artifacts/dashboard-input/` とし、JSON 入力は accidental commit を避けるため git ignored のまま扱う。workflow / live validation で使う場合も raw prompt、response、tool arguments / results、credential、authorization header、sensitive bundle content / local path は配置しない。
- Outcome linkage として GitHub / Notion / issue / PR などの成果側指標を同じ dashboard に並べる構想は扱うが、外部 API 連携、個人 identity mapping、HR system 連携、本番 ETL は Sprint4 初期スコープ外とする。
- 共有環境、実データ、社内サーバー検証が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。
- AppHost ローカルランチャー化は Sprint2.5 の対象外であり、扱う場合は先に `docs/spec.md` 9 の AppHost 方針を再確認する。

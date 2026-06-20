# Roadmap And History

この文書は repository 全体の roadmap / history index である。
プロダクト仕様の正は [docs/requirements.md](requirements.md)、[docs/spec.md](spec.md)、[docs/specifications/](specifications/) とする。

## Current Product Focus

現在の中心は Local-first な Agent workflow observability である。

- Copilot clients から OTel を収集する。
- Langfuse で個別 trace を確認する。
- saved raw OTLP JSON から normalized dataset を生成する。
- deterministic candidate pipeline で診断・改善候補を整理する。
- static HTML dashboard を生成し、必要に応じて GitHub Pages snapshot として公開する。

## Historical Work

| Area | 状態 | 概要 |
| --- | --- | --- |
| Langfuse baseline | 完了 | [docs/sprints/sprint1-langfuse-poc/](sprints/sprint1-langfuse-poc/) に Langfuse 直接送信、設定 CLI、計測 schema、human approval workflow までの履歴を保存した |
| Raw Data Loop | 完了 | [docs/sprints/sprint2-raw-data-loop/](sprints/sprint2-raw-data-loop/) で raw OTLP file ingest、SQLite raw store、normalized dataset、Langfuse 非依存 loop を実装した |
| ConfigCli Maintainability | 完了 | [docs/sprints/sprint2-5-maintainability/](sprints/sprint2-5-maintainability/) で ConfigCli 分割、CSV / JSON 共通処理集約、redacted real-trace 互換性確認を行った |
| Trace Diagnosis | 完了 | [docs/sprints/sprint3-trace-diagnosis/](sprints/sprint3-trace-diagnosis/) で deterministic 診断候補生成、改善候補生成、自動採用判断 record を実装した |
| Observability Dashboard | 完了 | [docs/sprints/sprint4-observability-dashboard/](sprints/sprint4-observability-dashboard/) で dashboard view、metric、dimension、drilldown、dataset contract を定義した |
| Static Dashboard | 完了 | [docs/sprints/sprint5-static-dashboard/](sprints/sprint5-static-dashboard/) で static HTML dashboard、GitHub Actions publish workflow、dashboard input staging contract を実装した |

## Open Follow-ups

- GitHub Pages access control の環境確認。
- 初回 live workflow 実行結果の確認。
- 日次 snapshot の repository size monitoring。
- email / display name mapping。
- shared dashboard の access control、retention、利用者周知。
- external outcome linkage の product / security decision。
- 実データを扱う場合の masking / redaction 方針。

## Rule For New Work

新しい product behavior を追加する場合は、実装前に以下を更新する。

1. [docs/requirements.md](requirements.md)。
2. [docs/spec.md](spec.md)。
3. 該当する [docs/specifications/](specifications/) file。
4. 必要な user guide または contributor guide。

Sprint-local notes は履歴として残してよいが、仕様を sprint-local document だけに閉じ込めない。

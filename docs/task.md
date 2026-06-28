# Roadmap And History

この文書は repository 全体の roadmap / history index である。
プロダクト仕様の正は [docs/requirements.md](requirements.md)、[docs/spec.md](spec.md)、[docs/specifications/](specifications/) とする。

## Current Product Focus

現在の中心は Local-first な Agent workflow observability である。

- Copilot clients から OTel を収集する。
- collection profile で telemetry routing mode を切り替える。
- `raw-only` を最小必須 profile、`docker-desktop-langfuse` を標準 full profile とする。
- Langfuse で個別 trace を確認する。
- saved raw OTLP JSON から normalized dataset を生成する。
- deterministic candidate pipeline で診断・改善候補を整理する。
- static HTML dashboard を生成し、必要に応じて GitHub Pages snapshot として公開する。

## Planned Work

| Area | 状態 | 概要 |
| --- | --- | --- |
| Monitor Agent Execution View | M1 実施中 | [docs/sprints/sprint9-monitor-agent-execution-view/](sprints/sprint9-monitor-agent-execution-view/) で Local Ingestion Monitor を「取り込み健全性」から「エージェント実行詳細」へ拡張する。受信済み OTel から sanitized な per-span projection（どのツール/MCP・成否・sub-agent のモデル/トークン・turn 単位トークン）を生成し、VS Code Agent Debug View を参考にした機能的 UI で表示する。決定 D021（非目的の絞り込み）/ D022（span projection）/ D023（raw 既定表示＋`--sanitized-only` 安全弁、D020 更新）を M1 で記録。デザイン改善は後続 Sprint。 |

## Historical Work

| Area | 状態 | 概要 |
| --- | --- | --- |
| Langfuse baseline | 完了 | [docs/sprints/sprint1-langfuse-poc/](sprints/sprint1-langfuse-poc/) に Langfuse 直接送信、設定 CLI、計測 schema、human approval workflow までの履歴を保存した |
| Raw Data Loop | 完了 | [docs/sprints/sprint2-raw-data-loop/](sprints/sprint2-raw-data-loop/) で raw OTLP file ingest、SQLite raw store、normalized dataset、Langfuse 非依存 loop を実装した |
| ConfigCli Maintainability | 完了 | [docs/sprints/sprint2-5-maintainability/](sprints/sprint2-5-maintainability/) で ConfigCli 分割、CSV / JSON 共通処理集約、redacted real-trace 互換性確認を行った |
| Trace Diagnosis | 完了 | [docs/sprints/sprint3-trace-diagnosis/](sprints/sprint3-trace-diagnosis/) で deterministic 診断候補生成、改善候補生成、自動採用判断 record を実装した |
| Observability Dashboard | 完了 | [docs/sprints/sprint4-observability-dashboard/](sprints/sprint4-observability-dashboard/) で dashboard view、metric、dimension、drilldown、dataset contract を定義した |
| Static Dashboard | 完了 | [docs/sprints/sprint5-static-dashboard/](sprints/sprint5-static-dashboard/) で static HTML dashboard、GitHub Actions publish workflow、dashboard input staging contract を実装した |
| Collection Profiles | レビュー完了 | [docs/sprints/sprint6-collection-profiles/](sprints/sprint6-collection-profiles/) で `CAO_COLLECTION_PROFILE`、raw-only minimum、Docker Desktop / WSL2 / remote managed routing profiles の code / docs を実装し M6 review で accept した。`raw-local-receiver` は Sprint7 へ handoff 済み。remote managed live validation のみ外部 access-control / consent 判断待ち |
| Local Raw Receiver | synthetic validation 完了 / live validation 待ち | [docs/sprints/sprint7-local-raw-receiver/](sprints/sprint7-local-raw-receiver/) で repository-local foreground receiver、`raw-local-receiver` profile output、raw store integration、synthetic JSON/protobuf smoke validation を実装した。VS Code direct telemetry live validation は未確認 |
| Local Raw Receiver Monitor | **完了** | [docs/sprints/sprint8-local-raw-receiver-monitor/](sprints/sprint8-local-raw-receiver-monitor/) で ConfigCli から共有 `Telemetry` / `Persistence.Sqlite` project を抽出した（M1）。M2 で `CopilotAgentObservability.LocalMonitor` ASP.NET Core host、loopback bind、Host header 検証、`POST /v1/traces` JSON/protobuf 受信、SQLite raw store 永続化、`413` body-size 境界、`--enable-raw-view` option、`profile-vscode-env --target monitor` / `--endpoint` を追加した。M3 で bounded-channel ingestion queue + 単一 SQLite writer worker（commit 後のみ `2xx`、queue full `503` / commit timeout `504` / shutdown `503` / DB busy `503` / 非 busy 失敗 `500`）、`schema_version` additive migration（空の projection tables を allowlist 列で作成、raw/PII なし、`raw_records` 維持、WAL + busy_timeout 並行読取）、readiness 閾値オプション、最初の `/health/*` を追加した。M4 で sanitized projection builder（`Telemetry/Monitoring/`、allowlist のみ、非空 `trace_id` ごとに `monitor_traces` へ fan-out、trace_id 無しは ingestion 行のみで stall させない）、冪等 projection 投入（`INSERT OR IGNORE` + ガード付き集約 upsert）、`ProjectionWorker`（startup catch-up / `SQLITE_BUSY` retry / 非busy失敗の隔離 / migration 完了待ち）、`/api/monitor/ingestions`・`/api/monitor/traces` cursor API（sanitized、`after`/`limit`、`limit+1` 終端、`raw_record_id` と projection-id の cursor domain、payload 非ロード、不正 query `400`）、projection-lag readiness の有効化（`ready`/`degraded`/`not_ready`。loopback/db/writer/projection-worker/projection-status を必須ゲート化）、opt-in raw-detail route `GET /traces/{rawRecordId}/raw`（`--enable-raw-view` 時のみ存在＝無効時 `404`、cross-site `403`、`Cache-Control: no-store`、HTML エスケープ inert 描画）を追加した。検証は build 0 警告・0 エラー、test 421 passing（300 ConfigCli + 121 LocalMonitor、M3 baseline 371 超）。Codex implementation review の指摘2件（readiness の必須ゲート漏れ / projection-status の stale zero-lag）を修正済み。M5 で sanitized Razor Web UI（`/`・`/ingestions`・`/traces`・`/diagnostics`、allowlist 列のみ・`Html.Raw` 不使用・`raw` リンクは `--enable-raw-view` 時のみ M4 opt-in route へ）、notification-only SSE（`GET /events`、`text/event-stream`、projection 後に `data: {}` のみ・raw/PII 非送出・同期購読登録で取りこぼし無し・gap recovery は `/api/monitor/*` cursor）、`wwwroot/monitor.js`（通知ごとに cursor API 再読込、DOM に raw 非挿入）を追加。static asset は Production 既定環境でも配信されるよう static web assets を明示ロード。検証は build 0/0、test 433 passing（300 ConfigCli + 133 LocalMonitor、M4 baseline 421 超）＋実 `dotnet run` スモークで全ページ・静的アセット 200。M5 までで raw 表示の de-scope（framework 既定エスケープ）を維持。M6 は検証マイルストーンとして DR6 negative security matrix（default UI/API/SSE が raw/PII 非返却・raw route フラグ無し 404／フラグ有り cross-site 403・SSE GET 限定で cross-origin POST 拒否・非 loopback Host 400・error 応答に raw/DB path/user 名なし）、HTTP レベル readiness 失敗（瞬間 backpressure/commit timeout と閾値未満 lag は 200 degraded、持続 stall・projection_lag_exceeded・projection_status_unknown は 503）、同一 DB restart recovery を追加（いずれも既存実装で合格、production 変更なし）。build 0/0・test 445 passing（300 ConfigCli + 145 LocalMonitor）。`profile-vscode-env --target monitor` は 4320+http/protobuf を出力、実プロセス合成 OTLP で取り込み→projection→`/health/ready` 200 を確認。ただし**実 VS Code Copilot Chat のライブ検証は人手必須でブロック**（`milestones/M6-security-live-validation/live-validation.md`）。Sprint8 はこのライブ検証が hard gate のため**未完了**。 |

## Open Follow-ups

- **未対応の機能仕様・検証（未確認の検証フェーズ）**
  - リモート・プロファイル（`remote-managed-*`）の実環境での疎通検証（Live Validation）。
  - 静的ダッシュボード publish workflow の実 repository 上での初回 GitHub Actions / GitHub Pages ライブ実行結果確認。
  - ローカル・レシーバーの常駐起動・配置オプション（IIS / IIS Express ホスティング、Windows サービス化、タスクトレイアプリ化など）の評価とパッケージング検討。
- **未検討・未決定の意思決定事項（Open Product / Security Decisions）**
  - 静的ダッシュボード（GitHub Pages 等）の共有運用におけるアクセス制御の具体設計。
  - 共有ダッシュボードのデータ保持期間（Retention）、削除方法、利用者への周知方法。
  - 実データ運用時のマスキング・秘匿化（Redaction）方針の決定。
  - Collector / remote / shared operation での TLS、SSO、sampling、credential handling の採用方針。
  - リモート・プロファイル利用時のユーザー同意ワークフロー（User Consent Workflow）の実装・定義。
  - ID情報のハンドリング（`user.id` / `user.email` から表示名（Display Name）へのマッピング設計）。
  - 外部成果物との紐付け（External Outcome Linkage - GitHub Issues, PR, Notion等）の採用可否とプロダクト/セキュリティポリシーの策定。
  - 日次ダッシュボードスナップショットによるリポジトリ肥大化の監視および対策。
- **仕様上のプレースホルダー・保留事項**
  - `dashboard-dataset` における Outcome Linkage Candidate のプレースホルダー実装の具体化。
  - 互換性のために残されている古いエントリポイント（`langfuse-*`, `collector-*`）のクリーンアップ時期と方法の決定。

## Rule For New Work

新しい product behavior を追加する場合は、実装前に以下を更新する。

1. [docs/requirements.md](requirements.md)。
2. [docs/spec.md](spec.md)。
3. 該当する [docs/specifications/](specifications/) file。
4. 必要な user guide または contributor guide。

Sprint-local notes は履歴として残してよいが、仕様を sprint-local document だけに閉じ込めない。

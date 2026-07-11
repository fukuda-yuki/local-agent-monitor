# Technical Specification Index

この文書は Copilot Agent Observability の技術仕様入口である。
詳細は機能・レイヤーごとに分割した [docs/specifications/](specifications/) を正本とする。

## Source Of Truth

仕様判断は次の順で扱う。

1. [docs/requirements.md](requirements.md)
2. この文書
3. [docs/specifications/](specifications/)
4. [docs/architecture.md](architecture.md)
5. [docs/decisions.md](decisions.md)
6. 現行実装

`docs/sprints/` は履歴と根拠を残す場所であり、現行仕様の正本ではない。
Sprint 資料を根拠に製品仕様を変える場合は、先に `docs/requirements.md`、この文書、または `docs/specifications/` に反映する。

## Current Product Shape

Copilot Agent Observability は Local-first な Agent workflow observability 製品である。

現在の標準構成:

```text
Copilot clients
  -> OTLP HTTP
  -> Langfuse trace viewer
  -> saved raw OTLP JSON
  -> Config CLI
  -> SQLite raw store
  -> normalized measurements
  -> candidate records
  -> dashboard dataset
  -> static HTML dashboard
```

Collection profile は telemetry routing mode を表す public interface である。
Profile selector は `CAO_COLLECTION_PROFILE` とし、詳細は
[specifications/interfaces/collection-profiles.md](specifications/interfaces/collection-profiles.md) を正本とする。

最小構成は `raw-only` であり、Langfuse、Docker Desktop、WSL2 Docker Engine、Collector、remote endpoint、background process なしで saved raw OTLP JSON から raw data loop を実行する。
標準 full profile は `docker-desktop-langfuse` である。

`raw-local-receiver` は Langfuse なしで VS Code からこの repository の local receiver へ直接 telemetry を送信する profile であり、Sprint7 の実装対象とする。

Local Ingestion Monitor は、`raw-local-receiver` の telemetry をローカルで確認するための単一 ASP.NET Core プロセス（loopback-only）であり、Sprint8 で実装した。Sprint7 の Config CLI receiver（`127.0.0.1:4319`）と並存し、別 loopback port（既定 `127.0.0.1:4320`、Collector の `4317`/`4318` と Sprint7 CLI receiver の `4319` を回避）で動作する。OTLP HTTP/protobuf を受信して SQLite raw store に永続化し、sanitized monitor projection、ローカル UI、health endpoint を提供する。Sprint9 で受信済み OTel テレメトリから per-span の sanitized projection（`monitor_spans`）を追加し、agent-execution view としてツール / MCP 呼び出し名、成否、sub-agent のモデル / トークン、turn 単位トークンを表示する。Sprint10 で TraceDetail ページに4つの設計ビュー（Summary / Timeline / Flow Chart / Cache）をタブ UI として追加し、Flow Chart は当初 vendored Cytoscape.js + dagre（MIT、`wwwroot/vendor/`、CDN 不使用）でレンダリングした（Sprint12 で素の DOM へ置換。後述）。全ビューは既存の sanitized spans API（`GET /api/monitor/traces/{traceId}/spans`）のみを消費し、raw 境界・PII 境界の変更はない。ビジュアルテーマは VS Code Dark+ を基盤とし（D027）、タイポグラフィは vendored Noto Sans JP / Noto Sans Mono（D028、`wwwroot/vendor/fonts/`、OFL）。raw body と PII は既定で表示する（server-rendered、inert text）。`--sanitized-only` フラグで metadata-only モードを復元できる。Sprint12 で UI を刷新し、ダッシュボード（`/`）とトレース一覧（`/traces`）に各トレースの代表ユーザープロンプトを server-rendered 表示してトレースをプロンプトで識別できるようにした（raw-bearing 拡張。same-origin / `Cache-Control: no-store` / `--sanitized-only` 除去を強制し、`/api/monitor/*`・SSE の sanitized 不変条件は維持。D032）。Flow Chart は Cytoscape を廃し、Span Tree（インデント + ウォーターフォールバー）と DOM フローの toggle 切替として素の DOM で再実装した（D033）。`/ingestions` ページは廃止してダッシュボードへ統合し、UI は日本語化、ナビは3項目（ダッシュボード / トレース / 診断）に集約した。Windows では `scripts/local-monitor/` の PowerShell scripts により user-level Windows Task Scheduler の logon startup を任意登録できる。Sprint14 で Windows x64 self-contained folder publish の Release ZIP 配布面を追加し、利用者端末では source build や .NET SDK / Runtime 事前導入を要求しない。Sprint18 で UI をデザインハンドオフ準拠の Console 型 IA へ全面再設計した（D042）: 208px 左サイドバー + 2 項目ナビ（概要 / トレース）、診断はステータスバッジ → ポップオーバー経由、概要ダッシュボード（期間別 token KPI / モデル別内訳 / キャッシュ効率 / 高コスト TOP5 / 時間帯分布 / 最近のトレース）、トレース一覧の master-detail 化（テーブル + 右プレビュー）、トレース詳細のタブ廃止（フロー | waterfall セグメント切替 + 常設キャッシュ列）、スパンインスペクタ（整形 / raw タブ、新規 raw-bearing route。D043）、エラー解析モード、Copilot 解析ドロワー（履歴再送チャット。D045）。デザイントークンはハンドオフ §10 の hex 値を正とする。これを支えるため monitor projection schema v4 で trace 単位 cache token rollup と `trace_status` を additive に追加し（D044）、sanitized 集計 endpoint `GET /api/monitor/overview` と `GET /api/monitor/trace-list` を新設した。既存 public routes の shape / ordering は変更していない。詳細は [specifications/layers/telemetry-ingestion.md](specifications/layers/telemetry-ingestion.md) と [specifications/security-data-boundaries.md](specifications/security-data-boundaries.md) を正本とする。

GitHub Copilot app Canvas adapter は、Sprint11 PoC（`/api/monitor/*` と `/health/ready` の sanitized data から bounded action DTO を返す薄い adapter、loopback / per-launch token / raw 非送出境界、D029 / D030）として実装した任意統合である。Sprint15 で、これを Local Monitor の既存 API / view model / projection を再利用した診断 surface へ引き上げる（D036）。Sprint15 ではまず表示境界を変えない子 A（拡張所有ヘルパーページの UX 改善：trace を status / model / span 数 / tool 数 / token / duration / time / 短縮 trace id を含む判断可能な一覧にし、focus / ボタン / 見出しを日本語化、health / error 状態と次操作を具体化、health 生レスポンスを既定折りたたみ）を実装する。子 B（Canvas dashboard view）は Local Monitor 側に sanitized 集計 endpoint `GET /api/monitor/summary` を追加して Razor Index と共用し、子 C（Canvas trace detail view）は既存 bounded action のロジックを再利用した選択トレースの要約カードをヘルパーページに追加する（いずれも D037 で設計確定し本スプリントで実装）。子 D（Canvas raw preview）は既存の raw-bearing route `GET /traces/{rawRecordId}/raw`（HTML エンコード済み固定フォーマット）から server-to-server で取得しそのまま埋め込む新規ページ遷移ルート `GET /raw-preview/:traceId/:spanId` として実装する（D038）。子 B 残作業として Canvas ヘルパーページに新規ルート `GET /api/summary` 経由の「Local Monitor 概要」カードを追加する（D038）。D037 で見送った OTel 単独の session-to-trace correlation は、Issue #51 の明示的な Session event input と exact-link 規則を前提に限って supersede する。Issue #51 は SDK stream / compatible Hook / exact OTel enrichment を別 Session subsystem へ取り込み、Canvas `ctx.sessionId` を native session ID として Session workspace を構築する。repository / timestamp proximity では結合せず、OTLP receiver と既存 monitor projection は変更しない（D051）。Sprint16 では `.github/extensions/otel-monitor-canvas/` を唯一の copyable extension distribution unit とし、既存 OTLP Resource Attributes `vcs.repository.name` / `workspace.name` / `repo.snapshot` から sanitized `repository_name` / `workspace_label` / `repo_snapshot` を Local Monitor projection と Canvas helper / bounded action DTO へ追加する（D040）。Sprint17 では helper page の `session.send()` 分析トリガーに requested profile / model / reasoning / timeout hint controls と dispatch metadata を追加するが、Local Monitor raw analysis runner へ置き換えず、per-message execution control がない値を effective execution metadata として主張しない。Canvas action response は bounded DTO のままで、Issue #51 が明示する Session ingest / workspace interfaces 以外の UI 再実装・telemetry input / schema / API field・Copilot actions への raw / PII 送出は行わない。Issue #51 は Issue #45 の `session.send()` behavior と Issue #49 の Agent ownership を変更しない。Session UI 着手前に Issue #52 の current-screen capture と承認済み four-tab prototype を必須 gate とする。

Issue #54 では Canvas Improve を `canvas-improvement-proposals.md` の local runtime proposal lifecycle として実装する。詳細分析は既存の token-gated `POST /analyze` -> `session.send()` に限定し、モデル応答を取得・保存・再利用しない。Canvas が保存するのは人間が明示入力した sanitized proposal fields と既存 Session / Evidence への opaque reference だけである。Candidate は1つ以上の citeable evidence、Recommended は2つ以上の distinct exact-bound Session の evidence と明示 promotion を要する。Verified は Issue #56 comparison だけが設定できる。direct apply、diff、file path、snapshot、rollback、git 操作は Issue #55 の責務である。

Issue #55 は `canvas-proposal-apply.md` の trusted-local apply boundary として、Issue #54 proposal の明示承認後に限る file mutation を追加する。Canvas action と `session.send()` は apply payload を扱わず、per-launch token を持つ helper screen が Local Monitor の loopback API を proxy する。Local Monitor は startup `--apply-root <kind>=<absolute-directory>` でのみ root を受け取り、opaque root ID / relative path / existing regular file の allowlist、non-reparse ancestry、base SHA-256 / hunk-selection digest / immutable approval digest を再検証する。全 target が書込み直前に一致しない `stale` の場合は no-write で停止する。snapshot + write-ahead journal + recovery により transaction は all-applied or all-restored とし、rollback は current-hash precondition を持つ。source/diff text と absolute path は helper local display 以外へ出さず、git 操作・任意 path・directory/delete/rename・automatic apply は追加しない。

GitHub Pages publish workflow は現行スコープから削除した（D049）。Static dashboard は `generate-static-dashboard` で local artifact として生成する。

## Specification Map

| Area | Spec |
| --- | --- |
| 実装仕様入口 | [specifications/README.md](specifications/README.md) |
| Telemetry ingestion | [specifications/layers/telemetry-ingestion.md](specifications/layers/telemetry-ingestion.md) |
| Raw store and normalization | [specifications/layers/raw-store-normalization.md](specifications/layers/raw-store-normalization.md) |
| Candidate pipeline | [specifications/layers/candidate-pipeline.md](specifications/layers/candidate-pipeline.md) |
| Dashboard publishing | [specifications/layers/dashboard-publishing.md](specifications/layers/dashboard-publishing.md) |
| Security and data boundaries | [specifications/security-data-boundaries.md](specifications/security-data-boundaries.md) |
| Collection profile interface | [specifications/interfaces/collection-profiles.md](specifications/interfaces/collection-profiles.md) |
| Config CLI interface | [specifications/interfaces/config-cli.md](specifications/interfaces/config-cli.md) |
| Normalized measurement dataset interface | [specifications/interfaces/measurement-dataset.md](specifications/interfaces/measurement-dataset.md) |
| Candidate record interfaces | [specifications/interfaces/candidate-records.md](specifications/interfaces/candidate-records.md) |
| Human-review record interfaces | [specifications/interfaces/human-review-records.md](specifications/interfaces/human-review-records.md) |
| Dashboard dataset interface | [specifications/interfaces/dashboard-dataset.md](specifications/interfaces/dashboard-dataset.md) |
| Instruction diagnosis analysis interface | [specifications/interfaces/instruction-diagnosis-analysis.md](specifications/interfaces/instruction-diagnosis-analysis.md) |
| Canvas Session workspace interface | [specifications/interfaces/canvas-session-workspace.md](specifications/interfaces/canvas-session-workspace.md) |
| Canvas Session Evidence interface | [specifications/interfaces/canvas-session-evidence.md](specifications/interfaces/canvas-session-evidence.md) |
| Canvas Improvement Proposal interface | [specifications/interfaces/canvas-improvement-proposals.md](specifications/interfaces/canvas-improvement-proposals.md) |
| Canvas Proposal Apply interface | [specifications/interfaces/canvas-proposal-apply.md](specifications/interfaces/canvas-proposal-apply.md) |
| Contributor workflow | [contributor-guide.md](contributor-guide.md) |

## Public Interfaces

Publicly documented interfaces are:

- Config CLI command names, arguments, CSV / JSON output shape。
- Collection profile names and `CAO_COLLECTION_PROFILE` values。
- `OTEL_RESOURCE_ATTRIBUTES` keys and recommended values。
- Dashboard dataset JSON and CSV logical tables。
- Static dashboard artifact layout: `index.html` and `dashboard-data.json`。
- Data safety boundary for repository-stored files。
- Local Ingestion Monitor loopback endpoints: `POST /v1/traces`、`GET /api/monitor/ingestions`、`GET /api/monitor/traces`、`GET /api/monitor/traces/{traceId}/spans`、`GET /api/monitor/summary`、`GET /api/monitor/overview?period=today|7d|30d`（期間別 token KPI / モデル別集計 / 時間帯分布。D042/D044）、`GET /api/monitor/trace-list?q&model&status&period&sort&offset&limit`（offset paging のトレース一覧 + cache 集計 + `trace_status`。`q` は TraceId 部分一致のみで prompt 本文は検索・返却しない。D042/D044）、`GET /health/live`、`GET /health/ready`、および SSE notification stream。`/api/monitor/*` と SSE は raw / PII を返さない（sanitized のみ）。`/health/ready` は飽和継続時に `503`（瞬間的 backpressure は `degraded` の `2xx`）を返し、`status` / `checks` / `degraded_reasons` を持つ機械可読 body を伴う。既定しきい値は ingestion-stall `10s` / projection-lag `60s`（設定可能）。
- Local Ingestion Monitor raw-bearing routes（既定表示）: trace-detail page（agent-execution view、bounded raw preview inline + full raw record link）、`GET /traces/{rawRecordId}/raw`（server-rendered HTML）、`GET /traces/{traceId}/prompt-label`（JSON、D039）、`GET /traces/{traceId}/spans/{spanId}/detail`（スパンインスペクタ用 JSON: tool 呼出引数 / 結果末尾、llm メッセージ構成 / プレビュー、raw span JSON。D043）、および ダッシュボード（`/`）と トレース一覧（`/traces`）。後者2つは各トレースの代表ユーザープロンプトを server-rendered または same-origin prompt-label route fetch で表示する（raw store の OTLP payload から抽出、truncated、escaped inert text。prompt ラベルのみ raw でその他列は sanitized metadata。D032 / D039 / D042）。raw-bearing route set の全 route で same-origin 強制（cross-site は `403`）、`Cache-Control: no-store`。`--sanitized-only` 起動時は raw-bearing route / raw section を除去（raw-detail route は `404`、dashboard / traces の prompt ラベルは省略し短縮 TraceId にフォールバック）、PII は除外。prompt ラベルは `/api/monitor/*` と SSE には含めない。full-payload JSON raw API は提供しない。Canvas helper は、拡張所有 loopback server の token-gated local screen として、既存 raw-bearing span detail route から選択 trace の prompt / response preview を server-to-server 取得して表示してよく（D050）、同じ token-gated helper screen の `/api/traces` と `/api/summary` highlight trace label でも prompt label を表示してよい（D039 / D050）。Canvas action responses、`session.send()` prompts、logs、repository-safe outputs、static artifacts には raw prompt / response / prompt label を含めない。
- Local Ingestion Monitor run interface: loopback port（既定 `http://127.0.0.1:4320`）、`--port` / `--url`、`--sanitized-only`（metadata-only モード。raw-bearing route を `404` にし PII を除外）、リクエスト本文サイズ上限 `--max-request-body-bytes`（既定 `31457280` bytes = 30 MiB、env `CAO_MONITOR_MAX_REQUEST_BODY_BYTES`）。`POST /v1/traces` は本文が上限を超えると `413` / `request_too_large` を返し raw を書かない。

  Update (D039 / D042 / D050): Local Monitor の client-side overview / trace-list と Canvas helper は、same-origin / token-gated local screen 上で `GET /traces/{traceId}/prompt-label` を `fetch` し、prompt label を `textContent` 相当の inert text として表示してよい。これは full raw payload の client-side fetch 許可ではなく、`/api/monitor/*` と SSE は prompt-free のまま。
- Local Ingestion Monitor Windows startup scripts: `scripts/local-monitor/start.ps1`、`stop.ps1`、`status.ps1`、`set-startup-task.ps1`、`install-startup-task.ps1`、`uninstall-startup-task.ps1`、`install-user-env.ps1`、`uninstall-user-env.ps1`。Task Scheduler task の既定名は `CopilotAgentObservability LocalMonitor`、trigger は current user logon、既定 URL は `http://127.0.0.1:4320`、既定 DB / logs / state は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下。Task 登録 script は client routing 設定を書き換えない。user env script は別の明示操作として current user の永続環境変数に raw-local-receiver / monitor 向け OTLP routing を設定・解除する。startup 登録、enable / disable、今すぐ起動、user env install / uninstall は利用者が明示実行した場合のみ行う。
- Local Ingestion Monitor Release ZIP interface: `.github/workflows/local-monitor-release.yml` と `scripts/local-monitor/package-release.ps1` は Windows x64 self-contained folder publish を `local-monitor-win-x64.zip` として生成する。ZIP layout は `app/`、`scripts/`、`README.md`、`manifest.json`、notices を含む。ZIP scripts は `install.ps1`、`start.ps1`、`stop.ps1`、`status.ps1`、`set-startup-task.ps1`、`install-startup-task.ps1`、`uninstall-startup-task.ps1`、`install-user-env.ps1`、`uninstall-user-env.ps1` を含む。install root 既定は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\`、runtime root 既定は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\`。uninstall は DB / logs を既定保持し、`-RemoveData` 明示時のみ runtime data を削除する。
- Local Ingestion Monitor Copilot raw analysis routes（raw-default only）: `POST /traces/{traceId}/analysis`（CSRF header required; creates a queued local analysis run and dispatches the .NET GitHub Copilot SDK analysis service without embedding raw in the request。Sprint18 で optional `question` / `history`（過去 Q&A turns）を受け付け、runner が prompt に履歴ブロックを追記する history-resend 追い質問に対応。server 側に会話 session 状態は持たない。D045）、`GET /traces/{traceId}/analysis/runs/{runId}`（local raw-derived result; same-origin + `Cache-Control: no-store`）、`GET /traces/{traceId}/analysis/runs/{runId}/safe-summary`（repository-safe allowlist summary）。These routes are not under `/api/monitor/*`; `/api/monitor/*` and SSE remain sanitized-only. `--sanitized-only` removes the analysis routes.
- Local Monitor Copilot raw analysis configuration: `CopilotAnalysis:Enabled`（未設定時は enabled; `false` で local analysis runner を明示無効化）、`CopilotAnalysis:Model`（既定 `gpt-5`）、`CopilotAnalysis:TimeoutSeconds`（raw analysis runner が 1 回の SDK send/wait に許容する実行タイムアウト秒。正の整数。未設定・不正値は既定 `60`。Canvas options の timeout hint とは独立）、`CopilotAnalysis:BaseDirectory`（Copilot SDK runtime state directory; unset uses a writable temp-local LocalMonitor directory）、and optional BYOK provider keys `CopilotAnalysis:Provider:Type`、`BaseUrl`、`WireApi`（`completions` or `responses`）、`ApiKey`。These may be supplied through user-secrets or equivalent local configuration. `ApiKey` must not be logged, persisted in analysis events, or emitted to repository-safe outputs.
- Local Ingestion Monitor Canvas analysis options endpoint: `GET /api/analysis/options` returns sanitized profile/model metadata for the Canvas helper. Initial profiles are `fast` (60s timeout hint, default reasoning `low`), `standard` (180s, `medium`), and `deep` (600s, `high`). Models come from `CopilotAnalysis:Models:*` configuration, with `CopilotAnalysis:DefaultModel` / `CopilotAnalysis:Model` fallback. Returned model fields are id, display name, provider display name, reasoning-effort support, and default marker. Provider secrets, API keys, base URLs, local paths, raw telemetry, and PII are never returned. Timeout values are Canvas requested wait/display hints, not Local Monitor raw runner execution timeouts.
- Local Ingestion Monitor client config: `config-cli profile-vscode-env --profile raw-local-receiver --target monitor`（または `--endpoint`）が monitor endpoint（既定 `http://127.0.0.1:4320`）向けの VS Code env を出力。`--target receiver` 既定は `4319` のまま。
- Canvas adapter は raw default の Local Monitor と併用できる。`--sanitized-only` は Canvas の必須起動条件ではなく、Local Monitor の任意 metadata-only opt-out である。Canvas actions は引き続き既存 sanitized `/api/monitor/*` と readiness を読む bounded DTO surface であり、action responses / logs / committed outputs に raw / PII を返さない。
- Canvas helper analysis trigger: extension-owned `POST /analyze` remains a token-gated `session.send({ prompt })` fire-and-forget trigger. Payload includes `traceId`、optional `spanId`、`focus`、`profile`、`requestedModel`、`requestedReasoningEffort`、`requestedTimeoutSeconds`; response includes dispatch metadata (`analysis_trigger_id`、requested values、`prompt_template_version`、`dispatched_at`、and message id when the SDK exposes one). It does not call `/traces/{traceId}/analysis`, does not wait for a model response, and does not store final analysis result metadata.
- Canvas cross-repo metadata fields: `repository_name`、`workspace_label`、`repo_snapshot` は既存 OTLP Resource Attributes `vcs.repository.name`、`workspace.name`、`repo.snapshot` から生成する sanitized nullable projection fields である。`repo.name` は repository label source として扱わない。これらは `/api/monitor/*`、Canvas helper routes、bounded Canvas action DTO でのみ使用し、既存 projected rows は自動 backfill しない。
- Canvas Session workspace: `POST /api/session-ingest/v1/events`（schema/header version 1、batch 1..100、1 MiB、commit 後のみ `204`、固定 `400/413/415/503/504`）、sanitized `GET /api/session-workspace/sessions` / session detail / `resolve` / `status`、same-origin/no-store の `GET /sessions/{id}/events/{eventId}/content`、および installed `hook-forward --endpoint <loopback-url> --timeout-ms 250`。完全な identity、merge、completeness、retention、response shape は [Canvas Session workspace interface](specifications/interfaces/canvas-session-workspace.md) を正本とする。
- Canvas Session Evidence: 選択 Session の `runs[].trace_id` を run 順で exact-only 合成し、各 trace の Issue #49 `agent-graph` と `spans?limit=200` 全ページを token-gated helper proxy 経由で表示する。Agent ownership は graph を唯一の正本とし、Session events は常に Session/unowned。詳細は [Canvas Session Evidence interface](specifications/interfaces/canvas-session-evidence.md) を正本とする。
- Canvas Improvement Proposals: exact-bound terminal Session の evidence を使う local-runtime `candidate` / `recommended` / `verified` lifecycle。詳細分析は既存 `session.send()` dispatch のままで、Canvas はモデル応答を取得しない。Candidate は citeable evidence、Recommended は2つ以上の distinct exact-bound Session と explicit promotion、Verified は Issue #56 comparison のみが設定する。direct apply / diff / path / rollback は Issue #55 の責務である。詳細は [Canvas Improvement Proposal interface](specifications/interfaces/canvas-improvement-proposals.md) を正本とする。
- Canvas Proposal Apply: Issue #54 proposal を明示承認後に限って、startup `--apply-root user_config|skill|repository=<absolute-directory>` に登録済みの root 内 existing regular file へ適用する。Canvas action / `session.send()` は file authority を持たず、token-gated helper display だけが full diff を扱う。apply は relative path / non-reparse target / all-target current SHA-256 / immutable approval digest を検証し、stale は no-write。snapshot + journal recovery は all-applied or all-restored、rollback は post-apply hash precondition と一回限りを保証する。詳細は [Canvas Proposal Apply interface](specifications/interfaces/canvas-proposal-apply.md) を正本とする。
- Copilot SDK raw analysis is hosted by the Local Monitor process as a .NET GitHub Copilot SDK service. Its internal tool set is `get_raw_trace`、`get_raw_record`、`get_raw_span_context`、`get_trace_summary`、`get_trace_span_tree`、`get_cache_summary`、`get_instruction_evidence`（instruction-diagnosis 向けの deterministic instruction-evidence extractor 出力。bounded `conversation_context` を含む。D047 / D048）; raw-returning tools are process-internal and remain separate from repository-safe summary generation.

Changing these requires updating the relevant specification file and tests.

## Validation

Code、project file、CLI behavior、workflow を変更した場合:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

LocalMonitor browser smoke test が solution test suite に含まれるため、build と test の間に Playwright chromium bootstrap が必要。wrapper は未指定時に `PLAYWRIGHT_BROWSERS_PATH` を repository-local の ignored `artifacts\playwright-browsers` に設定し、browser binaries と Playwright cache lock を writable workspace 内に置く。Linux CI では同じ script に `-WithDeps` を付ける。

Collector example を変更した場合:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

User-facing docs を変更した場合:

- README と user guide のリンク先が存在すること。
- screenshot path が存在すること。
- 古い入口表現が直接入口文書に残っていないこと。

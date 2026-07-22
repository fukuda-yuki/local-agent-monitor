# Requirements

この文書は Copilot Agent Observability の製品要件を定義する。
詳細な実装仕様は [docs/spec.md](spec.md) と [docs/specifications/](specifications/) を参照する。

## 1. 目的

Copilot Agent Observability は、GitHub Copilot Chat、GitHub Copilot CLI、Codex App から出力される OpenTelemetry data を収集し、Agent workflow の挙動を trace 単位と集計単位の両方で確認できる状態を作る。

利用者が判断できるようにするもの:

- agent invocation、LLM call、tool call、permission、file / shell operation の実行過程。
- prompt、response、tool arguments、tool results、token usage、duration、error の trace-level 調査。
- VS Code GitHub Copilot Chat、GitHub Copilot CLI、Codex App の挙動差分。
- baseline、variant、experiment、task ごとの比較。
- raw telemetry から normalized dataset、diagnosis candidate、improvement candidate、auto-decision record、dashboard dataset を再現可能に生成する流れ。
- Agent workflow の健全性、失敗傾向、コスト見積もり、改善候補を俯瞰する static dashboard。

## 2. 利用者

主な利用者:

- Copilot agent workflow の挙動を調査する開発者。
- prompt、skill、MCP、CLI wrapper の改善効果を比較する実装者。
- trace 由来の失敗傾向や改善候補を確認する maintainer。
- dashboard artifact を確認する reviewer。

対象外の利用者像:

- 個人別の勤務評価やランキングを作りたい管理者。
- Copilot seat / billing / adoption analytics を管理したい管理者。
- DLP、監査ログ、機密情報検査の本番基盤を求める管理者。

## 3. 機能範囲

GitHub Copilot first-trace common adapter の各 request は新たに得た一つの `setup status` に current source detection/version と bounded loopback health probe を合成し、historical detected version や plan/apply result を static authority にしない。`agent_restart_required` だけが残る managed-source begin は許可するが、他の blocker は拒否し、観測 unavailable は成功へ変換せず unknown を維持する。

必須機能:

- VS Code GitHub Copilot Chat の OTel trace / metrics / events 収集。
- GitHub Copilot CLI の OTel trace / metrics 収集。
- collection profile による telemetry routing mode の明示的な切り替え。
- raw-only minimum profile。Langfuse、Docker Desktop、WSL2 Docker Engine、Collector、remote endpoint、background process なしで saved raw OTLP JSON から raw data loop を実行できること。
- Docker Desktop + Langfuse standard full profile。ローカル Langfuse trace viewer による個別 trace review と raw data loop の両方を扱えること。
- Docker Desktop + Collector + Langfuse profile。
- WSL2 Docker Engine + Langfuse profile。
- WSL2 Docker Engine + Collector + Langfuse profile。
- remote managed Langfuse profile。
- remote managed Collector profile。
- repository-hosted raw local receiver profile。Langfuse なしで VS Code からこの repository の local receiver へ telemetry を送信し、raw data loop に接続できること。
- Local Ingestion Monitor。VS Code GitHub Copilot Chat から OTLP HTTP/protobuf を直接受信し、SQLite raw store に永続化し、loopback-only のローカル UI で取り込みの健全性（受信、永続化、projection、エラー有無、health / readiness）を確認できること。さらに、受信済み OTel テレメトリから per-span の sanitized projection を生成し、agent-execution view として、どのツール / MCP を呼び出したか（名前単位）、各呼び出しの成否、sub-agent のモデル / トークン使用量、turn 単位のトークン合計を表示できること。raw body（tool call arguments / results、sub-agent instructions / responses、system prompt）と PII（`user.id` / `user.email`）は既定で表示する（server-rendered、inert text）。`--sanitized-only` フラグで metadata-only モードを復元し、raw section / full raw route を除外して PII を除外できること。ただし sanitized なトレース詳細画面 shell（フロー / waterfall / キャッシュ列などの sanitized ビュー）は `--sanitized-only` でも利用できること。UI は Sprint18 でデザインハンドオフ準拠の Console 型 IA へ再設計した（D042）: 208px サイドバー + 2 項目ナビ（概要 / トレース。診断はステータスバッジのポップオーバー経由）、期間別 token KPI・モデル別内訳・キャッシュ効率・高コスト TOP5 を備える概要ダッシュボード、テーブル + プレビューパネルの master-detail トレース一覧、タブを廃止したトレース詳細（縦型フロー | waterfall セグメント切替、並行 tool 呼出の可視化、常設キャッシュ列）、スパンインスペクタ（整形 / raw タブ。raw 詳細は新規 raw-bearing route。D043）、エラー解析モード（エラー要約・回復済み / 未回復区分・入力トークン推移）、Copilot 解析ドロワー（履歴再送方式の追い質問チャット。D045）として client-side + server-rendered で提示できること。フロー / waterfall / キャッシュ列は既存 spans API（`GET /api/monitor/traces/{traceId}/spans`）と sanitized 集計 endpoint 上の presentation であり、素の DOM（`createElement` / `textContent`）で実装する（D033 / D042）。これを支える projection schema 変更は additive のみ（cache token rollup + `trace_status`。D044）とし、既存 public routes の shape / ordering と raw 境界は変更しない。加えて、トレースを不透明な TraceId ではなく利用者の入力プロンプトでも識別できるよう、ダッシュボード（`/`）とトレース一覧（`/traces`）に代表プロンプトを server-rendered で表示できること。これは raw-bearing な追加であり、既存 raw 面と同一の制御（same-origin、`Cache-Control: no-store`、`--sanitized-only` での除去とその際の短縮 TraceId フォールバック、escaped inert text）に従い、`/api/monitor/*` と SSE は引き続き sanitized metadata のみで prompt を含まない（D032）。Windows では、単一ユーザー向けの任意運用面として user-level Windows Task Scheduler による logon startup と current-user 永続環境変数による monitor routing を提供し、runtime DB / logs / state は既定で `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下に置くこと。Windows x64 では GitHub Actions が作成する self-contained folder publish の Release ZIP から利用者単位に install でき、利用者端末で `dotnet run` / `dotnet build` / `dotnet restore` や .NET SDK / Runtime の事前導入を要求しないこと。Release ZIP の install、今すぐ起動、Task Scheduler startup 登録、startup enable / disable、user env install / uninstall、stop、status、uninstall は分離した操作として扱うこと。
- Local Monitor Copilot raw analysis。raw-default posture では、選択 trace / raw record / span を .NET 版 GitHub Copilot SDK に raw 投入して local analysis できること。raw analysis runs、events、results は local runtime data とし、GitHub Issue / docs / dashboard 向けには raw 本文を含まない repository-safe summary だけを生成できること。analysis focus として、利用者の実装指示を trace 証拠に基づいて診断する指示診断（`instruction-diagnosis`）を含むこと。指示診断の証拠は選択 trace を anchor とし、trace 内部（追い指示・言い換え turn、error span、失敗 / 再試行 tool call、token 浪費）に加えて、同一 `conversation_id` の前後最大 2 trace ずつ（最大 5 entries、earliest span start time then trace id ordering）に限定した bounded sibling summaries を補助証拠として扱えること（D048）。finding 単位の固定 4 点形式（分類 / span・turn・bounded sibling trace 証拠引用 / ギャップ説明 / 次回向け改善指示文）で、finding は日本語で出力し、証拠を引用できない finding は出力しないこと（D046）。指示診断はドロワー表示のみで、Canvas focus set は拡張しないこと。`--sanitized-only` では raw analysis UI、start route、result route を無効化すること（指示診断 focus と bounded conversation scope を含む）。既存 Canvas adapter は置き換えない。
- Instruction finding handoff。`instruction-diagnosis` の提案 finding は process-internal tool で closed taxonomy と exact raw-local evidence index に照合してから、source Session / trace / span ID を kind-specific opaque token に変換した `instruction-finding-handoff.v1` として成功結果と atomic に保存すること。`supported` だけを candidate eligible とし、`weak` / `incomplete` は ineligible receipt に留める。carrier と candidate text は固定 template のみで raw prompt / response、tool data、PII、credential、local path を含めず、zero finding を有効とする（D065）。source-neutral な v1 consumer validator は最大 1048576-byte、最大 JSON depth 16 の canonical UTF-8 bytes のみを受け入れ、unknown field / version、noncanonical bytes、derived identity / template / association / ordering / reference-token 違反を fail closed で拒否し、成功時には正の `analysis_run_id` だけを返す。#72 / #73 / #85 はこの単一の v1 validation authority を再利用し、独自 field / category / serializer / hash / template を追加せず、handoff / validator 自体から effect、apply、export authority を導出しない。#85 の export authority は別の Sanitized Evidence Export contract にのみ由来する。
- Instruction finding consumer compatibility。v1 consumer validator が証明するのは carrier 自身の canonical structure と semantic self-consistency だけであり、producer / store provenance または過去の raw reference resolution を証明しない。trusted owner から exact bytes を取得する責務は caller に残し、pre-tokenization の raw reference resolution は producer pipeline だけが行う。1048576-byte / depth 16 と、その範囲内の single-pass JSON read・有限 semantic reconstruction を consumer の完全な work/cardinality bound とする。closed category から再構築される candidate は最大 8 件である。既存 v1 は duplicate collapse、finding、evidence reference に個別最大数を定義していないため、互換性を壊す per-collection ceiling は追加せず、producer の pre-serialization draft admission も変更しない。
- Session foundation。Issue #51 では Local Monitor に既存 OTLP receiver / monitor projection と分離した Session subsystem を追加し、Copilot SDK stream、Copilot-compatible Hook、既存 OTel の exact-linked enrichment から Session / Run / Event を正規化する。additive tables は `sessions`、`session_native_ids`、`session_runs`、`session_events`、`session_event_content`、`session_projection_state` とし、`RawTelemetryStore.cs` に責務を追加しない。local ID は UUIDv7 string、source uniqueness は SDK event ID / Hook canonical hash / OTel trace-span identity とする。同一 native session ID、明示 resume/handoff、または exact trace context のみ merge を許可し、repository / timestamp proximity では merge しない。completeness は `unbound`（OTel-only / native ID 未結合）、`partial`（native ID あり / lifecycle または input family 不完全）、`rich`（instruction、lifecycle、SDK/Hook または OTel evidence あり / content または terminal evidence 一部欠落）、`full`（surface-required start-to-end evidence あり / unsupported version・ingest gap なし / exact-linked OTel enrichment）の固定4値とする。
- User-controlled retention mutation (Issue #90). The Local Monitor must provide `pin`, `unpin`, and `delete_now` for exact `session` or `item` targets only, using a deterministic preview followed by explicit confirmation. The workflow is idempotent and produces append-only audit events. A confirmed delete denies reads immediately and hands physical deletion to the existing Issue #89 worker. Bulk, heuristic, approximate, path-, repository-, timestamp-, or content-based targeting is prohibited. Every actionable preview and mutation result discloses the fixed backup non-purge warning; backups are not purged by Issue #90.
- Source capability semantic contract v1。`docs/specifications/contracts/source-capabilities/v1/source-capability-manifest.schema.json`（JSON Schema 2020-12）と surface ごとの manifest は構造と capability declaration の正本、canonical Markdown は authority、provenance、completeness、safety、handoff の意味の正本とする。利用可能な OTel identity / hierarchy / timing と Hook/SDK の native lifecycle / explicit event identity を、それぞれの field family の authority とし、repository / workspace / timestamp は identity evidence ではない。lower-authority evidence と missing value は strong value を上書きせず、heuristic merge と synthetic span を許可しない。manifest は content の read / transport / storage authority を与えない。
- Historical source import policy。Tier A の product-owned versioned artifact と exact fixture/fingerprint に結合した Tier B producer history だけを候補 source とし、private / heuristic Tier C store は対象外とする。current GitHub Copilot CLI / Claude Code profile の supported application/format sets と active allowlists は空で、明示 opt-in detector は content を読まず zero candidates を返す。#77 / #78 による exact fixture-bound promotion は D056 ceiling 内の technical compatibility revision とし、#79 は sparse 1–10 allowlisted leaves と field provenance の exact parity、no synthesis、partial / `historical_summary_only`、exact merge、fixture marker rejection を検証する。metadata-only は retention item を作らず、将来の consented content だけを #89 / #90 の契約へ渡す（D066）。Issue #76 自体は policy/contract のみを所有し、adapter、importer、background scan、migration、UI を実装しない。
- Historical source import workflow。Issue #79 は Issue #77 / #78 の adapter output を再 parse せず、明示的な source 選択と同意、versioned preview、別の confirmation、source / database snapshot の stale 再検証、idempotent な all-or-none commit、result / history read を Local Monitor API / UI と Config CLI の共通 contract として提供する。current real profiles は zero-candidate preview のみで commit 不可とし、production の HTTP / CLI payload から synthetic fixture や trusted-admission seam を起動できない。将来 exact profile が promotion された場合のみ、schema-valid output と exact evidence tuple を再検証した typed internal seam が positive workflow を起動できる。accepted candidate は dedicated `historical_import` schema に distinct historical observation / provenance / sanitized conflict / operation receipt として保存し、Session / Run / Event / trace / timestamp を合成しない。source date と new Session / Event count は、権威ある入力がない限り zero ではなく `unavailable` と表示する。metadata-only observation は `content_state=not_captured` で retention row を作らず、future content は workflow v1 では blocked として既存 `session_event_content` / #89 / #90 以外の保存・pin・delete path を追加しない。
- Historical evidence extraction。already exact-bound な Session、sanitized summary、objective、#59 evidence から bounded かつ coherent な raw-local / repository-safe の対を deterministic に作成・永続化する。#58 の nullable repository/workspace semantics、#59 の opaque token と public validator、現在の retention authorization を再利用し、missing / unknown を zero に変換しない。source discovery/import、LLM interpretation、UI、effect/apply、export publication は行わず、#76 の historical-source import policy を実装したものとは扱わない（D065）。
- Historical instruction analysis。Issue #73 は persisted #72 extraction 一件だけを明示 provider で解析し、provider には canonical bytes から独立再構築した view だけを渡し、post-validation は provider から隔離した owner snapshot だけを authority とする。final #59 finding の exact refs は provider anchor trace を一意に所有する同一 Session に限定し、anchor ref 1 件以上と #59 relation を満たす同 Session の bounded non-anchor context ref だけを許可する。他 Session は recurrence の独立評価にだけ寄与する。同じ #59 category が各 Session 内で独立に category-specific minimum を満たす distinct Session が 2 件以上ある場合だけ `recurring` / eligible とし、1 件は `single_session`、0 件は `insufficient_support` とする。exact/resolved citation は 0/1 件でも frozen #59 の `weak` / `incomplete` finding と exact support projection を保持し、unrelated / under-minimum group は promotion に使わない。model/provider は共有 sensitive-carrier gate、`timeout_ms` は `1..3,600,000` に固定し、absent/checksum-mismatch の `stale_extraction` と owner-invalid な `extraction_invalid`、caller の `canceled` と owner timeout の `timed_out` を区別する。queued/running/全 terminal state を `historical-instruction-analysis.read.v1` として #75 に渡し、success receipt がなくても extraction truncation、sanitized/content、repository-safe completeness/source distribution を保持する。projection は completeness/source-kind total の一致と capability count 上限を再検証し、unavailable (`sanitized_only=false` / `content_available=false`) は empty distribution、provider-stage state は positive Session total に固定する。通常 host は store/composition/read boundary を登録するが provider/credential/raw execution は既定で構成せず、current host が `--sanitized-only` の間は過去の raw extraction が残っていても runner 構成を拒否する。HTTP/UI/proposal/effect/apply は追加しない。
- Historical efficiency analysis。Issue #72 の exact canonical `historical-evidence.repository-safe.v1` bytes だけを入力にし、token volume、context growth、cache inefficiency、retry overhead、duration outlier、opaque model mix を versioned deterministic rule で評価する。frozen #72 v1 が producer-authored repeated-call identity、exact tool-failure status、permission-wait duration、exact subagent ownership を永続化しないため、対応する4カテゴリは理由付き `unavailable` とし、reserved group/capability shape から能力を捏造しない。missing / unknown / zero denominator は complete evaluation に変換しない。quality evidence は強度判定を downgrade できるが、効率観測から quality improvement / effect / monetary cost を主張しない。結果は extraction ID/hash を各 driver にも exact-bind し、exact evidence refs、category coverage、quality availability、fixed mitigation を持つ canonical repository-safe receipt として Issue #75 が formula を再計算せず消費する。HTTP、UI、DB/migration、import、AI narrative、proposal/effect/apply は含めない。
- Alert-rule engine foundation。versioned source-neutral snapshot / capability と compiled rule registry を deterministic に評価し、missing / unknown / unavailable evidence を zero と推測せず bounded suppression にする。accepted match は exact evidence、config/input/evaluation hashes を持つ immutable canonical receipt として component-owned `alert_engine` schema v1 に保存し、sensitive comparable label は private keyed HMAC token にする（D067）。#81 / #82 は rule packs、#83 は lifecycle、#84 は Alert Center reads/UI/aggregation、#85 は sanitized export をそれぞれ所有する separate consumers であり、receipt bytes や engine-owned tables を再定義しない。canonical receipt を別 component が消費する場合は、#80 所有の strict v1 consumer validator を使用し、unknown / duplicate field、非 canonical byte、version/profile/invariant 違反と receipt 内 field から再計算できる `alert_id` の不一致を固定 no-leak error で拒否すること。consumer boundary は 8 MiB と receipt v1 の固定 JSON depth に制限し、返却 projection は alert/session/optional trace/source surface/last-observed identity のみとする。この上限と consumer-only semantic gate は既存 producer/serializer/store admission や bytes を変更せず、超過または semantic-invalid receipt を downstream が truncate してはならない。receipt 単体から再計算できない evaluation/config/input identity は canonical lowercase hash shape のみを検証し、origin/signature/authorization/provenance を主張しない。
- Alert engine production compatibility。source-neutral な production application boundary は caller が既に正規化した一つの snapshot を frozen registry/configuration/evidence resolver で評価し、既存 store へ completed evaluation を append して成功した場合だけ evaluation/config identity、ordered receipt ID、typed suppression、typed rejected match を持つ bounded immutable outcome を返す。initialization、busy、unavailable、conflict、contract rejection を固定状態で区別し、append failure を成功へ変換しない。Alert Center の trusted server-side read は additive query interface から alert ID / evaluation ID / suppression ordinal の固定順、cursor、1..100 limit で取得し、receipt bytes は既存 strict consumer と同じ #80 authority を通過した fully typed sealed Alert Center projection としてのみ渡す。evaluation projection も canonical evaluation 全体の strict 再構築と byte identity を通過し、scalar identity と child row count が一致する場合だけ返す。既存 five-field consumer API は変更せず、#84 は receipt parser を複製しない。query は schema v1 の既存3 tableを読み、migration、任意 SQL、raw/content table read、source provenance 推測を追加しない。
- Alert lifecycle。immutable `alert.receipt.v1` とは別の component-owned append-only event chain から current state / revision を導出し、mutation は optimistic concurrency と idempotency を必須にする。reevaluation、supersession、source deletion は明示された versioned seam だけを使用し、heuristic predecessor / Session-time relationを推測せず、canonical receipt bytes を変更または削除しない。sanitized lifecycle read/mutation routes は `--sanitized-only` でも利用可能で、same-origin、CSRF、bounded audit value、`Cache-Control: no-store` を維持する。#83 は rule logic、Alert Center UI、notification を実装しない（D067）。
- Alert Center。Issue #84 は #80 の immutable canonical receipt / typed bounded query projection と #83 lifecycle のみを正本として、`GET /api/alert-center/v1/alerts` と `/alerts` に severity、state、rule、source、repository/workspace、date、completeness filter、observed/effective threshold、exact evidence、bounded sanitized lifecycle transition history、coverage suppression、recurring group を表示する。repository/workspace query label は #84 sanitized label guard を変更なしで通過した値だけを exact filter/query DTO に反映し、path、email-like PII、Bearer/credential/token marker は固定 invalid-query として拒否する。persisted scope label も同じ guard を通し、一つでも unsafe なら scope 全体を unknown/null として値を DTO に出さない。最大 2,000 receipt の stable alert-ID cursor snapshot を超えた場合は `incomplete` とし unseen 件数を推測せず、coverage も 20 evaluation page / 2,000 evaluation / 100 fact で独立に上限を持ち、上限到達時は omitted unknown の incomplete とする。recurring は exact rule/version + repository/workspace + source/version + UTC observation date が一致する distinct Session 2 件以上だけを supported とする。証拠リンクは kind-aware に exact UUID Session、opaque evidence ID と独立した exact UUID Session event ID、exact trace projection、または persisted row ID と Session/trace/span/time/source observation tuple を検証し、trace/span のみで結合しない。Alert Center の rule/source facet は current page、filter-independent bounded coverage、active value の union を維持し、custom period は両端、順序、inclusive 366-day maximum を client/server で一致して検証する。Overview は active period の bounded DTO から open count、critical/warning breakdown、source breakdown、top supported recurring rule、latest critical を表示し、pagination/acquisition が bounded または incomplete の場合は断定を避ける。filter/page/lifecycle/period/SSE の非同期 refresh は generation を持ち、古い response が新しい URL、lifecycle、period 表示を上書きしない。評価は same-origin + CSRF の `POST /api/alert-center/v1/evaluations` で利用者が canonical UUIDv7 Session と exact trace を明示した時だけ行う。選択 trace の span count と全 persisted monitor span を一致させ、各 `raw_record_id` の #61 source observation が同じ exact surface/application-version partition にあることを検証し、missing/mixed/versionless を拒否する。current manifest では semantic capability を generic projection から昇格せず、applicable rules は `missing_required_capability` として保存する。既定 receiver の `raw-otlp` は application version がないため評価前に拒否し、明示的な exact application version を持つ raw-OTLP partition の non-applicable rules に限って `source_not_applicable` を保存する。ingestion/startup/navigation/GET/browser からの評価、独自 receipt parser/second state store、notification、recommendation は禁止する。`--sanitized-only` でも metadata-only surface は利用でき、D042 の sidebar 2 項目は増やさず Overview/trace/Session から context link する。
- Sanitized evidence export。公開 HTTP / CLI control は `sanitized-export-control.v1` の schema version、exact `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` UTC `created_at` / selection time、selection のみとし、caller snapshot / record bytes / safety marker / output path を受け取らないこと。共有 public parser 自身が materialization 前に 1 MiB 上限と canonical time lexeme を検証する。明示された既存 Local Monitor database から、一つの read-only SQLite connection と deferred transaction で monitor v7、Session v13、optional #59 / #80 schema を anchor し、bounded descriptor と byte-length gate の後に selected exact-ID carrier だけを読むこと。同一 trace が複数の distinct Session に結合された場合だけ unavailable とし、同一 Session / trace の複数 source-surface provenance projection は保持する。`raw_records`、`session_event_content`、raw analysis/content store は query せず、#59 / #80 の公開 consumer validator と exact canonical bytes を再利用する。#58 safe metadata projection、#59 handoff、#80 receipt 以外の carrier を v1 では受理せず、#72 historical dataset と #83 lifecycle event は named future profile なしに追加しない。snapshot ID は selected inventory / exact hashes / capabilities / versions の length-framed canonical identity とする。preview / create は同一 snapshot を一度だけ capture し、scanner / producer / archive failure では partial artifact や success bytes を返さない。`result` は独立して strict archive / checksum / canonical carrier を検証するが provenance / origin attestation を主張しない。import、raw replay、backup/restore、upload、signing は #86 / #87 / #88 または別 issue の責務とする（D068）。
- Raw local replay。Issue #87 は `raw-local-replay` だけを明示 opt-in の local-only raw profile とし、export / replay の両方で persistent warning、exact preview digest、固定同意文を要求する。export は既存 Local Monitor database から exact Session / trace / raw-record / source / half-open UTC range を一つの Retention catalog v1 composite `operation` lease で all-or-none capture し、元 ID・timestamp・source/adapter/schema/content provenance を保持した strict deterministic ZIP を生成する。replay は archive / checksum / inventory / credential guard と pinned normalization/projection/dashboard version を fail closed で検証し、live raw/Session/projection database を変更せず、外部 model を呼ばず、既存 `sensitive_bundle` / `sensitive-bundle-7d` capture journal・lease・cleanup path だけで隔離 namespace を保持する。同じ replay ID と同じ request は idempotent、異なる archive/options/version は conflict とし、heuristic Session merge、caller file cleanup、sanitized bundle への raw carrier 混入、repository-safe output 化を禁止する。`--sanitized-only` は preview/export/import/replay/status/download を raw read/write 前に拒否する（D069）。
- Source schema drift and Claude Code P0 integration。ingest batch ごとに source surface/version、adapter version、schema fingerprint、observed inventory hash、support state を additive storage へ保存し、unknown span/event/attribute は raw body や値を含まない bounded metadata として保持する。検証済み source version は manifest/evidence に明示するが、未検証 version 自体では拒否しない。既知 fingerprint と一致すれば通常処理し、異なる fingerprint は data を捨てず `schema_drift_detected` として degraded にする。`unsupported_source_version` は既知の非互換または必須 signal 不在に限定する。Claude Code の OTel identity/parentage/timing と Hook lifecycle/event identity を authority 境界どおりに取り込み、同一 native session ID、明示 resume/handoff、byte-equivalent trace context 以外で結合しない。Claude の Agent ownership は exact source parentage のみを使用し、欠落時は unresolved とする。interactive CLI、`claude -p`、Agent SDK の live validation が実行不能な場合は具体的 blocker と独立 follow-up task を残し、fixture-backed implementation、security boundary、Copilot regression、build/full tests の完了を妨げない。
- Configuration ownership setup。Issue #66 では agent-specific guided setup が共用する versioned user-scoped ownership ledger と `setup plan` / `setup apply` / `setup rollback` / `setup status` を提供する。plan は値と path を redacted にし、apply 前の base SHA-256、managed conflict、restart requirement、rollback availability を示す。apply は全 target の stale preflight、backup、same-directory temporary file、atomic replace、Windows current-user environment API、file/environment-member ごとの write-ahead intent、reverse-order compensation を使い、partial failure と rollback outcome を ledger に残す。rollback も restore 前 intent を使い、current hash が applied hash と一致するときだけ change-set 単位で許可する。全 command は通常処理前に interrupted transaction を回復し、requested/created change-set と recovered change-set を別フィールドで返す。private plan の `desired_state` は schema v1 の closed union とする。inline string は historical bytes と generic non-tagged file/TOML/opaque target の canonical v1 arm、tagged owned-values + expected-hash object は `SetupTargetKind.Json`、adapter `github-copilot`、label `vscode-stable-default-user-settings` または `vscode-insiders-default-user-settings` の VS Code JSONC record 専用であり、これは migration/fallback ではなく同じ v1 contract である。既存 committed ownership-ledger v1 fixture は未変更の restart evidence として維持し、serializer 変更前に legacy string `desired_state` を含む separate private-plan v1 fixture を production `SetupPlanStore` から capture して write-close-reopen byte identity を証明する。tagged string value は 1..2048 UTF-16 units とする。VS Code は Plan 時も bounded memory で complete JSONC bytes を operation/hash 計算だけに使って直ちに discard し、apply lock 下の revalidation materialization も永続化しない。ledger/journal には hash だけを保存し、recovery は materialize を再実行せず expected hash と backup だけで判定する。永続 plan の adapter が apply 時に未登録なら `unsupported_adapter`、macOS/Linux の Copilot CLI plan を apply する場合は `unsupported_target` とし、どちらも target write、backup、journal、ledger lifecycle transition を行わない。force rollback、machine-wide environment、`setx`、shell profile mutation、DB/log/runtime data deletion、symlink/junction/reparse/path traversal を含めない。ledger/log/repository-safe output は raw path、setting value、credential、token、authorization header、raw exception を含めない。
- Configuration status projection。unshipped ownership ledger v1 は status 用の plan-time repository-safe target projection（当時の `expected_result` を含む）を immutable snapshot として保持し、`setup status` は current/reference/rollback facts を private runtime artifact と現在の target state から毎回再検証する。全 member が `no-op` の physical target は ownership/backup quorum には参加しないが、rollback の change-set-wide fresh preflight guard には残り、その base state が変化していれば rollback unavailable とする。履歴 snapshot の `expected_result` は strict v1 schema/safety/cross-field contract で検証し、現在の embedded manifest との一致は要求しない。新規 plan は引き続き現在の canonical manifest と exact match しなければならない。ledger 全体の既存 1 MiB cap は維持し、snapshot 用の第2 cap や自動 pruning は追加しない。
- GitHub Copilot guided setup。Issue #67 では Issue #66 framework の `github-copilot` adapter として、VS Code GitHub Copilot Chat、terminal GitHub Copilot CLI、caller-managed GitHub Copilot App/SDK の detect/plan/apply/rollback/status を提供する。既定 endpoint は loopback Local Monitor とし、VS Code Stable / Insiders はそれぞれ Default Profile の documented user settings だけを変更する。VS Code `settings.json` は plan/revalidate とも 1 MiB + sentinel で読み、malformed/oversize は `malformed_settings` で fail closed とする。VS Code の新規 plan は complete JSONC document を永続化せず tagged v1 owned-values representation のみを保存し、apply revalidation が lock 下で生成した bytes と expected hash を検証する。supported minimum を満たしていても persisted version と異なる version drift は `recovery_required` とする。non-default profile は常に read-only で、存在時は固定 warning `vscode_non_default_profiles_not_modified` を返す。Copilot CLI は read-only current-process environment と別の Windows current-user environment を表示し、後者だけを Windows で変更する。macOS/Linux は detect/plan のみで shell profile を変更しない。App/SDK は sample contract を返すだけで caller-owned file を変更しない。managed channel は native > server > file の優先順位で、最上位の存在する channel 全体を採用し channel 間で merge しない。その結果を per-setting の managed policy > environment > user setting > default に適用する。managed source は read-only、外部 CLI から観測不能な server-managed policy は `managed_policy_unverified` とし、Copilot CLI は env-only detection のため常に同 warning を返して effective managed state を主張しない。content capture は既存値を保持し、独立した明示 option と warning がある場合だけ有効化する。`client.kind`、`OTEL_SERVICE_NAME`、`OTEL_RESOURCE_ATTRIBUTES`、`OTEL_EXPORTER_OTLP_HEADERS`、`COPILOT_OTEL_SOURCE_NAME`、credential、既存 resource attributes は global user environment へ追加・変更しない。Local Monitor endpoint は bounded `GET /health/live` probe で厳密に識別し、refused / no listener は `monitor_not_running`、connect/read/total timeout、redirect、non-200、oversize、malformed、または別 JSON は `port_owned_by_foreign_process` とする。setup success は static configuration verification までで、first trace 到着は Issue #103 の明示的で bounded な Doctor handoff の責務とする。Issue #103 は exact target / Doctor database / expiry と任意の exact verification revision・raw record・native Session selection だけを使い、setup success、synthetic probe、latest trace/Session、repository/workspace/cwd/process、または時刻近接から `first_trace_ready` を推測しない。
- Claude Code guided setup。Issue #68 では同じ Issue #66 framework に `claude-code` adapter を追加し、`setup plan --adapter claude-code --target <cli|app-sdk|all>` を提供する。`cli` は interactive CLI と `claude -p` が共有する user-level `~/.claude/settings.json` の `env` と mapper 対応済み全 Hook を ownership-aware に管理し、`app-sdk` は Python / TypeScript の caller-managed guidance のみで書込み・rollback ownership を持たない。Claude Code 2.1.207 以上の通常版を strict SemVer で受け入れ、older / prerelease / malformed は `unsupported_version` とする。Windows native は apply/rollback 対象、WSL2 は Linux process、`WSL_DISTRO_NAME`、Microsoft kernel marker の3条件を満たす場合だけ `--allow-wsl2-routing` の明示 opt-in と WSL 内からの loopback `GET /health/ready` 成功を要求し、gateway、non-loopback bind、Host-header 緩和、NAT fallback は追加しない。Windows native または他 adapter で同 option を使うと `invalid_arguments` とする。macOS/Linux native の installer は対象外である。default plan は OTel content gate の既存値を変更せず、`--include-content-capture` のときだけ `OTEL_LOG_USER_PROMPTS`、`OTEL_LOG_TOOL_DETAILS`、`OTEL_LOG_TOOL_CONTENT` を `1` にする。全既定 Hook は raw-bearing event を取得し得るため、content gate とは別に固定 warning `claude_hooks_capture_raw_content` を返す。setup success は static configuration verification までで、first real trace / Doctor state は Issue #104 の責務とする。
- Configuration setup platform closure。private setup runtime root は Windows の `%LOCALAPPDATA%`、macOS の `$HOME/Library/Application Support`、Linux の absolute `XDG_DATA_HOME`（未設定・空・非 absolute は `$HOME/.local/share`）配下に同じ `CopilotAgentObservability/LocalMonitor/setup` layout で置き、macOS/Linux でも plan を永続化してから apply を `unsupported_target` にできること。Copilot managed-settings の native > server > file は `GitHubCopilot` registry / `com.github.copilot` preferences / server / well-known file だけに適用し、`Software\Policies\Microsoft\VSCode`、macOS configuration profile、Linux `/etc/vscode/policy.json` の VS Code enterprise policy は独立に評価すること。どちらの観測済み read-only system でも desired telemetry と異なる値は `managed_policy_conflict`、同じ値は managed no-write とし、VS Code enterprise policy の存在によって Copilot server/file を抑止または検証済みとみなさないこと。Copilot CLI の既存 `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` は detect-only とし、`http/protobuf` なら固定 warning を返して保持し、その他は `environment_override_conflict` で plan を作らず、write allowlist に追加しないこと。endpoint probe は connect/read/total timeout をすべて foreign owner とし、4096 payload bytes と sentinel 1 byte（または trustworthy `Content-Length`）で oversize を判定すること。
- First-trace Doctor core。Issue #102 では direct / Config CLI / Local Monitor HTTP が共有する source-independent `DoctorResult`（`doctor.v1`）を提供し、12 個の explicit-known/unknown fact families から 20 個の固定 state を純粋・決定的に評価すること。全 state の severity / retryability / next action / reason code（v1 では state code と同一）と blocking precedence、terminal (`ready_no_real_trace` / `first_trace_ready`)、advisory ordering を固定し、blocker がある場合は blocker だけ、ない場合は terminal の後に advisory を返し、unknown を false / zero / success に変換しないこと。partial は `success=false`、non-null evaluation、null primary state、empty states、nonempty ordered missing families に固定すること。direct evaluation は source-neutral typed `DoctorObservation` に real/synthetic class と fixed evidence kind を含めること。real-source verification は server-generated UUIDv7 と 1..30 分の明示 window、期待 source/adapter、revision、最大 100 candidate / 16 accepted opaque references を用い、complete caller は参照だけを選択し、store/service が persisted candidate を trusted observation に解決するため、caller が evidence class/kind/source を偽装できないこと。latest trace、repository/workspace/cwd、trace ID 単独、timestamp proximity では candidate を選ばないこと。synthetic probe は receiver/persistence/projection health のみを証明し、real-source receipt や exact Session binding を満たさないこと。CLI は evaluate/start/status/complete/cancel の5 command、Local Monitor は対応する5つの `/api/doctor` route を同一 result で提供し、strict 64 KiB input、fixed exit/HTTP mapping、loopback/Host/same-origin/CSRF/no-store/sanitized-output 境界を維持すること。Doctor v1 SQLite lifecycle/candidate tables は monitor/session component version と分離し、start/complete/cancel の compare-and-swap、evidence acceptance、migration を transactional/restart-safe にすること。Doctor store failure は verification route/command だけを `doctor_store_busy` / `doctor_store_unavailable` に degrade し、Local Monitor startup、ingestion、stateless evaluation、D051 readiness contract を変更しないこと。D059 を維持し、exact verification と無関係な schema drift 単独では `first_trace_ready` を失敗させないこと。詳細は [first-trace Doctor interface](specifications/interfaces/first-trace-doctor.md) と D060 を正本とする。
- GitHub Copilot first-trace slice。Issue #103 では #66/#67 の static setup result を #102 の frozen twelve-family Doctor input へ source-specific に mapping し、VS Code Copilot Chat、Copilot CLI、caller-managed App/SDK の verification をそれぞれ `github-copilot-vscode`、`github-copilot-cli`、`github-copilot-app-sdk` と canonical adapter `github-copilot-doctor` で扱うこと。adapter は underlying raw/Session provenance と explicit raw/native selection または安全に伝播した verification ID を exact に検証してから既存 `ObserveCandidate` boundary を使い、setup success/no-op、synthetic probe、capability declaration、latest trace、repository/workspace/cwd/process/time proximity を real-source evidence にしない。accepted ingest、raw persistence、projection disposition、exact Session binding、completeness/content は独立 gate とし、selected raw row に successful projection row がないだけでは `not_started`、`pending`、`failed` のいずれにもせず unknown を維持する。Managed VS Code/CLI は completed projection と exact raw provenance がある場合に binding を `not_required/not_applicable`、completeness を既知の unbound とし、raw-derived `completeness_content` candidate を使用できるが、App/SDK は exact Session binding を必須とする。選択された opaque reference は persisted source-diagnostic target から単一 raw row へ exact に解決できる場合だけ runtime facts に昇格し、複数 raw row の reference を混在させない。#102 Doctor contract と #105 common proxy/UI/Release closeout は変更しない。
- Cross-surface first-trace closeout。Issue #105 では `github-copilot-vscode`、`github-copilot-cli`、`github-copilot-app-sdk`、`claude-code` を一つの固定 source registry として Config CLI、Release ZIP、Local Monitor の `/diagnostics` Doctor section から選択できること。CLI JSON、human summary、UI proxy は同じ persisted `FirstTraceEnvelope` と埋め込まれた未変更の `doctor.v1` result を投影し、UI は source 固有 state、優先順位、missing-fact fallback、latest/timestamp heuristic を追加しないこと。UI proxy は additive な `/api/doctor/ui/v1` route 群とし、既存5つの `/api/doctor` routeを変更しない。Doctor result の opaque evidence reference から exact trace、local Session、source diagnostic observation への navigation は、server が検証済み target kind、opaque ID、固定 same-origin relative href を別の sanitized projection として返す場合だけ許可する。Local Monitor は D042 の2項目ナビを維持し、Issue #105 自体は新規画面を追加せず、Doctor、exact Session summary、source diagnostic target を `/diagnostics` 内へ表示すること。Issue #79 の別 `/historical-import` 画面はこの Doctor closeout に含めない。Windows x64 Release ZIP は source build、手動環境変数編集、.NET SDK/Runtimeを要求せず、同じ first-trace command surface と rollback/uninstall 後の整合した Doctor state を提供すること。
- Session ingest / workspace。installed Local Monitor は `POST /api/session-ingest/v1/events` と sanitized `/api/session-workspace` reads、および raw-bearing `GET /sessions/{id}/events/{eventId}/content` を提供する。raw event content は secret-filter 後に metadata と分離して保存し、`expires_at = captured_at + 90 days` を付与する。Retention catalog v1 が item-level の read denial と physical cleanup を所有し、expiry 後の read は凍結された `410` / `expired_pending_deletion` を維持する。pin / delete-now は Issue #90 に残す。installed `hook-forward --endpoint <loopback-url> --timeout-ms 250 [--source claude-code [--source-version <metadata-token>] [--schema-fingerprint <64-lowercase-hex>]]` は stdin JSON 1件を読み、invalid/network/timeout でも exit 0、stdout/stderr 無出力、Hook decision 非影響とする。`--source` 省略は既存 Copilot Hook mode であり追加引数なしの互換性を維持する。Claude mode は exact `--source claude-code` と、信頼できる source version または承認済み Hook schema fingerprint の少なくとも一方を out-of-band 引数で要する。provenance 引数は Claude mode でのみ有効とし、Claude invocation の selector/provenance 欠落または不正時は値や source を payload shape から発明せず転送しない。Canvas / App SDK は `ctx.sessionId` を native session ID として使い、最初の Canvas open から capture する。missed earlier events は復元せず completeness を下げる。persisted event は保存し、ephemeral usage は集計のみ、reasoning / delta は保存しない。
- Canvas Improvement proposals。Canvas Improve は、exact-bound かつ terminal Session の evidence を利用者が確認して作る local-runtime proposal lifecycle を提供する。詳細分析は既存 `session.send()` + bounded Canvas actions の dispatch を維持し、Local Monitor raw analysis runner に置換しない。proposal は `candidate`、`recommended`、`verified` の固定 lifecycle とし、Candidate は citeable evidence、Recommended は少なくとも2つの distinct exact-bound Session の evidence と利用者による明示 promotion を要する。Verified は Issue #56 の comparison verdict に限定する。proposal は target kind/opaque label、sanitized rationale、expected effect、risk、opaque evidence references を local runtime に保存してよいが、raw prompt / response、tool args/results、PII、credential、token、local sensitive path、source fragment を保存・action/log/prompt/repository-safe outputへ送出してはならない。proposal creation / promotion は loopback、same-origin、CSRF を要する明示的なユーザー操作であり、自動生成・自動 promotion・file/config/Skill/Agent/Instruction の自動変更を行わない。direct apply、diff、snapshot、rollback、git操作は Issue #55 のみが扱う。
- Canvas proposal apply。Issue #55 は、利用者が明示的に承認した proposal だけを、起動時に明示登録した local user-config / local Skill / repository working-tree root 内の既存 regular file に適用できるようにする。Canvas は token-gated local helper で full diff と選択 hunk を確認するだけであり、Local Monitor の適用エンジンが relative path、root kind、base hash、selection digest を検証して書き込む。全 target が stale でないことを確認できない場合は一切書き込まない。各適用は fsync 済み snapshot と recovery journal を先に作成し、成功時は全変更、失敗・中断時は全 snapshot へ回復する。rollback は current hash が適用直後の hash と一致するときだけ一度許可する。approval、apply、rollback は loopback、same-origin、CSRF、no-store の明示操作であり、audit は proposal / source Session / actor / outcome の sanitized metadata のみを残す。raw prompt / response、source/diff 本文、absolute path、credential、token は Canvas action、log、repository-safe output、committed artifact へ出してはならない。git branch / commit / push / PR 操作、任意 path、directory / delete / rename、symlink / junction / reparse point、automatic apply は含めない。
- Canvas effect comparison。Issue #56 は、exact Session / Run / trace と immutable objective evaluation receipt、Issue #54 proposal revision、active Issue #55 application receipt に基づき、利用者が明示確定した pre/post cohort の効果を quality-first で比較する。objective receipt は pass/fail、normal/severe、evaluator ID/version、criterion、case key、exact evidence refs を持ち、repository/timestamp proximity や normalized measurement の unlinked `success_status` を evidence に昇格させない。included Session は exact-bound・terminal・full かつ human または objective quality evidence を持ち、pre/post 各3件未満、missing/partial/conflicting evidence、rollback/stale application は `insufficient_evidence` とする。quality pass rate と severe regression を efficiency より先に判定し、quality 同等時だけ duration / total-token median の10%境界を使う。verdict は `improved` / `no_change` / `regressed` / `insufficient_evidence` の固定4値で単一総合 score を作らない。`improved` の effect receipt 保存と proposal `verified` 更新は同一 transaction とし、rollback 後の receipt は履歴として保持するが active improvement として表示しない。cohort confirmation / comparison writes は loopback、same-origin、CSRF、no-store の明示操作であり、raw content、path/source/diff、automatic Verified、git 操作を含めない。

Update (D039 / D042 / D050): Sprint18 の Local Monitor overview / trace-list と Canvas helper は、同じ raw-bearing prompt-label route を same-origin / token-gated local screen で `fetch` し、prompt label を inert text として表示してよい。これは上記の「server-rendered」由来のプロンプト識別要件を現在の client-side UI に適用したもので、full raw payload を client-side fetch する許可ではない。`/api/monitor/*` と SSE は引き続き prompt-free。
- Langfuse による個別 trace viewer。ただし Langfuse は standard full profile の viewer であり、raw-only minimum profile の必須要素ではない。
- saved raw OTLP JSON の file-based ingest。
- SQLite raw store。
- raw store から normalized measurement dataset への変換。
- deterministic CLI による diagnosis / improvement / auto-decision candidate generation。
- static HTML dashboard と dashboard dataset generation。

任意機能:

- Codex App / app-server の OTel trace / logs / metrics 収集。
- GitHub Copilot app Canvas adapter。Local Ingestion Monitor の既存 monitor
  context を Copilot app side panel から参照する任意統合として扱う。Canvas
  adapter は Local Ingestion Monitor の既存 API / view model / projection を
  再利用した診断 surface であり、Canvas extension 内に Local Monitor UI を
  再実装しない。Canvas adapter は raw default の Local Monitor を扱ってよいが、
  Canvas actions / logs / committed outputs / static artifacts へ raw prompt /
  response body、tool arguments / results、PII、credential、token、local sensitive
  path、raw OTLP payload を返してはならない。Sprint11 M5 では拡張所有の loopback
  ヘルパーページ上に「Analyze selected trace with Copilot」UI トリガーを任意提供し、
  トリガー指示は選択した trace id・optional span id・focus・action 名のみを含み、
  monitor payload や raw / PII を埋め込まない（D029）。Sprint15 では、拡張所有
  ヘルパーページが (a) status / primary model / span 数 / tool 数 / token / duration /
  time / 短縮 trace id を含む「判断できる」trace 一覧、(b) 日本語の focus / ボタン /
  見出し（focus の enum 値 `latency` / `tokens` / `cache` / `errors` と action 名は
  不変）、(c) `ready` / `not_ready` / `unreachable` を区別し確認 URL・起動コマンド・
  設定確認・参照 monitor base URL など次操作を具体化した health / error 導線、
  (d) health 生レスポンスの既定折りたたみ、を提供する。これは表示境界を変えず、
  Canvas action response を bounded DTO のまま維持する（D036）。Local Monitor 側に
  sanitized 集計 endpoint `GET /api/monitor/summary`（既存 projection の allowlist
  範囲内、`limit` 既定 50、cursor pagination なし）を追加し、Razor ダッシュボードと
  Canvas で共用する（D037）。Canvas ヘルパーページには選択したトレースの要約カード
  （状態・主要モデル・トークン合計・所要時間・cache hit rate、bounded DTO のみ、
  span tree / cache 明細は含まない）を追加する（D037）。Canvas ヘルパーページの
  「Local Monitor 概要」カードは、新規拡張所有ルート `GET /api/summary`
  経由で `GET /api/monitor/summary` を bounded にプロキシし、per-model /
  per-client-kind 集計と latest / top-token / error トレースを表示する。
  概要カードの highlight trace は、`/api/traces` と同じく拡張所有・token
  認証・loopback helper surface に限って prompt label を併記してよい。
  これは利用者自身のローカル Canvas 画面表示であり、`/api/monitor/*`、
  Canvas action response、`session.send()` prompt、logs、repository-safe
  outputs、static artifacts へ prompt label / raw prompt を流さない
  （D038 / D039 / D050）。Canvas raw preview は、既存の raw-bearing route
  `GET /traces/{rawRecordId}/raw`（固定フォーマットの HTML エンコード済み
  `<pre>`）から server-to-server で取得し再デコードせずそのまま埋め込む方式で
  実装する。新規ページ遷移ルート `GET /raw-preview/:traceId/:spanId`
  （拡張所有・token 認証・`Cache-Control: no-store`）として提供し、
  クライアント側 JS は raw を JSON として受け取らない（D038）。D050 では、
  同じ拡張所有・token 認証・loopback helper surface に限り、選択 trace の
  prompt / response preview を `GET /traces/{traceId}/spans/{spanId}/detail`
  から server-to-server 取得して画面表示してよい。これは利用者自身のローカル
  Canvas 画面表示であり、Canvas action response、`session.send()` prompt、
  logs、repository-safe outputs、static artifacts へ raw prompt / response を
  流さない境界は変更しない。D037 時点で見送った OTel 単独の
  session-to-trace correlation は、Issue #51 の明示 Session event input と
  exact-link evidence を使う別 Session subsystem に限って supersede する。
  repository / timestamp proximity による推定相関は引き続き禁止する。実装（コード作成・自動テスト検証）は
  Claude が行い、GitHub Copilot Canvas runtime ツール
  （`extensions_manage`/`open_canvas`/`invoke_canvas_action`）を要する
  ライブ検証のみ、実装完了後の別工程として GitHub Copilot へ委譲する（D038）。
  Sprint16 では cross-repo 利用のため、`.github/extensions/otel-monitor-canvas/`
  を唯一の copyable extension distribution unit とし、既存 OTLP Resource
  Attributes `vcs.repository.name` / `workspace.name` / `repo.snapshot` から sanitized
  `repository_name` / `workspace_label` / `repo_snapshot` を Local Monitor
  projection と Canvas helper / bounded action DTO に限って表示できる（D040）。
  `vcs.repository.name` が absent の場合だけ canonical GitHub HTTPS
  `vcs.repository.url.full` の sanitized repository segment を fallback にできる。
  `/diagnostics` は Retention-gated な bounded key/count/scope/classification と
  fixed 5-state reason のみを表示し、attribute value、URL、owner、identity、PII、
  credential、path を表示・永続化しない。既存 API / SSE / Canvas DTO shape は変えない。
  既存 projected rows は自動 backfill せず、metadata 欠落時の Canvas helper 表示は
  `unknown repository` とする。mirror folder、package manifest、dependency、
  current repo auto-match、raw / PII / path / token の Canvas action / log /
  repository-safe output 送出は追加しない。Sprint17 では既存の
  `session.send()` + bounded Canvas actions 分析トリガーを維持したまま、
  Canvas helper で requested analysis profile / requested model /
  requested reasoning effort / timeout hint を選べるようにする。これらは
  per-message execution control ではなく Copilot への指示・表示・dispatch
  metadata であり、UI は実行モデル / reasoning / timeout が強制されたとは
  表示しない。Local Monitor は sanitized `GET /api/analysis/options` で
  configured model/profile metadata を提供してよいが、Canvas helper の
  `/analyze` は Local Monitor raw analysis runner を起動しない。
- Canvas Session workspace の Evidence tab は、選択 Session の run に
  byte-for-byte で記録された non-null `trace_id` だけを run 順で合成し、
  Issue #49 Agent graph と sanitized spans 全ページを表示できること。Agent
  ownership / hierarchy / parallel / relationship は Issue #49 API を唯一の
  情報源とし、Session event は常に unowned とする。exact trace がなくても
  Session event timeline は利用でき、欠落・エラー・推定・判定不能を推測で
  補完しない。Evidence は `--sanitized-only` でも利用でき、raw content を
  取得・復元・action/log/output へ送出しない。
- Grafana JSON dashboard fallback。

参考のみ:

- Claude Code の observability 事例。
- Visual Studio 系 client。
- GitHub / Notion / issue / PR 等の external outcome linkage。

## 4. 非目的

本製品では以下を扱わない。

- Copilot の利用者数、利用回数、日次アクティブユーザーの集計。
- 個人別の生産性評価、勤務監視、ランキング。
- 経営向け利用状況 dashboard、課金、コスト配賦。
- DLP、機密情報検査、監査ログ基盤。
- VS Code 内部ログ、workspaceStorage、chatSessions を入力ソースにした解析、および VS Code の in-editor Debug UI の複製。ただし受信済み OTel テレメトリから導出する sanitized agent-execution view は許可する（D021）。
- Langfuse / Collector / Grafana の共有運用決定。
- remote managed Langfuse / Collector の利用者同意 workflow。
- trace から repository patch / diff を生成すること。
- repository file の自動修正（Issue #55 の明示承認済み・root 制限・stale guard・snapshot/rollback 付き local apply は除く）。
- commit / push / pull request の自動作成。
- 改善効果の自動合否判定。
- GitHub / Notion / HR system との本番 ETL。
- Local Ingestion Monitor への Digital Agency Design System（DADS）適用（D027。Monitor は VS Code 慣習に従う開発者向けツール。Static Dashboard は対象外）。
- Cache Explorer での raw prompt body の prefix-diff、および `conversation_id` による cross-trace stitching（D026。前者は raw-bearing route を増やすため、後者は API 変更を要するため）。
- GitHub Copilot app Canvas adapter で Local Monitor UI を再実装すること、Issue #51/#53/#54 で明示した Session workspace / Evidence / Improvement Proposal interfaces 以外の telemetry input / raw endpoint / schema / API field を追加すること、raw prompt / response body、tool arguments / results、PII、credential、token、local sensitive path を Copilot actions へ返すこと。Issue #53 で Canvas 独自の Agent ownership、Session-event-to-Agent ownership、test/review/Skill facts を推測すること。direct apply、Compare、Issue #89 の physical cleanup、Issue #90 の pin / unpin / delete-now mutation/action/proxy は含めない。Issue #90 の Session-primary 導線に限り、選択済み exact local Session ID だけを Local Monitor の retention page へ渡す navigation-only link を許可する。この link は Canvas action、retention fetch/proxy、`session.send()` content、helper token、raw/PII を追加しない。

## 5. Data Requirements

収集対象:

- trace / span / span attributes / span events。
- metrics / events。
- prompt content。
- response content。
- system prompt。
- tool schema。
- tool arguments。
- tool results。
- token usage。
- model information。
- duration。
- error information。
- session id / run id。
- event id。Local Session / Run / Event ID は UUIDv7 string とし、native source ID とは分離する。
- user id / user email。
- team id / department。
- client kind。
- experiment id / experiment condition。

Span 名は client 実装や version により変化し得るため、特定 span 名だけには依存しない。
正規化後は、agent invocation、LLM call、tool call、permission / approval、file operation、shell command、error、user interaction などの論理カテゴリで扱う。

## 6. Expected Resource Attributes

Expected collection metadata（収集期待 Resource Attributes）:

```text
user.id
user.email
team.id
department
client.kind
experiment.id
```

Repository-safe automatic missing-attribute validation is narrower than this collection metadata list. It checks only `client.kind` and `experiment.id`.

**2層モデル:**

- **収集レイヤー**: 上記 6 属性は、live telemetry 設定時の expected collection metadata として維持する。telemetry source はこれらの属性を Resource に設定することが期待される。
- **repository-safe 検証レイヤー**: 自動欠落検証（dashboard dataset の `missing-required-attribute` health row）は `client.kind` と `experiment.id` のみを対象とする。`user.id` / `user.email` / `team.id` / `department` は取得できる場合に保持してよいが、これらが欠落しても `missing-required-attribute` health row は生成しない。これら PII / 組織属性の収集健全性は、raw / PII を含む local monitor 側（loopback 既定表示）でのみ観察する。`team.id` / `department` は PII ではないが、repository-safe dataset では未知 resource 属性として保持され、必須検証には含めない。

`trace_id` は Resource Attribute ではなく **source trace reference** である。参照整合性のため、欠落時は collection health row（`missing-required-attribute`）を出力してよいが、Resource Attribute の必須検証とは別枠で扱う。

`client.kind` の推奨値:

```text
vscode-copilot-chat
copilot-cli
codex-app
```

推奨 Resource Attributes:

```text
vcs.repository.name
workspace.name
task.id
task.category
task.run_index
experiment.condition
prompt.version
repo.snapshot
agent.variant
skill.version
mcp.profile
cli.wrapper.version
```

## 7. Collection Profile Requirements

Collection profile は telemetry routing mode を表す public interface とする。

Profile selector:

```text
CAO_COLLECTION_PROFILE
```

必須 profile:

| Profile | 要件 |
| --- | --- |
| `raw-only` | 最小必須 profile。保存済み raw OTLP JSON を入力にし、Langfuse / Docker / Collector / remote endpoint / background process なしで raw data loop を実行する。 |
| `docker-desktop-langfuse` | 標準 full profile。Docker Desktop 上の local Langfuse へ OTLP HTTP で送信し、live trace review と raw data loop を接続する。 |
| `docker-desktop-collector-langfuse` | Docker Desktop 上の Collector へ送信し、Collector から Langfuse へ relay する。 |
| `wsl2-docker-langfuse` | WSL2 上の Docker Engine で動く Langfuse へ Windows client から送信する。 |
| `wsl2-docker-collector-langfuse` | WSL2 上の Docker Engine で動く Collector へ Windows client から送信し、Collector から Langfuse へ relay する。 |
| `remote-managed-langfuse` | 管理された remote Langfuse endpoint へ送信する。 |
| `remote-managed-collector` | 管理された remote Collector endpoint へ送信する。 |
| `raw-local-receiver` | この repository が提供する local receiver へ VS Code から直接 telemetry を送信し、raw data loop に接続する。 |

Profile 差分は collection / routing / live viewer availability の違いとして扱う。
Profile により raw store schema、normalized measurement schema、candidate schema、dashboard dataset schema を分岐させてはならない。

`remote-managed-langfuse` と `remote-managed-collector` は、本 repository では WARNING と placeholder configuration までを扱う。
remote managed endpoint へ送信する前に、access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を別 decision として決める。

## 8. Data Safety Requirements

Repository に保存してよいもの:

- synthetic fixture。
- redacted summary。
- normalized aggregate dataset。
- sanitized dashboard dataset。
- trace id / candidate id / evidence ref 等の参照 ID。
- 実データ由来の aggregate metrics。
- `user.id` / `user.email` を含む分類属性。ただし共有・公開前に access control を確認すること。

Repository に保存してはならないもの:

- raw prompt / raw response。
- system prompt の全文。
- tool arguments / tool results の全文。
- observed session 由来の source code fragment / file contents。
- credential、secret、token、API key、password。
- Base64 authorization header。
- sensitive bundle content。
- sensitive bundle local path。

Local Ingestion Monitor の raw / PII 表示は loopback-only の runtime surface であり、ここで定義する repository 保存禁止や §9 の static dashboard 非表示とは別物である。raw body（tool call arguments / results、sub-agent instructions / responses、system prompt）と PII（`user.id` / `user.email`）は **既定で表示する**（server-rendered、inert text）。`--sanitized-only` フラグで metadata-only モードを復元し、TraceDetail の raw section と full raw route を除外して PII を除外できる（D023 / D030）。`--sanitized-only` でも TraceDetail の sanitized tab shell は表示される。既定・`--sanitized-only` いずれの場合も raw / PII を repository-safe outputs、static dashboard、ログ、CI artifact へ出力してはならない。`/api/monitor/*` と SSE は常に sanitized metadata のみを返す。この表示は単一のローカル利用者が自分のデータを閲覧する用途に限り、cross-machine な露出（remote / non-loopback、browser 経由の off-machine 送出）から防御する。

Windows Task Scheduler startup surface は Local Monitor を user logon 時に起動するだけであり、client routing 設定を書き換えない。既定 URL は `http://127.0.0.1:4320`、既定 DB / logs / state は `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` 配下とする。Windows では別の明示操作として current user の永続環境変数（HKCU user environment）に raw-local-receiver / monitor 向け OTLP routing を設定・解除できる。これは Windows ユーザーで新規起動される VS Code GitHub Copilot Chat、GitHub Copilot CLI、その他同ユーザー process に継承される既定値であり、既存 process には再起動まで反映されない。永続化は user scope のみ、管理者権限不要、`setx` ではなく user environment API を使い、変更通知を送る。グローバル user environment では client 種別を一意に決められないため `client.kind` は設定しない。Task Scheduler 経由でも loopback-only、Host header validation、same-origin、`Cache-Control: no-store`、raw / PII 非ログ出力、repository-safe outputs への raw / PII 非送出を維持する。`--sanitized-only` は常時起動時にも指定できる任意 opt-out として残す。

LocalMonitor Release ZIP は published app と操作スクリプトのみを配布媒体に含める。raw store / runtime DB / logs / state は利用者端末の local runtime artifact として扱い、Release ZIP、GitHub Actions logs、Release metadata、repository artifact、Issue、static dashboard、CI artifact に raw / PII / credentials / full tool arguments / tool results を含めてはならない。uninstall 時、DB / logs は既定で保持し、明示指定された場合のみ削除する。

GitHub Copilot app Canvas adapter は Local Monitor の任意表示統合であり、raw default の Local Monitor を扱ってよい。ただし Canvas action responses、logs、committed outputs、repository-safe artifacts、static dashboard、CI artifact には raw prompt / response body、tool arguments / results、PII、credential、token、local sensitive path、raw OTLP payload を含めてはならない。`--sanitized-only` は Canvas 専用要件ではなく、必要に応じて利用者が選ぶ metadata-only opt-out として残す。Issue #51 の bounded exception として、Session event ingest、Session storage schema、sanitized workspace reads、same-origin/no-store raw content read、exact OTel session binding を追加してよい。この exception は Canvas actions へ raw を返す許可ではなく、Issue #45 の `session.send()` behavior と Issue #49 の Agent ownership semantics を変更しない。Sprint16 の bounded exception として、resource-scoped `vcs.repository.name` を authoritative source とする sanitized `repository_name`、`workspace.name` 由来の `workspace_label`、`repo.snapshot` 由来の `repo_snapshot` を `/api/monitor/*`、Canvas helper routes、bounded Canvas action DTO に表示してよい（D040）。authoritative name が absent の場合だけ canonical GitHub HTTPS `vcs.repository.url.full` の sanitized repository segment を fallback にできるが、raw URL / owner は保存・送出しない。`repo.name` は repository label source として扱わない。Sprint17 の helper analysis controls は requested values であり、`session.send()` に per-message model / reasoning / execution-timeout enforcement がない限り effective model / reasoning を主張しない。

Session raw content を含む product-owned raw-bearing item は Retention catalog v1 に登録し、capture、authoritative expiry、irreversible read denial、durable cleanup、retry、restart recovery、physical removal を item ごとに扱う。既定 policy は `raw-default-90d` v1（capture timestamp + 90 日）であり、expired content は既存どおり `410` / `expired_pending_deletion` を返す。Session v1 の wire enum、property、status、response bytes は拡張しない。catalog item の lifecycle、catalog-gated read、physical cleanup は [Raw Store And Normalization Specification](specifications/layers/raw-store-normalization.md) を正本とする。pin / unpin / delete-now は Issue #90 の責務である。`--sanitized-only` では Session raw-content route は `404` とする。sanitized workspace reads、Canvas action responses、logs、repository-safe outputs、static artifacts へ raw event payload / content を流してはならない。

Retention catalog v1 の SQLite-owned item は、capture 時の random private 32-byte source token と exact owner fields から作る internal ownership receipt を持つ。receipt は `copilot-agent-observability/retention-owner-receipt/v1` の length-framed binary SHA-256 であり、decoded lowercase store instance ID、closed store kind、canonical source identity、authoritative timestamp text と UTC ticks、store binding、token を結合なしで bind する。receipt/token、raw value、path、credential は public DTO、log、repository-safe output に出さない。この primitive は schema/catalog/store/writer を変更しない。

Local Monitor Copilot raw analysis は Canvas adapter とは別の local raw-analysis surface であり、Local Monitor process 内の .NET GitHub Copilot SDK analysis service には raw trace / raw record / raw span context を渡してよい。禁止するのは raw を repository、Issue、PR、static dashboard、CI artifact、repository-safe docs へ出すことである。AI analysis result を GitHub 上に出す場合は、raw 本文を含まない repository-safe summary として扱う。

共有環境、実データ、社内サーバー、生成済み dashboard artifact の共有を扱う場合は、アクセス権、保持期間、削除方法、masking / redaction、利用者周知を先に決める。
remote managed Langfuse / Collector endpoint を使う場合は、送信前に access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を確認する。

## 9. Dashboard Requirements

Static HTML dashboard は Agent workflow 改善判断のための aggregate view とする。
個別 trace の詳細調査は Langfuse trace viewer、raw store、または明示 opt-in の sensitive bundle へ drill down する。

初期 view:

- Run Overview。
- Agent / Tool Behavior。
- Prompt / Skill / Instructions。
- Baseline vs Variant。
- Diagnosis / Improvement Loop。
- Collection Health。
- Outcome Linkage Candidate。

初期 client-side interaction:

- filter。
- sort。
- search。

初期 filter 軸:

- date。
- user。
- client。
- experiment。
- variant。
- status。

Dashboard に raw prompt / response / tool arguments / tool results の全文を表示してはならない。
`user.id` と `user.email` は表示および filter / search 対象に含めてよいが、共有先の access control を先に確認する。

## 10. Validation Requirements

Code、project file、CLI behavior、workflow を変更した場合は以下を実行する。

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

`dotnet test CopilotAgentObservability.slnx` includes Local Ingestion Monitor
Playwright smoke tests. The browser install step is therefore part of the
required validation bootstrap. The wrapper sets `PLAYWRIGHT_BROWSERS_PATH` to
the repository-local ignored `artifacts\playwright-browsers` path when unset,
so browser binaries stay outside tracked source and Playwright cache locks are
created inside the writable workspace. On Linux CI, pass `-WithDeps` to the same
script.

Collector example を変更した場合は、実 credential ではなく dummy `LANGFUSE_AUTH` で Compose 構文を確認する。

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

Copilot 実行に依存する挙動は自動テストだけで保証しない。
live validation では、確認日時、実行環境、sanitized setting label または
credential/path を含まない effective state、opaque な trace / record reference、
確認項目、未確認項目を記録する。credential、authorization value、raw content、
PII、sensitive path を含み得る literal setting value や raw identifier は
repository-safe evidence に記録しない。
Docker Desktop、WSL2 Docker Engine、remote managed endpoint、raw local receiver の各 profile は、それぞれ sanitized profile label、client kind、non-secret endpoint label、opaque または truncated な repository-safe trace / raw-record reference を live validation evidence に含める。

Release validation は一つの immutable candidate SHA に対して active product
surface を inventory し、versioned matrix contract に従って automated / live
evidence を分類する。Active row は `passed`、`failed`、
`blocked_external`、`not_applicable`、または作業中だけ許可する
`not_attempted` のいずれかとし、future surface の `not_available` と混同しない。
Skipped、timed-out、unavailable、unexecuted、または code defect は pass ではない。
Repository code で解決不能な required live case だけを `blocked_external` にできる。

## 11. Open Product Decisions

以下は実装前または共有運用前に決める。

- email / display name mapping。
- shared dashboard の access control、retention、利用者周知。
- external outcome linkage の採否。
- 実 GitHub / Notion ingestion の product / security decision。
- 実データを扱う場合の masking / redaction 方針。
- remote managed Langfuse / Collector の利用者同意 workflow。

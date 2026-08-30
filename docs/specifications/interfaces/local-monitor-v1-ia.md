# Local Monitor v1 IA Specification

Status: **Accepted**  
Authority: Issue #132  
Product design input: Issue #153  
Date: 2026-07-30
Route/transport amendment: PO136-A2b, 2026-08-09
Archive amendment: D082, 2026-08-09
Skill snapshot amendment: D083, 2026-08-11

## 1. Product boundary

Local Monitor v1 has two layers.

1. **AI-independent core**
   - find a local Repository and Session;
   - inspect Token/cache, Skill, Tool, Sub-agent, errors, retries, timing, hierarchy and raw-local detail;
   - navigate to exact Session / Run / Trace / Span / Event / raw record evidence;
   - compare two explicit Session cohorts without an LLM.
2. **Optional AI analysis**
   - v1 provider: GitHub Copilot SDK;
   - starts only from an explicit user action;
   - never blocks or weakens the core;
   - displays interpretation and improvement suggestions separately from observed facts.

Manual proposal authoring, manual evidence-selection workflows, Candidate/Recommended lifecycle, file apply, rollback and effect verdict are not primary Local Monitor v1 journeys.

## 2. Information architecture

```text
Repository selection
  -> Session Explorer
      -> Session detail
      -> Compare selection -> Repository Session Compare
```

There is no permanent sidebar or permanent top-level product navigation.

The shared header contains only:

```text
[breadcrumb]                                      [receiver status] [settings]
```

- Breadcrumbs are the primary back-navigation.
- AI status is not persistent in the header.
- Search is contextual; there is no global search in v1.
- Receiver status and Settings open the same Unified Settings modal at different sections.

## 3. Human page routes

Human pages exist only in raw-default posture.

| Route | Responsibility | Single question answered |
|---|---|---|
| `/` | Repository selection | Which Repository or Session scope should I inspect? |
| `/repositories/{repositoryId}/sessions` | Repository-scoped Session Explorer | Which Session in this Repository should I open or compare? |
| `/sessions` | All active Sessions virtual scope | Which Session should I open when Repository assignment is not useful? |
| `/sessions/unassigned` | Unassigned Session virtual scope | Which Session needs inspection or Repository assignment? |
| `/sessions/{sessionId}` | Session detail workspace | What happened in this Session? |
| `/repositories/{repositoryId}/comparisons/{comparisonId}` | Repository Session Compare | How do the fixed observed metrics differ between two explicit cohorts? |

Opaque IDs are used in routes. Repository display name, remote URL, local path, prompt text and raw values never appear as route identity.

Path identity, per-page query grammars, HTTP methods/statuses, browser history
and retired-route dispatch are exact in
[Local Monitor v1 Route and Session Explorer Transport](local-monitor-v1-route-transport.md).

### Settings modal URL state

Any primary page may carry one bounded query parameter:

```text
settings=state | receiver | ai | repositories | archive | storage | diagnostics
```

Opening/closing/changing a Settings section updates browser history. Closing removes `settings` while preserving the rest of the page query.

`settings` is a case-sensitive singleton. Unknown, empty or duplicate values
are `400 invalid_request`; the exact HTTP/recovery behavior comes from the
route/transport contract.

### Session detail URL state

```text
/sessions/{sessionId}?execution={executionId}&node={nodeId}&analysis={runId}
```

- all values are opaque validated IDs;
- `sessionId`, `execution` and `analysis` are canonical local UUIDv7 values;
  `node` is `node-` plus 32 lowercase hexadecimal characters;
- `node` requires or resolves its exact execution;
- an evidence link expands ancestors and selects/highlights the exact node;
- `analysis` names an exact run, never a mutable `latest` pointer;
- reload and back/forward restore the meaningful state.

The exact missing, scope-mismatch, node/execution-mismatch and transient-run
expiry results are owned by the route/transport contract. No state falls back
to a latest, nearby or same-name object.

### Session Explorer query state

Only non-sensitive filter/pagination state is reflected in the URL:

- `from` / `to`
- `source`
- `status`
- `has_skill` / `has_subagent` / `has_error` / `has_retry`
- `archive_scope`
- cursor
- `mode=compare`

Dynamic `q` and `model` filters exist only in the current page form/JavaScript
memory and the closed body of
`POST /api/local-monitor/v1/sessions`. They are never URL/history/storage/log
state and reset on reload or back/forward. The POST is the sole Session
collection transport; there is no GET alias, saved-search handle or fallback.
Non-default `limit` is also transient request state. A cursor may enter the URL
only for exact `q=null`, `model=[]`, `limit=null`/default 50; otherwise cursor
and limit remain page-memory/POST state and reload clears them. The exact
request, timestamp, cursor and conditional cursor-in-URL grammars are owned by
the route/transport contract.

Checkbox selection before a comparison snapshot is created is transient and is not encoded as hundreds of Session IDs in the URL. Reload before preview resets the unchecked draft. After preview, the comparison has an opaque server snapshot ID and a stable Compare route.

## 4. Existing route disposition

- `/` changes from the old Overview to Repository selection.
- The old `/traces` list is retired atomically when the functional #138
  Session Explorer backed by #134 ships. The narrow existing list classifier
  then returns empty no-store 404 for every method and advertises no `Allow`.
- `/traces/{traceId}` remains an exact low-level evidence page and is not a permanent navigation destination.
- Existing raw record/span/event routes remain technical evidence surfaces under their current security contracts.
- `/diagnostics`, historical import, backup/restore and retention pages may remain focused detail workflows opened from Settings. They are not permanent navigation.
- The old `/historical-analysis` human page is retired only when #164
  integrates the existing backend into Repository-local AI actions. Its narrow
  existing page classifier then uses the same empty no-store 404; versioned
  machine APIs remain under their accepted contracts.
- Existing unrelated standalone surfaces are not promoted into the v1 primary IA and are not implicitly deleted by this specification.
- No indefinite redirect, dual UI path or compatibility shim is added unless a frozen producer contract explicitly requires it.

## 5. Repository selection

Repository is presented as a card because the expected local set is small.

Each active card shows:

- display name;
- active assigned Session count;
- last observed safe instant.

Supplementary entries:

- `すべてのセッション`;
- `リポジトリ未設定のセッション`, only when non-empty;
- `リポジトリを追加`;
- archived Repository management through Settings.

Identity, locator, assignment, correction and conflict behavior are owned by
[the Local Repository catalog contract](local-repository-catalog.md), its
[DC156-12–19 executable closure](local-repository-catalog-executable.md), and
#155/#156. The complete registered DC156-01–19 contract is
`READY_FOR_IMPLEMENTATION`; #156 owns the catalog/assignment core and scope
composition. #134 alone owns
`GET /api/local-monitor/v1/repositories`, consuming the one
`ILocalRepositoryScopeSnapshotService` shared with #156/#161. Archive behavior
is owned by #160/#161.

## 6. Session Explorer

The screen is a dense direct-open list, not a dashboard and not master-detail.

### Header and actions

- Repository or virtual scope label;
- active result count;
- Repository management, where applicable;
- `比較を作成`.

No aggregate/KPI card row is displayed.

### Filters

- instruction label / Skill / Tool search;
- date range;
- source;
- model;
- status;
- Skill/Sub-agent/Error/Retry presence;
- include archived.

### Columns

| UI label | Contents |
|---|---|
| セッション | first instruction label or safe date-based fallback; source/model/capture note below |
| 状態 | terminal-safe Session state |
| 要約 | positive Skill / Tool / Sub-agent / Error / Retry values and honest missing state |
| トークン合計 | recorded total and cache-read ratio only when available |
| 開始 | safe start instant and duration |

Selecting a row opens Session detail directly. There is no preview pane.

The browser will obtain this list only through the #133/#134-owned closed
body-bearing POST; this UI never reads Workspace or Repository tables directly.
#133's semantic row requirements compose with the accepted exact success wire
in [`local-monitor-v1-session-collection.md`](local-monitor-v1-session-collection.md).
#134 alone maps the POST and performs the coherent read and serialization; the
functional Explorer page remains #138-owned.

### Compare selection mode

- normal mode has no checkboxes;
- `比較を作成` explicitly enters selection mode;
- selected Sessions are assigned to `基準` or `比較対象`;
- exact Skill-digest grouping may label the groups `変更前` / `変更後`;
- a bounded bottom action bar exposes cancel and preview;
- prompt/name/time similarity never groups Sessions;
- archive exclusions and invalid selections are visible before snapshot creation.

## 7. Repository Session Compare

Compare is fully deterministic and does not require AI.

The fixed sections, always in this order, are:

1. 対象
2. トークン
3. 入力トークンの内訳
4. 時間・実行量
5. スキル
6. ツール
7. サブエージェント
8. エラー・再試行
9. 比較条件

The screen does not contain `主要な差`, `比較上の注意` or `品質証拠` sections.

Columns are fixed:

```text
指標 | 基準 | 比較対象 | 差
```

- scalar facts show available count, per-Session median, minimum, maximum and supplementary total;
- named Skill/Tool/Sub-agent rows expose the complete union through search/pagination, not top-N ranking;
- missing is not zero;
- no score, ranking, anomaly judgement, improvement/regression verdict or LLM-authored summary is generated;
- `比較条件` lists source/model/version/completeness/available-count facts without interpretation;
- every metric drills down to included and unavailable Sessions and exact evidence;
- provider-ready users may ask AI to interpret the accepted deterministic receipt, but AI does not recalculate it.

### Deterministic scalar and selection rules

For every scalar metric, sort available per-Session values in ascending numeric
order. Odd cardinality uses the central value. Even cardinality uses the exact
decimal arithmetic mean of the two central values. Calculation remains in
base-10 decimal and never converts through binary floating point. `minimum`,
`maximum`, and supplementary `total` use the same available values; unavailable
states never enter the numeric sequence and never become zero.

The absolute difference is `B median - A median`. Relative difference is
`((B median - A median) / A median) * 100` only when A median is greater than
zero and both medians are available. It is otherwise null. The percentage is
calculated in decimal and rounded to one fractional decimal digit with
midpoint-away-from-zero rounding. Median, minimum, maximum, total, and absolute
difference are not rounded by this presentation rule.

Canonical decimal text is `0` for numeric zero. Every nonzero value is an
optional `-`, an integer part with no leading zero, and an optional fractional
part whose last digit is nonzero. A plus sign, exponent, integer leading zero,
fractional trailing zero, locale separator, whitespace, and negative zero are
forbidden. This canonical text is receipt state, not a new public Compare DTO.

Canonical selected-Session bytes begin with the strict UTF-8 domain
`copilot-agent-observability/local-comparison-selection/v1`, then encode cohort
`a` followed by cohort `b`. Within each cohort, exact canonical lowercase UUIDv7
Session IDs are distinct and ordinal-sorted. Each domain, cohort token,
invariant-decimal member count, and Session ID is framed by a four-byte unsigned
big-endian UTF-8 byte length followed by those bytes. SHA-256 covers the complete
framed sequence. Input order, display alias, current time, repository/name/time
proximity, and mutable Session state are not inputs and cannot repair selection.

The complete formula/snapshot contract is #165; implementation is #166. The
five machine operations, success DTOs, bounds, paging, and error graph are
owned by [Local Monitor v1 Repository Session Compare](local-monitor-v1-comparison.md).
Comparison IDs are canonical local UUIDv7. A live snapshot expires after 24
hours; #166 retains only the append-only, non-sensitive expiry tombstone in the
route/transport contract so a known expired URL deterministically returns
`410 comparison_expired`. Unknown and Repository-mismatched IDs return
`404 comparison_not_found`.

## 8. Session detail workspace

The screen has three vertical regions:

1. compact Session context;
2. fixed Session summary;
3. execution workspace with contextual inspector.

### Session context

- first instruction or safe date-based label;
- status;
- source;
- start/end/duration;
- archived indicator;
- capture warning only when there is a limitation;
- optional provider-ready `AIで分析` action.

Opaque technical IDs are not the title.

### Fixed summary

The summary always represents the complete Session snapshot and does not change when a node is selected.

#### トークン合計

- total;
- exact input/output horizontal bar;
- numeric labels.

#### 入力トークンの内訳

- cache read ratio when consistent;
- exact cache-read/new-input horizontal bar;
- cache write/creation as a separate supplementary value.

No subjective cache grade or monetary claim is shown.

Other fixed items:

- Skill;
- Tool;
- Sub-agent;
- Error / Retry.

### Initial inspector

Normal entry never shows an empty panel. It shows Session overview:

- initial instruction;
- additional instruction count;
- source/status/time/execution count;
- capture coverage;
- expandable technical information.

## 9. Hierarchical timeline

Hierarchy:

```text
Session
  -> 実行
      -> Agent / Skill / Tool / Sub-agent / Event / Error / Retry
```

Each row combines semantic hierarchy on the left and timing/duration/parallelism on the right.

- no separate tree/waterfall tabs;
- latest execution is expanded by default;
- previous execution headers retain summary/error/retry facts while collapsed;
- Agent identity is shown only with exact authority;
- `Main Agent` is never invented;
- unknown parents appear under an explicit unknown-relation group;
- missing timing is text/state, not a fake zero-width duration;
- page-level horizontal scrolling is prohibited; the timeline may have bounded internal scrolling.

## 10. Contextual inspector

Common structure:

- return to Session overview;
- kind/name/status/duration/parent path;
- object-specific facts;
- related activity/evidence;
- expandable `技術情報`.

### Tool

- status/start/end/duration/caller/exit;
- input/result/error on demand;
- retry/recovery relation;
- children;
- MCP server identity only when exact.

### Skill

- name/source/trigger/timing;
- current-valid invocation state;
- historical body/definition location on demand;
- current file only as separately labelled current state;
- no unused-Skill claim without certified absence.

### Sub-agent

- selected/started/completed/failed/deselected as distinct facts;
- exact input when available;
- activity/Token/children;
- no instruction inference from unrelated Tool input.

### Error / permission / event

- exact status/content/time/parent;
- retry/recovery relationship;
- raw content only through an authorized read.

There are no page-level `整形 / raw` tabs.

## 11. Optional AI presentation

### Session report

- Session header action;
- durable immutable report history;
- latest successful retained report by default;
- `再分析` creates a new snapshot/run;
- `過去の分析` remains in the same surface;
- follow-up chat is not persisted.

### Node analysis

- inspector action;
- exact node anchor;
- timeline remains visible;
- result is transient and is not inserted into Session report history.

### Repository selection / Compare

- bounded preview first;
- no permanent history;
- Compare AI receives only the deterministic comparison receipt.

AI result sections:

- scope/snapshot;
- summary;
- findings;
- improvement suggestions;
- evidence;
- limitations;
- provider/model/template provenance.

AI output is never rendered as an observed timeline node.

## 12. Unified Settings modal

At 1366×768 the modal is approximately 960×640 and leaves page context visible.

Sections:

- 状態
- 受信
- AI設定
- リポジトリ
- アーカイブ
- 保存・バックアップ
- 診断

Each section shows current state and primary supported action together. Complex or destructive workflows open a focused detail or confirmation dialog. There is no separate Settings dashboard or nested permanent navigation.

The raw-default `AI設定` section reads and explicitly checks one closed
Settings-owned readiness resource at
`/api/local-monitor/v1/settings/ai-readiness`. Its GET and POST expose only the
provider, selected model, selected configuration, closed readiness and
last-check states, and the fixed provider-egress notice. The POST action
`接続を確認` uses the owned Copilot SDK status and runtime identity certifier;
it creates no analysis run, SDK session, snapshot, result or retained item.

The raw-default `保存・バックアップ` section reads the exact same-origin,
no-store `GET /api/local-monitor/v1/settings/storage` aggregate. It reports
only the configured primary database file size, the existing Retention runtime
state, one process-local backup operation snapshot (including the last
successful instant and closed validation state), the latest historical import
operation state, and whether the active monitor lease proves restart is not
required. Unavailable owners produce closed `unknown` or `null` facts. The
aggregate never scans directories or backup files and never returns paths,
file names, operation IDs, policy values, failure reasons, or exception text.
It is absent under `--sanitized-only`; focused backup, retention, and historical
import actions retain their owning routes and workflows.

## 13. Archive behavior

- archive is reversible local metadata;
- archive is not delete, retention or pin;
- default Repository/Session lists, Compare and Repository-range AI exclude archived scope;
- direct Session access and explicit single-Session AI remain available;
- Repository archive does not cascade Session archive;
- new data does not silently restore an item.

The exact executable storage, state, mutation, route and backup contract is
[Local Archive v1](local-archive.md). D082 adds no Razor page/model, static
asset, navigation item, Settings layout, archive button, dialog, visual state
or sentence-level copy. Those later UI surfaces remain owned by
#145/#146/#167/#138 and #169.

## 14. User-facing terminology

Binding key labels:

| Internal/old expression | UI label |
|---|---|
| Repository | リポジトリ |
| Session | セッション |
| Source | 取得元 |
| 観測された動き | 要約 |
| 記録Token | トークン合計 |
| cache read | キャッシュから読み込み |
| new input | 新規入力 |
| cache creation | キャッシュ書き込み |
| Repository未割り当て | リポジトリ未設定のセッション |
| technical information | 技術情報 |

Sentence-level microcopy remains #169 work after the first integrated implementation and does not block initial implementation.

Missing-state labels are provided by #137 and must not expose internal enum names.

## 15. State matrices

### Core page states

Every primary page supports:

- loading;
- empty;
- successful data;
- no filter match;
- persistence busy/unavailable;
- malformed/deep-link-not-found;
- active and archived context;
- source unsupported, not observed, capture gap, certification pending, raw expired.

Malformed/missing/stale/expired HTTP status, closed page-state and recovery
tokens are defined by the route/transport contract. Sentence-level HTML and
copy remain #137/#169-owned and are not byte-frozen.

### AI states

- unconfigured: no action on core pages;
- ready: action visible;
- queued/running: async state in AI surface;
- succeeded/zero findings;
- provider failed/partial;
- invalid evidence/result;
- stale snapshot/scope too large;
- timed out/canceled;
- expired retained report.

### Compare states

- no cohort;
- invalid overlap;
- archived exclusion;
- empty after exclusion;
- too many Sessions;
- snapshot ready;
- deterministic result;
- low available count;
- mixed condition facts;
- expired snapshot.

## 16. Dimensions and scrolling

Hard validation viewport: **1366×768**.

### Shared

- header: 48px;
- page outer padding: 24px;
- major gap: 16px;
- compact gap: 8px;
- no page-level horizontal scroll.

### Repository selection

- card min width 300px, max width 380px;
- 16px grid gap;
- up to 3 columns at 1366px.

### Session Explorer

- title/action region: maximum 64px;
- filter region: maximum 88px;
- list owns remaining height and scrolls internally;
- row target height 52–64px.

### Session detail

- context region: maximum 72px;
- fixed summary: 104px;
- workspace uses the remaining height;
- inspector width: default 380px, min 360px, max 420px;
- timeline is flexible and internally scrollable.

At widths below 1180px, the inspector becomes a right overlay/drawer instead of forcing page horizontal scrolling. At the hard 1366px viewport it is a simultaneous second pane.

### Compare

- cohort/scope header: maximum 112px;
- fixed metric header remains visible;
- metric/named-row body scrolls internally;
- long named sections use search/pagination, not page-width expansion.

### Settings

- 960×640 target;
- max width `calc(100vw - 40px)`;
- max height `calc(100vh - 40px)`;
- section content scrolls inside the modal.

## 17. Accessibility

- semantic headings/landmarks/tables/tree controls;
- keyboard order follows visual order;
- focus-visible on every interactive element;
- Settings traps focus, Escape closes, and focus returns to the invoker;
- async state uses a concise polite live region;
- completion moves focus to the result heading;
- failure returns focus to the initiator or error summary;
- color is never the sole status/difference signal;
- bars always have text labels and values.

## 18. Security and posture

- human UI and `/api/local-monitor/v1/*` exist only in raw-default;
- `--sanitized-only` is receiver/health/machine-API-only per #159;
- raw reads are same-origin, no-store, retention-gated and inert text;
- mutations require CSRF;
- no raw/PII/path/credential in URLs, logs, fixed errors or repository artifacts;
- dynamic Explorer `q`/`model` values are transient POST-body state only;
- Session pagination cursors carry only a process-keyed HMAC binding, never
  raw values or an unkeyed low-entropy digest;
- AI data egress is explicit and separate from local observation;
- frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE remain unchanged;
- no heuristic Session/Repository/parent identity;
- no missing-to-zero or composite score.

## 19. Implementation ownership

| Area | Contract | Implementation |
|---|---|---|
| Repository catalog/assignment | [catalog contract](local-repository-catalog.md) + #155 | #156 |
| Skill projection validity | #154 | #154 |
| Skill v1 correction/v2 transport/body/path snapshot/current file | [snapshot contract](skill-invocation-snapshot.md) + #119/#157/#158/D083 | #158 after the prerequisite join and implementation/release gates |
| Sanitized-only runtime | #159 | #168 |
| Archive | [Local Archive v1](local-archive.md) + #160/D082 | #161 |
| AI scope/history | #162 | #163/#164 |
| Compare | #165 | #166 |
| Workspace read APIs | #133 plus accepted #171 [Session collection success contract](local-monitor-v1-session-collection.md) | #134; mapping may proceed |
| Human route/URL/Session collection request transport | [route transport](local-monitor-v1-route-transport.md) + #136 | #136 pure parsers; #134 maps the accepted success wire |
| Shell/header/Settings host | this spec | #135/#136 |
| Missing states | #129 + this spec | #137 |
| Repository selection | this spec | #167 |
| Session Explorer | this spec | #138 |
| Session summary | this spec | #139 |
| Timeline/inspector | this spec | #140 |
| Settings sections | this spec | #145/#146 |
| Japanese microcopy | this spec | #169 |
| Cross-cutting validation | this spec | #147 |
| User documentation | canonical docs | #148 |

The frozen v1 correction is executable independently: `skill.started` and
`skill.completed` are supported, while `skill.invoked` is unsupported on
`POST /api/session-ingest/v1/events`; every other v1 route/wire/enum/limit/
status/response byte remains frozen. D083 closes the additive #119/#158 product
decisions but does not make the feature production-ready. The nonregistered
#119 parser/handoff follows mandatory live-Issue reconciliation; #158 runtime
work waits for its exact prerequisite join, and host activation, registration
and release wait for the focused, platform, live, full-validation and review
gates in the snapshot contract.

Once those gates pass, D083 adds to raw-default composition only
`POST /api/session-ingest/v2/events`,
`GET /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}`,
`GET /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/content`
and
`POST /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/current-file-read`.
The receiver-only/`--sanitized-only` host registers none of them. The
current-file POST is additionally absent with zero configured roots. A
nonempty valid root set registers it only on an independently certified
Windows/Linux platform; explicit invalid roots or an unsupported platform fail
startup instead of degrading to route absence. D083 adds no Razor page,
navigation entry, compatibility path, v1 fallback or sanitized empty carrier.

# Canvas Session Workspace UI Interface

## Scope

This specification defines the Issue #52 Canvas Session Workspace: the
session sidebar, the four-tab shell (Review / Evidence / Improve / Compare),
the Review workspace, and the human-evaluation write path. It builds on the
Issue #51 contracts in `canvas-session-workspace.md` and changes no #51
ingest, identity, or completeness rule.

The mandatory pre-UI design gate for this UI was captured and approved on
2026-07-11; evidence lives in
`docs/sprints/issue-52-canvas-workspace/design-gate/`.

Issue #52 shipped Evidence, Improve, and Compare as fixed placeholder cards.
Issue #53 replaces the Evidence placeholder under the separate
`canvas-session-evidence.md` contract. Issue #54 replaces the Improve
placeholder under `canvas-improvement-proposals.md`; Issue #55 adds its
separately token-gated local apply confirmation flow under
`canvas-proposal-apply.md`. Compare remains a later child.

## Shell Composition

The extension-owned helper server serves the Session Workspace as the Canvas
page at `/`. The existing analysis helper view (trace selection, analysis
options, `session.send()` dispatch) moves unchanged to `/analysis` on the
same helper server and stays token-gated. The Issue #45 `session.send()`
execution boundary and the bounded analysis instruction are unchanged.

The workspace page is composed of:

- a top bar with the Local Monitor connection state (existing health
  derivation: ready / not_ready / unreachable);
- a session sidebar (below);
- four tabs: Review, Evidence, and Improve (functional after the Issue #53 and
  Issue #54 amendments), plus Compare (placeholder). Issue #52 historically
  shipped Evidence and Improve as placeholders; their current behavior is
  defined by `canvas-session-evidence.md` and
  `canvas-improvement-proposals.md`.

The page uses the existing helper-page design tokens (host-variable-backed
colors, card layout, Japanese UI copy). All monitor data reaches the page
through helper-server proxy routes gated by the per-launch `x-canvas-token`
header, mirroring the existing `/api/traces` pattern. Canvas action responses
remain bounded DTOs; no session raw content enters action responses or logs.

## Session Sidebar

Data source: `GET /api/session-workspace/sessions?limit=50` and
`GET /api/session-workspace/resolve` proxied through the helper server.

Groups, in order:

1. **この会話（exact-bound）** — the Session resolved from the Canvas
   `ctx.sessionId` via
   `resolve?source_surface=copilot-sdk&native_session_id=<ctx.sessionId>`.
   Present only when `binding_status` is `bound`.
2. **最近のセッション** — remaining list items whose `completeness` is not
   `unbound`, most-recent-first (list order).
3. **未紐付け（OTel のみ）** — list items with `completeness` = `unbound`.

Selection rules:

- Only the exact-bound Session of group 1 is auto-selected. If no Session
  resolves, no Session is selected and the Review tab shows a neutral
  "セッションを選択してください" empty state.
- The workspace never guesses a "latest" session and never binds by
  repository, workspace, or timestamp proximity.

Each sidebar item shows: label (below), status pill, completeness badge, an
`exact` binding badge when the Session has at least one `native` binding,
repository (nullable), and `started_at`/`last_seen_at` time.

Item label: the instruction preview's first line when the instruction is
available (bounded, sanitized-safe: the label is fetched with the instruction
preview route and truncated to 80 characters), otherwise the localized
fallback for the Session's dominant source surface, or
"OTel トレースのみ（未紐付け）" for unbound Sessions. Labels are display-only
and never persisted back.

Status pill mapping (from #51 `status`):

| `status` | Pill |
| --- | --- |
| `active` | 実行中 (accent, pulsing dot) |
| `completed` | 完了 (success) |
| `failed` | 失敗 (danger) |
| `unknown` | 未完全 (warn) |

An `active` Session is never rendered with success or failure wording.

## Review Tab

All fields come from the #51 sanitized detail response unless stated. Absent
data renders an honest empty state; nothing is inferred.

Cards, in order:

1. **セッションの結合** — binding kind per `native_ids[].binding_kind`
   (`native` renders as "exact（native session ID）"); unbound Sessions render
   a warning banner and "未紐付け".
2. **実際の指示** — the instruction preview (route below). Non-`available`
   `content_state` values render their honest label
   (`not_captured` / `redacted` / `unsupported` /
   `expired_pending_deletion`) and no reconstructed text.
3. **結果** — from `status`: `completed` → 成功見出しなしの「完了」表記と終了時刻,
   `failed` → 失敗 + 終了時刻, `active` → 「未確定」(no judgement),
   `unknown` → 「不明」. The card states only what the store records.
4. **品質ゲート** — exactly the deterministic v1 gates below.
5. **人間評価** — one-tap verdict buttons (below).
6. **次の操作** — the contextual action mapping below.

### Quality gates (v1)

| Gate | PASS | FAIL | 未評価 |
| --- | --- | --- | --- |
| 終了状態 | `status` = `completed` | `status` = `failed` | `active` / `unknown` |
| エラーイベント | zero `status` = `error` events AND completeness is `rich` or `full` | one or more `error` events (count shown) | otherwise |

No other gate is rendered in v1. Gates without a deterministic data source
(tests, review findings) are not fabricated; they arrive with later child
issues when typed evidence exists.

### Human evaluation

One-tap buttons 「期待どおり」/「問題あり」. Enabled only when the Session has
at least one `native` binding and `status` is `completed` or `failed`.
Tapping records the verdict; tapping the recorded verdict again clears it.
The current verdict renders as pressed state plus recorded time.

### Contextual next actions (v1)

| Session state | Primary action | Secondary |
| --- | --- | --- |
| `active` | Local Monitor を開く | — |
| `completed` / `failed`, bound, no proposal | 詳細分析と改善案を作る | Local Monitor を開く |
| `completed` / `failed`, bound, proposal exists | 改善案を確認 | Local Monitor を開く |
| unbound | Local Monitor でトレースを開く | — |

Review actions may reference Improve only through the bounded Issue #54
proposal interface. Compare remains a placeholder until its later-child
contract is implemented.

## Helper Proxy Routes (extension server)

All routes require the `x-canvas-token` header (or `t` query fallback where
the existing pattern uses it), are loopback-only, and never log payload
content.

| Route | Backs | Notes |
| --- | --- | --- |
| `GET /api/session-workspace/sessions` | monitor sessions list | sanitized pass-through |
| `GET /api/session-workspace/sessions/{sessionId}` | monitor session detail | sanitized pass-through |
| `GET /api/session-workspace/resolve` | monitor resolve | sanitized pass-through |
| `GET /api/session-instruction/{sessionId}` | instruction preview | shape below |
| `PUT /api/session-workspace/sessions/{sessionId}/human-evaluation` | monitor evaluation write | adds the monitor CSRF header server-side |

`GET /api/session-instruction/{sessionId}` picks the earliest event whose
`type` is in the user-instruction family already pinned by the #51
normalizer (`user.message`, `UserPromptSubmit`, `userPromptSubmitted`):

- if that event's `content_state` is `available`, the helper server fetches
  `GET /sessions/{id}/events/{eventId}/content` from the monitor and returns
  `{ "state": "available", "preview": "<first 2000 characters>" }`;
- otherwise it returns `{ "state": "<content_state>" }` (or
  `{ "state": "no_instruction" }` when no such event exists).

The preview is same-local-user display data: it must not be copied into
Canvas action responses, logs, or committed artifacts, matching the existing
trace-content preview boundary. Every `/api/session-instruction/*` response
sets `Cache-Control: no-store`, like the sibling raw-bearing helper routes.

## Local Monitor: Human Evaluation

New installed-monitor endpoint:

```text
PUT /api/session-workspace/sessions/{sessionId}/human-evaluation
```

- Loopback + Host-header validation as the existing monitor routes.
- Same-origin only (cross-site requests are rejected first) and requires the
  CSRF header `x-monitor-csrf: local-monitor` (state-changing action), the
  same two-layer guard as the existing raw analysis action.
- `Content-Type: application/json`; body is exactly
  `{ "verdict": "expected" | "problem" | null }`; `null` clears.
- `204` on success. Failures use the fixed shape `{ "error": "<code>" }`:
  `400` `invalid_session_id`, `400` `invalid_human_evaluation_request`,
  `403` `cross_origin_forbidden`, `403` `csrf_required`,
  `404` `session_not_found`, `415` `unsupported_media_type`.

Storage: additive session-store table `session_human_evaluation`
(`session_id` PK/FK, `verdict` TEXT, `recorded_at` TEXT ISO-8601). The
startup session schema migration extends as in #51 (failure fails host
construction). Clearing deletes the row.

The #51 sanitized detail response gains one additive top-level field
`human_evaluation`: `{ "verdict": "expected"|"problem",
"recorded_at": "<ISO-8601>" }` or JSON `null`. The list item shape is
unchanged. `canvas-session-workspace.md` records this amendment.

## Tests

- xunit: human-evaluation endpoint contract (CSRF required, verdict
  set/overwrite/clear, invalid id, missing session, detail field
  round-trip), migration additivity.
- `node --test`: pure workspace helpers — sidebar grouping/labels from a
  sessions list, status pill mapping, gate derivation, next-action mapping,
  instruction preview state handling — plus rendered-HTML assertions that
  the workspace page contains the sidebar groups, the four-tab shell, and
  the Review cards for representative session fixtures, and that
  `/analysis` serves the existing analysis view. These run through the
  existing `CanvasExtensionContractTests` node integration; no new browser
  harness is added for the extension. Browser-level evidence stays in the
  design-gate/E2E screenshot record.

## Non-Goals

No Compare behavior, no session merge or completeness changes, no new
dependencies, no change to the #45 dispatch instruction, no raw content in
sanitized responses, and no physical raw deletion (Issue #57). Direct apply,
patch generation, target-path resolution, and rollback remain Issue #55.

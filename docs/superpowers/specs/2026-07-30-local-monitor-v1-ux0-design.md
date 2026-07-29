# Local Monitor v1 UX0 — Accepted Design

Status: **Accepted**  
Issue: #153  
Date: 2026-07-30  
Final IA specification: [`docs/specifications/interfaces/local-monitor-v1-ia.md`](../../specifications/interfaces/local-monitor-v1-ia.md)

## Review outcome

The seven visual layouts are accepted as structural references. The images are not regenerated.

The literal Japanese text inside the mockups is not copy authority. Key terminology is fixed here and sentence-level microcopy is completed after the first integrated implementation in #169.

The Repository Compare mockup is accepted only for overall layout. Its `主要な差`, `比較上の注意` and `品質証拠` areas are superseded by #165/#166 and are not implemented.

## Product boundary

Local Monitor v1 has:

1. an AI-independent Repository/Session observation, investigation and comparison core;
2. optional GitHub Copilot SDK analysis and improvement suggestions.

The core works without an LLM, API key or provider authentication.

Manual proposal authoring, manual evidence selection, Candidate/Recommended workflow, apply, rollback and effect verdict are not primary Local Monitor v1 journeys.

## IA

```text
リポジトリを選ぶ
  -> セッションを探す
      -> セッション詳細を開く
      -> 比較対象を選ぶ -> 比較する
```

There is no permanent sidebar.

```text
[パンくず]                                      [受信状態] [設定]
```

Breadcrumbs are the primary back-navigation. AI status is not persistent. Search is contextual.

## Repository selection

- card layout;
- display name;
- assigned active Session count;
- last observed instant;
- all Sessions virtual scope;
- unassigned Session virtual scope;
- add/edit/assignment/archive entry;
- opaque local UUIDv7 identity.

## Session Explorer

- dense direct-open rows;
- no preview pane;
- no generic KPI/usage dashboard;
- contextual search and filters;
- columns: セッション / 状態 / 要約 / トークン合計 / 開始;
- compare checkboxes only after `比較を作成`;
- archive actions.

## Repository Session Compare

Compare is deterministic and AI-independent.

Fixed sections, in order:

1. 対象
2. トークン
3. 入力トークンの内訳
4. 時間・実行量
5. スキル
6. ツール
7. サブエージェント
8. エラー・再試行
9. 比較条件

It displays all predeclared facts, not LLM-selected “important” differences.

- no `主要な差` section;
- no natural-language `比較上の注意` section;
- no `品質証拠`;
- no improvement/effect verdict;
- missing is not zero;
- per-Session median/range/available denominator are primary;
- exact Skill digest may define a before/after boundary;
- optional AI may interpret, but not recalculate, the deterministic receipt.

## Session detail

Session detail is an execution workspace plus contextual inspector.

### Fixed top summary

- instruction/status/source/time;
- `トークン合計`: input/output horizontal bar;
- `入力トークンの内訳`: cache-read/new-input horizontal bar and optional cache write;
- Skill/Tool/Sub-agent/Error-Retry summary.

### Hierarchical timeline

```text
Session
  -> 実行
      -> Agent / Skill / Tool / Sub-agent / Event / Error / Retry
```

Semantic hierarchy and timing share one row. There are no separate tree/waterfall tabs. Latest execution opens by default. Unknown parent/time remains explicit.

### Inspector

- Tool: input/result/exit/retry/children;
- Skill: invocation/source/trigger/historical body/definition/current file distinction;
- Sub-agent: exact input/lifecycle/activity/children;
- Error/permission/event: exact state/content/relation;
- technical IDs and OTLP under `技術情報`;
- no page-level formatted/raw tabs.

## Optional AI

- whole Session report: durable immutable history;
- exact node analysis: transient;
- Repository selection and Compare analysis: bounded operational result, no permanent history;
- AI never receives the SQLite file or arbitrary SQL access;
- exact evidence links return to the selected timeline node;
- AI output is separate from observed facts.

## Archive

Session and Repository archive are reversible local metadata.

- default list/Compare/Repository AI exclusion;
- direct Session access remains;
- explicit single-Session AI remains;
- Repository archive does not cascade Session archive;
- incoming data does not restore automatically;
- archive is not delete, retention or pin.

## Unified Settings

One modal, approximately 960×640 at 1366×768:

- 状態
- 受信
- AI設定
- リポジトリ
- アーカイブ
- 保存・バックアップ
- 診断

Complex/destructive actions may open focused details or confirmations. Normal pages do not display persistent AI, backup, retention or diagnostic panels.

## `--sanitized-only`

Raw-default is the only human UI posture. Sanitized-only is receiver/health/machine-API-only and does not provide per-screen fallback UI.

## Binding key labels

| Old/internal | UI |
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

All other sentence-level Japanese remains #169 work and does not block initial implementation.

## Accepted visual set

1. Repository selection
2. Session Explorer
3. Compare selection mode
4. Repository Compare — layout only; content corrected by #165/#166
5. Session Workspace and inspector
6. Session AI result
7. Unified Settings modal

All are validated structurally for 1366×768, without a permanent sidebar or page-level horizontal scrolling.

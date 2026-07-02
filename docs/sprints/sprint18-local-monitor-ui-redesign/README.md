# Sprint18: Local Monitor UI Redesign

Local Ingestion Monitor UI を `.claude/design_handoff_local_monitor/`（2026-07-03
確定版デザインハンドオフ）に従い Console 型 IA へ全面再設計する sprint。

## Source of truth

- デザイン / IA: `.claude/design_handoff_local_monitor/README.md`
  （カンバス `ローカルモニター再設計.dc.html` が実測値の ground truth）
- 実装計画: [implement-claude-design-handoff-local-mo-squishy-horizon.md](implement-claude-design-handoff-local-mo-squishy-horizon.md)
- 決定記録: D042（IA / tokens / 7 画面 + C1–C8）、D043（span-detail raw
  route）、D044（schema v4 rollup）、D045（履歴再送チャット） in
  [../../decisions.md](../../decisions.md)

## Scope

- 7 画面: 概要 / トレース一覧（master-detail）/ トレース詳細（フロー ·
  waterfall + キャッシュ列）/ スパンインスペクタ / エラー解析モード /
  Copilot 解析ドロワー / 診断（ポップオーバー経由）
- バックエンド: monitor projection schema v3→v4（additive）、cache token
  rollup + `trace_status`、`GET /api/monitor/overview`、
  `GET /api/monitor/trace-list`、`GET /traces/{traceId}/spans/{spanId}/detail`
  （raw-bearing）、analysis start payload の `question` / `history`
- 不変: 既存 public routes の shape / ordering、
  `CanvasExtensionContractTests.cs`、sanitized / raw 境界、readiness contract

## Milestones

M0 docs → M1 backend foundation → M2 shell/tokens → M3 概要 → M4 一覧 →
M5 詳細 → M6 インスペクタ → M7 エラーモード → M8 ドロワー → M9 診断 →
M10 reconciliation / validation / evidence。
各 milestone で build + targeted tests + full test 後にコミット
（prefix: `Local Monitor UI Redesign <type>: <summary>`）。

## Evidence

検証結果・E2E テストケース・既知の制約はこのディレクトリ配下に保存する。

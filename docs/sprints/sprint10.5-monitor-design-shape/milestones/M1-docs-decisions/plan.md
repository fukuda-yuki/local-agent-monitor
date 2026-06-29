# M1: Docs & Decisions

## Status: Done

## Deliverables

1. `docs/decisions.md` — D024–D028 追記
2. `PRODUCT.md` — Visual Foundation セクション追加
3. `docs/spec.md` — monitor views セクション更新

## D024–D028 Summary

| Decision | Topic | Status |
| --- | --- | --- |
| D024 | 設計ビュー deferred non-goal のナローイング | Accepted |
| D025 | Cytoscape.js + dagre を vendored 可視化依存として許可 | Accepted |
| D026 | Cache Explorer は sanitized-metrics-only、trace-internal 限定 | Accepted |
| D027 | VS Code Dark+ テーマ採用。DADS 非適用 | Accepted |
| D028 | Noto Sans JP / Noto Sans Mono を vendored タイポグラフィとして採用 | Accepted |

## PRODUCT.md Change

"Visual Foundation" セクションを Design Principles に追加：
- Theme: VS Code Dark+（D027）
- Typography: Noto Sans JP / Noto Sans Mono vendored（D028）
- Color: OKLCH、VS Code Dark+ パレット
- Spacing: 8px ベースライングリッド

## spec.md Change

monitor views セクションを更新：
- 4つの設計ビュー（Summary / Timeline / Flow Chart / Cache）の記載
- TraceDetail タブアーキテクチャ（JS sanitized セクション + Razor raw セクション）
- クライアントサイド依存: Cytoscape.js + dagre（vendored）
- Noto フォント vendored
- VS Code Dark+ テーマ

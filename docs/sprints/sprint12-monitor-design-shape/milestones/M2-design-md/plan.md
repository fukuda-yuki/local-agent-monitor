# M2: DESIGN.md — 完了記録

## 成果物

`DESIGN.md` をプロジェクトルートに新規作成（9,797 bytes）。

## 内容

- **Design Tokens**: Color（Surface / Border / Ink / Accent / Semantic の5層、OKLCH）、Spacing（8px グリッド、6段階）、Typography（type scale 11-24px、Noto Sans JP/Mono の @font-face 定義）
- **Components**: Navigation、Filter Bar、Metric Panels、Data Tables、Tabs、Status Indicators、Links、Raw Preview
- **Layout**: ページ構造（sticky nav + context bar + 全幅コンテンツ）、Responsive 方針、Z-Index Scale
- **Motion**: 基本イージング、reduced-motion 対応
- **Accessibility**: WCAG AA 方針、フォーカスインジケータ、キーボード操作

## 適合確認

- [x] D027: VS Code Dark+ パレット準拠
- [x] D028: Noto Sans JP / Noto Sans Mono vendored
- [x] impeccable absolute bans（side-stripe borders, gradient text, glassmorphism, hero-metric, identical cards, uppercase eyebrows, numbered markers）
- [x] PRODUCT.md Design Principles（Data-first, Calm professionalism, Clarity, Trust, Local-first）

# Product

## Register

product

## Users

Copilot agent workflow の挙動を調査・改善する技術者：

- **開発者**: trace 単位で prompt / response / tool call / token / error を深掘りする
- **実装者**: prompt / skill / MCP / CLI wrapper の改善効果を A/B 比較する
- **Maintainer**: 日次・週次で agent の全体的な健全性と失敗傾向を俯瞰する
- **Reviewer**: dashboard snapshot で改善候補と判断記録を確認する

典型的な利用シーン：
- 朝イチで前日の agent 実行結果を俯瞰し、問題を素早く特定する
- 特定の trace を深掘りして原因を調査する
- 実験条件 (baseline vs variant) の差分を比較する

## Product Purpose

Copilot Agent Observability は、GitHub Copilot Chat / CLI / Codex App から出力される OpenTelemetry data を収集し、Agent workflow の実行過程を trace 単位・集計単位の両方で確認可能にする Local-first 観測基盤。

成功とは：利用者が「どの agent が、どの tool を呼び、どれだけ時間と token を使い、どこで失敗し、どう改善できるか」を、外部サービスに依存せず手元で判断できる状態。

## Brand Personality

**プロフェッショナル · 集中 · 明瞭**

- プロフェッショナル：飾らず、正確に。データを見る人の知性を信頼する。
- 集中：ノイズを排し、必要な情報に最短で到達できる。
- 明瞭：正常・警告・エラーが一目で区別でき、数値の意味を誤解させない。

Reference: **Grafana** — ダークテーマの落ち着いた配色、高い情報密度、ダッシュボードパネルによる俯瞰レイアウト、フィルター・検索・ドリルダウンの操作性。

## Anti-references

- **SaaS 系の派手なランディングページ風デザイン**: ヒーローセクション、大きなイラスト、過剰な余白、グラデーションボタン、グラスモーフィズム。情報密度が低く、データツールとして機能しない。
- **カラフルでポップな管理画面**: パステルカード、絵文字多用、アニメーション過多。信頼感を損なう。
- **クリーム / ベージュ背景の「温かみ」系**: AI が生成しがちな saturated default。観測ツールには冷たく落ち着いた色調が適する。
- **過剰なミニマリズム**: 情報を隠蔽する段階的開示、空の状態を埋めるイラスト。データは最初から見せる。

## Design Principles

1. **Data-first**: 情報密度を犠牲にしない。データを見に来た人が最初に目にするのはデータそのもの。装飾より内容。
2. **Calm professionalism**: Grafana 的な落ち着いたダークテーマ。色数は抑え、意味のある場所にだけ色を使う。飾りではなく機能で語る。
3. **Clarity through hierarchy**: 俯瞰（ダッシュボード）→ 一覧（traces）→ 詳細（trace detail）→ 生データ（raw）への自然なドリルダウン。フィルター・検索・ソートで必要な情報に最短で到達できる。
4. **Trust through consistency**: 数値は正確に、状態は明確に。成功（緑）· 警告（琥珀）· エラー（赤）の区別は色だけでなく形状やテキストでも伝える。同じものは同じ見た目で。
5. **Local-first simplicity**: サーバー不要、外部依存なし。静的 HTML で完結する確かさ。軽量で高速、余計なフレームワークに依存しない。

## Visual Foundation

- **Theme**: VS Code Dark+ ベースのダークテーマ（D027）。Grafana インスパイアのレイアウト・情報密度。
- **Typography**: Noto Sans JP / Noto Sans Mono（vendored under `wwwroot/vendor/fonts/`、D028）。システムフォント不可。
- **Color**: OKLCH、VS Code Dark+ パレット（青アクセント、意味的ステータスカラー）。Restrained 戦略。
- **Spacing**: 8px ベースライングリッド。

## Accessibility & Inclusion

- **WCAG AA** 相当を最低限確保
- 色だけで情報を伝えない（ステータスアイコン · テキストの併用）
- コントラスト比: 本文 ≥ 4.5:1, 大文字 ≥ 3:1
- `prefers-reduced-motion` 対応
- キーボード操作可能なフィルター・テーブルソート

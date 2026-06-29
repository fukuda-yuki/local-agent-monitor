# M3: CSS Token Architecture — 完了記録

## 成果物

`milestones/M3-css-tokens/architecture.md` を新規作成（14,187 bytes）。

## 内容

DESIGN.md を実装可能な CSS アーキテクチャに展開:

- ファイルレイアウト（7セクション）
- @font-face 定義（Noto Sans JP 6 weight + Noto Sans Mono 1 weight）
- CSS カスタムプロパティ階層（Surface / Border / Ink / Accent / Semantic / Typography / Spacing / Z-Index）
- CSS リセット／ベース（VS Code 慣習、`color-scheme: dark`）
- レイアウトプリミティブ（sticky header 2段、コンテンツ領域）
- コンポーネントスタイル（Table、Metric Panel、Tabs、Filter Input、Button、Raw Preview、Status Badge、Flow Chart）
- ユーティリティ（truncate、visually-hidden、mono、text-right）
- モーション（150ms ease-out 基本、reduced-motion 完全対応）
- 既存コード撤廃方針

## 適合確認

- [x] 全色OKLCH、ハードコードなし
- [x] Noto Sans JP / Noto Sans Mono vendored
- [x] 8px ベースライングリッド
- [x] VS Code Dark+ 慣習（no uppercase th、border-radius 2-4px、visited purple）
- [x] accessible（`:focus-visible` outline、色+テキストの二重伝達）

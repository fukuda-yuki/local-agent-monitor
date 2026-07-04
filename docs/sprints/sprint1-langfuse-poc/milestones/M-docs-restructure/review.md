# Review

## レビュー範囲

- `AGENTS.md`
- `README.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/knowledge/`
- `docs/sprints/sprint1-langfuse-poc/milestones/`

## 変更ファイル

- docs 運用文書と milestone テンプレートのみ。

## 使用したサブエージェント

- なし。ドキュメント構造のみの軽微で可逆な変更のため、自己レビューで対応する。

## Raw findings summary

- 旧参照検索では `docs/sprints/sprint1-langfuse-poc/archive/review/` の過去レビュー本文に旧 path が残った。
- `docs/archive/` を除外した旧参照検索では該当なし。
- 現行手順を定義する `AGENTS.md`、`README.md`、`docs/task.md` は新構造を参照している。

## メインエージェントの妥当性判断

- archive 内の旧 path は履歴情報であり、現行手順の source of truth ではないため修正しない。
- GitHub Issue を既定作業単位にする記述は現行手順から除去済み。

## 対応方針

- 旧構造参照と GitHub Issue 既定運用の残存を `rg` で確認する。

## 適用した修正

- `AGENTS.md` の作業開始、source of truth、レビュー、完了条件を milestone 配下の文書へ切り替えた。
- `README.md` と `docs/task.md` を新しい docs 構造に合わせた。
- 旧トップレベル knowledge file を `docs/sprints/sprint1-langfuse-poc/knowledge/` 配下へ分割した。
- M11-M22 と docs 再編用 milestone の作業束を追加した。

## 残リスク

- 旧 archive 内の過去レビュー本文には古い path が残る。履歴として扱うため修正しない。
- 旧トップレベル knowledge file は要約分割しているため、過去の逐語的な作業ログは git 履歴または archive review を参照する。

## 保留事項

- なし。

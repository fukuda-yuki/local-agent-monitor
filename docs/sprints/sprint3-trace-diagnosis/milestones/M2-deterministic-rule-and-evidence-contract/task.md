# M2: deterministic rule and evidence contract

## 目的

Sprint3 の実装前に、diagnosis candidate を生成する deterministic rule、content-aware pattern、auto-decision rule、sensitive bundle read contract を確定する。

M2 は blocking documentation milestone であり、code behavior は変更しない。

## 完了条件

- [ ] `rule_id` の初期 set と適用条件を確定している。
- [ ] `decision_rule_id` の初期 set と適用条件を確定している。
- [ ] content-aware rule が読む span attribute、span event、tool result、prompt / response fragment の範囲を定義している。
- [ ] sensitive bundle の `manifest.json` と `evidence/*.json` schema version 1 を確定している。
- [ ] content fragment の粒度、`evidence_ref` から bundle への逆引き手順、TTL / 削除 policy を定義している。
- [ ] `auto-approved` が repository 修正を実行しない境界を確認している。
- [ ] M2 後に `docs/spec.md` へ反映する内容と sprint-local に留める内容を分けている。

## 検証

- Markdown link と表の ID が M1 `command-boundary.md` と矛盾しないことを確認する。
- `rg` で `auto-approved` が Sprint3 出力状態として残り、repository 修正実行とは結びついていないことを確認する。
- M2 は documentation-only のため build / test は必須ではない。

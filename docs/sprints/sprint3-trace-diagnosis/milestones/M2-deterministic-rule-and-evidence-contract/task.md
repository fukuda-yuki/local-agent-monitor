# M2: deterministic rule and evidence contract

## 目的

Sprint3 の実装前に、diagnosis candidate を生成する deterministic rule、content-aware pattern、auto-decision rule、M24-M27 接続方針、sensitive bundle read / manual delete contract を確定する。

M2 は blocking documentation milestone であり、code behavior は変更しない。

## 完了条件

- [x] `rule_id` の初期 set と適用条件を確定している。
- [x] `decision_rule_id` の初期 set と適用条件を確定している。
- [x] content-aware rule が読む span attribute、span event、tool result、prompt / response fragment の範囲を定義している。
- [x] content-aware rule の deterministic pattern を literal / regex / field predicate のレベルで定義し、曖昧な「error pattern」「sensitive pattern」のまま残していない。
- [x] 既存 M24-M27 command / schema を置換しない互換性維持方針と、Sprint3 candidate output から既存 human-review record へ渡す adapter / mapping contract を確定している。
- [x] sensitive bundle の `manifest.json` と `evidence/*.json` schema version 1 を確定している。
- [x] content fragment の粒度、`evidence_ref` から bundle への逆引き手順、TTL / manual deletion policy を定義している。
- [x] `auto-approved` が repository 修正を実行しない境界を確認している。
- [x] `auto-approved` の Sprint3 内の出口を Sprint4 planning handoff record または M5/M6 review evidence として定義し、実装 command へ自動接続しないことを確認している。
- [x] M2 後に `docs/spec.md` へ反映する内容と sprint-local に留める内容を分けている。

## 成果物

- [plan.md](plan.md)
- [rule-and-evidence-contract.md](rule-and-evidence-contract.md)
- [questions.md](questions.md)
- [notes.md](notes.md)
- [review.md](review.md)

## 検証

- Markdown link と表の ID が M1 `command-boundary.md` と矛盾しないことを確認する。
- `rg` で `auto-approved` が Sprint3 出力状態として残り、repository 修正実行とは結びついていないことを確認する。
- `rg` で M1 の暫定 content-aware pattern 表現が確定仕様として残っていないことを確認する。
- M2 は documentation-only のため build / test は必須ではない。

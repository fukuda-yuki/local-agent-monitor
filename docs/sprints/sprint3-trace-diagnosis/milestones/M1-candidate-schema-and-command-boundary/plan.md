# M1 Plan

## 方針

M1 では、Sprint3 の candidate pipeline を既存の human-review pipeline から分離して定義する。
既存の M24 `validate-diagnoses`、M25 `generate-improvement-proposals`、M27 `record-human-decisions` は互換性維持対象とし、直接 schema を拡張しない。

## 実装前境界

M2 以降の実装 agent は、以下の順で進める。

1. M2 で deterministic rule、content-aware evidence contract、M24-M27 adapter / mapping contract、sensitive bundle read / manual delete contract を確定する。
2. M3 で `generate-diagnosis-candidates` を実装する。
3. M4 で `generate-improvement-candidates` と `generate-auto-decisions` を実装する。
4. M5 で M2 の adapter / mapping contract を反映し、candidate pipeline と M24-M27 human-review pipeline の接続を残す。
5. M6 でユーザー協業の redacted real-trace E2E を実施する。

M3 の最小実装は `generate-diagnosis-candidates` の synthetic fixture 対応に限定できる。
`generate-auto-decisions` は `auto-approved`、`needs-human-review`、`blocked` を出力する。
`auto-approved` は repository 修正を実行せず、Sprint4 planning に記録できる判断 record に留める。

## 検証方針

自動検証は repository 内の synthetic fixture だけで完結させる。
redacted real-trace E2E は M6 の手動ライブ確認として扱う。
実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含む live trace は repository に保存しない。

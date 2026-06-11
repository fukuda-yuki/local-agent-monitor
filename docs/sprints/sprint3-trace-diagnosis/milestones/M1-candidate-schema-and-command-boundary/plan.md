# M1 Plan

## 方針

M1 では、Sprint3 の candidate pipeline を既存の human-review pipeline から分離して定義する。
既存の M24 `validate-diagnoses`、M25 `generate-improvement-proposals`、M27 `record-human-decisions` は互換性維持対象とし、直接 schema を拡張しない。

## 実装前境界

M2 以降の実装 agent は、以下の順で進める。

1. `generate-diagnosis-candidates`
2. `generate-improvement-candidates`
3. `generate-auto-decisions`

M2 の最小実装は `generate-diagnosis-candidates` の synthetic fixture 対応に限定できる。
`generate-improvement-candidates` と `generate-auto-decisions` は M3 以降に分けてもよい。

## 検証方針

自動検証は repository 内の synthetic fixture だけで完結させる。
実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含む live trace は手動確認だけで扱い、repository に保存しない。

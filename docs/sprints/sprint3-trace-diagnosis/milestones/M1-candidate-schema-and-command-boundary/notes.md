# M1 Notes

## 2026-06-12: candidate pipeline split

- Candidate pipeline は既存 human-review pipeline の前段として扱う。
- M24 / M25 / M27 の既存 command と schema は互換性維持対象であり、Sprint3 candidate schema を直接混ぜない。
- Candidate output は後続で M24 diagnosis record、M25 proposal record、M27 human decision record へ変換または接続できる形にする。
- Sensitive full content は standard CSV / JSON に直接広げず、sensitive bundle file に分離する。
- Standard candidate output は sensitive bundle への path / reference を持つ。
- `generate-auto-decisions` は Sprint3 で `auto-approved`、`needs-human-review`、`blocked` を出力する。
- `auto-approved` は repository 修正を実行せず、後続の実装計画へ渡せる判断 record に留める。

## 2026-06-12: selected command names

- `generate-diagnosis-candidates`
- `generate-improvement-candidates`
- `generate-auto-decisions`

These names are sprint-local decisions for Sprint3 M1 and should be reflected into `docs/spec.md` before code implementation begins.

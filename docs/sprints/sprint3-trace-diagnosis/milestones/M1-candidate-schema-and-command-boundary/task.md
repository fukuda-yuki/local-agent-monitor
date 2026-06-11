# M1: candidate schema and command boundary

## 目的

Sprint3 の実装前に、diagnosis candidate、improvement proposal candidate、auto-decision の command 境界、入力、出力列、sensitive output 保存先、synthetic fixture 方針を sprint-local に確定する。

M1 は documentation milestone であり、code behavior は変更しない。
M1 の決定は、後続の M2 実装前に `docs/spec.md` へ反映する候補として扱う。

## 完了条件

- [x] candidate 専用 command を M24 / M25 / M27 の既存 command と分ける方針を明記している
- [x] diagnosis candidate、improvement proposal candidate、auto-decision の command 名を決めている
- [x] 各 command の入力と出力列を決めている
- [x] sensitive output の既定保存先と repository 保存禁止を明記している
- [x] synthetic fixture の方針を決めている
- [x] 実装着手前に残る open question を限定している
- [x] 起動確認済みの前提を記録している

## タスク分解

1. `docs/requirements.md`、`docs/spec.md`、`docs/task.md`、Sprint3 README を確認し、M1 が source of truth と衝突しないことを確認する。
2. `command-boundary.md` に command 名、入力、出力列、sensitive output、fixture 方針を定義する。
3. Sprint3 README に M1 milestone と決定事項へのリンクを追加する。
4. `notes.md` に M1 の決定を記録する。
5. `review.md` に source-of-truth 境界と残リスクを記録する。

## 検証

- M1 は documentation-only のため build / test は必須ではない。
- Markdown のリンク先が存在することを確認する。
- `docs/spec.md` に未確定 schema detail を追加していないことを確認する。
- Sprint3 の実装 agent が、M2 でどの command から着手するか判断できることを確認する。

## 検証記録

- 2026-06-12: Config CLI help、solution build、Aspire AppHost start / stop は Sprint3 README と review に記録済み。
- 2026-06-12: M1 では code behavior を変更しないため、追加 build / test は実行しない。

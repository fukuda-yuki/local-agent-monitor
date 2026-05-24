# M11: 研究計画書とのスコープ整合

## 目的

研究実施計画書と `docs/requirements.md` / `docs/spec.md` のスコープを照合し、M11 以降で扱う研究計測化の境界を確定する。

## 完了条件

- [x] 研究実施計画書と現行 requirements / spec の差分を確認する
- [x] 観測基盤として維持する M0-M10 の扱いを明記する
- [x] 研究計測化で扱う対象と非対象を `docs/spec.md` に反映する
- [x] 後続 M12-M22 に影響する未決事項を `questions.md` または `notes.md` に記録する
- [x] 必要なレビューを `review.md` に記録する

## 検証記録

- 2026-05-24: 研究実施計画書と `docs/requirements.md` / `docs/spec.md` のスコープ差分を確認した。
- 2026-05-24: `rg -n "自動採用|自動実装|勝敗|Copilot CLI|VS Code Copilot Chat|M11|M12|M23" docs` で文書整合を確認した。
- 2026-05-24: documentation-only のため、`dotnet build` / `dotnet test` は実行対象外とした。

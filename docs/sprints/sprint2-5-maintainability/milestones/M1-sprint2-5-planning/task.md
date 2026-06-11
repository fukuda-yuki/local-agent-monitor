# M1: Sprint2.5 planning

## 目的

Sprint2.5 の実装前に、ConfigCli の分割方針、責務境界、過剰分割禁止、redacted real-trace E2E の安全条件を明文化する。

M1 は documentation milestone であり、code behavior は変更しない。

## 完了条件

- [x] `docs/spec.md` に Sprint2.5 の目的、非目的、分割方針、redacted E2E 方針が反映されている
- [x] Sprint2.5 README に milestone table、Done Definition、AppHost follow-up boundary が記録されている
- [x] `Program.cs` 分割方針として 1 ファイル 1 主型を明記している
- [x] `*ParseResult`、`*Defaults`、密結合 companion type は親ファイルに同居させる方針を明記している
- [x] `*ParseResult.cs` や `*Defaults.cs` を機械的に単独生成しないことを明記している
- [x] redacted E2E で repository に保存してはいけない情報を明記している
- [x] Sprint3 は Sprint2.5 完了後の候補として扱われている

## タスク分解

1. `docs/spec.md`、`docs/task.md`、Sprint2 / Sprint3 sprint docs を確認し、Sprint2.5 の置き場と既存仕様との衝突がないことを確認する。
2. `docs/spec.md` に Sprint2.5 限定の保守性改善と redacted real-trace E2E 方針を追加する。
3. `docs/task.md` に Sprint2.5 を Sprint3 前の計画中 sprint として追加する。
4. Sprint2.5 README と M1-M5 の task を作成する。
5. 変更後に、`docs/requirements.md` を変更しない判断が妥当かを再確認する。

## 検証

- Markdown のリンク先が存在することを確認する。
- `docs/spec.md` の「実データ検証は既定スコープ外」という既存方針と、Sprint2.5 の redacted E2E 方針が矛盾していないことを確認する。
- 文書変更のみのため build / test は必須ではない。ただし、実装 milestone では M2 以降に build / test を必須とする。

## 検証記録

- 2026-06-11: `docs/spec.md` に Sprint2.5 の現在フェーズ、scope / non-scope、ConfigCli 分割方針、CSV / JSON helper 集約方針、redacted real-trace E2E 方針、AppHost follow-up boundary を追加した。
- 2026-06-11: `docs/task.md` に Sprint2.5 を Sprint3 前の計画中 sprint として追加し、Sprint3 を Sprint2.5 完了後の候補として維持した。
- 2026-06-11: Sprint2.5 README と M1-M5 task を作成し、README から各 milestone task へのリンク先が存在することを確認した。
- 2026-06-11: 文書変更のみのため build / test は実行していない。M2 以降の code milestone では `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` を必須検証とする。

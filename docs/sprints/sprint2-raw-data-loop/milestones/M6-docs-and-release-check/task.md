# M6: Sprint2 docs and release check

## 目的

Sprint2 MVP 実装と automated verification が完了した後、利用者向け文書と最終レビューを更新し、Sprint2 を完了できる状態にする。

M6 は実装済みの動作を文書化する段階であり、M2-M5 の挙動を先取りして README / getting-started に書かない。

## 完了条件

- [ ] `README.md` に Langfuse 非依存 raw data loop の入口を追加する
- [ ] `docs/getting-started.md` に `ingest-raw` / `normalize-raw` / Langfuse 非依存 loop の synthetic 手順を追加する
- [ ] user-facing docs に `data/raw-store.db`、temp output、raw payload の削除手順を追加する
- [ ] `docs/spec.md` 5.17 と実装済み CLI behavior の差分がないことを確認する
- [ ] `docs/requirements.md` 8.3 と Sprint2 README の差分がないことを確認する
- [ ] Sprint2 README の milestone 状態を実績に合わせて更新する
- [ ] `docs/task.md` の Sprint2 状態を更新する
- [ ] `dotnet build CopilotAgentObservability.slnx` を実行する
- [ ] `dotnet test CopilotAgentObservability.slnx` を実行する
- [ ] Sprint2 全体レビューを `review.md` に記録する

## タスク分解

1. M2-M5 の検証記録と実装済み CLI behavior を確認する。
2. user-facing docs に synthetic-only 手順と local raw store cleanup 手順を追加し、実 credential / secret / Base64 header / 実 user identity を含めない。
3. requirements / spec / sprint docs / implementation / tests の整合をチェックする。
4. 残リスクと Sprint3 以降の候補を分離して記録する。

## 検証記録

- 未実施。

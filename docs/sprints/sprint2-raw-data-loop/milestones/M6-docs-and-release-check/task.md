# M6: Sprint2 docs and release check

## 目的

Sprint2 MVP 実装と automated verification が完了した後、利用者向け文書と最終レビューを更新し、Sprint2 を完了できる状態にする。

M6 は実装済みの動作を文書化する段階であり、M2-M5 の挙動を先取りして README / getting-started に書かない。

## 完了条件

- [x] `README.md` に Langfuse 非依存 raw data loop の入口を追加する
- [x] `docs/getting-started.md` に `ingest-raw` / `normalize-raw` / Langfuse 非依存 loop の synthetic 手順を追加する
- [x] user-facing docs に `data/raw-store.db`、temp output、raw payload の削除手順を追加する
- [x] `docs/spec.md` 5.17 と実装済み CLI behavior の差分がないことを確認する
- [x] `docs/requirements.md` 8.3 と Sprint2 README の差分がないことを確認する
- [x] Sprint2 README の milestone 状態を実績に合わせて更新する
- [x] `docs/task.md` の Sprint2 状態を更新する
- [x] `dotnet build CopilotAgentObservability.slnx` を実行する
- [x] `dotnet test CopilotAgentObservability.slnx` を実行する
- [x] Sprint2 全体レビューを `review.md` に記録する

## タスク分解

1. M2-M5 の検証記録と実装済み CLI behavior を確認する。
2. user-facing docs に synthetic-only 手順と local raw store cleanup 手順を追加し、実 credential / secret / Base64 header / 実 user identity を含めない。
3. requirements / spec / sprint docs / implementation / tests の整合をチェックする。
4. 残リスクと Sprint3 以降の候補を分離して記録する。

## 検証記録

- 2026-06-08: `docs/spec.md` 5.17、`docs/requirements.md` 8.3、Sprint2 README、Config CLI command dispatch / help text、`LangfuseIndependentLoopTests.EndToEnd_RawStoreThroughHumanDecision_UsesSyntheticFixturesOnly` を照合した。`ingest-raw` / `normalize-raw` / downstream workflow は仕様どおり分離され、`aggregate-measurements` は Langfuse export adapter として維持されている。
- 2026-06-08: M5 E2E test は `raw-otlp.synthetic.json` と `m5-diagnoses.synthetic.json` のみを使い、`ingest-raw -> normalize-raw -> validate-diagnoses -> generate-improvement-proposals -> evaluate-improvement-proposals -> generate-decision-template -> record-human-decisions` を temp SQLite DB と temp output で通していることを確認した。
- 2026-06-08: `dotnet build CopilotAgentObservability.slnx` 成功。warning 0、error 0。NETSDK1057 は preview .NET SDK 使用の informational message として表示された。
- 2026-06-08: `dotnet test CopilotAgentObservability.slnx` 成功。159 tests passed、0 failed、0 skipped。NETSDK1057 は preview .NET SDK 使用の informational message として表示された。

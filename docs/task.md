# Sprint Index

この文書は repository 全体の sprint / roadmap index である。

プロダクト仕様・実装判断の正は `docs/spec.md` である。
ただし、`docs/requirements.md` を上位要件とし、`docs/spec.md` はその詳細仕様として扱う。
GitHub Issue は既定の作業単位にしない。ユーザーが明示的に作成・参照を指示した場合だけ使う。

## Sprint Index

| Sprint | 状態 | 概要 / 詳細 |
| --- | --- | --- |
| Sprint1: Langfuse PoC | 完了 | [docs/sprints/sprint1-langfuse-poc/](sprints/sprint1-langfuse-poc/) に M0-M28 と user-facing docs refresh までの PoC 資料を集約した |
| Sprint2: Raw Data Loop | 仕様化中 | [docs/sprints/sprint2-raw-data-loop/](sprints/sprint2-raw-data-loop/) で raw telemetry store、normalized dataset、Langfuse 非依存改善ループの正式仕様化を開始した |

## Roadmap

- Sprint1 は完了済みの参照資料として扱う。
- Sprint2 は M1 で正式仕様化を開始している。
- Sprint2 の実装に入る場合は、先に `docs/requirements.md` と `docs/spec.md` へ必要な仕様変更を反映する。

## Follow-up

- raw JSON の保持基盤を SQLite 既定候補、PostgreSQL 将来候補として検討する。
- raw store から normalized dataset を生成し、既存の measurement schema と改善支援 CLI に接続する。
- Langfuse UI は source of truth ではなく dashboard / trace viewer として再位置づける。
- 共有環境、実データ、社内サーバー検証が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。

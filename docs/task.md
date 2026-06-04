# Sprint Index

この文書は repository 全体の sprint / roadmap index である。

プロダクト仕様・実装判断の正は `docs/spec.md` である。
ただし、`docs/requirements.md` を上位要件とし、`docs/spec.md` はその詳細仕様として扱う。
GitHub Issue は既定の作業単位にしない。ユーザーが明示的に作成・参照を指示した場合だけ使う。

## Sprint Index

| Sprint | 状態 | 概要 / 詳細 |
| --- | --- | --- |
| Sprint1: Langfuse PoC | 完了 | [docs/sprints/sprint1-langfuse-poc/](sprints/sprint1-langfuse-poc/) に M0-M28 と user-facing docs refresh までの PoC 資料を集約した |
| Sprint2: Raw Data Loop | 仕様化中 | [docs/sprints/sprint2-raw-data-loop/](sprints/sprint2-raw-data-loop/) で raw OTLP file ingest、SQLite raw store、normalized dataset、Langfuse 非依存改善ループの MVP 仕様化を進めている |
| Sprint3: Trace Diagnosis | 候補 | [docs/sprints/sprint3-trace-diagnosis/](sprints/sprint3-trace-diagnosis/) に trace からの自動診断候補を後続要求として記録する |

## Roadmap

- Sprint1 は完了済みの参照資料として扱う。
- Sprint2 は M1 で MVP 仕様を `docs/requirements.md` と `docs/spec.md` に反映している。
- Sprint2 の実装に入る場合は、M1 の後続 milestone と task breakdown を作成してから着手する。
- Sprint3 は候補であり、Sprint2 MVP に trace からの自動診断を含めない判断を忘れないための置き場として扱う。

## Follow-up

- raw OTLP の file-based ingest と SQLite raw store を Sprint2 MVP として実装する。
- raw store から normalized dataset を生成し、既存の measurement schema と改善支援 CLI に接続する。
- Langfuse UI は source of truth ではなく dashboard / trace viewer の optional side path として扱う。
- trace から failure category / anti-pattern 候補を自動抽出する診断機能は Sprint3 候補として別途仕様化する。
- 共有環境、実データ、社内サーバー検証が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。

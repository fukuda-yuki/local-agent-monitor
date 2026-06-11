# Sprint Index

この文書は repository 全体の sprint / roadmap index である。

プロダクト仕様・実装判断の正は `docs/spec.md` である。
ただし、`docs/requirements.md` を上位要件とし、`docs/spec.md` はその詳細仕様として扱う。
GitHub Issue は既定の作業単位にしない。ユーザーが明示的に作成・参照を指示した場合だけ使う。

## Sprint Index

| Sprint | 状態 | 概要 / 詳細 |
| --- | --- | --- |
| Sprint1: Langfuse PoC | 完了 | [docs/sprints/sprint1-langfuse-poc/](sprints/sprint1-langfuse-poc/) に M0-M28 と user-facing docs refresh までの PoC 資料を集約した |
| Sprint2: Raw Data Loop | 完了 | [docs/sprints/sprint2-raw-data-loop/](sprints/sprint2-raw-data-loop/) で raw OTLP file ingest、SQLite raw store、normalized dataset、Langfuse 非依存改善ループ、docs and release check まで完了した |
| Sprint2.5: ConfigCli Maintainability | 完了 | [docs/sprints/sprint2-5-maintainability/](sprints/sprint2-5-maintainability/) で ConfigCli 保守性改善、CSV / JSON 共通処理集約、redacted real-trace E2E 確認、regression review and closeout まで完了した |
| Sprint3: Content-aware Trace Diagnosis and Auto-decision Foundation | 要件定義中 | [docs/sprints/sprint3-trace-diagnosis/](sprints/sprint3-trace-diagnosis/) で trace content を含む deterministic 診断候補生成、改善候補生成、自動採用判断を含む auto-decision record の基盤を定義する |

## Roadmap

- Sprint1 は完了済みの参照資料として扱う。
- Sprint2 は M1 で MVP 仕様を `docs/requirements.md` と `docs/spec.md` に反映し、M2-M6 の後続 milestone と task breakdown を作成している。
- Sprint2 は M2 raw store 基盤、M3 raw OTLP ingest、M4 raw normalization、M5 Langfuse 非依存 loop、M6 docs and release check まで完了している。
- Sprint2.5 は GitHub Issue #24 を正式な参照元とし、Sprint3 に進む前の技術負債整理として完了した。
- Sprint2.5 は M1 planning、M2 ConfigCli responsibility split、M3 output helper consolidation、M4 redacted real-trace E2E、M5 regression review and closeout まで完了している。
- Sprint3 は要件定義中であり、Sprint2 MVP と Sprint2.5 で除外した trace からの自動診断候補生成を、deterministic rule、content-aware evidence、改善候補生成、自動採用判断を含む auto-decision record まで拡張して扱う。
- Sprint3 M1 は `candidate-schema-and-command-boundary` とし、candidate 専用 command / schema、auto-decision 専用 schema、sensitive output 保存先、synthetic fixture 方針を sprint-local に確定した。
- Sprint3 M2-M5 は、deterministic rule / evidence contract、diagnosis candidate 実装、improvement / auto-decision 実装、M24-M27 human-review pipeline 接続を扱う。
- 実 repository 修正を伴う自動改善実装は Sprint3 の既定スコープ外とし、Sprint4 以降の候補として安全境界を定義してから扱う。

## Follow-up

- Langfuse UI は source of truth ではなく dashboard / trace viewer の optional side path として扱う。
- Sprint3 では、trace から failure category / anti-pattern 候補を deterministic に自動抽出し、改善候補と自動採用判断を含む auto-decision record に接続する。
- Sprint3 の sensitive local output は、明示 opt-in 時に実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含められる。ただし repository には保存しない。
- 共有環境、実データ、社内サーバー検証が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。
- AppHost ローカルランチャー化は Sprint2.5 の対象外であり、扱う場合は先に `docs/spec.md` 9 の AppHost 方針を再確認する。

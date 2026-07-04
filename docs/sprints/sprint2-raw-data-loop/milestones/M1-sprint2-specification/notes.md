# M1 Notes

## 2026-06-04 Sprint2 readiness check

- `docs/requirements.md` は、Sprint2 で Langfuse に依存しない raw JSON 保持基盤を検討するとしているが、正式採用時は同文書の更新が必要としている。
- `docs/spec.md` は、Sprint2 の schema、migration、CLI interface、運用手順を正式仕様として未確定としている。
- `docs/sprints/sprint2-raw-data-loop/README.md` の raw record は候補であり、実装 schema ではない。
- 既存 Config CLI は Langfuse export 風の sanitized JSON から measurement CSV / JSON を生成できる。
- 既存改善支援 loop は diagnosis record 入力以降を deterministic に扱うが、raw trace または measurement から diagnosis を自動生成する実装はない。
- Config CLI project は現時点で追加 package dependency を持たないため、SQLite 採用は明示的な dependency 判断を伴う。
- AppHost は空であり、現行仕様では raw store / DB / worker を AppHost に追加しない方針である。

## 2026-06-05 User answers

- Sprint2 MVP では、`diagnose` は引き続き人間分類 diagnosis record の validation とする。
- trace から failure category / anti-pattern 候補を自動抽出する診断機能は Sprint2 MVP に含めない。
- trace からの自動診断は Sprint3 あたりの後続要求として忘れないように、`docs/sprints/sprint3-trace-diagnosis/` に候補メモを作成する。

## 2026-06-05 Sprint2 M1 decision draft

この section は、`questions.md` へのユーザー回答をもとにした M1 の decision draft である。
2026-06-05 に `docs/requirements.md` と `docs/spec.md` へ反映済みである。
実装時の正式判断は `docs/requirements.md` と `docs/spec.md` を優先する。

### 推奨判断

- Sprint2 MVP の既定入力は raw OTLP の file-based ingest とする。
  - Langfuse export は Langfuse 稼働と API key を前提にするため、Langfuse 非依存という Sprint2 の目的に合わない。
  - Collector output は将来の組織展開候補として有用だが、MVP では構成要素が増える。
  - raw OTLP はクライアントが出した payload に最も近く、後続の normalization 方針を後から調整しやすい。
- Sprint2 MVP では自前 HTTP receiver を含めず、保存済み raw OTLP JSON を `config-cli` で取り込む file-based ingest を優先する。
- raw store は SQLite をローカル PoC の既定候補として採用する。
  - server process 不要で、削除は DB file の削除で済む。
  - `Microsoft.Data.Sqlite` などの dependency 追加を伴うため、実装前に `docs/spec.md` へ明記する。
- SQLite DB file の既定 path は `data/raw-store.db` を候補とし、`data/` または DB file は repository に commit しない。
- migration は MVP では schema version 1 のみとし、migration tool は追加しない。
  - 破壊的変更が必要になった場合は、PoC の範囲では DB file 再作成を許容する。
- raw record schema は、Sprint2 README の候補を土台にする。
  - `id` は MVP では SQLite の `INTEGER PRIMARY KEY AUTOINCREMENT` で十分とする。
  - `received_at` は raw store に取り込んだ時刻とし、payload 内の span start / end time とは区別する。
  - MVP の index は `trace_id`、`received_at`、`source` を候補にする。
- PoC では raw payload の全量保存を許容する。
  - raw store は repository に commit しない。
  - 実データ、顧客データ、credential、secret を含む payload は検証入力に使わない。
  - masking / redaction は Sprint2 MVP のスコープ外とし、共有環境または実データ検証前に別途仕様化する。
- raw store から normalized dataset へ変換する command は、既存 `aggregate-measurements` と分ける。
  - `aggregate-measurements` は Langfuse export 風 JSON 向けとして維持する。
  - 新 command 名は `normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]` を候補とする。
- raw store への取り込み command は `ingest-raw <raw.json> --db <raw-store.db>` を候補とする。
- Sprint2 MVP の `diagnose` は、引き続き人間分類 diagnosis record の validation とする。
  - trace からの自動診断は Sprint2 MVP に含めず、Sprint3 候補として扱う。
- Langfuse が起動していない状態で成功とみなす最小 loop は以下とする。

```text
ingest-raw -> normalize-raw -> validate-diagnoses -> generate-improvement-proposals -> evaluate-improvement-proposals -> generate-decision-template / record-human-decisions
```

- Langfuse への投入は optional side path として扱う。
- 最小 loop の検証は synthetic fixture で完結させ、live Copilot / live Langfuse は手動確認として分離する。
- README / getting-started に Langfuse 非依存フローを載せるのは、Sprint2 MVP の実装と automated verification が完了した後にする。

### 反映済みの文書

- `docs/requirements.md`
  - raw JSON / raw OTLP store を Sprint2 MVP の対象にすること。
  - SQLite をローカル PoC の既定 raw store とし、PostgreSQL を将来候補に留めること。
  - Sprint2 MVP では trace からの自動診断を含めないこと。
- `docs/spec.md`
  - Sprint2 MVP の入力、raw record schema、DB path、migration 方針、CLI interface、data handling、validation 方針。
  - `aggregate-measurements` と `normalize-raw` の責務分離。
  - Langfuse なしで動く最小 loop。
- `.gitignore`
  - `data/` は既存 `.gitignore` で ignore 済みのため追加変更は不要。

# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-06-04: Sprint2 M1 start

### 仕様整合

- `docs/task.md` と Sprint2 README は Sprint2 を仕様化中として扱い、実装可能な確定仕様とはしていない。
- README は schema、migration、CLI interface、運用手順を `docs/requirements.md` と `docs/spec.md` へ反映してから確定するとしている。
- M1 task は product behavior を未確定のままにし、次の作業を仕様化と milestone 分割としている。

### テストと検証

- documentation-only のため、実行コードは変更していない。
- 開始前調査の検証記録として、`dotnet test CopilotAgentObservability.slnx` 121 件成功と、`dotnet build CopilotAgentObservability.slnx` の単独再実行成功を `task.md` に記録した。

### 保守性

- M1 は既存 Sprint1 milestone と同じく `task.md`、`questions.md`、`notes.md`、`review.md` を使う。
- 未決事項を `questions.md` に分離し、実装 agent が Sprint2 の idea notes を確定仕様として扱わないようにした。

### 結果

Sprint2 を仕様化タスクとして開始するうえでの blocking issue は見つからなかった。

## 2026-06-04: 並列 sub-agent レビュー後の再レビュー

### 実施内容

source-of-truth 整合と、M1 start package の使いやすさの 2 観点で read-only sub-agent レビューを実施した。
Main-Agent は各指摘を `docs/requirements.md`、`docs/spec.md`、M1 の目的に照らして評価し、妥当な指摘のみ修正した。

### 採用した指摘

- 指摘: Sprint2 README の `diagnose` 説明が、trace から failure category / anti-pattern 候補を抽出する確定 behavior のように読め、現行 `docs/spec.md` の M24 境界と衝突する。
  - 対応: `diagnose` は人間が分類した diagnosis record の検証を既定説明とし、trace からの自動診断を Sprint2 MVP に含めるかは M1 で確定する表現へ修正した。
- 指摘: Sprint2 README と `docs/task.md` の一部が、raw store 中心の構成や Langfuse 再位置づけを確定済み方針のように読める。
  - 対応: README の目的、改善ループ、Langfuse UI、`docs/task.md` の follow-up を「検討する」表現へ弱めた。
- 指摘: M1 task は完了条件は明確だが、次の agent が最初に何をするかが暗黙だった。
  - 対応: `task.md` に「次に行うこと」を追加し、`questions.md` の判断を `notes.md` に記録し、確定内容を requirements / spec に反映し、実装は後続 milestone 作成後に開始する流れを明記した。

### 再レビュー結果

M1 docs は Sprint2 を確定仕様ではなく仕様化中の作業として扱っている。
raw store、normalized dataset、Langfuse 非依存 loop、trace からの自動診断は M1 で確定する論点として残っており、実装 agent が未確定事項を product behavior として扱うリスクは低減された。

### 残リスク

- M1 ではまだ `docs/requirements.md` と `docs/spec.md` を更新していないため、Sprint2 実装は開始できない。
- raw payload の保存範囲、retention、sanitization、SQLite dependency 追加可否は M1 の後続判断に依存する。

## 2026-06-04: 修正後 sub-agent 再レビュー

### 実施内容

前回指摘を修正した後、同じ 2 つの read-only sub-agent に再レビューを依頼した。

### 結果

- source-of-truth 整合レビュー: 残存 finding なし。前回の blocking finding だった `diagnose` の自動診断誤読リスクは解消済みと判断された。
- M1 start package レビュー: 残存 finding なし。README と `docs/task.md` の raw store / Langfuse UI 表現は「検討する」に揃い、未確定事項として読めると判断された。

### 残リスク

追加の残リスクはない。
M1 の本来の未決事項は `questions.md` に残しており、実装前に requirements / spec へ反映する必要がある。

## 2026-06-05: Sprint3 trace diagnosis 候補メモ追加レビュー

### レビュー範囲

- `docs/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M1-sprint2-specification/notes.md`
- `docs/sprints/sprint3-trace-diagnosis/README.md`

### 指摘と対応

- 指摘: trace からの自動診断を Sprint2 MVP に混ぜると、既存の `diagnose` validation workflow と責務が混ざる。
  - 対応: Sprint2 MVP では `diagnose` を人間分類 diagnosis record の validation に留め、trace からの自動診断は Sprint3 候補として分離した。
- 指摘: 後続候補として残す場合も、自動採用や repository 修正につながる誤読を避ける必要がある。
  - 対応: Sprint3 候補メモに、出力は人間分類の補助候補であり、自動採用、自動改善実装、repository 修正、patch / diff 生成、commit / push / pull request 作成、自動勝敗決定は非スコープであることを明記した。

### 妥当性判断

今回の変更は documentation-only であり、実装、public CLI、schema、依存関係は変更していない。
Sprint3 は候補 sprint として記録しただけで、`docs/requirements.md` と `docs/spec.md` の正式仕様は変更していない。

### 残リスク

Sprint3 の正式化は、Sprint2 の raw store / normalize 実装と M1 の requirements / spec 更新後に改めて判断する必要がある。

## 2026-06-05: Sprint2 M1 decision draft レビュー

### レビュー範囲

- `docs/sprints/sprint2-raw-data-loop/milestones/M1-sprint2-specification/notes.md`

### 観点

- `questions.md` の未決事項に対するユーザー回答が decision draft として記録されていること。
- `docs/requirements.md` と `docs/spec.md` に反映する前の正式仕様として誤読されないこと。
- Sprint2 MVP に trace からの自動診断、自前 HTTP receiver、masking / redaction、README / getting-started 更新を先取りしていないこと。

### 妥当性判断

decision draft は、raw OTLP file-based ingest、SQLite raw store、`ingest-raw` / `normalize-raw`、既存 `aggregate-measurements` との責務分離、Langfuse なしの最小 loop を整理している。
同時に、この時点では `docs/requirements.md` と `docs/spec.md` への反映前であることを明記していた。

### 残リスク

- SQLite dependency、DB path、raw record schema、CLI interface はまだ正式仕様に反映していない。
- `.gitignore` の更新は実装時に行う必要がある。

## 2026-06-05: Sprint2 MVP requirements / spec 反映レビュー

### レビュー範囲

- `docs/requirements.md`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint2-raw-data-loop/README.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M1-sprint2-specification/task.md`

### 指摘と対応

- 指摘: M1 decision draft のままでは Sprint2 MVP を実装できない。
  - 対応: raw OTLP file-based ingest、SQLite raw store、raw record schema、`ingest-raw` / `normalize-raw`、Langfuse 非依存 loop、data handling、diagnosis boundary を `docs/requirements.md` と `docs/spec.md` に反映した。
- 指摘: Sprint2 README が候補表現のままだと、requirements / spec 反映後の状態とずれる。
  - 対応: README を MVP 方針と成功条件の概要に更新し、正式判断は requirements / spec を優先する旨を残した。

### 妥当性判断

今回の変更は documentation-only であり、実装、public CLI 実体、依存関係は変更していない。
Sprint2 MVP の実装前に必要な主要な product / interface / data handling 判断は source of truth に反映された。
`data/` は既存 `.gitignore` で ignore 済みであるため、追加変更は不要と判断した。

### 残リスク

- この時点では Sprint2 の後続 milestone と task breakdown は未作成だった。
- `ingest-raw` / `normalize-raw`、SQLite dependency、raw store schema は未実装である。

## 2026-06-05: Sprint2 後続 milestone / task breakdown レビュー

### レビュー範囲

- `docs/requirements.md`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint2-raw-data-loop/README.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M1-sprint2-specification/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M2-raw-store-foundation/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M3-raw-otlp-ingest/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M4-raw-normalization/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M5-langfuse-independent-loop/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M6-docs-and-release-check/task.md`

### 要件定義との照合

| 要件 / 仕様 | 対応 milestone | 妥当性判断 |
| --- | --- | --- |
| `docs/requirements.md` 8.3 の raw OTLP file-based ingest | M3 | 自前 HTTP receiver、常駐 process、独自 OTLP receiver を明示的に非スコープにしている |
| `docs/requirements.md` 8.3 の SQLite raw store / PostgreSQL 将来候補 | M2 | SQLite schema version 1 のみを扱い、PostgreSQL を実装対象にしていない |
| `docs/spec.md` 5.17 の raw record schema と index | M2 | 固定列、source 値域、index、migration なしを完了条件にしている |
| `docs/spec.md` 5.17 の `ingest-raw` CLI | M3 | raw OTLP JSON file と temp SQLite DB の deterministic tests を完了条件にしている |
| `docs/spec.md` 5.17 の `normalize-raw` CLI | M4 | raw store / raw JSON input、M12 measurement schema、M15 / M16 集計方針、`aggregate-measurements` との責務分離を完了条件にしている |
| Langfuse 非依存 loop | M5 | `validate-diagnoses` 以降の既存 workflow への synthetic E2E 接続を完了条件にしている |
| data handling / repository 保存禁止 | M2-M6 | synthetic fixture、temp DB、credential / secret / Base64 header / 実 identity 排除を各 milestone に含めている |
| trace からの自動診断除外 | M5 | `diagnose` は人間分類 diagnosis record validation に留めることを完了条件にしている |
| README / getting-started 更新タイミング | M6 | MVP 実装と automated verification 完了後に user-facing docs を更新する順序にしている |

### 指摘と対応

- 指摘: `ingest-raw` と SQLite raw store を同じ milestone にすると、DB schema と CLI parsing の失敗原因が混ざる。
  - 対応: M2 を raw store 基盤、M3 を ingest CLI に分離した。
- 指摘: `normalize-raw` は既存 `aggregate-measurements` と責務が近く、回帰リスクがある。
  - 対応: M4 の完了条件に `aggregate-measurements` を変更しないことと既存 tests の regression 確認を入れた。
- 指摘: Langfuse 非依存 loop の確認が user-facing docs 更新より前に必要である。
  - 対応: M5 を E2E synthetic loop、M6 を docs and release check に分離した。

### 妥当性判断

M2-M6 は `docs/requirements.md` と `docs/spec.md` 5.17 の順序に沿っており、未確定の product behavior を追加していない。
各 milestone は実装範囲、非スコープ、検証方法を明記しているため、次の agent が迷いやすい点は M1 時点より減っている。
今回の変更は documentation-only であり、実装、public CLI 実体、依存関係は変更していない。

### 残リスク

- SQLite dependency の追加方法は M2 実装時に project file と lockfile 影響を確認する必要がある。
- raw OTLP の実 shape は synthetic fixture で最小確認するため、live Copilot / Collector output の網羅性は Sprint2 MVP 後の追加検証に残る。
- README / getting-started の利用者向け更新は M6 まで行わない。

## 2026-06-05: Sprint2 pre-implementation debt review

### レビュー範囲

- `docs/requirements.md`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint2-raw-data-loop/README.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M2-raw-store-foundation/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M3-raw-otlp-ingest/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M4-raw-normalization/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M5-langfuse-independent-loop/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M6-docs-and-release-check/task.md`
- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/MeasurementAggregationTests.cs`

### 指摘と対応

- 指摘: raw OTLP ingest の synthetic fixture が Langfuse export 風 `traces` schema で代用されると、Sprint2 の raw OTLP adapter が実入力とずれる負債になる。
  - 対応: M3 に `resourceSpans` / `scopeSpans` / `spans` envelope と OTLP attribute value shape を使う完了条件を追加した。
- 指摘: `received_at` を現在時刻だけで実装すると、raw store tests が時刻依存になる。
  - 対応: M2 に固定 `received_at` で deterministic test できることを追加した。
- 指摘: `normalize-raw` が measurement schema、output columns、unknown attribute sanitizer を複製すると、`aggregate-measurements` と出力仕様が分岐する。
  - 対応: M4 に `MeasurementRow` / `MeasurementOutputWriter.Columns` を単一 source of truth として使うこと、writer と sanitizer を共有することを追加した。
- 指摘: raw store が full raw payload を保持するため、MVP 後の user-facing docs に削除手順がないとローカル機密データの残存リスクが残る。
  - 対応: M6 に `data/raw-store.db`、temp output、raw payload の cleanup 手順を追加する完了条件を追加した。

### 妥当性判断

今回の変更は documentation-only であり、product behavior、public CLI 実体、schema、依存関係は変更していない。
追加した内容は、既存の Sprint2 MVP 境界を広げず、実装時の重複・fixture drift・時刻依存・ローカルデータ残存を防ぐための作業条件である。

### 残リスク

- live Copilot / Collector output の網羅性確認は、Sprint2 MVP 後の追加検証に残る。
- SQLite dependency の具体 package は M2 実装時に project file と lockfile 影響を確認して決める。

## 2026-06-08: M1-M3 follow-up review

### レビュー範囲

- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint2-raw-data-loop/README.md`
- M1-M3 task / review records

### Sub-Agent 指摘と Main-Agent 評価

- 指摘: `docs/spec.md` の現在フェーズが Sprint2 MVP 仕様化中のままで、M1 完了後の `docs/task.md` / Sprint2 README とずれている。
  - 妥当性: 妥当。`docs/spec.md` は現在フェーズの詳細仕様を定義するため、M1 仕様化完了後の実装・検証フェーズに合わせる必要がある。
  - 対応: `docs/spec.md` 1 の現在主作業を Sprint2 MVP の実装・検証に更新した。
- 指摘: Sprint2 README の `source` 説明が「など」となっており、`docs/spec.md` 5.17 の 3 値固定より広く読める。
  - 妥当性: 妥当。README は正式仕様ではないが、sprint-local 共有資料として誤読を避ける価値がある。
  - 対応: `source` を `raw-otlp`、`collector-output`、`langfuse-export` のいずれかとして表現を固定した。

### 残リスク

- Sprint2 README は引き続き補助資料であり、product behavior の正は `docs/requirements.md` と `docs/spec.md` とする。

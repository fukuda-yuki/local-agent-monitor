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
同時に、正式仕様として扱うには `docs/requirements.md` と `docs/spec.md` への反映が必要であることを明記している。

### 残リスク

- SQLite dependency、DB path、raw record schema、CLI interface はまだ正式仕様に反映していない。
- `.gitignore` の更新は実装時に行う必要がある。

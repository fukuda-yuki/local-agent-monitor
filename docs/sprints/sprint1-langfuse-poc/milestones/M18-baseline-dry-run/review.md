# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-25: CLI 側部分 dry run 後レビュー

### レビュー範囲

- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M17-baseline-protocol/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M18-baseline-dry-run/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M18-baseline-dry-run/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M18-baseline-dry-run/review.md`
- ignored local artifacts under `tmp/m18-baseline-dry-run/`

### 観点別 Sub-Agent レビュー

仕様整合 / milestone 完了判定、台帳 / schema / 集計成立性、データ扱い / secret leakage の 3 観点で read-only Sub-Agent レビューを実施した。

採用した指摘:

- M18 は `vscode-copilot-chat` と `copilot-cli` の各 2 runs が必要だが、現状は `copilot-cli` 2 runs のみで未完了。
- M18 docs がテンプレートのままで、実施済みの CLI 側 dry run と未実行の VS Code 側が記録されていなかった。
- ローカル `ledger.csv` の trace id / 集計列が空欄のままで、CLI 側 dry run の証跡として弱かった。
- 共有可能な sanitized output としては identity-bearing Resource Attributes を落とすべきだった。

採用しなかった指摘:

- `ReadExplicitCount` の複数候補矛盾検出は有用な将来リスクだが、M18 の live dry run 記録の修正範囲を超えるため、今回の実装変更には含めない。

### 対応

- `docs/task.md` の M18 状態を `未着手` から `進行中` に更新した。
- `docs/sprints/sprint1-langfuse-poc/milestones/M18-baseline-dry-run/task.md` に CLI 側 dry run と VS Code 側未実行の検証記録を追加した。
- `docs/sprints/sprint1-langfuse-poc/milestones/M18-baseline-dry-run/notes.md` に sanitized な CLI 側実施概要、集計成立性、未完了範囲を記録した。
- ignored local `tmp/m18-baseline-dry-run/ledger.csv` に CLI 側 trace id、trace found、集計列を反映した。
- ignored local sanitized snapshot から identity-bearing Resource Attributes を削除し、`aggregate-measurements` を再実行した。

### 残リスク

このレビュー時点では M18 の full scope は未完了だった。
残作業は `vscode-copilot-chat` で `maint-refactor-001` を `task_run_index=1,2` として実行し、Langfuse trace id と集計接続を同じ台帳形式で確認することだった。

Langfuse Public API response shape と M15 aggregation fixture の差分は、今後の live trace 追加時にも再確認が必要である。
M20 rubric が未定義のため、`success_status` は引き続き `not-evaluated` とする。

## 2026-05-26: full scope 完了後レビュー

### レビュー範囲

- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M18-baseline-dry-run/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M18-baseline-dry-run/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M18-baseline-dry-run/review.md`
- ignored local artifacts under `tmp/m18-baseline-dry-run/`

### 確認結果

M18 の 1 類型 x 2 `client.kind` x 2 runs は完了した。
`copilot-cli` と `vscode-copilot-chat` の各 2 runs で Langfuse trace を確認し、sanitized snapshot から `aggregate-measurements` で 4 行の CSV / JSON を生成できた。

VS Code Copilot Chat では 1 回の Chat 実行で複数 trace が出るため、`invoke_agent GitHub Copilot Chat` を代表 trace として扱った。
この代表 trace は agent invocation 全体に近く、M18 の baseline 台帳で trace 単位の token、turn count、tool call count、duration、error count を接続する目的に合う。
VS Code Copilot Chat 側では `unknown_spans_json` が非空になった。
これは M16 で定義した未知 observation の保持方針に沿った診断用情報であり、content や raw attributes は含めないため M18 の schema readiness を妨げない。

### データ扱い

ローカル台帳と sanitized aggregation artifacts は ignored な `tmp/m18-baseline-dry-run/` に置き、repository commit 対象にしていない。
tracked docs には件数、状態、判断理由だけを記録し、credential、Base64 header、prompt body、response body、tool arguments/results、実 trace content、identity-bearing な値は記録していない。

### 残リスク

M20 rubric が未定義のため、品質非劣化の `pass` / `fail` / `needs-review` 判定は行っていない。
全 run の `success_status` は `not-evaluated` とする。

VS Code Copilot Chat の trace 分割は client 実装に依存する可能性がある。
M19 本計測でも、代表 trace の選び方を `invoke_agent GitHub Copilot Chat` 優先として維持し、該当 trace がない場合はその run を `trace-missing` または調査対象として扱う。

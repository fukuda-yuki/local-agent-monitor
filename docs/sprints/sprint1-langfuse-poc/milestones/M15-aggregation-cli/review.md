# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-24: M15 集計 CLI / script MVP 実装レビュー

### レビュー範囲

- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/MeasurementAggregationTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/langfuse-legacy-traces.synthetic.json`
- `tests/CopilotAgentObservability.ConfigCli.Tests/CopilotAgentObservability.ConfigCli.Tests.csproj`
- `docs/sprints/sprint1-langfuse-poc/milestones/M15-aggregation-cli/`

### 観点

- M14 の入力方針である Public API legacy trace / observation read response 保存 JSON に沿った合成 fixture になっていること。
- M12 measurement schema の列、欠損値、Resource Attribute 写像、`success_status=not-evaluated` と矛盾しないこと。
- M16 前の count 系列を正式分類として扱っていないこと。
- fixture と docs に secret、実 trace content、実ユーザーデータ、顧客データ、実運用ログを含めていないこと。
- 自動テストが token、duration、暫定 tool call count、error count、欠損属性、未知 span 名を確認していること。

### 初回結果

実装は既存 `ConfigCli` に閉じており、新規 dependency や lockfile 更新はない。
CSV / JSON 出力は同じ固定列順を使い、CSV では欠損値を空欄、JSON では `null` として出力する。
`turn_count` は M15 では算出せず、`tool_call_count` は明示的な tool observation の暫定カウントに留めている。

### 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`

### 残リスク

- M15 fixture は合成 snapshot であり、実 Langfuse self-host の response shape すべてを網羅していない。
- `turn_count` と正式な `tool_call_count` 分類は M16 の算出ルール定義に依存する。
- subagent レビューは、現在の subagent tool ルールがユーザー明示依頼時のみ spawn 可能なため実施していない。

## 2026-05-25: 観点別 Sub-Agent レビューと再レビュー

### 実施観点

- 仕様適合: M12 / M14 / M15 の source of truth と出力 semantics。
- テスト、edge case、回帰リスク: CLI option、I/O error、fixture coverage。
- 保守性、最小性、データ扱い: repository style、unknown metadata、content capture 由来情報の扱い。

### 指摘評価と対応

| 指摘 | Main-Agent 判断 | 対応 |
| --- | --- | --- |
| `observations` 欠損時に `tool_call_count` / `error_count` が `0` になる | 採用。欠損と明示的な空配列を区別すべき | `observations` が配列でない場合は JSON `null` / CSV 空欄にし、空配列は `0` を維持した |
| unknown span が raw observation 全体を保持しない | 不採用。M15 では最小識別情報保持に留める方針で、raw payload は content capture 由来情報を含み得る | `id`、`name`、`type`、`kind` の最小情報を維持し、fixture で raw attributes をコピーしないことを確認した |
| missing parent directory の input path が misleading error になり得る | 採用 | `File.Exists` で入力 path を事前確認し、存在しない path は `input file not found` に統一した |
| `--csv --json` のように option token が output path として消費される | 採用 | `--csv` / `--json` の値が option token の場合は error にした |
| error count の fixture が `status=error` のみ | 採用 | `level=error` と object-valued `statusMessage` の synthetic observation を追加した |
| unknown metadata のコピーが広すぎる | 採用 | `unknown_attributes_json` は未写像 Resource Attributes のみに絞り、非 Resource metadata は出力しない |
| string numeric parsing が culture-sensitive | 採用 | string numeric parse を invariant culture に変更した |

### 再検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`

### 再レビュー結果

採用した指摘への修正を確認した。
再レビューで残った軽微なテストギャップとして、`error` property による error count 分岐と、非 Resource metadata の `prompt` / `source` が出力に混入しないことの明示 assertion が挙がった。
どちらも追加テストで補強した。
M15 としての残リスクは、実 Langfuse self-host response shape の網羅性と M16 の正式 counting rules に限定される。

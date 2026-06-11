# Sprint2.5: ConfigCli Maintainability

Sprint2.5 は、Sprint3 に進む前に Config CLI の技術負債を整理し、redacted real Copilot trace 由来データが raw data loop を通ることを最小確認する sprint である。

この sprint は GitHub Issue #24 を正式な参照元とする。
正式な product behavior と実装判断は `../../requirements.md` と `../../spec.md` を優先する。
本 README は sprint-local の作業分解と判断記録である。

## Milestones

| Milestone | 状態 | 概要 |
| --- | --- | --- |
| [M1: Sprint2.5 planning](milestones/M1-sprint2-5-planning/task.md) | 完了 | Program.cs 分割方針、責務境界、過剰分割禁止、redaction 方針を確定した |
| [M2: ConfigCli responsibility split](milestones/M2-configcli-responsibility-split/task.md) | 完了 | `Program.cs` を entry point と責務別ファイルへ分割し、既存 CLI contract を維持した |
| [M3: Output helper consolidation](milestones/M3-output-helper-consolidation/task.md) | 完了 | CSV / JSON 共通処理を集約し、出力互換性を確認した |
| [M4: Redacted real-trace E2E](milestones/M4-redacted-real-trace-e2e/task.md) | 未着手 | redacted real Copilot trace 由来 input で raw data loop の最小 E2E evidence を作成する |
| [M5: Regression review and closeout](milestones/M5-regression-review-and-closeout/task.md) | 未着手 | build / test / CLI contract 確認、review、Sprint2.5 closeout を行う |

## 目的

- `src/CopilotAgentObservability.ConfigCli/Program.cs` の責務分割により、可読性とレビュー容易性を改善する。
- CSV / JSON 出力補助と CSV parsing / escaping の重複を集約し、schema 追加時の修正漏れを減らす。
- 既存 CLI command 名、引数、出力形式、exit code を維持する。
- redacted real Copilot trace 由来データで `ingest-raw` から downstream workflow までの互換性を最小確認する。
- Sprint3 の trace diagnosis 検討前に、変更しやすい Config CLI 構造へ整える。

## 非目的

- AppHost ローカルランチャー化。
- Aspire AppHost への resource 追加。
- trace-driven automatic diagnosis の実装。
- LLM 呼び出しによる診断・提案生成。
- 改善案の自動採用、自動実装、patch / diff 生成、repository 自動修正。
- 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity の repository 保存。

## 実装方針

`Program.cs` は `Main` と最小 entry point に縮小する。
分割後も namespace は `CopilotAgentObservability.ConfigCli` のまま維持する。
public API は追加せず、既存テストのためだけに不要な可視性拡大を行わない。

ファイル分割は 1 ファイル 1 主型を原則とし、ファイル名は主型名に一致させる。
ただし、`*ParseResult`、`*Defaults`、親型に密結合した nested record などの companion type は、親となる主型のファイルに同居させる。
`*ParseResult.cs` や `*Defaults.cs` を機械的に単独生成しない。

推奨配置は以下とする。

```text
src/CopilotAgentObservability.ConfigCli/
  Program.cs
  Cli/
    CliApplication.cs
    CliHelpText.cs
    Options/
  RawTelemetry/
  Measurements/
  Diagnosis/
  Improvements/
  Shared/
```

CSV / JSON helper 集約では、header、列順、CSV の空欄、JSON の `null`、末尾改行、indentation、invalid input error の意味を変更しない。

## Redacted E2E 方針

Sprint2.5 の real-trace E2E は、実 Copilot OTel 由来の形状に raw data loop が耐えるかを確認する互換性チェックである。
Phase 1 / Sprint2 MVP の既定スコープを、未加工の実データ収集へ広げるものではない。

repository に保存してよいものは redacted または sanitized された summary、手順、検証結果だけとする。
実 content、secret、credential、Base64 header、実 user identity を含む raw payload は repository に保存しない。

## Done Definition

- `Program.cs` が entry point として十分小さくなり、責務別ファイルへ分割されている。
- CSV / JSON 共通処理が集約され、同一の CSV escaping 実装が複数箇所に残っていない。
- 既存 CLI contract に意図しない差分がない。
- `dotnet build CopilotAgentObservability.slnx` が成功している。
- `dotnet test CopilotAgentObservability.slnx` が成功している。
- redacted real Copilot trace 由来 input の E2E evidence が記録されている。
- 実 content、secret、credential、Base64 header、実 user identity が repository に保存されていない。
- AppHost ローカルランチャー化が follow-up として分離されている。

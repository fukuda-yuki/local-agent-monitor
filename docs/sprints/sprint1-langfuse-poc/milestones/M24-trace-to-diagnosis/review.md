# M24 Review

## 2026-06-03: Self-review

### 対象

- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/DiagnosisValidationTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/diagnoses.synthetic.json`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M24-trace-to-diagnosis/*`

### 観点

- M23 taxonomy の ID と M24 diagnosis record の列が一致していること。
- M17 `failure_type` と M23 `failure_category_id` を混同しないこと。
- raw prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を出力しないこと。
- 自動診断、改善案生成、自動採用、自動 repository 修正、自動勝敗決定を実装していないこと。
- synthetic fixture と deterministic tests で主要な成功・失敗ケースを確認していること。

### 結果

現時点の実装は M24 の記録検証 MVP に閉じており、trace からの自動分類や改善案生成には踏み込んでいない。
`dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` は成功した。
テストは 56 件成功した。

## 2026-06-03: Sub-Agent review follow-up

### 指摘と判定

- `evidence_summary` が Base64 header や実 email を拒否できない: 妥当。Base64 / Basic Auth / OTLP header / email / identity key の検出を追加した。
- `prompt` の単純部分一致により `prompt.version` など安全な比較メタデータ説明まで拒否する: 妥当。raw content を示す具体 pattern に絞り、`prompt.version` を含む sanitized comparison evidence を許可するテストを追加した。
- 不正な `task_run_index` が null に丸められる: 妥当。CSV / JSON とも非空かつ整数でない値をエラーにした。
- JSON の string 列が object / array / number / bool を raw JSON text として受け入れる: 妥当。diagnosis の string 列は JSON string / null のみ許可するようにした。
- CSV writer は改行を quote できるが reader は複数行 quoted field に未対応: 妥当。MVP では `evidence_summary` の CR / LF を危険 pattern として拒否する方針にした。

### 再検証

- `dotnet build CopilotAgentObservability.slnx` 成功。
- `dotnet test CopilotAgentObservability.slnx` 成功。63 tests passed。

## 2026-06-03: Focused re-review follow-up

### 指摘と判定

- bare Base64 credential が通る: 妥当。Base64 candidate を抽出して decode し、credential-like な decoded value を拒否するようにした。
- `token` の単純部分一致が safe token metrics evidence を拒否する: 妥当。`total_tokens` などの metric 名は許可し、`access token`、`refresh token`、`auth token`、`bearer`、GitHub token prefix など credential-specific pattern に絞った。

### 再検証

- `dotnet build CopilotAgentObservability.slnx` 成功。
- `dotnet test CopilotAgentObservability.slnx` 成功。66 tests passed。

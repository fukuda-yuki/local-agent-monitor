# M2: ConfigCli responsibility split

## 目的

巨大化した `src/CopilotAgentObservability.ConfigCli/Program.cs` を、既存挙動を変えずに責務別ファイルへ分割する。

M2 では分割を主目的とし、CSV / JSON helper の集約は M3 で扱う。

## 完了条件

- [x] `Program.cs` が `Main` と最小 entry point に縮小されている
- [x] `CliApplication` が `Cli/CliApplication.cs` に移動している
- [x] help text が `Cli/CliHelpText.cs` に抽出され、明示的な参照名で使われている
- [x] raw ingest / raw store / raw normalization 関連型が `RawTelemetry/` 配下へ移動している
- [x] measurement aggregation / output 関連型が `Measurements/` 配下へ移動している
- [x] diagnosis input / validation 関連型が `Diagnosis/` 配下へ移動している
- [x] improvement proposal / evaluation / human decision 関連型が `Improvements/` 配下へ移動している
- [x] 共通補助型がある場合は `Shared/` 配下へ移動している
- [x] namespace は `CopilotAgentObservability.ConfigCli` のまま維持している
- [x] public API 追加や不要な可視性拡大を行っていない
- [x] 既存 CLI command 名、引数、出力形式、exit code が変わっていない

## タスク分解

1. 分割前に `dotnet test CopilotAgentObservability.slnx` を実行し、既存状態を確認する。
2. `Program.cs` 内の型を責務別に移動する。機械的に `*ParseResult.cs` や `*Defaults.cs` を量産しない。
3. 1 ファイル 1 主型を基本にしつつ、密結合 companion type は親ファイルに同居させる。
4. using、namespace、visibility、test access を整理する。
5. CLI help と主要 command の出力互換性を確認する。

## 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`
- CLI help 出力の互換性確認
- 主要 command の exit code と stdout / stderr の互換性確認

## 注意事項

- 分割と同時に behavior を変更しない。
- 隣接する命名、formatting、構造を理由なく改善しない。
- M3 で扱う CSV / JSON helper 集約を M2 に混ぜない。

## 実績

- 2026-06-11: `Program.cs` を entry point のみに縮小し、既存型を `Cli/`、`RawTelemetry/`、`Measurements/`、`Diagnosis/`、`Improvements/`、`Shared/` に移動した。
- 2026-06-11: `CliHelpText.Text` を追加し、`CliApplication` から明示参照する形にした。help text の文面は変更していない。
- 2026-06-11: CSV escaping / parsing / JSON output helper の重複集約は M3 の対象として残した。
- 2026-06-11: 分割前に `dotnet test CopilotAgentObservability.slnx` を実行し、161 件成功した。
- 2026-06-11: 分割後に `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` を実行し、build は成功、test は 161 件成功した。
- 2026-06-11: `dotnet run --project src/CopilotAgentObservability.ConfigCli -- --help`、`validate-resource-attributes` 正常系、未知 command の exit code 1 と stderr を確認した。

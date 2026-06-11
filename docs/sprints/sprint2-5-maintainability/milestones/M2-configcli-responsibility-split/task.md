# M2: ConfigCli responsibility split

## 目的

巨大化した `src/CopilotAgentObservability.ConfigCli/Program.cs` を、既存挙動を変えずに責務別ファイルへ分割する。

M2 では分割を主目的とし、CSV / JSON helper の集約は M3 で扱う。

## 完了条件

- [ ] `Program.cs` が `Main` と最小 entry point に縮小されている
- [ ] `CliApplication` が `Cli/CliApplication.cs` に移動している
- [ ] help text が `Cli/CliHelpText.cs` に抽出され、明示的な参照名で使われている
- [ ] raw ingest / raw store / raw normalization 関連型が `RawTelemetry/` 配下へ移動している
- [ ] measurement aggregation / output 関連型が `Measurements/` 配下へ移動している
- [ ] diagnosis input / validation 関連型が `Diagnosis/` 配下へ移動している
- [ ] improvement proposal / evaluation / human decision 関連型が `Improvements/` 配下へ移動している
- [ ] 共通補助型がある場合は `Shared/` 配下へ移動している
- [ ] namespace は `CopilotAgentObservability.ConfigCli` のまま維持している
- [ ] public API 追加や不要な可視性拡大を行っていない
- [ ] 既存 CLI command 名、引数、出力形式、exit code が変わっていない

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

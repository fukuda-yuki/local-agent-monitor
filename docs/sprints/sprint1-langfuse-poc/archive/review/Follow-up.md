# Follow-up Review Note

## レビュー範囲

- Follow-up: Config CLI 既定 endpoint の Phase 1 Langfuse 化
- Config CLI の汎用コマンド出力
- Config CLI tests
- `docs/spec.md`、`docs/task.md`、`docs/sprints/sprint1-langfuse-poc/knowledge.md` の更新

## 変更ファイル

- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/knowledge.md`
- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/CliApplicationTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/ConfigSamplesTests.cs`
- `docs/review/Follow-up.md`

## 使用したサブエージェント

- なし

## サブエージェントの起動成否

- 上位実行ポリシーにより、ユーザーが明示的にサブエージェント利用を依頼していない場合はサブエージェントを起動できないため未起動。
- 代替としてメインエージェントが仕様準拠性、テスト、回帰リスクを自己レビューした。

## Raw Findings Summary

- Config CLI の汎用コマンド `vscode-settings`、`vscode-env`、`copilot-cli-env` は、古い Phase 0 HTTPS endpoint ではなく Phase 1 Langfuse endpoint を出力する必要がある。
- Langfuse 直接送信では Basic Auth header が必要なため、汎用 env コマンドにも credential placeholder と header 設定が必要。
- `collector-*` コマンドは Collector 経由送信用であり、Langfuse Basic Auth header を出力してはならない。
- Phase 0 Aspire HTTP 用コマンドの追加は今回のスコープ外。

## メインエージェントの妥当性判断

- 妥当。現在の主作業は Phase 1 Langfuse PoC であり、汎用 Config CLI の既定値も Phase 1 の既定構成に合わせるべき。
- 汎用 env コマンドで trace-specific endpoint / headers も出す変更は、既存の `langfuse-*` コマンドと同じ安全側の出力になり、signal-specific 設定が必要な exporter にも対応できる。
- `collector-*` コマンドは Langfuse 認証を Collector 側で付与する仕様のため、既存の cleanup 挙動を維持するのが妥当。

## 対応方針

- 汎用コマンドの既定 endpoint を Phase 1 Langfuse endpoint に切り替える。
- 汎用 env コマンドに Langfuse 認証 prelude、`OTEL_EXPORTER_OTLP_HEADERS`、`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`、`OTEL_EXPORTER_OTLP_TRACES_HEADERS` を追加する。
- テスト名と期待値を Phase 1 Langfuse 前提に更新する。
- `docs/spec.md`、`docs/task.md`、`docs/sprints/sprint1-langfuse-poc/knowledge.md` に今回の仕様と判断を記録する。

## 適用した修正

- Config CLI の `DefaultOtlpEndpoint` を Phase 1 Langfuse endpoint に変更した。
- `vscode-env` と `copilot-cli-env` の出力に Langfuse Basic Auth header と trace-specific endpoint / headers を追加した。
- 汎用コマンドのテストを Phase 1 Langfuse 前提に更新し、認証 header 出力の確認を追加した。
- `docs/spec.md` に Config CLI 汎用コマンドの既定送信先方針を追加し、非スコープから既定 endpoint 変更を削除した。
- `docs/task.md` の Follow-up 項目を完了に更新した。
- `docs/sprints/sprint1-langfuse-poc/knowledge.md` に今回の判断を記録した。

## 残リスク

- 汎用コマンドは Langfuse 認証 placeholder を含むため、Phase 0 Aspire Dashboard への軽量送信には適さない。Phase 0 Aspire HTTP 用コマンドが必要になった場合は別タスクで追加する。
- 実データ、共有環境、社内サーバー検証には、retention、アクセス権、masking / redaction、利用者周知の事前仕様化が必要。

## 保留事項

- 実データ、共有環境、社内サーバー検証が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。

## 検証

- `dotnet build CopilotAgentObservability.slnx` は成功した。警告 0、エラー 0。
- `dotnet test CopilotAgentObservability.slnx` は成功した。35 件合格、失敗 0、スキップ 0。
- `git diff --check` は成功した。
- `dotnet run --project src\CopilotAgentObservability.ConfigCli\CopilotAgentObservability.ConfigCli.csproj -- vscode-env` で、Langfuse endpoint、Basic Auth header、trace-specific endpoint / headers が出力されることを確認した。
- `dotnet run --project src\CopilotAgentObservability.ConfigCli\CopilotAgentObservability.ConfigCli.csproj -- copilot-cli-env` で、Langfuse endpoint、Basic Auth header、trace-specific endpoint / headers が出力されることを確認した。

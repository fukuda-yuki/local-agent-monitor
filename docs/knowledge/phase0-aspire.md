# Phase 0 Aspire Knowledge

この文書は Phase 0: ローカル Aspire Dashboard 疎通確認の再利用可能な調査結果と判断理由を記録する。
仕様の正は `docs/spec.md` であり、この文書は source of truth ではない。

## 初期方針

- 初期実装の主言語は C# / .NET 10 とした。
- 初期 milestone は Phase 0: ローカル Aspire Dashboard 疎通確認を優先した。
- .NET 側の役割は Aspire AppHost と設定生成・検証用の補助 CLI とした。
- solution はユーザー明示指示に従い `.sln` ではなく `CopilotAgentObservability.slnx` とした。

## M1-M4 検証結果

- `dotnet new aspire-apphost -n CopilotAgentObservability.AppHost -o src\CopilotAgentObservability.AppHost --force --no-restore` を適用し、AppHost を標準テンプレート構成に更新した。
- M1 検証として `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx --no-build` が成功した。
- M2 では AppHost 主手順を当初 `https` launch profile とし、Aspire Dashboard frontend は `https://localhost:17100`、OTLP/HTTP endpoint は `https://localhost:21025` とした。
- ローカル疎通確認では `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` を採用し、OTLP API key header なしで送信できる構成にした。
- M3 では Config CLI に `vscode-settings`、`copilot-cli-env`、`validate-resource-attributes` を追加した。
- M4 では `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx --no-build` が成功し、Config CLI tests は 18 件合格した。

## HTTPS endpoint 切り分け

- VS Code / VS Code Insiders の GitHub Copilot Chat OTel 設定で `endpoint=https://localhost:21025` が有効になっていることはログで確認できたが、Aspire Dashboard に telemetry が表示されなかった。
- PowerShell から `https://localhost:21025/v1/logs` と `/v1/traces` へ送信した人工 OTLP telemetry は HTTP 200 となり、Aspire Dashboard の Structured Logs / Traces に表示された。
- Node/Electron 相当の `fetch` では `https://localhost:21025/v1/logs` が `DEPTH_ZERO_SELF_SIGNED_CERT` で失敗した。
- VS Code GitHub Copilot Chat extension の OTLP exporter は Node/Electron 側の HTTP client を使うため、`https` profile のローカル開発証明書を信頼できず export に失敗する可能性が高い。
- ユーザーの手動確認により、AppHost を `http` launch profile で起動し、VS Code settings の `github.copilot.chat.otel.otlpEndpoint` を `http://localhost:19164` に変更すると Dashboard に表示されることを確認した。
- Phase 0 の主手順は `http` launch profile に変更した。

## M28 AppHost 再評価

- M28 (Issue #22) で、既存の空 AppHost を AI 協業向けのローカル診断エントリポイントとして再評価した。
- AppHost は空のまま維持する。Config CLI、OTel Collector、Langfuse、ServiceDefaults、新規 Web アプリ、DB / Redis / Worker は追加しない。
- Config CLI は補助 CLI であり、AppHost の常駐 resource として扱わない。既存の `dotnet run` 手順を維持する。
- `aspire agent init --workspace-root . --non-interactive --nologo` を実行し、5 つの skill files が生成された。
- `aspire` / `aspire-orchestration` / `aspire-monitoring` は維持し、`aspire-deployment` / `aspire-init` は本リポジトリの scope に合わないため削除した。
- Aspire MCP server は AppHost が空のため現時点では情報露出がない。resource 追加時に再評価する。
- Phase 1 Langfuse baseline を置き換えない方針を `docs/spec.md` § 9 に明記した。

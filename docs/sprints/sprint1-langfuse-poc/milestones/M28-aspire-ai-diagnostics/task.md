# M28: Aspire AppHost 再評価と AI 診断 workflow の整備

GitHub Issue: [#22](https://github.com/fukuda-yuki/copilot-agent-observability/issues/22)

## 目的

既存の空 Aspire AppHost を AI 協業向けのローカル診断・検証エントリポイントとして再評価し、Aspire を使う場面と使わない場面を明確にする。

## 完了条件

- [x] 既存 AppHost の役割が `docs/spec.md` に明記されている
- [x] AppHost に resource を追加するか、空のまま維持するかの判断理由が記録されている
- [x] 新規 Web アプリ project を作成しない方針が明記されている
- [x] ServiceDefaults を追加しない判断理由が記録されている
- [x] Config CLI を常駐 AppHost resource として扱わない判断理由が記録されている
- [x] `aspire agent init` の採用可否と生成物の扱いが記録されている
- [x] Aspire MCP server で AI に見せてよい情報範囲が記録されている
- [x] sensitive resource / telemetry の除外要否が評価されている
- [x] Phase 1 Langfuse baseline を置き換えないことが明記されている
- [x] Codex / 開発者向けの Aspire 利用指針が文書化されている
- [x] `dotnet build CopilotAgentObservability.slnx` が成功する
- [x] `dotnet test CopilotAgentObservability.slnx` が成功する
- [x] secrets / connection string / credential / 実 trace content が tracked repository files に保存されていない

## 検証記録

- 2026-06-04: `docs/spec.md` § 9 に Aspire AppHost の役割と使い分けセクションを追加した。
- 2026-06-04: AppHost を空のまま維持する判断理由を § 9.2 に記録した。Config CLI / OTel Collector / Langfuse / ServiceDefaults / 新規 Web アプリ / DB / Redis / Worker のそれぞれについて追加しない理由を明記した。
- 2026-06-04: `aspire agent init --workspace-root . --non-interactive --nologo` を実行し、5 つの skill files が生成された。`aspire` / `aspire-orchestration` / `aspire-monitoring` を維持し、`aspire-deployment` / `aspire-init` は scope 外のため削除した。結果を § 9.3 に記録した。
- 2026-06-04: Aspire MCP server の情報露出方針を § 9.4 に記録した。AppHost が空のため現時点で露出情報はなく、ExcludeFromMcp() は不要。resource 追加時に再評価する。
- 2026-06-04: Aspire を使う場面 / 使わない場面を § 9.5 に整理した。Phase 1 Langfuse baseline を置き換えないことを明記した。
- 2026-06-04: `AGENTS.md` に spec.md § 9 へのポインタを追記した。
- 2026-06-04: `docs/sprints/sprint1-langfuse-poc/knowledge/phase0-aspire.md` に M28 再評価結果を追記した。
- 2026-06-04: `dotnet build CopilotAgentObservability.slnx` 成功（0 警告・0 エラー）。
- 2026-06-04: `dotnet test CopilotAgentObservability.slnx` 成功。121 tests passed。
- 2026-06-04: tracked repository files に secrets / connection string / credential / 実 trace content が保存されていないことを確認した。ignore 済みの `tmp/langfuse/.env` はローカル Langfuse 実行用 credential を含むため、この完了条件の対象外とする。

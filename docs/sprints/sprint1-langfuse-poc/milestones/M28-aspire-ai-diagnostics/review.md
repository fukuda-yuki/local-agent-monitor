# M28 Review

## 2026-06-04 Sub-Agent Review

### 指摘 1: `docs/task.md` の M28 状態が `作業中` のまま

- 判定: 妥当。
- 対応: `docs/task.md` の M28 状態を `完了` に更新した。
- 理由: `docs/sprints/sprint1-langfuse-poc/milestones/M28-aspire-ai-diagnostics/task.md` の完了条件は全て `[x]` で、検証記録も残っているため、repository index と不整合だった。

### 指摘 2: Aspire skill が削除済み / scope 外 workflow へ誘導する

- 判定: 妥当。
- 対応: `.agents/skills/aspire*` の repository-local guidance を縮退し、`aspire-init`、`aspire-deployment`、`aspireify`、AppHost wiring、ServiceDefaults、deployment、Azure / K8s diagnostics は `docs/spec.md` 更新前は scope 外と明記した。
- 理由: M28 の仕様では AppHost を空のまま維持し、Phase 1 Langfuse baseline を置き換えず、deployment / init 系 skill は削除済みとして扱うため。

### 指摘 3: telemetry export / dashboard token / API key の扱いが skill 側で弱い

- 判定: 妥当。
- 対応: `aspire-monitoring` skill と参照文書に、`aspire export` archive、dashboard login URL / token、API key、prompt / response content、tool arguments / results、content-capture telemetry を保存・共有しない制約を追記した。
- 理由: `docs/spec.md` § 9.4 は content capture 由来情報、credential、secret、実 trace content を MCP 経由で公開しない方針を定めている。

### 指摘 4: ignored `tmp/langfuse/.env` に local credential がある

- 判定: 妥当。ただし tracked repository files には該当しない。
- 対応: M28 の no-secrets 完了条件を `tracked repository files` に限定して明記した。
- 理由: `tmp/` は ignore 済みで、ローカル Langfuse 実行用 credential を含む想定の作業領域である。一方、commit 対象の repository files に secrets / connection string / credential / 実 trace content を保存しない制約は維持する。

## 再レビュー観点

- `docs/spec.md` § 9 と repository-local Aspire skill の scope guard が矛盾しないこと。
- M28 の完了状態が `docs/task.md` と milestone task で一致すること。
- tracked files に実 credential、Base64 header、connection string、実 trace content を追加していないこと。

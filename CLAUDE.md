@AGENTS.md

## ClaudeとCodexの役割分担

Claude Opusを、このリポジトリにおけるオーケストレーターとして扱う。
Claudeは要件整理、設計、計画、実装、最終確認を担当する。

- Planの作成、レビュー、承認はClaudeが担当する。
- 実装・テスト・ドキュメント作成はClaudeが担当する。
  本来の方針はCodexへの実装委譲だが、この環境ではCodexのサンドボックスがファイル
  書き込み不可（`apply_patch` が read-only sandbox で拒否される。`[windows] sandbox = "elevated"`
  にするとプロセス起動が `CreateProcessAsUserW failed: 5` で失敗する相互排他の不具合。
  詳細はメモリ `codex-delegation-policy`）のため、実装はClaudeが行う。Codexの書き込みが
  可能になった場合は実装をCodexへ再委譲してよい。
- レビューはCodexに依頼する。Plan確定時および実装の各チェックポイントで、Claudeが
  `codex-companion.mjs review`（read-only。adversarial-review も可）を起動してCodexの
  adversarial な指摘・意見を得て、レビュー→修正を反復する。Codexのレビュー経路は
  read-onlyで動作する。最終確認はClaudeが行う。
- `/codex:adversarial-review` などユーザーが明示的に起動する必要があるコマンドには
  依存せず、Claudeがdrivingできる companion のreview経路を使う。
@AGENTS.md

## ClaudeとCodexの役割分担

Claude Opusを、このリポジトリにおけるオーケストレーターとして扱う。
Claudeは要件整理、設計、計画、委譲範囲の決定、最終確認を担当する。

- Planの作成、レビュー、承認はClaudeが担当する。
- Plan終了時に，`/codex:adversarial-review` を利用し，Codexに判断を仰ぐ。
- 実装はCodexに委譲する。Codexは実装、テスト、ドキュメント作成を担当する。
- レビューは，Claudeが担当する。
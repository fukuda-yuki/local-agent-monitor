# copilot-agent-observability

GitHub Copilot Chat / GitHub Copilot CLI の OpenTelemetry (OTel) データを収集・確認し、Agent / MCP / Skills / CLI の改善検討に必要な観測情報を得るための検証リポジトリです。

## 目的

- Copilot 実行過程を trace 単位で確認する
- tool call、token usage、duration、error を確認する
- prompt、response、tool arguments、tool results を確認する
- `client.kind` と `experiment.id` により trace を分類する
- Instructions / Skills / Agent / MCP / CLI の改善検討に必要な baseline trace を保存・参照する

このリポジトリは、改善案の自動生成、改善効果の自動合否判定、patch / diff 生成、commit / push / pull request 作成を目的にしません。

## 対象範囲

- 必須: VS Code GitHub Copilot Chat
- 必須: GitHub Copilot CLI
- 参考: Claude Code
- 対象外: Visual Studio 2026

## 現状フェーズ

現在は Phase 1: ローカル Langfuse PoC です。

Phase 1 では、Docker Desktop 上の Langfuse self-host Docker Compose を既定の実行基盤とし、VS Code GitHub Copilot Chat / GitHub Copilot CLI から Langfuse に OTLP HTTP で直接送信します。M9 ではこの baseline を維持したまま、ローカル OTel Collector 経由送信の最小サンプルも追加しています。ローカルでは Langfuse UI 到達、初期ユーザー / organization / project / API key の作成まで確認済みです。

M11 以降では、既存の観測基盤を継承し、研究実施計画書に基づく計測・集計・評価支援へ進めます。改善案の自動生成、自動実装、勝敗の自動決定は引き続き目的に含めません。

| 用途 | URL |
| --- | --- |
| Langfuse UI | `http://localhost:3000` |
| Langfuse OTLP endpoint | `http://localhost:3000/api/public/otel` |
| Langfuse OTLP traces endpoint | `http://localhost:3000/api/public/otel/v1/traces` |

送信時の認証は、Langfuse project の public key / secret key を Base64 化した Basic Auth を使います。詳細な環境変数と実行手順は `docs/spec.md` と `docs/task.md` を参照してください。

実行手順と検証項目の正は `docs/spec.md` と `docs/task.md` です。

Config CLI には、Phase 1 の Langfuse 向け設定サンプルを出力する `langfuse-vscode-settings`、`langfuse-vscode-env`、`langfuse-copilot-cli-env` と、M9 の Collector 向け設定サンプルを出力する `collector-vscode-settings`、`collector-vscode-env`、`collector-copilot-cli-env` があります。

## ドキュメント

- `docs/requirements.md`: 要件定義
- `docs/spec.md`: 現在フェーズの詳細仕様
- `docs/task.md`: 実装・検証チェックリスト
- `docs/knowledge.md`: 調査結果、判断理由、確認済み事項
- `docs/review/`: 現行 milestone のレビュー記録
- `docs/archive/`: 完了済み記録

# Phase 1 Langfuse Knowledge

この文書は Phase 1: ローカル Langfuse PoC の再利用可能な調査結果と判断理由を記録する。
仕様の正は `docs/spec.md` であり、この文書は source of truth ではない。

## 現在の前提

- 現在の主作業は Phase 1: ローカル Langfuse PoC である。
- Phase 0: ローカル Aspire Dashboard 疎通確認は完了済み背景として扱う。
- Phase 1 の既定構成は Docker Desktop 上の Langfuse self-host Docker Compose とする。
- Phase 1 では VS Code GitHub Copilot Chat / GitHub Copilot CLI から Langfuse OTLP HTTP endpoint へ直接送信し、OTel Collector は必須にしない。
- Phase 1 では content capture を有効化するが、投入データは合成データまたは検証用データを基本にする。

## VS Code Agent Debug との役割分担

- VS Code Agent Debug / Chat Debug View は、個別セッションの手動デバッグ用途として利用する。
- 本リポジトリでは、同等のデバッグ UI や VS Code 内部ログ解析機能を実装しない。
- 本リポジトリは、VS Code GitHub Copilot Chat / GitHub Copilot CLI の公式 OpenTelemetry 出力を Langfuse 等の observability backend に取り込み、trace / prompt / response / tool call / token usage を確認できることを検証する PoC に集中する。
- workspaceStorage 監視、常駐 Collector、PostgreSQL 保存、独自 UI は実装しない。

## Langfuse 起動とクライアント設定

- ユーザー確認により、Phase 1 の既定 PoC 実行基盤は Docker Desktop 上の Langfuse self-host Docker Compose とした。
- Langfuse UI は `http://localhost:3000`、OTLP endpoint は `http://localhost:3000/api/public/otel`、trace-specific endpoint は `http://localhost:3000/api/public/otel/v1/traces` を既定候補とした。
- Langfuse 認証は public key と secret key を Basic Auth 化し、`OTEL_EXPORTER_OTLP_HEADERS` または `OTEL_EXPORTER_OTLP_TRACES_HEADERS` で渡す方針とした。
- `tmp/langfuse/.env` を作成し、`docker compose up -d --wait --wait-timeout 600` で Langfuse self-host を起動した。
- `http://localhost:3000` に到達でき、Langfuse self-host のデモ用初期ログインでサインインできた。
- Config CLI に `langfuse-vscode-settings`、`langfuse-vscode-env`、`langfuse-copilot-cli-env` を追加した。
- Basic Auth 生成サンプルでは PowerShell の parser error を避けるため、`${publicKey}:${secretKey}` の形で変数名を区切る。

## 手動ライブ確認結果

- 2026-05-05 に VS Code GitHub Copilot Chat と GitHub Copilot CLI の Langfuse 直接 OTLP HTTP 送信を確認した。
- VS Code trace `5d81e50cca0eb67ac68248a2b27e4f7d` で `client.kind=vscode-copilot-chat`、`experiment.id=baseline`、prompt、response、tool span、duration、token usage を確認した。
- GitHub Copilot CLI 側では `client.kind=copilot-cli`、`experiment.id=baseline`、service `github-copilot` / version `1.0.40`、latency、token usage を確認した。
- CLI 側 content capture には合成プロンプト以外のローカル context 断片も含まれたため、今後の実データ検証では、投入データ、作業ディレクトリ、content capture の扱いをさらに制限する必要がある。
- 追加確認 trace `c9d55a6b5571c7d8e8fa18e861c93db8` では、合成 fixture の `README.md` のみが読み取られ、旧リポジトリ名、`docs/memo.json`、`current_file_content`、`recently_viewed_code_snippets` は検出されなかった。

## Collector 経由送信

- M9 では、Langfuse 直接送信を Phase 1 baseline として残し、直接送信が不安定な場合や後続の組織展開候補に備えるための Collector 経由送信を追加候補として扱う。
- ローカル Collector は Docker Desktop 上で起動し、クライアントからは OTLP/gRPC `localhost:4317` と OTLP/HTTP `http://localhost:4318` で受ける。
- Collector の host port binding は `127.0.0.1` に限定し、content capture を含む telemetry receiver を外部 interface に公開しない。
- Collector から Langfuse へは `http://host.docker.internal:3000/api/public/otel` に OTLP HTTP exporter で送信する。
- Langfuse 認証は `LANGFUSE_AUTH=Base64(public-key:secret-key)` として環境変数で渡し、repository 内の Collector config や Compose example には secret を保存しない。
- 2026-05-05 に Collector 経由で VS Code trace `8ca6f6422ccbd9d4ca34e2d443c7cf4a` と CLI trace `844864ac23f39dacb525ec252ddeab76` が Langfuse に取り込まれることを確認した。

## Follow-up

- Config CLI の汎用コマンド `vscode-settings`、`vscode-env`、`copilot-cli-env` の既定 endpoint は Phase 1 Langfuse 直接送信用の `http://localhost:3000/api/public/otel` に切り替えた。
- `langfuse-*` コマンドは Langfuse 直接送信を明示するコマンドとして維持し、`collector-*` コマンドは Collector 経由送信用に `http://localhost:4318` と Langfuse header cleanup を維持する。

## 利用者向け文書

- 2026-06-04 に README を初回利用者向けの入口として再構成した。
- `docs/getting-started.md` を追加し、利用前チェック、Langfuse 起動、Config CLI の設定サンプル出力、VS Code GitHub Copilot Chat / GitHub Copilot CLI の OTel 設定、Langfuse UI での確認、よくある失敗をまとめた。
- README と getting started は、`docs/spec.md` の既定構成を説明する利用者向け文書であり、仕様の source of truth ではない。
- 利用者向け文書では、実 credential、secret、Base64 化済み header、実 trace content、実 prompt / response content、実 user identity を保存しない。設定例は placeholder と synthetic / example identity のみにする。

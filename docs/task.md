# Task Breakdown

この文書は `docs/spec.md` を実装・検証タスクに分解したチェックリストである。
現在の主作業は Phase 1: ローカル Langfuse PoC である。

## M0-M4: Phase 0 完了済み

- [x] .NET 10 ソリューション初期化
- [x] Aspire AppHost によるローカル Dashboard 起動確認
- [x] Config CLI による設定サンプル生成・Resource Attributes 検証
- [x] Phase 0 の `http` launch profile 方針を文書化
- [x] VS Code GitHub Copilot Chat から Aspire Dashboard への OTLP HTTP 表示をユーザー環境で確認
- [x] Phase 0 の未確認ライブ項目は既知制約として閉じ、Phase 1 の Langfuse 確認項目へ置き換える

## M5: Phase 1 ドキュメント再編

- [x] `docs/spec.md` を Phase 1: ローカル Langfuse PoC 中心に再構成する
- [x] `docs/task.md` を Phase 1 用チェックリストとして再作成する
- [x] `README.md` の現状フェーズと目的表現を Phase 1 に合わせる
- [x] `AGENTS.md` から存在しない計画文書への参照を削除する
- [x] `docs/requirements.md` の重複貼り付け残りを削除する
- [x] 完了済み review note を `docs/archive/review/` に移動する

## M6: Langfuse ローカル起動

- [x] Docker Desktop が起動していることを確認する
- [x] Langfuse 公式 repository を取得する
- [x] Docker Compose の secret をローカルで設定する
- [x] `docker compose up` で Langfuse self-host を起動する
- [x] `http://localhost:3000` で Langfuse UI に到達できることを確認する
- [x] 初期ユーザー、organization、project を作成する
- [x] project の public key / secret key を作成する
- [x] Langfuse 停止手順と Docker volume 削除手順を確認する

2026-05-04 時点で、`tmp/langfuse/.env` を作成し、`docker compose up -d --wait --wait-timeout 600` で Langfuse self-host を起動した。
`http://localhost:3000` への到達、`demo@langfuse.com` / `password` でのログイン、`Seed Org` / `Seed Project` の表示、`Project API Keys` 画面での API key 作成済み表示を確認した。
停止は `docker compose down`、volume 削除込みは `docker compose down -v` を使う。

## M7: Phase 1 クライアント設定

- [x] VS Code Agent Debug / Chat Debug View は手動デバッグ用途であり、Phase 1 の成果物にしないことを確認する
- [x] public key と secret key から Basic Auth header を生成する
- [x] `OTEL_EXPORTER_OTLP_HEADERS` に `Authorization=Basic <base64>` と `x-langfuse-ingestion-version=4` を設定する
- [x] signal-specific 設定が必要な場合に備え、`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` と `OTEL_EXPORTER_OTLP_TRACES_HEADERS` の値を確認する
- [x] VS Code GitHub Copilot Chat の OTel settings を Langfuse endpoint に合わせる
- [x] VS Code から Langfuse へ直接 OTLP HTTP 送信する設定を確認する
- [x] VS Code プロセスに渡す OTel 関連環境変数を確認する
- [x] GitHub Copilot CLI の OTel 環境変数を Langfuse endpoint に合わせる
- [x] Copilot CLI から Langfuse へ直接 OTLP HTTP 送信する設定を確認する
- [x] `OTEL_RESOURCE_ATTRIBUTES` に必須属性を設定する
- [x] `OTEL_RESOURCE_ATTRIBUTES` に `user.id`, `user.email`, `team.id`, `department`, `client.kind`, `experiment.id` を設定する
- [x] `client.kind=vscode-copilot-chat` と `client.kind=copilot-cli` を使い分ける
- [x] content capture を有効化し、合成データのみで検証する

2026-05-04 時点で、Config CLI に Phase 1 向けの `langfuse-*` 生成コマンドを追加した。VS Code プロセスへの実反映とライブ確認は M8 で未完了である。
`global.json` に `rollForward: latestFeature` と `allowPrerelease: true` を明示し、インストール済み SDK `10.0.300-preview.0.26177.108` で `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` が成功することを確認した。

2026-05-05 時点で、VS Code GitHub Copilot Chat と GitHub Copilot CLI の Langfuse 直接 OTLP HTTP 送信をユーザー環境で確認した。
VS Code 側は `client.kind=vscode-copilot-chat`、CLI 側は `client.kind=copilot-cli`、両方で `experiment.id=baseline` を Langfuse 上で確認した。
初回 CLI 側 trace にはローカル context 断片が含まれたため、追加で `C:\Users\mwam0\Documents\Codex\otel-synthetic-cli-check` の合成 fixture だけを置いたディレクトリで確認した。
追加確認 trace `c9d55a6b5571c7d8e8fa18e861c93db8` では、合成 fixture の `README.md` のみが読み取られ、旧リポジトリ名、`docs/memo.json`、`current_file_content`、`recently_viewed_code_snippets` は検出されなかった。

## M8: Phase 1 手動ライブ確認

- [x] VS Code GitHub Copilot Chat の trace が Langfuse に取り込まれることを確認する
- [x] GitHub Copilot CLI の trace または metrics が Langfuse に取り込まれることを確認する
- [x] prompt / response / tool arguments / tool results が確認できることを確認する
- [x] token usage、duration、error が確認できることを確認する
- [x] `client.kind=vscode-copilot-chat` と `client.kind=copilot-cli` を識別できることを確認する
- [x] `experiment.id=baseline` で trace を識別できることを確認する
- [x] VS Code Agent Debug View ではなく Langfuse 上の trace として確認できた証跡を記録する
- [x] 確認日時、実行環境、Langfuse 起動方式、設定値、trace id または識別情報、確認できた項目、未確認項目を記録する

2026-05-05 に、Docker Desktop 上の Langfuse self-host (`http://localhost:3000`) で手動ライブ確認を実施した。
VS Code trace `5d81e50cca0eb67ac68248a2b27e4f7d` では、`client.kind=vscode-copilot-chat`、`experiment.id=baseline`、prompt、response、tool span、duration、token usage を確認した。
代表値として root / agent duration は `1m 3s`、agent token usage は `144,297 -> 7,016 (sum 151,313)`、generation duration は `11.22s`、generation token usage は `26,353 -> 827 (sum 27,688)` だった。
CLI 側では `client.kind=copilot-cli`、`experiment.id=baseline`、service `github-copilot` / version `1.0.40`、latency `0.28s`、token usage `4,371 -> 3 (sum 4,374)` を確認した。
別の CLI agent trace として `invoke_agent`、latency `3.52s`、token usage `33,818 -> 120 (sum 33,980)` も確認した。
追加確認として、`C:\Users\mwam0\Documents\Codex\otel-synthetic-cli-check` に合成 fixture の `README.md` だけを置き、GitHub Copilot CLI を再実行した。
trace `c9d55a6b5571c7d8e8fa18e861c93db8` では、`client.kind=copilot-cli`、`experiment.id=baseline`、service `github-copilot` / version `1.0.40`、prompt / response、tool span、duration、token usage を確認した。
同 trace の observation は `invoke_agent`、`chat gpt-5.3-codex`、`report_intent`、`view`、`chat gpt-5.3-codex` で、`view` は `C:\Users\mwam0\Documents\Codex\otel-synthetic-cli-check\README.md` のみを読んだ。
ClickHouse 上の検索で、同 trace 内の旧リポジトリ名、`docs/memo.json`、`current_file_content`、`recently_viewed_code_snippets` は 0 件、synthetic path は 3 件、`Synthetic Fixture` は 2 件だった。
代表値として `invoke_agent` の latency は `8799ms`、token usage は `16,921 -> 233 (total 34,034)`、最終 generation の latency は `1997ms`、token usage は `192 -> 57 (total 17,046)` だった。

## M9: OTel Collector 経由送信の仕様化と最小実装

- [x] `docs/spec.md` に Collector 経由送信を Phase 1 baseline の次候補として定義する
- [x] Collector の OTLP HTTP/gRPC receiver port を `4318` / `4317` として定義する
- [x] Collector の host port binding を `127.0.0.1` に限定する
- [x] Collector から Langfuse への既定 exporter endpoint を `http://host.docker.internal:3000/api/public/otel` として定義する
- [x] Langfuse 認証を `LANGFUSE_AUTH=Base64(public-key:secret-key)` で渡し、secret を repository に保存しない方針を定義する
- [x] `docker compose config` の確認では dummy `LANGFUSE_AUTH` を使うことを定義する
- [x] M9 の非スコープとして masking / redaction、TLS、SSO、共有環境運用、Resource Attributes の Collector 側自動付与、sampling を明記する
- [x] M9 Collector example は trace pipeline のみを有効にし、metrics pipeline は扱わない
- [x] `infra/otel-collector/otel-collector.example.yaml` を追加する
- [x] `infra/otel-collector/docker-compose.example.yml` を追加する
- [x] Config CLI に `collector-vscode-settings` を追加する
- [x] Config CLI に `collector-vscode-env` を追加する
- [x] Config CLI に `collector-copilot-cli-env` を追加する
- [x] Config CLI の新コマンド出力を単体テストで確認する
- [x] Docker が利用可能な環境で `docker compose -f infra/otel-collector/docker-compose.example.yml config` を確認する
- [x] Langfuse 起動後に Collector 経由で VS Code GitHub Copilot Chat の trace が取り込まれることを確認する
- [x] Langfuse 起動後に Collector 経由で GitHub Copilot CLI の trace が取り込まれることを確認する
- [x] Collector 経由 trace で prompt / response、tool span、token usage、`client.kind`、`experiment.id` を確認する

M9 は Collector 経由送信の PoC 準備であり、組織展開そのものではない。
Langfuse 直接送信コマンドは baseline として維持する。
2026-05-05 に、`LANGFUSE_AUTH=dummy` を設定したうえで `docker compose -f infra\otel-collector\docker-compose.example.yml config` が成功することを確認した。
同日に `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` が成功し、Config CLI tests は 35 件合格した。
レビュー指摘により、Collector の host port binding は `127.0.0.1` に限定し、Collector example は trace pipeline のみに変更した。
また、Collector 系 Config CLI 出力では Langfuse 直接送信用の `OTEL_EXPORTER_OTLP_HEADERS`、`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`、`OTEL_EXPORTER_OTLP_TRACES_HEADERS` を解除する。
手動ライブ確認を実施した場合は、確認日時、Langfuse trace id または識別情報、Collector 起動方式、確認できた項目、未確認項目を記録する。

2026-05-05 JST に、Docker Desktop 上で Langfuse self-host と Collector example を起動し、Collector 経由送信を手動ライブ確認した。
Collector は `127.0.0.1:4317` / `127.0.0.1:4318` で listen し、`http://localhost:4318/v1/traces` は GET で `405 Method Not Allowed` を返した。
VS Code GitHub Copilot Chat は trace `8ca6f6422ccbd9d4ca34e2d443c7cf4a` として取り込まれ、`client.kind=vscode-copilot-chat`、`experiment.id=baseline`、prompt / response、`read_file` tool span、token usage を確認した。
GitHub Copilot CLI は trace `844864ac23f39dacb525ec252ddeab76` として取り込まれ、`client.kind=copilot-cli`、`experiment.id=baseline`、prompt / response、`view` tool span、token usage を確認した。
両 trace とも `C:\Users\mwam0\Documents\Codex\otel-synthetic-cli-check\README.md` を対象にし、旧リポジトリ名、`docs/memo.json`、`current_file_content`、`recently_viewed_code_snippets` は検出されなかった。

## M10: 週次 Gitleaks secret scan workflow

- [x] `docs/requirements.md` に repository secret scan 要件を追加する
- [x] `docs/spec.md` に weekly gitleaks workflow 仕様を追加する
- [x] `.github/workflows/weekly-gitleaks-secret-scan.yml` を追加する
- [x] 週次 schedule を月曜 09:00 JST (`0 0 * * 1`) にする
- [x] `workflow_dispatch` を有効にする
- [x] `permissions` を `contents: read` と `issues: write` に限定する
- [x] `actions/checkout` を既存 workflow と同じ pinned SHA で使用し、`fetch-depth: 0` にする
- [x] gitleaks CLI `v8.30.1` を固定し、checksum verification 後に実行する
- [x] gitleaks JSON report を runner temp のみに保存し、artifact upload しない
- [x] secret 検知時に secret 値や match 文字列を含めず、新規 GitHub Issue を作成する
- [x] secret 検知時は workflow を成功扱いにし、scan 失敗や Issue 作成失敗は workflow 失敗にする
- [x] 合成 gitleaks JSON fixture で Issue body 生成を確認する
- [x] workflow YAML 構文を確認する
- [x] `docs/review/M10.md` にレビュー結果を記録する

M10 は Langfuse / OTel 観測データの DLP ではなく、repository hygiene のための GitHub Actions workflow である。
Issue には commit link、file、line、RuleID、Fingerprint を記載するが、secret 値、match 文字列、secret の前後文脈、redacted report 全文は記載しない。
false positive 対応用の `.gitleaks.toml` や `.gitleaksignore` は初期実装では追加しない。
2026-05-19 に、workflow YAML を `js-yaml` で parse し、合成 gitleaks JSON fixture から生成した Issue body に fixture の `Secret` / `Match` 値が含まれないことを確認した。
同日に gitleaks `v8.30.1` Linux x64 tarball の SHA-256 checksum が workflow 固定値と一致すること、`git diff --check` が成功することを確認した。
`gh workflow view` は workflow が default branch に未反映のため 404 となった。default branch 反映後に `workflow_dispatch` で実動作を確認する。

## M11-M13: Phase A 現状整理と研究スコープ接続

- [ ] [M11: 研究計画書とのスコープ整合](https://github.com/fukuda-yuki/copilot-agent-observability/issues/8)
- [ ] [M12: 研究用 measurement schema 定義](https://github.com/fukuda-yuki/copilot-agent-observability/issues/9)
- [ ] [M13: 模擬保守タスクセット定義](https://github.com/fukuda-yuki/copilot-agent-observability/issues/10)

既存の M0-M10 は破棄せず、Copilot OTel を Langfuse に収集できる観測基盤として扱う。
M11 以降では、研究実施計画書に基づき、trace が見える状態から研究指標を再現可能に集計できる状態へ進める。
改善案生成、自動実装、勝敗の自動決定は引き続き非スコープとする。

## M14-M16: Phase B Langfuse から研究指標を取り出す

- [ ] [M14: Langfuse export / API 調査](https://github.com/fukuda-yuki/copilot-agent-observability/issues/11)
- [ ] [M15: 集計 CLI / script MVP](https://github.com/fukuda-yuki/copilot-agent-observability/issues/12)
- [ ] [M16: turn count / tool call count 算出ルール](https://github.com/fukuda-yuki/copilot-agent-observability/issues/13)

Langfuse の trace / observation / usage / metadata 取得方式は M14 で決める。
M15 は M14 で決めた入力形式を使い、M12 の集計列を持つ CSV / JSON を生成する。
M16 では span 名に過度に依存しない turn count / tool call count の分類ルールを定義する。

## M17-M19: Phase C baseline 計測プロトコル

- [ ] [M17: baseline 実行手順と記録様式](https://github.com/fukuda-yuki/copilot-agent-observability/issues/14)
- [ ] [M18: baseline 計測の小規模 dry run](https://github.com/fukuda-yuki/copilot-agent-observability/issues/15)
- [ ] [M19: baseline 本計測](https://github.com/fukuda-yuki/copilot-agent-observability/issues/16)

baseline 計測は、初期既定を `N=10`、`experiment.id=baseline`、合成 fixture のみとする。
M18 では 1 類型 x 2 回で schema と集計が成立するか確認し、M19 で 4 類型 x N 回の baseline trace を取得する。
欠損 trace、取得失敗、手動除外は記録する。

## M20-M22: Phase D 評価と改善比較の準備

- [ ] [M20: 品質非劣化 rubric 定義](https://github.com/fukuda-yuki/copilot-agent-observability/issues/17)
- [ ] [M21: variant / A-B 計測プロトコル](https://github.com/fukuda-yuki/copilot-agent-observability/issues/18)
- [ ] [M22: 結果レポート雛形](https://github.com/fukuda-yuki/copilot-agent-observability/issues/19)

M20 では 4 類型ごとに `pass`、`fail`、`needs-review` の人間評価用 rubric を定義する。
M21 では baseline / variant 比較のため、`experiment.id`、`experiment.condition`、`prompt.version`、`agent.variant` の使い分けを定義する。
M22 では token、turn count、tool call count、duration、`success_status`、観察メモを記録できる Markdown レポート雛形を追加する。

## Future Backlog: trace-driven improvement loop

- [ ] M23: failure taxonomy / anti-pattern 定義
- [ ] M24: trace-to-diagnosis MVP
- [ ] M25: improvement proposal generator
- [ ] M26: proposal evaluator
- [ ] M27: human approval workflow

この backlog は M11-M22 完了後に別途 Issue 化する。
改善案生成基盤は、trace / metrics / rubric を入力に、失敗分類、改善候補生成、variant 評価、人間承認を順に行うものとして検討する。
自動 repository 修正、自動 commit、自動 push、自動 pull request、自動勝敗決定は既定スコープ外とする。

## Phase E: 既存未完了の整理

- [ ] [M10 follow-up: gitleaks fixture 削除](https://github.com/fukuda-yuki/copilot-agent-observability/issues/20)
- [ ] [Backlog: 共有環境・実データ検証の事前仕様化](https://github.com/fukuda-yuki/copilot-agent-observability/issues/21)

M10 follow-up は研究計測とは独立した repository hygiene 作業として扱う。
共有環境、実データ、社内サーバー検証が必要になった場合は、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する。

## Follow-up

- [x] Config CLI の既定 endpoint が古い Phase 0 HTTPS 系の値のままなので、別タスクで Phase 0 HTTP endpoint または Phase 1 Langfuse endpoint へ切り替えるか判断する
- [ ] 実データ、共有環境、社内サーバー検証が必要になった場合、retention、アクセス権、masking / redaction、利用者周知を先に仕様化する: https://github.com/fukuda-yuki/copilot-agent-observability/issues/21

2026-05-05 に、Config CLI の汎用コマンド `vscode-settings`、`vscode-env`、`copilot-cli-env` の既定送信先を Phase 1 Langfuse 直接送信用の `http://localhost:3000/api/public/otel` に切り替えた。
汎用 env コマンドは Langfuse Basic Auth header と trace-specific endpoint / headers も出力する。
`langfuse-*` コマンドは明示的な Langfuse 出力として維持し、`collector-*` コマンドは `http://localhost:4318` と Langfuse header cleanup のまま維持した。

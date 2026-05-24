# Knowledge Notes

この文書は実装時の補助知識、外部制約、検証メモを置く場所である。
プロダクト仕様・実装方針・優先順位の正は `docs/spec.md` とし、この文書は source of truth ではない。
`README.md` はプロジェクト背景・全体像・初期構想の参考資料として扱う。

## 現在の前提

- 現在の主作業は Phase 1: ローカル Langfuse PoC である。
- Phase 0: ローカル Aspire Dashboard 疎通確認は完了済み背景として扱う。
- Phase 1 の既定構成は Docker Desktop 上の Langfuse self-host Docker Compose とする。
- Phase 1 では VS Code GitHub Copilot Chat / GitHub Copilot CLI から Langfuse OTLP HTTP endpoint へ直接送信し、OTel Collector は必須にしない。
- Phase 1 では content capture を有効化するが、投入データは合成データまたは検証用データを基本にする。
- M11 以降では、既存の M0-M10 を観測基盤として維持し、研究実施計画書に基づく計測・集計・評価支援へ進める。
- 研究計測化では改善案の自動生成、自動実装、勝敗の自動決定は扱わない。

## 2026-05-04: VS Code Agent Debug 機能との役割分担と、独自デバッグ実装の非採用

### 決定

VS Code Agent Debug / Chat Debug View は、個別セッションの手動デバッグ用途として利用する。
本リポジトリでは、同等のデバッグ UI や VS Code 内部ログ解析機能を実装しない。

本リポジトリは、VS Code GitHub Copilot Chat / GitHub Copilot CLI の公式 OpenTelemetry 出力を Langfuse 等の observability backend に取り込み、trace / prompt / response / tool call / token usage を確認できることを検証する PoC に集中する。

### 理由

- VS Code 側に既に Agent Debug / Chat Debug 系の手動デバッグ機能がある。
- Copilot Chat / Copilot CLI は公式 OTel 出力に対応している。
- 独自デバッグ UI や内部ログ解析は、保守コストと仕様変更リスクが大きい。
- 本プロジェクトの目的は改善そのものではなく、改善検討に必要な観測データを得ることである。
- Phase 1 の成功条件は OTel 収集成功であり、改善効果判定ではない。

### 影響

- Phase 1 では Langfuse への直接 OTLP HTTP 送信確認を優先する。
- workspaceStorage 監視、常駐 Collector、PostgreSQL 保存、独自 UI は実装しない。
- 将来の組織展開時のみ、OTel Collector、masking / redaction、認証、保持期間、アクセス権を再検討する。

## 2026-04-25: 初期実装方針の確認

- 初期実装の主言語は C# / .NET 10 とする。
- 初期マイルストーンは Phase 0: ローカル Aspire Dashboard 疎通確認を優先する。
- .NET 側の役割は Aspire AppHost と設定生成・検証用の補助 CLI とする。
- README と `docs/requirements.md` にズレがある場合は、`docs/requirements.md` を優先し、README 修正は後続タスクとして扱う。

## 2026-04-25: M1 初期化結果
- .NET SDK はローカルに `10.0.203` があり、`global.json` で固定した。
- ユーザー明示指示に従い、solution は `.sln` ではなく `CopilotAgentObservability.slnx` として作成した。
- 環境再確認後、`aspire-apphost` テンプレートが利用可能になっていることを確認したため、`dotnet new aspire-apphost -n CopilotAgentObservability.AppHost -o src\CopilotAgentObservability.AppHost --force --no-restore` を適用し、AppHost を標準テンプレート構成に更新した。
- M1 検証として `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx --no-build` が成功した。

## 2026-04-25: M2 Aspire Phase 0 疎通基盤
- Phase 0 の AppHost 主手順は当初 `https` launch profile とし、Aspire Dashboard frontend は `https://localhost:17100`、OTLP/HTTP endpoint は `https://localhost:21025` とした。
- VS Code GitHub Copilot Chat の `otlp-http` 送信先は当初 `github.copilot.chat.otel.otlpEndpoint=https://localhost:21025` とした。
- ローカル疎通確認では `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` を採用し、OTLP API key header なしで送信できる構成にした。この設定は Phase 0 のローカル開発専用であり、共有環境や本番方針ではない。
- `http` launch profile は Aspire の未暗号化トランスポート制約によりそのままでは起動しないため、使用時に備えて `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` を設定した。後続検証で VS Code GitHub Copilot Chat からの送信には `http` profile を主手順に変更した。
- M2 検証として `dotnet build CopilotAgentObservability.slnx` が成功した。
- M2 起動確認として `dotnet run --project src\CopilotAgentObservability.AppHost\CopilotAgentObservability.AppHost.csproj --launch-profile https` を実行し、`https://localhost:17100` が HTTP 200 を返すことを確認した。確認後、起動した AppHost プロセスは停止した。

## 2026-04-25: M3 Config CLI 実装結果
- Config CLI に `vscode-settings`、`copilot-cli-env`、`validate-resource-attributes` を追加した。
- M3 の出力既定値は当時の Phase 0 仕様に従い、OTLP endpoint は `https://localhost:21025`、Copilot CLI の `client.kind` は `copilot-cli`、`experiment.id` は `baseline` とした。
- `validate-resource-attributes` は必須キー欠落と不正な `key=value` 形式を error、推奨値外の `client.kind` と `experiment.id` を warning として扱う。
- M3 検証として `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` が成功した。

## 2026-04-25: M4 検証結果
- `dotnet build CopilotAgentObservability.slnx` は成功した。警告 0、エラー 0。
- `dotnet test CopilotAgentObservability.slnx --no-build` は成功した。Config CLI tests は 18 件合格、失敗 0、スキップ 0。
- `dotnet run --project src\CopilotAgentObservability.AppHost\CopilotAgentObservability.AppHost.csproj --launch-profile https` で AppHost を起動し、`https://localhost:17100` が HTTP 200 を返すことを確認した。確認後、起動した AppHost プロセスは停止した。
- VS Code GitHub Copilot Chat からの trace 取り込み、span tree、token usage、duration、error、prompt / response / tool arguments / tool results、`client.kind=vscode-copilot-chat`、`experiment.id=baseline` は、このセッションから VS Code Copilot Chat を操作して実送信できないため未確認。
- 手動ライブ確認では、確認日時、VS Code version、GitHub Copilot Chat extension version、設定値、実行した依頼内容、Aspire Dashboard 上の trace id または識別情報、確認できた項目、未確認項目と理由を記録する。

## 2026-04-26: Phase 0 HTTPS endpoint 切り分け
- VS Code / VS Code Insiders の GitHub Copilot Chat OTel 設定で `endpoint=https://localhost:21025` が有効になっていることはログで確認できたが、Aspire Dashboard に telemetry が表示されなかった。
- PowerShell から `https://localhost:21025/v1/logs` と `/v1/traces` へ送信した人工 OTLP telemetry は HTTP 200 となり、Aspire Dashboard の Structured Logs / Traces に表示されたため、Dashboard の OTLP 受信自体は動作していた。
- Node/Electron 相当の `fetch` では `https://localhost:21025/v1/logs` が `DEPTH_ZERO_SELF_SIGNED_CERT` で失敗した。VS Code GitHub Copilot Chat extension の OTLP exporter は Node/Electron 側の HTTP client を使うため、`https` profile のローカル開発証明書を信頼できず export に失敗する可能性が高い。
- ユーザーの手動確認により、AppHost を `http` launch profile で起動し、VS Code settings の `github.copilot.chat.otel.otlpEndpoint` を `http://localhost:19164` に変更すると Dashboard に表示されることを確認した。
- Phase 0 の主手順は `http` launch profile に変更する。frontend は `http://localhost:15090`、OTLP/gRPC は `http://localhost:19163`、OTLP/HTTP は `http://localhost:19164` を使う。
- `consolelogs` は AppHost 管理リソースの stdout/stderr 用であり、OTLP Logs の確認先ではない。OTLP Logs は `structuredlogs` で確認する。
- `tmp\otel-chat-logs.jsonl` の file exporter 出力では `scopeSpans=0`、`scopeMetrics=16`、`spanContext` を持つ log record が確認された。ログに `spanContext` が付いていても Traces 画面に表示される span とは限らないため、signal 種別に応じて Traces / Structured Logs / Metrics を確認する。

## 2026-04-30: M5 Phase 1 準備
- ユーザー確認により、Phase 1 の既定 PoC 実行基盤は Docker Desktop 上の Langfuse self-host Docker Compose とした。
- Phase 1 の送信経路は VS Code GitHub Copilot Chat / GitHub Copilot CLI から Langfuse OTLP HTTP endpoint への直接送信とし、OTel Collector は必須にしない。
- Langfuse UI は `http://localhost:3000`、OTLP endpoint は `http://localhost:3000/api/public/otel`、trace-specific endpoint は `http://localhost:3000/api/public/otel/v1/traces` を既定候補とした。
- Langfuse 認証は public key と secret key を Basic Auth 化し、`OTEL_EXPORTER_OTLP_HEADERS` または `OTEL_EXPORTER_OTLP_TRACES_HEADERS` で渡す方針とした。
- content capture は Phase 1 でも有効化するが、ローカル限定 PoC とし、合成データまたは検証用データを基本にする。保持期間は 30 日上限を目安とする。

## 2026-04-30: M6 Langfuse ローカル起動準備
- `Get-Command docker`、`where.exe docker`、Docker Desktop 既定インストールパス、Docker 関連プロセス、`winget list --name Docker` を確認したが、この PowerShell 環境では Docker CLI / Docker Desktop を検出できなかった。
- `Get-NetTCPConnection -LocalPort 3000` では既存の 3000 番利用は検出されなかった。
- Langfuse 公式 repository を `tmp/langfuse` に shallow clone した。取得 commit は `81e1ba312088e9bf10245fd2999dea82862c7fbf`。
- Docker Compose の `# CHANGEME` に対応する local secret と、headless initialization 用の初期 user / organization / project / API key 値を `tmp/langfuse/.env` に生成した。`.env` は Langfuse repository 側で ignored であり、API key、管理者パスワード、secret 値は記録しない。
- `docker version`、`docker compose version`、`docker compose up` は `docker` コマンド不在により実行不可だった。
- Langfuse 公式 Docker Compose 手順では、通常停止は `docker compose down`、volume 削除込みの停止は `docker compose down -v` とされている。

## 2026-05-04: M6 Langfuse ローカル起動完了
- `tmp/langfuse/.env` を作成し、`docker compose up -d --wait --wait-timeout 600` で Langfuse self-host を起動した。
- `http://localhost:3000` に到達でき、`demo@langfuse.com` / `password` でサインインできた。
- `Seed Org` と `Seed Project` が headless initialization で作成され、`Project API Keys` 画面で API key の provision 済み表示を確認した。
- 取得した Langfuse repository は `81e1ba312088e9bf10245fd2999dea82862c7fbf` を checkout した detached HEAD 状態で使っている。
- この PowerShell 環境では `git` が PATH に入っていなかったため、`C:\Program Files\Git\cmd\git.exe` を直接呼び出した。

## 2026-05-04: M7 Phase 1 クライアント設定着手
- Config CLI に `langfuse-vscode-settings`、`langfuse-vscode-env`、`langfuse-copilot-cli-env` を追加した。
- Phase 1 用の env スクリプトでは、PowerShell 内で public key / secret key から Basic Auth を組み立てる形にした。
- 既存の Phase 0 コマンドは維持し、Langfuse 向けの出力は別コマンドに分離した。
- PowerShell の double-quoted string では `$publicKey:$secretKey` が ParserError になるため、Basic Auth 生成サンプルでは `${publicKey}:${secretKey}` の形で変数名を区切る。
- 2026-05-04 時点のローカル環境では、`global.json` が要求する .NET SDK `10.0.203` に対してインストール済み SDK は `10.0.300-preview.0.26177.108` のみである。`dotnet --version`、`dotnet test CopilotAgentObservability.slnx`、`DOTNET_ROLL_FORWARD_TO_PRERELEASE=1` 付きの `dotnet --version` はいずれも SDK 解決で失敗し、`10.0.300-preview.0.26177.108` は現在の `global.json` の互換 SDK として選択されなかった。
- `global.json` に `rollForward: latestFeature` と `allowPrerelease: true` を明示し、要求 SDK `10.0.203` がない環境でも同じ major/minor の新しい feature band preview SDK を選択できるようにした。
- 更新後、`dotnet --version` は `10.0.300-preview.0.26177.108` を返した。`dotnet build CopilotAgentObservability.slnx` は成功し、警告 0、エラー 0。`dotnet test CopilotAgentObservability.slnx` は成功し、29 件合格、失敗 0、スキップ 0。

## 2026-05-05: M8 Phase 1 手動ライブ確認結果
- Langfuse self-host は Docker Desktop 上で起動中で、`http://localhost:3000` が HTTP 200 を返した。
- `http://localhost:3000/api/public/otel` は GET では 404、`/api/public/otel/v1/traces` は GET では 405 だった。OTLP trace endpoint はブラウザ GET ではなく POST 用であるため、GET 結果だけを失敗判定にしない。
- 初回 VS Code 送信では Langfuse web log に `Error verifying auth header: Invalid credentials` が出た。API key を作り直し、VS Code を OTel 環境変数付き PowerShell から再起動すると trace が取り込まれた。
- VS Code trace `5d81e50cca0eb67ac68248a2b27e4f7d` で `client.kind=vscode-copilot-chat`、`experiment.id=baseline`、prompt、response、tool span、duration、token usage を確認した。
- VS Code trace の代表値は root / agent duration `1m 3s`、agent token usage `144,297 -> 7,016 (sum 151,313)`、generation duration `11.22s`、generation token usage `26,353 -> 827 (sum 27,688)`。
- GitHub Copilot CLI は `gh copilot explain ...` 形式ではなく、現在の preview では `gh copilot -p "..."` 形式で非対話 prompt を渡す必要があった。
- CLI 側 trace / observation として `client.kind=copilot-cli`、`experiment.id=baseline`、service `github-copilot` / version `1.0.40`、latency `0.28s`、token usage `4,371 -> 3 (sum 4,374)` を Langfuse 上で確認した。
- 別の CLI agent trace として `invoke_agent`、latency `3.52s`、token usage `33,818 -> 120 (sum 33,980)` も確認した。
- CLI 側 content capture には合成プロンプト以外のローカル context 断片も含まれた。今後の実データ検証では、投入データ、作業ディレクトリ、content capture の扱いをさらに制限する必要がある。
- 追加確認として、`C:\Users\mwam0\Documents\Codex\otel-synthetic-cli-check` に合成 fixture の `README.md` だけを置き、GitHub Copilot CLI を再実行した。
- 追加確認 trace `c9d55a6b5571c7d8e8fa18e861c93db8` では、`client.kind=copilot-cli`、`experiment.id=baseline`、service `github-copilot` / version `1.0.40`、prompt / response、tool span、duration、token usage を確認した。
- 同 trace の observation は `invoke_agent`、`chat gpt-5.3-codex`、`report_intent`、`view`、`chat gpt-5.3-codex` で、`view` は `C:\Users\mwam0\Documents\Codex\otel-synthetic-cli-check\README.md` のみを読んだ。
- ClickHouse 上の検索で、同 trace 内の旧リポジトリ名、`docs/memo.json`、`current_file_content`、`recently_viewed_code_snippets` は 0 件、synthetic path は 3 件、`Synthetic Fixture` は 2 件だった。
- 代表値として `invoke_agent` の latency は `8799ms`、token usage は `16,921 -> 233 (total 34,034)`、最終 generation の latency は `1997ms`、token usage は `192 -> 57 (total 17,046)` だった。

## 2026-05-05: M9 OTel Collector 経由送信の仕様化と最小実装
- M9 では、Langfuse 直接送信を Phase 1 baseline として残し、直接送信が不安定な場合や後続の組織展開候補に備えるための Collector 経由送信を追加候補として扱う。
- ローカル Collector は Docker Desktop 上で起動し、クライアントからは OTLP/gRPC `localhost:4317` と OTLP/HTTP `http://localhost:4318` で受ける。
- Collector の host port binding は `127.0.0.1` に限定し、content capture を含む telemetry receiver を外部 interface に公開しない。
- Collector から Langfuse へは `http://host.docker.internal:3000/api/public/otel` に OTLP HTTP exporter で送信する。
- Langfuse 認証は `LANGFUSE_AUTH=Base64(public-key:secret-key)` として環境変数で渡し、repository 内の Collector config や Compose example には secret を保存しない。
- `docker compose config` は環境変数展開後の値を出力するため、Compose 構文確認では実 credential ではなく dummy `LANGFUSE_AUTH` を使う。
- Config CLI の Collector 向けコマンドはクライアント送信先を `http://localhost:4318` に変更し、Langfuse Basic Auth header は出力しない。Langfuse 認証は Collector 側で付与する。
- 同じ shell で Langfuse 直接送信から Collector 経由送信へ切り替える場合に備え、Collector 向けコマンドは `OTEL_EXPORTER_OTLP_HEADERS`、`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`、`OTEL_EXPORTER_OTLP_TRACES_HEADERS` を解除する。
- M9 の Collector example は trace pipeline のみを有効にし、metrics pipeline は扱わない。
- M9 では masking / redaction、TLS、SSO、共有環境運用、Resource Attributes の Collector 側自動付与、sampling は扱わない。
- Collector 経由のライブ確認は、Langfuse 起動後に Collector を起動し、VS Code GitHub Copilot Chat / GitHub Copilot CLI の送信先を `http://localhost:4318` に向けて、Langfuse 上で trace、prompt / response、tool span、token usage、`client.kind`、`experiment.id` を確認する。

## 2026-05-19: M10 週次 Gitleaks secret scan workflow
- M10 は Langfuse / OTel 観測データの DLP ではなく、repository hygiene として git 履歴全体を gitleaks CLI で週次 scan する。
- schedule は月曜 09:00 JST (`0 0 * * 1`) とし、`workflow_dispatch` も有効にする。
- gitleaks CLI は `v8.30.1` 固定、Linux x64 tarball の SHA-256 checksum verification 後に実行する。
- finding ありの場合は毎回新規 GitHub Issue を作成し、workflow は成功扱いにする。scan 失敗、checksum 不一致、Issue 作成失敗は workflow 失敗にする。
- Issue には commit link、file、line、RuleID、Fingerprint を記載する。secret 値、match 文字列、secret の前後文脈、redacted report 全文は記載しない。
- Issue 発行確認のため、ユーザー依頼により `tests/fixtures/gitleaks/intentional-finding.env` に synthetic GitHub token 形式の fake value を追加した。実 credential ではなく、確認後に削除する前提の一時 fixture とする。

## 2026-05-05: M9 Collector 経由送信の手動ライブ確認結果
- Langfuse self-host は Docker Desktop 上で起動中で、`http://localhost:3000` が HTTP 200 を返した。
- Collector example は `otel/opentelemetry-collector-contrib:0.114.0` で起動し、host 側では `127.0.0.1:4317` と `127.0.0.1:4318` で listen した。
- Collector container 内 receiver は `0.0.0.0:4317` / `0.0.0.0:4318` で起動するため Collector warning は出るが、Docker host port binding は `127.0.0.1` に限定されていた。
- `http://localhost:4318/v1/traces` への GET は `405 Method Not Allowed` で、OTLP HTTP POST 用 endpoint として到達可能な状態だった。
- VS Code GitHub Copilot Chat から Collector 経由で Langfuse に trace `8ca6f6422ccbd9d4ca34e2d443c7cf4a` が取り込まれた。
- 同 VS Code trace の resource attributes には `service.name=copilot-chat`、`service.version=0.46.2`、`client.kind=vscode-copilot-chat`、`experiment.id=baseline` が含まれた。
- 同 VS Code trace では `invoke_agent GitHub Copilot Chat`、`chat gpt-5.4-mini`、`read_file`、`manage_todo_list`、`embeddings text-embedding-3-small-512` を確認し、prompt / response、tool span、token usage を確認した。
- GitHub Copilot CLI から Collector 経由で Langfuse に trace `844864ac23f39dacb525ec252ddeab76` が取り込まれた。
- 同 CLI trace の resource attributes には `service.name=github-copilot`、`service.version=1.0.40`、`client.kind=copilot-cli`、`experiment.id=baseline` が含まれた。
- 同 CLI trace では `invoke_agent`、`chat gpt-5.3-codex`、`view`、`report_intent` を確認し、prompt / response、tool span、token usage を確認した。
- 合成 fixture 確認として、両 trace で `otel-synthetic-cli-check` と `Synthetic Fixture` を検出し、旧リポジトリ名、`docs/memo.json`、`current_file_content`、`recently_viewed_code_snippets` は 0 件だった。

## 2026-05-05: Follow-up Config CLI 既定 endpoint の Phase 1 Langfuse 化
- Follow-up task では、Config CLI の汎用コマンド `vscode-settings`、`vscode-env`、`copilot-cli-env` の既定 endpoint を Phase 1 Langfuse 直接送信用の `http://localhost:3000/api/public/otel` に切り替える判断を採用した。
- 汎用 env コマンドは Langfuse Basic Auth header と trace-specific endpoint / headers も出力する。
- `langfuse-*` コマンドは Langfuse 直接送信を明示するコマンドとして維持し、`collector-*` コマンドは Collector 経由送信用に `http://localhost:4318` と Langfuse header cleanup を維持する。
- Phase 0 Aspire HTTP 用の新規コマンドは今回追加しない。

## 2026-05-24: M11 以降の研究計測化 Issue 分割
- ユーザー確認により、ゼロベース化せず、既存の M0-M10 を Copilot OTel / Langfuse 観測基盤として継承する判断を採用した。
- GitHub Issues は実作業単位、`docs/task.md` は同期されるチェックリストとして扱う。
- M11-M13 は研究計画書とのスコープ整合、measurement schema、模擬保守タスクセット定義を扱う。
- M14-M16 は Langfuse export / API 調査、集計 CLI / script MVP、turn count / tool call count 算出ルールを扱う。
- M17-M19 は baseline 実行手順、小規模 dry run、本計測を扱う。
- M20-M22 は品質非劣化 rubric、variant / A-B 計測プロトコル、結果レポート雛形を扱う。
- M10 follow-up の gitleaks fixture 削除と、共有環境・実データ検証の事前仕様化は Phase E / Backlog として独立 Issue にした。

## 2026-05-24: エージェント改善案生成基盤の実現可能性調査
- 実現可能性の判断は、エージェントが自律的に自分を直接修正する基盤ではなく、trace-driven agent improvement loop として作るなら現実的、というもの。
- 推奨 loop は、trace / metrics / rubric の収集、failure taxonomy / anti-pattern 分類、改善候補生成、baseline / variant 評価、人間承認の順に進める。
- M11-M22 はこの loop の前提であり、measurement schema、baseline 計測、rubric、variant 比較プロトコルを先に固める必要がある。
- M23 以降の候補は、failure taxonomy / anti-pattern 定義、trace-to-diagnosis MVP、improvement proposal generator、proposal evaluator、human approval workflow。
- 改善候補は `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` のいずれかに分類する。
- 自動 repository 修正、自動 commit、自動 push、自動 pull request、自動勝敗決定は引き続き既定スコープ外とする。
- Issue #8 に scope コメント、Issue #17 に rubric / failure taxonomy コメント、Issue #18 に variant / A-B と自動改善研究コメントを追加した。

参考文献・参考システム:
- OpenTelemetry GenAI semantic conventions: https://opentelemetry.io/docs/specs/semconv/gen-ai/
- Langfuse Scores / evaluation docs: https://langfuse.com/docs/evaluation/scores/overview
- Langfuse Scores via API / SDK: https://langfuse.com/docs/evaluation/evaluation-methods/custom-scores
- OpenInference: https://arize-ai.github.io/openinference/
- Reflexion: Language Agents with Verbal Reinforcement Learning: https://arxiv.org/abs/2303.11366
- Self-Refine: Iterative Refinement with Self-Feedback: https://arxiv.org/abs/2303.17651
- DSPy: Compiling Declarative Language Model Calls into Self-Improving Pipelines: https://arxiv.org/abs/2310.03714
- Automatic Prompt Optimization with Gradient Descent and Beam Search: https://arxiv.org/abs/2305.03495
- Promptbreeder: Self-Referential Self-Improvement Via Prompt Evolution: https://arxiv.org/abs/2309.16797
- TextGrad: Automatic Differentiation via Text: https://arxiv.org/abs/2406.07496
- GEPA: Reflective Prompt Evolution Can Outperform Reinforcement Learning: https://arxiv.org/abs/2507.19457
- TRAIL: Trace Reasoning and Agentic Issue Localization: https://arxiv.org/abs/2505.08638

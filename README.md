# Copilot Agent Observability

Copilot Agent Observability は、GitHub Copilot Chat、GitHub Copilot CLI、Codex App から出力される OpenTelemetry data を収集し、Agent workflow の実行過程を trace、集計 dataset、診断候補、静的 dashboard として確認するための Local-first な観測基盤です。

利用者数や利用回数を見るための dashboard ではありません。Agent がどの client で動き、どの tool を呼び、どれだけ時間や token を使い、どこで失敗し、どの改善候補につながるかを確認するための製品です。

## 動作モード

| Mode | 目的 | 入力 | 主な出力 |
| --- | --- | --- | --- |
| Live Trace Review | 個別 trace を人間が調査する | VS Code Copilot Chat / Copilot CLI / Codex App の OTLP | Langfuse trace viewer |
| Raw Data Loop | Langfuse UI に依存せず再現可能に集計する | saved raw OTLP JSON | SQLite raw store, normalized measurements |
| Static Dashboard | Agent workflow の傾向を俯瞰する | dashboard dataset JSON | `index.html`, `dashboard-data.json`, GitHub Pages snapshot |
| Diagnosis / Improvement Support | 失敗傾向と改善候補を deterministic に整理する | normalized measurements, candidate inputs | diagnosis candidate, improvement candidate, auto-decision, human decision records |

## Dashboard Overview

静的 dashboard は synthetic fixture だけで生成でき、server-side API や外部 network dependency を要求しません。

<p align="center">
  <img width="900" alt="Static dashboard overview" src="./docs/assets/screenshots/static-dashboard-overview.png">
</p>

filter、search、sort は browser 上で完結します。

<p align="center">
  <img width="900" alt="Static dashboard filters" src="./docs/assets/screenshots/static-dashboard-filters.png">
</p>

## できること

- VS Code GitHub Copilot Chat、GitHub Copilot CLI の OTel trace / metrics / events を収集し、任意で Codex App / app-server の OTel 出力も扱う。
- collection profile で telemetry routing mode を切り替える。
- Langfuse で prompt、response、tool call、token usage、duration、error を trace 単位で確認する。
- saved raw OTLP JSON を SQLite raw store に取り込み、normalized measurement dataset を生成する。
- trace 由来の diagnosis candidate、improvement candidate、auto-decision、human decision record を生成・検証する。
- normalized dataset と candidate outputs から dashboard dataset を作り、静的 HTML dashboard と GitHub Pages snapshot を生成する。

## しないこと

- 個人別の生産性評価、勤務監視、ランキング。
- Copilot の利用者数、利用回数、課金、コスト配賦の管理。
- DLP、機密情報検査、監査ログ基盤の代替。
- VS Code Agent Debug / Chat Debug View の再実装。
- trace から repository patch / diff を生成すること。
- repository file の自動修正、commit、push、pull request 作成。
- 改善効果の自動合否判定。

## 必要なもの

| 項目 | 用途 |
| --- | --- |
| GitHub Copilot を利用できるアカウント | Copilot Chat / CLI の実行 |
| VS Code + GitHub Copilot Chat extension | VS Code 側 telemetry source |
| GitHub Copilot CLI | CLI 側 telemetry source |
| .NET SDK | Config CLI、build、test |
| PowerShell | Windows 向け設定例の実行 |
| Docker Desktop | `docker-desktop-langfuse` / `docker-desktop-collector-langfuse` profile |
| WSL2 Docker Engine | `wsl2-docker-langfuse` / `wsl2-docker-collector-langfuse` profile |

`raw-only` は最小必須 profile であり、Langfuse、Docker Desktop、WSL2 Docker Engine、Collector、remote endpoint、background process なしで saved raw OTLP JSON から raw data loop を実行します。

WARNING: remote managed Langfuse / Collector endpoint、共有環境、実データ公開、GitHub Pages 公開、社内サーバー運用を行う場合は、送信前に access control、retention、削除方法、masking / redaction、利用者周知または同意、identity handling、credential handling を先に決めてください。この repository は remote / shared endpoint の利用者同意 workflow を実装しません。

## Collection Profiles

Profile selector:

```text
CAO_COLLECTION_PROFILE
```

Required support profiles:

| Profile | 用途 |
| --- | --- |
| `raw-only` | 最小構成。保存済み raw OTLP JSON から raw data loop を実行する |
| `docker-desktop-langfuse` | 標準 full profile。Docker Desktop 上の Langfuse で live trace review を行う |
| `docker-desktop-collector-langfuse` | Docker Desktop 上の Collector 経由で Langfuse へ relay する |
| `wsl2-docker-langfuse` | WSL2 Docker Engine 上の Langfuse へ Windows client から送信する |
| `wsl2-docker-collector-langfuse` | WSL2 Docker Engine 上の Collector 経由で Langfuse へ relay する |
| `remote-managed-langfuse` | 管理された remote Langfuse endpoint へ送信する |
| `remote-managed-collector` | 管理された remote Collector endpoint へ送信する |
| `raw-local-receiver` | この repository の local receiver が VS Code から直接 telemetry を受ける |

WSL2 Docker Engine profile の endpoint placeholder `<windows-reachable-wsl2-host>` は、Windows client から到達できる host に置き換えます。WSL2 localhost forwarding が使える場合は `localhost` を使い、machine-specific IP address は repository file に保存しないでください。

## 最短手順

1. Docker Desktop を起動し、Langfuse self-host をローカルで起動する。
2. Langfuse で project と API key を作成する。
3. Config CLI で client 向け OTel 設定サンプルを出力する。
4. VS Code Copilot Chat または Copilot CLI を OTel 設定付きで起動する。
5. 検証用または synthetic data だけを使って Copilot を実行する。
6. Langfuse UI で trace を確認する。
7. saved raw OTLP JSON がある場合は raw data loop と static dashboard を生成する。

代表コマンド:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile docker-desktop-langfuse
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-copilot-cli-env --profile docker-desktop-langfuse
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw <raw.json> --db data\raw-store.db
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --json tmp\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\measurements.json --json tmp\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard.json --out-dir tmp\site
```

Synthetic fixture だけで dashboard を試す例:

```powershell
New-Item -ItemType Directory -Force tmp\dashboard-demo | Out-Null
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site
```

## データ安全境界

Repository に保存してよいもの:

- synthetic fixture
- redacted summary
- normalized aggregate dataset
- sanitized dashboard dataset
- trace id、candidate id、evidence ref などの参照 ID
- 実データ由来の aggregate metrics

Repository に保存してはいけないもの:

- raw prompt / raw response
- system prompt の全文
- tool arguments / tool results の全文
- observed session 由来の source code fragment / file contents
- credential、secret、token、API key、password
- Base64 authorization header
- sensitive bundle content
- sensitive bundle local path

`user.id` と `user.email` は dashboard dataset と static dashboard の表示・filter 対象にできます。ただし、共有または公開する前に repository / Pages access control と利用者周知を確認してください。

## ドキュメント

- [利用者向け詳細ガイド](docs/user-guide.md)
- [要件定義](docs/requirements.md)
- [技術仕様索引](docs/spec.md)
- [実装仕様](docs/specifications/README.md)
- [Contributor Guide](docs/contributor-guide.md)
- [Architecture](docs/architecture.md)
- [Decisions](docs/decisions.md)
- [Roadmap / History](docs/task.md)

## 開発者向け検証

Code、project file、CLI behavior、workflow を変更した場合は以下を実行します。

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

`dotnet test` includes the LocalMonitor Playwright smoke test. Run the
Playwright install after `dotnet build` because the script is generated under
the test project's `bin\Debug\net10.0` directory. In Linux CI, use
`install --with-deps chromium`.

Collector example を変更した場合は、実 credential ではなく dummy value で Compose 構文を確認します。

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

## 参考にした構成

README の構成は、画像付きで利用者に機能を説明している [github-copilot-resources/copilot-metrics-viewer](https://github.com/github-copilot-resources/copilot-metrics-viewer) を参考にしています。

# copilot-agent-observability

GitHub Copilot Chat / GitHub Copilot CLI の OpenTelemetry (OTel) データをローカルの Langfuse に収集し、Agent / MCP / Skills / CLI の挙動を trace 単位で確認するための検証リポジトリです。

このリポジトリは、Copilot の利用回数や利用者数を集計するためのものではありません。Agent がどのように考え、どの tool を呼び、どのくらい token や時間を使い、どこで error が起きたかを確認し、今後の改善検討に使うための PoC です。

## まず読むもの

初めて使う人は、次の順で読んでください。

1. [docs/getting-started.md](docs/getting-started.md): 利用者向けの準備、起動、設定、確認手順
2. [docs/spec.md](docs/spec.md): 確定仕様、実装判断、検証方針
3. [docs/task.md](docs/task.md): repository 全体の sprint / roadmap index
4. [docs/requirements.md](docs/requirements.md): 上位要件

## このリポジトリでできること

- VS Code GitHub Copilot Chat の OTel trace を Langfuse に送る
- GitHub Copilot CLI の OTel trace / metrics を Langfuse に送る
- Langfuse 上で prompt、response、tool call、token usage、duration、error を確認する
- `client.kind` で VS Code Chat と CLI を区別する
- `experiment.id` や `task.id` などで baseline / variant / task を分類する
- sanitized した Langfuse export から研究用 CSV / JSON を生成する
- saved raw OTLP JSON を SQLite raw store に取り込み、Langfuse なしで normalized dataset を生成する
- trace 由来の診断、改善提案、人間承認記録を deterministic な CLI で扱う

## 対象範囲

| 対象 | 扱い |
| --- | --- |
| VS Code GitHub Copilot Chat | 必須 |
| GitHub Copilot CLI | 必須 |
| Claude Code | 参考のみ |
| Visual Studio 2026 | 対象外 |

## 現在の既定構成

Sprint1: ローカル Langfuse PoC は完了済みです。
Sprint1 baseline は、Langfuse self-host への直接 OTLP 送信です。

```text
VS Code GitHub Copilot Chat
GitHub Copilot CLI
        |
        | OTLP HTTP
        v
Langfuse self-host on Docker Desktop
```

| 用途 | URL |
| --- | --- |
| Langfuse UI | `http://localhost:3000` |
| Langfuse OTLP endpoint | `http://localhost:3000/api/public/otel` |
| Langfuse OTLP traces endpoint | `http://localhost:3000/api/public/otel/v1/traces` |

M9 では、直接送信が不安定な場合や後続の組織展開候補に備えて、ローカル OTel Collector 経由送信の最小サンプルも追加しています。ただし、Sprint1 baseline は Langfuse への直接送信です。

Sprint2: Raw Data Loop も完了済みです。Sprint2 では、Langfuse が起動していなくても saved raw OTLP JSON を SQLite raw store に取り込み、normalized dataset と改善支援 CLI に接続できる最小 loop を追加しています。Langfuse UI は raw data の source of truth ではなく、人間が trace を確認する dashboard / trace viewer の optional side path として扱います。

```text
saved raw OTLP JSON
        |
        | ingest-raw
        v
SQLite raw store (data/raw-store.db)
        |
        | normalize-raw
        v
normalized dataset
        |
        v
diagnosis / proposal / evaluation / human decision workflow
```

## 利用前に必要なもの

申請や契約が必要になり得るもの:

- GitHub Copilot を利用できる GitHub アカウントまたは組織契約
- GitHub Copilot CLI を利用できる認証状態
- Docker Desktop の利用許可。組織利用では Docker Desktop のライセンス条件も確認してください。

ローカルに準備するもの:

- VS Code
- GitHub Copilot Chat extension
- GitHub Copilot CLI
- Docker Desktop
- .NET SDK
- PowerShell

Langfuse Cloud、Grafana、社内サーバー、SSO、TLS 終端は、現在のローカル PoC では必須ではありません。

## 最短の利用手順

詳細は [docs/getting-started.md](docs/getting-started.md) を参照してください。

1. GitHub Copilot Chat と GitHub Copilot CLI が利用できることを確認する。
2. Docker Desktop を起動する。
3. Langfuse self-host Docker Compose を起動する。
4. `http://localhost:3000` で Langfuse の初期ユーザー、organization、project、API key を作成する。
5. Config CLI で VS Code / CLI 向けの OTel 設定サンプルを出力する。
6. VS Code または Copilot CLI を OTel 環境変数付きで起動する。
7. Copilot に合成データまたは検証用データだけを使った依頼を実行する。
8. Langfuse UI で trace、prompt、response、tool call、token usage、duration、error を確認する。

Config CLI の代表コマンド:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-vscode-settings
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-vscode-env
dotnet run --project src\CopilotAgentObservability.ConfigCli -- langfuse-copilot-cli-env
```

Langfuse 非依存 raw data loop の代表コマンド:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw <raw.json> --db data\raw-store.db
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw data\raw-store.db --csv <measurements.csv> --json <measurements.json>
```

この loop の入力には synthetic raw OTLP JSON だけを使ってください。詳細な手順は [docs/getting-started.md](docs/getting-started.md) を参照してください。

## データ扱いの注意

Phase 1 では content capture を有効にします。そのため、prompt、response、source code、file contents、tool arguments、tool results、system prompt、tool schema、path information が Langfuse に保存され得ます。

ローカル PoC では、合成データまたは検証用データだけを投入してください。実データ、顧客データ、秘密情報、credential、secret、Base64 header、実 trace content、実 user identity を repository に保存しないでください。

Sprint2 の raw store 既定 path は `data/raw-store.db` です。`data/`、一時 CSV / JSON output、raw payload file は local runtime data であり、repository に commit しないでください。不要になったら削除してください。

## このリポジトリがしないこと

- Copilot の利用状況ダッシュボードを作る
- 利用者数、利用回数、日次アクティブユーザーを集計する
- 経営向けの課金・コスト配賦をする
- DLP や機密情報検査を実装する
- VS Code Agent Debug / Chat Debug View 相当の UI を作る
- VS Code 内部ログや workspaceStorage を主方式として解析する
- trace から failure category / anti-pattern 候補を自動抽出する
- 改善案を自動採用する
- repository を自動修正する
- patch / diff / commit / push / pull request を自動作成する

## 開発者向け検証

Config CLI、AppHost、プロジェクトファイル、依存関係を変更した場合は、次を実行します。

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Collector example を変更した場合は、実 credential ではなく dummy の `LANGFUSE_AUTH` を使って Compose 構文を確認します。

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

## ドキュメント

- [docs/getting-started.md](docs/getting-started.md): 利用者向けガイド
- [docs/requirements.md](docs/requirements.md): 上位要件
- [docs/spec.md](docs/spec.md): 確定仕様・実装判断の主 source of truth
- [docs/task.md](docs/task.md): repository 全体の sprint / roadmap index
- [docs/sprints/sprint1-langfuse-poc/](docs/sprints/sprint1-langfuse-poc/): Sprint1 の完了済み PoC 資料
- [docs/sprints/sprint2-raw-data-loop/](docs/sprints/sprint2-raw-data-loop/): Sprint2 の完了済み raw data loop 資料

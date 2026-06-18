# Requirements: GitHub Copilot Chat / CLI の OTel データを活用した Agent / MCP / Skills / CLI 改善基盤

## 1. 文書の位置づけ

この文書は、GitHub Copilot Chat / GitHub Copilot CLI の OpenTelemetry データを活用し、Agent / MCP / Skills / CLI の改善検討に必要な観測・分析基盤の要件を定義する。

本書は `requirements.md` として管理する。

本書では、以下を定義する。

* 背景
* 目的
* 非目的
* 対象範囲
* 収集要件
* Resource Attributes 要件
* 可視化・分析要件
* 推奨アーキテクチャ
* セキュリティ・機密情報の扱い
* 保持期間
* PoC 成功条件
* 未決事項
* 参考資料

詳細仕様、設計、実装手順、環境構築手順、運用手順は本書の対象外とし、必要に応じて別文書で定義する。
初回利用者向けの準備、起動、設定、確認手順は `README.md` と `docs/getting-started.md` で説明する。

本書と詳細仕様に矛盾がある場合は、原則として本書を優先する。

---

## 2. 背景

GitHub Copilot Chat および GitHub Copilot CLI は、OpenTelemetry による観測データの出力に対応している。

VS Code GitHub Copilot Chat では、traces / metrics / events を出力でき、agent interaction、LLM call、tool execution、token usage などを観測できる。

GitHub Copilot CLI でも、OpenTelemetry による traces / metrics の出力に対応しており、agent interactions、LLM calls、tool executions、token usage などを観測できる。

Codex App / app-server も OpenTelemetry による観測データの opt-in 出力に対応している。Codex App の OTel routing 設定は user-level の `~/.codex/config.toml` に置き、project-local `.codex/config.toml` は OTel routing key の source of truth として扱わない。

本プロジェクトでは、これらの OTel データを収集・可視化し、GitHub Copilot の単純な利用状況把握ではなく、Agent / MCP / Skills / CLI の設計・運用を改善するための判断材料として活用する。

VS Code Agent Debug / Chat Debug View は、個別セッションを開発者が手動で調査する用途として有効である。
本プロジェクトはそれらを代替・再実装せず、公式 OTel 出力を observability backend に送信し、後から trace を保存・確認・比較できる状態を作ることに限定する。

---

## 3. 目的

本基盤の目的は、GitHub Copilot Chat / GitHub Copilot CLI の実行過程を OpenTelemetry として収集し、Agent / MCP / Skills / CLI の挙動を把握・分析できる状態を作ることである。

本基盤は、Agent / MCP / Skills / CLI の改善そのものを実行するものではない。

本基盤が提供する価値は、以下である。

* Agent の実行過程を trace 単位で確認できること
* LLM call、tool call、tool arguments、tool results、token usage、duration、error を確認できること
* MCP tool や CLI 実行の挙動を観測できること
* Instructions / Skills / Agent 定義 / MCP tool schema / CLI wrapper の改善検討に必要な観測情報を取得できること
* VS Code GitHub Copilot Chat と GitHub Copilot CLI の挙動差分を確認できること
* `experiment.id` 等により、baseline / variant / experiment ごとに trace を分類・確認できること
* 将来的な改善施策の前提となる baseline trace を保存・参照できること
* 研究計測用の trace 集計データを再現可能に作成できること
* baseline / variant の比較に必要な token、turn count、tool call count、duration、error count、success status を確認できること
* Agent workflow の健全性、コスト、失敗傾向、改善候補を dashboard として継続的に確認できること

個別セッションの手動デバッグは VS Code Agent Debug / Chat Debug View の役割とし、本基盤は継続的な trace 収集・保存・比較の検証基盤として扱う。

M11 以降では、既存の OTel / Langfuse 観測基盤を前提に、研究実施計画書に基づく計測・集計・評価支援を扱う。
M11-M22 では、改善案生成、改善案の自動実装、自動採用、勝敗の自動決定は本基盤の責務に含めない。
Sprint3 では、trace content を含む観測データから deterministic rule により診断候補、改善候補、自動採用判断を含む auto-decision record を生成する後続基盤を扱う。
実 repository 修正を伴う自動改善実装、patch / diff 生成は Sprint3 と Sprint4 の既定スコープには含めず、Sprint5 以降の候補として安全境界を定義してから扱う。
Sprint3 では、既存 M24-M27 human-review command / schema を置換せず互換性維持対象とし、candidate pipeline から既存 record へ渡す adapter / mapping contract を実装前に定義する。
Sprint3 candidate pipeline は synthetic automated verification に加え、ユーザー協業による redacted real-trace E2E で GitHub Copilot CLI と GitHub Copilot Chat の入力互換性を確認する。
Sprint4 では、既存 OTel / raw store / normalized dataset / candidate pipeline を前提に、Agent workflow 観測 dashboard の要件、表示項目、集計 contract、非目的、検証方法を定義する。
Sprint4 の dashboard は、AI workflow 改善判断に使う観測ビューであり、組織全体の利用監視、個人別生産性評価、経営向け利用状況 dashboard とは扱わない。

---

## 4. 非目的

以下は本プロジェクトの目的に含めない。

* VS Code Agent Debug / Chat Debug View 相当のデバッグ UI
* VS Code 内部ログやローカル chat 履歴の解析
* VS Code workspaceStorage / chatSessions 監視を主方式にすること
* M11-M22 における Agent / MCP / Skills / CLI の改善そのもの
* M11-M22 における改善案生成
* 改善優先順位の自動決定
* M11-M22 における修正差分の自動生成
* Sprint3 以前におけるローカルファイルの自動修正
* M11-M22 における改善案の自動採用
* commit / push / pull request 作成
* 改善効果の自動合否判定
* 組織全体の Copilot 利用量把握
* 利用者数、利用回数、日次アクティブユーザーの集計
* 個人別の生産性評価、勤務監視、ランキング
* 課金・コスト配賦
* 経営向け利用状況ダッシュボード
* 監査ログ基盤
* DLP / 機密情報検査基盤
* Claude Code の本番収集
* Visual Studio 2026 の収集

利用状況の追跡は GitHub 公式 API で実施可能なため、本基盤では主目的に含めない。

Claude Code は、本 PoC の直接収集対象ではなく、可観測性設計や運用事例の参考として扱う。

---

## 5. 対象範囲

### 5.1 収集対象

| 対象 | 扱い | 備考 |
| --- | --- | --- |
| VS Code GitHub Copilot Chat | 必須 | Agent mode / tool execution / prompt / response / token usage の観測対象 |
| GitHub Copilot CLI | 必須 | CLI 経由の agent 実行、tool call、subagent、token usage の観測対象 |
| Codex App | 任意 | Codex Desktop / app-server 経由の agent 実行、tool call、token usage、duration の観測対象 |
| Claude Code | 参考のみ | 本 PoC の直接収集対象外 |
| Visual Studio 2026 | 対象外 | 今回は VS Code、CLI、Codex App に限定 |

### 5.2 観測対象

本基盤では、以下を観測対象とする。

* trace
* metrics
* events
* span
* span attributes
* span events
* prompt content
* response content
* system prompt
* tool schema
* tool arguments
* tool results
* token usage
* model information
* duration
* error information
* session id
* run id
* user id
* user email
* team id
* department
* client kind
* experiment id

### 5.3 分析対象

本基盤では、以下の分析を可能にする。

* Agent trace review
* Tool / MCP behavior review
* Prompt / Skill / Instructions review
* CLI behavior review
* VS Code Chat と CLI の挙動比較
* baseline trace の確認
* experiment / variant ごとの trace 絞り込み
* 研究計測用 dataset への trace 集計
* baseline / variant の比較表作成
* 人間が評価可能な品質非劣化 rubric の管理

ただし、Sprint2.5 までと M11-M22 の研究計測フェーズでは、これらは改善判断のための観測・分析であり、改善の実施や改善効果の保証は本基盤の責務ではない。
Sprint3 では、明示的に仕様化した範囲に限り、診断候補生成、改善候補生成、自動採用判断を含む auto-decision record 生成を後続スコープとして扱う。
実 repository 修正を伴う自動改善実装は Sprint5 以降の候補とする。

---

## 6. 基本方針

### 6.1 利用状況追跡ではなく、Agent / MCP / Skills / CLI 改善基盤とする

本基盤は、Copilot の利用者数や利用頻度を把握するためのものではない。

本基盤では、以下のような問いに答えるための観測データを提供する。

* Agent がどのような手順で実行されたか
* LLM call がどの程度発生しているか
* tool call がどの程度発生しているか
* tool arguments はどのように渡されているか
* tool results はどのように返されているか
* token usage はどの span / operation に紐づいているか
* tool error や timeout は発生しているか
* MCP tool の使われ方を確認できるか
* CLI 経由の実行で、shell / file / search 操作がどのように発生しているか
* VS Code Chat と CLI で挙動に差があるか
* Instructions / Skills / MCP / CLI wrapper の改善検討に必要な trace が取得できるか

### 6.2 改善実施はスコープ外とする

本基盤は、改善判断に必要なデータを提供する。

Sprint2.5 までと M11-M22 の研究計測フェーズでは、以下は本基盤のスコープ外とする。

* 改善候補の自動決定
* 改善案の自動実装
* repository file の自動修正
* patch / diff の自動生成
* commit / push / pull request の自動作成
* 改善施策の優先順位決定
* 改善効果の自動合否判定

Sprint3 では、改善候補の deterministic 生成と、自動採用判断を含む auto-decision record 生成を扱う。
実 repository file の自動修正、patch / diff の生成、commit / push / pull request 作成は、Sprint3 と Sprint4 では扱わず、Sprint5 以降で安全境界を定義してから扱う。

### 6.3 PoC では最大収集を前提とする

PoC では、Agent / MCP / Skills / CLI の挙動を詳細に確認するため、content capture を有効化する。

そのため、PoC では以下の取得を前提とする。

* prompt
* response
* system prompt
* tool schema
* tool arguments
* tool results

VS Code GitHub Copilot Chat では、`github.copilot.chat.otel.captureContent` を `true` に設定する。

GitHub Copilot CLI では、content capture に対応する環境変数を有効化する。

### 6.4 span 名に過度に依存しない

`invoke_agent`、`chat`、`execute_tool` などの span 名は、クライアント実装やバージョンにより変化し得る。

本基盤では、特定の span 名だけに依存せず、以下のような論理カテゴリで扱えることを求める。

* agent invocation
* LLM call
* tool call
* permission / approval
* file operation
* shell command
* error
* user interaction

---

## 7. 推奨アーキテクチャ

### 7.1 Phase 0: ローカル疎通確認

最初はローカル一台で疎通確認する。

```text

VS Code GitHub Copilot Chat
        ↓ OTLP
Aspire Dashboard

```

目的は以下である。

* OTel が出力されることを確認する
* trace tree が見えることを確認する
* agent invocation / LLM call / tool call の階層が確認できることを確認する
* content capture 有効時に prompt / response / tool arguments / tool results が取得できることを確認する

### 7.2 Phase 1: ローカル Langfuse PoC

本命の PoC では Langfuse を使用する。

```text
VS Code GitHub Copilot Chat
GitHub Copilot CLI
Codex App
        ↓ OTLP
Langfuse
```

この Phase では以下を確認する。

* VS Code Copilot Chat の trace が Langfuse に入る
* Copilot CLI の trace が Langfuse に入る
* Codex App / app-server の trace / logs / metrics が Langfuse または Collector に入る
* prompt / response / tool arguments / tool results が確認できる
* token usage が確認できる
* tool call の名前、引数、結果、duration、error が確認できる
* `experiment.id` による baseline / variant / experiment の絞り込みができる

### 7.3 Phase 2: 組織展開向け Collector 構成

組織展開時は、クライアントから Langfuse に直接送信せず、OpenTelemetry Collector を中継する構成を候補とする。

```text
各ユーザー端末
  ├─ VS Code GitHub Copilot Chat
  └─ GitHub Copilot CLI
        ↓ OTLP
社内 OTel Collector
        ↓
Langfuse
```

将来的には必要に応じて以下に fan-out する。

```text
社内 OTel Collector
  ├─ Langfuse
  ├─ Grafana / Tempo
  ├─ Loki
  └─ 集計用DB
```

Collector を挟む理由は以下である。

* 認証を集約できる
* マスキング処理を後付けしやすい
* サンプリングを制御できる
* 送信先を切り替えやすい
* Langfuse と Grafana 等に同時送信しやすい
* 組織・チーム属性を付与しやすい
* 将来 Claude Code 等を追加しやすい

---

## 8. 可視化・分析基盤の方針

### 8.1 第一候補: Langfuse

今回の目的では、Grafana より Langfuse を優先する。

理由は、本基盤の目的が SRE 的なメトリクス監視ではなく、LLM Agent の実行過程の分析であるため。

Langfuse で重視する分析対象は以下である。

* trace tree
* prompt
* response
* system prompt
* tool schema
* tool arguments
* tool results
* token usage
* duration
* error
* experiment / variant

### 8.2 Grafana / Tempo / Loki / Mimir の扱い

Grafana 系は初期 PoC の第一候補にはしない。

ただし、将来的に以下が必要になった場合は追加する。

* tool 別 p95 latency
* tool 別 error rate
* 日次 token 推移
* client.kind 別の trace duration 推移
* team.id 別の aggregate metrics
* 長期メトリクス監視
* アラート

Sprint4 では、この将来候補を具体化し、Grafana 等に載せる前提で必要な dashboard dataset と panel 要件を定義する。
ただし、Sprint4 要件定義時点では Grafana Cloud、Tempo、Loki、Mimir、Datadog、New Relic 等の採用を固定しない。
dashboard は raw prompt / response / tool result の全文を一覧化する場所ではなく、trace viewer や sensitive bundle への最小参照を持つ集計ビューとして扱う。
Sprint4 M4 の dashboard prototype path では、Microsoft Learn の AI coding agents 向け Grafana dashboard 構成を参考に、Grafana-first の集計 dashboard prototype を第一候補として比較する。
この場合も Langfuse は置き換えず、個別 trace の span tree、prompt / response、tool arguments / results、token usage、error を調査する drilldown 先として維持する。

### 8.3 raw store と PostgreSQL の扱い

PostgreSQL は、生 OTel の主ストレージとしては扱わない。

利用する場合は、以下の用途に限定する。

* 集計済みサマリ
* trace id と分析メモの対応管理
* 実験条件の管理
* マスキング済みデータの検索用テーブル

Sprint2 MVP では、Langfuse に依存しない raw JSON / raw OTLP 保持基盤を扱う。
Sprint2 MVP の既定入力は raw OTLP の file-based ingest とし、自前 HTTP receiver は含めない。
raw store は SQLite をローカル PoC の既定とし、PostgreSQL は共有環境・長期保持・検索性能が必要になった場合の将来候補として扱う。
Sprint2 MVP では、raw store から normalized dataset を生成し、既存 measurement schema と改善支援 CLI に接続する。
`diagnose` は引き続き人間分類 diagnosis record の validation とし、trace から failure category / anti-pattern 候補を自動抽出する診断機能は Sprint2 MVP に含めない。
trace からの自動診断は Sprint3 で扱う。

Sprint3 では、trace からの deterministic な自動診断候補生成に加え、content-aware evidence、改善候補生成、自動採用判断を含む auto-decision record 生成の基盤を扱う。
Sprint3 の出力は、明示 opt-in の sensitive local output として、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を含むことを許容する。
これらの sensitive output は repository に保存・commit しない。
Sprint3 の sensitive local output は expiry metadata と削除対象 path を持つが、自動削除 command は実装しない。
削除はユーザーが manifest を確認して手動で実施する。
実 repository 修正を伴う自動改善実装は Sprint5 以降の候補として扱う。

---

## 9. 収集要件

### 9.1 共通で収集する情報

PoC では最大収集を前提にする。

収集対象は以下である。

* trace
* metrics
* events
* prompt content
* response content
* system prompt
* tool schema
* tool arguments
* tool results
* token usage
* model information
* duration
* error information
* session id
* run id
* user id
* user email
* team id
* department
* client kind
* experiment id

### 9.2 VS Code GitHub Copilot Chat

VS Code Copilot Chat では以下を有効化する。

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:3000/api/public/otel",
  "github.copilot.chat.otel.captureContent": true
}
```

VS Code Copilot Chat について、以下を満たすこと。

* OTel 出力を有効化できること
* OTLP HTTP exporter を使用して送信できること
* endpoint を設定できること
* `github.copilot.chat.otel.captureContent=true` を有効化できること
* captureContent 有効時に、prompt / response / system prompt / tool schema / tool arguments / tool results を確認できること
* token usage を確認できること
* duration を確認できること
* error を確認できること
* span attributes / span events を確認できること

### 9.3 GitHub Copilot CLI

GitHub Copilot CLI では以下の環境変数を使用する。

```powershell
$env:COPILOT_OTEL_ENABLED="true"
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:3000/api/public/otel"
$env:OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT="true"
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=copilot-cli,experiment.id=baseline"
```

GitHub Copilot CLI について、以下を満たすこと。

* OTel 出力を有効化できること
* OTLP endpoint を設定できること
* file exporter または OTLP exporter による出力方式を確認できること
* content capture を有効化できること
* CLI 実行に関連する trace / metrics を確認できること
* tool call、shell command、file operation、error、token usage を確認できること
* span attributes / span events を確認できること

---

## 10. Resource Attributes 要件

### 10.1 必須属性

`OTEL_RESOURCE_ATTRIBUTES` に以下を設定する。

```text
user.id
user.email
team.id
department
client.kind
experiment.id
```

例:

```powershell
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=123456,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode-copilot-chat,experiment.id=baseline"
```

### 10.2 推奨属性

可能であれば以下も付与する。

```text
repo.name
workspace.name
task.id
task.run_index
experiment.condition
prompt.version
repo.snapshot
agent.variant
skill.version
mcp.profile
cli.wrapper.version
task.category
```

例:

```powershell
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=123456,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode-copilot-chat,repo.name=example-repo,agent.variant=default,skill.version=v1,experiment.id=baseline"
```

### 10.3 client.kind の利用方針

`client.kind` は、VS Code GitHub Copilot Chat と GitHub Copilot CLI を識別するために使用する。

値の例:

```text
vscode-copilot-chat
copilot-cli
```

### 10.4 experiment.id の利用方針

`experiment.id` は、baseline / variant / experiment を分類し、trace を絞り込むために使用する。

値の例:

```text
baseline
skill-v1
skill-v2
mcp-result-shaping-v1
instructions-slim-v1
cli-wrapper-v1
```

比較対象の例:

```text
baseline vs skill-v2
baseline vs instructions-slim-v1
skill-v1 vs skill-v2
mcp-result-shaping-v1 vs mcp-result-shaping-v2
```

ただし、本基盤の PoC 成功条件は改善効果の評価ではなく、OTel データを収集・確認できることである。

---

## 11. 分析ビュー要件

### 11.1 Agent Trace Review

目的:

```text
1回の依頼が、どのように実行されたかを trace tree で確認する。
```

確認対象:

* root span
* agent invocation
* LLM call
* tool call
* tool name
* tool arguments
* tool results
* token usage
* duration
* error
* prompt
* response
* system prompt

確認する問い:

* Agent がどのような順序で処理したか
* LLM call がどの span に対応しているか
* tool call がどの span に対応しているか
* tool arguments がどのように渡されているか
* tool results がどのように返されているか
* token usage が確認できるか
* error が確認できるか

---

### 11.2 Tool / MCP Behavior Review

目的:

```text
MCP tool や内部 tool の挙動を確認する。
```

確認対象:

* tool name
* tool call count
* tool error count
* tool duration
* tool result size
* tool arguments
* tool results
* permission result
* timeout
* repeated call
* argument shape
* result shape

確認する問い:

* どの tool が呼び出されたか
* tool arguments を確認できるか
* tool results を確認できるか
* tool call の duration を確認できるか
* tool error を確認できるか
* permission error や timeout を確認できるか

---

### 11.3 Prompt / Skill / Instructions Review

目的:

```text
Instructions / Skills / Agent 定義の改善検討に必要な観測情報を確認する。
```

確認対象:

* `experiment.id`
* system prompt
* system prompt size
* prompt
* prompt size
* response
* input tokens
* output tokens
* total tokens
* LLM call count
* turn count
* tool call count
* trace duration
* error count
* agent variant
* skill version

確認する問い:

* system prompt が取得できるか
* prompt / response が取得できるか
* input tokens / output tokens が取得できるか
* tool call count が取得できるか
* turn count が取得できるか
* `experiment.id` で trace を絞り込めるか
* `skill.version` や `agent.variant` により variant を識別できるか

---

### 11.4 CLI Behavior Review

目的:

```text
GitHub Copilot CLI の挙動を VS Code GitHub Copilot Chat と区別して確認する。
```

確認対象:

* `client.kind=copilot-cli`
* command / shell 系 tool call
* file operation
* search / glob / grep 系 tool call
* permission span
* error
* token usage
* trace duration
* exit code
* command type

確認する問い:

* CLI 実行の trace を確認できるか
* CLI における tool call を確認できるか
* shell / git / test 実行を確認できるか
* file operation を確認できるか
* search / glob / grep 系の操作を確認できるか
* VS Code Chat と CLI の trace 差分を確認できるか

---

### 11.5 VS Code Chat / CLI 差分確認

目的:

```text
VS Code GitHub Copilot Chat と GitHub Copilot CLI の trace を同一基盤上で確認し、挙動差分を把握できるようにする。
```

確認対象:

* trace structure
* LLM call
* tool call
* token usage
* duration
* error
* prompt
* response
* tool arguments
* tool results
* Resource Attributes
* span attributes
* span events

---

### 11.6 Agent Workflow Observability Dashboard

目的:

```text
Agent / MCP / Skills / CLI の実行傾向、失敗傾向、コスト、改善候補を継続的に俯瞰し、trace review や human review の優先順位を判断できるようにする。
```

主な利用者:

* Agent / MCP / Skills / CLI の改善担当者
* Developer experience / platform engineering 担当者
* baseline / variant 計測を行う研究・評価担当者
* Sprint3 candidate pipeline の human review 担当者

Sprint4 dashboard は、経営層、労務管理者、個人別生産性評価者を主利用者にしない。

意思決定対象:

* どの trace / workflow / task を優先的に trace review するか
* どの tool / MCP / CLI 操作を改善候補として調査するか
* どの diagnosis / improvement / auto-decision candidate を human review に回すか
* baseline / variant のどの差分を regression candidate として扱うか
* OTel 収集、Resource Attributes、normalization、candidate generation のどの欠損を先に直すか
* Sprint4 M4 で Grafana JSON dashboard、static report、repository-local preview のどれを prototype path として採用するか

確認対象:

* trace count
* success / error / excluded count
* p50 / p95 trace duration
* p50 / p95 time to first token または time to first chunk
* input / output / total tokens
* estimated cost
* LLM call count
* tool call count
* tool error count
* repeated tool call count
* retry count
* subagent / nested agent call count
* long-running turn / stuck session count
* approval / permission result
* unknown span / missing attribute count
* `client.kind`
* `experiment.id`
* `experiment.condition`
* `agent.variant`
* `prompt.version`
* `skill.version`
* diagnosis candidate count
* improvement candidate count
* auto-decision status count
* human-review backlog count

確認する問い:

* どの workflow / variant / client で duration、token、tool call、error が増えているか
* response 開始の遅さと workflow 完了の遅さを分けて見られるか
* tool call の回数が多い箇所と、合計 duration が大きい箇所を分けて見られるか
* subagent / nested agent / retry / approval 待ちが duration や失敗の主因になっていないか
* trace review すべき失敗や異常値がどこに集中しているか
* Sprint3 candidate pipeline が出した diagnosis / improvement / auto-decision の分布を確認できるか
* baseline と variant の比較に必要な集計が dashboard 上で確認できるか
* OTel 収集、Resource Attributes、normalization の欠損を検知できるか
* raw content を表示せず、必要な場合だけ trace id や evidence ref から詳細調査へ進めるか

Sprint4 dashboard の初期ビューは以下に分ける。

* Run Overview
* Agent / Tool Behavior
* Prompt / Skill / Instructions
* Baseline vs Variant
* Diagnosis / Improvement Loop
* Collection Health
* Outcome Linkage Candidate

各 view では、集計 dashboard と trace detail を混在させない。
dashboard は count、rate、percentile、sum、status distribution、sanitized reference を扱い、prompt / response / tool arguments / tool results の全文確認は Langfuse trace viewer、raw store、sensitive bundle 等への drilldown で行う。
Sprint4 の prototype 方針は Grafana-first dashboard + Langfuse drilldown を第一候補とする。
Grafana は Run Overview、Agent / Tool Behavior、Baseline vs Variant、Collection Health の集計表示に使い、Langfuse は個別 trace detail の調査に使う。
Grafana Cloud / Azure Managed Grafana / Application Insights / Tempo / Loki / Mimir 等の本番採用は Sprint4 M1 では決めない。

Run Overview は、日次・週次の実行傾向、duration、token、estimated cost、LLM call、tool call、TTFT、stuck session を俯瞰する。
Agent / Tool Behavior は、tool count ranking だけでなく、tool total duration ranking、tool error ranking、timeout、retry、approval / permission、subagent / nested agent 待ちを分けて確認する。
Baseline vs Variant は、同一 `task.id` / `repo.snapshot` / `client.kind` を前提に、success status、duration、TTFT、token、estimated cost、tool failure rate、candidate count を比較する。
Prompt / Skill / Instructions は、`prompt.version`、`skill.version`、`agent.variant` の変更が token、turn count、tool call、error、candidate distribution に与える影響を確認する。
Diagnosis / Improvement Loop は、diagnosis candidate、improvement candidate、auto-decision、human-review backlog の件数だけでなく、severity、rule、proposed change kind、review status の滞留を確認する。
Collection Health は、OTel signal、Resource Attributes、normalization、unknown span、missing required attribute、candidate generation failure を確認する。

Outcome Linkage Candidate は、GitHub / Notion / issue / PR 等の外部成果データと接続する将来候補である。
Sprint4 要件定義では、入力側の Agent workflow 観測と同じ dashboard に並べるための指標候補を定義するまでとし、外部 ETL、認証、個人識別情報の結合、組織展開は実装しない。
品質・安全性評価指標、利用量・ROI 指標、チーム別利用状況は将来候補として扱えるが、Sprint4 初期 dashboard では Agent workflow 改善判断に必要な aggregate / sanitized 指標に限定する。

---

## 12. セキュリティ・機密情報の扱い

### 12.1 基本方針

本基盤は DLP / 機密情報検査基盤を目的としない。

ただし、PoC では content capture を有効化するため、観測データに機密情報が含まれる可能性を前提にする。

### 12.2 content capture の扱い

content capture により、以下の情報が取得され得る。

* user prompt
* response
* source code
* file contents
* tool arguments
* tool results
* system prompt
* tool schema
* path information
* repository information

PoC では、Agent / MCP / Skills / CLI の挙動を詳細に確認するため、content capture を有効化する。

本番展開時には、以下を別途定義する。

* 対象環境
* 対象リポジトリ
* 収集対象データ
* 保存先
* アクセス権
* 保持期間
* 削除方法
* masking / redaction 方針
* 利用者への周知または同意要否

### 12.3 個人識別属性の扱い

PoC では、`user.id` および `user.email` を必須属性として扱う。

ただし、これらは Agent / MCP / Skills / CLI の観測データを分類・分析するための属性であり、個人別の生産性評価や勤務監視を目的としない。

本番展開時には、個人識別属性の利用目的、保持期間、アクセス権、削除方法を別途定義する。

---

## 13. 保持期間

初期 PoC では以下とする。

| データ                                               |        保持期間 |
| ------------------------------------------------- | ----------: |
| prompt / response / tool arguments / tool results |         30日 |
| full trace                                        |         30日 |
| span metadata                                     |         90日 |
| aggregate metrics                                 |          1年 |
| 分析メモ                                              | 手動削除または別途定義 |

本番展開時には、マスキング済みデータの保持期間を別途定義する。

---

## 14. PoC 成功条件

Phase 1 PoC の成功条件は、OTel 収集の成功に限定する。

改善効果の評価、改善案の生成、修正差分の生成、改善実施は Phase 1 PoC 成功条件に含めない。
個別セッションを VS Code Agent Debug / Chat Debug View で手動確認できることも、本 PoC の成功条件には含めない。

### 14.1 VS Code GitHub Copilot Chat の OTel 収集成功条件

以下を満たすこと。

* VS Code GitHub Copilot Chat の OTel 出力を取得できる
* trace が observability backend に取り込まれる
* span tree を確認できる
* agent invocation を確認できる
* LLM call を確認できる
* tool call を確認できる
* token usage を確認できる
* duration を確認できる
* error を確認できる
* prompt を確認できる
* response を確認できる
* system prompt を確認できる
* tool schema を確認できる
* tool arguments を確認できる
* tool results を確認できる
* `user.id` を確認できる
* `user.email` を確認できる
* `team.id` を確認できる
* `department` を確認できる
* `client.kind` を確認できる
* `experiment.id` を確認できる

### 14.2 GitHub Copilot CLI の OTel 収集成功条件

以下を満たすこと。

* GitHub Copilot CLI の OTel 出力を取得できる
* trace または metrics が observability backend に取り込まれる
* CLI 実行に関する span を確認できる
* agent invocation を確認できる
* LLM call を確認できる
* tool call を確認できる
* token usage を確認できる
* duration を確認できる
* error を確認できる
* prompt を確認できる
* response を確認できる
* tool arguments を確認できる
* tool results を確認できる
* `user.id` を確認できる
* `user.email` を確認できる
* `team.id` を確認できる
* `department` を確認できる
* `client.kind=copilot-cli` を確認できる
* `experiment.id` を確認できる

### 14.3 共通成功条件

以下を満たすこと。

* VS Code GitHub Copilot Chat と GitHub Copilot CLI の trace を同一基盤上で確認できる
* `client.kind` によりクライアント種別を識別できる
* `experiment.id` により trace を絞り込める
* `user.id`、`user.email`、`team.id`、`department` により trace を分類できる
* 取得される span attributes / events / metrics の一覧を確認できる
* content capture により取得される prompt / response / tool arguments / tool results を確認できる

### 14.4 研究計測フェーズの成功条件

M11 以降の研究計測フェーズでは、以下を満たすことを成功条件とする。

* 研究計測用 Resource Attributes と集計列が定義されている
* 4 類型の模擬保守タスクを定義できる
* Langfuse から trace / observation / usage / metadata を取得する方式が決まっている
* 合成 fixture から研究用 CSV / JSON を生成できる
* baseline の実行手順、記録様式、除外基準が定義されている
* token、turn count、tool call count、duration、error count、success status を trace 単位で確認できる
* 品質非劣化 rubric が人間の評価用に定義されている
* variant / A-B 比較の計測プロトコルが定義されている

### 14.5 Sprint4 dashboard 要件定義の成功条件

Sprint4 dashboard 要件定義では、以下を満たすことを成功条件とする。

* dashboard の目的、非目的、利用者、意思決定対象が定義されている
* Run Overview、Agent / Tool Behavior、Prompt / Skill / Instructions、Baseline vs Variant、Diagnosis / Improvement Loop、Collection Health、Outcome Linkage Candidate の初期ビューが定義されている
* 各 view の panel、metric、dimension、filter、drilldown 先が定義されている
* TTFT / trace duration / tool duration / retry / subagent / approval / stuck session を分けて確認する要件が定義されている
* raw prompt / response / tool arguments / tool results を dashboard dataset に既定保存しない方針が定義されている
* normalized measurement、diagnosis candidate、improvement candidate、auto-decision record から dashboard dataset を作れるかが確認されている
* Grafana 等の可視化基盤を採用する場合の最小 data contract が定義されている
* 集計 dashboard と Langfuse trace viewer / sensitive bundle への drilldown の責務分担が定義されている
* GitHub / Notion / issue / PR 等の outcome linkage は将来候補として境界が定義されている
* Sprint4 で実装する範囲と、Sprint5 以降へ送る範囲が分離されている

---

## 15. 未決事項

詳細仕様検討では、以下を検討する。

* Langfuse の self-host 構成
* WSL2 / Docker Desktop / 社内サーバーのどれを PoC 実行基盤にするか
* Copilot CLI の content capture 設定で取得できる具体的な属性
* VS Code GitHub Copilot Chat と Copilot CLI の属性差分
* Langfuse での trace 検索・export 方法
* OTel Collector を使用するか
* OTel Collector の設定
* OTLP endpoint の認証方式
* `OTEL_EXPORTER_OTLP_HEADERS` の配布方法
* `OTEL_RESOURCE_ATTRIBUTES` の自動生成方法
* user.id の付与方法
* user.email の付与方法
* team.id の付与方法
* department の付与方法
* `client.kind` の命名
* `experiment.id` の命名
* `session.id` / `run.id` の生成方法
* content capture の利用条件
* masking / redaction の必要範囲
* 保持期間
* アクセス権
* 本番展開可否

---

## 16. 参考資料

### 16.1 VS Code 公式: Monitor agent usage with OpenTelemetry

VS Code GitHub Copilot Chat の OTel 設定、収集される signals、content capture、Aspire Dashboard / Jaeger / Langfuse 連携例を確認するための参考資料。

### 16.2 microsoft/vscode-copilot-chat の monitoring docs

VS Code GitHub Copilot Chat の GitHub 側ドキュメント。Langfuse 連携設定、remote collector with authentication、file output、console output などの例がある。

### 16.3 GitHub Docs: Copilot CLI command reference

GitHub Copilot CLI の OTel 公式情報。Copilot CLI が traces / metrics を出力し、GenAI Semantic Conventions に従うこと、OTel が有効化される条件を確認するための参考資料。

### 16.4 Zenn: GitHub Copilot Chat エージェントの振る舞いを OTel で分析する

日本語の実践記事。OTel の signals、trace / metrics / logs-events の役割、Copilot Chat の観測の流れを把握するための参考資料。

### 16.5 Zenn: GitHub Copilot CLI も OTel で観測する

GitHub Copilot CLI の trace を Langfuse に流し、agent invocation、LLM call、tool call、token usage、tool call metrics を観測する事例。

### 16.6 Langfuse OpenTelemetry integration

Langfuse の OTel / GenAI Semantic Conventions 対応、attribute mapping を確認するための参考資料。

### 16.7 Langfuse GitHub repository

Langfuse 本体の公開リポジトリ。LLM observability、metrics、evals、prompt management、datasets などを確認するための参考資料。

### 16.8 microsoft/vscode Issue: Agent Observability based on OpenTelemetry

VS Code / Copilot Chat の Agent Observability に関する関連 issue。trace-based trajectory visualization、debugging、monitoring、post-run analysis、multi-session tracking などの論点を確認するための参考資料。

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

本書と詳細仕様に矛盾がある場合は、原則として本書を優先する。

---

## 2. 背景

GitHub Copilot Chat および GitHub Copilot CLI は、OpenTelemetry による観測データの出力に対応している。

VS Code GitHub Copilot Chat では、traces / metrics / events を出力でき、agent interaction、LLM call、tool execution、token usage などを観測できる。

GitHub Copilot CLI でも、OpenTelemetry による traces / metrics の出力に対応しており、agent interactions、LLM calls、tool executions、token usage などを観測できる。

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

個別セッションの手動デバッグは VS Code Agent Debug / Chat Debug View の役割とし、本基盤は継続的な trace 収集・保存・比較の検証基盤として扱う。

M11 以降では、既存の OTel / Langfuse 観測基盤を前提に、研究実施計画書に基づく計測・集計・評価支援を扱う。
ただし、改善案の自動生成、自動実装、勝敗の自動決定は引き続き本基盤の責務に含めない。

---

## 4. 非目的

以下は本プロジェクトの目的に含めない。

* VS Code Agent Debug / Chat Debug View 相当のデバッグ UI
* VS Code 内部ログやローカル chat 履歴の解析
* VS Code workspaceStorage / chatSessions 監視を主方式にすること
* Agent / MCP / Skills / CLI の改善そのもの
* 改善案の自動生成
* 改善優先順位の自動決定
* 修正差分の自動生成
* ローカルファイルの自動修正
* commit / push / pull request 作成
* 改善効果の自動合否判定
* 組織全体の Copilot 利用量把握
* 利用者数、利用回数、日次アクティブユーザーの集計
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
| Claude Code | 参考のみ | 本 PoC の直接収集対象外 |
| Visual Studio 2026 | 対象外 | 今回は VS Code と CLI に限定 |

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

ただし、これらは改善判断のための観測・分析であり、改善の実施や改善効果の保証は本基盤の責務ではない。

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

以下は本基盤のスコープ外とする。

* 改善候補の自動決定
* 改善案の自動実装
* repository file の自動修正
* patch / diff の自動生成
* commit / push / pull request の自動作成
* 改善施策の優先順位決定
* 改善効果の自動合否判定

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
        ↓ OTLP
Langfuse
```

この Phase では以下を確認する。

* VS Code Copilot Chat の trace が Langfuse に入る
* Copilot CLI の trace が Langfuse に入る
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

### 8.3 PostgreSQL の扱い

PostgreSQL は、生 OTel の主ストレージとしては扱わない。

利用する場合は、以下の用途に限定する。

* 集計済みサマリ
* trace id と分析メモの対応管理
* 実験条件の管理
* マスキング済みデータの検索用テーブル

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
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=123456,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode,experiment.id=baseline"
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
$env:OTEL_RESOURCE_ATTRIBUTES="user.id=123456,user.email=user@example.com,team.id=platform,department=engineering,client.kind=vscode,repo.name=example-repo,agent.variant=default,skill.version=v1,experiment.id=baseline"
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

## 12. セキュリティ・機密情報の扱い

### 12.1 基本方針

本基盤は DLP / 機密情報検査基盤を目的としない。

ただし、PoC では content capture を有効化するため、観測データに機密情報が含まれる可能性を前提にする。

### 12.1.1 Repository secret scan

repository hygiene として、GitHub Actions から週次で gitleaks CLI による secret scan を実行する。
この scan は観測データの DLP / 機密情報検査基盤ではなく、repository に commit された secret の検知を目的とする。

scan は repository の git 履歴全体を対象とし、検知された場合は毎回新規 GitHub Issue を作成する。
Issue には secret 値、match 文字列、secret の前後文脈、redacted report 全文を記載しない。
Issue には検知件数、workflow run URL、commit link、file、line、RuleID、Fingerprint を記載する。

secret 検知自体では workflow を失敗扱いにしない。
gitleaks CLI の download / checksum verification / 実行失敗、または Issue 作成失敗は workflow 失敗として扱う。

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

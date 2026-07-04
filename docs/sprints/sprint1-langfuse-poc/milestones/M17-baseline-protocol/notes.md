# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-05-25: baseline 実行単位

M17 では、baseline 計測を `client.kind` 別に数える方針にした。
Phase 1 と M11 以降の仕様では VS Code GitHub Copilot Chat と GitHub Copilot CLI の両方を研究計測対象としているため、`vscode-copilot-chat` と `copilot-cli` を合算せず、別条件として同じ task / prompt / condition を反復する。

既定の実行量は以下とする。

| Milestone | 実行量 |
| --- | --- |
| M18 | 1 類型 x 2 client_kind x 2 runs |
| M19 | 4 類型 x 2 client_kind x N=10 |

baseline の既定属性は以下とする。

```text
experiment.id=baseline
experiment.condition=baseline
agent.variant=baseline
prompt.version=v1
repo.snapshot=synthetic-dotnet-fixture-v1
```

## CSV 台帳テンプレート

M18 / M19 の実測記録は CSV 台帳を既定とする。
M17 では台帳ファイル自体は作成せず、列定義を仕様化する。
Markdown には要約、判断理由、検証記録を残し、trace 単位の実測行は CSV 台帳に記録する。
CSV 台帳は repository に commit する成果物とはしない。
共有または repository 保存が必要になった場合は、実 credential、実 trace content、実ユーザーデータ、実 prompt content、実 email address、Base64 化済み header、secret を含まないように sanitization してから扱う。

```csv
run_id,task_id,task_category,client_kind,task_run_index,experiment_id,experiment_condition,prompt_version,agent_variant,repo_snapshot,started_at,completed_at,operator_id,environment,resource_attributes,prompt_used,langfuse_trace_id,langfuse_trace_url,trace_found,trace_checked_at,input_tokens,output_tokens,total_tokens,turn_count,tool_call_count,duration_ms,error_count,success_status,run_status,failure_type,exclusion_reason,retry_of_run_id,notes
```

各 run は `task.id + client.kind + task.run_index + experiment.condition + prompt.version + agent.variant + repo.snapshot` の組み合わせで識別する。
`run_id` は台帳上の一意な実行 ID とし、除外 run の再実行時も再利用しない。
`operator_id` は実名や実 email address ではなく、ローカル計測用の仮名または空欄にする。
`resource_attributes` は raw 環境変数 dump ではなく、M17 で許可した Resource Attribute key の値だけを記録し、secret、token、credential、content 由来の key は記録しない。
`prompt_used` は M13 で定義した task id / prompt version / synthetic prompt 参照として記録し、実 prompt 本文や実データを含む prompt は記録しない。
`langfuse_trace_id` は、M12 schema の `trace_id` へ写像する。

## 失敗・欠損 trace・除外

`run_status` は以下の 5 値とする。

| run_status | 扱い |
| --- | --- |
| `completed` | Copilot 実行が完了し、対応する Langfuse trace を確認できた |
| `failed` | Copilot 実行自体が失敗した |
| `trace-missing` | Copilot 実行は完了したが、対応する Langfuse trace を確認できない |
| `excluded` | 実行記録は残すが、baseline 集計から除外する |
| `not-run` | 予定された run が未実行である |

手順違反、誤った task / client / Resource Attributes、実データ混入リスク、明らかな環境障害は `excluded` とし、`exclusion_reason` を必須にする。
`failed`、`trace-missing`、`excluded` は baseline の有効 N には数えない。
再実行時は新しい `run_id` を作り `retry_of_run_id` に元 run を記録する。

`trace_found` は `true`、`false`、空欄のいずれかにする。
未実行や trace 確認前は空欄を使用する。
`failure_type` は、`copilot-error`、`langfuse-unavailable`、`trace-missing`、`wrong-attributes`、`wrong-task`、`wrong-client-kind`、`real-data-risk`、`operator-error`、`environment-error`、`other`、空欄のいずれかにする。

実データ混入リスクがある run では、汚染された Langfuse trace を削除するか、必要に応じてローカル Langfuse volume を破棄する。
その場合でも台帳行自体は残すが、`prompt_used`、raw `resource_attributes`、`langfuse_trace_url`、content を含む `notes` は残さず、sanitized した `exclusion_reason` だけを記録する。

M20 の品質非劣化 rubric が確定するまでは、`success_status=not-evaluated` を既定とする。

## 実行前チェック

実行前に以下を確認する。

- Langfuse self-host UI と OTLP endpoint に到達できる。
- Basic Auth header と content capture 設定が対象クライアントに渡っている。
- `OTEL_RESOURCE_ATTRIBUTES` に必須属性と研究用属性が含まれる。
- 入力 prompt と fixture は M13 の synthetic fixture に限定する。
- 実 credential、Base64 化済み header、実 trace content、実ユーザーデータは repository に保存しない。

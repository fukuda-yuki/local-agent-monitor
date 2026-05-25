# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-05-25: CLI 側 dry run 部分実施

M18 の full scope は `maint-refactor-001` の 1 類型 x 2 `client.kind` x 2 runs である。
この時点では `copilot-cli` 側 2 runs のみ実施し、`vscode-copilot-chat` 側 2 runs は未実行だったため、M18 は未完了として扱った。

preflight では Docker Desktop を起動し、既存の local Langfuse self-host containers が起動していること、Langfuse UI が `http://localhost:3000` で HTTP 200 を返すことを確認した。
Langfuse project key は ignored な local `.env` から shell 内だけで読み込み、repository には保存していない。

CLI 側 dry run の結果:

| client_kind | planned runs | completed runs | trace found | aggregation rows | run_status |
| --- | ---: | ---: | ---: | ---: | --- |
| `copilot-cli` | 2 | 2 | 2 | 2 | `completed` |
| `vscode-copilot-chat` | 2 | 0 | 0 | 0 | 未実行 |

CLI 側の sanitized aggregation では、token、turn count、tool call count、duration、error count、`success_status=not-evaluated` を 2 行出力できた。
sanitized snapshot には prompt body、response body、tool arguments/results、credential、Base64 header、実 trace content を含めず、Resource Attributes も M12 schema に必要な実験識別属性に限定した。

M20 の rubric は未定義のため、CLI 側 2 runs の `success_status` は `not-evaluated` のままとした。
count の矛盾検出や品質判定は M18 の dry run 実施範囲では変更せず、後続の集計改善または rubric milestone の検討候補として残す。

ローカル作業用の CSV 台帳と sanitized aggregation artifacts は `tmp/m18-baseline-dry-run/` に置いた。
`tmp/` は `.gitignore` で除外されており、CSV 台帳は M17 の方針どおり repository commit 対象にしない。

## 2026-05-26: M18 full scope dry run 完了

`vscode-copilot-chat` 側の `task_run_index=1,2` を追加実行し、M18 の 1 類型 x 2 `client.kind` x 2 runs が完了した。
VS Code Copilot Chat では 1 回の Chat 実行で複数 trace が出るため、`invoke_agent GitHub Copilot Chat` の trace を各 run の代表 trace として台帳に記録した。

M18 dry run の最終結果:

| client_kind | planned runs | completed runs | trace found | aggregation rows | run_status |
| --- | ---: | ---: | ---: | ---: | --- |
| `copilot-cli` | 2 | 2 | 2 | 2 | `completed` |
| `vscode-copilot-chat` | 2 | 2 | 2 | 2 | `completed` |

4 trace から content と identity-bearing 属性を除いた sanitized snapshot を作成し、`aggregate-measurements` で 4 行の CSV / JSON を生成できた。
CLI と VS Code Copilot Chat の両方で、`trace_id`、`client_kind`、`task_id`、`task_run_index`、token、turn count、tool call count、duration、error count、`success_status` が schema に接続できることを確認した。
VS Code Copilot Chat 側の集計では `unknown_spans_json` が非空になったが、これは M16 の方針どおり、分類ルール外の observation を破棄せず最小識別情報だけ残す診断用 spillover として扱う。
prompt、response、tool arguments/results、raw attributes は `unknown_spans_json` に含めない。

`success_status` は M20 の品質非劣化 rubric が未定義のため、全 run `not-evaluated` のままとした。
実 prompt body、response body、tool arguments/results、credential、Base64 header、実 trace content は repository に保存していない。

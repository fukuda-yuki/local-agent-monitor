# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-05-24

M13 では、baseline / variant 比較に使う初期の模擬保守タスクセットを 4 類型 x 1 件で定義した。
類型は M12 measurement schema の `task.category` と合わせ、`refactoring`、`bug-investigation`、`test-generation`、`code-review` の 4 値に固定した。

初期タスクは以下とした。

| task.id | task.category | 狙い |
| --- | --- | --- |
| `maint-refactor-001` | `refactoring` | 小さな .NET CLI 処理の重複や責務混在を、外部仕様を変えずに整理できるかを見る |
| `maint-bug-001` | `bug-investigation` | 合成入力で再現する token 集計系の不具合について、原因と最小修正案を分けて説明できるかを見る |
| `maint-test-001` | `test-generation` | 純粋ロジックに対して、正常系、境界、異常系の deterministic unit tests を提案できるかを見る |
| `maint-review-001` | `code-review` | 小さな疑似 diff を仕様根拠付きでレビューし、仕様逸脱、テスト不足、保守リスクを分けられるかを見る |

fixture はすべて synthetic .NET code fixture とし、実ユーザーデータ、顧客データ、秘密情報、実 credential、実運用ログは使わない。
M13 では fixture の内容、入力条件、期待観点を文書化するだけに留め、実 fixture ファイル作成や集計 CLI 入力 fixture は M15 以降で扱う。

`prompt.version` の初期値は `v1`、`repo.snapshot` の初期値は `synthetic-dotnet-fixture-v1` とした。
これらの dotted 名は Resource Attribute key であり、M12 の研究用 dataset では `prompt_version`、`repo_snapshot` に正規化される。
追加タスク数、反復回数、実行前チェック、Langfuse trace id の記録様式、除外基準は M17 以降に委譲する。
`pass`、`fail`、`needs-review` の厳密な判定基準は M20 の品質非劣化 rubric に委譲する。

Sub-Agent レビュー後の補正:

- `maint-refactor-001` の prompt は、実装指示と誤読されにくいように「変更案を示す」と表現した。
- `maint-bug-001` は、M12 の `total_tokens` 方針と矛盾しないように、source の `total_tokens` が欠損している場合の fallback 合計不具合に限定した。
- `maint-test-001` の必須属性欠損は、validation error 固定ではなく、M12 の null / 空欄表現と補足記録に接続できることを確認する観点として扱う。

除外した選択肢:

- 実 repository の現在のコードを直接 fixture として使う案は採用しない。計測時点の実装差分に左右され、反復性が下がるため。
- 実 Langfuse trace、実運用ログ、実ユーザープロンプトを使う案は採用しない。M13 の範囲は合成データに限定されるため。
- M13 で fixture ファイルを作成する案は採用しない。M13 の完了条件はタスクセット定義であり、M15 の入力 fixture 作成と分離するため。

# Notes

この milestone 内に閉じる調査結果、判断理由、検証メモを記録する。

## 2026-05-24

M14 では、Langfuse 上の trace / observation / usage / metadata を研究用 dataset に取り出す方式を比較した。

候補と判断は以下。

| 方式 | 判断 |
| --- | --- |
| Public API legacy trace / observation read | M15 の既定入力にする。ローカル self-host Langfuse で再現可能で、保存 JSON を合成 fixture / sanitized snapshot として扱いやすい |
| UI export | CSV / JSON の one-off export として手動診断に使えるが、M15 MVP の既定入力にはしない |
| Blob Storage export | scheduled export / 大量 export 候補。self-host でも候補になるが、M15 MVP の最小入力にはしない |
| Observations API v2 | 新規 data extraction 向けだが、M14 時点では Cloud-only と公式 docs にあるため self-host baseline の既定にはしない |
| ClickHouse 直接参照 | self-host の内部 storage を直接見る最後の調査・復旧候補。M15 MVP の既定入力にはしない |

M15 の入力契約は、Public API の legacy trace / observation read response を保存した JSON 形状に合わせる。
入力には trace、observation、usage、metadata、Resource Attributes を保持し、M12 の欠損値方針に従って CSV では空欄、JSON では `null` に写像できるようにする。
`turn_count` / `tool_call_count` の正式分類は M16 に残す。

認証情報は repository に保存しない。
API key、Base64 化済み header、Langfuse 管理者パスワード、実 trace content、実ユーザーデータ、顧客データ、実運用ログは M15 fixture に含めない。

参照した公式 docs:

- https://langfuse.com/docs/api-and-data-platform/features/public-api
- https://langfuse.com/docs/api-and-data-platform/features/observations-api
- https://langfuse.com/docs/api-and-data-platform/features/export-from-ui
- https://langfuse.com/docs/api-and-data-platform/features/export-to-blob-storage

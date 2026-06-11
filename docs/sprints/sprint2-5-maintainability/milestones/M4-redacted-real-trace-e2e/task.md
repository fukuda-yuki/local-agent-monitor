# M4: Redacted real-trace E2E

## 目的

Synthetic fixture だけでなく、redacted real Copilot trace 由来 input が raw data loop を通ることを確認し、Sprint3 前の互換性 evidence を残す。

M4 は実データ収集基盤の拡張ではない。
repository に保存できるのは redacted または sanitized された evidence と手順記録だけである。

## 完了条件

- [ ] 実 Copilot trace 由来 input の取得手順と redaction 方針が記録されている
- [ ] 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を repository に保存していない
- [ ] redacted input で `ingest-raw` が成功している
- [ ] raw store から `normalize-raw` が成功している
- [ ] measurement CSV / JSON に取得可能な `trace_id`、`client_kind`、token、duration、error、tool call count が出力されることを確認している
- [ ] 必要に応じて diagnosis / proposal / evaluation / human decision まで接続している
- [ ] evidence markdown に、確認日時、環境、入力の redaction 方針、実行コマンド、生成物、確認済み項目、未確認項目が記録されている

## タスク分解

1. 実 Copilot trace 由来 input の取得元と保存場所を決める。未加工 payload は repository commit 対象外に置く。
2. redaction 方針に従い、repository に保存可能な summary または sanitized input だけを用意する。
3. `ingest-raw` で temp SQLite raw store に取り込む。
4. `normalize-raw` で measurement CSV / JSON を生成する。
5. 取得可能な列と欠損している列を確認する。
6. downstream workflow へ接続する場合は、人間が sanitized diagnosis input を作成し、proposal / evaluation / human decision まで通す。
7. evidence markdown と review 記録を作成する。

## 検証

- `ingest-raw <redacted-raw.json> --db <temp-raw-store.db>`
- `normalize-raw <temp-raw-store.db> --csv <temp.csv> --json <temp.json>`
- 必要に応じて `validate-diagnoses`、`generate-improvement-proposals`、`evaluate-improvement-proposals`、`generate-decision-template`、`record-human-decisions`
- repository diff に secret、credential、Base64 header、実 identity、実 content が含まれていないことの確認

## 注意事項

- 未加工の実 trace payload を repository に保存しない。
- evidence は確認可能な事実と未確認項目を分けて書く。
- trace-driven automatic diagnosis は実装しない。

# M4: Redacted real-trace E2E

## 目的

Synthetic fixture だけでなく、redacted real Copilot trace 由来 input が raw data loop を通ることを確認し、Sprint3 前の互換性 evidence を残す。

M4 は実データ収集基盤の拡張ではない。
repository に保存できるのは redacted または sanitized された evidence と手順記録だけである。

## 完了条件

- [x] 実 Copilot trace 由来 input の取得手順と redaction 方針が記録されている
- [x] 実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を repository に保存していない
- [x] redacted input で `ingest-raw` が成功している
- [x] raw store から `normalize-raw` が成功している
- [x] measurement CSV / JSON に取得可能な `trace_id`、`client_kind`、token、duration、error、tool call count が出力されることを確認している
- [x] 必要に応じて diagnosis / proposal / evaluation / human decision まで接続している
- [x] evidence markdown に、確認日時、環境、入力の redaction 方針、実行コマンド、生成物、確認済み項目、未確認項目が記録されている

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

## 実施記録

- 2026-06-11: M4 を開始し、`evidence.md` に redacted input の取得手順、保存場所、redaction 方針、実行コマンド、確認項目を記録した。repository 内に M4 用 redacted real-trace input はまだないため、E2E 実行は redacted payload 提供後に行う。
- 2026-06-11: GitHub Copilot CLI 1.0.57 の OTel file exporter で実 Copilot CLI 由来 span を取得し、content / identity / tool payload を redaction した OTLP envelope を `tmp\m4-redacted-real-trace-e2e\redacted-raw.json` に生成した。
- 2026-06-11: redacted input で `ingest-raw -> normalize-raw` が成功し、measurement JSON / CSV に `trace_id=0e0c15ad877bcb21b5ba78795b3774d3`、`client_kind=copilot-cli`、`input_tokens=71468`、`output_tokens=724`、`total_tokens=72192`、`duration_ms=8241`、`error_count=0`、`tool_call_count=2` が出力されることを確認した。
- 2026-06-11: sanitized diagnosis input を作成し、`validate-diagnoses -> generate-improvement-proposals -> evaluate-improvement-proposals -> generate-decision-template -> record-human-decisions` まで接続できることを確認した。
- 2026-06-11: content capture を含む未加工 Copilot OTel JSONL と Copilot CLI session output は redaction 後に `tmp\m4-redacted-real-trace-e2e\` から削除した。

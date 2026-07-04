# M6: collaborative real-trace E2E

## 目的

Sprint3 candidate pipeline が synthetic fixture だけでなく、redacted real Copilot trace 由来の入力でも最小 E2E として成立することを確認する。

M6 は実 trace 互換性の確認であり、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を repository に保存しない。

## 作業分担

- Agent は GitHub Copilot CLI で実施できる OTel 設定、raw ingest、normalize、candidate pipeline 実行、redacted evidence 整理を担当する。
- User は GitHub Copilot Chat for VS Code への prompt 送信、VS Code 側 trace 発生確認、必要な画面上の確認など、ユーザー操作の方が低コストな作業を担当する。
- Agent と User は、実行前に capture content、sensitive output 保存先、削除対象 path、redaction 方針を確認する。

## 完了条件

- [x] GitHub Copilot CLI 由来の redacted real-trace 入力で、`generate-diagnosis-candidates` まで実行できることを確認している。
- [x] GitHub Copilot Chat 由来の redacted real-trace 入力で、`generate-diagnosis-candidates` まで実行できることを確認している。
- [x] 可能な場合は `generate-improvement-candidates` と `generate-auto-decisions` まで通し、通せない場合は未確認理由を記録している。
- [x] sensitive bundle を生成した場合は `manifest.json` の `delete_target_paths` を確認し、削除状況または削除保留理由を記録している。
- [x] repository に保存する evidence は redacted summary、実行 command、trace id の匿名化識別子、確認項目、未確認項目に限定している。
- [x] 実 content、credential、secret、Base64 header、実 user identity を sprint-local docs、fixtures、review record に保存していない。

## 検証

- 自動テストは M3 / M4 の synthetic fixture を source of truth とする。
- M6 の live check は network endpoint とユーザー操作に依存するため、確認日時、環境、作業分担、入力 redaction 方針、実行 command、確認結果、削除状況を sprint-local notes に記録する。

## 実施記録

- 2026-06-18: GitHub Copilot CLI 1.0.63 の OTel file exporter で read-only prompt の実 trace を取得し、trace id / span id、prompt / response、tool schema、tool arguments / results、identity、repository identifiers を redaction した OTLP envelope を ignored `tmp\sprint3-m6-real-trace-e2e\20260618-cli\redacted-raw.json` に生成した。
- 2026-06-18: CLI 由来 redacted input で `ingest-raw -> normalize-raw -> generate-diagnosis-candidates -> generate-improvement-candidates -> generate-auto-decisions` が成功した。`normalize-raw` は 11 measurement rows、candidate pipeline は 11 diagnosis candidates、10 improvement candidates、10 auto-decision records を生成した。
- 2026-06-18: `--include-sensitive-content` により sensitive bundle を生成し、`manifest.json` の `delete_target_paths` を確認した。確認後、未加工 OTel JSONL、Copilot session output、sensitive bundle directory を削除した。
- 2026-06-18: VS Code Copilot Chat の一時 workspace を起動し、ユーザー送信後に file exporter JSONL を取得した。log record 形式の出力を redacted OTLP envelope に変換し、ignored `tmp\sprint3-m6-real-trace-e2e\20260618-vscode\redacted-raw.json` に生成した。
- 2026-06-18: VS Code Copilot Chat 由来 redacted input で `ingest-raw -> normalize-raw -> generate-diagnosis-candidates -> generate-improvement-candidates -> generate-auto-decisions` が成功した。`normalize-raw` は 27 measurement rows、candidate pipeline は 27 diagnosis candidates、26 improvement candidates、26 auto-decision records を生成した。
- 2026-06-18: VS Code Chat 側の sensitive bundle manifest を確認後、未加工 OTel JSONL と sensitive bundle directory を削除した。これにより M6 の GitHub Copilot CLI / GitHub Copilot Chat 両方の redacted real-trace E2E 完了条件を満たした。

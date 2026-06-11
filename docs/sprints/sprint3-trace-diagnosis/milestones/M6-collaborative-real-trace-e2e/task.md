# M6: collaborative real-trace E2E

## 目的

Sprint3 candidate pipeline が synthetic fixture だけでなく、redacted real Copilot trace 由来の入力でも最小 E2E として成立することを確認する。

M6 は実 trace 互換性の確認であり、実 prompt / response content、tool arguments / results、credential、secret、Base64 header、実 user identity を repository に保存しない。

## 作業分担

- Agent は GitHub Copilot CLI で実施できる OTel 設定、raw ingest、normalize、candidate pipeline 実行、redacted evidence 整理を担当する。
- User は GitHub Copilot Chat for VS Code への prompt 送信、VS Code 側 trace 発生確認、必要な画面上の確認など、ユーザー操作の方が低コストな作業を担当する。
- Agent と User は、実行前に capture content、sensitive output 保存先、削除対象 path、redaction 方針を確認する。

## 完了条件

- [ ] GitHub Copilot CLI 由来の redacted real-trace 入力で、`generate-diagnosis-candidates` まで実行できることを確認している。
- [ ] GitHub Copilot Chat 由来の redacted real-trace 入力で、`generate-diagnosis-candidates` まで実行できることを確認している。
- [ ] 可能な場合は `generate-improvement-candidates` と `generate-auto-decisions` まで通し、通せない場合は未確認理由を記録している。
- [ ] sensitive bundle を生成した場合は `manifest.json` の `delete_target_paths` を確認し、ユーザーによる削除実施または削除保留理由を記録している。
- [ ] repository に保存する evidence は redacted summary、実行 command、trace id の匿名化識別子、確認項目、未確認項目に限定している。
- [ ] 実 content、credential、secret、Base64 header、実 user identity を sprint-local docs、fixtures、review record に保存していない。

## 検証

- 自動テストは M3 / M4 の synthetic fixture を source of truth とする。
- M6 の live check は network endpoint とユーザー操作に依存するため、確認日時、環境、作業分担、入力 redaction 方針、実行 command、確認結果、削除状況を sprint-local notes に記録する。

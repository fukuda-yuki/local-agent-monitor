# M4: improvement and auto-decision implementation

## 目的

`generate-improvement-candidates` と `generate-auto-decisions` を実装し、diagnosis candidate を deterministic な auto-decision record に変換する。

M4 は M2 で `auto-approved` の Sprint3 内の出口と M24-M27 adapter / mapping contract が確定するまで開始しない。

## 完了条件

- [x] `generate-improvement-candidates` が diagnosis candidate CSV / JSON から proposal candidate を生成できる。
- [x] `generate-auto-decisions` が `auto-approved`、`needs-human-review`、`blocked` を出力する。
- [x] `auto-approved` は repository 修正、patch / diff、commit / PR を実行しない。
- [x] `auto-approved` は Sprint4 planning handoff 用の record として残り、Sprint3 内の実装 command に自動接続されない。
- [x] sensitive data risk と scope overreach を decision rule で検出する。
- [x] synthetic fixture で auto-approved、needs-human-review、blocked を検証している。

## 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`

## 結果

- `generate-improvement-candidates` を追加し、diagnosis candidate CSV / JSON から `candidate_status=blocked` を除外して improvement candidate record を生成するようにした。
- `generate-auto-decisions` を追加し、M2 の decision rule order に従って scope overreach は `blocked`、sensitive bundle reference は `needs-human-review`、safe metadata は `auto-approved`、その他は default human review を出力するようにした。
- `auto-approved` の `next_action` は `record-for-sprint4-planning` に限定し、Sprint3 内で repository 修正、patch / diff 生成、commit / PR 作成を行う consumer command は追加していない。
- 2026-06-17 に `dotnet build CopilotAgentObservability.slnx`、M4 focused tests、`dotnet test CopilotAgentObservability.slnx` で検証した。

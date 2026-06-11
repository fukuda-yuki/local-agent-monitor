# M4: improvement and auto-decision implementation

## 目的

`generate-improvement-candidates` と `generate-auto-decisions` を実装し、diagnosis candidate を deterministic な auto-decision record に変換する。

M4 は M2 で `auto-approved` の Sprint3 内の出口と M24-M27 adapter / mapping contract が確定するまで開始しない。

## 完了条件

- [ ] `generate-improvement-candidates` が diagnosis candidate CSV / JSON から proposal candidate を生成できる。
- [ ] `generate-auto-decisions` が `auto-approved`、`needs-human-review`、`blocked` を出力する。
- [ ] `auto-approved` は repository 修正、patch / diff、commit / PR を実行しない。
- [ ] `auto-approved` は Sprint4 planning handoff 用の record として残り、Sprint3 内の実装 command に自動接続されない。
- [ ] blocked 条件として sensitive data risk と scope overreach を検出する。
- [ ] synthetic fixture で auto-approved、needs-human-review、blocked を検証している。

## 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`

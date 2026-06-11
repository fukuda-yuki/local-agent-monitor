# M4: improvement and auto-decision implementation

## 目的

`generate-improvement-candidates` と `generate-auto-decisions` を実装し、diagnosis candidate を deterministic な auto-decision record に変換する。

## 完了条件

- [ ] `generate-improvement-candidates` が diagnosis candidate CSV / JSON から proposal candidate を生成できる。
- [ ] `generate-auto-decisions` が `auto-approved`、`needs-human-review`、`blocked` を出力する。
- [ ] `auto-approved` は repository 修正、patch / diff、commit / PR を実行しない。
- [ ] blocked 条件として sensitive data risk と scope overreach を検出する。
- [ ] synthetic fixture で auto-approved、needs-human-review、blocked を検証している。

## 検証

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`

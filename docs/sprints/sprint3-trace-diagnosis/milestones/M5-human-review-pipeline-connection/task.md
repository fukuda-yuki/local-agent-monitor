# M5: human-review pipeline connection

## 目的

 Sprint3 candidate pipeline を既存 M24-M27 human-review pipeline に接続する adapter command を、M2 で確定した adapter / mapping contract に従って追加し、未接続の parallel pipeline を残さない。

## 完了条件

- [ ] M2 で決めた既存 M24-M27 互換性維持方針を維持している。
- [ ] diagnosis candidate CSV / JSON と normalized measurement CSV / JSON を入力に取り、M24 diagnosis record CSV / JSON を出力する adapter command を追加している。
- [ ] diagnosis candidate から M24 diagnosis record への列 mapping、落とす列、保持する `evidence_ref`、human review status の扱いを反映している。
- [ ] 既存 command の compatibility note と、Sprint3 candidate pipeline が前段であることを記録している。
- [ ] Sprint3 の完了条件に、candidate output が人間レビュー workflow で消費できることを含めている。
- [ ] `docs/spec.md` と `docs/task.md` を確定結果に同期している。

## 検証

- Adapter command の synthetic fixture test を追加する。
- `dotnet build CopilotAgentObservability.slnx` と `dotnet test CopilotAgentObservability.slnx` を実行する。

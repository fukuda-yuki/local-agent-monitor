# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-05-24 Review

### 範囲

- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint1-langfuse-poc/knowledge/research-measurement.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M14-langfuse-export/task.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M14-langfuse-export/notes.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M14-langfuse-export/plan.md`
- `docs/sprints/sprint1-langfuse-poc/milestones/M14-langfuse-export/review.md`

### Spec compliance / functional correctness

- M14 は Langfuse export / API 調査と M15 入力形式の決定に閉じており、集計 CLI 実装や live trace 収集を行っていない。
- Phase 1 の既定であるローカル self-host Langfuse を前提に、Cloud-only とされる Observations API v2 を baseline 必須にしていない。
- M15 の入力形式は Public API legacy trace / observation read response の保存 JSON として明記され、UI export、Blob Storage export、ClickHouse 直接参照の位置づけも分離されている。

### Tests / validation

- Documentation-only のため `dotnet build` / `dotnet test` は実行対象外。
- `rg` により M14 関連記述と credential 方針を確認した。
- live Langfuse / 実 Copilot trace は M14 の完了条件に含めていない。

### Maintainability

- M15、M16、M17 以降の責務境界を維持した。
- 取得方式の優先順位を表にし、後続 milestone が判断を再利用しやすい形にした。

### 残リスク

- Langfuse API の推奨方式は変化し得るため、M15 実装時に対象 self-host version の API response shape を合成 fixture で固定する必要がある。
- Public API legacy read は公式 docs 上で scale 面の既定非推奨とされているため、大量 export が必要になった場合は Blob Storage export または v2 API availability を再確認する。
- M14 は実データ取得を行っていないため、Copilot OTel 由来の Resource Attributes が Langfuse response のどの階層に現れるかは M15 の fixture 設計時に確認する。

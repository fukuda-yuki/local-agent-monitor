# Research Measurement Knowledge

この文書は M11 以降の研究計測化と trace-driven improvement loop に関する再利用可能な調査結果と判断理由を記録する。
`docs/requirements.md` を上位要件、`docs/spec.md` を詳細仕様の正とし、この文書は source of truth ではない。

## M11 以降の前提

- M11 以降では、既存の M0-M10 を観測基盤として維持し、研究実施計画書に基づく計測・集計・評価支援へ進める。
- M11-M22 の研究計測化では改善案生成、自動実装、自動採用、勝敗の自動決定は扱わない。
- 改善案生成・評価の小規模デモを検討する場合は、M23 以降の後続候補に分離する。
- ユーザー確認により、ゼロベース化せず、既存の M0-M10 を Copilot OTel / Langfuse 観測基盤として継承する判断を採用した。
- GitHub Issue は既定の作業単位にしない。ユーザーが明示した場合だけ作成・参照する。

## Milestone 分割

- M11-M13 は研究計画書とのスコープ整合、measurement schema、模擬保守タスクセット定義を扱う。
- M14-M16 は Langfuse export / API 調査、集計 CLI / script MVP、turn count / tool call count 算出ルールを扱う。
- M17-M19 は baseline 実行手順、小規模 dry run、本計測を扱う。
- M20-M22 は品質非劣化 rubric、variant / A-B 計測プロトコル、結果レポート雛形を扱う。
- M10 follow-up の gitleaks fixture 削除と、共有環境・実データ検証の事前仕様化は独立 follow-up として扱う。

## M14 Langfuse export / API

- M15 の既定入力は、ローカル self-host Langfuse で再現可能な Public API legacy trace / observation read response を保存した JSON とする。
- Observations API v2 は新規 data extraction 向けの候補だが、M14 時点の公式 docs では Cloud-only のため self-host baseline の既定にはしない。
- UI export は手動診断用の one-off export、Blob Storage export は scheduled / 大量 export 候補、ClickHouse 直接参照は最後の調査・復旧候補として扱う。
- M15 fixture や snapshot には API credential、Base64 化済み header、管理者パスワード、実 trace content、実ユーザーデータ、顧客データ、実運用ログを含めない。

## Trace-driven improvement loop

- 実現可能性の判断は、エージェントが自律的に自分を直接修正する基盤ではなく、trace-driven agent improvement loop として作るなら現実的、というもの。
- 推奨 loop は、trace / metrics / rubric の収集、failure taxonomy / anti-pattern 分類、改善候補生成、baseline / variant 評価、人間承認の順に進める。
- M11-M22 はこの loop の前提であり、measurement schema、baseline 計測、rubric、variant 比較プロトコルを先に固める必要がある。
- M23 以降の候補は、failure taxonomy / anti-pattern 定義、trace-to-diagnosis MVP、improvement proposal generator、proposal evaluator、human approval workflow。
- 改善候補は `prompt`、`instruction`、`skill`、`tool schema`、`workflow`、`eval` のいずれかに分類する。
- M23 以降でも改善候補は人間が採否する提案として扱い、自動採用、自動 repository 修正、自動 commit、自動 push、自動 pull request、自動勝敗決定は引き続き既定スコープ外とする。

## References

- OpenTelemetry GenAI semantic conventions: https://opentelemetry.io/docs/specs/semconv/gen-ai/
- Langfuse Scores / evaluation docs: https://langfuse.com/docs/evaluation/scores/overview
- Langfuse Scores via API / SDK: https://langfuse.com/docs/evaluation/evaluation-methods/custom-scores
- OpenInference: https://arize-ai.github.io/openinference/
- Reflexion: Language Agents with Verbal Reinforcement Learning: https://arxiv.org/abs/2303.11366
- Self-Refine: Iterative Refinement with Self-Feedback: https://arxiv.org/abs/2303.17651
- DSPy: Compiling Declarative Language Model Calls into Self-Improving Pipelines: https://arxiv.org/abs/2310.03714
- Automatic Prompt Optimization with Gradient Descent and Beam Search: https://arxiv.org/abs/2305.03495
- Promptbreeder: Self-Referential Self-Improvement Via Prompt Evolution: https://arxiv.org/abs/2309.16797
- TextGrad: Automatic Differentiation via Text: https://arxiv.org/abs/2406.07496
- GEPA: Reflective Prompt Evolution Can Outperform Reinforcement Learning: https://arxiv.org/abs/2507.19457
- TRAIL: Trace Reasoning and Agentic Issue Localization: https://arxiv.org/abs/2505.08638

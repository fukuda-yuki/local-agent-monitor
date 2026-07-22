# Implementation Specifications

このディレクトリは Copilot Agent Observability の実装仕様正本です。
上位要件は [../requirements.md](../requirements.md)、仕様索引は [../spec.md](../spec.md) を参照してください。

## Layers

| Layer | Spec |
| --- | --- |
| Telemetry ingestion | [layers/telemetry-ingestion.md](layers/telemetry-ingestion.md) |
| Raw store and normalization | [layers/raw-store-normalization.md](layers/raw-store-normalization.md) |
| Candidate pipeline | [layers/candidate-pipeline.md](layers/candidate-pipeline.md) |
| Dashboard publishing | [layers/dashboard-publishing.md](layers/dashboard-publishing.md) |

## Interfaces

- [Source schema drift and Claude Code](interfaces/source-schema-drift-claude-code.md)

| Interface | Spec |
| --- | --- |
| Collection profiles | [interfaces/collection-profiles.md](interfaces/collection-profiles.md) |
| Config CLI | [interfaces/config-cli.md](interfaces/config-cli.md) |
| Configuration setup | [interfaces/configuration-setup.md](interfaces/configuration-setup.md) |
| First-trace Doctor | [interfaces/first-trace-doctor.md](interfaces/first-trace-doctor.md) |
| Normalized measurement dataset | [interfaces/measurement-dataset.md](interfaces/measurement-dataset.md) |
| Candidate records | [interfaces/candidate-records.md](interfaces/candidate-records.md) |
| Human-review records | [interfaces/human-review-records.md](interfaces/human-review-records.md) |
| Dashboard dataset | [interfaces/dashboard-dataset.md](interfaces/dashboard-dataset.md) |
| Instruction diagnosis analysis | [interfaces/instruction-diagnosis-analysis.md](interfaces/instruction-diagnosis-analysis.md) |
| Canvas Session workspace | [interfaces/canvas-session-workspace.md](interfaces/canvas-session-workspace.md) |
| Canvas Session workspace UI | [interfaces/canvas-session-workspace-ui.md](interfaces/canvas-session-workspace-ui.md) |
| Canvas Session Evidence | [interfaces/canvas-session-evidence.md](interfaces/canvas-session-evidence.md) |
| Canvas Improvement Proposals | [interfaces/canvas-improvement-proposals.md](interfaces/canvas-improvement-proposals.md) |
| Canvas Proposal Apply | [interfaces/canvas-proposal-apply.md](interfaces/canvas-proposal-apply.md) |
| Canvas Effect Comparison | [interfaces/canvas-effect-comparison.md](interfaces/canvas-effect-comparison.md) |
| Trace agent execution graph | [interfaces/trace-agent-execution-graph.md](interfaces/trace-agent-execution-graph.md) |
| Historical source import | [interfaces/historical-source-import.md](interfaces/historical-source-import.md) |
| Historical evidence extraction | [interfaces/historical-evidence-extraction.md](interfaces/historical-evidence-extraction.md) |
| Sanitized evidence export | [interfaces/sanitized-evidence-export.md](interfaces/sanitized-evidence-export.md) |
| Security and data boundaries | [security-data-boundaries.md](security-data-boundaries.md) |
| Validation and release matrix | [validation-release-matrix.md](validation-release-matrix.md) |

## Change Rule

Public behavior or schema changes must update:

1. Relevant spec file in this directory.
2. User-facing guide when user workflow changes.
3. Tests covering the changed contract.
4. `docs/requirements.md` or `docs/spec.md` when the product scope changes.

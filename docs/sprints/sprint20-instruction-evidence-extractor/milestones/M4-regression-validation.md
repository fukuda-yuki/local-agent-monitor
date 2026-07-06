# Sprint20 M4 - Regression Validation Record

Date: 2026-07-07. Branch: `feat/sprint20` (M1-M3 commits, working tree clean
before this record). Recorded per `docs/agent-guides/review-workflow.md`
(self-review format).

## Scope reviewed

The full Sprint20 diff `main...HEAD` (7 implementation commits, 1116
insertions / 47 deletions across 16 files):

- Docs: `docs/decisions.md` (D047), `docs/spec.md` (six -> seven tool names),
  `docs/specifications/interfaces/instruction-diagnosis-analysis.md`
  (Evidence Extractor Output Contract + Per-Category Required Evidence),
  `docs/specifications/layers/telemetry-ingestion.md` (seventh
  process-internal tool), sprint `Plan.md` (checkbox tracking).
- Product code: `Analysis/InstructionEvidenceExtractor.cs` (new pure
  deterministic extractor + output records), `Analysis/DotNetCopilotRawAnalysisRunner.cs`
  (extractor wiring into `MonitorAnalysisToolData.Create`, seventh tool
  `get_instruction_evidence`, prompt template v3 block),
  `Projection/IMonitorProjectionStore.cs` (+ `ListConversationTraces`),
  `MonitorProjectionRows.cs` (`MonitorConversationTraceRow` DTO),
  `RawTelemetryStore.cs` (read-only `ListConversationTraces` query).
- Tests: `InstructionEvidenceExtractorTests.cs` (new),
  `MonitorProjectionStoreTests.cs` (+ store query cases),
  `MonitorAnalysisRouteTests.cs` (+ tool-data shape and prompt v3 cases),
  and three fake projection stores updated for the new interface member
  (`MonitorSummaryEndpointTests.cs`, `MonitorTraceDetailTests.cs`,
  `ProjectionWorkerTests.cs`).

## Validation commands and results

Pinned suite run from the repository root on 2026-07-07:

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | Succeeded, 0 warnings / 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | Exit 0; Chromium already bootstrapped in `artifacts\playwright-browsers` |
| `dotnet test CopilotAgentObservability.slnx` | **718 passing** (ConfigCli 301 + LocalMonitor 417, Playwright smoke included), 0 failed / 0 skipped |

The 718 total is the ~700 Sprint19 baseline plus the 18 Sprint20 additions
(LocalMonitor 394 at Sprint19 M4 -> 417 here). No test was skipped as a
substitute for a required command; no command was swapped when another was
available (AGENTS.md).

The collector config check was not run: no files under `infra\otel-collector\`
changed in this sprint (per the pinned suite's conditional rule).

## Spec-consistency review (behavior checked)

Recorded self-review in the parent session over `main...HEAD` (subagent
dispatch not used - AGENTS.md permits it only on explicit user request). All
checks pass:

- Additive-only: one new class (`InstructionEvidenceExtractor`), one new
  process-internal tool (`get_instruction_evidence`), one prompt template
  revision (v2 -> v3). No new routes, no schema change, no projection
  migration, no new API field on any endpoint DTO in the diff.
- The one new store query `ListConversationTraces(conversationId)` is a
  read-only, parameterized `SELECT trace_id, MIN(start_time) FROM monitor_spans
  ... GROUP BY trace_id ORDER BY MIN(start_time), trace_id` over existing
  columns - no DDL, no write path; recorded in D047 so it is not a silent
  boundary stretch. Returns metadata only (trace id + earliest span start
  time).
- Six existing tools untouched: the runner diff appends the seventh tool after
  the existing six (`get_raw_trace`, `get_raw_span`, `get_trace_summary`,
  `get_trace_span_tree`, `get_cache_summary`, and the raw-span-context tool
  are unchanged context lines); `get_instruction_evidence` serializes
  `data.InstructionEvidence`.
- Canvas boundary: `CanvasExtensionContractTests.cs` is unmodified
  (`git diff main...HEAD -- <file>` is empty) and green; the Canvas focus set
  (D036) is not extended.
- Prompt v3 parity: `InstructionDiagnosisPromptBlock` keeps taxonomy v1 (5
  category ids), the fixed 4-part finding format, the no-evidence-no-finding
  rule, the Japanese-output pin, and the D045 history / follow-up blocks
  unchanged; it inserts the per-category required-evidence rules
  (`missing-context-constraints` -> `error_spans`/`retry_chains`;
  `task-size-split` -> multi-goal `user_instruction` + `turn_tokens`
  concentration; `ambiguity` -> `conversation` sibling metadata + corrective
  wording; `goal-clarity` / `missing-acceptance-criteria` -> turn-level
  evidence) and the raw-verified-citation escape hatch, transcribed from
  `instruction-diagnosis-analysis.md`. Extractor field names
  (`error_spans`, `retry_chains`, `turn_tokens`, `user_instruction`,
  `conversation`) are identical across D047, the interface spec, the code
  records, and the prompt block.
- Determinism: extractor orders every collection explicitly (spans by
  `(RawRecordId, SpanOrdinal)`; siblings by `MIN(start_time)` then trace id);
  `Extract_IsDeterministic_SameInputTwiceGivesEqualSerializedOutput` asserts
  byte-identical serialized output.
- Data safety: no raw prompt/response bodies, tool arguments, PII,
  credentials, or sensitive local paths in the committed diff. Test fixtures
  are synthetic (`'a' * 200`, `"first line"`, `"hello world"`, placeholder
  OTLP templates). The only raw-derived extractor field,
  `user_instruction.Descriptor`, is capped at 160 chars, first line only, with
  a literal `"..."` truncation marker; `error_spans` descriptors are built
  from allowlist columns (operation / tool / error kind) only, asserted by
  `Assert.DoesNotContain("payload", ...)`.
- `--sanitized-only` unchanged: the new tool and extractor live inside the raw
  analysis surface, which `--sanitized-only` continues to remove wholesale (no
  change to that switch in the diff).

## Findings

No blocking issues found. Non-blocking observations:

1. `ListConversationTraces` SQL landed in `RawTelemetryStore.cs` rather than
   the `RawTelemetryStore.Overview.cs` partial named in the plan File Map; it
   sits beside the other span reads as intended. Cosmetic file-placement
   deviation, already noted in the M2 Task 2.1 record.
2. The 160-char `user_instruction` descriptor is the only field that surfaces
   real prompt text at runtime (by design, D047); it appears only in local
   runtime tool output, never in a committed file, so the "no raw/PII in
   committed files" boundary holds.

## Residual risks / unverified scope

- Category=evidence coupling quality (does prompt v3 actually tighten coupling
  without losing recall) is not verifiable by the regression suite; it is the
  human-gated M5 A/B scope over the six preserved Sprint19 traces via the
  validated BYOK path.
- Extractor over-fit to the six Sprint19 trace shapes is mitigated by edge
  fixtures (zero errors, missing conversation id, single-span trace, empty
  trace) but is confirmed only by the M5 live comparison.
- LLM citation / grounding behavior against real traces remains open until M5.

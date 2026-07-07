# Sprint20 - Instruction Evidence Extractor (Issue #46 Phase 2, step 1)

Status: M1-M4 complete; M5 conditional pass / Phase 2 GO with provider caveat.
All six traces now have results (A1/A2/B1/B2 under BYOK; C1/C2 completed under
the SDK default no-BYOK path). The 900-second C1/C2 timeout is localized to the
external GitHub Copilot/BYOK provider path and is not a Sprint20 implementation
blocker.

Milestone/task breakdown: [Plan.md](Plan.md).
M4 regression evidence: [milestones/M4-regression-validation.md](milestones/M4-regression-validation.md).
M5 A/B evidence: [milestones/M5-ab-validation/ab-validation.md](milestones/M5-ab-validation/ab-validation.md).

Sprint20 implements the first Phase 2 step of
[Issue #46](https://github.com/fukuda-yuki/copilot-agent-observability/issues/46),
following the Phase 1 scope item 5 roadmap ("prompt-only first, migrate proven
evidence patterns to deterministic pre-extraction") and the Sprint19 M5
verdict (GO, with two design inputs recorded in the
[M5 evidence](../sprint19-instruction-diagnosis-analysis/milestones/M5-live-validation/live-validation.md)):

1. Category=evidence coupling was the weakest contract element (2 of 9
   findings stretched category definitions while citing real evidence).
2. Analysis is trace-scoped while Copilot CLI emits one trace per invocation
   (conversation id links sibling traces; prior-turn content is not replayed).

Sprint20 adds a **deterministic instruction-evidence extractor** that runs in
code at analysis start and feeds the LLM structured, verifiable evidence
through a new process-internal tool. Expected side effects: tighter
category=evidence coupling, fewer raw-exploration round trips (lower timeout
and hallucination exposure), lower token use.

## Scope

- New pure, deterministic extractor class
  `Analysis/InstructionEvidenceExtractor.cs` in the Local Monitor project.
  Input: the existing projection rows and raw records already loaded by
  `MonitorAnalysisToolData.Create` plus sibling-trace metadata resolved via
  `conversation_id`. Output (structured, no long raw bodies):
  - `error_spans[]` - span id, tool name, error kind, short factual error
    descriptor.
  - `retry_chains[]` - same-tool failed-then-retried span id chains with
    final outcome.
  - `turn_tokens[]` - per chat-turn input/output token distribution
    (waste / concentration signals).
  - `user_instruction` - span reference holding the user instruction plus a
    short factual descriptor.
  - `conversation` - sibling trace ids, ordering, and count for the same
    `conversation_id` (metadata only, no bodies; minimal answer to M5
    observation 2).
- New process-internal SDK tool `get_instruction_evidence` returning the
  serialized extractor output, defined alongside the existing six tools.
- Prompt template v3 (`InstructionDiagnosisPromptBlock`): per-category
  required-evidence rules keyed to extractor fields, e.g.
  `missing-context-constraints` requires citing `error_spans` /
  `retry_chains` entries; `task-size-split` requires both a multi-goal
  instruction and `turn_tokens` concentration; `ambiguity` requires user
  rephrase evidence (conversation metadata plus the corrective wording of the
  analyzed trace). Findings grounded outside the extractor remain allowed
  only with an explicitly raw-verified span id citation (discovery stays
  possible).

## Boundary

- Additive only: one new class, one new process-internal tool, one prompt
  template revision. No new routes, no schema change, no new API fields, no
  projection migration.
- The existing six analysis tools stay available unchanged (verification
  path).
- Diagnosis results remain local runtime data; `--sanitized-only` continues
  to remove the whole raw analysis surface.
- Canvas focus set (D036) unchanged; Canvas `/analyze` still does not start
  the raw analysis runner (Sprint17 boundary).
- Memory candidate generation, adoption workflow, repository-safe export,
  and GitHub issue/commit correlation stay out of scope (Issue #46 idea
  backlog).
- Sprint evidence stays sanitized: trace/span references, verdicts, and
  iteration log only.

## Milestones

| Milestone | Summary | Status |
| --- | --- | --- |
| M1 | Specs and decision record | Done |
| M2 | Extractor and unit tests | Done |
| M3 | Tool exposure and prompt template v3 | Done |
| M4 | Regression validation | Done |
| M5 | A/B live validation vs Sprint19 M5 and Issue #46 update (human-gated) | Conditional pass (provider caveat) |

### M1 - Specs and decision record

- Add a decision to `docs/decisions.md` (deterministic pre-extraction step:
  extractor field set, additive tool, per-category coupling rules, A/B gate).
- Update `docs/specifications/interfaces/instruction-diagnosis-analysis.md`
  (extractor output contract, per-category required-evidence rules) and
  `docs/specifications/layers/telemetry-ingestion.md` (add
  `get_instruction_evidence` to the process-internal tool list).
- Update the raw analysis bullets in `docs/requirements.md` / `docs/spec.md`
  if the M1 review finds shipped-behavior wording that must change.

### M2 - Extractor and unit tests

- Implement `InstructionEvidenceExtractor` as a pure function over already
  loaded rows/records; deterministic output ordering.
- Unit tests with synthetic span/raw fixtures: error-span extraction, retry
  chain detection (failure then same-tool retry), token distribution,
  user-instruction span resolution, conversation sibling metadata, empty
  trace behavior.

### M3 - Tool exposure and prompt template v3

- Wire the extractor into `MonitorAnalysisToolData.Create` and define
  `get_instruction_evidence`.
- Prompt template v3 with per-category required-evidence rules (see Scope);
  keep the 4-part finding format, no-evidence-no-finding rule, Japanese
  output rule, and D045 history blocks unchanged.
- Unit tests: prompt v3 content, tool data shape, existing focus prompts
  unchanged.

### M4 - Regression validation

- Pinned suite from the repository root: `dotnet build`, Playwright install
  script, `dotnet test` (baseline: 700 passing after Sprint19).
- Spec-consistency self-review per `docs/agent-guides/review-workflow.md`.

### M5 - A/B live validation and Issue #46 update (human-gated)

- Re-run the instruction-diagnosis focus over the **same six real traces**
  preserved from Sprint19 M5
  (`artifacts\sprint19-live-validation\monitor-live.db`) via the validated
  BYOK path, and compare against the Sprint19 M5 results.
- Gate criteria: the Sprint19 three (citation existence, trace-specificity,
  no-evidence-no-finding) plus one new criterion - **every finding is
  grounded in extractor fields or an explicitly raw-verified span citation**.
  The two Sprint19 coupling deviations (B1 findings 3 and 4) must not recur
  in equivalent form.
- On failure, iterate prompt v3 (or extractor field definitions) and record
  each iteration and verdict.
- Save sanitized A/B evidence under `milestones/M5-ab-validation/`; append
  the outcome to Issue #46.
- 2026-07-07 result (BYOK): A1/A2/B1/B2 completed under the Sprint20
  prompt/extractor path; C1/C2 timed out after the revised 900-second BYOK
  timeout.
- 2026-07-08 result (no-BYOK): C1/C2 were completed under the SDK default
  (no-BYOK) Copilot provider, run as the Local Monitor .NET process from
  `bin\Debug\net10.0` (where `runtimes\win-x64\native\copilot.exe` is
  present). C1 = one `missing-context-constraints` finding (4/4 cited spans
  resolved, extractor-grounded); C2 = no finding (no-evidence-no-finding
  upheld, 6/6 evidence refs resolved). The Sprint19 B1 coupling deviations did
  not recur. Because C1/C2 ran under a different provider/model than the BYOK
  path used for A1/A2/B1/B2, provider parity is recorded as a caveat. M5 is a
  **conditional pass / Phase 2 GO with provider caveat**. Evidence localizes
  the 900-second timeout to the external GitHub Copilot/BYOK provider path, not
  the SDK/prompt/extractor. Issue #46 has a repository-safe comment draft only
  (not posted).

## User-provided live validation inputs (M5)

- The preserved Sprint19 M5 monitor DB and BYOK provider configuration
  (glm-5.2 on the OpenCode Go endpoint, `CopilotAnalysis:TimeoutSeconds`
  600, validated 2026-07-06).
- Sprint20 validation raised `CopilotAnalysis:TimeoutSeconds` to 900 seconds
  after B1 timed out at 600 seconds. B1 then completed, but C1/C2 still timed
  out at 900 seconds under BYOK.
- For C1/C2, the user re-ran the same instruction-diagnosis analysis under the
  SDK default (no-BYOK) Copilot provider, executed as the Local Monitor .NET
  process from `bin\Debug\net10.0`; both completed. In-PowerShell reflection
  attempts (runs 21-22) failed only because the no-BYOK provider could not
  locate `copilot.exe` under the PowerShell host. This provider switch for
  C1/C2 is a recorded provider-parity caveat, not a silent switch; the BYOK
  timeout is treated as an external GitHub Copilot/provider-path issue.
- Human judgment on the trace-specificity and coupling criteria.

## Risks

- Extractor field design may over-fit the six Sprint19 traces; unit fixtures
  must include shapes not present in those traces (e.g. zero errors, missing
  conversation id, single-span trace).
- Coupling rules that are too strict could suppress legitimate findings the
  extractor cannot see; the raw-verified-citation escape hatch exists for
  that, and M5 A/B comparison measures whether recall degrades (fewer valid
  findings than Sprint19 on the same traces is a signal to loosen).
- The A/B comparison depends on the preserved runtime DB; it is
  repository-ignored local data, so if it is lost before M5, fall back to
  generating fresh traces (recorded as a deviation, not a silent switch).

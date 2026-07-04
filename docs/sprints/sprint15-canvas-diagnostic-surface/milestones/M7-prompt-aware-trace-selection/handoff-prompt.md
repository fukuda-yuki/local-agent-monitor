# Sprint15 M7 implementation handoff prompt

This file is the self-contained prompt for a fresh Claude Code session in
this same repository. Paste everything below the `---` line as the initial
message to that session.

---

```
Repo: C:\Users\mwam0\Documents\Codex\copilot-agent-observability
Branch: main, HEAD: d913f8f (working tree clean as of this handoff)

# Context

Sprint15 "Canvas Diagnostic Surface". Read AGENTS.md first (workflow,
source-of-truth order, ask-before rules, git rules). Then read, in order:

1. docs/decisions.md — D039 (MOST IMPORTANT: this is the design decision
   this milestone implements, confirmed and committed; read it in full).
   Skim D036–D038 for background if useful, but D039 is the operative one.
2. docs/specifications/security-data-boundaries.md — search for "D039" and
   read the "Sprint15 continuation — prompt-aware trace selection" section
   in full, plus the D032 paragraph it cross-references (search "D032").
3. docs/sprints/sprint15-canvas-diagnostic-surface/README.md — the M7 row
   in the milestone table and the "## M7: Prompt-aware trace selection
   (D039)" section.
4. docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M7-prompt-aware-trace-selection/plan.md
   — the concrete, actionable implementation plan for this milestone. It is
   detailed and specific; follow it, don't re-derive the design from
   scratch.
5. For pattern reference (this milestone follows the SAME conventions),
   skim milestones/M4-dashboard-card/{plan.md,review.md} and
   milestones/M5-raw-preview/{plan.md,review.md} — especially M5's review.md,
   which documents a real gotcha (a pre-existing whole-file "no /raw"
   contract-test assertion breaking when new, deliberately-authorized raw
   content is introduced) that you should watch for an equivalent of here,
   even though M7 does not introduce a `/raw` substring itself.

# Already done (all committed on `main`, all tests green as of HEAD)

- M1–M5: Canvas helper UX, `/api/monitor/summary` backend endpoint +
  Canvas-side dashboard card, trace-detail card, raw-preview page-navigation
  route. All implemented, tested, self-reviewed.
- M6: the live Canvas runtime verification (extensions_manage / open_canvas /
  invoke_canvas_action) was actually run in a real GitHub Copilot app
  session and its evidence is committed at
  docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M6-live-validation-handoff/live-validation.md
  (commit acc0834). All 5 actions validated live; no blocking issues found.
- A prior self-review round (commit a88062a) found and fixed two real bugs
  in M4's `/api/summary` proxy (a status-labeling bug from calling
  `formatTraceLine` on a DTO shape without a `status` field, and an
  error-passthrough bug that masked Local Monitor's own `400` responses).
  Both are documented in milestones/M4-dashboard-card/review.md under
  "Findings from self-review (round 2)" — read this if you want a concrete
  example of the kind of correctness bug to watch for in your own
  self-review of M7 (the pattern: reusing a formatter/helper against a data
  shape it wasn't actually designed for).

Validation baseline at this commit (d913f8f):

    dotnet build CopilotAgentObservability.slnx → 0 warnings, 0 errors
    dotnet test CopilotAgentObservability.slnx → 606 passing, 0 failed
    node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs → 18/18 pass

# Your job this session: implement M7 per D039 and the plan, then self-review

D039 is a design decision that has already been explicitly authorized for
implementation by the user (this is not a "design only, wait for
go-ahead" situation like D037 was before D038 — the user has already said
to start implementing). Implement it fully:

1. **Local Monitor**: add `GET /traces/{traceId}/prompt-label` to
   `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`, inside the
   existing `if (!options.SanitizedOnly) { ... }` block (same block as
   `/traces/{traceId}/analysis/...` from D035 and `/traces/{rawRecordId:long}/raw`).
   Reuse `MonitorPromptExtractor.ExtractPromptLabel` and
   `IMonitorProjectionStore.ListRawRecordsByTraceId` exactly as
   `Pages/Traces.cshtml.cs`'s `PopulatePrompts` already calls them — do not
   write new extraction logic. Full response shape, error handling, and
   same-origin/no-store requirements are in the plan.md.
2. **Canvas extension**: add `fetchHelperPromptLabel` to
   `.github/extensions/otel-monitor-canvas/extension.mjs`, fetch it in
   parallel for every item in the existing `GET /api/traces` route's
   response, and merge `prompt_label` onto each item (additive only — do
   not remove the existing `line` field).
3. **Canvas helpers**: update `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`'s
   `renderHelperHtml` client-side script so the dropdown `<option>` text
   shows the prompt label (when present) alongside the existing line. The
   plan.md discusses whether this composition logic needs to be a separate
   exported+tested pure function or can stay as a one-line inline
   composition in the client `<script>` (mirroring the established
   "server pre-formats, client stays minimal" pattern from M3/M4's
   reviews) — use your judgment, but whichever you choose, make sure it is
   covered by at least one `node --test` assertion.
4. **Tests**: extend `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorRawViewTests.cs`
   (same file already covering `/traces/{rawRecordId}/raw`, same
   `MonitorTempDirectory`/`StartHostAsync(temp, sanitizedOnly: ...)` test
   host pattern), `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`,
   and `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`.
   Exact test list is in the plan.md's "Tests" section.
5. **Before you seed a raw record with a prompt attribute for your new
   Local Monitor tests**, check whether `MonitorRawViewTests.cs`'s existing
   `SeedRawRecord` helper (or any existing `MonitorPromptExtractor`-focused
   test file, if one exists — search for it) already builds a payload
   shape with a `gen_ai.prompt` span attribute, and reuse that fixture
   shape rather than inventing a new one from scratch.
6. Run every command in the plan.md's "Validation" section, in order,
   including the full `dotnet test CopilotAgentObservability.slnx` at the
   end (not just the filtered ones) to confirm nothing pre-existing broke.
7. Write `docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M7-prompt-aware-trace-selection/review.md`
   per `docs/agent-guides/review-workflow.md` — this touches a data-safety
   boundary (a new raw-bearing route), so use the deeper-review checklist,
   not a documentation-only self-review. Explicitly re-verify and state:
   (a) `prompt_label` never reaches any Canvas **action** response
   (`monitor_health`, `list_recent_traces`, `get_trace_summary`,
   `get_trace_span_tree`, `get_cache_summary`), any `session.send()` prompt
   text, any log, or any committed artifact; (b) the new Local Monitor
   route is absent (`404`) under `--sanitized-only`; (c) the new route
   enforces the same-origin check like every other raw-bearing route; (d)
   `/api/monitor/*` and the SSE stream are unchanged and still carry no
   prompt field. Before you finish, re-read your own diff once specifically
   looking for the two failure classes already found twice in this sprint:
   (i) a whole-file "no X substring" contract-test assertion that your new
   code legitimately triggers and needs precise re-scoping rather than
   deletion (M5's `/raw` lesson); (ii) reusing an existing
   formatter/helper against a data shape that doesn't actually have the
   field it reads (M4's `formatTraceLine`/`status` lesson).
8. Update the M7 row in the Sprint15 README's milestone table (status →
   implemented, mirroring how M4/M5's rows were updated) and the "## M7"
   section's closing sentence.
9. Commit with a message starting with `Sprint15:` per this repo's
   Conventional-Commits-after-prefix convention (see `git log --oneline`
   for examples). Do not push, tag, or open a PR unless explicitly asked.

# Constraints (same as every prior Sprint15 milestone)

- Canvas **action** responses stay bounded DTOs — no raw prompt content, no
  PII, no credentials, ever, in any of the 5 `invoke_canvas_action` handlers.
  `prompt_label` is a helper-page-surface-only field (same category as M5's
  raw-preview page and M4's `/api/summary` proxy), never an action-response
  field.
- The new Local Monitor route stays outside `/api/monitor/*` and the SSE
  stream — those remain exactly as sanitized as before (D032 unchanged for
  that specific family).
- Loopback-only, per-launch `x-canvas-token` on every Canvas-owned route (no
  exception for the new prompt fetch — it goes through the existing
  `/api/traces` route, which is already token-gated).
- No `console.log` in the Canvas extension. No new dependency additions.
- Do not commit runtime state, real telemetry, or real prompts/responses —
  synthetic/anonymized fixtures only in tests (the existing
  `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json`
  fixture or a similarly synthetic one for the new Local Monitor tests).
- Do not touch anything under `.github/skills/create-canvas/` or replace it
  with a `.github/prompts/*.prompt.md` file (per the Sprint15 README's
  "Controlling guidance" section).

# After M7 is done

No further Sprint15 work is currently planned (child E is dropped per D037;
M1–M6 are done). If you finish M7 cleanly, say so plainly and stop — do not
invent additional scope.
```

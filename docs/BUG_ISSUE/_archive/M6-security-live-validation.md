# M6 — Security boundary (DR6 negative matrix) + live validation

Feature: the DR6 negative-matrix test surface for the raw-default-on posture and
the new surfaces, plus the human-gated live-validation evidence record.

## Fix-unit index

| Card | Severity | Fix unit | Plan note |
| --- | --- | --- | --- |
| M6-1 | Medium | Live-validation document consistency | Fixed: Part B remains pending unless user-confirmed; candidate evidence is not closure evidence. |

Primary next plan: reconcile the four milestone records only after the user
confirms whether VS Code Copilot Chat Part B was actually run on 2026-06-28.

Source of truth: `docs/specifications/security-data-boundaries.md`; README
"Safety boundary" + the F1–F6 plan-review record; AGENTS.md "Local-First Risk
Posture"; `docs/sprints/.../milestones/M6-security-and-live-validation/{plan,review,live-validation}.md`.

Key files: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSecurityBoundaryTests.cs`,
`src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`, `.../MonitorOptions.cs`,
`docs/sprints/.../milestones/M6-security-and-live-validation/live-validation.md`.

The automated DR6 matrix is implemented faithfully — see "Evaluated but not
filed" for the seven rules verified as genuinely asserted. The one filed finding
is documentation consistency.

---

<a id="M6-1"></a>

## M6-1 — `live-validation.md` Part B status is inconsistent with the committed milestone records — Medium (confidence: High on the inconsistency; the underlying run requires user confirmation) [Claude sub-agent]

- **Location:** working-tree `docs/sprints/.../M6-security-and-live-validation/live-validation.md:131`
  (uncommitted — `git status` shows it `M`) marks **"Part B — VS Code GitHub
  Copilot Chat: COMPLETE (2026-06-28)"** with detailed evidence (VS Code 1.126.0,
  Copilot Chat 0.54.0, trace `d5f1e865c74fb247793c070f550de290`, a nested
  `Explore` sub-agent under `runSubagent`). The **committed** companion records
  still say it is open:
  - `review.md:49` — "Part B — VS Code Copilot Chat: **PENDING USER** (human-gated)";
  - `plan.md:5` — "Part B (VS Code Copilot Chat) **PENDING USER**";
  - README M6 row — "Planned (live validation human-gated)".
  The head of the same file (`live-validation.md:7-8`) also still calls Part B
  "PENDING USER", contradicting its own line 131.
- **Spec / context:** the milestone is explicitly **human-gated**; the agent
  cannot drive the VS Code extension UI (AGENTS.md / project memory). A Part-B
  "COMPLETE" is only valid if a human actually performed and recorded the run.
- **Impact:** The doc set is internally inconsistent: an auditor reading the
  committed `review.md` / `plan.md` / README concludes Part B is open, while the
  working-tree `live-validation.md` claims it is closed. Risk: M6 is treated as
  fully validated on the strength of an uncommitted, not-cross-referenced edit,
  masking whether the human-gated step is actually done. (The Part-B evidence is
  internally plausible and not contradicted by the code — this is a
  consistency/traceability issue, not an accusation of fabrication.)
- **Recommendation:** If the user did run Part B on 2026-06-28, reconcile in one
  commit — update `live-validation.md` lines 7-8 to COMPLETE and flip
  `review.md` / `plan.md` / the README M6 row to match, then commit. If not, mark
  Part B as still PENDING in `live-validation.md`. The status must be consistent
  across all four documents before M6 is declared complete.
- **Resolution:** Fixed without claiming human-gated completion. The Part B
  section now remains `PENDING USER CONFIRMATION`; the detailed VS Code evidence
  is retained as candidate evidence until the user confirms the run.

---

## Evaluated but not filed

### DR6 matrix rules verified as genuinely asserted (negative, inject-then-assert-absent)

1. **Raw never via JSON / SSE** — list & spans APIs (`DefaultSurfaces_NeverReturnRawOrPii_...`, `SpanApi_NeverReturnsRawOrPii_...`); SSE emits only `data: {}`.
2. **Raw HTML routes reject cross-site → 403** — both raw-detail and trace-detail, for **both** `Sec-Fetch-Site: cross-site` and a foreign `Origin`.
3. **`Cache-Control: no-store` on ALL raw-bearing routes** — `AllRawBearingRoutes_SetNoStore` asserts both the trace-detail page and the raw-detail route; the raw-bearing set is exactly those two (no third route inlines `PayloadJson`). Closes plan-review finding F5. (The `403`/`404` short-circuit header gap is filed under M5-2, not here, since those responses carry no raw.)
4. **`--sanitized-only` ⇒ raw routes `404` + PII excluded + no cacheable raw** — routes conditionally unmapped; PII-excluded asserted across all read APIs.
5. **Per-attribute sanitization (email/path/secret into `tool_name`, `mcp_tool_name`, `agent_name`, `error.type`), incl. under `--sanitized-only`** — `SpanApi_GuardsUnsafeFreeFormValues_AndKeepsRows` `[Theory false/true]`.
6. **Non-loopback bind rejected; Host-header validation** — `IsAllowedLoopbackHost` exact-match only (`localhost`/`127.0.0.1`/`::1`/`[::1]`); `X-Forwarded-Host` not trusted (no ForwardedHeaders middleware); DNS-rebinding defended.
7. **Raw / PII never logged or committed** — logging providers cleared; error bodies asserted raw/path/`Environment.UserName`-free; sprint artifacts contain only synthetic placeholders (`user@example.com`, `leak-marker@example.com`).

### Evaluated and rejected as bugs (by-design within the documented threat model)

- **No CSRF/same-origin check on `POST /v1/traces`** (`MonitorHost.cs:303`): this
  is the OTLP receiver (local exporter sends no `Origin`); it only *writes* a
  telemetry record and returns no raw, and a same-user local process can already
  write records. The matrix's "state-changing action" rule is asserted via the
  `/events` GET-only negative test. Acceptable per the single-user local threat
  model — noting only so it is not mistaken for full mutation-route coverage.
- **Same-origin check treats "neither `Sec-Fetch-Site` nor `Origin`" as
  same-origin** (`MonitorHost.cs:474-495`): required for legitimate top-level
  navigation / direct GET; cross-origin `fetch`/sub-resource requests carry
  `Sec-Fetch-Site: cross-site` or `Origin` and are caught. A legacy-browser
  `no-cors` GET is opaque (body unreadable). Acceptable.
- **Per-attribute sanitization on the trace-detail HTML page** is covered only
  transitively (the values are guarded null at projection time, so the HTML test
  is redundant). Not a vulnerability; an optional thoroughness test.
- **Deliberate display-side de-scope** (no CSP / no XSS payload-matrix): accepted
  per AGENTS.md / D020; default-encoding escaping is the kept baseline and is
  verified.

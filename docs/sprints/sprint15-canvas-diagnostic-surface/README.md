# Sprint15: Canvas Diagnostic Surface

Sprint15 raises the GitHub Copilot app **Canvas adapter** from the Sprint11
"thin action helper" into a **Local Monitor reuse-type diagnostic surface**.
The goal is that a user who opens the Canvas can immediately tell the Local
Monitor connection state and the next action, can pick a trace from a line that
carries decision-supporting fields, and can dispatch a Copilot analysis with a
clear focus — **without reimplementing the Local Monitor UI inside the Canvas
extension**.

```text
NG: the Canvas adapter reimplements the Local Monitor UI as a second monitor
OK: the Canvas adapter reuses the Local Monitor view model / endpoint / projection
    and presents a Canvas-shaped diagnostic surface
```

> **Sprint number correction.** This work was filed as a "Sprint12 parent
> Issue". The repository's Sprint12 (Monitor UX Redesign, D032/D033) is already
> complete, Sprint13 is complete, and Sprint14 is in progress. To avoid a number
> collision this Epic is created as **Sprint15** (D036).

This is a parent Epic. It is split into child issues A–E. Only **child A**
(helper UX, display boundary unchanged) is implemented in this sprint; B–E are
specified here and deferred to later sprints.

## Controlling guidance

The Canvas extension remains the repository-local project-scoped extension at
`.github/extensions/otel-monitor-canvas/`. The `/create-canvas` skill at
`.github/skills/create-canvas/SKILL.md` is the controlling workflow; it must not
be replaced with a `.github/prompts/*.prompt.md` file. Decisions D029, D030, and
D036 govern this sprint. Boundary rules are in
[security-data-boundaries.md](../../specifications/security-data-boundaries.md).

## Principle: reuse, do not reimplement

1. Reuse the existing Local Monitor sanitized API / projection.
2. If Canvas needs data the monitor does not yet project, add a **reusable**
   sanitized view model / endpoint on the Local Monitor side (shared with the
   Razor pages), not a Canvas-only data store.
3. Do not put heavy UI logic inside the Canvas extension.
4. The Canvas helper HTML stays a lightweight shell; diagnostic data comes from
   the Local Monitor projection.

## Boundary (unchanged by child A)

- Canvas action responses stay **bounded DTOs**. They never include raw prompt /
  response bodies, tool arguments / results, PII, credentials, tokens, local
  sensitive paths, or raw OTLP payloads.
- Extension-owned servers bind to `127.0.0.1`, close in `onClose()`, and use a
  per-launch token. Diagnostics use `session.send()` / no `console.log()`. No
  CDN / remote fetch.
- No raw / local detail bodies in GitHub Issues / PRs / repo docs / static
  dashboard / GitHub Pages / CI artifacts, in the Canvas extension logs, or in
  committed output.
- Whether a Canvas surface may show a prompt / response preview is **child D**'s
  separate design decision and is **not** enabled by child A.

## Child issues

### A. Canvas helper UX improvement — **implemented this sprint**

Display boundary unchanged. Improve only the look and the operation path of the
extension-owned helper page.

- Decision-supporting trace line (not just `trace_id — status — spans:N`):
  status / primary model / span count / tool call count / token total /
  duration / time / shortened trace id. All fields come from the existing
  `compactTrace`-shaped sanitized `/api/monitor/traces` proxy — **no new
  endpoint**.
- Japanese focus labels (遅い原因 / トークン消費 / キャッシュ効率 / エラー原因),
  keeping the enum values `latency` / `tokens` / `cache` / `errors` and the
  action names unchanged.
- Japanese button / heading text (e.g. `Copilotでこのトレースを分析`).
- Concrete health / error guidance: distinguish `ready` / `not_ready` /
  `unreachable`; show the `/health/ready` URL, a start command, DB path / port /
  URL configuration hints, and the monitor base URL the extension references.
- Collapse the raw health response by default.

Feasibility: high.

### B. Canvas dashboard view — deferred

New-Session entry point showing the whole Local Monitor at a glance: recent
traces, error traces, high-token traces, slow traces, per-model summary,
per-client-kind summary, connection state, recent sessions.

Reuse direction: add a sanitized aggregate endpoint on the Local Monitor (e.g.
`GET /api/monitor/summary`) built from `MonitorTraceRollup` and the existing
projection store, **shared with the Razor index page** so the dashboard
highlight logic is not duplicated in JS. Client-side aggregation over a single
`/api/monitor/traces` page (limit 50) is rejected because it cannot compute
error / slow highlights across the full dataset. Public-interface change → spec
first.

Feasibility: medium–high.

### C. Canvas trace detail view — deferred

Render the selected trace on the Canvas from the existing bounded actions
(`get_trace_summary` / `get_trace_span_tree` / `get_cache_summary`): trace
summary, span tree, top spans, token / latency / cache / error summary, tool
call summary, and the analysis-dispatch affordance. Bounded projection only; no
raw preview.

Feasibility: medium.

### D. Canvas raw preview boundary — deferred (design first)

Decide whether the Canvas loopback helper page may show a prompt / response
preview, and under which controls (no-store / loopback / same-origin /
per-launch token, explicit user action, contract-test changes). Action responses
stay bounded DTOs regardless. This conflicts with the Sprint11 boundary, so it
is an independent design issue handled after A's UX and bounded detail land.

Feasibility: medium (technically possible; boundary design needed).

### E. Session-to-trace correlation — deferred (design first)

Stably correlate the open Copilot app session with a Local Monitor trace. Design
a correlation key from session id / instance id / trace id / resource
attributes. When no stable key exists, show an explicit "inferred" candidate or
require manual selection — never present an auto-correlation as confirmed.

Feasibility: low–medium.

## Recommended implementation order

A → B → C → D → E. A changes no display boundary and is immediately useful; B/C
advance Local Monitor projection reuse; D changes a design boundary and is taken
after the UX / bounded detail are in place; E is the most uncertain and needs a
correlation-key design.

## Tech-debt prerequisite (F8)

`docs/task.md` records tech-debt F8: the Canvas contract test is
substring-matching only and cannot catch syntax errors or helper-server token /
request-shaping regressions, and JS-level executable smoke coverage should be
added **before** any major `extension.mjs` change. Child A is exactly such a
change, so F8 is handled first (milestone M1, step A0): extract the
side-effect-free pure functions into `canvas-helpers.mjs`, add a `node --test`
smoke, and wire `node --check` / `node --test` into the `dotnet test` gate.

## Acceptance criteria (parent Epic)

- The Canvas improvement direction is defined as a Local Monitor reuse-type
  diagnostic surface, not a re-implementation of the Local Monitor UI.
- Requirements are split into child issues A–E at an actionable granularity.
- Display-boundary-changing work (D) is separated from boundary-unchanged UX
  work (A).
- The implementation order is explicit.

Product conditions the Epic ultimately targets (across all children) are listed
in the parent Issue and carried by D036.

## Milestones

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Helper UX (child A) | F8 smoke scaffold (A0), decision-supporting trace line, Japanese focus / button / heading, concrete health/error guidance, collapsed health response, contract-test update. Display boundary unchanged. | Implemented; automated tests + self-review done. Live Canvas runtime validation pending (human-gated). |

## Validation

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

JS smoke (F8):

```powershell
node --check .github\extensions\otel-monitor-canvas\extension.mjs
node --check .github\extensions\otel-monitor-canvas\canvas-helpers.mjs
node --test .github\extensions\otel-monitor-canvas\canvas-helpers.test.mjs
```

Canvas runtime live validation (new UI text, decision-supporting trace list,
Japanese focus, health/error guidance, analyze trigger) is human-gated, like
Sprint11 M6, and recorded as pending. The completion bar for child A is the
automated test suite + `node --check` / `node --test` + recorded self-review.

## Non-goals

- Storing local detail bodies on GitHub, repository-safe dashboards, or CI
  artifacts.
- Emitting local detail bodies into Canvas extension logs.
- Adding an independent data store unrelated to the Local Monitor on the Canvas
  side.
- Re-implementing the Local Monitor UI inside the Canvas adapter.
- Returning to `--sanitized-only` as a Canvas prerequisite.

## Related

- Issue #39: run Copilot SDK raw analysis from the Local Monitor (Sprint13, D035).
- Sprint11 Canvas adapter PoC (D029, D030).
- Local Monitor raw / repository-safe boundary (D020, D023, D032,
  security-data-boundaries.md).

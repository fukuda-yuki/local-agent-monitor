# Issue #52 Mandatory Design Gate Evidence

Status: **approved by the user on 2026-07-11** (four-tab prototype v1,
published as a private Claude artifact and reviewed with the screenshots
below). The user also decided the Issue #53 dependency: implement the
Issue #49 agent execution graph first, then integrate it in #53.

Prototype-to-v1 delta noted at approval time: the "レビュー指摘" quality-gate
row in the prototype is illustrative; the v1 gate derivation is pinned in the
Issue #52 specification and only includes gates with a deterministic data
source.

## Gate requirement

`docs/specifications/interfaces/canvas-session-workspace.md` (Pre-UI Gate):
before any Issue #51 Session UI implementation, Issue #52 must capture the
current screen and obtain approval for the four-tab prototype.

## Current screen capture

- `current-canvas-full.png` — the current Canvas surface rendered at
  1280px width with synthetic stub data.

Capture method: the Canvas surface returned by `createCanvas.open()` is the
extension-owned loopback helper page (`renderHelperHtml` in
`canvas-helpers.mjs`). The capture harness served the unmodified
`renderHelperHtml` output and stubbed the same-origin helper API routes
(`/api/traces`, `/api/summary`, `/api/analysis/options`,
`/api/trace-detail/*`, `/api/trace-content/*`) with synthetic JSON, then
screenshotted the populated ready state in Chromium. No Local Monitor DB, no
real telemetry, and no Copilot session were involved. An in-app capture from a
live GitHub Copilot App session was not performed (requires a human-driven
session); the rendered page is the same DOM/CSS the app displays.

## Four-tab prototype

- `four-tab-prototype.html` — self-contained interactive prototype
  (synthetic data only; no network access).
- Screenshots (1280px, Chromium):
  - `proto-review-running.png` — Review, exact-bound running session.
  - `proto-review-completed.png` — Review, completed session with quality
    gate results and human evaluation enabled.
  - `proto-review-failed.png` — Review, failed session with FAIL gates,
    `not_captured` instruction state, and evidence links.
  - `proto-review-unbound.png` — Review, OTel-only unbound session
    (honest empty states, no inferred completion).
  - `proto-evidence-completed.png` — Evidence, agent tree + linked
    timeline + Main Agent inspector with verdict evidence links.
  - `proto-evidence-failed.png` — Evidence, unresolved relationship badge
    and error chips.
  - `proto-improve.png` — Improve placeholder (post-#52 child scope).

## Design constraints honored

- Design tokens, card layout, pill/badge idiom, and Japanese UI copy are
  inherited from the current Canvas helper page (`renderHelperHtml`).
  Dark values follow the same host-variable convention.
- No latest-session guessing: the sidebar auto-selects only the
  exact-bound (native session ID) session; unbound sessions sit in a
  separate group with an explicit warning.
- Running sessions show "未確定" and are never judged success/failure.
- `not_captured` instruction content and unbound sessions render honest
  empty states; nothing is reconstructed by inference.
- Agent relationships are labeled `exact` / `inferred` / `unresolved`
  (Issue #49 model vocabulary; Evidence production work is gated on the
  #49 dependency decision).
- Improve / Compare tabs are shell placeholders; their behavior stays in
  later child issues.

## Data safety

All captures and prototype data are synthetic. No captured prompts, real
telemetry, credentials, or machine paths appear in this folder.

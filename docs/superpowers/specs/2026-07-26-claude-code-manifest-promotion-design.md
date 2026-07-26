# Claude Code Manifest Promotion Design

## Goal

Align the Claude Code source capability manifest with the repository's already
implemented and approved observed producer paths without changing the v1 schema,
runtime behavior, or the meaning of any unobserved capability.

## Evidence and current mismatch

Issue #101 promoted the Claude OTel mapping from documentation-only to
`print_mode_live_observed`, with live evidence for structural OTel spans and
exact span classification. Issue #104 shipped the Claude first-trace setup and
Doctor handoff. Issue #107 shipped batch-level OTel content-state derivation.
The current `claude-code.json` still declares `support_status: planned` and
leaves all capability leaves except source-version detection as `unknown`.

The canonical Claude binding and security documents still contain historical
statements that no Task 7-9 producer structure was observed. Those statements
must be narrowed to the capabilities that remain unobserved; otherwise the
manifest and its semantic source of truth disagree.

## Contract changes

Change only these existing v1 values in
`docs/specifications/contracts/source-capabilities/v1/manifests/claude-code.json`:

- `support_status`: `planned` -> `preview`.
- `signals.trace`: `unknown` -> `available`.
- `signals.hook`: `unknown` -> `available`.
- `trace_span_identity.trace_id`: `unknown` -> `available`.
- `trace_span_identity.span_id`: `unknown` -> `available`.
- `trace_span_identity.parentage`: `unknown` -> `available`.
- `timing_ttft.timing`: `unknown` -> `available`.
- `content_capture_gate`: `unknown` -> `available`.

Keep `stability: preview`, `source_version_detector: available`, and every
other leaf unchanged at its current value. In particular, do not promote native
session identity, TTFT, model/token values, retry/attempt, tool input/output,
permission decisions, ownership, prompt/response, or file/diff capabilities.

## Documentation alignment

Update only stale capability-status claims in the Claude canonical documents:

- `claude-code/exact-binding.md` will distinguish observed OTel identity,
  parentage, timing, and Hook path support from the still-deferred complete
  trace-context binding and unobserved native-session capability.
- `claude-code/security.md` will state that the selected trace/Hook/content-gate
  capabilities are observed or implemented, while content-bearing fields and
  all other unobserved semantic leaves retain their existing gates and safety
  boundaries.

The OTel and Hook mapping authority, evidence-source records, v1 schema, source
surface registry label, and runtime implementation remain unchanged.

## Regression and integrity updates

Update `SourceCapabilityContractTests` to assert the promoted header and exact
Claude availability matrix. The matrix must continue to prove that every
unlisted capability remains `unknown`.

Recompute the canonical SHA-256 for the changed Claude manifest and update the
matching row in
`docs/specifications/interfaces/token-context-cache-alert-rules.md` and the
corresponding alert test fixture. No other manifest hash changes.

## Validation

Use the repository-pinned focused ConfigCli contract test first, followed by the
Alerts test that pins the manifest hash. Then run the required solution build,
Playwright Chromium installation, and full solution test commands from
`AGENTS.md`. Inspect the final diff to confirm the user's pre-existing deletion
of `artifacts/dashboard-input/README.md` remains untouched and no raw or local
runtime data is added.

## Non-goals

- No schema major-version change or new manifest field.
- No adapter, receiver, database, HTTP, UI, setup, or Doctor code change.
- No promotion of fields supported only by official documentation or by
  unobserved producer semantics.

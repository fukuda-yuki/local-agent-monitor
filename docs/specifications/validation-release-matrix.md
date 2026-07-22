# Validation And Release Matrix Specification

This specification defines the canonical cross-surface validation contract.
It validates existing product behavior; it does not authorize production
behavior changes, missing surfaces, compatibility shims, or security-policy
changes.

The machine-readable schema is
[`validation-matrix.schema.json`](contracts/validation-matrix/v1/validation-matrix.schema.json).
Future owners extend
[`future-surface-registry.json`](contracts/validation-matrix/v1/future-surface-registry.json)
when their surface becomes active.

## Candidate And Inventory Contract

Every work item records `matrix_prep_sha` before preparing fixtures. A final
candidate may be frozen only after every declared dependency satisfies its own
close/acceptance condition and the accepted dependency revisions are ancestors
of the candidate. Evidence and classifications use the exact
`final_validation_sha`, never a branch name or moving `main`.

The final inventory is derived from that immutable tree. The validator
re-inventories every route, reader, writer, store, adapter, UI/action response,
log stream, and validation contract changed since `matrix_prep_sha`. Every
active surface has a known owning component or Issue. A replacement candidate
invalidates classifications affected by changes to production code,
specifications, fixtures, scanner logic, or executable tests.

An active operation/profile has a `required` or `optional` requirement level
and a separate `applicable` or `not_applicable` applicability state.
`not_applicable` requires a canonical contract reference and reason. Optional
applicable rows still receive a terminal classification. An unavailable future surface is
not an active row and appears only in the future registry as `not_available`.

Issue #86 transitions `transactional-sanitized-import` out of that registry.
Its exact active artifact is
`docs/sprints/issue-86-sanitized-import/validation-matrix.json` and owns rows
`91-I-086` (archive/transaction/API/CLI/UI), `91-S-086` (strict security and
retention boundary), and `91-L-086` (genuine second-machine execution). A
same-machine database or path relocation is useful automated compatibility
evidence but cannot classify `91-L-086` as passed.

## Profile Axes

Rows keep independent axes rather than flattening them into one label:

- collection/routing profile;
- content access: `raw-default`, `content-disabled`, explicitly authorized
  content enabled, or `sanitized-only`;
- source compatibility: supported fingerprint, new/unverified version,
  known-incompatible/required-signal-missing, schema drift, unknown fields, or
  parse failure;
- Hook and OTel availability;
- binding/completeness: Hook-only, OTel-only, exact-linked, partial, or unbound;
- restart/reconnect state; and
- retention lifecycle plus pinned/unpinned/delete-now state.

Missing capabilities remain missing. They are not projected as false, zero,
safe, supported, or exactly linked.

## Active Row Classification

The closed classification set is:

- `passed`: all required observations executed and matched the invariant;
- `failed`: an automated failure, product defect, security failure, or other
  candidate-resolvable failure;
- `blocked_external`: a required live row that repository code cannot resolve,
  such as provider/source availability, quota, or missing operator
  authorization;
- `not_applicable`: the operation/profile is excluded by a cited canonical
  contract; and
- `not_attempted`: transient work-in-progress state, forbidden at close.

Skipped, unavailable, timed-out, retried, incomplete, or unexecuted cases are
never `passed`. `blocked_external` is live-only and records severity, exact
blocker, retry condition, and unverified capability. Code defects, automated
failures, and hard-security failures are `failed`, never external blockers.

Every row records the schema version, stable row ID, surface, operation,
profile axes, versions where applicable, expected invariant, automated/live
evidence references, bounded actual result, classification, severity, blocker
and retry condition, unverified capability when externally blocked, owner,
validation SHA/date, and environment boundary. The executable semantic
validator rejects duplicate row IDs, row/candidate SHA mismatches, missing
classification evidence, inconsistent blocker fields, and a release decision
that does not equal the aggregate row state.

## Evidence Compatibility And Safety

Historical live evidence may be reused only when revision, surface,
source/application/adapter version, sanitized setting labels, and environment
boundaries are compatible. The row records that basis. Mismatched evidence is
historical context only; rerun the coverage gap. A historical blocked case is
never promoted to pass.

Repository-safe evidence contains only bounded classifications, counts,
non-sensitive versions, opaque references, exit codes, and sanitized setting
labels/effective states. It never contains credentials, authorization values,
raw prompts/responses, tool arguments/results, source/file bodies, PII,
database content, reversible marker values, or machine-sensitive paths.

The synthetic scanner supports only the transformations declared by its
versioned corpus. It is a deterministic release validator, not enterprise DLP,
privacy/legal certification, arbitrary recursive decoding, decryption,
decompression, or secure-erasure proof.

## Mandatory Invariants

- Sanitized API, SSE, UI, Canvas action/helper output, application logs, and
  repository-safe evidence contain no synthetic raw/secret/PII/path marker or
  marker-derived label.
- Errors contain no payload fragment, credential, authorization value, or
  sensitive absolute path. Nested JSON/Markdown/HTML remains inert text.
- Only allowlisted loopback/same-origin raw surfaces return raw content and all
  raw-bearing responses are `no-store`.
- `sanitized-only` removes raw routes, prompt labels, raw analysis, and other
  raw-bearing actions while retaining sanitized views.
- Content-disabled sources do not fabricate raw content.
- Expiry and confirmed delete-now deny reads before physical deletion; later
  pin, restart, or retry cannot restore deleted content.
- Session deletion uses exact ownership only. Repository, cwd, path, timestamp,
  shared trace ID, and generic adapter label are not identity evidence.
- Unsupported/incompatible, drifted, Hook-unavailable, and OTel-unavailable
  states remain distinct. New/unverified version alone is not unsupported.

Any violation above is a hard blocker and prevents a release-ready decision.

## Release Decision

The matrix returns exactly one decision and, when applicable, a structured
external-blocker summary containing the row ID, severity, blocker, retry
condition, and unverified capability:

- `release_ready`: every required active row is `passed` or contract-based
  `not_applicable`;
- `release_ready_with_external_blockers`: every automated and hard-security row
  passes, and only permitted live rows have exact `blocked_external` results;
- `release_blocked`: any failed, not-attempted, unclassified, hard-blocked, or
  code-defect row remains.

Close requires no unknown owner or unclassified active surface, no
`not_attempted` row, a complete future registry with owners and entry
conditions, repository-safe handoff, and the repository-required build,
Playwright bootstrap, and full test suite against the unchanged candidate.

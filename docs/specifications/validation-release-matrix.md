# Validation And Release Matrix Specification

This specification defines the reusable cross-surface validation contract. It
validates existing product behavior; it does not authorize production behavior
changes, compatibility paths, or security-policy changes.

The reusable machine-readable result shape is
[`validation-matrix.schema.json`](contracts/validation-matrix/v1/validation-matrix.schema.json).
Candidate-specific matrices, plans, evidence, checksums, attestations, and
closeout reports are not repository artifacts. A workflow may materialize the
schema under an ignored local directory or as a GitHub Actions artifact, while
the Pull Request or active Issue records the bounded outcome.

## Validation Lanes

Development uses the Affected, Completion, and Nightly lanes defined by
[`repository-workflow.md`](../agent-guides/repository-workflow.md):

- Affected validation runs the nearest component-owned check for the changed
  behavior or contract.
- Completion CI runs the portable deterministic Fast set and fixed Critical
  smoke set for Pull Requests and pushes to `main`.
- Nightly runs the schedulable deep matrix on Windows and Linux without
  operator-only live validation.

Component-owned specifications, executable tests, and reusable validation
scripts define exact coverage. Future work and unavailable surfaces remain in
their owning GitHub Issues instead of a repository roadmap or future-surface
registry.

## Candidate And Evidence Contract

When a release workflow freezes a candidate, all evidence uses one exact
`final_validation_sha`, never a branch name or moving `main`. A preparation SHA
may be used when the owning release workflow must prove which changes were
inventoried; it must resolve to an ancestor of the final candidate.

Every active operation/profile has a `required` or `optional` requirement level
and a separate `applicable` or `not_applicable` applicability state.
`not_applicable` requires a current contract reference and reason. Optional
applicable rows still receive a terminal classification.

The closed classification set is:

- `passed`: every required observation executed and matched the invariant;
- `failed`: an automated failure, product defect, security failure, or other
  candidate-resolvable failure;
- `blocked_external`: a required live row that repository code cannot resolve,
  such as provider availability or missing operator authorization;
- `not_applicable`: the operation/profile is excluded by a current contract;
  and
- `not_attempted`: transient work-in-progress state, forbidden at close.

Skipped, unavailable, timed-out, retried, incomplete, or unexecuted cases are
never `passed`. `blocked_external` is live-only and records severity, exact
blocker, retry condition, and unverified capability. Code defects, automated
failures, and hard-security failures are `failed`, never external blockers.

Rows keep independent profile axes where applicable: collection/routing,
content access, source compatibility, Hook and OTel availability,
binding/completeness, restart/reconnect, and retention lifecycle. Missing
capabilities remain missing; they are not projected as false, zero, safe,
supported, or exactly linked.

## Evidence Compatibility And Safety

Historical live observations may inform a current canonical inventory only
when revision, surface, source/application/adapter version, setting labels, and
environment boundaries remain compatible. The current inventory records the
bounded fact and compatibility basis. A historical blocked result is never
promoted to pass.

Repository-safe results contain only bounded classifications, counts,
non-sensitive versions, opaque references, exit codes, and sanitized setting
states. They never contain credentials, authorization values, raw prompts or
responses, tool arguments or results, source/file bodies, PII, database
content, reversible marker values, or machine-sensitive paths.

The synthetic scanner supports only the transformations declared by its
versioned corpus. It is a deterministic release validator, not enterprise DLP,
privacy/legal certification, recursive decoding, decryption, decompression, or
secure-erasure proof.

## Mandatory Invariants

- Sanitized API, SSE, UI, Canvas action/helper output, application logs, and
  repository-safe results contain no synthetic raw/secret/PII/path marker or
  marker-derived label.
- Errors contain no payload fragment, credential, authorization value, or
  sensitive absolute path. Nested JSON, Markdown, and HTML remain inert text.
- Only allowlisted loopback/same-origin raw surfaces return raw content, and all
  raw-bearing responses are `no-store`.
- `sanitized-only` removes raw routes, prompt labels, raw analysis, and other
  raw-bearing actions while retaining sanitized views.
- Content-disabled sources do not fabricate raw content.
- Expiry and confirmed delete-now deny reads before physical deletion; later
  pin, restart, or retry cannot restore deleted content.
- Session deletion uses exact ownership only. Repository, cwd, path, timestamp,
  shared trace ID, and generic adapter label are not identity evidence.
- Unsupported, drifted, Hook-unavailable, and OTel-unavailable states remain
  distinct. New or unverified version alone is not unsupported.

Any violation above is a hard blocker and prevents a release-ready decision.

## Result Placement And Release Decision

Reusable schemas, scanners, deterministic fixtures, and validation methods
remain with their component or contract owner. Run-specific output goes to the
Pull Request review or summary, the active GitHub Issue comment, or a bounded
GitHub Actions artifact. Local scratch stays under an ignored directory.

The matrix returns exactly one decision:

- `release_ready`: every required active row is `passed` or contract-based
  `not_applicable`;
- `release_ready_with_external_blockers`: every automated and hard-security row
  passes, and only permitted live rows have exact `blocked_external` results;
- `release_blocked`: any failed, not-attempted, unclassified, hard-blocked, or
  code-defect row remains.

Close requires no unknown owner or unclassified active surface. The active
GitHub Issues own future surfaces and entry conditions; repository validation
does not maintain a parallel future-work registry.

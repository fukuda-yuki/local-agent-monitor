# Issue #92 Codex App discovery

Status: discovery complete / Codex App Desktop production integration `NO-GO`;
final repository validation pending.

This directory is historical discovery and validation evidence. It does not
define product behavior. Current behavior is defined by
`docs/requirements.md`, `docs/spec.md`, and the relevant files under
`docs/specifications/`.

## Result

Issue #92 established a bounded structural inventory for Codex
CLI/app-server `0.145.0` alongside independently detected Codex App Desktop
package `26.715.10079.0`. The successful live producer was a standalone
app-server, not a Desktop-owned process. The Desktop-bundled producer binary
was present, but direct terminal execution was blocked by WindowsApps access
control. The standalone run is not substituted for that blocked retry.
Producer version `0.145.0` came from the public CLI version command, not from a
retained `service.version` value.

A safe sanitized OS diagnostic that did not read command lines observed a
package-root `codex.exe` process with a package-root parent process. It emitted
no ID, path, or hash value, did not identify the child role, and does not prove
app-server identity, Desktop-owned OTel execution, App Session/window identity,
or merge authority. An earlier command-line-reading attempt was invalidated and
excluded; none of its values were retained.

The approved decision is `NO-GO` for Codex App Desktop production integration:

- content-disabled per-command routing to a disposable loopback JSON OTLP
  receiver worked for the standalone producer;
- source trace/span IDs, source parent fields, timing, and version structure
  were observed;
- Desktop ownership, App Session/window identity, native thread/turn to OTel
  binding, concurrency, restart/resume, complete signal/field coverage, and
  content-enabled behavior remain unverified;
- generic runtime `thread.id`, process, repository, workspace, cwd, timestamp,
  prompt, and arrival order are not correlation authority;
- the standalone native-thread relation was observed unbound, while native-turn
  correlation is unverified because no turn ran;
- Issue #92 adds no production adapter, Setup, Doctor, UI, persistence, or
  private-state integration.

The v1 manifest cannot express standalone-only availability while excluding
Desktop ownership. It therefore promotes only the independently observed
source-version detector; trace and Desktop-specific capabilities remain
`unknown`.

The verbatim live command, exact ephemeral harness, and raw output were not
retained. Committed counts, shapes, and key names are non-replayable sanitized
attestation and cannot authorize the manifest or production integration.

## Blocked follow-up

Issue #93 production adapter, Setup, Doctor, UI, trace-manifest promotion, and
future-registry activation remain blocked and must not start from this
attestation. A separately approved discovery retry and prerequisite
configuration specification must first establish Desktop-owned execution, a
retained repository-safe replay harness, exact supported
configuration/detection, safe log-export policy, source identity and parentage,
and exact-or-explicitly-unbound native correlation.

Existing Codex App generated log-export profiles are a high-severity blocker:
the pinned `rust-v0.145.0` tool-result logs include arguments, output, and error
text that may be content- or path-bearing even when prompt logging is disabled.
Issue #92 does not change those production generators.

Artifacts:

- [Plan](plan.md)
- [Live validation evidence](live-validation.md)
- [Artifact checksums](artifact-checksums.json)
- [Discovery inventory](../../specifications/contracts/source-capabilities/v1/codex-app/discovery-inventory.json)
- [Exact-correlation table](../../specifications/contracts/source-capabilities/v1/codex-app/exact-correlation.md)
- [Sanitized initialize fixture](../../specifications/contracts/source-capabilities/v1/codex-app/fixtures/content-disabled-initialize.sanitized.json)
- [Sanitized thread-start fixture](../../specifications/contracts/source-capabilities/v1/codex-app/fixtures/content-disabled-thread-start.sanitized.json)
- [Sanitized process-tree diagnostic](../../../scripts/validation/issue-92/observe-desktop-process-tree.ps1)

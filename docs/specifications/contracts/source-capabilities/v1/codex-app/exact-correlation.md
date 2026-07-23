# Codex App exact-correlation inventory

This document records Issue #92 discovery evidence. It is not a production
adapter contract and does not claim Codex App Desktop support.

## Validated tuple and evidence boundary

- Desktop package: `26.715.10079.0`, detected from public package metadata.
- Codex CLI/app-server: `0.145.0`, detected with the public version command;
  the observed `service.version` key value was not retained and is not version
  authority.
- Desktop-bundled producer: binary present, but direct terminal execution
  blocked by WindowsApps access control.
- Observed producer: standalone app-server, driven through the public
  app-server protocol. It is not a substitute for the blocked Desktop retry.
- Capture: content-disabled, per-command overrides only, disposable loopback
  JSON OTLP receiver.
- Committed evidence: key names, counts, identifier shapes, and relationship
  states only. Raw payloads, identifier values, resource-attribute values,
  machine paths, and user content were discarded.

Package and producer versions were observed independently. Their coexistence
does not establish process ownership or Desktop App integration.

A safe read-only OS diagnostic that projected only process IDs, parent IDs, and
executable paths observed a package-root `codex.exe` process with a package-root
parent process. It emitted no ID, path, or hash value and did not read command
lines. It therefore did not identify the child role or prove app-server
identity, Desktop-owned OTel execution, Session identity, or merge authority.
An earlier command-line-reading attempt was excluded from evidence; no value
from it was retained.

## Correlation table

| Relationship | Status | Accepted authority | Required absence behavior |
| --- | --- | --- | --- |
| Desktop package → package-root process tree | `non_authoritative_diagnostic` | Safe sanitized package-root `codex.exe` child/parent relationship only. | Diagnostic reporting only; do not infer child role, telemetry ownership, or join by process, time, repository, workspace, cwd, or order. |
| Desktop package → app-server process | `unverified` | None. The accepted diagnostic did not read command lines or identify the child role. | Keep app-server identity and Desktop telemetry ownership unverified. |
| app-server process → App Session/window | `unverified` | None observed | Keep App Session identity absent. |
| protocol native thread → OTel trace | `unbound` | A native thread ID was returned by `thread/start`, but was absent from the corresponding OTel span. | Keep the OTel trace unbound; generic `thread.id` is not native identity. |
| protocol native turn → OTel span | `unverified` | No turn profile was executed. | Make no turn-to-span support or absence claim. |
| OTel trace → OTel span | `exact` | Source trace ID carried by each source span. | Preserve the source IDs byte-for-byte after normal transport decoding. |
| OTel span → parent span | `source_declared_partial` | Source `parentSpanId` field. | Preserve present values; leave references outside the exported batch unresolved and never synthesize roots. |
| concurrent Desktop windows/protocol threads | `unverified` | None observed. | Make no ownership or isolation claim. |
| restart/resume continuity | `unverified` | None observed. | Make no continuity or merge claim. |

Repository, workspace, current directory, process identity, timestamps, prompt
similarity, and arrival order are never identity evidence. The app-server
instrumentation attribute named `thread.id` is a generic runtime tracing field,
not proof of the native Codex protocol thread identifier.

The version-pinned security evidence is
[`registry.rs`](https://github.com/openai/codex/blob/rust-v0.145.0/codex-rs/core/src/tools/registry.rs)
and
[`session_telemetry.rs`](https://github.com/openai/codex/blob/rust-v0.145.0/codex-rs/otel/src/events/session_telemetry.rs).
Together with the official
[Advanced Configuration telemetry inventory](https://developers.openai.com/codex/config-advanced/#observability-and-telemetry),
they establish that tool-result log arguments, output, and error text can be
content- or path-bearing independently of the raw-user-prompt gate.

## Non-replayable standalone attestation

The content-disabled initialization profile emitted one JSON `/v1/traces`
request containing six spans across five trace IDs. It established source trace
and span ID shapes, parent fields, timing, resource and span attribute key
names, and the `codex-app-server` scope. Four spans were roots; two parent
references pointed outside the exported batch. The `thread/start` profile
emitted one JSON trace request containing seven spans and returned a native
protocol thread ID, but that native ID did not appear on its OTel span.

The verbatim command, exact ephemeral receiver/parser harness, and raw output
were not retained. These counts, shapes, key names, and parent-membership
results therefore cannot be independently regenerated from committed
artifacts. They are sanitized attestation only and do not authorize a
`codex-app` production capability.

Codex loaded user configuration before applying the listed per-command
overrides. Global values were not inspected or written, but influence from
unoverridden fields was not excluded. Resource-attribute key names therefore
cannot be attributed solely to the standalone producer or promoted as a stable
Codex App contract.

The v1 Codex App manifest cannot scope an availability declaration to a
standalone producer while excluding Desktop ownership. It therefore promotes
only source version detection. Trace, logs, metrics, native Session identity,
semantic fields, and all Desktop-specific capabilities remain `unknown`.
Manifest availability grants no read, transport, storage, or display authority
for content.

## Production-integration gate

Issue #92 is `NO-GO` for Codex App Desktop production integration. Standalone
CLI/app-server attestation cannot authorize a `codex-app` adapter. Issue #93
production adapter, Setup, Doctor, UI, trace-manifest promotion, and future
registry activation remain blocked and must not start from this attestation. A
separately approved discovery retry and prerequisite configuration contract
must first capture Desktop-owned execution with a retained repository-safe
replay harness and establish exact supported configuration and detection, safe
log-export policy, source identity and parentage, and
exact-or-explicitly-unbound native correlation.

Codex CLI and generic standalone app-server support remain out of scope. The
Issue #91 future registry remains `not_available`.

## Primary references

- [Codex configuration reference](https://developers.openai.com/codex/config-reference/)
- [Codex Advanced Configuration telemetry inventory](https://developers.openai.com/codex/config-advanced/#observability-and-telemetry)
- [Codex app-server documentation](https://developers.openai.com/codex/app-server/)
- [Codex app-server public protocol](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [Codex configuration schema](https://github.com/openai/codex/blob/main/codex-rs/core/config.schema.json)
- [Codex 0.145.0 tool dispatch](https://github.com/openai/codex/blob/rust-v0.145.0/codex-rs/core/src/tools/registry.rs)
- [Codex 0.145.0 tool-result telemetry](https://github.com/openai/codex/blob/rust-v0.145.0/codex-rs/otel/src/events/session_telemetry.rs)

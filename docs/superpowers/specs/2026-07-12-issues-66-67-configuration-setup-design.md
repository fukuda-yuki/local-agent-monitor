# Issues #66-#67 Configuration Setup Design

Date: 2026-07-12

## Goal

Deliver a reversible, user-scoped setup transaction framework and use it for
guided GitHub Copilot configuration without modifying settings the tool does
not own, exposing secrets/paths, or claiming that static configuration proves a
first trace.

## Approved product choices

- Work from the Issue #61 contract baseline, with the shared Windows CRLF test
  fix required for a clean baseline.
- Public verbs are `setup plan`, `setup apply`, `setup rollback`, and
  `setup status`.
- VS Code, CLI, and App/SDK are selectable targets. `all` plans all three, but
  apply still requires an explicit change-set ID.
- Content capture is unchanged by default and requires a separate sensitive
  option.
- Ledger/backups have no automatic retention limit in these issues.
- Unsupported versions and managed conflicts fail closed.
- App/SDK is caller-managed guidance and never an automatic file mutation.

## Context and verified producer contracts

The existing Config CLI is the canonical producer of collection-profile
settings. Existing Windows scripts use current-user environment APIs and the
Release ZIP publishes the Local Monitor app plus PowerShell operations. Issue
#61 declares the `github-copilot-vscode` and `github-copilot-cli` source
capabilities; it has no App/SDK manifest.

Official upstream facts checked on 2026-07-12:

- VS Code 1.119 introduced the stable Copilot OTel settings; guided apply uses
  1.128+ because that release first enforces managed-channel precedence. Current VS Code
  documentation defines policy > environment > user setting > default
  precedence and documents the `github.copilot.chat.otel.*` user settings.
- Copilot managed settings may arrive through native MDM, signed-in-account
  server policy, or a well-known `managed-settings.json`. An external CLI can
  read local MDM/file sources but cannot prove the signed-in-account result.
- Copilot CLI 1.0.4 introduced OpenTelemetry instrumentation. Current GitHub
  documentation defines `COPILOT_OTEL_ENABLED`,
  `COPILOT_OTEL_EXPORTER_TYPE`, `OTEL_EXPORTER_OTLP_ENDPOINT`, and protocol
  variables.
- GitHub Copilot SDK exposes a caller-provided `TelemetryConfig`, including the
  .NET `OtlpEndpoint` and `OtlpProtocol` fields.

Primary references:

- https://code.visualstudio.com/updates/v1_119
- https://code.visualstudio.com/docs/agents/guides/monitoring-agents
- https://code.visualstudio.com/docs/enterprise/ai-settings
- https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference
- https://docs.github.com/en/copilot/how-tos/copilot-sdk/observability/opentelemetry

## Considered approaches

### A. Config CLI transaction engine plus thin PowerShell wrapper (selected)

The Config CLI owns DTOs, ledger, plans, transaction, platform abstractions,
and adapters. The Release ZIP publishes a self-contained Config CLI beside the
monitor app. Repository and packaged wrappers invoke the same executable
contract.

Advantages: one producer contract, executable cross-surface tests, no
PowerShell/C# behavior duplication, and a direct path to later adapters.
Tradeoff: the Release ZIP becomes larger because it contains a second
self-contained executable.

### B. PowerShell-native ledger and mutations

This would fit the existing operations scripts, but would duplicate parsing,
hashing, transaction, result serialization, and test fixtures between scripts
and the Config CLI. It would make the wrapper the producer rather than a
consumer and increase secret/error-output risk. Rejected.

### C. Local Monitor HTTP setup service

This could reuse the installed process, but would add privileged mutation
routes, CSRF/HTTP/proxy/UI DTOs, and a requirement that the monitor already be
running before setup. Issues #66/#67 require CLI/PowerShell and explicitly do
not need a new remote or browser-mediated surface. Rejected.

## Architecture

New Config CLI responsibilities are grouped under `Setup/`:

- `Contracts`: fixed enums/records and JSON serialization.
- `Storage`: private immutable plans/backups/journals and repository-safe ledger v1.
- `Transactions`: exclusive lock, hashes, atomic file operations, current-user
  environment operations, compensation, rollback, and fault points.
- `Adapters`: registration and the `github-copilot` implementation.
- `Platform`: filesystem, environment, registry, process, endpoint, and time
  boundaries used by deterministic tests.
- `Cli`: option parsing and result-code/exit-code mapping.

The existing manual `profile-*` generators and legacy user-environment scripts
remain compatible entry points. New guided setup mutations are the changes
owned by the new ledger; the framework does not retroactively claim ownership
of pre-existing script changes.

No new project dependency is required. JSONC handling uses the BCL parser with
comment/trailing-comma support plus a focused top-level editor that preserves
unrelated keys and comments. The framework TOML codec accepts only the bounded
table/scalar syntax already emitted by Config CLI samples and fails closed on
malformed or unsupported syntax. Issue #67 does not write TOML; a future adapter
must extend the codec contract before accepting broader TOML.

## Data flow

```text
detect target/version/policy/current state/port
  -> adapter creates bounded physical targets/member changes + exact base hashes
  -> coordinator stores private immutable plan and safe planned ledger row
  -> user explicitly applies change-set ID
  -> exclusive lock + all-target preflight
  -> flushed backups + per-file/per-env-member write-ahead intents
  -> ordered atomic physical-target mutations
  -> static verification + applied hashes + safe ledger outcome
  -> optional all-target hash-guarded rollback
```

All mutable operations are injected behind interfaces. Production uses the
real current user; tests use synthetic files, environment dictionaries,
registry views, processes, endpoint probes, barriers, and fault points.

## Ownership and private data

The ledger records fixed IDs/labels, timestamps, state/outcome codes, hashes,
opaque backup references, restart requirements, and rollback status. It never
records an absolute path or value. A successful applied record grants the adapter
authority only over its listed member settings at that physical target's
post-apply hash.

Private plans hold target locations and validated desired values, not previous
values. Flushed apply-time backups hold the exact previous state required for
restore. Journals record physical-target progress. All are local runtime data,
not repository-safe output. Backups are retained until a future explicitly
designed cleanup feature.

## Transaction semantics

Plan order is deterministic and bounded to 16 physical targets, each with at
most 32 member setting changes. Every physical file/environment target has one
base/applied hash and one backup. Apply:

1. acquires a non-waiting exclusive lock;
2. validates every target, base hash, managed state, and reparse policy before
   the first write;
3. flushes all backups;
4. flushes a write-ahead transaction journal;
5. before each file or environment-member write, flushes `mutation_started`,
   then records `mutation_completed` after the write;
6. verifies every file/member still equals desired state, then persists applied
   hashes only after the committed journal;
7. compensates changed steps in reverse order with flushed pre-restore intents
   and immediate desired/prior/third-party classification;
8. records full restoration or per-target partial outcome.

Every command runs journal recovery under the exclusive lock first. Recovery
classifies each started step as prior, desired, or third-party state; third-party
state is preserved and becomes `partial`. Interrupted apply is restored,
committed-write/ledger lag is reconciled by member/file hashes, and interrupted
rollback completes using the same pre-restore intent protocol. A failed recovery
leaves a `partial` change set and permits status only, preventing further
mutation on ambiguous state. Status requests no new setup mutation but can run
this mandatory recovery before returning its bounded projection.

Rollback first validates all current applied hashes, flushes its journal, and
persists `rolling_back` before the first restore. Any preflight mismatch is a
no-write `rollback_stale` outcome while lifecycle remains `applied`; failures
after mutation become `partial`. Every restore reclassifies applied/previous/
third-party state immediately before writing and never overwrites third-party
state. There is no force path. Environment
notification is attempted once on an uninterrupted final path and may be
replayed by recovery when delivery cannot be proven.

The result contract keeps requested/created `change_set_id` separate from
`recovered_change_set_id` and `recovery_operation`. Recovery-blocking status
entries outrank planned and terminal history within the hard 100-entry bound.
Apply revalidates supported versions, managed state, and distinct writable
endpoint ownership before creating backups or mutation artifacts.

All public target results include bounded `detected` and sanitized
`detected_version`; App/SDK uses them to report package presence/version without
exposing a path. Status adds per-target verification state. The change-set state
is diverged before stale before unavailable before current, considering writable targets only,
and rollback is available only when every writable target is current and backed
up. Guidance targets are not part of this aggregation. Each target carries a
lifecycle-relative base/desired/previous/none reference; partial targets use
aggregate member outcomes. All-desired and all-previous targets keep that
reference; mixed desired/previous or preserved third-party state is explicitly
diverged with no single reference, while classification failure is unavailable.
Every partial change set has rollback unavailable.

## GitHub Copilot target behavior

### VS Code

Detect stable `code`, version 1.128+, `GitHub.copilot-chat`, running processes,
user settings, current/user environment, local managed-policy sources, and the
endpoint. Write only enabled/exporterType/endpoint settings. Keep captureContent
unchanged unless the sensitive option is explicit. A managed conflict blocks
the relevant write; an unobservable signed-in policy yields an unverified
effective-state warning and Policy Diagnostics next action.

### Terminal CLI

Detect `copilot version`, require 1.0.4+, compare current-process and
current-user environments, and write only enabled/exporter type/base endpoint/
protocol. Do not set global client identity, resource attributes, or headers.
Capture content is a separate optional record. Restart instructions apply to
existing terminals and sessions.

### App/SDK

Return a no-write .NET `TelemetryConfig` sample with loopback endpoint and
`http/protobuf`. Do not search or edit caller files. Other languages remain
caller-managed and refer to the official SDK contract.

## Error handling

All external exceptions are mapped to fixed safe codes. Raw exception messages
do not enter results, ledger, logs, or tests. Stale, policy conflict, malformed
config, unsafe path, permission denial, foreign port owner, busy lock, partial
apply, and partial rollback have distinct outcomes and next actions.

No-listener endpoint is a warning because setup may precede monitor startup.
A listening but unrecognized service is a conflict. Static success never means
that traces or metrics arrived.

## Testing strategy

Every production behavior begins with a failing test. Required early executable
contracts are:

- serializer round-trip from production DTOs through CLI and wrapper fixtures;
- ledger v1 close/reopen and unknown-version rejection;
- atomic file and ownership-preservation tests;
- barrier-controlled stale/lock/concurrent-edit cases without sleeps;
- failure at every mutation and compensation boundary;
- negative secret/header/value/path markers across all safe outputs;
- real Issue #61 manifest consumption by the VS Code/CLI adapter;
- policy/environment/user/default precedence;
- Release ZIP and repository wrapper invocation using the same result DTO.

There is no old ownership-ledger version, so migration testing is explicitly
N/A for v1. A future v2 must use fixtures from the actually shipped v1 and
verify migration across process restart.

## Review gates

- Each implementation task has a fresh implementer and separate reviewer.
- #66 completion requires security/concurrency review before #67 begins.
- The first real #67 adapter plan consumed through the #66 serializer is the
  early Issue-interface integration gate.
- Final review covers security, concurrency, migration N/A justification,
  Release ZIP compatibility, and the requirement-to-test matrix.

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
- VS Code Stable and Insiders are supported only through their Default Profile;
  every non-default profile is read-only and yields the fixed
  `vscode_non_default_profiles_not_modified` warning.
- Copilot CLI persistent user-environment apply is Windows-only. macOS/Linux
  retain detect/plan but apply returns `unsupported_target`; shell profiles are
  never a mutation target.

## Context and verified producer contracts

The existing Config CLI is the canonical producer of collection-profile
settings. Existing Windows scripts use current-user environment APIs and the
Release ZIP publishes the Local Monitor app plus PowerShell operations. Issue
#61 declares the `github-copilot-vscode` and `github-copilot-cli` source
capabilities; it has no App/SDK manifest.

Official upstream facts rechecked on 2026-07-14:

- VS Code 1.119 introduced the Copilot OTel settings; guided apply uses Stable
  and Insiders 1.128+ because that release first enforces managed-channel
  precedence. Current VS Code documentation defines policy > environment > user
  setting > default precedence and documents the
  `github.copilot.chat.otel.*` user settings for both channels.
- VS Code user settings are channel- and profile-scoped. Official paths separate
  Stable `Code` from Insiders `Code - Insiders`, and non-default settings live
  below `User/profiles/<profile-id>`; the product decision is to mutate Default
  Profile only.
- Copilot managed settings arrive through native MDM, signed-in-account server
  policy, or a well-known per-OS `managed-settings.json`. VS Code 1.128+ selects
  native > server > file as one authoritative channel without merge. The native
  Copilot locations are only Windows `GitHubCopilot` and macOS
  `com.github.copilot`; Linux has no native Copilot channel. VS Code enterprise
  `CopilotOtel*` policies under `Software\Policies\Microsoft\VSCode`, macOS
  configuration profiles, and Linux `/etc/vscode/policy.json` are an explicitly
  separate policy system. An external CLI can read local sources but cannot
  prove the signed-in-account result.
- Copilot CLI 1.0.4 introduced OpenTelemetry instrumentation. Current GitHub
  documentation defines `COPILOT_OTEL_ENABLED`,
  `COPILOT_OTEL_EXPORTER_TYPE`, `OTEL_EXPORTER_OTLP_ENDPOINT`, and protocol
  variables, including `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` as a trace-specific
  override of the global protocol.
- The existing runtime uses .NET `LocalApplicationData`. Its macOS mapping is
  `$HOME/Library/Application Support`; on Linux it is an absolute
  `XDG_DATA_HOME` or `$HOME/.local/share` fallback.
- GitHub Copilot SDK exposes a caller-provided `TelemetryConfig`, including the
  .NET `OtlpEndpoint` and `OtlpProtocol` fields.

Primary references:

- https://code.visualstudio.com/updates/v1_119
- https://code.visualstudio.com/docs/agents/guides/monitoring-agents
- https://code.visualstudio.com/docs/enterprise/ai-settings
- https://code.visualstudio.com/docs/enterprise/policies
- https://code.visualstudio.com/docs/configure/settings
- https://code.visualstudio.com/docs/configure/profiles
- https://code.visualstudio.com/docs/configure/command-line
- https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference
- https://docs.github.com/en/copilot/how-tos/copilot-sdk/observability/opentelemetry
- https://opentelemetry.io/docs/specs/otel/protocol/exporter/
- https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolder
- https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/Environment.GetFolderPathCore.Unix.cs.html

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
- `Storage`: private immutable plans/backups/journals and repository-safe ledger
  v1, including the immutable plan-time status-target projection snapshot.
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
  -> coordinator stores private immutable plan and safe planned ledger row,
     including the immutable status-target projection snapshot
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

The source boundary stays single-producer: adapters return the internal
`SetupChangePlan`/`SetupChangeRecord` model, the coordinator returns
`SetupCommandResult`, the CLI serializes that result, and PowerShell forwards
it unchanged. Apply loads the persisted private plan; it never asks an adapter
to recreate a public DTO. If that plan's adapter is no longer registered,
`apply` returns `unsupported_adapter` and leaves the plan/ledger byte-for-byte
unchanged with no platform probe or mutation artifact.

## Ownership and private data

The ledger records fixed IDs/labels, timestamps, state/outcome codes, hashes,
opaque backup references, restart requirements, rollback status, and the
immutable repository-safe target fields needed by public status. The snapshot
retains plan-time detection/version/source/endpoint/manifest/guidance metadata
and redacted member states; it never records an absolute path or raw value.
New plans require exact equality with the current canonical manifest. Historical
ledger manifests instead use a strict v1 schema/closed-code/safety/
target-surface/cross-field validator, so status preserves a valid plan-time fact
without requiring equality to a later embedded manifest. Status does not rerun
adapter detection. It uses private artifacts and the current target only to
freshly derive reference/current/rollback facts. A successful applied record
grants the adapter authority only over its listed changed member settings at
that physical target's post-apply hash; an all-no-op physical target grants no
authority or backup but retains its base-state rollback preflight guard.

Private plans hold target locations and validated desired values, not previous
values. Flushed apply-time backups hold the exact previous state required for
restore. Journals record physical-target progress. All are local runtime data,
not repository-safe output. Backups are retained until a future explicitly
designed cleanup feature.

The private root exists on every planning platform: `%LOCALAPPDATA%` on
Windows, `$HOME/Library/Application Support` on macOS, or absolute
`XDG_DATA_HOME` with `$HOME/.local/share` fallback on Linux, then
`CopilotAgentObservability/LocalMonitor/setup/`. A trusted injected platform can
replace the base for tests/hosting; no setup CLI/environment override exists.

The complete ledger keeps its existing 1 MiB cap. Snapshot fields are strictly
bounded, including detected versions at 128 UTF-16 code units, and an executable
boundary proves the largest accepted single change set fits under that cap.
Snapshot storage adds no second cap. Indefinite retention therefore has finite
history capacity; an append that would exceed 1 MiB fails closed without
replacing the durable ledger, and no automatic pruning is added.

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
endpoint ownership before creating backups or mutation artifacts. Issue #67
extends this gate to planning-OS support, VS Code Default Profile extension
presence, and exact logical member semantics. Any failure is no-artifact and
no-write.

All public target results include bounded `detected` and sanitized
`detected_version`; App/SDK uses them to report package presence/version without
exposing a path. Status adds per-target verification state. The change-set state
is diverged before stale before unavailable before current, considering writable targets only,
and rollback availability is equivalent to the rollback command's fresh
change-set preflight. Changed targets form the ownership/backup quorum; unowned
all-no-op targets need no backup but must still equal their base state, so their
drift makes the whole rollback unavailable. Guidance targets are not part of
either calculation. Each target carries a lifecycle-relative
base/desired/previous/none reference; partial targets use aggregate member
outcomes. All-desired and all-previous targets keep that reference; mixed
desired/previous or preserved third-party state is explicitly diverged with no
single reference, while classification failure is unavailable. Every partial
change set has rollback unavailable.

## GitHub Copilot target behavior

### VS Code

Detect Stable `code` and Insiders `code-insiders`, require version 1.128+ and
`GitHub.copilot-chat` for every installed requested channel, and plan physical
targets in Stable-then-Insiders order. Write only each channel's Default Profile
enabled/exporterType/endpoint settings. Detect non-default profile directories
only to emit `vscode_non_default_profiles_not_modified`; never open or mutate
their settings. Keep captureContent unchanged unless the sensitive option is
explicit.

Extension detection uses exactly each channel executable with
`--list-extensions --show-versions` and no `--profile`. Official VS Code CLI
behavior can create a missing named profile, so setup never passes a profile
name, never names `Default`, and never creates/selects a non-default profile.
Stable paths come from the settings documentation; the official Profiles page
qualifies the Insiders mapping by replacing intermediate folder `Code` with
`Code - Insiders`.

Managed sources are read-only. Select the entire Copilot native, server, or
file object in that order; never merge those channels. Resolve VS Code
enterprise `CopilotOtel*` policies independently rather than treating them as
Copilot native. A differing observed constraint from either system blocks the
plan, while an equal value is managed/no-write. Enterprise policy presence
never suppresses Copilot server/file evaluation. Copilot native absence leaves
the unobservable signed-in server tier unresolved, yielding
`managed_policy_unverified` and Policy Diagnostics guidance.

### Terminal CLI

Detect `copilot version`, require 1.0.4+, compare current-process and
current-user environments on Windows, and write only
`COPILOT_OTEL_ENABLED`, `COPILOT_OTEL_EXPORTER_TYPE`,
`OTEL_EXPORTER_OTLP_ENDPOINT`, and `OTEL_EXPORTER_OTLP_PROTOCOL`. The optional
content member is exactly
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`. Do not set global client
identity, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`,
`OTEL_EXPORTER_OTLP_HEADERS`, `COPILOT_OTEL_SOURCE_NAME`, or credentials.
Managed state is environment-only/unverified for this target.

Detect but never write `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL`. Preserve
`http/protobuf` with `cli_trace_protocol_override_not_modified`; any other
value blocks plan with `environment_override_conflict` and
`review_cli_trace_protocol_override` because it overrides the global protocol
for traces.

Windows apply uses only the #66 current-user API. macOS/Linux execute the same
detection/redacted plan path, persist the planning OS, and return
`unsupported_target` at apply with no shell-profile, backup, journal, ledger
transition, notification, or target write. Restart instructions apply to
existing Windows terminals and sessions.

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
Recognition sends a no-redirect `GET /health/live` under one 500 ms total
budget. A trustworthy `Content-Length` may reject oversize immediately;
otherwise the probe reads at most 4096 payload bytes plus one sentinel byte.
It accepts only HTTP 200 with a JSON object
containing exactly string `status=live`. Refused/no-listener is
`monitor_not_running`; connect/read/total timeout, redirect, non-200, oversize,
malformed/non-object, extra-property, or other JSON is
`port_owned_by_foreign_process`. Static success never means that traces or
metrics arrived.

## Testing strategy

Every production behavior begins with a failing test. Required early executable
contracts are:

- serializer round-trip from production DTOs through CLI and wrapper fixtures;
- strict ledger v1 status-snapshot close/reopen, cross-field validation, and
  missing-snapshot/unknown-version rejection, including all affected ledger
  constructor fixtures and the largest legal snapshot below 1 MiB;
- exact-current manifest validation for new plans and separate strict
  historical-manifest validation for ledger-origin status;
- guidance sample omission from storage/status JSON and fixed in-memory
  rehydration for DTO validation;
- terminal/non-terminal missing-plan behavior, fresh backup checks, adapter-not-
  rerun proof, and paired status/rollback preflight-equivalence cases;
- atomic file and ownership-preservation tests;
- barrier-controlled stale/lock/concurrent-edit cases without sleeps;
- failure at every mutation and compensation boundary;
- negative secret/header/value/path markers across all safe outputs;
- real Issue #61 manifest consumption by the VS Code/CLI adapter;
- Stable/Insiders Default Profile path and dual-channel order, fixed non-default
  warning, and proof that profile files outside Default are never opened;
- official three-OS managed source locations, Copilot native/server/file whole-
  channel selection, independent VS Code enterprise-policy conflicts,
  policy/environment/user/default precedence, and CLI always-unverified
  environment-only behavior;
- closed warning/next-action allowlists and trace-specific protocol override
  matching/conflicting cases without expanding the write allowlist;
- exact endpoint 500 ms/no-redirect/4096-byte-plus-sentinel or
  `Content-Length`/JSON recognition matrix;
- apply-time endpoint/policy/version/extension/member revalidation with no
  artifacts, plus persisted-adapter removal and macOS/Linux CLI apply command/
  code combinations;
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

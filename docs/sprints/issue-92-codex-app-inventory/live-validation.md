# Issue #92 Codex App discovery evidence

This is repository-safe discovery evidence. It contains no raw OTLP payload,
trace/span/native identifier value, resource-attribute value, prompt/response,
tool content, credential, PII, private App state, installation path, or machine
path.

## Evidence boundary

| Field | Record |
| --- | --- |
| Date / OS | 2026-07-24 / Windows x64 |
| Candidate base | `07dc219c4f5c5ef56e7810a23c6466a52e90aa97` |
| Desktop package | `OpenAI.Codex` `26.715.10079.0`, public package metadata only |
| Executable producer | Codex CLI/app-server `0.145.0` |
| Capture policy | content-disabled; content-enabled capture not authorized |
| Configuration | user config loaded by Codex; values not inspected; no write; listed fields overridden per command; other global influence not excluded |
| Receiver | disposable loopback JSON OTLP receiver with readiness gate |
| Stored evidence | counts, key names, identifier shapes, and relationship states only |
| Raw probe cleanup | initial six probe directories removed; review then found two residual system-temp directories (one empty; one containing 11 SQLite/WAL/SHM files totaling 20,002,576 bytes); contents were not read or used; the exact two targets were resolved and validated under system Temp, then recursively deleted; final matching-directory count is zero |

The Desktop-bundled producer binary was present, but direct execution from the
terminal was denied by WindowsApps access control through both direct
PowerShell invocation and `ProcessStartInfo`. The successful standalone
app-server probe is not treated as a Desktop-owned retry or as proof of Desktop
support. The retry locally resolved the package InstallLocation and
package-relative producer path. That absolute path was neither retained nor
committed, and no private App database, cache, history, or content was read.

The accepted process diagnostic used only process ID, parent ID, and executable
path projections in memory. It observed a package-root `codex.exe` process and a
package-root parent process, emitted fixed booleans only, and returned
`app_server_identity_observed=false`, `desktop_otel_execution_observed=false`,
and `merge_authority=false`. It did not read command lines and retained no ID,
path, or hash value. An earlier ad-hoc diagnostic did read process command lines
in memory; safety review invalidated and excluded that attempt entirely. It
emitted only sanitized booleans, and no command-line value was retained or
committed.

## Probe template and replayability

Classification: `non_replayable_attestation`.

The verbatim command, exact ephemeral PowerShell receiver/parser harness, and
raw OTLP output were not retained. The repository-safe logical command template
was:

```text
codex -c <otel.log_user_prompt> -c <otel.environment> -c <otel.exporter> -c <otel.trace_exporter> -c <otel.metrics_exporter> app-server --listen stdio
```

The thread-start run additionally overrode `history.persistence` and
`sqlite_home`. Values and shell quoting are intentionally not reconstructed as
exact evidence. The public protocol method sequence was `initialize`,
`initialized`, then `thread/start` for the second profile; no `turn/start` or
prompt was sent.

The exact overridden key names were:

- `otel.log_user_prompt`
- `otel.environment`
- `otel.exporter`
- `otel.trace_exporter`
- `otel.metrics_exporter`
- thread-start only: `history.persistence`
- thread-start only: `sqlite_home`

Codex loaded user configuration before applying these overrides. Values in that
configuration were not inspected and the file was not written, but influence
from unoverridden global fields was not excluded. In particular, retained
resource-attribute key names cannot be attributed solely to the standalone
producer or treated as a stable Codex App contract.

The ephemeral derivation procedure accepted loopback HTTP requests, recorded
method/path/content type and raw body privately, parsed OTLP JSON, flattened
`resourceSpans[].scopeSpans[].spans[]`, counted spans and distinct identifier
shapes without retaining values, enumerated span/resource key names, and
compared non-empty parent identifiers with the exported span-ID set. Six raw
directories were removed after sanitized extraction. A later review found and
removed two additional residual system-temp directories as recorded above.
Because neither harness
nor raw input remains, committed counts, key names, identifier shapes, and
parent-membership results cannot be independently regenerated.

## Official-source inventory

The official Codex configuration reference and public configuration schema
document independent log, trace, and metric exporters, per-command
configuration overrides, and the prompt logging gate. The public app-server
protocol documents native thread and turn identifiers. Documentation provides
candidate semantics only; a documented field or signal is not promoted without
approved observation.

Prompt logging disabled is not equivalent to content-free logs. Officially
documented and version-pinned tool-result log events include arguments, output,
and error text that may contain content or paths, so no log request or value is
repository-safe merely because `log_user_prompt` is false.

Primary references:

- [Codex configuration reference](https://developers.openai.com/codex/config-reference/)
- [Codex Advanced Configuration telemetry inventory](https://developers.openai.com/codex/config-advanced/#observability-and-telemetry)
- [Codex app-server documentation](https://developers.openai.com/codex/app-server/)
- [Codex app-server public protocol](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [Codex configuration schema](https://github.com/openai/codex/blob/main/codex-rs/core/config.schema.json)
- [Codex 0.145.0 tool dispatch](https://github.com/openai/codex/blob/rust-v0.145.0/codex-rs/core/src/tools/registry.rs)
- [Codex 0.145.0 tool-result telemetry](https://github.com/openai/codex/blob/rust-v0.145.0/codex-rs/otel/src/events/session_telemetry.rs)

## Live observations

### Initialize-only profile

The standalone app-server was initialized through its public stdio protocol,
with no thread or prompt. The executed trace profile observed exactly one JSON
`/v1/traces` request. The log profile was not executed, so its request count and
content type are `null` / not observed and establish no log absence or format
claim. The trace batch contained six spans across five trace IDs with scope
`codex-app-server`; observed span names were `auth` and `initialize`.

Identifier values were discarded after recording the source shapes: trace IDs
were 32-character hexadecimal values and span IDs were 16-character
hexadecimal values. Four spans were roots. Two source parent references pointed
outside the exported batch and remain unresolved.

Observed resource attribute key names:

- `department`
- `env`
- `experiment.id`
- `service.name`
- `service.version`
- `team.id`
- `telemetry.sdk.language`
- `telemetry.sdk.name`
- `telemetry.sdk.version`

Observed span attribute key names:

- `app_server.api_version`
- `app_server.client_name`
- `app_server.client_version`
- `app_server.connection_id`
- `busy_ns`
- `code.file.path`
- `code.line.number`
- `code.module.name`
- `idle_ns`
- `rpc.method`
- `rpc.request_id`
- `rpc.system`
- `rpc.transport`
- `target`
- `thread.id`
- `thread.name`

The attribute names are retained; their values are not. In particular, the
`0.145.0` version is sourced from the public CLI version command, not from the
discarded `service.version` value.

### Thread-start profile

A second content-disabled standalone run initialized the app-server and invoked
`thread/start` without starting a turn or sending a prompt. It exited
successfully and produced exactly one JSON `/v1/traces` request containing
seven spans. The public protocol returned a native thread ID, but the
`thread/start` OTel span did not carry that native ID. Its observed app-server
and RPC attribute keys therefore do not bind the protocol thread to an OTel
trace. Generic tracing-library `thread.id` remains rejected as native Codex
identity.

No content-enabled delta, turn, tool/file operation, shell operation, error,
concurrent window/thread, restart/resume, monitor reconnect, metric, or
complete log profile was executed.

Sanitized structural fixtures:

- [Initialize fixture](../../specifications/contracts/source-capabilities/v1/codex-app/fixtures/content-disabled-initialize.sanitized.json)
- [Thread-start fixture](../../specifications/contracts/source-capabilities/v1/codex-app/fixtures/content-disabled-thread-start.sanitized.json)

## Excluded attempts and failure history

- A receiver-not-ready attempt and a debug-driver attempt that produced no
  request are not used as absence evidence.
- An early standard-input close that produced no capture is not used as product
  evidence.
- A strict-configuration attempt encountered an existing unknown global key.
  This confirms Codex loaded the global layer before overrides. Its values were
  not inspected, it was not written, and that attempt is excluded.
- The Desktop-bundled producer retry was blocked by WindowsApps access control.
  It is retained as a blocker and is not replaced by the standalone run.
- An ad-hoc process-tree diagnostic read process command lines in memory. It was
  invalidated and excluded after safety review because command lines may contain
  user content. No command-line value was retained or committed. The accepted
  replacement projects only process ID, parent ID, and executable path and
  cannot identify app-server role.
- The initial statement that six deleted probe directories left no residue was
  premature. Review found two further matching system-temp directories: one
  empty and one containing 11 SQLite/WAL/SHM files totaling 20,002,576 bytes.
  Their contents were not read or used. The exact two targets were resolved and
  validated under system Temp, then recursively deleted; the final post-check
  found zero matching directories. No absolute path was retained. A
  pre-deletion process-reference check read process command lines in memory and
  was invalidated and excluded by the same safety ruling; no value was retained.

## Decision and exact correlation

Decision: `NO-GO` for Codex App Desktop production integration.

The authoritative relationship table is
[Codex App exact-correlation inventory](../../specifications/contracts/source-capabilities/v1/codex-app/exact-correlation.md).
Within the standalone attestation, OTel trace-to-span identity is exact. Source
parentage is preserved but partial when a referenced parent is outside the
batch. Protocol native thread identity was observed unbound from OTel. Turn
correlation is unverified because no turn ran. The accepted process diagnostic
proves only a non-authoritative package-root `codex.exe` child/parent relation;
it does not identify app-server role. Desktop app-server/process/Session/window
ownership, concurrency, and restart/resume remain unverified.

The v1 manifest cannot scope availability to the standalone producer. Only
source version detection is promoted for `codex-app`; trace and every
Desktop-specific capability remain `unknown`.

## Blocked and unverified profiles

The machine-readable blocker, retry condition, and unverified-capability text
is pinned in `discovery-inventory.json`.

| Profile | Classification | Severity | Retry condition |
| --- | --- | --- | --- |
| Desktop-bundled producer execution | `blocked_external` | high | Launch through an authorized Desktop-owned path and capture content-disabled OTLP on a disposable loopback receiver. |
| Desktop ownership and Session/window | `unverified` | high | Execute an authorized Desktop-owned run and observe an explicit source-owned relationship without private-state reads. |
| native thread → OTel | `unverified` | high | Observe the same native thread ID or explicit source link in protocol and OTel evidence. |
| native turn → OTel | `unverified` | high | Run a separately approved turn with log export disabled, or a separately authorized proven non-content mechanism, and observe the same native turn ID or explicit source link in protocol and OTel evidence. |
| concurrent windows/threads | `unverified` | medium | Exercise concurrent approved surfaces and prove isolation with source-native exact identity. |
| restart/reconnect/resume | `unverified` | medium | Exercise each lifecycle case and observe explicit continuity or an honest unbound result. |
| complete trace/log/metric inventory | `blocked_external` | high | Obtain content authorization or an explicitly non-content log mechanism, then inventory all three exporters without retaining values. |
| semantic field inventory | `unverified` | high | Exercise approved cases with log export disabled, or with a separately authorized and proven non-content mechanism, and inventory key names and absence states. |
| existing generated log-export profiles | `blocked_external` | high | Define and test safe defaults or an explicitly non-content mechanism in a separately approved prerequisite specification; separately authorize any content-bearing profile. |
| content-enabled delta | `blocked_external` | high | Obtain separate authorization, keep raw output private, and publish only leak-scanned structural deltas. |

## Issue #93 blocked gate

Issue #93 remains blocked. Standalone CLI/app-server attestation does not
authorize a `codex-app` production adapter. Production adapter, Setup, Doctor,
UI, trace-manifest promotion, and future-registry activation must not start
from this attestation. A separately approved discovery retry and prerequisite
configuration specification must first establish Desktop-owned execution, a
retained repository-safe replay harness, exact configuration/detection, safe
log policy, source identity and parentage, and
exact-or-explicitly-unbound native correlation.

Existing Codex App log-export / Langfuse / Collector samples enable log export.
Pinned `rust-v0.145.0` tool-result events include arguments, output, and error
text that may contain content or paths even with prompt logging disabled. Those
log-export profiles are not established as repository-safe and are a
high-severity blocker. Issue #92 does not alter production generators.

## Automated validation

The initial test-first run compiled successfully and failed all five new
Issue #92 contract tests because the inventory, fixtures, and canonical text
did not yet exist. This is the expected RED result.

Final review found that the first sanitized fixtures represented unexecuted log
and turn profiles with concrete zero/count/media-type fields. A strengthened
fixture-state contract then failed 1 of 8 Issue #92 tests, as expected. The
fixtures were corrected to keep execution state independent from observation:
unexecuted profiles now use explicit execution flags, `not_observed`, and null
count/media-type/native-turn observations. The repaired focused Issue #92 gate
passed 8/8, and the combined Issue #92/source-capability gate passed 27/27.

Safety review also found three stale summary surfaces that still described
Codex App as a supported optional source even though their detailed contracts
already recorded the Desktop production `NO-GO`. Requirements, architecture,
and telemetry-ingestion summaries were corrected to classify it as a
planned/blocked candidate governed by D072. This documentation correction
invalidates the earlier repository-wide run and requires the coordinator's
post-freeze rerun recorded below.

Final Issue #92 artifact gates and superseded repository-wide results:

| Command | Result |
| --- | --- |
| Issue #92 focused contract tests | final candidate: 8/8 passed, 0 failed/skipped |
| Source capability contract tests | final candidate: 19/19 passed, 0 failed/skipped |
| combined focused gate | final candidate: 27/27 passed, 0 failed/skipped |
| process diagnostic execution contract | included in Issue #92 8/8; exact output shape parsed, sensitive emission/read flags false |
| artifact checksum verification | final candidate: 6/6 repository-safe artifacts matched SHA-256 manifest; manifest does not hash itself |
| Issue #91 scanner self-test | final candidate: 118 transformation cases and 5 negative cases passed |
| repository-safe artifact scans | final candidate: contract 4 files / 472 variants and sprint+script+manifest 5 files / 590 variants; 0 matches |
| Claude skill mirror check | pre-review pass: 5 shared skills; final candidate rerun delegated to coordinator |
| solution build | pre-review pass: 0 warnings / 0 errors; superseded by final corrections; final candidate rerun delegated to coordinator |
| Playwright Chromium bootstrap | pre-review exit 0; superseded by final corrections; final candidate rerun delegated to coordinator |
| full solution tests | pre-review 8,492/8,492 passed, 0 failed/skipped (`20 + 451 + 266 + 4,604 + 3,151`); superseded by final corrections; final candidate rerun delegated to coordinator |
| repository-safe self-review | final candidate: `git diff --check`, JSON parse, stale-claim scan, zero-residue check, and artifact scanner passed |

The pre-review build, Playwright bootstrap, and 8,492-test full run are retained
as historical evidence but are not claimed as final-candidate validation. The
coordinator owns the final exact repository-wide validation after this handoff.

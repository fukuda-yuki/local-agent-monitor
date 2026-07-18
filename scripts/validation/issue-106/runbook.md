# Issue #106 Claude Code live-validation preparation runbook

This runbook prepares, but does not execute or certify, the four Claude Code
live-validation cases owned by Issue #106. A final run is allowed only against
the immutable candidate SHA supplied by Issue #104. Dry-run output from the
kickoff main revision is non-authoritative and is never final evidence.

## Scope and safety boundary

Use a disposable Local Monitor process, a disposable SQLite database, a
throwaway Claude project directory, and a non-default loopback port. Do not
reuse the operator's normal Monitor state, database, Hook settings, or running
process. The repository-local lifecycle wrapper's state file is under the
operator's normal Local Monitor runtime root; when that root is in use, start
the disposable process directly with the exact Local Monitor arguments below
so the operator instance cannot be overwritten.

The only live producer command in this matrix is `claude -p`. Do not use real
prompts, credentials, PII, repository content, tool payloads, or transcript
paths. The content-enabled case requires a distinct explicit operator
authorization, separate from authorization to run tools or to use Claude
Code. `preflight.ps1` fails closed unless `-OperatorAuthorized` is supplied.

## Run variables and exact commands

Set these values in the operator's session; do not commit the expanded values:

```powershell
$ValidationRoot = Join-Path $PWD 'tmp\issue-106-validation'
$StorageRoot = Join-Path $ValidationRoot 'storage'
$DatabasePath = Join-Path $StorageRoot 'monitor.db'
$HookProject = Join-Path $ValidationRoot 'hook-project'
$MonitorProject = Join-Path $PWD 'src\CopilotAgentObservability.LocalMonitor'
$Port = 4323
$MonitorUrl = "http://127.0.0.1:$Port"
$OtlpTraceEndpoint = "$MonitorUrl/v1/traces"
$HookEndpoint = "$MonitorUrl/api/session-ingest/v1/events"
```

Run the fail-closed preflight before changing Claude settings:

```powershell
pwsh -NoProfile -File scripts/validation/issue-106/preflight.ps1 `
  -Port $Port -DisposableRoot $ValidationRoot -OperatorAuthorized
```

The Local Monitor source entry point accepts `--db`, `--url`, and
`--sanitized-only`; `--port` is also accepted by the entry point, but this
runbook pins the explicit loopback URL. The exact disposable start commands
are:

```powershell
dotnet run --project $MonitorProject -- --db $DatabasePath --url $MonitorUrl
dotnet run --project $MonitorProject -- --db $DatabasePath --url $MonitorUrl --sanitized-only
```

Start the command as a disposable process, retaining its process ID in the
local run record. Confirm `GET $MonitorUrl/health/live` and
`GET $MonitorUrl/health/ready` before starting Claude. The equivalent existing
wrapper command is `scripts/local-monitor/start.ps1 -Mode DotnetRun -Url
$MonitorUrl -DbPath $DatabasePath`, with `-SanitizedOnly` for the second form;
do not use that wrapper when its normal user-level state file could collide
with the operator's own Monitor.

The live Claude producer contract is:

| Field | Pinned value |
| --- | --- |
| Endpoint | `$OtlpTraceEndpoint` (loopback, full `/v1/traces` endpoint) |
| Protocol | Live: OTLP HTTP/protobuf (`http/protobuf`, `Content-Type: application/x-protobuf`) using the per-signal trace variables |
| Signal | Live: traces only; the Monitor exposes `/v1/traces` and rejects unsupported signal routes |
| Claude export settings | `CLAUDE_CODE_ENABLE_TELEMETRY=1`, `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1`, `OTEL_TRACES_EXPORTER=otlp`, `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf`, `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=$OtlpTraceEndpoint` |

Set the variables in the same process environment that launches Claude. Keep
the prior values in memory and restore or remove every variable during cleanup.
The content gates are independent of the telemetry enablement settings:

```powershell
$env:CLAUDE_CODE_ENABLE_TELEMETRY = '1'
$env:CLAUDE_CODE_ENHANCED_TELEMETRY_BETA = '1'
$env:OTEL_TRACES_EXPORTER = 'otlp'
$env:OTEL_EXPORTER_OTLP_TRACES_PROTOCOL = 'http/protobuf'
$env:OTEL_EXPORTER_OTLP_TRACES_ENDPOINT = $OtlpTraceEndpoint
```

The checked-in JSON files under `scripts/validation/issue-106/fixtures/` have a
separate fixture-only direct-receiver dry-run path. It verifies the Monitor's
synthetic JSON compatibility and is not the live Claude producer contract:

```powershell
$FixturePath = Join-Path $PWD 'scripts/validation/issue-106/fixtures/otel-interaction-key-absent.json'
$FixtureBody = (Get-Content -LiteralPath $FixturePath -Raw).Replace('MARKER-PLACEHOLDER', $Marker)
Invoke-WebRequest -Method Post -Uri $OtlpTraceEndpoint -ContentType 'application/json' -Body $FixtureBody
```

Record this dry run as `fixture-only: OTLP JSON direct POST` with
`Content-Type: application/json`; do not record it as evidence that the live
Claude producer emitted JSON.

## Synthetic-marker contract

At the beginning of every producer run, generate a fresh marker:

```powershell
pwsh -NoProfile -File scripts/validation/issue-106/new-marker.ps1
```

Use a new harmless `SYNTH-<guid>` value for each run. Pass the runtime value
to `claude -p`, fixture replacement, and `scan-leaks.ps1`; never hard-code it
in a script, fixture, report, or command committed to this worktree. For a
fixture dry run, replace every literal `MARKER-PLACEHOLDER` in a copied fixture
under the disposable root only. Committed evidence may contain only the
match/no-match result and `sha256:<first-12-hex-chars>` reference emitted by
the scanner. It must never contain the raw marker or a reversible encoding.

After each case, run the scanner before changing to the next case:

```powershell
pwsh -NoProfile -File scripts/validation/issue-106/scan-leaks.ps1 `
  -Marker $Marker `
  -RepositoryRoot $PWD `
  -EvidencePath $EvidenceDirectory `
  -LogDirectory $ApplicationLogDirectory `
  -SanitizedOutputPath $SavedSanitizedOutput
```

The scanner must report no marker or generic leak match in repository output or
evidence files. It also checks any supplied logs and saved sanitized API/UI
responses for credential-shaped strings, authorization headers, and absolute
user-profile paths. Do not copy raw route output into evidence.

## Pinned environment and record fields

Every case record must fill all of these fields without storing raw content:

| Field | Required record |
| --- | --- |
| Candidate | Immutable Issue #104 `FINAL_CANDIDATE_SHA`; never a moving branch name |
| Source version | Exact `claude --version` output, plus whether the run was Windows native, WSL2, or another supported boundary |
| Monitor revision | `git rev-parse HEAD` from the clean candidate worktree |
| OS/execution boundary | OS, native/WSL boundary, disposable project root, disposable process/database boundary, and loopback-only endpoint |
| Endpoint/protocol/signal | Live producer: loopback `$OtlpTraceEndpoint`, OTLP HTTP/protobuf per-signal trace (`Content-Type: application/x-protobuf`), and readiness result. Fixture dry run: the same `/v1/traces` endpoint with a direct HTTP POST and `Content-Type: application/json`; this is fixture-only, not the live producer contract |
| Hook state | Disabled or configured; for configured runs record `hook-forward --source claude-code`, timeout, trusted source-version or schema-fingerprint selector, and Hook ingest endpoint |
| Content gate state | `OTEL_LOG_USER_PROMPTS=1` for the enabled run; unset or `0` for the disabled observation; all other content gates explicitly recorded |
| Sanitized-only | `true` or `false`, including the exact start command form |
| Restart timing | UTC timestamps or ordered markers for pre-session start, active-session stop, restart, readiness, and resume |
| Commands and exits | Exact producer, start/stop/restart, API/UI probe, and leak-scan commands with exit statuses |

Never use `trace_id`, `cwd`, process identity, prompt text, timestamp proximity,
manual UI adjacency, or a generic diagnostic label as identity evidence.

## Case 1 — content state and gate-disabled key observation

Run with a fresh disposable database and `--sanitized-only` false. Ensure Hook
forwarding is disabled for this case so the content-state observation is
OTel-specific.

### 1A. Content-enabled `claude -p`

After distinct operator authorization and a fresh marker, set
`OTEL_LOG_USER_PROMPTS=1` and run:

```powershell
$env:OTEL_LOG_USER_PROMPTS = '1'
claude -p $Marker
$caseExit = $LASTEXITCODE
```

Expected observations:

- `$caseExit` is `0` and a `claude_code.interaction` span is received at
  `/v1/traces`.
- The raw OTLP span has a `user_prompt` attribute key containing the runtime
  marker. Record only `key_present` and marker `MATCH`; do not record its value.
- The sanitized trace API/list/flow/waterfall surfaces contain no marker. In
  raw-default mode the raw route may contain the marker only during the local
  operator check; it is not copied into evidence.
- The derived `content_state` is `available` under the #107 contract. If the
  source is not promoted or the state is still `unsupported`, classify the
  case as blocked with the exact candidate defect, never as passed.

### 1B. Gate-disabled `claude -p` observation for #110

Start from a clean disposable database and unset the gate; repeat with `0` if
the operator needs to distinguish the two producer settings:

```powershell
Remove-Item Env:OTEL_LOG_USER_PROMPTS -ErrorAction SilentlyContinue
# Or, in a separate clean run: $env:OTEL_LOG_USER_PROMPTS = '0'
claude -p $Marker
$caseExit = $LASTEXITCODE
```

Inspect only whether the `user_prompt` attribute key exists on the real
`claude_code.interaction` span. Record exactly one of `key_absent`,
`key_present_empty`, or `key_present_nonempty`; never store the value. Expected
contract behavior is `not_captured` when the gate is disabled or the key is
absent. If the real producer emits the key with an empty value, record the
observation as the #110 open question: a presence-only resolver may derive
`available`, so this is not a passed disabled-gate result until the candidate
disposition is explicitly recorded. A nonempty value while disabled is an
unexpected producer observation and is blocked pending candidate/producer
disposition.

## Case 2 — Hook plus OTel exact/native-session binding

Use a fresh database and a throwaway project directory. Configure only the
project's `.claude/settings.json`. The command recorded in each Claude Hook
entry must invoke the built Local Monitor entry point with the exact forwarder
arguments:

```text
<monitor-executable> hook-forward --endpoint <loopback>/api/session-ingest/v1/events --timeout-ms 250 --source claude-code --source-version <claude-version>
```

The Hook input is one producer JSON object on stdin; the forwarder emits the
normalized v1 envelope to the loopback endpoint. Configure the mapper-backed
`SessionStart`, `UserPromptSubmit`, and `Stop` events in the throwaway project,
then run `claude -p $Marker` with OTel enabled. The Hook and OTel paths must be
active in the same run.

### Positive exact-evidence check

The only positive binding assertion is:

1. the Hook envelope resolves to exactly one native session;
2. one OTel span has one unambiguous `session.id`; and
3. the UTF-8 bytes of those two native session IDs are identical.

Verify the projected result is `binding_state=exact_linked` and retains the
actual adapter/provenance labels. Do not record either raw ID. Record only
`native_session_id_byte_equal=true`, uniqueness, and the resulting sanitized
state. A common trace ID, same cwd, same prompt, matching time, process, model,
or Hook canonical hash cannot upgrade the result.

### Negative evidence check for #109

Run a separate clean negative case deliberately arranged so a Hook event and an
OTel trace share a trace ID, but the OTel record has no byte-equal native
`session.id` (or has a different native session ID). If the diagnostic path
labels the OTel observation `otel-exact`, leave that label present; the test is
specifically guarding against treating that generic label as exact-binding
evidence. Verify the sanitized projection remains `otel_only`/`hook_only` and
never `exact_linked`. If it reports `exact_linked` based only on the shared
trace ID or generic label, classify the candidate as blocked by #109 with the
exact observed state. Do not soften this negative case with cwd or time
proximity.

## Case 3 — restart/reconnect during an active or resumable session

Use a fresh database, raw-default mode, and a configured Hook if the native
session ID is required to resume. Record the monitor PID and prove its command
line contains the disposable database path and requested loopback port before
stopping it.

1. Start the disposable Monitor, prove live/readiness, and launch a harmless
   `claude -p $Marker` session.
2. Confirm the first trace is persisted, then stop only the proven disposable
   process while the session is still active or resumable.
3. Start the same exact Monitor command with the same database and port. Wait
   for readiness; do not delete or recreate the database.
4. Resume or continue the same native Claude session using the producer's
   native session mechanism, then wait for the next trace.
5. Compare sanitized trace identities/counts and ingestion health before and
   after the restart.

Expected outcome is no crash, no duplicate of the first trace, and no silent
loss of the resumed turn. The resumed turn may be a distinct trace; do not
infer a binding from that fact. Record the exact restart ordering and API exit
statuses. Any missing data, duplicate, or readiness failure is blocked with
the external cause and is not a pass.

## Case 4 — `--sanitized-only`

Use a fresh database and start with the exact sanitized command:

```powershell
dotnet run --project $MonitorProject -- --db $DatabasePath --url $MonitorUrl --sanitized-only
```

Ingest a fresh content-disabled Claude trace. Verify all of the following:

- `GET /traces/{rawRecordId}/raw` returns `404`;
- the trace-detail shell contains `data-raw-available="false"`, omits the raw
  section/full raw links, and still exposes sanitized detail;
- the list/dashboard uses a shortened TraceId instead of a raw prompt label;
- sanitized spans, flow, waterfall, and classification endpoints remain
  available; and
- no marker or raw/PII value occurs in sanitized API/UI output or logs.

All required observations must pass for this case. An unavailable raw route
alone is insufficient if sanitized views are missing.

## Final-candidate synchronization (106-D)

Before any final classification, receive the Issue #104 `FINAL_CANDIDATE_SHA`
and its candidate input manifest. Leave the candidate input record unfilled
until both are received. Then:

```powershell
$FinalCandidateSha = '<immutable SHA from #104>'
git merge-base --is-ancestor fac88b9 $FinalCandidateSha
git merge-base --is-ancestor bfd2ce9 $FinalCandidateSha
git merge-base --is-ancestor 196ca75 $FinalCandidateSha
```

All three commands must exit `0`: #107 is `fac88b9`, #108 is `bfd2ce9`, and
`196ca75` is the recorded merge baseline. Verify the supplied manifest against
the candidate before running any live case.

Create a clean validation worktree at exactly the SHA, using a path outside
the moving #104 in-progress worktree and never checking out the #104 branch:

```powershell
git worktree add --detach <clean-validation-worktree> $FinalCandidateSha
git -C <clean-validation-worktree> rev-parse HEAD
git -C <clean-validation-worktree> status --porcelain
```

The detached worktree must be clean and its `HEAD` must equal the immutable
candidate SHA. Initialize a new disposable database and fresh Hook project
there. If Issue #104 publishes a replacement candidate, discard all prior
final-run results, remove the prior disposable state, create a new clean
worktree, and rerun the complete matrix. Never relabel evidence from one
candidate as evidence for another.

## Cleanup and classification rules

After every case, restore or remove the Claude/OTel environment variables,
remove the throwaway Hook settings, stop only the disposable Monitor proven by
port plus command-line/database ownership, and run:

```powershell
pwsh -NoProfile -File scripts/validation/issue-106/cleanup.ps1 `
  -DisposableRoot $ValidationRoot -Port $Port
```

Cleanup is idempotent and must never target the normal Local Monitor runtime
root or an operator-owned process. Preserve only sanitized, repository-safe
match/no-match results and truncated marker references.

Dry-run evidence is never final evidence. A blocked, skipped, or unavailable
check is never classified as passed. Final classifications are valid only for
the complete matrix executed against the frozen candidate SHA; this preparation
worktree and its dry-run records make no claim that final validation has run.

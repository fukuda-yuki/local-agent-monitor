# Config CLI Interface

`src/CopilotAgentObservability.ConfigCli` exposes repository-local commands for configuration, ingestion, normalization, candidate generation, and dashboard generation.

## Configuration Commands

```text
config-cli list-collection-profiles
config-cli profile-vscode-env [--profile <collection-profile>] [--target <receiver|monitor>] [--endpoint <loopback-http-url>]
config-cli profile-copilot-cli-env [--profile <collection-profile>]
config-cli profile-codex-app-config [--profile <collection-profile>]
config-cli vscode-settings
config-cli langfuse-vscode-settings
config-cli collector-vscode-settings
config-cli vscode-env
config-cli langfuse-vscode-env
config-cli collector-vscode-env
config-cli vscode-file-settings <outfile>
config-cli copilot-cli-env
config-cli langfuse-copilot-cli-env
config-cli collector-copilot-cli-env
config-cli langfuse-codex-app-config
config-cli collector-codex-app-config
config-cli validate-resource-attributes <OTEL_RESOURCE_ATTRIBUTES>
config-cli setup plan --adapter github-copilot --target <vscode|cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture]
config-cli setup plan --adapter claude-code --target <cli|app-sdk|all> [--endpoint <loopback-http-url>] [--include-content-capture] [--allow-wsl2-routing]
config-cli setup apply --change-set <uuid-v7>
config-cli setup rollback --change-set <uuid-v7>
config-cli setup status [--adapter <id>]
config-cli doctor evaluate --input <file> [--json]
config-cli doctor verification start --database <file> --source-surface <value> [--source-adapter <value>] --expires-at <RFC3339> [--json]
config-cli doctor verification status --database <file> --verification-id <uuid-v7> [--json]
config-cli doctor verification complete --database <file> --verification-id <uuid-v7> --expected-revision <positive-int> --input <file> [--json]
config-cli doctor verification cancel --database <file> --verification-id <uuid-v7> --expected-revision <positive-int> [--json]
```

Configuration commands must emit placeholders instead of real credentials.

`--profile` uses the values defined in
[collection-profiles.md](collection-profiles.md).
When `--profile` is omitted, profile-aware commands read
`CAO_COLLECTION_PROFILE`.
If neither is set, profile-aware commands must fail with a deterministic error
instead of silently choosing a profile.

Existing explicit commands such as `langfuse-vscode-env` and
`collector-vscode-env` remain supported compatibility entry points.

The `setup` command family is the reversible configuration surface introduced
by Issues #66/#67/#68. It does not replace or change the output of existing manual
profile generators. `setup plan` creates an immutable private plan;
`setup apply` and `setup rollback` require its UUIDv7 change-set ID. Every setup
command emits exactly one `setup.v1` JSON result on stdout, and stderr contains
only the fixed result code for a non-success result. The process exit code is
the `setup.v1` code-to-exit mapping defined by the canonical setup interface;
the PowerShell wrapper must preserve the Config CLI stdout bytes and exit code.
The canonical ledger, DTO, error, transaction, policy, command-result mapping,
and GitHub Copilot / Claude Code target rules are defined in
[configuration-setup.md](configuration-setup.md).

Repository mode invokes the current
`src/CopilotAgentObservability.ConfigCli/CopilotAgentObservability.ConfigCli.csproj`
through `dotnet`. A packaged `scripts/setup.ps1` detects the sibling
`../app/config-cli/` release layout and invokes its self-contained
`CopilotAgentObservability.ConfigCli.exe` directly. The packaged command must
not require an installed .NET SDK or runtime, and for the same private runtime
state it must return the same `setup.v1` stdout bytes and exit code as repository
mode. This selection changes only executable discovery; argument forwarding,
stdout, stderr, and exit semantics stay identical.
Argument forwarding includes the exact `--allow-wsl2-routing` token without
interpretation. A Windows Release ZIP invocation does not thereby claim or
perform Windows-to-WSL settings mutation; Claude WSL2 setup remains
repository-run from inside the verified WSL2 process as defined by the
canonical setup interface.

The presence of the sibling `../app/config-cli/` directory commits the wrapper
to packaged mode. If the expected
`CopilotAgentObservability.ConfigCli.exe` is absent or is not a file, the
wrapper must not fall back to repository `dotnet`. Because no Config CLI result
producer is available, it returns no stdout DTO, writes exactly
`internal_error\n` to stderr, and exits `5`, without an absolute/user path or a
raw PowerShell exception. An executable-start failure after the file check has
the same fixed outcome. A Config CLI process that starts normally retains its
own stdout, stderr, and exit code, including non-success results.

For the `raw-local-receiver` profile, `profile-vscode-env` selects which local
raw target the generated VS Code environment points at:

- `--target receiver` (default): the Config CLI receiver, `http://127.0.0.1:4319`
  (unchanged behavior).
- `--target monitor`: the Local Ingestion Monitor, `http://127.0.0.1:4320`.
- `--endpoint <loopback-http-url>`: explicit override (must be loopback) for a
  non-default monitor / receiver port; overrides the `--target` default.

`--target` and `--endpoint` apply only to `raw-local-receiver`; combining them
with another profile fails with a deterministic error.

`list-collection-profiles` lists all product profile values. Sprint6
profile-aware output commands returned a deterministic error for
`raw-local-receiver`; Sprint7 replaces that reserved error with generated
configuration that points to the local receiver endpoint and does not emit
Langfuse credentials, Collector headers, or remote endpoints.

## Doctor Commands

The five `doctor` commands are the source-independent Issue #102 diagnostic
surface. They do not replace `setup`: setup proves static configuration only,
while Doctor evaluates explicit facts or carries one bounded real-source
verification window.

`doctor evaluate --input` reads one strict `DoctorFactSnapshot` JSON object with
source-neutral typed `DoctorObservation` entries. Doctor verification complete
reads one strict input object containing `fact_snapshot` and
`accepted_evidence_refs`; its snapshot observations must be empty. The complete
caller supplies opaque references only, and the store/service resolves existing
unexpired persisted candidates into trusted observations. Both files are
bounded to 65,536 bytes, read with a sentinel byte, and reject
unknown/duplicate properties, unsupported schema versions, and inconsistent
fact groups. The database path is input only and must never appear in output,
stderr, logs, evidence, or an error.

`source-surface` and optional `source-adapter` use the shared bounded token
grammar. Verification IDs are canonical lowercase UUIDv7, revision is a
positive integer, `expires-at` is canonical UTC RFC 3339 round-trip form, and
the requested window must be 1..30 minutes from the injected start time.
Option names, arity, and values are parsed strictly; no positional/alias
fallback or permissive unknown option is accepted.

With `--json`, stdout is exactly one canonical `doctor.v1` object. Without
`--json`, stdout is the bounded human projection from the same already-evaluated
result. Adapters must not re-evaluate facts or redefine state ordering. Success
writes nothing to stderr; non-success writes only the fixed result code and one
newline. Recognized Doctor parse, bounded-read, JSON, filesystem, and SQLite
failures return sanitized Doctor results and never expose help text, exception
text, rejected JSON, credentials, raw content, PII, or local paths.
When any required fact family is unknown, the fixed partial result has
`success=false`, a non-null evaluation, a null primary state, an empty state
list, and nonempty canonically ordered missing families.

Exit categories are fixed:

| Exit | Doctor outcome |
| ---: | --- |
| `0` | `first_trace_ready`, successful start/status/complete/cancel |
| `2` | invalid arguments/input/schema |
| `3` | valid non-ready evaluation or partial fact snapshot |
| `4` | verification not-found/stale/expired/already-terminal/source/evidence conflict |
| `5` | `doctor_store_busy`, `doctor_store_unavailable`, or `internal_error` |

The shared result, fact families, complete fixed-code mapping, human projection,
evidence-selection rules, and persistence semantics are canonical in
[first-trace-doctor.md](first-trace-doctor.md). Existing setup output remains
`setup.v1`; Doctor output is never embedded in or substituted for a setup
result.

## Sanitized Export Commands

```text
config-cli sanitized-export preview --request <request.json>
config-cli sanitized-export export --request <request.json> --output <bundle.zip>
config-cli sanitized-export result --bundle <bundle.zip>
```

`preview` and `export` read one strict source-neutral request. `export` writes
only through the scanner-gated atomic publication path. `result` independently
verifies the frozen archive layout, manifest schema/profile/version fields,
canonical manifest serialization, entry inventory, per-file checksums, scanner
contract, and whole-archive SHA-256. Successful JSON results include the bundle
schema/profile, payload record count, total uncompressed bytes, and archive
SHA-256. Full behavior is canonical in
[sanitized-evidence-export.md](sanitized-evidence-export.md).

## Raw Data Commands

```text
config-cli ingest-raw <raw.json> --db <raw-store.db>
config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
config-cli aggregate-measurements <input.json> [--csv <output.csv>] [--json <output.json>]
config-cli serve-raw-local-receiver [--db <raw-store.db>] [--url <loopback-http-url>]
```

`serve-raw-local-receiver` starts the initial repository-local foreground
receiver for the `raw-local-receiver` profile. Defaults:

```text
--db data/raw-store.db
--url http://127.0.0.1:4319
```

The receiver must reject non-loopback bind URLs. It accepts OTLP HTTP trace
payloads on `/v1/traces`, persists received telemetry as local runtime raw
store data, and leaves normalized measurement, candidate, and dashboard
dataset schemas unchanged.

`serve-raw-local-receiver` is retained and runs **side-by-side** with the Local
Ingestion Monitor (Sprint8). The monitor is a **separate ASP.NET Core process**
(`src/CopilotAgentObservability.LocalMonitor`), not a Config CLI subcommand; it
binds a distinct loopback port (default `http://127.0.0.1:4320`, avoiding the
Collector `4318` and this receiver's `4319`) while this receiver keeps
`http://127.0.0.1:4319`. The monitor run interface, ports, and
health endpoints are specified in
[../layers/telemetry-ingestion.md](../layers/telemetry-ingestion.md), and its
raw / PII boundary in
[../security-data-boundaries.md](../security-data-boundaries.md). To point VS
Code at the monitor, generate the environment with `profile-vscode-env --profile
raw-local-receiver --target monitor`; the default `--target receiver` keeps
emitting `4319`.

## Candidate Commands

```text
config-cli generate-diagnosis-candidates <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--include-sensitive-content --retention-database <local-monitor.db> [--sensitive-output-dir <dir>]] [--csv <output.csv>] [--json <output.json>]
config-cli generate-improvement-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-auto-decisions <improvement-candidates.csv|improvement-candidates.json> [--csv <output.csv>] [--json <output.json>]
config-cli adapt-diagnosis-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> <measurements.csv|measurements.json> [--csv <output.csv>] [--json <output.json>]
```

`--include-sensitive-content` requires both `--raw` and
`--retention-database`. `--retention-database` is the sole explicit catalog
binding; it is never inferred from `--raw`, a cwd, or an output path, cannot be
repeated, and is rejected without sensitive mode. `--sensitive-output-dir` is
an optional parent and remains accepted but inert without sensitive mode. Before
any raw read or output, the command opens and validates the named existing Local
Monitor database; it does not create, discover, or fall back to a catalog.

## Human Review Commands

```text
config-cli validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]
config-cli evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]
config-cli record-human-decisions <evaluations.csv|evaluations.json> <decisions.csv|decisions.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-decision-template <evaluations.csv|evaluations.json> [--csv <output.csv>] [--json <output.json>]
```

## Dashboard Commands

```text
config-cli generate-dashboard-dataset <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--diagnosis-candidates <input.csv|input.json>] [--improvement-candidates <input.csv|input.json>] [--auto-decisions <input.csv|input.json>] [--time-bucket <day|hour|week>] [--csv-dir <output-dir>] [--json <output.json>]
config-cli generate-static-dashboard <dashboard-dataset.json> --out-dir <output-dir> [--snapshot-date <YYYY-MM-DD>] [--title <title>]
```

## Change Rules

- New commands require tests and documentation updates.
- Existing command behavior changes require specification updates.
- Commands must not write secrets or raw sensitive content to repository-safe outputs.

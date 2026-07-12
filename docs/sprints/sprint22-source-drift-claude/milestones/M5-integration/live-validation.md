# Sprint22 M5 — Claude live-validation closeout

Status: repository-safe evidence recorded with explicit live blockers. This
document is the M5 closeout for Issue #65; it does not convert fixture-backed
or documentation-backed facts into producer telemetry evidence.

The three source surfaces are recorded independently. Evidence from one
surface is never copied to another surface, and a blocked live-only criterion
remains unvalidated.

## Evidence record

- Closeout recorded: 2026-07-13
- Source inventory observations: 2026-07-12
- Operating system: Windows
- CLI executable observed by inventory checks: `claude`, version `2.1.207`
- Agent SDK version: not available
- Capture policy: repository-safe structural metadata only; no raw prompt,
  response, tool argument/result, PII, credential, token, or local path is
  retained in this record.
- Canonical references: `docs/specifications/interfaces/source-schema-drift-claude-code.md`
  and the three v1 inventory manifests under
  `docs/specifications/contracts/source-capabilities/v1/inventories/`.

The observed CLI version is provenance evidence, not a receive allowlist. An
unverified version with a known structural fingerprint may be processed; a
new fingerprint is retained and reported as `schema_drift_detected`. A source
is `unsupported_source_version` only when a known incompatibility or required
signal is absent. No live fingerprint was produced by the runs below.

## Surface records

### Interactive Claude Code

| Field | Record |
| --- | --- |
| Surface / mode | `claude-code` / `interactive` |
| Date / OS | 2026-07-12 / Windows |
| Source version | `2.1.207` observed; interactive telemetry not captured |
| Settings labels | `CLAUDE_CODE_ENABLE_TELEMETRY`, `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA`, `OTEL_TRACES_EXPORTER`, `OTEL_LOGS_EXPORTER`, `OTEL_METRICS_EXPORTER`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL`, `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`, `OTEL_LOG_TOOL_CONTENT`, `OTEL_LOG_USER_PROMPTS`, `OTEL_LOG_ASSISTANT_RESPONSES`, `OTEL_LOG_TOOL_DETAILS`, `OTEL_LOG_RAW_API_BODIES`, `ENABLE_BETA_TRACING_DETAILED`, `BETA_TRACING_ENDPOINT` (labels documented only; effective settings not observed) |
| Opaque trace/session references | None observed |
| Observed capabilities | None; OTel, Hook, native session, trace/span identity, and content gate were not observed |
| Unknown fields / completeness | Not observed / not assessed |
| Blocker | `interactive_tty_unavailable`: stdin, stdout, and stderr were redirected, so no authenticated interactive TTY session could be started |
| Named follow-up | `task-7-interactive-claude-code-live-inventory-in-tty-authenticated-session` |

The interactive record is not substituted by `claude -p`. The follow-up must
run separate content-disabled and (if explicitly authorized) synthetic-marker
content-enabled sessions, keep raw API body capture disabled, and record only
structural OTel/Hook evidence and fingerprints.

### Claude print mode (`claude -p`)

| Field | Record |
| --- | --- |
| Surface / mode | `claude-code` / `print` |
| Date / OS | 2026-07-12 / Windows |
| Source version | `2.1.207` observed |
| Settings labels | The same telemetry, OTLP, content, and detailed-tracing labels listed for interactive mode; effective for the bounded content-disabled run |
| Opaque trace/session references | None observed |
| Execution result | Bounded run exited `0`, did not time out, and retained no raw output |
| Observed capabilities | No structural OTel traces/logs/metrics, Hook events, native session identity, trace/span identity, or content gate |
| Unknown fields / completeness | Not observed / not assessed |
| Blocker | `no_emitted_structural_telemetry`: the completed print run emitted no structural interaction, request, tool, Hook, or trace-response telemetry |
| Named follow-up | `claude-code-print-otel-hook-structure-capture` |

The successful process exit is not a structural telemetry pass. It does not
justify creating an OTel or Hook fixture, and it does not substitute for the
interactive or Agent SDK surface. The follow-up requires a disposable local
OTLP sink configured before process start, an explicitly authorized safe tool
or error path, and separate content-disabled/content-enabled synthetic-marker
runs.

### Claude Agent SDK

| Field | Record |
| --- | --- |
| Surface / mode | `claude-code` / `agent-sdk` |
| Date / OS | 2026-07-12 / Windows |
| Source version | Not available |
| Settings labels | SDK OTel/content labels are documented in the inventory; no effective SDK settings were observed |
| Opaque trace/session references | None observed |
| Observed capabilities | None; OTel, Hook, native session, trace/span identity, content gate, and active-parent relationship were not observed |
| Unknown fields / completeness | Not observed / not assessed |
| Blocker | `agent_sdk_package_and_credential_unavailable`: no supported Agent SDK package or reference and no authorized SDK credential were available; no SDK query was started |
| Named follow-up | `#63-live-agent-sdk-inventory` |

The installed Claude CLI, `claude -p`, an Anthropic client SDK, Managed Agents,
and emulated telemetry are not substitutes for this surface. The follow-up
must use an already-provided Python or TypeScript Agent SDK package, an
authorized credential, an active parent span, and a disposable structural
capture sink.

## Compatibility and diagnostics contract

The implementation and fixture tests cover the closed compatibility states;
live producer evidence above does not add a new state:

| State | Reason / next action |
| --- | --- |
| `supported` | no reason / `none` |
| `supported_with_unknown_fields` | `unknown_fields_observed` / `review_unknown_fields` |
| `schema_drift_detected` | `schema_drift_detected` / `capture_fixture_and_review_mapping` |
| `unsupported_source_version` | `unsupported_source_version` / `use_compatible_source_or_update_adapter` |
| `recognized_record_drop_detected` | `recognized_record_drop_detected` / `restore_mapping_or_update_versioned_golden` |
| `adapter_failure` | `adapter_parse_failure` → `validate_payload_and_protocol`; `adapter_exception` → `inspect_sanitized_adapter_failure` |

The sanitized diagnostics endpoint is
`GET /api/monitor/source-diagnostics?after&limit`. It returns opaque IDs,
source/version/adapter metadata, fingerprints and inventory hashes when
known, compatibility state, bounded unknown counts, ordered reason codes, and
`next_action`; it never returns payload fragments or exception text. Source
compatibility does not change `/health/ready` status or thresholds.

## Content and identity warnings

`capture_content_state` is one of `available`, `not_captured`, `redacted`, or
`unsupported`. This is a capture observation, not permission to read or
display raw content. Raw content remains local runtime data behind the existing
loopback, same-origin, no-store, retention, secret-filter, and
`--sanitized-only` boundaries. This report contains no captured content.

Claude OTel owns trace/span identity, parentage, and timing. Hook owns native
session lifecycle and explicit Hook event identity. Binding is allowed only by
an identical native session ID, explicit resume/handoff, or byte-equivalent
trace context; repository, cwd, process, transcript path, and timestamp
proximity are not evidence. Claude ownership and UI hierarchy remain
`unresolved` when parentage is missing or ambiguous.

Complete byte-equivalent trace-context binding is intentionally deferred: the
current Session event DTO exposes `trace_id` but not a complete
trace-context/provenance envelope. A shared trace ID alone is insufficient and
must not produce `exact_linked`; this remains a named interface follow-up.

## Gate outcome

Fixture-backed implementation, security/data-boundary checks, and repository
tests may close their respective gates independently. The live producer gate
is **not complete** for any surface above: interactive execution is blocked by
TTY availability, print mode produced no structural telemetry, and the Agent
SDK was unavailable. The named follow-ups are the only accepted path to future
live evidence.

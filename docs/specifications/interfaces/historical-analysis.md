# Historical Analysis Interface

## Authority and scope

Issue #75 defines the installed Local Monitor historical-analysis presentation
surface. It consumes the persisted Issue #72 dataset, Issue #73
`historical-instruction-analysis.read.v1` read DTO and nested canonical Issue
#59 bytes, Issue #74 exact DTO/canonical receipt bytes, and Issue #53 exact
evidence navigation. It does not re-query history, reconstruct evidence,
recompute an efficiency formula/threshold/quality/verdict/coverage/mitigation,
or add an analysis/finding/candidate schema.

The page is `GET /historical-analysis`. The versioned API family is
`/api/historical-analysis/v1/*`. It is user-triggered: there is no automatic or
background analysis and no combined analyze-all action. Instruction and
efficiency executions are independent. This interface does not absorb
historical import, proposal apply, effect verdict, provider pricing, Alert
Center, portability, raw analysis, or content-enabled live capture. It adds no
dependency, database migration, or new capture authority.

## Operations

All bodies are JSON, at most 1,048,576 bytes, and use closed v1 shapes with
strict unknown-field rejection. Bad bodies, content types, or identifiers use
`400 { "error": "invalid_historical_analysis_request" }` without echoing input.

| Operation | Contract |
| --- | --- |
| `POST /api/historical-analysis/v1/preview` | Accepts one bounded #72 selection and invokes only the #72 owner. Returns extraction identity, included/excluded decisions and exact reasons, distribution, capability, content/posture, and truncation before execution. Excluded or metadata-omitted Session bodies are never read. |
| `POST /api/historical-analysis/v1/instruction-runs` | Starts one #73 run for the exact preview extraction ID and raw-local checksum plus closed #73 provenance. It never starts efficiency work. |
| `GET /api/historical-analysis/v1/instruction-runs/{analysisRunId}` | Returns the exact #73 `historical-instruction-analysis.read.v1` DTO. Success/zero data is exposed only after nested canonical #59 bytes pass `InstructionFindingHandoffConsumerV1.Validate`. |
| `POST /api/historical-analysis/v1/efficiency-runs` | Starts one presentation run over the named #72 repository-safe extraction. It never starts instruction work. |
| `GET /api/historical-analysis/v1/efficiency-runs/{analysisRunId}` | Returns the exact #74 receipt DTO and canonical bytes-derived checksum envelope, without formula or interpretation recomputation. |
| `POST /api/historical-analysis/v1/evidence/resolve` | Resolves safe #72/#59 tokens only through the owner-validated extraction index and #53 navigation mapping. It returns availability/content state and an existing escaped target, never evidence content. |

An absent or checksum-changed extraction is `stale_extraction`, never silently
reselected. GET operations are reads. The POST operations require same-origin
and `x-monitor-csrf: local-monitor`; cross-origin is `403 cross_origin_forbidden`
and a missing token is `403 csrf_required`.

## Preview and presentation

An explicit preview is required before either start control is enabled. It
renders every #72 decision in owner order and exact reason, including
`missing_session_reference`, `filter_mismatch`, `window_truncated`, no exact
evidence, current content state, and `truncated_before`. Mixed source and
completeness are warnings, not a merged cohort or inferred quality judgement.

Instruction presentation consumes only `historical-instruction-analysis.read.v1`.
It preserves `queued`, `running`, `succeeded`, `zero_findings`,
`no_eligible_sessions`, `content_unavailable`, `stale_extraction`,
`extraction_invalid`, `invalid_citation`, `provider_partial`,
`provider_failed`, `timed_out`, and `canceled` exactly. `provider_unavailable`
is the fixed public condition when no explicitly configured provider is
available. Normal production composition has no provider, so it returns
`provider_unavailable` without pretending a run executed. `zero_findings` is a
provider-complete empty #59 handoff, distinct from unavailable, failure, stale,
partial, timeout, and canceled states. Cards preserve #59 category, final
verdict, candidate eligibility, support projection, exact references, gap, and
fixed next-time instruction text. `supported`, `weak`, and `incomplete` have
distinct presentation and weak/incomplete findings are never promoted.

Efficiency presentation consumes exact #74 DTO/canonical bytes and preserves
`succeeded`, `zero_drivers`, and failed invalid-input outcomes without making a
receipt. It shows each supplied category, observed value, rule/threshold, exact
evidence, quality availability, coverage/reasons, verdict, and mitigation.
`supported`, `weak`, and `incomplete` remain distinct. It says that efficiency
drivers are not monetary cost, provider pricing, improvement, or effect verdict.

## Exact evidence resolution

The resolve body contains the extraction ID/checksum and safe reference tokens.
Each token first resolves to the exact owner-validated Session/trace/span/turn,
then returns only:

```json
{
  "reference": "<opaque token>",
  "resolution_state": "resolved | missing | unresolved | expired",
  "content_state": "available | not_captured | redacted | unsupported | expired_pending_deletion | not_applicable",
  "target": "/traces/{trace}"
}
```

`target` is nullable. A present target is only the existing escaped same-origin
`/traces/{trace}` or `/diagnostics?session_id=...` target from #53. There is no heuristic lookup by repository, workspace, path, timestamp, order, shared
trace, or latest Session. `missing`, `unresolved`, and `expired` remain
distinct. Navigation does not authorize raw reads; responses contain no raw
prompt/response/tool body, identifier carrier, path, credential, PII, or source
exception.

## Browser, security, and accessibility

All routes retain loopback bind, Host-header validation, no CORS, same-origin
reads, `Cache-Control: no-store`, JSON-only bounded bodies, and strict
unknown-field rejection. Raw-default is a validation profile, not authorization
to return raw content: responses are repository-safe in raw-default and
sanitized-only postures. `--sanitized-only` keeps safe page/preview/status/
resolution reads while retaining explicit unavailable/expired state; it cannot
enable the #73 provider runner.

The browser retains only a current bounded safe preview/receipt projection in
memory. It uses no browser storage and no full history, raw descriptor, raw
body, provider input, or reusable raw response. Finding, mitigation, error,
and reference text are escaped inert text, never HTML. Logs, URLs, evidence,
screenshots, and tests contain only safe IDs, fixed states, counts, and bounded
safe text.

The page provides semantic headings, labelled scope controls and tables, named
Instruction/Efficiency start controls, and descriptive evidence links. Tab and
Shift+Tab follow visual order; Enter/Space invoke enabled controls. Completion
focuses the result heading; failure/cancel returns focus to its initiator. A
concise `aria-live="polite"` region announces state changes; validation moves
focus to its summary. Color is never the only indication of verdict or state.

## Validation and Issue #91 ownership

`HistoricalAnalysisSpecificationTests` is the focused executable contract.
Future production work owns the `historical-analysis` entry in
`docs/specifications/contracts/validation-matrix/v1/future-surface-registry.json`
and Issue #91 rows for functional, security, and live/E2E coverage. This task
leaves the registry transition and active artifact to Task 4. Required profiles
are raw-default, sanitized-only, content-available, content-unavailable, and
expired-evidence. Matrix coverage includes zero eligible Sessions, mixed
source/completeness, truncation, supported/weak/zero findings, supported/
incomplete/zero drivers, provider unavailable/failed/partial, timeout, cancel,
stale/invalid citation, sanitized-only, expiry, exact drill-down, and keyboard/
live-region behavior. Provider-free normal composition is `provider_unavailable`;
live provider execution remains `blocked_external` until safe evidence exists.

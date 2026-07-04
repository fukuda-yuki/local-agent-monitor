# Sprint16 M5 Live Canvas Runtime Validation

## Summary

GitHub Copilot completed the Sprint16 M5 live Canvas runtime validation on
2026-07-02. Both user-scoped and project-scoped Canvas extension paths opened
successfully, synthetic repository metadata projected into the helper UI, and
bounded Canvas actions returned sanitized responses only.

## Runtime and scope checks

- User scope: copied `.github/extensions/otel-monitor-canvas/` to
  `%USERPROFILE%\.copilot\extensions\otel-monitor-canvas`, ran
  `extensions_reload`, and opened `user:otel-monitor-canvas` with
  `open_canvas`.
- Project scope: copied the same extension folder into the current target
  repository scope, ran `extensions_reload`, and opened
  `project:otel-monitor-canvas` with `open_canvas`.
- Result: both scopes reported `Connected`.

## Synthetic trace checks

Synthetic traces were ingested through `/v1/traces`:

| Trace | Metadata shape | Result |
| --- | --- | --- |
| `synthetic-repo-trace-01` | Includes `repo.name`, `workspace.name`, and `repo.snapshot`. | Projected and displayed sanitized repository/workspace/snapshot labels. |
| `synthetic-unknown-trace-02` | Omits repository metadata. | Projected and displayed the `unknown repository` fallback. |

## Helper page checks

The helper HTML/API was verified in both user scope and project scope:

- Extension scope display: passed.
- Monitor URL display: passed.
- Readiness display: passed.
- Repository/workspace labels: passed.
- Manual repository/workspace filter: passed.
- `unknown repository` fallback: passed.

## Bounded action checks

The following Canvas actions were invoked in both scopes:

- `monitor_health`
- `list_recent_traces`
- `get_trace_summary`
- `get_trace_span_tree`
- `get_cache_summary`

Responses were checked for raw prompt/response bodies, tool arguments/results,
PII, credentials, tokens, raw OTLP payloads, and local paths. The synthetic raw,
secret, and local-path markers used for the negative checks did not appear in
any action response.

## Outcome

Sprint16 M5 live Canvas runtime validation passed for both user-scoped and
project-scoped extension usage. No remaining Sprint16 validation scope is
unverified.

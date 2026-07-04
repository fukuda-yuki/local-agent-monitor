# Sprint9 M6 — Self-Review

Scope of change: **tests + docs only**. No product code, public interface, or
security policy changed — M6 *proves* the M1–M5 posture, it does not alter it.
No new dependencies. Per AGENTS.md this qualifies for a recorded self-review.

Validation run:

```
dotnet build CopilotAgentObservability.slnx   # 0 errors
dotnet test  CopilotAgentObservability.slnx   # ConfigCli 300 + LocalMonitor 214 = 514 passed, 0 failed
```

LocalMonitor tests went 209 → 214 (+5 new cases: `SpanApi_NeverReturnsRawOrPii_UnderRawDefaultOn`,
`SpanApi_GuardsUnsafeFreeFormValues_AndKeepsRows` [Theory ×2],
`SanitizedOnly_ExcludesPiiFromAllReadApis`, `AllRawBearingRoutes_SetNoStore`).

## DR6 negative-matrix traceability

Every matrix item maps to an asserting test or recorded evidence. New tests are
in `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSecurityBoundaryTests.cs`;
already-green tests are listed for completeness (not duplicated).

| # | DR6 matrix item | Asserting test(s) / evidence | New? |
| --- | --- | --- | --- |
| 1 | raw never via `/api/monitor/*` JSON | `DefaultSurfaces_NeverReturnRawOrPii_EvenWithRawShownByDefault` (list APIs); `SpanApi_NeverReturnsRawOrPii_UnderRawDefaultOn` + `SpanApi_GuardsUnsafeFreeFormValues_AndKeepsRows` (spans API) | spans: **new** |
| 1 | raw never via SSE | `MonitorSseTests.Events_NotifyAfterProjectionAndDoNotCarryRawOrPii` (SSE emits `data: {}` only; markers + raw trace-id absent) | existing |
| 2 | raw HTML routes reject cross-site → `403` | raw-detail: `RawRoute_CrossOriginIsForbidden`, `MonitorRawViewTests.RawDetail_CrossSiteFetchIsForbidden` / `_ForeignOriginIsForbidden`; trace-detail: `MonitorTraceDetailTests.TraceDetail_CrossSiteFetchIsForbidden` / `_ForeignOriginIsForbidden` | existing |
| 3 | `Cache-Control: no-store` on **all** raw-bearing routes | `AllRawBearingRoutes_SetNoStore` (both routes in one test); per-route: `MonitorTraceDetailTests.TraceDetail_ByDefault_...WithNoStore`, `MonitorRawViewTests.RawDetail_ByDefault_RendersInertEscapedHtmlWithNoStore` | consolidation: **new** |
| 4 | `--sanitized-only` ⇒ raw routes `404` | `RawRoute_IsAbsentUnderSanitizedOnly`, `MonitorTraceDetailTests.TraceDetail_UnderSanitizedOnly_Returns404AndNoRaw`, `MonitorRawViewTests.RawDetail_AbsentUnderSanitizedOnly_Returns404` | existing |
| 4 | `--sanitized-only` ⇒ PII excluded + no cacheable raw | `SanitizedOnly_ExcludesPiiFromAllReadApis` (list + spans APIs PII-free under sanitized-only); raw routes absent ⇒ no raw response to cache (the `404` tests above) | PII/list: **new** |
| 5 | per-attribute sanitization (email/path/secret guarded out of projection + `/api/monitor/*`), incl. under `--sanitized-only` | `SpanApi_GuardsUnsafeFreeFormValues_AndKeepsRows` [Theory `false`/`true`]: injects email/path/secret into `gen_ai.tool.name`, `…mcp_tool_name`, `gen_ai.agent.name`, `error.type` → fields `null`, values absent, row kept, safe `read_file` survives. Supporting unit coverage: `MonitorSpanProjectionBuilderTests` | **new** |
| 6 | non-loopback / bad-`Host` rejection | `NonLoopbackHostHeader_IsRejectedOnPageRoutes` (`Host: example.com` → `400 invalid_host`); loopback-only bind validation: `MonitorOptionsTests` | existing |
| 7 | raw never logged | Code review: `MonitorHost.Build` calls `builder.Logging.ClearProviders()` ([MonitorHost.cs:35](../../../../../src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs)) — **no log provider is registered**, so nothing is logged at all; a log-capture test would be vacuous. Supporting: `RawPayloadMarkers_DoNotAppearInErrorResponses` (error bodies never echo raw markers / DB path / username / "Exception"). | code-review |
| 7 | raw never committed | Live-validation DB + run logs kept in a scratch dir outside the repo; `live-validation.md` records sanitized evidence only (placeholder `user@example.com`, ids, counts — no raw payloads/PII). No raw OTLP fixtures committed. | code-review |

Supporting property (D020 inert-text de-scope): raw is rendered as escaped,
inert text — `MonitorRawViewTests.RawDetail_ByDefault_RendersInertEscapedHtmlWithNoStore`
asserts `&lt;script&gt;` is encoded and no live `<script` survives. This relies
on the Razor/`HtmlEncoder` default, not a bespoke CSP/sanitizer (intended).

## Live validation

- **Part A — GitHub Copilot CLI 1.0.65:** COMPLETE (2026-06-28). Real
  tool / LLM / token emission projected; **agent child-span hierarchy confirmed**
  (one `invoke_agent` parent → 6 children via `parent_span_id`); PII excluded
  from sanitized surfaces; raw + `no-store` + cross-site `403` on raw-bearing
  routes. Evidence: [live-validation.md](live-validation.md) Part A.
- **Part B — VS Code Copilot Chat:** PENDING USER (human-gated). Checklist in
  [live-validation.md](live-validation.md) Part B. Honest gaps from the CLI run
  (no MCP tool span; no *nested* sub-agent `invoke_agent`) are flagged there for
  the user to close in Part B.

## Design decisions

- **Did not add a duplicate SSE test.** The plan's G1 SSE assertion is already
  satisfied by `MonitorSseTests.Events_NotifyAfterProjectionAndDoNotCarryRawOrPii`;
  re-asserting it would duplicate, against the "do not duplicate" principle.
  Cited in the table instead.
- **`error.type` attribute key.** The projection reads `error.type` (per
  `MonitorSpanProjectionBuilder.ErrorTypeKeys`); the probe payload injects there.
- **Injection values** were chosen to reliably trip `MeasurementSanitizer.IsUnsafeStringValue`:
  email regex (`leak-tool@evil.example.com`), `[A-Za-z]:\` Windows path
  (`C:\Users\victim\secret.txt`), `/etc/` Unix path (`/etc/shadow`), and the
  `api_key` keyword (`api_key=sk-live-DEADBEEF`).

## Residual / not done

- Part B (VS Code) live evidence is owned by the user.
- MCP tool spans and a nested sub-agent hierarchy were not exercised in the live
  CLI run (synthetic per-attribute tests cover MCP-name sanitization; the nested
  hierarchy is a Part B item).

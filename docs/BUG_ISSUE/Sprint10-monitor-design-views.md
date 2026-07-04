# Sprint10 — Monitor Design Views bug cards

Scope reviewed: branch `sprint10-monitor-design-views` vs `main`, focused on
Sprint10 Local Ingestion Monitor design views, validation, and completion state.

Source of truth checked:

- `docs/requirements.md`
- `docs/spec.md`
- `docs/specifications/layers/telemetry-ingestion.md`
- `docs/specifications/security-data-boundaries.md`
- `docs/task.md`
- `docs/sprints/sprint10-monitor-design-views/README.md`
- `docs/sprints/sprint10-monitor-design-views/milestones/M6-validation/*`

Validation attempted during review:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Result:

- `dotnet build CopilotAgentObservability.slnx`: passed, 0 warnings, 0 errors.
- `dotnet test CopilotAgentObservability.slnx`: failed because the new
  Playwright test could not find the Chromium executable under the default
  Playwright browser cache. `ConfigCli.Tests` passed 300 tests; LocalMonitor ran
  246 passing tests and 1 failing test.

Follow-up validation attempted during the 2026-06-29 review:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~IngestionWriterWorkerTests.Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown
dotnet test CopilotAgentObservability.slnx
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj
```

Result:

- `dotnet build CopilotAgentObservability.slnx`: passed, 0 warnings, 0 errors.
- Playwright Chromium install: passed.
- First `dotnet test CopilotAgentObservability.slnx`: failed in
  `IngestionWriterWorkerTests.Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown`.
  `ConfigCli.Tests` passed 300 tests; LocalMonitor had 247 passing tests and 1
  failing test.
- Targeted rerun of the ingestion-worker shutdown test: passed.
- Second `dotnet test CopilotAgentObservability.slnx`: failed in
  `MonitorProjectionApiTests.CursorApis_RejectInvalidQueryWith400` because
  Kestrel failed to bind a `GetFreePort()`-selected loopback port
  (`address already in use`). `ConfigCli.Tests` again passed 300 tests;
  LocalMonitor had 247 passing tests and 1 failing test.
- Direct LocalMonitor project run: passed, 248 tests.

Sprint10 BUG_ISSUE cleanup validation on 2026-06-29:

- `dotnet build CopilotAgentObservability.slnx`: passed, 0 warnings, 0 errors.
- Playwright Chromium install: passed.
- `dotnet test CopilotAgentObservability.slnx`: passed, 300 ConfigCli tests and
  250 LocalMonitor tests.

The direct targeted/project runs are diagnostic evidence only. They do not
substitute for the required solution-level validation command.

## Fix-unit index

| Card | Severity | Fix unit | Status |
| --- | --- | --- | --- |
| S10-1 | High | `--sanitized-only` TraceDetail design views | Fixed |
| S10-2 | High | Playwright validation/bootstrap | Fixed |
| S10-3 | Medium | Sprint10 completion evidence/state | Open — live evidence blocked |
| S10-4 | High | LocalMonitor test host port allocation | Fixed |
| S10-5 | Medium | Ingestion writer shutdown-drain validation | Fixed |

---

<a id="S10-1"></a>

## S10-1 — `--sanitized-only` removes the TraceDetail page, so sanitized Sprint10 tabs cannot open — High

Status: Fixed. TraceDetail now keeps the sanitized Summary / Timeline / Flow
Chart / Cache shell available under `--sanitized-only`, while omitting raw
previews, PII, and full raw links. The full raw route remains `404` in
`--sanitized-only`.

### Problem

Sprint10's design views are specified as sanitized client-side presentation over
the existing spans API and are expected to work under `--sanitized-only`.
Current implementation returns `404` for the whole TraceDetail page under
`--sanitized-only`, so the user cannot open the Summary / Timeline / Flow Chart /
Cache tabs even though those tabs read only sanitized JSON.

### Source of truth

- `docs/specifications/security-data-boundaries.md` says Sprint10 design views:
  - are client-side rendering over sanitized `/api/monitor/*` JSON and SSE only;
  - add no raw-bearing route;
  - work identically under `--sanitized-only` because they are sanitized.
- `docs/spec.md` says Sprint10 TraceDetail has two sections:
  - upper JS tab section backed by sanitized spans JSON;
  - lower Razor raw preview section preserving D023.
- `docs/task.md` already records this as a spec/implementation conflict and says
  Sprint10 is not complete while it remains.

### Observed implementation

- `src/CopilotAgentObservability.LocalMonitor/Pages/TraceDetail.cshtml.cs`
  returns `NotFound()` when `MonitorOptions.SanitizedOnly` is true.
- `src/CopilotAgentObservability.LocalMonitor/Pages/TraceDetail.cshtml` places
  the sanitized tab shell and the raw OTLP preview on the same Razor page.
- `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor-views.js` fetches
  only `/api/monitor/traces/{traceId}/spans?after=...&limit=200` and builds the
  Timeline / Flow Chart / Cache views with `textContent` / DOM APIs, not raw
  routes.
- Existing tests intentionally assert the current broken state:
  `MonitorTraceDetailTests.TraceDetail_UnderSanitizedOnly_Returns404AndNoRaw`.

### Impact

The safety valve mode removes more than raw / PII. It also removes the sanitized
views that Sprint10 exists to provide. This blocks completion because users
cannot inspect sanitized design views in screen-sharing / health-check runs that
must use `--sanitized-only`.

### Reproduction

Use any projected trace in a monitor started with `SanitizedOnly: true`, then
request:

```text
GET /traces/{traceId}
```

Expected: sanitized TraceDetail shell and tabs are available, raw preview/full
raw links are absent.

Actual: `404`.

### Suggested fix path

This card touches a documented security boundary, so resolve the spec conflict
first, then code.

Likely implementation direction:

1. Split TraceDetail rendering into a sanitized shell that is available in both
   modes and a raw section that is rendered only when `!SanitizedOnly`.
2. Keep `GET /traces/{rawRecordId}/raw` returning `404` under
   `--sanitized-only`.
3. Keep same-origin and `Cache-Control: no-store` on any response that includes
   raw / PII. Decide whether the sanitized-only TraceDetail shell still needs
   `no-store`; simplest is to keep it.
4. Remove full raw links and inline raw previews under `--sanitized-only`.
5. Update `docs/specifications/security-data-boundaries.md`,
   `docs/specifications/layers/telemetry-ingestion.md`, `docs/spec.md`, and
   `docs/task.md` so TraceDetail is no longer described as wholly removed when
   the user only wants sanitized tabs.

### Tests to add/update

- Replace `TraceDetail_UnderSanitizedOnly_Returns404AndNoRaw` with assertions
  that `/traces/{traceId}` returns `200`, contains the sanitized tab shell, and
  does not contain raw markers, PII markers, raw previews, or full raw links.
- Add or extend a browser-level test for `--sanitized-only`:
  Timeline / Flow Chart / Cache load from `/api/monitor/traces/{traceId}/spans`
  and no request URL contains `/raw`.
- Keep existing negative tests that `/traces/{rawRecordId}/raw` returns `404`
  under `--sanitized-only`.

### Validation

Run after the fix:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

If Playwright remains in the default test suite, S10-2 must be fixed first or in
the same change so this validation command can pass in a clean environment.

---

<a id="S10-2"></a>

## S10-2 — Standard `dotnet test CopilotAgentObservability.slnx` fails when Playwright browsers are not preinstalled — High

Status: Fixed. CI and repository validation docs now bootstrap Playwright
Chromium after solution build and before the solution test command, so the
browser smoke test no longer depends on hidden developer-profile browser state.

### Problem

Sprint10 adds a Playwright browser smoke test to the LocalMonitor test project.
The repository's standard validation command is still only:

```powershell
dotnet test CopilotAgentObservability.slnx
```

In a clean environment, that command now fails unless Chromium has already been
installed into the Playwright browser cache. The implementation added the NuGet
package and test, but did not update the standard workflow / CI bootstrap or make
the test deterministic when the browser is absent.

### Source of truth

- `docs/requirements.md` and `docs/spec.md` require
  `dotnet build CopilotAgentObservability.slnx` and
  `dotnet test CopilotAgentObservability.slnx` for code/project/workflow changes.
- `.github/workflows/static-dashboard-pages.yml` runs
  `dotnet test CopilotAgentObservability.slnx` without a Playwright browser
  install step.
- `docs/sprints/sprint10-monitor-design-views/milestones/M6-validation/review.md`
  records a targeted manual sequence that installs Chromium, but also says those
  diagnostic runs are not substitutes for the required solution test command.

### Observed implementation

- `tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj`
  adds `Microsoft.Playwright` `1.61.0`.
- `MonitorDesignViewPlaywrightTests.TraceDetailDesignViews_RenderFromSanitizedSpansOnly`
  calls `playwright.Chromium.LaunchAsync(...)` unconditionally.
- No repository-level script, CI step, or standard validation doc installs the
  browser before `dotnet test`.

### Review reproduction

From repository root:

```powershell
dotnet test CopilotAgentObservability.slnx
```

Observed failure:

```text
Microsoft.Playwright.PlaywrightException : Executable doesn't exist under
%LOCALAPPDATA%\ms-playwright\chromium_headless_shell-1228\chrome-headless-shell-win64\chrome-headless-shell.exe

Please run the following command to download new browsers:
    pwsh bin/Debug/netX/playwright.ps1 install
```

### Impact

The required repository validation is red on a clean machine and likely in CI.
This blocks merge readiness even if the product code is otherwise correct.

### Suggested fix path

Pick one explicit validation policy and make the repository consistent.

Preferred options:

- **CI/bootstrap option:** add a Playwright browser install step before every CI
  job that runs the solution tests, and update contributor / repository workflow
  docs with the required local command. This keeps the browser smoke test active
  in normal test runs but changes validation prerequisites.
- **Opt-in smoke option:** keep `dotnet test CopilotAgentObservability.slnx`
  deterministic without browser state by skipping the Playwright test when the
  browser is absent, and add an explicit named command for browser smoke
  validation. This preserves the current standard command but makes the browser
  coverage opt-in and must be documented as not substituting for live validation.

Avoid silently relying on a browser binary that happens to exist in the current
developer profile.

### Tests/docs to update

- Update `.github/workflows/static-dashboard-pages.yml` or any future validation
  workflow that runs `dotnet test CopilotAgentObservability.slnx`.
- Update `docs/agent-guides/repository-workflow.md`, `README.md`, and
  `docs/contributor-guide.md` if the required local validation sequence changes.
- If using opt-in smoke, tag/skip the Playwright test deliberately and document
  the exact command that exercises it.

### Validation

On a clean environment, run:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

The fix is not complete until the solution test command passes without hidden
machine-local browser state.

---

<a id="S10-3"></a>

## S10-3 — Sprint10 completion remains blocked until live evidence is recorded — Medium

Status: Open — user-gated live evidence is still missing. S10-1, S10-2, S10-4,
and S10-5 have been fixed, so the remaining completion blocker is real VS Code
Copilot Chat live validation evidence.

### Problem

The review request said Sprint10 implementation is complete, but repository
records must not mark Monitor Design Views complete until the remaining
human-gated evidence exists. Earlier blockers were:

- S10-1: `--sanitized-only` TraceDetail design views previously could not open
  (fixed).
- S10-2: solution tests require hidden Playwright browser state (fixed).
- Live VS Code Copilot Chat validation has not been recorded (still open).

This is a completion/evidence bug rather than a production-code defect by
itself. It should remain open until the product behavior and evidence trail are
consistent.

### Source of truth

- `docs/task.md` says Monitor Design Views is
  `M6 automated validation added / live validation blocked`.
- `docs/sprints/sprint10-monitor-design-views/README.md` says M6 is blocked by
  live validation.
- `docs/sprints/sprint10-monitor-design-views/milestones/M6-validation/live-validation.md`
  says no user-provided live VS Code Copilot Chat evidence has been recorded and
  that this remains a completion blocker.
- `docs/requirements.md` says Copilot-execution-dependent behavior is not
  guaranteed by automated tests alone; live validation must record date,
  environment, settings, trace id or identifier, confirmed items, and
  unconfirmed items.

### Investigated evidence

- Automated browser validation exists only over synthetic projected spans.
- The live-validation file currently provides a user procedure but no recorded
  evidence.
- Sprint10 M6 review explicitly records that M6 must not be marked complete
  until sanitized live evidence is supplied.
- `docs/task.md` has not been promoted to a completed state.
- S10-1 and S10-2 automated fixes are recorded, but they use synthetic evidence
  and do not satisfy the live gate.

### Impact

Agents may incorrectly treat Sprint10 as releasable and skip the remaining
human-gated evidence. That risks shipping a UI that works only for synthetic
fixtures while real VS Code Copilot Chat hierarchy/cache-token emission remains
unconfirmed.

### Suggested fix path

1. Run the live validation procedure in
   `docs/sprints/sprint10-monitor-design-views/milestones/M6-validation/live-validation.md`
   with a real VS Code Copilot Chat trace.
2. Record only sanitized evidence:
   - date/time and environment;
   - monitor command and endpoint;
   - VS Code / Copilot Chat version if available;
   - trace id or raw record identifier;
   - whether Timeline, Flow Chart, and Cache populated;
   - whether hierarchy and cache/token fields matched what the UI expects;
   - any unconfirmed items.
3. Update Sprint10 README, M6 live-validation record, M6 review if needed, and
   `docs/task.md` only after the evidence is present.

### Validation

Required automated validation after any code/workflow/doc changes:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Required live validation evidence is separate and cannot be replaced by
synthetic automated tests.

---

<a id="S10-4"></a>

## S10-4 — LocalMonitor tests pick loopback ports with a TOCTOU race, so required solution validation can fail — High

Status: Fixed. LocalMonitor tests now start Kestrel on dynamic loopback port
`0` through one shared helper and read the actual bound URL after startup. The
only remaining fixed-port collision test keeps a `TcpListener` open through the
startup attempt, so the collision is deterministic rather than a preselected
free-port race.

### Problem

The required solution validation command can fail before exercising product
behavior because LocalMonitor tests choose a "free" port by opening
`TcpListener(IPAddress.Loopback, 0)`, closing it, and later asking Kestrel to
bind that numeric port. The port is no longer reserved between those two steps.
Parallel test execution or another local process can claim it first.

### Source of truth

- `docs/requirements.md`, `docs/spec.md`, and `docs/decisions.md` D015 require
  `dotnet test CopilotAgentObservability.slnx` as the standard validation
  command for code / project / workflow changes.
- `docs/agent-guides/repository-workflow.md` says failed required validation
  cannot be replaced by another command.
- Local Monitor itself is specified to fail deterministically when its configured
  port is already bound; test infrastructure should not introduce a random port
  collision before assertions run.

### Observed implementation before fix

Duplicated `GetFreePort()` helpers existed across LocalMonitor tests, including:

- `MonitorProjectionApiTests`
- `MonitorTraceDetailTests`
- `MonitorUiTests`
- `MonitorSecurityBoundaryTests`
- `MonitorSseTests`
- `MonitorReadinessFailureTests`
- `MonitorRawViewTests`
- `MonitorDesignViewPlaywrightTests`
- `MonitorHostTests`

Each helper closes the listener before the ASP.NET Core host starts.

### Review reproduction

After a successful build and Playwright browser install, rerunning:

```powershell
dotnet test CopilotAgentObservability.slnx
```

failed in:

```text
MonitorProjectionApiTests.CursorApis_RejectInvalidQueryWith400(
  path: "/api/monitor/traces/trace-1/spans?after=-1")
```

with:

```text
Failed to bind to address http://127.0.0.1:54806: address already in use.
```

Sprint10 M4 review records the same transient `address already in use` failure
in an unrelated `MonitorProjectionApiTests` case.

### Impact

Sprint10 cannot be treated as validated because the required solution command is
not reliably green. This is a validation reliability bug, not a product behavior
change in the Sprint10 views.

### Suggested fix path

Replace the duplicated `GetFreePort()` pattern with one deterministic test-host
helper. Prefer starting Kestrel on a dynamic loopback port (`port 0`) and reading
the actual bound address from the host after `StartAsync`. If that is not
available for the current host setup, serialize socket-bound LocalMonitor tests
or otherwise reserve the port through the bind step.

Implemented fix:

- `MonitorTestHost.StartAsync` builds test hosts with `http://127.0.0.1:0` and
  reads the bound address from `IServerAddressesFeature`.
- LocalMonitor API/page/SSE/security/readiness/browser tests use the shared
  helper instead of duplicated `GetFreePort()` methods.
- `PortAlreadyBoundRunReturnsDeterministicStartupError` opens a listener and
  keeps it bound while `MonitorHost.RunAsync` starts, preserving the product
  behavior test without reintroducing the race.

### Tests to add/update

- Update LocalMonitor test helpers to use the shared deterministic host starter.
- Keep browser tests on a real socket because Playwright needs an HTTP URL.
- Add a repeatable stress check or run the affected LocalMonitor API/page tests
  together enough times to catch the previous race.

### Validation

Run the required validation sequence unchanged:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

The fix is complete only when the solution test command is reliably green
without relying on reruns.

---

<a id="S10-5"></a>

## S10-5 — Ingestion writer shutdown-drain test remains intermittent under solution validation — Medium

Status: Fixed. A gated worker regression now proves `StopAsync` does not return
until an already-accepted queue item has finished committing. The current worker
implementation already satisfied that contract; no production worker change was
needed.

### Problem

`IngestionWriterWorkerTests.Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown`
intermittently fails under the required solution test command. The test enqueues
five accepted requests, calls `StopAsync`, and expects every request completion
to be successful and committed. In failing runs, at least one request completion
is not completed successfully after `StopAsync` returns.

### Source of truth

- `docs/requirements.md`, `docs/spec.md`, and D015 require the full solution
  test command to pass for Sprint10 validation.
- The current `IngestionWriterWorker` contract in code states that shutdown
  stops accepting new work and drains already-accepted queued items.
- The existing test suite encodes this as required behavior; a red validation
  command cannot be treated as success by substituting a targeted rerun.

### Observed implementation

Current worker shutdown path:

- `IngestionWriterWorker.StopAsync` calls `queue.CompleteAdding()`.
- `ExecuteAsync` reads `queue.Reader.ReadAllAsync(CancellationToken.None)`,
  intentionally ignoring the stopping token so accepted items can drain.
- The test immediately asserts `request.Completion.IsCompletedSuccessfully`
  after `await worker.StopAsync(CancellationToken.None)`.

### Review reproduction

After build and Playwright install:

```powershell
dotnet test CopilotAgentObservability.slnx
```

failed in:

```text
IngestionWriterWorkerTests.Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown
Assert.True() Failure
Expected: True
Actual:   False
```

A targeted rerun of the same test passed, and the LocalMonitor test project
passed directly. Sprint10 M5 review records the same test as an unrelated
intermittent full-solution failure that later passed on rerun.

### Impact

This blocks reliable Sprint10 validation and leaves uncertainty about whether
the issue is a test scheduling assumption or a real shutdown drain race. Until
root cause is fixed, a green targeted rerun should be treated only as diagnostic
evidence.

### Suggested fix path

Investigate the worker stop path before changing assertions. Confirm whether
`BackgroundService.StopAsync` can return before the queue-drain loop has
completed in this setup, or whether the test is racing task-continuation
scheduling. Then make either the worker contract or the test deterministic. If
the product contract is to drain accepted items, expose an explicit drain
completion point or make `StopAsync` await it. If only the test is racing, use a
condition-based wait around the committed completions rather than an immediate
state assertion.

Implemented fix:

- Added a gated `IngestionWriterWorker` regression that enqueues one accepted
  request, waits until the writer has entered `Insert`, calls `StopAsync`, and
  asserts the stop task stays incomplete until the gate is released.
- Kept the existing five-item shutdown drain test; the gated test narrows the
  contract so future regressions fail deterministically.
- Because the gated test passed against the existing worker, the issue was
  closed as a validation-stability gap rather than a production shutdown bug.

### Tests to add/update

- Keep a shutdown-drain regression with a gated fake writer so the test proves
  accepted items are processed before shutdown completes.
- Add a timeout-bounded condition wait only after root cause is confirmed.
- Re-run the full solution test command, not only the targeted test.

### Validation

Run:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

The issue is closed only when the full solution test command no longer requires
reruns to get past this test.

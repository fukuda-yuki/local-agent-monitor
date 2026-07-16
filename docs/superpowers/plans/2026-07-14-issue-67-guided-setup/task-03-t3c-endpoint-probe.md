# Task 03 (T3c): Local Monitor endpoint ownership probe

**Objective:** Classify ownership of the selected loopback endpoint from one
bounded probe: `GET <canonical-origin>/health/live`, no redirects, one
500 ms total budget, at most 4096 body bytes plus one sentinel byte, and the
closed three-way classification (Local Monitor live / `monitor_not_running` /
`port_owned_by_foreign_process`).

**Depends on:** task-01 (T3a) committed and reviewed. May run in parallel
with task-02; the file sets are disjoint.

**Files (T3c ownership):**
- Create: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/GitHubCopilotEndpointProbe.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/GitHubCopilotEndpointProbeTests.cs`

**Non-scope:** the HTTP transport itself (T3a's `ISetupHttpProbe` owns
sockets/timeouts; this task only classifies its observation), policy
resolution, aggregate/partition/target files, catalog edits.

**Interfaces:**
- Consumes: `ISetupHttpProbe.Get(origin, "/health/live", 500, 4096)` from
  T3a returning `SetupHttpProbeObservation` (outcomes: `Response` with
  status/trustworthy-content-length/bounded-body/completeness, `Refused`,
  `TimedOut`, `TransportFailure`, `RedirectBlocked`).
- Produces (frozen for T4/T5; the classification-to-diagnostic mapping is the
  consumer's job):

```csharp
internal enum GitHubCopilotEndpointClassification
{
    LocalMonitorLive,       // usable; no warning
    MonitorNotRunning,      // usable; warning monitor_not_running + start_local_monitor
    ForeignOwner,           // fail: port_owned_by_foreign_process, no artifacts
}

internal static class GitHubCopilotEndpointProbe
{
    public static GitHubCopilotEndpointClassification Classify(
        ISetupPlatform platform,
        string canonicalOrigin);
}
```

**Contract (spec "Endpoint and error-state detection" — encode verbatim):**
- Request: `GET <origin>/health/live`, redirects disabled.
- One 500 ms total budget covering connect, request, response headers, and
  body read. Connect, read, and total-budget timeout all classify as
  `ForeignOwner` once the probe attempt begins.
- If a trustworthy valid `Content-Length` exceeds 4096 → fail immediately
  (`ForeignOwner`); otherwise read at most 4096 payload bytes + 1 sentinel
  byte so an unknown/chunked 4097-byte response is detected without an
  unbounded read.
- `LocalMonitorLive` only for HTTP 200 whose complete body parses as a JSON
  object with exactly one property, case-sensitive string `"status":"live"`;
  JSON whitespace and property order irrelevant; duplicate or additional
  properties forbidden.
- Socket connection refused or an equivalent positive no-listener result →
  `MonitorNotRunning`.
- Everything else — any redirect (unfollowed), non-200, timeout, transport
  failure other than refused/no-listener, body over 4096 bytes, malformed
  JSON, non-object JSON, any other JSON object → `ForeignOwner`.
- The probe is an observation; nothing here claims ownership cannot change
  afterwards (no retry loop, no second probe).

## Steps

- [ ] **Step 1: Write the failing classification matrix** in
  `GitHubCopilotEndpointProbeTests` over scripted `ISetupHttpProbe`
  observations. Minimum cases (one test per row, deterministic — no real
  sockets):
  1. 200 + `{"status":"live"}` → `LocalMonitorLive`.
  2. 200 + `{ "status" : "live" }` (whitespace) → `LocalMonitorLive`.
  3. 200 + `{"status":"live","extra":1}` → `ForeignOwner`.
  4. 200 + duplicate `status` property → `ForeignOwner`.
  5. 200 + `{"status":"Live"}` (case) → `ForeignOwner`.
  6. 200 + non-object (`"live"`, `[]`) → `ForeignOwner`.
  7. 200 + malformed JSON → `ForeignOwner`.
  8. 200 + incomplete body (4097th byte observed / `IsComplete=false`) →
     `ForeignOwner`.
  9. Trustworthy `Content-Length: 4097` → `ForeignOwner` (assert via the
     fake that no body read beyond headers happened, i.e. the bounded-read
     request was skipped or zero-sized — pin the exact seam behavior).
  10. Non-200 (204, 404, 500) → `ForeignOwner`.
  11. `RedirectBlocked` → `ForeignOwner`.
  12. `Refused` → `MonitorNotRunning`.
  13. `TimedOut` and `TransportFailure` → `ForeignOwner`.
  14. Exactly one probe call per classification (call counter on the fake).

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~GitHubCopilotEndpointProbeTests
```

- [ ] **Step 3: Implement `Classify`** (pure over the observation; strict
  single-property JSON check via `Utf8JsonReader` so duplicate properties are
  detectable — `JsonDocument` alone silently keeps duplicates; verify and
  pick the mechanism that makes case 4 provable).

- [ ] **Step 4: Run GREEN**, build, `git diff --check`.

- [ ] **Step 5: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/GitHubCopilotEndpointProbe.cs tests/CopilotAgentObservability.ConfigCli.Tests/GitHubCopilotEndpointProbeTests.cs
git commit -m "Issue #67: feat(setup): classify monitor endpoint ownership"
```

  Body: why — the spec closes endpoint ownership into three outcomes with a
  fail-closed default so setup never writes toward a port owned by an
  unknown process.

## Validation

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~GitHubCopilotEndpointProbeTests
dotnet build CopilotAgentObservability.slnx
git diff --check
```

## Completion criteria

- Every spec bullet has at least one executable case; unknown observations
  fail closed to `ForeignOwner`.
- Exactly one probe attempt per classification; no retries, no real network.
- Independent review PASS (reviewer walks the spec section line-by-line
  against the test matrix).

**Report destination:** chat + ledger row per README policy.

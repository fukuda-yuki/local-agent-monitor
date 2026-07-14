# Task 07 (T6): App/SDK no-write guidance target partition

**Objective:** Implement the `app-sdk` target partition: bounded detection of
the current repository's .NET `GitHub.Copilot.SDK` package presence/version,
one no-write guidance target with null manifest, the exact pinned .NET
sample, and a compile test proving the sample matches the repository's SDK
contract.

**Depends on:** task-04 (T3d) committed and reviewed. May run in parallel
with tasks 05/06; the file sets are disjoint.

**Files (T6 ownership):**
- Create: everything under
  `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/AppSdk/`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/CopilotSdkGuidanceAdapterTests.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/CopilotSdkTelemetryCompileTests.cs`

**Non-scope:** T3-owned shared files, other target subdirectories, the
production composition root (T7), catalog edits, and — explicitly — any
PackageReference addition or change (the compile test uses whatever SDK
reference already exists in the repository; if none exists in a compilable
location, the compile test asserts the sample against the pinned contract by
the mechanism the repository already uses for sample verification — inspect
how existing `ConfigSamples` are tested before choosing, and record the
choice in the commit body).

**Interfaces:**
- Consumes (frozen): the T3d partition seam; `ISetupFileSystem` bounded
  reads for package detection; `SetupGuidance` (kind/language/sample) and
  `SetupTargetKind.Guidance`; the pinned sample constant that already exists
  as `SetupContractValidator.AppSdkGuidanceSample` (the validator requires
  guidance samples to match it — reuse, do not duplicate drift).
- Produces: `AppSdkTargetPartition : IGitHubCopilotTargetPartition` with
  `TargetToken == "app-sdk"` — consumed by T7's composition only.

**Contract (spec "GitHub Copilot App / SDK" — encode verbatim):**
- Detection reports whether the current repository's .NET
  `GitHub.Copilot.SDK` package is present and its sanitized semantic
  version; it does not search or mutate arbitrary external application
  files, and never returns a project or package path.
- Exactly one guidance target, label `github-copilot-app-sdk-guidance`,
  `target_kind=guidance`, `detected` = package presence,
  `detected_version` = sanitized version or null, `operation=no-op`,
  `rollback_available=false`, `expected_result=null` (no #61 manifest; no
  borrowed capability declaration), guidance shape
  `{"kind":"caller_managed_sample","language":"dotnet","sample":<pinned>}`.
- The pinned sample (must byte-match the validator constant):

```csharp
new CopilotClientOptions
{
    Telemetry = new TelemetryConfig
    {
        OtlpEndpoint = "http://127.0.0.1:4320",
        OtlpProtocol = "http/protobuf"
    }
}
```

- Plan is `no-op` guidance; there is no mutation, no rollback, and no
  apply-time revalidation work (`Revalidate` returns `Revalidated()` with no
  diagnostics — verify against the T3d seam whether guidance-only records
  even route to revalidation, and pin the answer).
- Success means the sample is available — never a claim the caller used it
  or a trace arrived. Other SDK languages remain caller-managed.

## Steps

- [ ] **Step 1: Write the failing guidance tests** in
  `CopilotSdkGuidanceAdapterTests`: package present → detected true +
  version; absent → detected false + null version; no path in any field
  (marker negative: plant a marker in the package path/csproj location and
  assert absence from all records/projections); exactly one guidance target
  with the exact label/kind/no-op/null-manifest/false-rollback shape; sample
  byte-equals `SetupContractValidator.AppSdkGuidanceSample`; endpoint and
  policy state do NOT gate guidance (guidance plans succeed regardless —
  verify this against the spec/T3d aggregation rule and pin it; if the
  aggregate gates all partitions on the probe, record the pinned behavior
  instead).

- [ ] **Step 2: Write the failing compile test** in
  `CopilotSdkTelemetryCompileTests`: the sample compiles against the pinned
  SDK telemetry contract (mechanism per the Non-scope note; the test's job
  is to fail when the SDK contract or the pinned sample drifts).

- [ ] **Step 3: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~CopilotSdkGuidanceAdapterTests|FullyQualifiedName~CopilotSdkTelemetryCompileTests"
```

- [ ] **Step 4: Implement the partition** (bounded csproj/package detection
  via `ISetupFileSystem`; guidance record construction reusing the validator
  sample constant).

- [ ] **Step 5: Run GREEN + full ConfigCli + build + `git diff --check`.**

- [ ] **Step 6: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/AppSdk tests/CopilotAgentObservability.ConfigCli.Tests/CopilotSdkGuidanceAdapterTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/CopilotSdkTelemetryCompileTests.cs
git commit -m "Issue #67: feat(setup): add Copilot SDK guidance"
```

  Body: why — App/SDK configuration is caller-owned; the partition's whole
  value is a verified-current sample plus honest detection, with no write
  path to invent.

## Validation

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~CopilotSdkGuidanceAdapterTests|FullyQualifiedName~CopilotSdkTelemetryCompileTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

## Completion criteria

- One guidance target with the exact pinned shape; sample sourced from the
  validator constant (single owner, no duplicate literal).
- Path-marker negative and no-mutation guarantees executable.
- Compile test fails on SDK-contract or sample drift.
- No PackageReference change (`git diff` on every `.csproj` is empty).
- Full ConfigCli suite and build pass; independent review PASS.

**Report destination:** chat + ledger row per README policy.

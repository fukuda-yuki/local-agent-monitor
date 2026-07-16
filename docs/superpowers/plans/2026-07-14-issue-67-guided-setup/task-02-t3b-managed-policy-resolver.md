# Task 02 (T3b): Managed-policy resolver

**Objective:** Resolve the GitHub Copilot managed-policy state from T3a
observations: whole-channel `native > server > file` selection with no field
merging, independent VS Code enterprise-policy evaluation, equal-versus-
differing constraint classification, and the unverified-server boundary.
Pure classification over observations — no I/O of its own, no plan or
carrier construction.

**Depends on:** task-01 (T3a) committed and reviewed. May run in parallel
with task-03; the file sets are disjoint.

**Files (T3b ownership):**
- Create: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/GitHubCopilotManagedPolicyResolver.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/GitHubCopilotManagedPolicyTests.cs`

**Non-scope:** platform files, endpoint probe, aggregate/partition/target
files, catalog/validator edits, warning/next-action emission into a public
result (the resolver returns typed classification; targets translate it).

**Interfaces:**
- Consumes: `ISetupManagedSettingsSource.Read(SetupManagedLocation)` and
  `SetupPlanningOs` from T3a (via `ISetupPlatform`), plus the desired
  telemetry field values supplied by the caller.
- Produces (frozen for T4; record final shapes in the commit body):

```csharp
internal sealed record GitHubCopilotManagedPolicyResolution(
    GitHubCopilotManagedChannel WinningChannel,   // Native | File | None
    bool ServerTierVerifiable,                    // true only when native proves the winner
    IReadOnlyList<ManagedFieldConstraint> CopilotConstraints,
    IReadOnlyList<ManagedFieldConstraint> EnterprisePolicyConstraints);

internal sealed record ManagedFieldConstraint(
    string SettingKey,
    ManagedConstraintComparison Comparison);      // EqualToDesired | DiffersFromDesired

internal static class GitHubCopilotManagedPolicyResolver
{
    public static GitHubCopilotManagedPolicyResolution Resolve(
        ISetupPlatform platform,
        SetupPlanningOs planningOs,
        IReadOnlyDictionary<string, string> desiredValues);
}
```

  Constraint values themselves are never stored or returned — only the
  comparison outcome (security rule: no raw setting values). Malformed
  managed content (non-JSON file, unreadable registry value) must map to a
  deterministic classification decided in this task and recorded in the
  commit body (recommended: a `MalformedWinningChannel` marker that T4 maps
  to `managed_policy_conflict`-class refusal — verify against the spec's
  "observed conflicting value" sentence before pinning).

**Contract (spec "VS Code GitHub Copilot Chat", managed sections):**
- Tier order `native > server > file`. The first tier that supplies any
  managed settings is the sole managed object; lower tiers are ignored
  wholesale; fields are never merged across channels.
- Linux has no native channel; server is never locally observable.
- When native is present: it proves the winning channel;
  `ServerTierVerifiable = true`.
- When native is absent: the server tier cannot be proved present or absent;
  `ServerTierVerifiable = false` even when a file tier is observed (a local
  file is a bounded fact, not a claim of effectiveness). T4 translates this
  to warning `managed_policy_unverified` + next action
  `run_vscode_policy_diagnostics`.
- VS Code enterprise policy (`CopilotOtel*` under both `HKLM` and `HKCU`
  `Software\Policies\Microsoft\VSCode`, macOS configuration profiles, Linux
  `/etc/vscode/policy.json`) is a separate system: computer policy wins over
  user policy *within* that system; it never suppresses Copilot server/file
  discovery and is never merged into the Copilot object.
- Per planned field: any observed value from either system differing from
  the desired value → `DiffersFromDesired` (T4 maps to
  `managed_policy_conflict`, no plan); equal observed constraints →
  `EqualToDesired` (reported as managed, not rewritten).

## Steps

- [ ] **Step 1: Write the failing classification matrix** in
  `GitHubCopilotManagedPolicyTests` using scripted
  `SetupTestPlatform.ManagedSettings` observations. Minimum cases:
  1. Native present + equal values → winner Native, verifiable, all
     `EqualToDesired`, file tier content ignored even when contradictory.
  2. Native present + one differing field → `DiffersFromDesired` for exactly
     that field.
  3. Native absent + file present + equal → winner File, NOT verifiable.
  4. Native absent + file absent → winner None, NOT verifiable, no
     constraints.
  5. Linux planning OS never consults a native location (assert via the fake
     that no native `Read` occurs).
  6. Enterprise HKLM and HKCU disagree → HKLM wins within the enterprise
     list; Copilot channel resolution unaffected.
  7. Enterprise equal + Copilot file differing → both lists populated
     independently (no merge).
  8. Malformed winning-channel content → the pinned deterministic
     classification; no exception text propagates.
  9. No raw observed value appears in any resolution object
     (`Assert.DoesNotContain` on serialized test dump for an injected
     marker value).

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~GitHubCopilotManagedPolicyTests
```

- [ ] **Step 3: Implement the resolver** (pure function over observations;
  JSON parsing bounded and closed — only the telemetry keys the desired-set
  names are compared, unknown managed keys ignored).

- [ ] **Step 4: Run GREEN**, then `git diff --check` and build.

- [ ] **Step 5: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/GitHubCopilotManagedPolicyResolver.cs tests/CopilotAgentObservability.ConfigCli.Tests/GitHubCopilotManagedPolicyTests.cs
git commit -m "Issue #67: feat(setup): resolve Copilot managed policy"
```

  Body: why — the spec pins whole-channel precedence with an unverifiable
  server tier and an independent enterprise-policy system; encoding it as a
  pure resolver keeps the boundary testable and keeps raw managed values out
  of every downstream artifact.

## Validation

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~GitHubCopilotManagedPolicyTests
dotnet build CopilotAgentObservability.slnx
git diff --check
```

## Completion criteria

- Whole-channel selection with no merge proven, including the
  contradictory-file-under-native case.
- Unverified-server boundary: verifiable only when native is present.
- Enterprise system evaluated independently with computer-over-user
  precedence.
- No raw observed value in any output; malformed content classified
  deterministically.
- Independent review PASS.

**Report destination:** chat + ledger row per README policy.

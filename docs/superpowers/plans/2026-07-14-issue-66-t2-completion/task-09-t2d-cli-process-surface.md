# Task 09: T2d CLI process surface (setup verbs, JSON stdout, exit mapping)

**Objective:** Expose the recognized `setup` grammar through `CliApplication`
with a generic dispatcher-injection seam: exactly one `SetupJson` result on
stdout per recognized verb, the fixed stderr and 29-code exit mapping, the
bare/unknown-setup-verb special case, preserved legacy top-level behavior,
and updated help text.

**Depends on:** Task 08 integration review PASS.

**Files (T2d ownership, first half):**
- Modify: `src/CopilotAgentObservability.ConfigCli/Cli/CliApplication.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Cli/CliHelpText.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/CliApplicationTests.cs`

**Non-scope:** `Program.cs` (T7 owns the production composition root and
adapter registration), any adapter registration, any dispatcher/lock/
recovery/target logic, `scripts/` (Task 10), release packaging (T8).

**Interfaces:**
- Consumes: `SetupOptions.Parse(string[]) : SetupOptionsParseResult`
  (`Options` xor fixed `Code`), `SetupCommandDispatcher.Dispatch(SetupOptions) : SetupCommandResult`,
  `SetupJson` (the sole serializer), `SetupCodes` constants.
- Produces (Task 10 and T7 rely on these exact names):
  - `CliApplication.Run(string[] args, TextWriter output, TextWriter error)`
    — unchanged signature, preserved legacy behavior when the first token is
    not `setup` and no dispatcher is supplied;
  - a new overload
    `CliApplication.Run(string[] args, TextWriter output, TextWriter error, Func<SetupOptions, SetupCommandResult>? setupDispatcher)`
    — the injection seam. The three-argument overload forwards with `null`.
    With `null` and first token `setup`, behavior follows the spec's
    recognition rules using parse-only handling (no dispatcher construction):
    recognized-verb parse failures still produce the `invalid_arguments`
    result path; a recognized, successfully parsed command without an
    injected dispatcher is a T7 wiring gap and must fail closed as
    `internal_error` through the same result path (never a silent legacy
    fallback). Record this pre-T7 behavior in the commit body.
  - `internal static int MapExitCode(string code)` — the exhaustive 29-code
    mapping (or a private equivalent tested through `Run`).

**Contract (spec "Public commands" + "Fixed result and error codes"):**

- Recognition: first token exactly `setup`, second token exactly one of
  `plan|apply|rollback|status`.
- Recognized verb: every option/arity/value failure returns that command's
  `setup.v1` result with `code=invalid_arguments` (JSON on stdout, code +
  `\n` on stderr, exit 2).
- Bare `setup` or `setup <unknown-verb>`: NO stdout JSON; stderr exactly
  `invalid_arguments` + one newline; exit 2; no help rendering, lock
  acquisition, recovery, registry lookup, or storage read.
- Unknown top-level token other than `setup`: preserved legacy exit-1/help
  behavior.
- Success result: one JSON object + newline on stdout, nothing on stderr.
- Failure result: one JSON object on stdout, exactly the fixed result code +
  one newline on stderr, no exception/help text appended.
- Exit mapping (exhaustive):

| Exit | Codes |
| --- | --- |
| 0 | `plan_ready`, `no_changes`, `apply_succeeded`, `rollback_succeeded`, `status_ready`, `interrupted_apply_recovered`, `interrupted_rollback_recovered` |
| 2 | `invalid_arguments` |
| 3 | `managed_policy_conflict`, `environment_override_conflict`, `stale_plan`, `rollback_stale` |
| 4 | `unsupported_adapter`, `unsupported_target`, `target_not_installed`, `unsupported_version`, `rollback_not_available`, `port_owned_by_foreign_process` |
| 5 | `malformed_settings`, `permission_denied`, `unsafe_path`, `partial_apply`, `setup_busy`, `recovery_required`, `interrupted_recovery_failed`, `ledger_corrupt`, `ledger_version_unsupported`, `internal_error` |
| 6 | `partial_rollback` |

## Steps

- [ ] **Step 1: Write the failing recognition tests** in
  `CliApplicationTests` (existing file; follow its output/error
  StringWriter conventions):
  - `Run_SetupPlanForwardsParsedOptionsToInjectedDispatcherAndWritesOneJson`
    — inject a recording `Func<SetupOptions, SetupCommandResult>` returning a
    validator-valid `plan_ready` result; assert stdout is exactly
    `SetupJson.Serialize(result)` + newline, stderr empty, exit 0, delegate
    called once with the parsed options.
  - `Run_BareSetupWritesFixedStderrOnlyAndExitsTwo` — `["setup"]`: stdout
    empty, stderr exactly `"invalid_arguments\n"`, exit 2, injected delegate
    never called.
  - `Run_UnknownSetupVerbBehavesLikeBareSetup` — `["setup","frobnicate"]`.
  - `Run_RecognizedVerbParseFailureWritesInvalidArgumentsResult` —
    `["setup","plan","--adapter"]` (missing value): stdout carries the
    `invalid_arguments` JSON for command `plan`, stderr
    `"invalid_arguments\n"`, exit 2, delegate never called.
  - `Run_UnknownTopLevelTokenPreservesLegacyHelpBehavior` — `["nonsense"]`:
    unchanged legacy stderr/help, exit 1.

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~CliApplicationTests
```

- [ ] **Step 3: Write the exhaustive exit-mapping tests.** One
  `[Theory]` with 29 `[InlineData]` rows (code → expected exit), driving
  `Run` with an injected dispatcher that returns a validator-valid result for
  that code and command, asserting exit code, one-JSON stdout, and
  fixed-stderr behavior (empty for the 7 success codes, `code\n` for the 22
  failure codes). Building 29 validator-valid results is the bulk of the
  work — reuse the dispatcher-test fixtures where a code requires
  correlation/targets (e.g. recovery codes need
  `recovered_change_set_id`/`recovery_operation`).

- [ ] **Step 4: Implement.**
  - `CliHelpText`: add the four setup command lines (mirror the spec's
    "Public commands" block exactly; keep existing text otherwise).
  - `CliApplication`: add the overload and, before the legacy `switch`, the
    setup branch:

```csharp
if (args.Length >= 1 && string.Equals(args[0], "setup", StringComparison.Ordinal))
{
    return RunSetup(args, output, error, setupDispatcher);
}

private static int RunSetup(
    string[] args,
    TextWriter output,
    TextWriter error,
    Func<SetupOptions, SetupCommandResult>? setupDispatcher)
{
    if (args.Length < 2 || args[1] is not ("plan" or "apply" or "rollback" or "status"))
    {
        error.Write(SetupCodes.InvalidArguments);
        error.Write('\n');
        return 2;
    }

    var parsed = SetupOptions.Parse(args);
    SetupCommandResult result;
    if (parsed.Options is null)
    {
        result = /* fixed invalid_arguments SetupCommandResult for the
                    recognized verb; no dispatcher, lock, or storage touch */;
    }
    else if (setupDispatcher is null)
    {
        result = /* fixed internal_error result for the recognized verb (T7 wiring gap) */;
    }
    else
    {
        result = setupDispatcher(parsed.Options);
    }

    output.WriteLine(SetupJson.Serialize(result));
    if (!result.Success)
    {
        error.Write(result.Code);
        error.Write('\n');
    }

    return MapExitCode(result.Code);
}
```

  `MapExitCode` is an exhaustive `switch` over the 29 codes with a
  fail-closed `_ => 5` arm. The fixed result constructions must pass
  `SetupContractValidator.Validate`; build them through the same helper
  shapes the dispatcher uses (command, success=false, code, correlation from
  `--change-set` when the verb parsed one, empty collections). Newline
  handling: use `WriteLine`/`Write('\n')` exactly as asserted; the wrapper
  test in Task 10 compares bytes.

- [ ] **Step 5: Run GREEN + full ConfigCli + build.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~CliApplicationTests
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

- [ ] **Step 6: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Cli/CliApplication.cs src/CopilotAgentObservability.ConfigCli/Cli/CliHelpText.cs tests/CopilotAgentObservability.ConfigCli.Tests/CliApplicationTests.cs
git commit -m "Issue #66: feat(setup): expose setup verbs on the CLI process surface"
```

  Body: why — the dispatcher was complete but unreachable from the process
  boundary; the spec pins one-JSON stdout, fixed stderr, and the exhaustive
  29-code exit mapping; note the pre-T7 null-dispatcher fail-closed behavior.

## Validation

Step 5 commands.

## Completion criteria

- All 29 result codes have executable exit/stdout/stderr tests.
- Bare/unknown setup verb: no JSON, fixed stderr, exit 2, zero
  lock/recovery/storage/dispatcher activity.
- Legacy top-level behavior unchanged (existing `CliApplicationTests` all
  green).
- Injection seam consumed only — no production dispatcher construction, no
  `Program.cs` edit; independent review PASS.

**Report destination:** chat + ledger row per README policy.

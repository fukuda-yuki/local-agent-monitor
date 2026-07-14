# Issue #66 Task09 prerequisite contract fix report

## Scope

- Branch: `codex/issues-66-67-guided-setup`
- Initial HEAD: `3cbe822722fa4eeab84130bc7ecf7aa25b2bdbd5`
- Initial worktree: clean
- Owned production file: `src/CopilotAgentObservability.ConfigCli/Setup/Contracts/SetupContractValidator.cs`
- Owned test file: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupContractValidationTests.cs`

This fixes only the prerequisite result-contract defect. It does not claim Issue #66 Task09 complete and does not change CLI parsing, dispatch, adapters, storage, specifications, or Issue #67 behavior.

## Root cause and change

`SetupContractValidator.ValidateResultCodeForCommand` omitted `invalid_arguments` from the explicit `status` failure-code membership even though the canonical interface requires every recognized status option/arity/value failure to return a repository-safe `SetupCommandResult(command=status, code=invalid_arguments)`.

The production change adds only `SetupCodes.InvalidArguments` to the existing `SetupCommand.Status` failure-code expression. It deliberately does not reuse `IsCommonFailure`, because that would also add `permission_denied` and `unsafe_path` to status and broaden the matrix beyond this fix.

The regression test invokes `SetupContractValidator.Validate` directly, then passes the same repository-safe result through `SetupJson.Serialize` and verifies the status command/code, failure flag, null correlations/adapter, empty public arrays, and non-truncated shape.

## TDD evidence

RED command:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~ValidateAndSerialize_StatusInvalidArguments_AcceptsRepositorySafeFailure"
```

RED result before the production edit: exit `1`; failed `1`, passed `0`, skipped `0`, total `1`. The failure was `setup_contract_invalid` from `ValidateResultCodeForCommand`, proving the intended missing membership.

GREEN result after the one-line production edit: exit `0`; failed `0`, passed `1`, skipped `0`, total `1`.

## Focused validation

Command:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupContractValidationTests|FullyQualifiedName~SetupContractShapeTests"
```

Result: exit `0`; failed `0`, passed `171`, skipped `0`, total `171`. The .NET 10 preview SDK emitted its existing `NETSDK1057` informational messages; there were no build or test failures.

`git diff --check`: exit `0`, no output.

## Self-review

- Spec compliance and correctness: matches `docs/specifications/interfaces/configuration-setup.md` lines 46-52 and 92-94.
- Regression risk: the status matrix gains exactly one code; all other command/code memberships are textually unchanged.
- Test quality: the regression exercises real validator and serializer code without mocks and asserts the repository-safe public result shape.
- Maintainability: no abstraction, fallback, compatibility path, comment, dependency, or adjacent refactor was added.
- Data safety: the fixture contains only fixed contract literals, nulls, and empty arrays.
- Documentation: no source-of-truth update is needed because the implementation was defective against an already-pinned contract.

No blocking self-review findings. Full-solution build and tests were not requested for this narrowly assigned prerequisite fix; the exact assigned focused suite is the verified scope.

## Unresolved issues

None within this prerequisite contract-fix scope. Task09 implementation itself remains outside this report and is not claimed complete.

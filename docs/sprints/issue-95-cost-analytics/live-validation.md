# Issue #95 Live Validation

## Candidate binding

matrix_prep_sha: 98bfd62c5aad65961beefe42f51141c7e9f54169
final_validation_sha: 88e4c40dd7e76d6a80bb87a17c4e5acd88081bf8
validation_date: 2026-07-25
validation_environment: Windows x64, .NET SDK 10.0.300-preview.0.26177.108, .NET runtime 10.0.5, repository-local Playwright Chromium

The candidate is a clean descendant of current `main`
`f1f9512180a2c54631e5837f3bc90ab43d53b542`, P1
`07dc219c4f5c5ef56e7810a23c6466a52e90aa97`, and P2
`245de89b0d016012a68e29ed00309c9cc768e81a`. The functional tree remained
unchanged throughout the recorded GREEN gates.

## Required commands and results

required_command_count: 16
required_command_failures: 0
command_result: pwsh scripts\agent\sync-claude-skills.ps1 -Check | exit=0 | result=passed
command_result: dotnet build CopilotAgentObservability.slnx | exit=0 | result=passed
command_result: pwsh scripts\test\install-playwright-chromium.ps1 | exit=0 | result=passed
command_result: dotnet test CopilotAgentObservability.slnx | exit=0 | result=passed
command_result: dotnet test CopilotAgentObservability.slnx --no-build --no-restore --filter <13 canonical Issue #75 handoff filters> --logger console;verbosity=minimal | exit=0 | result=passed
command_result: dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~HistoricalEvidenceProductionTests --logger console;verbosity=minimal | exit=0 | result=passed
command_result: dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~AlertCenter|FullyQualifiedName~AlertLifecycle|FullyQualifiedName~MonitorOverview --logger console;verbosity=minimal | exit=0 | result=passed
command_result: dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~RuntimeBackupCliTests --logger console;verbosity=minimal | exit=0 | result=passed
command_result: dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~RuntimeBackup --logger console;verbosity=minimal | exit=0 | result=passed
command_result: dotnet test CopilotAgentObservability.slnx --no-build --no-restore --filter <20 canonical Issue #95 handoff filters> --logger console;verbosity=minimal | exit=0 | result=passed
command_result: dotnet test tests\CopilotAgentObservability.Alerts.Tests\CopilotAgentObservability.Alerts.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~SqliteAlertEngineStoreV2Tests|FullyQualifiedName~SqliteAlertLifecycleStoreTests --logger console;verbosity=minimal | exit=0 | result=passed
command_result: dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~PricingPersistenceFoundationTests|FullyQualifiedName~RuntimeBackupPricingCompatibilityTests --logger console;verbosity=minimal | exit=0 | result=passed
command_result: dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~PricingCatalogProviderTests --logger console;verbosity=detailed | exit=0 | result=passed
command_result: pwsh -NoProfile -File scripts\validation\issue-91\test-scan-outputs.ps1 | exit=0 | result=passed
command_result: pwsh scripts\validation\issue-95\test-evidence-chain.ps1 -VerifierPath scripts\validation\issue-95\verify-evidence-chain.ps1 -MatrixValidatorPath scripts\validation\issue-91\validate-matrix.ps1 -MatrixFixturePath docs\specifications\contracts\cost-analytics\v1\issue-91-validation-row-contract.json | exit=0 | result=passed
command_result: git diff --check | exit=0 | result=passed

The full aggregate recorded 8,976 passed / 1 skipped / 8,977 total with the
only skip being the Linux-only `linux_fifo=not_applicable` case, and zero failures. Project totals were Local
Monitor 3,427 passed / 1 skipped, Config CLI 4,606 passed, Doctor 266 passed,
Alerts 495 passed, Pricing 162 passed, and Instruction Findings 20 passed.
The canonical Issue #95 focused surface passed Local Monitor 791 / 1 OS-only
skip, Alerts 159, and Config CLI 22.

automated_filter: FullyQualifiedName~PricingPersistenceFoundationTests
automated_filter: FullyQualifiedName~PricingCatalogProviderTests
automated_filter: FullyQualifiedName~PricingQueryFoundationTests
automated_filter: FullyQualifiedName~CostConfigurationApplicationServiceTests
automated_filter: FullyQualifiedName~CostRecalculationCoordinatorTests
automated_filter: FullyQualifiedName~CostAnalyticsReadModelTests
automated_filter: FullyQualifiedName~CostRouteTests
automated_filter: FullyQualifiedName~CostPageTests
automated_filter: FullyQualifiedName~CostPagePlaywrightTests
automated_filter: FullyQualifiedName~AlertEngineV2Tests
automated_filter: FullyQualifiedName~CostAlertPresentationResolverTests
automated_filter: FullyQualifiedName~GoldenAlertReceiptTests
automated_filter: FullyQualifiedName~AlertEvaluationApplicationTests
automated_filter: FullyQualifiedName~SqliteAlertEngineStoreV2Tests
automated_filter: FullyQualifiedName~SqliteAlertEngineQueryStoreTests
automated_filter: FullyQualifiedName~AlertCenter
automated_filter: FullyQualifiedName~AlertLifecycle
automated_filter: FullyQualifiedName~SanitizedExport
automated_filter: FullyQualifiedName~RuntimeBackup
automated_filter: FullyQualifiedName~LocalMonitorScriptTests

## RED and failure history

red_failure_count: 1
red_failure: wrong_surface | command=pwsh scripts\validation\issue-95\test-evidence-chain.ps1 -VerifierPath scripts\validation\issue-95\verify-evidence-chain.ps1 -MatrixValidatorPath scripts\validation\issue-91\validate-matrix.ps1 -MatrixFixturePath docs\specifications\contracts\cost-analytics\v1\issue-91-validation-row-contract.json | observed=failed:row_contract_mismatch | expected_code=row_contract_mismatch | executable_fixture=wrong_surface | corrected_by=7227c1f8c80b6c5d26db62578e45ba313f695759

Earlier failures remain evidence rather than being erased by the final pass:

- The original per-case fixture-repository recreation exceeded the explicit
  60-second self-test boundary and was corrected at `28bd1679`.
- At `28bd1679b9d5f1b17148edb8f611ad1fe7f5e8b1`, the valid full aggregate
  failed only
  `EvidenceChainVerifierSelfTestCoversAncestryDiffAndHashFailures`.
  Targeted execution passed in about 37 seconds. Process observation showed
  at least 1,496 short-lived Git instances directly and at least 2,122 during
  the aggregate, with no persistent hung child. The cause was aggregate
  process-launch contention, not an xUnit timeout, commit-message semantics,
  missing Chromium, or an accepted retry. Persistent `git cat-file --batch`
  reads preserved all 31 cases, the exact stdout/stderr semantics, and the
  fixed 60-second contract.
- Review then rejected an unrelated 16 MiB committed-object limit. Candidate
  `6e7c262cd07290d29f5d9043726592d8d546c90d` removed only that semantic
  restriction while keeping fail-closed integer handling.
- The first full aggregate at `6e7c262c` failed the unchanged historical
  maximum-window wall-clock assertion at 11.2116941 seconds. Five isolated
  runs were about four seconds, and controlled CPU contention doubled the
  duration. Candidate `c1de4033` moved the unchanged 10-second, 200+1 Session,
  200 handoff production fixture outside the parallel xUnit pool. No timeout,
  workload, assertion, case, or production behavior was changed.
- The first evidence chain
  `c1de4033 -> 033aad078896b67ef630d565c7e8ebf270f394a8 -> d1492d08c44e88347da982e8aaad37627e671561`
  failed the canonical verifier with
  `red_failure_correction_not_executable`. The verifier had collapsed a
  multi-line `diff-tree` result into one string. Candidate
  `7227c1f8c80b6c5d26db62578e45ba313f695759` added a multi-path correction
  fixture and explicitly split the path result; the unchanged 31 cases then
  passed with exact stdout and empty stderr.
- The first full aggregate at `7227c1f8` still failed the explicit 60-second
  self-test with 3,425 Local Monitor passes, one failure, and one OS-only
  skip. The direct self-test passed in about 20 seconds. Candidate
  `88e4c40dd7e76d6a80bb87a17c4e5acd88081bf8` isolated the unchanged
  validation contract class from unrelated xUnit collection contention.
  It did not change the timeout, 31 cases, repository reuse, or evidence
  semantics.
- Preserved #75 overview/terminal-state isolation and #88 raw-replay
  concurrency failures remain documented in their Issue histories and are
  ancestors of the final candidate.

## OS-specific security coverage

validation_os: windows
applicable_os_security_tests: file_symlink=passed,directory_symlink=passed,windows_open_mutation=passed,windows_normalized_segment=passed
not_applicable_os_security_tests: linux_fifo=not_applicable
applicable_security_prerequisite_skips: 0

The detailed provider run passed 18 applicable cases and skipped only the
registered Linux-only FIFO case. Scanner self-test output was
`transformation_cases=118`, `negative_cases=5`, and
`self_test_result=PASS`.

## 91-A-095

Classification: passed. Exact candidate tests covered pricing persistence,
query and configuration surfaces, recalculation generations, cost API/UI,
23 Cost UI Playwright cases, alert receipt v2, Alert Center and lifecycle,
fresh and upgraded migrations, sanitized export, runtime backup, and restart.

## 91-S-095

Classification: passed. Canonical identity and bytes, loopback API
boundaries, private override confinement, malicious and future input
rejection, archive safety, Windows reparse/open-mutation/normalized-segment
coverage, and repository-safe scanner self-tests all passed.

## 91-L-095

91-L-095: blocked_external
severity: high
blocker: Reviewed positive source/version-to-pricing mappings and separate live authorization are unavailable for both GitHub Copilot and Claude Code.
retry_condition: Obtain reviewed positive source mappings for both GitHub Copilot and Claude Code plus separate live authorization; freeze a new candidate; persist a genuine positive estimate for each provider; evaluate a configured budget; read the resulting cost_receipt_v2 and lifecycle through Alert Center; then run repository-safe leak scanning.
unverified_capability: Positive estimate persistence, configured budget evaluation, and Alert Center cost_receipt_v2/lifecycle readback for both GitHub Copilot and Claude Code.

## Live validation

No genuine GitHub Copilot or Claude Code positive pricing mapping was
available, and no separate content-capture authorization was inferred.
Synthetic fixtures were not promoted to live evidence. The row therefore
remains `blocked_external/high`, which produces
`release_ready_with_external_blockers` without claiming the unverified live
capability.

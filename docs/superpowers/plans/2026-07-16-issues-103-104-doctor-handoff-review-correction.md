# Issues #103/#104 Doctor Handoff Plan Review Correction

This correction supersedes only the bundled source-implementation test shown in
`2026-07-16-issues-103-104-doctor-handoff.md`. All other task boundaries,
production code, commands, and constraints remain unchanged.

## Finding

The original plan required one test to observe all three manifest-backed
surfaces at once. After #103 implemented its two GitHub Copilot handoffs, that
test would still fail for the Claude Code handoff owned by #104. Conversely,
#104 could not make the test GREEN without #103. The test therefore obscured
parallel ownership and prevented either worktree from independently verifying
its own deliverable.

## Corrected Task 2 RED tests

Replace the bundled
`ManifestBackedSourceHandoffs_AreImplementedOutsideDoctorCore` test with three
facts using common reflection/discovery helpers:

```csharp
[Fact]
public void GitHubCopilotVsCodeSourceHandoff_IsImplementedOutsideDoctorCore() =>
    AssertSourceHandoff("github-copilot-vscode");

[Fact]
public void GitHubCopilotCliSourceHandoff_IsImplementedOutsideDoctorCore() =>
    AssertSourceHandoff("github-copilot-cli");

[Fact]
public void ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore() =>
    AssertSourceHandoff("claude-code");
```

The common discovery scans the Doctor, Config CLI, and Local Monitor production
assemblies, rejects every concrete implementation in the Doctor assembly,
requires exactly one `DoctorSourceHandoffAttribute` on each implementation, and
requires exactly one registration for the requested surface.

## Corrected G0 checkpoint

- Four shared contract tests are expected GREEN.
- The two `GitHubCopilot*SourceHandoff` facts are intentionally RED and owned by
  #103.
- `ClaudeCodeSourceHandoff_IsImplementedOutsideDoctorCore` is intentionally RED
  and owned by #104.
- A compile error or any unrelated failure is not an acceptable RED checkpoint.

## Corrected parallel handoff

- #103 may run and turn GREEN only its two named facts while #104 remains RED.
- #104 may run and turn GREEN only its named fact while #103 remains RED.
- The full `DoctorSourceHandoffContractTests` class becomes GREEN only after the
  two implementation branches are integrated.
- Neither branch edits the expected surface values to suppress a test owned by
  the other Issue.

The executable test file and
`docs/specifications/interfaces/source-specific-doctor-handoff.md` are the
reviewed authority for this correction.

# Issues #103/#104 Doctor Handoff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the source-neutral Doctor handoff contract and add an intentional RED coverage gate for the GitHub Copilot and Claude Code source implementations owned by Issues #103 and #104.

**Architecture:** The existing Doctor assembly receives two contribution records, one composer, one discovery attribute, and one interface. The contribution record shapes enforce the five setup-owned and seven runtime-owned fact families. Reflection-based contract tests are written before the production types, then the shared contract turns GREEN while one implementation-coverage test intentionally remains RED until #103/#104 add concrete source handoffs outside the Doctor core.

**Tech Stack:** .NET 10, xUnit, existing `CopilotAgentObservability.Doctor`, Config CLI, and Local Monitor assemblies.

## Global Constraints

- Follow `AGENTS.md` and `docs/agent-guides/repository-workflow.md`.
- Canonical behavior is fixed in `docs/specifications/interfaces/source-specific-doctor-handoff.md`.
- Do not add a Doctor state, reason code, severity, retryability, next action, evidence class, evidence kind, CLI command, HTTP route, storage table, dependency, fallback, or heuristic selector.
- Do not move setup/runtime authority between the five and seven contribution families.
- Completion snapshots must contain an empty `observations` list.
- Latest trace, latest Session, repository, workspace, cwd, process identity, trace ID alone, and timestamp proximity remain forbidden.
- Use only synthetic metadata. Do not persist or report raw prompt/response/tool content, PII, credentials, authorization values, paths, or payload fragments.
- G0 intentionally ends with one named RED test for missing #103/#104 implementations. Build must be GREEN; compile errors and unrelated test failures are not accepted as RED evidence.
- Commit to `codex/issues-103-104-doctor-handoff-contract`. Do not create a PR or merge the branch.

---

### Task 1: Canonical handoff specification

**Files:**
- Create: `docs/specifications/interfaces/source-specific-doctor-handoff.md`
- Modify: `docs/specifications/README.md`
- Create: `docs/superpowers/specs/2026-07-16-issues-103-104-doctor-handoff-design.md`

**Interfaces:**
- Consumes: Issue #102 `DoctorFactSnapshot`, `DoctorObservation`, `DoctorEvidenceCandidate`, and `DoctorVerification`.
- Produces: the exact type shapes and authority rules implemented by Tasks 3 and 4.

- [x] **Step 1: Record the three considered approaches and select source-neutral contributions plus a composer.**
- [x] **Step 2: Pin five setup-owned and seven runtime-owned fact families.**
- [x] **Step 3: Pin surface-scoped verification with null Doctor adapters for `github-copilot-vscode`, `github-copilot-cli`, and `claude-code`.**
- [x] **Step 4: Pin direct versus persisted-completion observation rules.**
- [x] **Step 5: Index the new canonical interface.**

---

### Task 2: Write the executable RED contract tests first

**Files:**
- Create: `tests/CopilotAgentObservability.Doctor.Tests/DoctorSourceHandoffContractTests.cs`

**Interfaces:**
- Consumes: existing Doctor facts, verification types, Config CLI assembly, and Local Monitor assembly.
- Produces: a reflection-based contract that compiles before the new production types exist.

- [ ] **Step 1: Add the complete test file below without adding production code.**

```csharp
using System.Reflection;

namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorSourceHandoffContractTests
{
    private const string InvalidCompositionMessage =
        "Source handoff produced an invalid Doctor fact snapshot.";
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-16T00:00:00.0000000Z");

    [Fact]
    public void DirectComposition_MapsFixedAuthorityAndPreservesObservations()
    {
        var setup = CreateSetupContribution();
        var runtime = CreateRuntimeContribution();
        var observations = new[]
        {
            new DoctorObservation(
                "github-copilot-vscode",
                null,
                DoctorEvidenceClass.RealSource,
                DoctorEvidenceKind.Ingest,
                "ingest-receipt-1",
                ObservedAt),
        };

        var snapshot = InvokeDirect(
            "github-copilot-vscode",
            null,
            ObservedAt,
            setup,
            runtime,
            observations);

        Assert.Equal(DoctorSchemaVersions.FactsV1, snapshot.SchemaVersion);
        Assert.Equal("github-copilot-vscode", snapshot.SourceSurface);
        Assert.Null(snapshot.ExpectedSourceAdapter);
        Assert.Null(snapshot.VerificationId);
        Assert.Equal(observations, snapshot.Observations);
        Assert.Equal(new InstallAndSourceVersionFacts(
            MonitorInstallStatus.Installed,
            SourceVersionStatus.Supported,
            SourceFeatureStatus.Available), snapshot.InstallAndSourceVersion);
        Assert.Equal(new ProcessReceiverAndPortFacts(
            MonitorProcessStatus.Running,
            ReceiverBindStatus.Bound,
            PortOwnerStatus.Monitor), snapshot.ProcessReceiverAndPort);
        Assert.Equal(new SourceEffectiveConfigurationFacts(
            EndpointAlignmentStatus.Match), snapshot.SourceEffectiveConfiguration);
        Assert.Equal(new EndpointReachabilityFacts(
            ReachabilityStatus.Reachable), snapshot.EndpointReachability);
        Assert.Equal(new ProtocolAndSignalCompatibilityFacts(
            ProtocolStatus.HttpProtobuf,
            TraceSignalStatus.Enabled), snapshot.ProtocolAndSignalCompatibility);
        Assert.Equal(new SourceVersionAndSchemaDiagnosticsFacts(
            SourceCompatibilityStatus.Supported,
            SchemaStatus.Matching), snapshot.SourceVersionAndSchemaDiagnostics);
        Assert.Equal(new LastIngestFacts(LastIngestOutcome.Accepted), snapshot.LastIngest);
        Assert.Equal(new RawPersistenceFacts(RawPersistenceOutcome.Persisted), snapshot.RawPersistence);
        Assert.Equal(new ProjectionFacts(ProjectionOutcome.Completed), snapshot.Projection);
        Assert.Equal(new ExactSessionBindingFacts(
            ExactSessionBindingRequirement.Required,
            ExactSessionBindingOutcome.ExactBound), snapshot.ExactSessionBinding);
        Assert.Equal(new CompletenessAndContentFacts(
            DoctorCompleteness.Full,
            ContentCaptureStatus.Enabled,
            RawAccessStatus.Available), snapshot.CompletenessAndContent);
        Assert.Equal(new RestartOrNewProcessFacts(
            RestartRequirement.NotRequired), snapshot.RestartOrNewProcess);
    }

    [Fact]
    public void VerificationComposition_UsesVerificationIdentityAndNoCallerObservations()
    {
        var verification = new DoctorVerification(
            "01890abc-def0-7000-8000-000000000001",
            "claude-code",
            null,
            DoctorVerificationState.Active,
            1,
            ObservedAt,
            ObservedAt.AddMinutes(5),
            null,
            null,
            []);

        var snapshot = InvokeCompletion(
            verification,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution());

        Assert.Equal("claude-code", snapshot.SourceSurface);
        Assert.Null(snapshot.ExpectedSourceAdapter);
        Assert.Equal(verification.VerificationId, snapshot.VerificationId);
        Assert.Empty(snapshot.Observations);
    }

    [Fact]
    public void InvalidComposition_UsesFixedSanitizedError()
    {
        var observations = new[]
        {
            new DoctorObservation(
                "github-copilot-vscode",
                null,
                DoctorEvidenceClass.RealSource,
                DoctorEvidenceKind.Ingest,
                "prompt: secret-value",
                ObservedAt),
        };

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeDirect(
            "github-copilot-vscode",
            null,
            ObservedAt,
            CreateSetupContribution(),
            CreateRuntimeContribution(),
            observations));
        var argument = Assert.IsType<ArgumentException>(exception.InnerException);

        Assert.Equal(InvalidCompositionMessage, argument.Message);
        Assert.DoesNotContain("secret-value", argument.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", argument.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorCoreDefinesNoSourceSpecificDoctorEnum()
    {
        var prohibited = typeof(DoctorFactSnapshot).Assembly.GetTypes()
            .Where(type => type.IsEnum)
            .Where(type =>
                type.Name.Contains("GitHub", StringComparison.OrdinalIgnoreCase)
                || type.Name.Contains("Copilot", StringComparison.OrdinalIgnoreCase)
                || type.Name.Contains("Claude", StringComparison.OrdinalIgnoreCase))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(prohibited);
    }

    [Fact]
    public void ManifestBackedSourceHandoffs_AreImplementedOutsideDoctorCore()
    {
        var doctorAssembly = typeof(DoctorFactSnapshot).Assembly;
        var interfaceType = RequireDoctorType("IDoctorSourceHandoff");
        var attributeType = RequireDoctorType("DoctorSourceHandoffAttribute");
        var implementationTypes = new[]
        {
            typeof(CliApplication).Assembly,
            typeof(MonitorHost).Assembly,
        }
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => !type.IsAbstract && interfaceType.IsAssignableFrom(type))
        .ToArray();

        Assert.DoesNotContain(implementationTypes, type => type.Assembly == doctorAssembly);

        var actualSurfaces = implementationTypes
            .Select(type => type.GetCustomAttributes(attributeType, inherit: false).SingleOrDefault())
            .Where(attribute => attribute is not null)
            .Select(attribute => Assert.IsType<string>(
                attributeType.GetProperty("SourceSurface")!.GetValue(attribute)))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["claude-code", "github-copilot-cli", "github-copilot-vscode"],
            actualSurfaces);
    }

    private static object CreateSetupContribution() => Activator.CreateInstance(
        RequireDoctorType("DoctorSetupFactContribution"),
        new InstallAndSourceVersionFacts(
            MonitorInstallStatus.Installed,
            SourceVersionStatus.Supported,
            SourceFeatureStatus.Available),
        new SourceEffectiveConfigurationFacts(EndpointAlignmentStatus.Match),
        new EndpointReachabilityFacts(ReachabilityStatus.Reachable),
        new ProtocolAndSignalCompatibilityFacts(
            ProtocolStatus.HttpProtobuf,
            TraceSignalStatus.Enabled),
        new RestartOrNewProcessFacts(RestartRequirement.NotRequired))!;

    private static object CreateRuntimeContribution() => Activator.CreateInstance(
        RequireDoctorType("DoctorRuntimeFactContribution"),
        new ProcessReceiverAndPortFacts(
            MonitorProcessStatus.Running,
            ReceiverBindStatus.Bound,
            PortOwnerStatus.Monitor),
        new SourceVersionAndSchemaDiagnosticsFacts(
            SourceCompatibilityStatus.Supported,
            SchemaStatus.Matching),
        new LastIngestFacts(LastIngestOutcome.Accepted),
        new RawPersistenceFacts(RawPersistenceOutcome.Persisted),
        new ProjectionFacts(ProjectionOutcome.Completed),
        new ExactSessionBindingFacts(
            ExactSessionBindingRequirement.Required,
            ExactSessionBindingOutcome.ExactBound),
        new CompletenessAndContentFacts(
            DoctorCompleteness.Full,
            ContentCaptureStatus.Enabled,
            RawAccessStatus.Available))!;

    private static DoctorFactSnapshot InvokeDirect(
        string sourceSurface,
        string? sourceAdapter,
        DateTimeOffset observedAt,
        object setup,
        object runtime,
        IReadOnlyList<DoctorObservation> observations) =>
        (DoctorFactSnapshot)InvokeComposer(
            "ComposeDirectEvaluation",
            sourceSurface,
            sourceAdapter,
            observedAt,
            setup,
            runtime,
            observations)!;

    private static DoctorFactSnapshot InvokeCompletion(
        DoctorVerification verification,
        DateTimeOffset observedAt,
        object setup,
        object runtime) =>
        (DoctorFactSnapshot)InvokeComposer(
            "ComposeVerificationCompletion",
            verification,
            observedAt,
            setup,
            runtime)!;

    private static object? InvokeComposer(string methodName, params object?[] arguments)
    {
        var composer = RequireDoctorType("DoctorSourceHandoffComposer");
        var method = Assert.Single(composer.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(candidate => candidate.Name == methodName));
        return method.Invoke(null, arguments);
    }

    private static Type RequireDoctorType(string name)
    {
        var type = typeof(DoctorFactSnapshot).Assembly.GetType(
            $"CopilotAgentObservability.Doctor.{name}",
            throwOnError: false,
            ignoreCase: false);
        return Assert.IsType<Type>(type);
    }
}
```

- [ ] **Step 2: Run the focused test before production code.**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorSourceHandoffContractTests
```

Expected: test assembly compiles; the new tests fail because the shared contract types are absent. `DoctorCoreDefinesNoSourceSpecificDoctorEnum` remains GREEN. Record exact test counts and failure names.

- [ ] **Step 3: Commit the RED checkpoint.**

```powershell
git add tests\CopilotAgentObservability.Doctor.Tests\DoctorSourceHandoffContractTests.cs
git commit -m "Issues #103/#104: test(doctor): pin source handoff RED contract"
```

---

### Task 3: Implement the source-neutral handoff contract

**Files:**
- Create: `src/CopilotAgentObservability.Doctor/DoctorSourceHandoff.cs`

**Interfaces:**
- Consumes: the exact RED reflection contract from Task 2.
- Produces: contribution records, discovery attribute, interface, and composer for #103/#104.

- [ ] **Step 1: Add the production file below.**

```csharp
namespace CopilotAgentObservability.Doctor;

public sealed record DoctorSetupFactContribution(
    InstallAndSourceVersionFacts? InstallAndSourceVersion,
    SourceEffectiveConfigurationFacts? SourceEffectiveConfiguration,
    EndpointReachabilityFacts? EndpointReachability,
    ProtocolAndSignalCompatibilityFacts? ProtocolAndSignalCompatibility,
    RestartOrNewProcessFacts? RestartOrNewProcess);

public sealed record DoctorRuntimeFactContribution(
    ProcessReceiverAndPortFacts? ProcessReceiverAndPort,
    SourceVersionAndSchemaDiagnosticsFacts? SourceVersionAndSchemaDiagnostics,
    LastIngestFacts? LastIngest,
    RawPersistenceFacts? RawPersistence,
    ProjectionFacts? Projection,
    ExactSessionBindingFacts? ExactSessionBinding,
    CompletenessAndContentFacts? CompletenessAndContent);

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DoctorSourceHandoffAttribute : Attribute
{
    public DoctorSourceHandoffAttribute(string sourceSurface)
    {
        SourceSurface = sourceSurface;
    }

    public string SourceSurface { get; }
}

public interface IDoctorSourceHandoff
{
    string SourceSurface { get; }

    string? ExpectedSourceAdapter { get; }

    DoctorFactSnapshot ComposeDirectEvaluation(
        DateTimeOffset observedAt,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts,
        IReadOnlyList<DoctorObservation> observations);

    DoctorFactSnapshot ComposeVerificationCompletion(
        DoctorVerification verification,
        DateTimeOffset observedAt,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts);
}

public static class DoctorSourceHandoffComposer
{
    private const string InvalidCompositionMessage =
        "Source handoff produced an invalid Doctor fact snapshot.";

    public static DoctorFactSnapshot ComposeDirectEvaluation(
        string sourceSurface,
        string? expectedSourceAdapter,
        DateTimeOffset observedAt,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts,
        IReadOnlyList<DoctorObservation> observations)
    {
        if (setupFacts is null || runtimeFacts is null || observations is null)
        {
            throw InvalidComposition();
        }

        return Compose(
            sourceSurface,
            expectedSourceAdapter,
            observedAt,
            verificationId: null,
            setupFacts,
            runtimeFacts,
            observations);
    }

    public static DoctorFactSnapshot ComposeVerificationCompletion(
        DoctorVerification verification,
        DateTimeOffset observedAt,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts)
    {
        if (verification is null
            || verification.State != DoctorVerificationState.Active
            || !DoctorValidation.IsValidVerification(verification)
            || setupFacts is null
            || runtimeFacts is null)
        {
            throw InvalidComposition();
        }

        return Compose(
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            observedAt,
            verification.VerificationId,
            setupFacts,
            runtimeFacts,
            []);
    }

    private static DoctorFactSnapshot Compose(
        string sourceSurface,
        string? expectedSourceAdapter,
        DateTimeOffset observedAt,
        string? verificationId,
        DoctorSetupFactContribution setupFacts,
        DoctorRuntimeFactContribution runtimeFacts,
        IReadOnlyList<DoctorObservation> observations)
    {
        var snapshot = new DoctorFactSnapshot(
            DoctorSchemaVersions.FactsV1,
            sourceSurface,
            expectedSourceAdapter,
            observedAt,
            verificationId,
            observations,
            setupFacts.InstallAndSourceVersion,
            runtimeFacts.ProcessReceiverAndPort,
            setupFacts.SourceEffectiveConfiguration,
            setupFacts.EndpointReachability,
            setupFacts.ProtocolAndSignalCompatibility,
            runtimeFacts.SourceVersionAndSchemaDiagnostics,
            runtimeFacts.LastIngest,
            runtimeFacts.RawPersistence,
            runtimeFacts.Projection,
            runtimeFacts.ExactSessionBinding,
            runtimeFacts.CompletenessAndContent,
            setupFacts.RestartOrNewProcess);

        if (!DoctorValidation.IsValidFactSnapshot(snapshot))
        {
            throw InvalidComposition();
        }

        return snapshot;
    }

    private static ArgumentException InvalidComposition() =>
        new(InvalidCompositionMessage);
}
```

- [ ] **Step 2: Run the focused test again.**

Run the same command as Task 2.

Expected:

- four shared contract tests GREEN;
- `ManifestBackedSourceHandoffs_AreImplementedOutsideDoctorCore` RED because no
  #103/#104 concrete implementation exists;
- no compile failure or other test failure.

- [ ] **Step 3: Run the solution build.**

```powershell
dotnet build CopilotAgentObservability.slnx
```

Expected: PASS with zero errors.

- [ ] **Step 4: Commit the shared implementation.**

```powershell
git add src\CopilotAgentObservability.Doctor\DoctorSourceHandoff.cs
git commit -m "Issues #103/#104: feat(doctor): add source handoff contract"
```

Commit body: explain that fixed setup/runtime authority and empty completion observations prevent #103/#104 from diverging while concrete source producers remain intentionally absent.

---

### Task 4: Record and review the intentional RED gate

**Files:**
- Create: `docs/sprints/issues-103-104-doctor-handoff/ledger.md`
- Modify only if a contradiction is found: the design, canonical handoff spec, or plan.

**Interfaces:**
- Consumes: Tasks 1 through 3.
- Produces: repository-safe evidence for the #103/#104 worktree split.

- [ ] **Step 1: Run focused tests and record exact results.**
- [ ] **Step 2: Run `dotnet build CopilotAgentObservability.slnx`.**
- [ ] **Step 3: Run `pwsh scripts\test\install-playwright-chromium.ps1`.**
- [ ] **Step 4: Run the full solution test command once.**

```powershell
dotnet test CopilotAgentObservability.slnx
```

Expected: the same single intentional source-implementation coverage failure; no compile failure and no unrelated failing test. A different failure set is not an acceptable RED checkpoint.

- [ ] **Step 5: Review the diff against the canonical contract.**

Check:

- exactly five setup and seven runtime properties;
- no source-specific enum or state in the Doctor assembly;
- no candidate write route/command;
- completion observations are always empty;
- invalid composition uses the fixed sanitized message;
- the coverage test scans only production assemblies outside Doctor core;
- no prompt/response/tool body, PII, credential, path, or payload fragment;
- no placeholder, TODO, fallback, sleep, polling, or retry loop.

- [ ] **Step 6: Commit the ledger after review.**

```powershell
git add docs\sprints\issues-103-104-doctor-handoff\ledger.md
git commit -m "Issues #103/#104: docs(doctor): record G0 RED handoff gate"
```

## Handoff to Issues #103 and #104

- #103 adds concrete `IDoctorSourceHandoff` implementations annotated for
  `github-copilot-vscode` and `github-copilot-cli`.
- #104 adds a concrete implementation annotated for `claude-code`.
- Each implementation returns `ExpectedSourceAdapter = null` and delegates both
  composition methods to `DoctorSourceHandoffComposer`.
- The shared test file is not edited by either issue except to correct a proven
  G0 contract defect. Each issue turns only its missing surface rows GREEN.
- After all three rows are GREEN, run the repository-pinned build, Playwright
  bootstrap, and full test commands before integration.

using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.Tests;

internal static class SkillInvocationV2TestIdentity
{
    internal static CertifiedSkillProducerIdentityV1 V1065 { get; } = Create("1.0.65");
    internal static CertifiedSkillProducerIdentityV1 V1075 { get; } = Create("1.0.75");

    internal static CertifiedSkillProducerIdentityV1 Create(string version) => new(
        version, 3, "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1",
        "github-copilot-sdk.skill-invoked.normalize.v2", "github-copilot-sdk.skill-invoked.v1",
        "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c", 2);
}

internal static class SkillInvocationNormalizedJsonTestWriter
{
    internal const int MaxProducerBodyBytes = SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes;

    internal static bool TryWrite(string? nativeSessionId, GitHub.Copilot.SkillInvokedEvent? sourceEvent, out byte[]? body) =>
        TryWrite(nativeSessionId, sourceEvent, sourceEvent?.Data?.Content, out body);

    internal static bool TryWrite(string? nativeSessionId, GitHub.Copilot.SkillInvokedEvent? sourceEvent,
        string? certifiedDefinitionContent, out byte[]? body) =>
        SkillInvocationNormalizedJsonV1.TryWrite(nativeSessionId, sourceEvent, certifiedDefinitionContent,
            new TestCapability(SkillInvocationV2TestIdentity.V1065), out body);

    internal static bool TryWriteCancellable(string? nativeSessionId, GitHub.Copilot.SkillInvokedEvent? sourceEvent,
        CancellationToken cancellationToken, out byte[]? body) =>
        TryWriteCancellable(nativeSessionId, sourceEvent, sourceEvent?.Data?.Content, cancellationToken, out body);

    internal static bool TryWriteCancellable(string? nativeSessionId, GitHub.Copilot.SkillInvokedEvent? sourceEvent,
        string? certifiedDefinitionContent, CancellationToken cancellationToken, out byte[]? body) =>
        SkillInvocationNormalizedJsonV1.TryWriteCancellable(nativeSessionId, sourceEvent, certifiedDefinitionContent,
            new TestCapability(SkillInvocationV2TestIdentity.V1065), cancellationToken, out body);

    private sealed record TestCapability(CertifiedSkillProducerIdentityV1 CertifiedIdentity)
        : ISkillInvocationV2RuntimeCapability;
}

public sealed class SkillInvocationV2VersionIdentityContractTests
{
    [Fact]
    public void D090CurrentAuthority_CorrectsChronologyWithoutRelaxingIdentityOrFutureGates()
    {
        var requirements = File.ReadAllText(RepositoryPath("docs", "requirements.md"));
        var specification = File.ReadAllText(RepositoryPath("docs", "spec.md"));
        var snapshotInterface = File.ReadAllText(RepositoryPath(
            "docs", "specifications", "interfaces", "skill-invocation-snapshot.md"));
        var decisions = File.ReadAllText(RepositoryPath("docs", "decisions.md"));
        var task = File.ReadAllText(RepositoryPath("docs", "task.md"));
        var evidenceReadme = File.ReadAllText(RepositoryPath(
            "docs", "sprints", "issue-158-skill-invocation-snapshot", "README.md"));
        var liveEvidence = File.ReadAllText(RepositoryPath(
            "docs", "sprints", "issue-158-skill-invocation-snapshot", "milestones",
            "M1-owned-session-producer", "live-validation.md"));

        var requirementsSummary = Section(requirements, "- Skill invocation snapshot foundation", "- Codex App discovery");
        var requirementsD089 = Section(requirements, "### D089", "## 4. 非目的");
        Assert.False(HasStaleCurrentReleaseChronology(requirementsSummary));
        Assert.Contains("same-client post-implementation T0b", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.Contains("future registry revisionまたはproducer/startup implementationでは実装前T0bをmandatory gate", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.Contains("exact-final-candidate Windows/Linux platform/live refreshは`711b3e16283796d7d8dcebe8733fdb63dbc86df6`でcomplete", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.Contains("pinned full validation、fresh independent review、release/merge/closureは未完了", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.DoesNotContain("exact-final-candidate platform/live refresh、full validation、review、releaseは未完了", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.Contains("r0002 deterministically admits exactly `1.0.65` and `1.0.75`", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.Contains("owned-session importer implementationとexact-final-candidate Windows/Linux platform/live refresh", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.DoesNotContain("implementation/platform/live/full-validation/review/release evidenceは未完了", requirementsSummary, StringComparison.Ordinal);
        Assert.Contains("mandatory T0b and final signed-in Windows live tuple is exactly bundled `1.0.75` / protocol `3`", NormalizeWhitespace(requirementsD089), StringComparison.Ordinal);
        Assert.Contains("owned-session importer implementation and exact-final-candidate Windows/Linux platform/live refresh are complete", NormalizeWhitespace(requirementsD089), StringComparison.Ordinal);
        Assert.Contains("pinned full validation, fresh independent review, release, merge, and closure remain pending", NormalizeWhitespace(requirementsD089), StringComparison.Ordinal);
        Assert.Contains("D089", specification, StringComparison.Ordinal);
        Assert.Contains("mandatory live lane is exactly bundled `1.0.75`, protocol `3`", NormalizeWhitespace(specification), StringComparison.Ordinal);

        var specCurrent = Section(specification, "D086 supersedes only D083", "The exact checked-in byte authorities");
        Assert.False(HasStaleCurrentReleaseChronology(specCurrent));
        Assert.Contains("retracts the contradicted claim that current-release T0b preceded r0002 and producer implementation", NormalizeWhitespace(specCurrent), StringComparison.Ordinal);
        Assert.Contains("Future registry revisions or producer/startup implementations require T0b before implementation", NormalizeWhitespace(specCurrent), StringComparison.Ordinal);
        Assert.Contains("completed exact-final-candidate platform evidence; pinned full validation and fresh independent review remain release gates", NormalizeWhitespace(specCurrent), StringComparison.Ordinal);
        Assert.DoesNotContain("platform/live evidence refresh, final validation, independent review, and release remain pending", NormalizeWhitespace(specCurrent), StringComparison.Ordinal);
        var specCrossReference = Section(specification, "The independent `skill_projection:1` component", "The SDK transport and retained raw snapshot");
        Assert.False(HasStaleCurrentReleaseChronology(specCrossReference));
        Assert.Contains("exact bundled `1.0.75` / protocol `3` same-client live proof was accepted after the unchanged exact two-entry r0002 and owned-session producer", NormalizeWhitespace(specCrossReference), StringComparison.Ordinal);
        Assert.Contains("#158 atomic writer/importer implementation is complete", NormalizeWhitespace(specCrossReference), StringComparison.Ordinal);
        Assert.DoesNotContain("must still implement", specCrossReference, StringComparison.Ordinal);

        var currentProducer = Section(snapshotInterface, "### D086 current producer contract", "### D087 current content authority");
        var interfaceStatus = Section(snapshotInterface, "Status:", "This specification is the detailed authority");
        Assert.Contains("owned-session importer implementation and exact-final-candidate platform/live evidence complete", NormalizeWhitespace(interfaceStatus), StringComparison.Ordinal);
        Assert.Contains("pinned full validation and fresh independent review remain release gates", NormalizeWhitespace(interfaceStatus), StringComparison.Ordinal);
        Assert.DoesNotContain("platform/live evidence refresh, final validation, and review pending", NormalizeWhitespace(interfaceStatus), StringComparison.Ordinal);
        Assert.False(HasStaleCurrentReleaseChronology(currentProducer));
        Assert.Contains("versioned T0b preceded r0002 and producer implementation", NormalizeWhitespace(currentProducer), StringComparison.Ordinal);
        Assert.Contains("Future registry revisions or producer/startup implementations require T0b before implementation", NormalizeWhitespace(currentProducer), StringComparison.Ordinal);
        Assert.Contains("completed exact-final-candidate platform evidence; pinned full validation and fresh independent review remain release gates", NormalizeWhitespace(currentProducer), StringComparison.Ordinal);
        Assert.Contains("exact bundled `1.0.75`, protocol `3`", NormalizeWhitespace(currentProducer), StringComparison.Ordinal);
        var finalGates = Section(snapshotInterface, "## Required TDD slices and release gates", "## What is decided versus what still requires evidence");
        Assert.False(HasStaleCurrentReleaseChronology(finalGates));
        Assert.Contains("exact bundled `1.0.75`, protocol `3`", NormalizeWhitespace(finalGates), StringComparison.Ordinal);
        Assert.Contains("exact-final-candidate Windows/Linux live evidence was completed and recorded", NormalizeWhitespace(finalGates), StringComparison.Ordinal);
        Assert.Contains("Issue #158 M1 live validation", NormalizeWhitespace(finalGates), StringComparison.Ordinal);
        Assert.DoesNotContain("exact-final-candidate Windows/Linux live evidence remains pending", NormalizeWhitespace(finalGates), StringComparison.Ordinal);
        Assert.Contains("pinned full validation and review workflow", NormalizeWhitespace(finalGates), StringComparison.Ordinal);
        Assert.DoesNotContain("prove each exact r0002 tuple", finalGates, StringComparison.Ordinal);
        Assert.DoesNotContain("An unproved tuple is not admitted", finalGates, StringComparison.Ordinal);
        var remainingEvidence = Section(snapshotInterface, "## What is decided versus what still requires evidence", null);
        Assert.DoesNotContain("actual Windows/Linux native walker matrices", remainingEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-in bundled exact-tuple T0b/final live observation", remainingEvidence, StringComparison.Ordinal);
        Assert.Contains("#119 parser/handoff result; full repository validation; and code review", NormalizeWhitespace(remainingEvidence), StringComparison.Ordinal);

        var d086 = NormalizeWhitespace(Section(decisions, "## D086:", "## D085:"));
        Assert.False(HasStaleCurrentReleaseChronology(d086));
        Assert.Contains("As corrected by D090, this current release did not run versioned gate T0b before r0002 or producer startup code", d086, StringComparison.Ordinal);
        Assert.Contains("future registry revisions and producer/ startup implementations must do so", d086, StringComparison.Ordinal);
        Assert.Contains("On the same signed-in bundled client T0b must prove exact status Version and integer ProtocolVersion", d086, StringComparison.Ordinal);

        var d089 = Section(decisions, "## D089:", "## D090:");
        var normalizedD089 = NormalizeWhitespace(d089);
        Assert.Contains("Status: Accepted (2026-08-25)", normalizedD089, StringComparison.Ordinal);
        Assert.Contains("deterministic compatibility and admission coverage for `1.0.65`", normalizedD089, StringComparison.Ordinal);
        Assert.Contains("not the mandatory live lane", normalizedD089, StringComparison.Ordinal);
        Assert.Contains("exact and fail closed", normalizedD089, StringComparison.Ordinal);

        var d090 = NormalizeWhitespace(Section(decisions, "## D090:", null));
        Assert.False(HasStaleCurrentReleaseChronology(d090));
        Assert.Contains("fc5a0e890341093fa42eec9f05f4b95569ea634c", d090, StringComparison.Ordinal);
        Assert.Contains("007826104af146a0920e62939a47a2aa3503f86a", d090, StringComparison.Ordinal);
        Assert.Contains("1ddc79142ba6afc93074360a19992d0c1eee0774", d090, StringComparison.Ordinal);
        Assert.Contains("527c7b5f299296afefe13def783c08be121684b9", d090, StringComparison.Ordinal);
        Assert.Contains("retracted, not merely left unproven", d090, StringComparison.Ordinal);
        Assert.Contains("mandatory pre-implementation T0b", d090, StringComparison.Ordinal);
        Assert.Contains("exact version/protocol/SessionStart", d090, StringComparison.Ordinal);
        Assert.Contains("no-fallback", d090, StringComparison.Ordinal);
        Assert.Contains("exact-final-candidate Windows/Linux live refresh, final validation, independent review, release, merge, and Issue closure remain pending", d090, StringComparison.Ordinal);

        var legacyActivation = Section(snapshotInterface, "Historical D083 evidence", "## Group 6");
        Assert.Contains("Historical D083 evidence", NormalizeWhitespace(legacyActivation), StringComparison.Ordinal);
        Assert.Contains("superseded for the current release gate", NormalizeWhitespace(legacyActivation), StringComparison.Ordinal);

        var taskStatus = Section(task, "| Skill invocation snapshot (Issues #119/#157/#158)", "| Versioned pricing registry");
        Assert.False(HasStaleCurrentReleaseChronology(taskStatus));
        Assert.Contains("owned-session importer and exact-final-candidate platform-live refresh complete / final validation-review-release pending", NormalizeWhitespace(taskStatus), StringComparison.Ordinal);
        Assert.Contains("exact-final-candidate Windows/Linux live refreshも`711b3e16283796d7d8dcebe8733fdb63dbc86df6`で完了", NormalizeWhitespace(taskStatus), StringComparison.Ordinal);
        Assert.Contains("pinned full validation、fresh independent review、release/merge/closureは未完了", NormalizeWhitespace(taskStatus), StringComparison.Ordinal);
        Assert.DoesNotContain("exact-final-candidate platform-live refresh and final validation-review pending", NormalizeWhitespace(taskStatus), StringComparison.Ordinal);
        Assert.DoesNotContain("exact-final-candidate Windows/Linux live refresh、pinned full validation、fresh independent review、release evidenceは未完了", NormalizeWhitespace(taskStatus), StringComparison.Ordinal);

        var normalizedEvidenceReadme = NormalizeWhitespace(evidenceReadme);
        var normalizedLiveEvidence = NormalizeWhitespace(liveEvidence);
        const string finalCandidate = "711b3e16283796d7d8dcebe8733fdb63dbc86df6";
        const string priorCandidate = "527c7b5f299296afefe13def783c08be121684b9";
        Assert.Contains($"| Windows signed-in owned session | `{finalCandidate}` | 2026-08-25 | `passed` in one authorized attempt |", evidenceReadme, StringComparison.Ordinal);
        Assert.Contains($"| Linux WSL Ubuntu native ext4 | `{finalCandidate}` | 2026-08-25 | `passed` in one wrapper attempt |", evidenceReadme, StringComparison.Ordinal);
        Assert.DoesNotContain($"| Windows signed-in owned session | `{priorCandidate}`", evidenceReadme, StringComparison.Ordinal);
        Assert.DoesNotContain($"| Linux WSL Ubuntu native ext4 | `{priorCandidate}`", evidenceReadme, StringComparison.Ordinal);
        Assert.Contains($"`{priorCandidate}` is historical and superseded", normalizedEvidenceReadme, StringComparison.Ordinal);
        Assert.Contains("no cross-candidate inference is used", normalizedEvidenceReadme, StringComparison.Ordinal);
        Assert.Contains("schema `issue-158-live-validation.v1`, source application `1.0.75`, and protocol `3`", normalizedLiveEvidence, StringComparison.Ordinal);
        Assert.All(new[]
        {
            "| `retained_roots` | 1 |", "| `retained_skills` | 1 |", "| `probe_sessions` | 1 |",
            "| `execution_sessions` | 1 |", "| `user_invoked` | 1 |", "| `agent_invoked` | 1 |",
            "| `task_complete` | 1 |", "| `v2_imported` | 2 |", "| `v1_imported` | 2 |",
            "| `snapshot_rows` | 2 |"
        }, fact => Assert.Contains(fact, liveEvidence, StringComparison.Ordinal));
        Assert.All(new[]
        {
            "`operator_gate`", "`cli_override_absent`", "`retained_only_inventory`", "`exact_tool_union`",
            "`native_reproof`", "`current_generation`", "`metadata_route`", "`historical_route`",
            "`current_file_route`", "`shutdown_drain`", "`cleanup_complete`"
        }, check => Assert.Contains(check, liveEvidence, StringComparison.Ordinal));
        Assert.Contains("| `matrix_cases` | 6 |", liveEvidence, StringComparison.Ordinal);
        Assert.All(new[]
        {
            "`detached_clean_candidate`", "`kernel_supported`", "`native_ext4`",
            "`retained_root_reproof`", "`strict_utf8_read`", "`unsafe_path_rejected`",
            "`missing_rejected`", "`oversized_rejected`", "`binary_rejected`"
        }, check => Assert.Contains(check, liveEvidence, StringComparison.Ordinal));
        Assert.Contains("approximately 34.0 seconds", normalizedLiveEvidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approximately 41.69 seconds", normalizedLiveEvidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SDK `10.0.203`", liveEvidence, StringComparison.Ordinal);
        Assert.Contains("`installer_roots` and `lane_roots` were `0`", normalizedLiveEvidence, StringComparison.Ordinal);
        Assert.Contains("| `f7da5ad655cc62f04e22bac671ae6808be5a1780` | `succeeded_post_success_execution_evidence_prepared_invocation_count` |", liveEvidence, StringComparison.Ordinal);
        Assert.Contains("| `dc155cc87690956bd02b33a981125ceddadeff49` | `succeeded_post_success_execution_evidence_prepared_invocation_excess` |", liveEvidence, StringComparison.Ordinal);
        Assert.Contains("| `655da557af6615262a50c44cbd5c2f613ea5e25f` | `wrapper_test_output` |", liveEvidence, StringComparison.Ordinal);
        Assert.Contains("| `967d55f8e4e2ced2dd00b540f14e48825f891e8a` | `wrapper_test_output_pass_summary` |", liveEvidence, StringComparison.Ordinal);
        Assert.Contains($"run-windows-owned-session.ps1 -CandidateSha {finalCandidate} -OperatorAuthorized", liveEvidence, StringComparison.Ordinal);
        Assert.Contains($"run-linux-current-file-matrix.ps1 -CandidateSha {finalCandidate} -OperatorAuthorized", liveEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain($"run-windows-owned-session.ps1 -CandidateSha {priorCandidate}", liveEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain($"run-linux-current-file-matrix.ps1 -CandidateSha {priorCandidate}", liveEvidence, StringComparison.Ordinal);
        Assert.Contains("Pinned full validation, fresh final reviews, merge, release, and Issues #173/#158 closure remained pending", normalizedLiveEvidence, StringComparison.Ordinal);

        Assert.All(StaleCurrentReleaseChronologyPhrases, phrase =>
            Assert.True(HasStaleCurrentReleaseChronology(phrase)));
        Assert.False(HasStaleCurrentReleaseChronology(
            "D090 retracts the contradicted claim that current-release T0b preceded r0002 and producer implementation."));
    }

    private static bool HasStaleCurrentReleaseChronology(string section)
    {
        var normalized = NormalizeWhitespace(section);
        return StaleCurrentReleaseChronologyPhrases.Any(phrase =>
            normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] StaleCurrentReleaseChronologyPhrases =
    [
        "T0b must precede r0002",
        "T0b precedes r0002",
        "Versioned T0b precedes r0002",
        "T0b preceded the unchanged exact two-entry r0002",
        "Before r0002 or producer startup code, versioned gate T0b must prove",
        "does not relax the pre-r0002 and pre-producer sequencing",
        "Versioned T0b must first certify one signed-in bundled"
    ];

    [Fact]
    public void D087CanonicalSources_KeepCurrentR0002DistinctFromHistoricalR0001()
    {
        var requirements = File.ReadAllText(RepositoryPath("docs", "requirements.md"));
        var nonGoals = requirements[
            requirements.IndexOf("## 4. 非目的", StringComparison.Ordinal)..
            requirements.IndexOf("## 5. Data Requirements", StringComparison.Ordinal)];
        var ingestion = File.ReadAllText(RepositoryPath(
            "docs", "specifications", "layers", "telemetry-ingestion.md"));
        var snapshotInterface = File.ReadAllText(RepositoryPath(
            "docs", "specifications", "interfaces", "skill-invocation-snapshot.md"));

        Assert.DoesNotContain("D087", nonGoals, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow。\n\n- trace", nonGoals, StringComparison.Ordinal);
        Assert.Contains("github-copilot-sdk.skill-invoked.normalize.v2", ingestion, StringComparison.Ordinal);
        Assert.Contains("exact source versions 1.0.65", ingestion, StringComparison.Ordinal);
        Assert.Contains("and 1.0.75", ingestion, StringComparison.Ordinal);
        Assert.Contains("never falls back to r0001", ingestion, StringComparison.Ordinal);
        Assert.DoesNotContain("current r0002 value set:\n\n| Property | Exact r0001 rule", ingestion, StringComparison.Ordinal);
        Assert.Contains("Exact historical-r0001 / current-r0002 rule", snapshotInterface, StringComparison.Ordinal);
        Assert.Contains("Under historical r0001 and current r0002", snapshotInterface, StringComparison.Ordinal);
        Assert.Contains("exact callback-time certified native `currentProof.Content`", snapshotInterface, StringComparison.Ordinal);
        Assert.Contains("Every other payload field comes from typed `SkillInvokedData`", snapshotInterface, StringComparison.Ordinal);
        Assert.Contains("exact parsed request/facts producer tuple", snapshotInterface, StringComparison.Ordinal);
        Assert.Contains("current registry (r0002 here)", snapshotInterface, StringComparison.Ordinal);
        Assert.DoesNotContain("exact r0001 tuple to be accepted", snapshotInterface, StringComparison.Ordinal);
        Assert.DoesNotContain("r0001 `trace_id` and `span_id` are always literal null", snapshotInterface, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_LoadsCompleteTwoRevisionHistory_AndUsesOnlyR0002AsCurrent()
    {
        var registry = SkillInvocationV2ArtifactRegistry.Load();

        Assert.Equal(2, registry.CurrentRevision);
        Assert.Equal([1, 2], registry.History.Select(item => item.Revision));
        Assert.Equal("github-copilot-sdk.skill-invoked.normalize.v1",
            Assert.Single(registry.History[0].Entries).Tuple.NormalizationVersion);
        Assert.Equal(["1.0.65", "1.0.75"], registry.CurrentEntries.Select(item => item.Tuple.SourceApplicationVersion));
        Assert.All(registry.CurrentEntries, entry => Assert.Equal(
            "github-copilot-sdk.skill-invoked.normalize.v2", entry.Tuple.NormalizationVersion));
    }

    [Fact]
    public void R0002_IsPinnedCanonicalArtifact()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "SkillInvocationV2", "compatibility-registry-r0002.json");
        var bytes = File.ReadAllBytes(path);

        Assert.Equal(771, bytes.Length);
        Assert.Equal("e3da4e7334f4e1645de315820181d2752f71ddb9aeba4355a659d185165daaf6", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Theory]
    [InlineData("1.0.65")]
    [InlineData("1.0.75")]
    public void Admission_CertifiesExactCurrentTuple(string version)
    {
        var status = new CopilotRuntimeStatusObservationV1(version, 3, version);

        Assert.True(CopilotRuntimeIdentityCertifierV1.TryCertify(status, out var identity));
        Assert.Equal(version, identity!.SourceApplicationVersion);
        Assert.Equal(3, identity.ProtocolVersion);
        Assert.Equal(2, identity.RegistryRevision);
    }

    [Theory]
    [InlineData("1.0.76", 3, null)]
    [InlineData("1.0.75", 4, null)]
    [InlineData("1.0.75", 3, "1.0.65")]
    public void Admission_RejectsUncertifiedOrDriftedIdentity(string version, int protocol, string? sessionStart)
    {
        Assert.False(CopilotRuntimeIdentityCertifierV1.TryCertify(
            new CopilotRuntimeStatusObservationV1(version, protocol, sessionStart),
            out _));
    }

    [Fact]
    public void ImplementationOnlyTypes_AreInternal()
    {
        Assert.False(typeof(ISkillInvocationV2RuntimeCapability).IsPublic);
        Assert.False(typeof(SkillInvocationV2Parser).IsPublic);
        Assert.False(typeof(ParsedSkillInvocationV2Batch).IsPublic);
    }

    [Fact]
    public void ProductionSurface_HasNoIdentitySynthesizingOverloads()
    {
        Assert.DoesNotContain(typeof(CertifiedSkillProducerIdentityV1).GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            method => method.Name == "CurrentDefault");
        Assert.All(typeof(SkillInvocationNormalizedJsonV1).GetMethods(BindingFlags.Static | BindingFlags.Public), method =>
        {
            if (method.Name is "TryWrite" or "TryWriteCancellable")
            {
                Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(ISkillInvocationV2RuntimeCapability));
            }
        });
        var generationConstructor = Assert.Single(
            typeof(CopilotRuntimeGenerationV1).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Contains(generationConstructor.GetParameters(), parameter =>
            parameter.ParameterType == typeof(IAsyncDisposable) && !parameter.HasDefaultValue);
        Assert.DoesNotContain(typeof(CopilotRuntimeAdmissionV1).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name == "PublishAdmittedGeneration");
        Assert.DoesNotContain(typeof(SkillRuntimeCapabilityBridgeV1).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name == "ForwardCallbackAsync");
        Assert.Null(typeof(CopilotSdkSkillDiscoveryGateway).Assembly.GetType(
            "CopilotAgentObservability.LocalMonitor.SkillRuntime.CopilotSdkBundleClientV1"));
        Assert.DoesNotContain(typeof(CopilotSdkSkillDiscoveryGateway).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name is "AdmitRuntimeGenerationAsync" or "ReportSessionStartObservationAsync");
        Assert.Empty(Assert.Single(typeof(CopilotSdkSkillDiscoveryGateway)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).GetParameters());
        Assert.Equal(["DiscoverSkillsAsync"], typeof(ICopilotSkillRuntimeClient)
            .GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ParsedBatch_FreezesIdentityAfterOneCapabilityRead()
    {
        var identity = TestIdentity("1.0.75");
        var capability = new SingleReadCapability(identity);
        var body = Encoding.UTF8.GetBytes("{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"native-session\",\"source_application_version\":\"1.0.75\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v2\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":null,\"source_ephemeral\":true,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"skill\",\"path\":\"p\",\"content\":\"b\"}}]}");

        var batch = SkillInvocationV2Parser.Parse(body, capability);
        var facts = SkillInvocationV2IngestRequestFactsV1.Derive(batch);

        Assert.Same(identity, batch.CertifiedIdentity);
        Assert.Equal("1.0.75", facts.ProducerTuple.SourceApplicationVersion);
        Assert.Equal(1, capability.Reads);
    }

    [Theory]
    [InlineData("1.0.65")]
    [InlineData("1.0.75")]
    public void Writers_EmitExplicitCapabilityIdentity(string version)
    {
        var capability = new StableCapability(TestIdentity(version));

        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-session", RequiredEvent(), "body", capability, out var ordinary));
        Assert.True(SkillInvocationNormalizedJsonV1.TryWriteCancellable("native-session", RequiredEvent(), "body", capability, CancellationToken.None, out var cancellable));
        Assert.Equal(ordinary, cancellable);
        using var document = JsonDocument.Parse(ordinary!);
        Assert.Equal(version, document.RootElement.GetProperty("source_application_version").GetString());
    }

    [Fact]
    public void Parser_Accepts1075AndRejectsIndependentFiveFieldDrift()
    {
        var capability = new StableCapability(TestIdentity("1.0.75"));
        var valid = Request1075();
        Assert.Equal("1.0.75", SkillInvocationV2Parser.Parse(valid, capability).CertifiedIdentity.SourceApplicationVersion);

        foreach (var field in new[] { "source_application_version", "adapter_version", "normalization_version", "payload_schema", "schema_fingerprint" })
        {
            var text = Encoding.UTF8.GetString(valid);
            var marker = field == "source_application_version" ? "1.0.75"
                : field == "adapter_version" ? "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1"
                : field == "normalization_version" ? "github-copilot-sdk.skill-invoked.normalize.v2"
                : field == "payload_schema" ? "github-copilot-sdk.skill-invoked.v1"
                : "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c";
            Assert.Throws<JsonException>(() => SkillInvocationV2Parser.Parse(
                Encoding.UTF8.GetBytes(text.Replace($"\"{field}\":\"{marker}\"", $"\"{field}\":\"drift\"", StringComparison.Ordinal)), capability));
        }
    }

    [Fact]
    public void FactsFingerprint_DiffersOnlyByCertifiedSourceVersion()
    {
        var facts65 = SkillInvocationV2IngestRequestFactsV1.Derive(SkillInvocationV2Parser.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Request1075()).Replace("1.0.75", "1.0.65", StringComparison.Ordinal)),
            new StableCapability(TestIdentity("1.0.65"))));
        var facts75 = SkillInvocationV2IngestRequestFactsV1.Derive(SkillInvocationV2Parser.Parse(
            Request1075(), new StableCapability(TestIdentity("1.0.75"))));

        Assert.NotEqual(facts65.RequestFingerprintSha256, facts75.RequestFingerprintSha256);
        Assert.Equal("1.0.65", facts65.ProducerTuple.SourceApplicationVersion);
        Assert.Equal("1.0.75", facts75.ProducerTuple.SourceApplicationVersion);
    }

    internal static CertifiedSkillProducerIdentityV1 TestIdentity(string version) => SkillInvocationV2TestIdentity.Create(version);

    private sealed class SingleReadCapability(CertifiedSkillProducerIdentityV1 identity) : ISkillInvocationV2RuntimeCapability
    {
        public int Reads { get; private set; }
        public CertifiedSkillProducerIdentityV1 CertifiedIdentity => ++Reads == 1
            ? identity
            : throw new InvalidOperationException("Identity was reread.");
    }

    private sealed record StableCapability(CertifiedSkillProducerIdentityV1 CertifiedIdentity)
        : ISkillInvocationV2RuntimeCapability;

    private static SkillInvokedEvent RequiredEvent() => new()
    {
        Id = Guid.Parse("018f0f4e-7b2a-4c11-8a3b-123456789abc"),
        Timestamp = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        Data = new SkillInvokedData { Name = "skill", Path = "p", Content = "b" }
    };

    private static byte[] Request1075() => Encoding.UTF8.GetBytes("{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"native-session\",\"source_application_version\":\"1.0.75\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v2\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":null,\"source_ephemeral\":true,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"skill\",\"path\":\"p\",\"content\":\"b\"}}]}");

    private static string RepositoryPath(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
                return Path.Combine([directory.FullName, .. segments]);
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string Section(string source, string startMarker, string? endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing section marker: {startMarker}");
        if (endMarker is null)
            return source[start..];
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing section marker after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static string NormalizeWhitespace(string source) =>
        string.Join(' ', source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

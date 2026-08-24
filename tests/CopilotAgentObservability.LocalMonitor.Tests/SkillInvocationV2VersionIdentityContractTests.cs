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
    public void D089CurrentAuthority_SeparatesR0002AdmissionFromTheMandatoryLiveTuple()
    {
        var requirements = File.ReadAllText(RepositoryPath("docs", "requirements.md"));
        var specification = File.ReadAllText(RepositoryPath("docs", "spec.md"));
        var snapshotInterface = File.ReadAllText(RepositoryPath(
            "docs", "specifications", "interfaces", "skill-invocation-snapshot.md"));
        var decisions = File.ReadAllText(RepositoryPath("docs", "decisions.md"));
        var task = File.ReadAllText(RepositoryPath("docs", "task.md"));

        var requirementsSummary = Section(requirements, "- Skill invocation snapshot foundation", "- Codex App discovery");
        var requirementsD089 = Section(requirements, "### D089", "## 4. 非目的");
        Assert.Contains("exact bundled `1.0.75` / protocol `3` T0b must precede r0002 and all producer startup/implementation code", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.Contains("r0002 deterministically admits exactly `1.0.65` and `1.0.75`", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.Contains("implementation is complete", NormalizeWhitespace(requirementsSummary), StringComparison.Ordinal);
        Assert.DoesNotContain("implementation/platform/live/full-validation/review/release evidenceは未完了", requirementsSummary, StringComparison.Ordinal);
        Assert.Contains("mandatory T0b and final signed-in Windows live tuple is exactly bundled `1.0.75` / protocol `3`", NormalizeWhitespace(requirementsD089), StringComparison.Ordinal);
        Assert.Contains("D089", specification, StringComparison.Ordinal);
        Assert.Contains("mandatory live lane is exactly bundled `1.0.75`, protocol `3`", NormalizeWhitespace(specification), StringComparison.Ordinal);

        var specCurrent = Section(specification, "D086 supersedes only D083", "The exact checked-in byte authorities");
        Assert.Contains("T0b precedes r0002 and all producer startup/implementation code", NormalizeWhitespace(specCurrent), StringComparison.Ordinal);
        var specCrossReference = Section(specification, "The independent `skill_projection:1` component", "The SDK transport and retained raw snapshot");
        Assert.Contains("T0b-certified exact bundled `1.0.75` / protocol `3`", NormalizeWhitespace(specCrossReference), StringComparison.Ordinal);
        Assert.Contains("#158 atomic writer/importer implementation is complete", NormalizeWhitespace(specCrossReference), StringComparison.Ordinal);
        Assert.DoesNotContain("must still implement", specCrossReference, StringComparison.Ordinal);

        var currentProducer = Section(snapshotInterface, "### D086 current producer contract", "### D087 current content authority");
        Assert.Contains("Versioned T0b precedes r0002 and all producer startup/implementation code", NormalizeWhitespace(currentProducer), StringComparison.Ordinal);
        Assert.Contains("exact bundled `1.0.75`, protocol `3`", NormalizeWhitespace(currentProducer), StringComparison.Ordinal);
        var finalGates = Section(snapshotInterface, "## Required TDD slices and release gates", "## What is decided versus what still requires evidence");
        Assert.Contains("exact bundled `1.0.75`, protocol `3`", NormalizeWhitespace(finalGates), StringComparison.Ordinal);
        Assert.DoesNotContain("prove each exact r0002 tuple", finalGates, StringComparison.Ordinal);
        Assert.DoesNotContain("An unproved tuple is not admitted", finalGates, StringComparison.Ordinal);

        var d089 = Section(decisions, "## D089:", null);
        var normalizedD089 = NormalizeWhitespace(d089);
        Assert.Contains("Status: Accepted (2026-08-25)", normalizedD089, StringComparison.Ordinal);
        Assert.Contains("deterministic compatibility and admission coverage for `1.0.65`", normalizedD089, StringComparison.Ordinal);
        Assert.Contains("not the mandatory live lane", normalizedD089, StringComparison.Ordinal);
        Assert.Contains("exact and fail closed", normalizedD089, StringComparison.Ordinal);
        Assert.Contains("does not relax the pre-r0002 and pre-producer sequencing", normalizedD089, StringComparison.Ordinal);

        var legacyActivation = Section(snapshotInterface, "Historical D083 evidence", "## Group 6");
        Assert.Contains("Historical D083 evidence", NormalizeWhitespace(legacyActivation), StringComparison.Ordinal);
        Assert.Contains("superseded for the current release gate", NormalizeWhitespace(legacyActivation), StringComparison.Ordinal);

        var taskStatus = Section(task, "| Skill invocation snapshot (Issues #119/#157/#158)", "| Versioned pricing registry");
        Assert.Contains("implementation complete", NormalizeWhitespace(taskStatus), StringComparison.Ordinal);
        Assert.Contains("platform-live evidence refresh and final validation-review pending", NormalizeWhitespace(taskStatus), StringComparison.Ordinal);
    }

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

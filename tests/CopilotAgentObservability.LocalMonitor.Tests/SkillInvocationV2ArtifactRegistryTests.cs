using System.Reflection;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationV2ArtifactRegistryTests
{
    private const string SchemaFingerprint = "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c";

    [Fact]
    public void Load_ExposesOnlyTheExactEmbeddedR0001AcceptedTuple()
    {
        var registry = SkillInvocationV2ArtifactRegistry.Load();

        Assert.Equal(1, registry.CurrentRevision);
        Assert.Equal(SchemaFingerprint, registry.SchemaFingerprint);
        var entry = Assert.Single(registry.Entries);
        Assert.Equal("1.0.65", entry.Tuple.SourceApplicationVersion);
        Assert.Equal("copilot-sdk-dotnet-1.0.4+cao-skill-v2.1", entry.Tuple.AdapterVersion);
        Assert.Equal("github-copilot-sdk.skill-invoked.normalize.v1", entry.Tuple.NormalizationVersion);
        Assert.Equal("github-copilot-sdk.skill-invoked.v1", entry.Tuple.PayloadSchema);
        Assert.Equal(SchemaFingerprint, entry.Tuple.SchemaFingerprint);
        Assert.Equal(SkillInvocationV2CompatibilityDisposition.Accepted, entry.Disposition);
        Assert.True(registry.IsAccepted(entry.Tuple));
        Assert.False(registry.IsAccepted(entry.Tuple with { SourceApplicationVersion = "1.0.66" }));
        Assert.Same(registry, SkillInvocationV2ArtifactRegistry.Load());
    }

    [Fact]
    public void Load_DefensivelyOwnsContractBytesCollectionsAndOpaqueCapability()
    {
        var sourcePayload = Encoding.UTF8.GetBytes("{\"name\":\"skill\"}");
        var evidence = new SkillInvocationV2RawPayloadEvidence(sourcePayload);
        sourcePayload[0] = (byte)'!';

        var capability = new TestRuntimeCapability();
        var envelope = new SkillInvocationV2AcceptedEnvelope(
            evidence,
            SkillInvocationV2PayloadState.Available,
            SkillInvocationV2PayloadReason.None);
        var sourceEnvelopes = new[] { envelope };
        var batch = new ParsedSkillInvocationV2Batch(sourceEnvelopes, capability);
        sourceEnvelopes[0] = new SkillInvocationV2AcceptedEnvelope(
            new SkillInvocationV2RawPayloadEvidence(Encoding.UTF8.GetBytes("null")),
            SkillInvocationV2PayloadState.Missing,
            SkillInvocationV2PayloadReason.NameMissing);

        var copy = evidence.PayloadUtf8.ToArray();
        copy[0] = (byte)'!';

        Assert.Equal("{\"name\":\"skill\"}", Encoding.UTF8.GetString(evidence.PayloadUtf8.Span));
        Assert.Same(capability, batch.RuntimeCapability);
        Assert.Same(envelope, Assert.Single(batch.AcceptedEnvelopes));
        Assert.DoesNotContain(capability.ToString(), batch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(capability.ToString(), envelope.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactValidation_RejectsChangedSchemaBytesOrSidecarMismatch()
    {
        var (schema, sidecar, registry) = ReadCanonicalArtifacts();
        var changedSchema = schema.ToArray();
        changedSchema[0] ^= 0x01;
        var changedSidecar = sidecar.ToArray();
        changedSidecar[0] = (byte)'0';

        AssertRejected(changedSchema, sidecar, History(registry));
        AssertRejected(schema, changedSidecar, History(registry));
    }

    [Theory]
    [InlineData("{\"extra\":true,\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[]}")]
    [InlineData("{\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[]}")]
    [InlineData("{\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[{\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8FAC48D8A878CBC9A4EBF59AAE78E242B3375F4B82ABED7C7A0E45D7A6FF7A5C\",\"disposition\":\"accepted\"}]}")]
    [InlineData("{\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[{\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"disposition\":\"accepted\"},{\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"disposition\":\"accepted\"}]}")]
    public void ArtifactValidation_RejectsMalformedRegistryShapeHashAndTupleDuplication(string malformedRegistry)
    {
        var (schema, sidecar, _) = ReadCanonicalArtifacts();

        AssertRejected(schema, sidecar, History(Encoding.UTF8.GetBytes(malformedRegistry)));
    }

    [Fact]
    public void ArtifactValidation_RejectsMissingGappedHistoryAndNeverFallsBackToR0001()
    {
        var (schema, sidecar, registry) = ReadCanonicalArtifacts();
        var malformedNewerRevision = Encoding.UTF8.GetBytes("{not-json}");

        AssertRejected(schema, sidecar, new Dictionary<int, byte[]>());
        AssertRejected(schema, sidecar, new Dictionary<int, byte[]> { [1] = registry, [3] = registry });
        AssertRejected(schema, sidecar, new Dictionary<int, byte[]> { [1] = registry, [2] = malformedNewerRevision });
    }

    private static Dictionary<int, byte[]> History(byte[] r0001) => new() { [1] = r0001 };

    private static (byte[] Schema, byte[] Sidecar, byte[] Registry) ReadCanonicalArtifacts()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestData", "SkillInvocationV2");
        return (
            File.ReadAllBytes(Path.Combine(root, "github-copilot-sdk.skill-invoked.v1.schema.json")),
            File.ReadAllBytes(Path.Combine(root, "github-copilot-sdk.skill-invoked.v1.schema.sha256")),
            File.ReadAllBytes(Path.Combine(root, "compatibility-registry-r0001.json")));
    }

    private static void AssertRejected(byte[] schema, byte[] sidecar, IReadOnlyDictionary<int, byte[]> history)
    {
        var method = typeof(SkillInvocationV2ArtifactRegistry).GetMethod(
            "LoadForArtifactValidation",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [schema, sidecar, history]));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private sealed class TestRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public override string ToString() => "runtime-capability-must-not-leak";
    }
}

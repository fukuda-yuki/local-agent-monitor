using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationV2ArtifactRegistryTests
{
    private const string SchemaFingerprint = "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c";
    private const string SidecarFingerprint = "3f6b076bb7329662088c0b055a81e5f3d9789cd654ddde27bf3b1877d32ba123";
    private const string RegistryFingerprint = "3ae5d255647edad6e23f077c3e9042be50d593211cd9a90d6c9f7210c53bfdda";

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
    public void RawPayloadEvidence_DefensivelyOwnsPayloadAndDigestBuffers()
    {
        var sourcePayload = Encoding.UTF8.GetBytes("{\"name\":\"skill\"}");
        var evidence = new SkillInvocationV2RawPayloadEvidence(sourcePayload);
        sourcePayload[0] = (byte)'!';

        Assert.True(MemoryMarshal.TryGetArray(evidence.PayloadUtf8, out var payloadAlias));
        Assert.True(MemoryMarshal.TryGetArray(evidence.PayloadSha256, out var digestAlias));
        payloadAlias.Array![payloadAlias.Offset] = (byte)'!';
        digestAlias.Array![digestAlias.Offset] ^= 0x01;

        Assert.Equal("{\"name\":\"skill\"}", Encoding.UTF8.GetString(evidence.PayloadUtf8.Span));
        Assert.Equal("2767a326386c927e17ac6bbdd6ec558e9ee2039dba681a918e7c70029b6e833a", Convert.ToHexStringLower(SHA256.HashData(evidence.PayloadUtf8.Span)));
        Assert.Equal("2767a326386c927e17ac6bbdd6ec558e9ee2039dba681a918e7c70029b6e833a", Convert.ToHexStringLower(evidence.PayloadSha256.Span));
        Assert.Equal(SchemaFingerprint.Length / 2, evidence.PayloadSha256.Length);
    }

    [Theory]
    [InlineData(SkillInvocationV2PayloadState.Available, SkillInvocationV2PayloadReason.None)]
    [InlineData(SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.DuplicateProperty)]
    [InlineData(SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.UnknownProperty)]
    [InlineData(SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.InvalidFieldType)]
    [InlineData(SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.NameInvalid)]
    [InlineData(SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.PathInvalid)]
    [InlineData(SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.NameMissing)]
    [InlineData(SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.BodyMissing)]
    [InlineData(SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.DefinitionPathMissing)]
    [InlineData(SkillInvocationV2PayloadState.Binary, SkillInvocationV2PayloadReason.BodyUnicodeInvalid)]
    [InlineData(SkillInvocationV2PayloadState.Binary, SkillInvocationV2PayloadReason.PathUnicodeInvalid)]
    [InlineData(SkillInvocationV2PayloadState.Oversized, SkillInvocationV2PayloadReason.BodyOversized)]
    [InlineData(SkillInvocationV2PayloadState.Oversized, SkillInvocationV2PayloadReason.PathOversized)]
    public void AcceptedEnvelope_AdmitsOnlyGate6StateReasonPairsAndNullableFacts(
        SkillInvocationV2PayloadState state,
        SkillInvocationV2PayloadReason reason)
    {
        var rawPayload = new SkillInvocationV2RawPayloadEvidence(Encoding.UTF8.GetBytes("{}"));
        var facts = state == SkillInvocationV2PayloadState.Available ? AvailableFacts(source: null, trigger: null) : null;

        var envelope = new SkillInvocationV2AcceptedEnvelope(rawPayload, state, reason, facts);

        Assert.Same(facts, envelope.ClaimFacts);
        Assert.Equal(facts?.Name, envelope.Name);
        Assert.Equal(facts?.Source, envelope.Source);
        Assert.Equal(facts?.Trigger, envelope.Trigger);
        Assert.Same(facts?.Body, envelope.Body);
        Assert.Same(facts?.DefinitionPath, envelope.DefinitionPath);
    }

    [Fact]
    public void AcceptedEnvelope_RejectsStateReasonAndNullableFactContradictions()
    {
        var rawPayload = new SkillInvocationV2RawPayloadEvidence(Encoding.UTF8.GetBytes("{}"));
        var facts = AvailableFacts();

        Assert.Throws<ArgumentException>(() => new SkillInvocationV2AcceptedEnvelope(
            rawPayload, SkillInvocationV2PayloadState.Available, SkillInvocationV2PayloadReason.None, null));
        Assert.Throws<ArgumentException>(() => new SkillInvocationV2AcceptedEnvelope(
            rawPayload, SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.None, null));
        Assert.Throws<ArgumentException>(() => new SkillInvocationV2AcceptedEnvelope(
            rawPayload, SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.BodyUnicodeInvalid, null));
        Assert.Throws<ArgumentException>(() => new SkillInvocationV2AcceptedEnvelope(
            rawPayload, SkillInvocationV2PayloadState.Binary, SkillInvocationV2PayloadReason.BodyOversized, null));
        Assert.Throws<ArgumentException>(() => new SkillInvocationV2AcceptedEnvelope(
            rawPayload, SkillInvocationV2PayloadState.Oversized, SkillInvocationV2PayloadReason.NameMissing, null));
        Assert.Throws<ArgumentException>(() => new SkillInvocationV2AcceptedEnvelope(
            rawPayload, SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.NameMissing, facts));
    }

    [Fact]
    public void ParsedAvailableFacts_RejectMalformedUtf16Text()
    {
        Assert.Throws<ArgumentException>(() => new SkillInvocationV2TextEvidence("\uD800"));
        Assert.Throws<ArgumentException>(() => new SkillInvocationV2ParsedClaimFacts(
            "\uD800",
            null,
            null,
            new SkillInvocationV2TextEvidence("body"),
            new SkillInvocationV2TextEvidence("path")));
    }

    [Fact]
    public void ParsedBatch_PreservesCapabilityIdentityButNeverSerializesOrUsesItForDiagnosticsOrEquality()
    {
        var envelope = new SkillInvocationV2AcceptedEnvelope(
            new SkillInvocationV2RawPayloadEvidence(Encoding.UTF8.GetBytes("{}")),
            SkillInvocationV2PayloadState.Available,
            SkillInvocationV2PayloadReason.None,
            AvailableFacts());
        var capability = new ThrowingRuntimeCapability();
        var sourceEnvelopes = new[] { envelope };
        var batch = new ParsedSkillInvocationV2Batch(sourceEnvelopes, capability);
        var other = new ParsedSkillInvocationV2Batch([envelope], new ThrowingRuntimeCapability());
        sourceEnvelopes[0] = new SkillInvocationV2AcceptedEnvelope(
            new SkillInvocationV2RawPayloadEvidence(Encoding.UTF8.GetBytes("{}")),
            SkillInvocationV2PayloadState.Missing,
            SkillInvocationV2PayloadReason.NameMissing,
            null);

        Assert.Same(capability, batch.RuntimeCapability);
        Assert.Same(envelope, Assert.Single(batch.AcceptedEnvelopes));
        Assert.False(batch.Equals(other));
        _ = batch.GetHashCode();
        var serialized = JsonSerializer.Serialize(batch);

        Assert.DoesNotContain("RuntimeCapability", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-capability-must-not-leak", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-capability-must-not-leak", batch.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalArtifacts_HaveIndependentLiteralLengthsAndHashes()
    {
        var (schema, sidecar, registry) = ReadCanonicalArtifacts();

        Assert.Equal(980, schema.Length);
        Assert.Equal(SchemaFingerprint, Convert.ToHexStringLower(SHA256.HashData(schema)));
        Assert.Equal(65, sidecar.Length);
        Assert.Equal(SidecarFingerprint, Convert.ToHexStringLower(SHA256.HashData(sidecar)));
        Assert.Equal(431, registry.Length);
        Assert.Equal(RegistryFingerprint, Convert.ToHexStringLower(SHA256.HashData(registry)));
    }

    [Fact]
    public void ArtifactValidation_RejectsChangedSchemaBytesOrSidecarMismatch()
    {
        var (schema, sidecar, registry) = ReadCanonicalArtifacts();
        var changedSchema = schema.ToArray();
        changedSchema[0] ^= 0x01;
        var changedSidecar = sidecar.ToArray();
        changedSidecar[0] = (byte)'0';

        AssertRejectedExact(changedSchema, sidecar, History(registry));
        AssertRejectedExact(schema, changedSidecar, History(registry));
        AssertRejectedStructurally(changedSchema, sidecar, History(registry));
        AssertRejectedStructurally(schema, changedSidecar, History(registry));
    }

    [Theory]
    [InlineData("{\"extra\":true,\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[]}")]
    [InlineData("{\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[]}")]
    [InlineData("{\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[{\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8FAC48D8A878CBC9A4EBF59AAE78E242B3375F4B82ABED7C7A0E45D7A6FF7A5C\",\"disposition\":\"accepted\"}]}")]
    [InlineData("{\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[{\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"not-a-sha256\",\"disposition\":\"accepted\"}]}")]
    [InlineData("{\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[{\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"disposition\":\"unknown\"}]}")]
    [InlineData("{\"schema_version\":\"skill-sdk-compatibility-registry.v1\",\"registry_revision\":1,\"entries\":[{\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"disposition\":\"accepted\"},{\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"disposition\":\"accepted\"}]}")]
    public void ArtifactValidation_RejectsMalformedRegistryShapeHashAndTupleDuplication(string malformedRegistry)
    {
        var (schema, sidecar, _) = ReadCanonicalArtifacts();

        AssertRejectedStructurally(schema, sidecar, History(Encoding.UTF8.GetBytes(malformedRegistry)));
    }

    [Fact]
    public void ArtifactValidation_RejectsMissingGappedHistoryAndNeverFallsBackToR0001()
    {
        var (schema, sidecar, registry) = ReadCanonicalArtifacts();
        var malformedNewerRevision = Encoding.UTF8.GetBytes("{not-json}");

        AssertRejectedStructurally(schema, sidecar, new Dictionary<int, byte[]>());
        AssertRejectedStructurally(schema, sidecar, new Dictionary<int, byte[]> { [1] = registry, [3] = registry });
        AssertRejectedStructurally(schema, sidecar, new Dictionary<int, byte[]> { [1] = registry, [2] = malformedNewerRevision });
    }

    [Fact]
    public void ArtifactValidation_RejectsAnUnprovenRevokedTuple()
    {
        var (schema, sidecar, registry) = ReadCanonicalArtifacts();
        var revoked = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(registry).Replace("\"accepted\"", "\"revoked\"", StringComparison.Ordinal));

        AssertRejectedStructurally(schema, sidecar, History(revoked));
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

    private static SkillInvocationV2ParsedClaimFacts AvailableFacts(string? source = "project", string? trigger = "user-invoked") => new(
        "skill",
        source,
        trigger,
        new SkillInvocationV2TextEvidence("body"),
        new SkillInvocationV2TextEvidence(".github/skills/skill.md"));

    private static void AssertRejectedExact(byte[] schema, byte[] sidecar, IReadOnlyDictionary<int, byte[]> history)
    {
        var method = typeof(SkillInvocationV2ArtifactRegistry).GetMethod(
            "LoadForArtifactValidation",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [schema, sidecar, history]));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private static void AssertRejectedStructurally(byte[] schema, byte[] sidecar, IReadOnlyDictionary<int, byte[]> history) =>
        Assert.Throws<InvalidOperationException>(() =>
            SkillInvocationV2ArtifactRegistry.ValidateArtifactStructureForTesting(schema, sidecar, history));

    private sealed class ThrowingRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public override string ToString() => "runtime-capability-must-not-leak";

        public override int GetHashCode() => throw new InvalidOperationException("Capability hash must not be observed.");
    }
}

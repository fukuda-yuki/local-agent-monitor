using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationV2IngestRequestFactsV1Tests
{
    private const string AvailablePayload = """{"name":"review","path":".github/skills/review.md","content":"body","source":"project","trigger":"user-invoked"}""";

    [Fact]
    public void Derive_AvailablePayload_CarriesPersistedTokensAndClaimFacts()
    {
        var facts = Derive(AvailablePayload);

        Assert.Equal(SkillInvocationPayloadState.Available, facts.PayloadState);
        Assert.Equal(SkillInvocationPayloadReason.None, facts.PayloadReason);
        Assert.Equal("available", facts.StateToken);
        Assert.Equal("none", facts.ReasonToken);
        Assert.Equal("review", facts.ClaimFacts!.Name);
        Assert.Equal("project", facts.ClaimFacts.Source);
        Assert.Equal("user-invoked", facts.ClaimFacts.Trigger);
    }

    [Fact]
    public void Derive_MissingNamePayload_CarriesPersistedFaultTokensAndNoClaimFacts()
    {
        var facts = Derive("""{"path":".github/skills/review.md","content":"body","source":"project","trigger":"user-invoked"}""");

        Assert.Equal(SkillInvocationPayloadState.Missing, facts.PayloadState);
        Assert.Equal(SkillInvocationPayloadReason.NameMissing, facts.PayloadReason);
        Assert.Equal("missing", facts.StateToken);
        Assert.Equal("name_missing", facts.ReasonToken);
        Assert.Null(facts.ClaimFacts);
    }

    [Fact]
    public void Derive_AdmittedPayload_CarriesExactPayloadLengthAndLowercaseDigests()
    {
        var facts = Derive(AvailablePayload);

        Assert.Equal((ulong)Encoding.UTF8.GetByteCount(AvailablePayload), facts.PayloadBytes);
        AssertLowercaseSha256(facts.PayloadSha256);
        AssertLowercaseSha256(facts.ContentDocumentSha256);
        AssertLowercaseSha256(facts.RequestFingerprintSha256);
    }

    [Fact]
    public void Derive_AdmittedPayload_MapsEveryRequestFingerprintField()
    {
        var facts = Derive(AvailablePayload);
        var payloadUtf8 = Encoding.UTF8.GetBytes(AvailablePayload);
        var payloadSha256 = LowercaseSha256(payloadUtf8);
        var contentDocument = Encoding.UTF8.GetBytes(
            "{\"schema_version\":\"session-event-content.skill-invoked.v1\",\"payload_utf8_base64\":\""
            + Convert.ToBase64String(payloadUtf8)
            + "\"}");
        var expectedInput = new SkillInvocationSnapshotReceiptFingerprintInput(
            SourceAdapter: "copilot-sdk-stream",
            SourceEventId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            SourceSurface: "copilot-sdk",
            NativeSessionId: "native-session",
            RunNativeId: "run-1",
            SourceParentEventId: "bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb",
            SourceEphemeral: true,
            TraceId: null,
            SpanId: null,
            OccurredAt: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            SourceApplicationVersion: "1.0.65",
            AdapterVersion: "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1",
            NormalizationVersion: "github-copilot-sdk.skill-invoked.normalize.v2",
            PayloadSchema: "github-copilot-sdk.skill-invoked.v1",
            SchemaFingerprint: "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c",
            PayloadSha256: payloadSha256,
            PayloadBytes: (ulong)payloadUtf8.Length,
            State: "available",
            Reason: "none",
            Name: "review",
            Source: "project",
            Trigger: "user-invoked",
            BodySha256: LowercaseSha256(Encoding.UTF8.GetBytes("body")),
            BodyUtf8Bytes: 4,
            DefinitionPathSha256: LowercaseSha256(Encoding.UTF8.GetBytes(".github/skills/review.md")),
            DefinitionPathUtf8Bytes: 24,
            ContentDocumentSha256: LowercaseSha256(contentDocument));

        Assert.Equal(
            SkillInvocationSnapshotReceiptFingerprint.Compute(expectedInput),
            facts.RequestFingerprintSha256);
    }

    [Fact]
    public void Derive_BatchWithoutExactlyOneEnvelope_ThrowsArgumentException()
    {
        var parsed = SkillInvocationV2Parser.Parse(ValidRequest(AvailablePayload), new TestRuntimeCapability());
        var envelope = Assert.Single(parsed.AcceptedEnvelopes);
        var emptyBatch = new ParsedSkillInvocationV2Batch([], new TestRuntimeCapability(), SkillInvocationV2TestIdentity.V1065, "native-session");
        var twoEnvelopeBatch = new ParsedSkillInvocationV2Batch(
            [envelope, envelope],
            new TestRuntimeCapability(),
            SkillInvocationV2TestIdentity.V1065,
            "native-session");

        Assert.Throws<ArgumentException>(() => SkillInvocationV2IngestRequestFactsV1.Derive(emptyBatch));
        Assert.Throws<ArgumentException>(() => SkillInvocationV2IngestRequestFactsV1.Derive(twoEnvelopeBatch));
    }

    [Fact]
    public void PayloadTokens_EveryDeclaredMember_HasDistinctLowercaseAsciiToken()
    {
        var states = Enum.GetValues<SkillInvocationPayloadState>();
        var stateTokens = states.Select(SkillInvocationPayloadTokensV1.StateToken).ToArray();
        var reasons = Enum.GetValues<SkillInvocationPayloadReason>();
        var reasonTokens = reasons.Select(SkillInvocationPayloadTokensV1.ReasonToken).ToArray();

        Assert.All(
            stateTokens.Select((token, index) => (token, index)),
            item => AssertValidPayloadToken(item.token, stateTokens, item.index));
        Assert.All(
            reasonTokens.Select((token, index) => (token, index)),
            item => AssertValidPayloadToken(item.token, reasonTokens, item.index));
        Assert.Equal("available", SkillInvocationPayloadTokensV1.StateToken(SkillInvocationPayloadState.Available));
        Assert.Equal("none", SkillInvocationPayloadTokensV1.ReasonToken(SkillInvocationPayloadReason.None));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SkillInvocationPayloadTokensV1.StateToken((SkillInvocationPayloadState)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SkillInvocationPayloadTokensV1.ReasonToken((SkillInvocationPayloadReason)(-1)));
    }

    [Fact]
    public void Derive_ClassifiedFault_NullsEveryClaimFieldInRequestFingerprint()
    {
        const string payload = """{"path":".github/skills/review.md","content":"body","source":"project","trigger":"user-invoked"}""";
        var facts = Derive(payload);
        var payloadUtf8 = Encoding.UTF8.GetBytes(payload);
        var contentDocument = Encoding.UTF8.GetBytes(
            "{\"schema_version\":\"session-event-content.skill-invoked.v1\",\"payload_utf8_base64\":\""
            + Convert.ToBase64String(payloadUtf8)
            + "\"}");
        var expectedInput = new SkillInvocationSnapshotReceiptFingerprintInput(
            SourceAdapter: "copilot-sdk-stream",
            SourceEventId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            SourceSurface: "copilot-sdk",
            NativeSessionId: "native-session",
            RunNativeId: "run-1",
            SourceParentEventId: "bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb",
            SourceEphemeral: true,
            TraceId: null,
            SpanId: null,
            OccurredAt: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            SourceApplicationVersion: "1.0.65",
            AdapterVersion: "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1",
            NormalizationVersion: "github-copilot-sdk.skill-invoked.normalize.v2",
            PayloadSchema: "github-copilot-sdk.skill-invoked.v1",
            SchemaFingerprint: "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c",
            PayloadSha256: LowercaseSha256(payloadUtf8),
            PayloadBytes: (ulong)payloadUtf8.Length,
            State: "missing",
            Reason: "name_missing",
            Name: null,
            Source: null,
            Trigger: null,
            BodySha256: null,
            BodyUtf8Bytes: null,
            DefinitionPathSha256: null,
            DefinitionPathUtf8Bytes: null,
            ContentDocumentSha256: LowercaseSha256(contentDocument));

        Assert.Null(facts.ClaimFacts);
        Assert.Equal(
            SkillInvocationSnapshotReceiptFingerprint.Compute(expectedInput),
            facts.RequestFingerprintSha256);
    }

    [Fact]
    public void Derive_TwoIdenticalRequests_ShareAFingerprintThatChangesWithSourceEventId()
    {
        var first = SkillInvocationV2Parser.Parse(ValidRequest(AvailablePayload), new TestRuntimeCapability());
        var identical = SkillInvocationV2Parser.Parse(ValidRequest(AvailablePayload), new TestRuntimeCapability());
        var differentSourceEventId = SkillInvocationV2Parser.Parse(
            ValidRequest(AvailablePayload, "cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            new TestRuntimeCapability());

        var firstFacts = SkillInvocationV2IngestRequestFactsV1.Derive(first);
        var identicalFacts = SkillInvocationV2IngestRequestFactsV1.Derive(identical);
        var differentFacts = SkillInvocationV2IngestRequestFactsV1.Derive(differentSourceEventId);

        Assert.Equal(firstFacts.RequestFingerprintSha256, identicalFacts.RequestFingerprintSha256);
        Assert.NotEqual(firstFacts.RequestFingerprintSha256, differentFacts.RequestFingerprintSha256);
    }

    [Fact]
    public void Derive_AdmittedPayload_CarriesExactProducerTuple()
    {
        var tuple = Derive(AvailablePayload).ProducerTuple;

        Assert.Equal("1.0.65", tuple.SourceApplicationVersion);
        Assert.Equal("copilot-sdk-dotnet-1.0.4+cao-skill-v2.1", tuple.AdapterVersion);
        Assert.Equal("github-copilot-sdk.skill-invoked.normalize.v2", tuple.NormalizationVersion);
        Assert.Equal("github-copilot-sdk.skill-invoked.v1", tuple.PayloadSchema);
        Assert.Equal("8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c", tuple.SchemaFingerprint);
    }

    [Fact]
    public void Derive_NullBatch_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SkillInvocationV2IngestRequestFactsV1.Derive(null!));
    }

    private static SkillInvocationV2IngestRequestFactsV1 Derive(string payload) =>
        SkillInvocationV2IngestRequestFactsV1.Derive(
            SkillInvocationV2Parser.Parse(ValidRequest(payload), new TestRuntimeCapability()));

    private static byte[] ValidRequest(
        string payload,
        string sourceEventId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa") =>
        Encoding.UTF8.GetBytes((
            EnvelopePrefix
            + sourceEventId
            + EventSuffix
            + payload
            + "}]}").Replace("github-copilot-sdk.skill-invoked.normalize.v1",
                "github-copilot-sdk.skill-invoked.normalize.v2", StringComparison.Ordinal));

    private const string EnvelopePrefix = "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"native-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"";

    private const string EventSuffix = "\",\"source_parent_event_id\":\"bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb\",\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":\"run-1\",\"source_ephemeral\":true,\"trace_id\":null,\"span_id\":null,\"payload\":";

    private static void AssertLowercaseSha256(string value)
    {
        Assert.Equal(64, value.Length);
        Assert.All(value, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    private static void AssertValidPayloadToken(string token, IReadOnlyList<string> allTokens, int tokenIndex)
    {
        Assert.NotEmpty(token);
        Assert.All(token, character => Assert.True(character is >= 'a' and <= 'z' or '_'));
        Assert.All(
            allTokens.Where((_, otherIndex) => otherIndex != tokenIndex),
            otherToken => Assert.NotEqual(token, otherToken));
    }

    private static string LowercaseSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class TestRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public CopilotAgentObservability.LocalMonitor.SkillRuntime.CertifiedSkillProducerIdentityV1 CertifiedIdentity => SkillInvocationV2TestIdentity.V1065;
    }
}

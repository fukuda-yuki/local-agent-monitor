using CopilotAgentObservability.Telemetry.Repositories;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryIdentityHashingTests
{
    [Fact]
    public void SourceIdentityUsesExactSpanFramingAndGoldenDigest()
    {
        var input = LocalRepositorySourceIdentityInput.Span(
            rawRecordId: 7,
            resourceSpanOrdinal: 2,
            scopeSpanOrdinal: 3,
            spanOrdinal: 4,
            attributeOrdinal: 5,
            attributeKey: "vcs.repository.url.full");

        Assert.Equal(
            "6c6f63616c2d7265706f7369746f72792d736f757263652d6f62736572766174696f6e0076310000000000000000070000000200000003000000040200000005000000177663732e7265706f7369746f72792e75726c2e66756c6c",
            LocalRepositoryIdentityHashing.SourceIdentityPreimageHex(input));
        Assert.Equal("74eb22b4464f0b3da30d505148542ab4e5fd74abaca071b5d67235c511d8a377", LocalRepositoryIdentityHashing.SourceIdentity(input));
    }

    [Fact]
    public void SourceIdentityUsesResourceSentinelsAtBoundaryValues()
    {
        var input = LocalRepositorySourceIdentityInput.Resource(
            rawRecordId: long.MaxValue,
            resourceSpanOrdinal: int.MaxValue,
            attributeOrdinal: int.MaxValue,
            attributeKey: "copilot_chat.repo.remote_url");

        Assert.Equal(
            "6c6f63616c2d7265706f7369746f72792d736f757263652d6f62736572766174696f6e007631007fffffffffffffff7fffffffffffffffffffffff017fffffff0000001c636f70696c6f745f636861742e7265706f2e72656d6f74655f75726c",
            LocalRepositoryIdentityHashing.SourceIdentityPreimageHex(input));
        Assert.Equal("ff0b6225d8d8d8dffd45d4c8738e67fb142b6e9956d0a088e8201fb37077e3f7", LocalRepositoryIdentityHashing.SourceIdentity(input));
    }

    [Fact]
    public void ContextIdentityFramesSourceDigestAsBytesAndSessionIdsAsText()
    {
        var input = new LocalRepositoryContextIdentityInput(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "00112233-4455-6677-8899-aabbccddeeff",
            "ffeeddcc-bbaa-9988-7766-554433221100",
            "00112233445566778899aabbccddeeff",
            "0123456789abcdef");

        Assert.Equal(
            "2d6b8ef6fcb31ce71dfa136b37d6bbbeeb37bbc48e5eed2114c009b3cc6829ae",
            LocalRepositoryIdentityHashing.ContextIdentity(input));
        Assert.Equal(
            "6c6f63616c2d7265706f7369746f72792d6f62736572766174696f6e2d636f6e74657874007631000123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0000002430303131323233332d343435352d363637372d383839392d6161626263636464656566660000002466666565646463632d626261612d393938382d373736362d35353434333332323131303000112233445566778899aabbccddeeff0123456789abcdef",
            LocalRepositoryIdentityHashing.ContextIdentityPreimageHex(input));
    }

    [Fact]
    public void OperationFingerprintDistinguishesNullFromEmptyAndUsesFixedFieldOrder()
    {
        var input = new LocalRepositoryOperationFingerprintInput(
            Method: "POST",
            RouteTemplate: "/api/local-monitor/v1/repositories",
            Operation: "create",
            TargetId: null,
            ExpectedRevision: string.Empty,
            DisplayName: "Repo",
            CanonicalLocator: "github.com/octo/repo",
            SessionAction: null,
            RepositoryId: "00112233-4455-6677-8899-aabbccddeeff");

        Assert.Equal(
            "3abbf6f9c170773c2c66f6e8f022c4d2adef7ccd6f81b5bf873137c76c587488",
            LocalRepositoryIdentityHashing.OperationFingerprint(input));
        Assert.Equal(
            "6c6f63616c2d7265706f7369746f72792d6f7065726174696f6e007631000000000900066d6574686f640100000004504f5354000e726f7574655f74656d706c61746501000000222f6170692f6c6f63616c2d6d6f6e69746f722f76312f7265706f7369746f7269657300096f7065726174696f6e010000000663726561746500097461726765745f696400001165787065637465645f7265766973696f6e0100000000000c646973706c61795f6e616d6501000000045265706f001163616e6f6e6963616c5f6c6f6361746f7201000000146769746875622e636f6d2f6f63746f2f7265706f000e73657373696f6e5f616374696f6e00000d7265706f7369746f72795f6964010000002430303131323233332d343435352d363637372d383839392d616162626363646465656666",
            LocalRepositoryIdentityHashing.OperationFingerprintPreimageHex(input));
        Assert.NotEqual(
            LocalRepositoryIdentityHashing.OperationFingerprint(input),
            LocalRepositoryIdentityHashing.OperationFingerprint(input with { ExpectedRevision = null }));
    }

    [Fact]
    public void AssignmentStateFingerprintDeduplicatesAndSortsCandidateIdsByRfcBytes()
    {
        var first = new LocalRepositoryAssignmentState(
            "conflict",
            "automatic",
            repositoryId: null,
            [
                "00112233-4455-6677-8899-aabbccddeeff",
                "00000100-0000-0000-0000-000000000000",
                "00000001-0000-0000-0000-000000000000",
                "00112233-4455-6677-8899-aabbccddeeff",
            ]);
        var second = first with
        {
            CandidateRepositoryIds =
            [
                "00000001-0000-0000-0000-000000000000",
                "00000100-0000-0000-0000-000000000000",
                "00112233-4455-6677-8899-aabbccddeeff",
            ],
        };

        Assert.Equal("a863801cbaf65ac25e3b6a267c51af3fe7b5b1124ce709e83a09381526273a99", LocalRepositoryIdentityHashing.AssignmentStateFingerprint(first));
        Assert.Equal(
            "6c6f63616c2d7265706f7369746f72792d61737369676e6d656e742d73746174650076310000000008636f6e666c696374000000096175746f6d6174696300000000030000002430303030303030312d303030302d303030302d303030302d3030303030303030303030300000002430303030303130302d303030302d303030302d303030302d3030303030303030303030300000002430303131323233332d343435352d363637372d383839392d616162626363646465656666",
            LocalRepositoryIdentityHashing.AssignmentStateFingerprintPreimageHex(first));
        Assert.Equal(LocalRepositoryIdentityHashing.AssignmentStateFingerprint(first), LocalRepositoryIdentityHashing.AssignmentStateFingerprint(second));
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "b558ff5f459b942809a1657ac0adce14d384548d5965802e0b9e55accee694a3")]
    public void ReconciliationFingerprintFramesDigestText(string payloadDigest, string expected)
    {
        var input = LocalRepositoryReconciliationEvidence.PayloadSha256(9, payloadDigest);

        Assert.Equal(expected, LocalRepositoryIdentityHashing.ReconciliationFingerprint(input));
        Assert.Equal(
            "6c6f63616c2d7265706f7369746f72792d7265636f6e63696c650076310000000000000000090000000e7061796c6f61645f73686132353600000040616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161610000001a6c6f63616c2d7265706f7369746f72792d636174616c6f673a31",
            LocalRepositoryIdentityHashing.ReconciliationFingerprintPreimageHex(input));
    }

    [Fact]
    public void ReconciliationFingerprintFramesUnavailableLiteral()
    {
        Assert.Equal(
            "38af197322cb66db678378d511aea18add9342a2a602b59f6ef139c6b0c1c15e",
            LocalRepositoryIdentityHashing.ReconciliationFingerprint(
                LocalRepositoryReconciliationEvidence.InputUnavailable(9)));
        Assert.Equal(
            "6c6f63616c2d7265706f7369746f72792d7265636f6e63696c6500763100000000000000000900000011696e7075745f756e617661696c61626c650000000b756e617661696c61626c650000001a6c6f63616c2d7265706f7369746f72792d636174616c6f673a31",
            LocalRepositoryIdentityHashing.ReconciliationFingerprintPreimageHex(
                LocalRepositoryReconciliationEvidence.InputUnavailable(9)));
    }

    [Fact]
    public void HashingRejectsInvalidBoundaryInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Resource(0, 0, 0, "vcs.repository.url.full")));
        Assert.Throws<ArgumentException>(() => LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Span(1, 0, -1, 0, 0, "vcs.repository.url.full")));
        Assert.Throws<ArgumentException>(() => LocalRepositoryIdentityHashing.ContextIdentity(new(
            new string('A', 64),
            "00112233-4455-6677-8899-aabbccddeeff",
            "ffeeddcc-bbaa-9988-7766-554433221100",
            "00112233445566778899aabbccddeeff",
            "0123456789abcdef")));
        Assert.Throws<ArgumentException>(() => LocalRepositoryIdentityHashing.ContextIdentity(new(
            new string('a', 64),
            "00112233-4455-6677-8899-aabbccddeeff",
            "ffeeddcc-bbaa-9988-7766-554433221100",
            "00112233445566778899AABBCCDDEEFF",
            "0123456789abcdef")));
        Assert.Throws<ArgumentException>(() => LocalRepositoryIdentityHashing.ContextIdentity(new(
            new string('a', 64),
            "00112233-4455-6677-8899-aabbccddeeff",
            "ffeeddcc-bbaa-9988-7766-554433221100",
            "00112233445566778899aabbccddeeff",
            "0123456789ABCDEf")));
        Assert.Throws<ArgumentException>(() => LocalRepositoryIdentityHashing.ContextIdentity(new(
            new string('a', 64),
            "00112233-4455-6677-8899-aabbccddeeff",
            "not-a-canonical-session-event-id-000000",
            "00112233445566778899aabbccddeeff",
            "0123456789abcdef")));
        Assert.Throws<ArgumentException>(() => LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState("assigned", "automatic", "00112233-4455-6677-8899-aabbccddeeff", ["00112233-4455-6677-8899-aabbccddeefF"])));
        Assert.Throws<ArgumentException>(() => LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.PayloadSha256(1, new string('A', 64))));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.InputUnavailable(0)));
    }
}

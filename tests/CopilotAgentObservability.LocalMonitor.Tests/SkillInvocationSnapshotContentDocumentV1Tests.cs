using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationSnapshotContentDocumentV1Tests
{
    private const string Prefix =
        "{\"schema_version\":\"session-event-content.skill-invoked.v1\",\"payload_utf8_base64\":\"";
    private const string Suffix = "\"}";

    [Fact]
    public void Build_SimplePayloadToken_ProducesExactCanonicalDocumentBytes()
    {
        var payload = Utf8("{\"name\":\"review\"}");
        var expectedBase64 = Convert.ToBase64String(payload);
        var expected = Utf8(Prefix + expectedBase64 + Suffix);

        var document = SkillInvocationSnapshotContentDocumentV1.Build(payload);

        Assert.Equal(expected, document);
        Assert.NotEqual((byte)0xEF, document[0]);
        Assert.Equal((byte)'}', document[^1]);
    }

    [Fact]
    public void TryReadPayloadToken_RoundTripsSeveralPayloadShapes()
    {
        byte[][] payloads =
        [
            Utf8("{\"a\": 1,\n  \"b\":  2}"),
            Utf8("{\"a\":1,\"a\":2}"),
            Utf8("{\"c\":\"a\"}"),
            Utf8("{\"c\":\"\\u0061\"}"),
            Utf8("{\"name\":\"レビュー\"}"),
        ];

        foreach (var payload in payloads)
        {
            var document = SkillInvocationSnapshotContentDocumentV1.Build(payload);
            var ok = SkillInvocationSnapshotContentDocumentV1.TryReadPayloadToken(document, out var recovered, out var failure);

            Assert.True(ok, failure);
            Assert.Equal(payload, recovered);
        }
    }

    [Fact]
    public void Build_EquivalentButDifferentlySpelledPayloads_ProduceDifferentDocumentsAndDigests()
    {
        var plain = Utf8("{\"c\":\"a\"}");
        var escaped = Utf8("{\"c\":\"\\u0061\"}");

        var plainDocument = SkillInvocationSnapshotContentDocumentV1.Build(plain);
        var escapedDocument = SkillInvocationSnapshotContentDocumentV1.Build(escaped);

        Assert.NotEqual(plainDocument, escapedDocument);
        Assert.NotEqual(
            SkillInvocationSnapshotContentDocumentV1.PayloadSha256(plain),
            SkillInvocationSnapshotContentDocumentV1.PayloadSha256(escaped));
    }

    [Fact]
    public void PayloadSha256_IsDigestOfPayloadTokenOnly_AndDiffersFromContentDocumentSha256()
    {
        var payload = Utf8("{\"name\":\"review\"}");
        var document = SkillInvocationSnapshotContentDocumentV1.Build(payload);
        var expectedPayloadDigest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        Assert.Equal(expectedPayloadDigest, SkillInvocationSnapshotContentDocumentV1.PayloadSha256(payload));
        Assert.NotEqual(
            SkillInvocationSnapshotContentDocumentV1.PayloadSha256(payload),
            SkillInvocationSnapshotContentDocumentV1.ContentDocumentSha256(document));
    }

    [Fact]
    public void Build_TwoBytePayload_Builds()
    {
        var payload = Utf8("{}");

        var document = SkillInvocationSnapshotContentDocumentV1.Build(payload);

        Assert.True(SkillInvocationSnapshotContentDocumentV1.TryReadPayloadToken(document, out var recovered, out var failure));
        Assert.Equal(payload, recovered);
        Assert.Equal(string.Empty, failure);
    }

    [Fact]
    public void Build_OneBytePayload_ThrowsArgumentOutOfRangeException()
    {
        var payload = new byte[] { (byte)'{' };

        Assert.Throws<ArgumentOutOfRangeException>(() => SkillInvocationSnapshotContentDocumentV1.Build(payload));
    }

    [Fact]
    public void Build_PayloadOneByteOverMax_ThrowsArgumentOutOfRangeException()
    {
        var payload = new byte[8_388_609];
        Array.Fill(payload, (byte)'a');

        Assert.Throws<ArgumentOutOfRangeException>(() => SkillInvocationSnapshotContentDocumentV1.Build(payload));
    }

    [Fact]
    public void Build_PayloadAtMaxBytes_Builds()
    {
        var payload = new byte[8_388_608];
        Array.Fill(payload, (byte)'a');

        var document = SkillInvocationSnapshotContentDocumentV1.Build(payload);

        Assert.True(SkillInvocationSnapshotContentDocumentV1.TryReadPayloadToken(document, out var recovered, out var failure));
        Assert.Equal(payload, recovered);
        Assert.Equal(string.Empty, failure);
    }

    [Fact]
    public void TryReadPayloadToken_LeadingBom_RejectsWithLeadingBomFailure()
    {
        var mutated = Prepend(ValidDocument(), 0xEF, 0xBB, 0xBF);

        AssertRejected(mutated, "leading_bom");
    }

    [Fact]
    public void TryReadPayloadToken_TrailingLineFeed_RejectsWithTrailingBytesFailure()
    {
        var mutated = Append(ValidDocument(), (byte)'\n');

        AssertRejected(mutated, "trailing_bytes");
    }

    [Fact]
    public void TryReadPayloadToken_TrailingSpace_RejectsWithTrailingBytesFailure()
    {
        var mutated = Append(ValidDocument(), (byte)' ');

        AssertRejected(mutated, "trailing_bytes");
    }

    [Fact]
    public void TryReadPayloadToken_SchemaVersionValueChanged_RejectsWithCanonicalShapeMismatchFailure()
    {
        var mutated = MutatePrefix(ValidDocument(), prefix => prefix.Replace("skill-invoked.v1", "skill-invoked.v2", StringComparison.Ordinal));

        AssertRejected(mutated, "canonical_shape_mismatch");
    }

    [Fact]
    public void TryReadPayloadToken_PropertiesSwapped_RejectsWithCanonicalShapeMismatchFailure()
    {
        var payload = Utf8("{\"name\":\"review\"}");
        var base64 = Convert.ToBase64String(payload);
        var mutated = Utf8(
            "{\"payload_utf8_base64\":\"" + base64 +
            "\",\"schema_version\":\"session-event-content.skill-invoked.v1\"}");

        AssertRejected(mutated, "canonical_shape_mismatch");
    }

    [Fact]
    public void TryReadPayloadToken_ThirdPropertyInserted_RejectsWithCanonicalShapeMismatchFailure()
    {
        var document = ValidDocument();
        var mutated = Concat(document[..^1], Utf8(",\"extra\":\"x\"}"));

        AssertRejected(mutated, "canonical_shape_mismatch");
    }

    [Fact]
    public void TryReadPayloadToken_SpaceAfterColon_RejectsWithCanonicalShapeMismatchFailure()
    {
        var mutated = MutatePrefix(ValidDocument(), prefix => prefix[..^1] + " " + prefix[^1..]);

        AssertRejected(mutated, "canonical_shape_mismatch");
    }

    [Fact]
    public void TryReadPayloadToken_SpaceInsideBase64_RejectsWithBase64WhitespaceFailure()
    {
        var mutated = MutateBase64(ValidDocument(), base64 => base64.Insert(1, " "));

        AssertRejected(mutated, "base64_whitespace");
    }

    [Fact]
    public void TryReadPayloadToken_CarriageReturnInsideBase64_RejectsWithBase64WhitespaceFailure()
    {
        var mutated = MutateBase64(ValidDocument(), base64 => base64.Insert(1, "\r"));

        AssertRejected(mutated, "base64_whitespace");
    }

    [Fact]
    public void TryReadPayloadToken_LineFeedInsideBase64_RejectsWithBase64WhitespaceFailure()
    {
        var mutated = MutateBase64(ValidDocument(), base64 => base64.Insert(1, "\n"));

        AssertRejected(mutated, "base64_whitespace");
    }

    [Fact]
    public void TryReadPayloadToken_HyphenInsideBase64_RejectsWithBase64AlphabetFailure()
    {
        var mutated = MutateBase64(ValidDocument(), base64 => "-" + base64[1..]);

        AssertRejected(mutated, "base64_alphabet");
    }

    [Fact]
    public void TryReadPayloadToken_UnderscoreInsideBase64_RejectsWithBase64AlphabetFailure()
    {
        var mutated = MutateBase64(ValidDocument(), base64 => "_" + base64[1..]);

        AssertRejected(mutated, "base64_alphabet");
    }

    [Fact]
    public void TryReadPayloadToken_Base64LengthNotMultipleOfFour_RejectsWithBase64PaddingFailure()
    {
        var mutated = MutateBase64(ValidDocument(), base64 => base64 + "A");

        AssertRejected(mutated, "base64_padding");
    }

    [Fact]
    public void TryReadPayloadToken_StrayEqualsInMiddleOfBase64_RejectsWithBase64PaddingFailure()
    {
        var mutated = MutateBase64(ValidDocument(), base64 => "=" + base64[1..]);

        AssertRejected(mutated, "base64_padding");
    }

    [Fact]
    public void TryReadPayloadToken_FinalQuantumWithNonZeroDiscardedBits_RejectsWithBase64PaddingFailure()
    {
        var payload = new byte[] { 0x7B, 0x7D, 0x61, 0x62 };
        var document = SkillInvocationSnapshotContentDocumentV1.Build(payload);

        var mutated = MutateBase64(document, base64 =>
        {
            Assert.EndsWith("==", base64, StringComparison.Ordinal);
            var chars = base64.ToCharArray();
            var lastDataCharIndex = chars.Length - 3;
            Assert.NotEqual('B', chars[lastDataCharIndex]);
            chars[lastDataCharIndex] = 'B';
            return new string(chars);
        });

        AssertRejected(mutated, "base64_padding");
    }

    [Fact]
    public void TryReadPayloadToken_NewlineInsideBase64Region_IsRejectedEvenThoughConvertFromBase64StringAcceptsIt()
    {
        var mutated = MutateBase64(ValidDocument(), base64 =>
        {
            var midpoint = base64.Length / 2;
            return base64[..midpoint] + "\n" + base64[midpoint..];
        });
        var mutatedText = Encoding.ASCII.GetString(mutated);
        var mutatedBase64Region = mutatedText[Prefix.Length..^Suffix.Length];

        var acceptedByFrameworkDecoder = Convert.FromBase64String(mutatedBase64Region);
        Assert.NotEmpty(acceptedByFrameworkDecoder);

        AssertRejected(mutated, "base64_whitespace");
    }

    [Fact]
    public void Constants_MatchExactContract()
    {
        Assert.Equal("application/json", SkillInvocationSnapshotContentDocumentV1.ContentKind);
        Assert.Equal("session-event-content.skill-invoked.v1", SkillInvocationSnapshotContentDocumentV1.SchemaVersion);
    }

    private static void AssertRejected(byte[] document, string expectedFailure)
    {
        var ok = SkillInvocationSnapshotContentDocumentV1.TryReadPayloadToken(document, out var recovered, out var failure);

        Assert.False(ok);
        Assert.Equal(expectedFailure, failure);
        Assert.Empty(recovered);
    }

    private static byte[] ValidDocument() =>
        SkillInvocationSnapshotContentDocumentV1.Build(Utf8("{\"name\":\"review\"}"));

    private static byte[] MutateBase64(byte[] document, Func<string, string> mutate)
    {
        var text = Encoding.ASCII.GetString(document);
        Assert.StartsWith(Prefix, text, StringComparison.Ordinal);
        Assert.EndsWith(Suffix, text, StringComparison.Ordinal);
        var base64 = text[Prefix.Length..^Suffix.Length];

        return Utf8(Prefix + mutate(base64) + Suffix);
    }

    private static byte[] MutatePrefix(byte[] document, Func<string, string> mutate)
    {
        var text = Encoding.ASCII.GetString(document);
        Assert.StartsWith(Prefix, text, StringComparison.Ordinal);
        var rest = text[Prefix.Length..];

        return Utf8(mutate(Prefix) + rest);
    }

    private static byte[] Prepend(byte[] document, params byte[] bytes) =>
        Concat(bytes, document);

    private static byte[] Append(byte[] document, params byte[] bytes) =>
        Concat(document, bytes);

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}

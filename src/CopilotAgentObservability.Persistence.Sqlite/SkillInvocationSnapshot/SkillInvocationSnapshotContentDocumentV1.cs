using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

internal static class SkillInvocationSnapshotContentDocumentV1
{
    internal const string SchemaVersion = "session-event-content.skill-invoked.v1";
    internal const string ContentKind = "application/json";

    private const string DocumentPrefixLiteral =
        "{\"schema_version\":\"" + SchemaVersion + "\",\"payload_utf8_base64\":\"";
    private const string DocumentSuffixLiteral = "\"}";

    private const int MinPayloadBytes = 2;
    private const int MaxPayloadBytes = 8_388_608;

    private const string FailureLeadingBom = "leading_bom";
    private const string FailureTrailingBytes = "trailing_bytes";
    private const string FailureShapeMismatch = "canonical_shape_mismatch";
    private const string FailureBase64Whitespace = "base64_whitespace";
    private const string FailureBase64Alphabet = "base64_alphabet";
    private const string FailureBase64Padding = "base64_padding";
    private const string FailurePayloadBytesOutOfRange = "payload_bytes_out_of_range";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] DocumentPrefix = StrictUtf8.GetBytes(DocumentPrefixLiteral);
    private static readonly byte[] DocumentSuffix = StrictUtf8.GetBytes(DocumentSuffixLiteral);
    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    internal static byte[] Build(ReadOnlySpan<byte> payloadTokenUtf8)
    {
        if (payloadTokenUtf8.Length < MinPayloadBytes || payloadTokenUtf8.Length > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadTokenUtf8),
                payloadTokenUtf8.Length,
                "skill_invocation_snapshot_content_document_payload_bytes_out_of_range");
        }

        var base64 = Convert.ToBase64String(payloadTokenUtf8);
        var document = new byte[DocumentPrefix.Length + base64.Length + DocumentSuffix.Length];

        var offset = 0;
        DocumentPrefix.CopyTo(document.AsSpan(offset));
        offset += DocumentPrefix.Length;
        Encoding.ASCII.GetBytes(base64, document.AsSpan(offset));
        offset += base64.Length;
        DocumentSuffix.CopyTo(document.AsSpan(offset));

        return document;
    }

    internal static string ContentDocumentSha256(ReadOnlySpan<byte> documentUtf8) =>
        Hex(SHA256.HashData(documentUtf8));

    internal static string PayloadSha256(ReadOnlySpan<byte> payloadTokenUtf8) =>
        Hex(SHA256.HashData(payloadTokenUtf8));

    internal static bool TryReadPayloadToken(ReadOnlySpan<byte> documentUtf8, out byte[] payloadTokenUtf8, out string failure)
    {
        payloadTokenUtf8 = Array.Empty<byte>();

        if (documentUtf8.Length >= Bom.Length && documentUtf8[..Bom.Length].SequenceEqual(Bom))
        {
            failure = FailureLeadingBom;
            return false;
        }

        if (documentUtf8.Length < DocumentPrefix.Length || !documentUtf8[..DocumentPrefix.Length].SequenceEqual(DocumentPrefix))
        {
            failure = FailureShapeMismatch;
            return false;
        }

        var afterPrefix = documentUtf8[DocumentPrefix.Length..];
        var closingQuoteIndex = afterPrefix.IndexOf((byte)'"');
        if (closingQuoteIndex < 0)
        {
            failure = FailureShapeMismatch;
            return false;
        }

        var base64Region = afterPrefix[..closingQuoteIndex];
        var remainder = afterPrefix[closingQuoteIndex..];
        if (remainder.Length < 2 || remainder[1] != (byte)'}')
        {
            failure = FailureShapeMismatch;
            return false;
        }

        if (remainder.Length > 2)
        {
            failure = FailureTrailingBytes;
            return false;
        }

        // Convert.FromBase64String silently skips embedded whitespace and accepts base64url's
        // '-'/'_' as if they were '+'/'/'; both must be caught here, before decoding, since the
        // decoder gives no signal that it tolerated non-canonical input.
        foreach (var candidate in base64Region)
        {
            if (IsBase64Whitespace(candidate))
            {
                failure = FailureBase64Whitespace;
                return false;
            }

            if (!IsBase64AlphabetCharacter(candidate))
            {
                failure = FailureBase64Alphabet;
                return false;
            }
        }

        if (base64Region.Length == 0 || base64Region.Length % 4 != 0)
        {
            failure = FailureBase64Padding;
            return false;
        }

        var paddingCount = 0;
        while (paddingCount < base64Region.Length && base64Region[base64Region.Length - 1 - paddingCount] == (byte)'=')
        {
            paddingCount++;
        }

        if (paddingCount > 2)
        {
            failure = FailureBase64Padding;
            return false;
        }

        for (var i = 0; i < base64Region.Length - paddingCount; i++)
        {
            if (base64Region[i] == (byte)'=')
            {
                failure = FailureBase64Padding;
                return false;
            }
        }

        var base64String = Encoding.ASCII.GetString(base64Region);
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64String);
        }
        catch (FormatException)
        {
            failure = FailureBase64Padding;
            return false;
        }

        // The final base64 quantum can carry data bits that FromBase64String silently discards
        // (e.g. a non-zero low nibble in a one-byte-remainder quantum). Re-encoding the decoded
        // bytes always yields the canonical, zero-padded spelling, so comparing it back against
        // the original region is the only way to prove no bits were dropped.
        if (!string.Equals(Convert.ToBase64String(decoded), base64String, StringComparison.Ordinal))
        {
            failure = FailureBase64Padding;
            return false;
        }

        if (decoded.Length < MinPayloadBytes || decoded.Length > MaxPayloadBytes)
        {
            failure = FailurePayloadBytesOutOfRange;
            return false;
        }

        payloadTokenUtf8 = decoded;
        failure = string.Empty;
        return true;
    }

    private static bool IsBase64Whitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsBase64AlphabetCharacter(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'+' or (byte)'/' or (byte)'=';

    private static string Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();
}

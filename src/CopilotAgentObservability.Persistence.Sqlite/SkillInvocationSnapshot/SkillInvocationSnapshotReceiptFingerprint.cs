using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

// Fields 11 and 28 are wire-fixed literals ("skill.invoked", "application/json"), not request
// data, so they are hard-coded in BuildFrame instead of being carried on this input.
internal readonly record struct SkillInvocationSnapshotReceiptFingerprintInput(
    string SourceAdapter,
    string SourceEventId,
    string SourceSurface,
    string NativeSessionId,
    string? RunNativeId,
    string? SourceParentEventId,
    bool SourceEphemeral,
    string? TraceId,
    string? SpanId,
    DateTimeOffset OccurredAt,
    string SourceApplicationVersion,
    string AdapterVersion,
    string NormalizationVersion,
    string PayloadSchema,
    string SchemaFingerprint,
    string PayloadSha256,
    ulong PayloadBytes,
    string State,
    string Reason,
    string? Name,
    string? Source,
    string? Trigger,
    string? BodySha256,
    ulong? BodyUtf8Bytes,
    string? DefinitionPathSha256,
    ulong? DefinitionPathUtf8Bytes,
    string ContentDocumentSha256);

internal static class SkillInvocationSnapshotReceiptFingerprint
{
    private const string FramePrefix = "skill-invocation-snapshot-receipt";
    private const string FrameVersion = "v1";
    private const ushort FieldCount = 29;
    private const string SkillInvokedEventType = "skill.invoked";
    private const string ContentDocumentMediaType = "application/json";
    private const string UtcTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static byte[] BuildFrame(in SkillInvocationSnapshotReceiptFingerprintInput input)
    {
        using var stream = new MemoryStream();
        stream.Write(StrictUtf8.GetBytes(FramePrefix));
        stream.WriteByte(0);
        stream.Write(StrictUtf8.GetBytes(FrameVersion));
        stream.WriteByte(0);
        WriteU16BE(stream, FieldCount);

        WriteUtf8Field(stream, 1, input.SourceAdapter);
        WriteUtf8Field(stream, 2, input.SourceEventId);
        WriteUtf8Field(stream, 3, input.SourceSurface);
        WriteUtf8Field(stream, 4, input.NativeSessionId);
        WriteNullableUtf8Field(stream, 5, input.RunNativeId);
        WriteNullableUtf8Field(stream, 6, input.SourceParentEventId);
        WriteBoolField(stream, 7, input.SourceEphemeral);
        WriteNullableUtf8Field(stream, 8, input.TraceId);
        WriteNullableUtf8Field(stream, 9, input.SpanId);
        WriteUtcTimeField(stream, 10, input.OccurredAt);
        WriteUtf8Field(stream, 11, SkillInvokedEventType);
        WriteUtf8Field(stream, 12, input.SourceApplicationVersion);
        WriteUtf8Field(stream, 13, input.AdapterVersion);
        WriteUtf8Field(stream, 14, input.NormalizationVersion);
        WriteUtf8Field(stream, 15, input.PayloadSchema);
        WriteSha256Field(stream, 16, input.SchemaFingerprint);
        WriteSha256Field(stream, 17, input.PayloadSha256);
        WriteUInt64Field(stream, 18, input.PayloadBytes);
        WriteUtf8Field(stream, 19, input.State);
        WriteUtf8Field(stream, 20, input.Reason);
        WriteNullableUtf8Field(stream, 21, input.Name);
        WriteNullableUtf8Field(stream, 22, input.Source);
        WriteNullableUtf8Field(stream, 23, input.Trigger);
        WriteNullableSha256Field(stream, 24, input.BodySha256);
        WriteNullableUInt64Field(stream, 25, input.BodyUtf8Bytes);
        WriteNullableSha256Field(stream, 26, input.DefinitionPathSha256);
        WriteNullableUInt64Field(stream, 27, input.DefinitionPathUtf8Bytes);
        WriteUtf8Field(stream, 28, ContentDocumentMediaType);
        WriteSha256Field(stream, 29, input.ContentDocumentSha256);

        return stream.ToArray();
    }

    internal static string Compute(in SkillInvocationSnapshotReceiptFingerprintInput input) =>
        Hex(SHA256.HashData(BuildFrame(input)));

    private static void WriteUtf8Field(Stream stream, ushort fieldId, string value) =>
        WriteField(stream, fieldId, 0x01, StrictUtf8.GetBytes(value));

    private static void WriteNullableUtf8Field(Stream stream, ushort fieldId, string? value)
    {
        if (value is null)
        {
            WriteNullField(stream, fieldId);
            return;
        }

        WriteUtf8Field(stream, fieldId, value);
    }

    private static void WriteBoolField(Stream stream, ushort fieldId, bool value)
    {
        Span<byte> payload = stackalloc byte[1];
        payload[0] = value ? (byte)0x01 : (byte)0x00;
        WriteField(stream, fieldId, 0x02, payload);
    }

    private static void WriteUInt64Field(Stream stream, ushort fieldId, ulong value)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(payload, value);
        WriteField(stream, fieldId, 0x03, payload);
    }

    private static void WriteNullableUInt64Field(Stream stream, ushort fieldId, ulong? value)
    {
        if (value is null)
        {
            WriteNullField(stream, fieldId);
            return;
        }

        WriteUInt64Field(stream, fieldId, value.Value);
    }

    private static void WriteUtcTimeField(Stream stream, ushort fieldId, DateTimeOffset value)
    {
        var formatted = value.ToUniversalTime().ToString(UtcTimeFormat, CultureInfo.InvariantCulture);
        var payload = StrictUtf8.GetBytes(formatted);
        if (payload.Length != 33)
        {
            throw new InvalidOperationException("skill_invocation_snapshot_receipt_fingerprint_occurred_at_invalid");
        }

        WriteField(stream, fieldId, 0x04, payload);
    }

    private static void WriteSha256Field(Stream stream, ushort fieldId, string hex) =>
        WriteField(stream, fieldId, 0x05, DecodeSha256(hex));

    private static void WriteNullableSha256Field(Stream stream, ushort fieldId, string? hex)
    {
        if (hex is null)
        {
            WriteNullField(stream, fieldId);
            return;
        }

        WriteSha256Field(stream, fieldId, hex);
    }

    private static void WriteNullField(Stream stream, ushort fieldId) =>
        WriteField(stream, fieldId, 0x00, ReadOnlySpan<byte>.Empty);

    private static void WriteField(Stream stream, ushort fieldId, byte kind, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(header, fieldId);
        header[2] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(header[3..], checked((uint)payload.Length));
        stream.Write(header);
        stream.Write(payload);
    }

    private static void WriteU16BE(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static byte[] DecodeSha256(string hex)
    {
        if (hex.Length != 64 || !IsLowercaseHex(hex))
        {
            throw new ArgumentException("skill_invocation_snapshot_receipt_fingerprint_digest_invalid", nameof(hex));
        }

        return Convert.FromHexString(hex);
    }

    private static bool IsLowercaseHex(string value) =>
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();
}

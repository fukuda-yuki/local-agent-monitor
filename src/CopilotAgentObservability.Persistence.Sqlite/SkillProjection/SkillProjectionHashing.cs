using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum SkillProjectionInputEvidenceKind
{
    PayloadSha256,
    DeletedBeforeDigestV10,
}

internal sealed record SkillProjectionFrontierInput(
    long SourceObservationId,
    long RawRecordId,
    SkillProjectionInputEvidenceKind EvidenceKind,
    string? RawPayloadSha256)
{
    internal string EvidenceValue =>
        EvidenceKind == SkillProjectionInputEvidenceKind.PayloadSha256
            ? RawPayloadSha256!
            : "10";
}

internal static class SkillProjectionHashing
{
    private static readonly byte[] ReconciliationDomain =
        "skill-projection-reconcile\0v2\0"u8.ToArray();
    private static readonly byte[] FrontierDomain =
        "skill-projection-frontier\0v2\0"u8.ToArray();

    internal static string ReconciliationFingerprint(
        SourceCompatibilityReconciliationRequest request,
        SkillProjectionFrontierInput input)
    {
        using var stream = new MemoryStream();
        stream.Write(ReconciliationDomain);
        WriteTextFrame(stream, UnsignedDecimal(request.SourceObservationId));
        WriteTextFrame(stream, request.TraceId);
        WriteTextFrame(stream, UnsignedDecimal(request.ExpectedInterpretationRevision));
        WriteTextFrame(stream, UnsignedDecimal(input.RawRecordId));
        WriteTextFrame(stream, Wire(input.EvidenceKind));
        WriteTextFrame(stream, input.EvidenceValue);
        WriteTextFrame(stream, request.ResolverRevision);
        WriteTextFrame(stream, request.RegistryRevision);
        WriteTextFrame(stream, request.ProjectorVersion);
        return Hex(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static string InputDigest(string payloadJson) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));

    internal static string FrontierDigest(
        string traceId,
        IReadOnlyList<SkillProjectionFrontierInput> inputs)
    {
        using var stream = new MemoryStream();
        stream.Write(FrontierDomain);
        WriteTextFrame(stream, traceId);
        WriteTextFrame(stream, checked((uint)inputs.Count).ToString(CultureInfo.InvariantCulture));
        foreach (var input in inputs)
        {
            WriteTextFrame(stream, UnsignedDecimal(input.SourceObservationId));
            WriteTextFrame(stream, UnsignedDecimal(input.RawRecordId));
            WriteTextFrame(stream, Wire(input.EvidenceKind));
            WriteTextFrame(stream, input.EvidenceValue);
        }
        return Hex(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static string Wire(SkillProjectionInputEvidenceKind kind) =>
        kind switch
        {
            SkillProjectionInputEvidenceKind.PayloadSha256 => "payload_sha256",
            SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10 => "deleted_before_digest_v10",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    internal static SkillProjectionInputEvidenceKind ParseEvidenceKind(string value) =>
        value switch
        {
            "payload_sha256" => SkillProjectionInputEvidenceKind.PayloadSha256,
            "deleted_before_digest_v10" => SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10,
            _ => throw new InvalidOperationException("skill_projection_input_evidence_invalid"),
        };

    internal static void ValidateInput(SkillProjectionFrontierInput input)
    {
        if (input.SourceObservationId <= 0 || input.RawRecordId <= 0
            || input.EvidenceKind == SkillProjectionInputEvidenceKind.PayloadSha256
               && !IsLowercaseHash(input.RawPayloadSha256)
            || input.EvidenceKind == SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10
               && input.RawPayloadSha256 is not null)
        {
            throw new InvalidOperationException("skill_projection_input_evidence_invalid");
        }
    }

    private static string UnsignedDecimal(long value) =>
        checked((ulong)value).ToString(CultureInfo.InvariantCulture);

    private static bool IsLowercaseHash(string? value) =>
        value is { Length: 64 }
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void WriteTextFrame(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }

    private static string Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(value).ToLowerInvariant();
}

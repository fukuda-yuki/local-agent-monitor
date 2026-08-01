using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Telemetry.Repositories;

internal static class LocalRepositoryIdentityHashing
{
    private static readonly byte[] SourceIdentityDomain = "local-repository-source-observation\0v1\0"u8.ToArray();
    private static readonly byte[] ContextIdentityDomain = "local-repository-observation-context\0v1\0"u8.ToArray();
    private static readonly byte[] OperationDomain = "local-repository-operation\0v1\0"u8.ToArray();
    private static readonly byte[] AssignmentStateDomain = "local-repository-assignment-state\0v1\0"u8.ToArray();
    private static readonly byte[] ReconciliationDomain = "local-repository-reconcile\0v1\0"u8.ToArray();

    public static string SourceIdentity(LocalRepositorySourceIdentityInput input)
    {
        using var frame = CreateSourceIdentityFrame(input);
        return frame.Digest();
    }

    internal static string SourceIdentityPreimageHex(LocalRepositorySourceIdentityInput input)
    {
        using var frame = CreateSourceIdentityFrame(input);
        return frame.PreimageHex();
    }

    public static string ContextIdentity(LocalRepositoryContextIdentityInput input)
    {
        using var frame = CreateContextIdentityFrame(input);
        return frame.Digest();
    }

    internal static string ContextIdentityPreimageHex(LocalRepositoryContextIdentityInput input)
    {
        using var frame = CreateContextIdentityFrame(input);
        return frame.PreimageHex();
    }

    public static string OperationFingerprint(LocalRepositoryOperationFingerprintInput input)
    {
        using var frame = CreateOperationFingerprintFrame(input);
        return frame.Digest();
    }

    internal static string OperationFingerprintPreimageHex(LocalRepositoryOperationFingerprintInput input)
    {
        using var frame = CreateOperationFingerprintFrame(input);
        return frame.PreimageHex();
    }

    public static string AssignmentStateFingerprint(LocalRepositoryAssignmentState input)
    {
        using var frame = CreateAssignmentStateFingerprintFrame(input);
        return frame.Digest();
    }

    internal static string AssignmentStateFingerprintPreimageHex(LocalRepositoryAssignmentState input)
    {
        using var frame = CreateAssignmentStateFingerprintFrame(input);
        return frame.PreimageHex();
    }

    public static string ReconciliationFingerprint(LocalRepositoryReconciliationEvidence input)
    {
        using var frame = CreateReconciliationFingerprintFrame(input);
        return frame.Digest();
    }

    internal static string ReconciliationFingerprintPreimageHex(LocalRepositoryReconciliationEvidence input)
    {
        using var frame = CreateReconciliationFingerprintFrame(input);
        return frame.PreimageHex();
    }

    private static HashFrame CreateSourceIdentityFrame(LocalRepositorySourceIdentityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.RawRecordId is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input.RawRecordId));
        }
        if (input.ResourceSpanOrdinal < 0 || input.AttributeOrdinal < 0 || string.IsNullOrEmpty(input.AttributeKey))
        {
            throw new ArgumentException("Invalid source identity input.", nameof(input));
        }

        var (scopeSpanOrdinal, spanOrdinal, discriminator) = input.ScopeKind switch
        {
            LocalRepositoryObservationScopeKind.Resource when input.ScopeSpanOrdinal is null && input.SpanOrdinal is null =>
                (uint.MaxValue, uint.MaxValue, (byte)0x01),
            LocalRepositoryObservationScopeKind.Span when input.ScopeSpanOrdinal is >= 0 && input.SpanOrdinal is >= 0 =>
                (checked((uint)input.ScopeSpanOrdinal.Value), checked((uint)input.SpanOrdinal.Value), (byte)0x02),
            _ => throw new ArgumentException("Invalid source identity scope coordinates.", nameof(input)),
        };

        var frame = new HashFrame();
        frame.Append(SourceIdentityDomain);
        AppendUInt64(frame, checked((ulong)input.RawRecordId));
        AppendUInt32(frame, checked((uint)input.ResourceSpanOrdinal));
        AppendUInt32(frame, scopeSpanOrdinal);
        AppendUInt32(frame, spanOrdinal);
        frame.Append([discriminator]);
        AppendUInt32(frame, checked((uint)input.AttributeOrdinal));
        AppendTextFrame(frame, input.AttributeKey);
        return frame;
    }

    private static HashFrame CreateContextIdentityFrame(LocalRepositoryContextIdentityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sourceIdentity = DecodeLowercaseHex(input.SourceIdentitySha256, 64, nameof(input.SourceIdentitySha256));
        var traceId = DecodeLowercaseHex(input.TraceId, 32, nameof(input.TraceId));
        var spanId = DecodeLowercaseHex(input.SpanId, 16, nameof(input.SpanId));
        ValidateCanonicalUuid(input.SessionId, nameof(input.SessionId));
        ValidateCanonicalUuid(input.SessionEventId, nameof(input.SessionEventId));
        var frame = new HashFrame();
        frame.Append(ContextIdentityDomain);
        frame.Append(sourceIdentity);
        AppendTextFrame(frame, input.SessionId);
        AppendTextFrame(frame, input.SessionEventId);
        frame.Append(traceId);
        frame.Append(spanId);
        return frame;
    }

    private static HashFrame CreateOperationFingerprintFrame(LocalRepositoryOperationFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var frame = new HashFrame();
        frame.Append(OperationDomain);
        AppendUInt32(frame, 9);
        AppendNamedField(frame, "method", input.Method);
        AppendNamedField(frame, "route_template", input.RouteTemplate);
        AppendNamedField(frame, "operation", input.Operation);
        AppendNamedField(frame, "target_id", input.TargetId);
        AppendNamedField(frame, "expected_revision", input.ExpectedRevision);
        AppendNamedField(frame, "display_name", input.DisplayName);
        AppendNamedField(frame, "canonical_locator", input.CanonicalLocator);
        AppendNamedField(frame, "session_action", input.SessionAction);
        AppendNamedField(frame, "repository_id", input.RepositoryId);
        return frame;
    }

    private static HashFrame CreateAssignmentStateFingerprintFrame(LocalRepositoryAssignmentState input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.CandidateRepositoryIds);
        ValidateCanonicalUuidWhenPresent(input.RepositoryId, nameof(input.RepositoryId));
        var candidates = input.CandidateRepositoryIds.Select(candidate =>
        {
            ValidateCanonicalUuid(candidate, nameof(input.CandidateRepositoryIds));
            return candidate;
        }).Distinct(StringComparer.Ordinal).OrderBy(candidate => candidate, CanonicalUuidByteComparer.Instance).ToArray();
        var frame = new HashFrame();
        frame.Append(AssignmentStateDomain);
        AppendTextFrame(frame, input.State);
        AppendTextFrame(frame, input.Authority);
        AppendNullableTextFrame(frame, input.RepositoryId);
        AppendUInt32(frame, checked((uint)candidates.Length));
        foreach (var candidate in candidates)
        {
            AppendTextFrame(frame, candidate);
        }
        return frame;
    }

    private static HashFrame CreateReconciliationFingerprintFrame(LocalRepositoryReconciliationEvidence input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.RawRecordId is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input.RawRecordId));
        }
        var (kind, evidence) = input.Kind switch
        {
            LocalRepositoryReconciliationEvidenceKind.PayloadSha256 when IsLowercaseHex(input.RawPayloadSha256, 64) => ("payload_sha256", input.RawPayloadSha256!),
            LocalRepositoryReconciliationEvidenceKind.InputUnavailable when input.RawPayloadSha256 is null => ("input_unavailable", "unavailable"),
            _ => throw new ArgumentException("Invalid reconciliation evidence.", nameof(input)),
        };
        var frame = new HashFrame();
        frame.Append(ReconciliationDomain);
        AppendUInt64(frame, checked((ulong)input.RawRecordId));
        AppendTextFrame(frame, kind);
        AppendTextFrame(frame, evidence);
        AppendTextFrame(frame, "local-repository-catalog:1");
        return frame;
    }

    private static void AppendNamedField(HashFrame frame, string name, string? value)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)nameBytes.Length));
        frame.Append(length);
        frame.Append(nameBytes);
        AppendNullableTextFrame(frame, value);
    }

    private static void AppendNullableTextFrame(HashFrame frame, string? value)
    {
        frame.Append([(byte)(value is null ? 0 : 1)]);
        if (value is not null)
        {
            AppendTextFrame(frame, value);
        }
    }

    private static void AppendTextFrame(HashFrame frame, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendUInt32(frame, checked((uint)bytes.Length));
        frame.Append(bytes);
    }

    private static void AppendUInt32(HashFrame frame, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        frame.Append(bytes);
    }

    private static void AppendUInt64(HashFrame frame, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        frame.Append(bytes);
    }

    private static byte[] DecodeLowercaseHex(string value, int length, string parameterName)
    {
        if (!IsLowercaseHex(value, length))
        {
            throw new ArgumentException("Value must be lowercase hexadecimal with the required length.", parameterName);
        }

        return Convert.FromHexString(value);
    }

    private static bool IsLowercaseHex(string? value, int length) =>
        value is { Length: var actualLength } && actualLength == length
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateCanonicalUuidWhenPresent(string? value, string parameterName)
    {
        if (value is not null)
        {
            ValidateCanonicalUuid(value, parameterName);
        }
    }

    private static void ValidateCanonicalUuid(string value, string parameterName)
    {
        if (value is not { Length: 36 }
            || value[8] != '-'
            || value[13] != '-'
            || value[18] != '-'
            || value[23] != '-'
            || !value.Where((_, index) => index is not 8 and not 13 and not 18 and not 23)
                .All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("Value must be a canonical lowercase UUID.", parameterName);
        }
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed class HashFrame : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly ArrayBufferWriter<byte> preimage = new();

        public void Append(ReadOnlySpan<byte> bytes)
        {
            hash.AppendData(bytes);
            bytes.CopyTo(preimage.GetSpan(bytes.Length));
            preimage.Advance(bytes.Length);
        }

        public string Digest() => Hex(hash.GetHashAndReset());

        public string PreimageHex() => Hex(preimage.WrittenSpan);

        public void Dispose() => hash.Dispose();
    }

    private sealed class CanonicalUuidByteComparer : IComparer<string>
    {
        public static CanonicalUuidByteComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return -1;
            }
            if (right is null)
            {
                return 1;
            }

            var leftBytes = Convert.FromHexString(left.Replace("-", string.Empty, StringComparison.Ordinal));
            var rightBytes = Convert.FromHexString(right.Replace("-", string.Empty, StringComparison.Ordinal));
            return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
        }
    }
}

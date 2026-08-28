using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalComparisonAcceptStatus
{
    Accepted,
    Identical,
    PersistenceBusy,
}

internal enum LocalComparisonReadStatus
{
    Found,
    NotFound,
    Expired,
    PersistenceBusy,
}

internal enum LocalComparisonCleanupStatus
{
    Completed,
    PersistenceBusy,
}

internal sealed record LocalComparisonCleanupResult(
    LocalComparisonCleanupStatus Status,
    int CleanedCount);

internal sealed record LocalComparisonStoredMembership(
    string ComparisonId,
    string Cohort,
    int Ordinal,
    string SessionId,
    string WorkspaceRevision,
    byte[] FactFrame,
    string FactSha256)
{
    internal static LocalComparisonStoredMembership Create(
        string comparisonId,
        string cohort,
        int ordinal,
        string sessionId,
        string workspaceRevision,
        byte[] factFrame)
    {
        ArgumentNullException.ThrowIfNull(factFrame);
        var frozen = factFrame.ToArray();
        return new(
            comparisonId,
            cohort,
            ordinal,
            sessionId,
            workspaceRevision,
            frozen,
            Convert.ToHexStringLower(SHA256.HashData(frozen)));
    }
}

internal sealed record LocalComparisonStoredResult(
    string ComparisonId,
    int ResultOrdinal,
    int SectionOrdinal,
    string RowKind,
    string RowKey,
    byte[] Payload,
    string PayloadSha256,
    IReadOnlyList<KeyValuePair<string, string>> Values)
{
    internal static LocalComparisonStoredResult Create(
        string comparisonId,
        int resultOrdinal,
        int sectionOrdinal,
        string rowKind,
        string rowKey,
        IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var frozenValues = LocalComparisonResultPayloadCodec.Freeze(values);
        var payload = LocalComparisonResultPayloadCodec.Encode(
            sectionOrdinal,
            rowKind,
            rowKey,
            frozenValues);
        return new(
            comparisonId,
            resultOrdinal,
            sectionOrdinal,
            rowKind,
            rowKey,
            payload,
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            frozenValues);
    }

    internal static LocalComparisonStoredResult Read(
        string comparisonId,
        int resultOrdinal,
        int sectionOrdinal,
        string rowKind,
        string rowKey,
        byte[] payload,
        string payloadSha256)
    {
        if (!string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(payload)),
                payloadSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("local_comparison_result_hash_invalid");
        }
        var values = resultOrdinal == 0
            && sectionOrdinal == 0
            && rowKind == "receipt"
            && rowKey == "comparison_receipt"
            ? Array.AsReadOnly(new[]
            {
                new KeyValuePair<string, string>("receipt_sha256", payloadSha256),
            })
            : LocalComparisonResultPayloadCodec.Decode(
                payload,
                sectionOrdinal,
                rowKind,
                rowKey);
        return new(
            comparisonId,
            resultOrdinal,
            sectionOrdinal,
            rowKind,
            rowKey,
            payload.ToArray(),
            payloadSha256,
            values);
    }
}

internal static class LocalComparisonReceiptFrame
{
    private const string Domain =
        "copilot-agent-observability/local-comparison-receipt/v1";

    internal static LocalComparisonStoredResult CreateResult(
        string comparisonId,
        string repositoryId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        byte[] selectionFrame,
        string selectionSha256,
        byte[] scopeConditionSha256,
        IReadOnlyList<LocalComparisonStoredMembership> memberships,
        IReadOnlyList<LocalComparisonStoredResult> results,
        IReadOnlyList<LocalComparisonStoredEvidence> evidence)
    {
        var payload = Create(
            comparisonId, repositoryId, createdAt, expiresAt,
            selectionFrame, selectionSha256, scopeConditionSha256,
            memberships, results, evidence);
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        return new(
            comparisonId,
            0,
            0,
            "receipt",
            "comparison_receipt",
            payload,
            hash,
            Array.AsReadOnly(new[]
            {
                new KeyValuePair<string, string>("receipt_sha256", hash),
            }));
    }

    internal static byte[] Create(
        string comparisonId,
        string repositoryId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        byte[] selectionFrame,
        string selectionSha256,
        byte[] scopeConditionSha256,
        IReadOnlyList<LocalComparisonStoredMembership> memberships,
        IReadOnlyList<LocalComparisonStoredResult> results,
        IReadOnlyList<LocalComparisonStoredEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(scopeConditionSha256);
        if (scopeConditionSha256.Length != 32)
            throw new ArgumentException("local_comparison_scope_condition_digest_invalid");
        using var stream = new MemoryStream();
        Write(stream, Domain);
        Write(stream, comparisonId);
        Write(stream, repositoryId);
        Write(stream, Timestamp(createdAt));
        Write(stream, Timestamp(expiresAt));
        Write(stream, selectionFrame);
        Write(stream, selectionSha256);
        Write(stream, "scope_condition_sha256");
        Write(stream, scopeConditionSha256);
        Write(stream, memberships.Count);
        foreach (var item in memberships)
        {
            Write(stream, item.ComparisonId);
            Write(stream, item.Cohort);
            Write(stream, item.Ordinal);
            Write(stream, item.SessionId);
            Write(stream, item.WorkspaceRevision);
            Write(stream, item.FactFrame);
            Write(stream, item.FactSha256);
        }
        var nonReceiptResults = results.Where(static item => item.ResultOrdinal != 0).ToArray();
        Write(stream, nonReceiptResults.Length);
        foreach (var item in nonReceiptResults)
        {
            Write(stream, item.ComparisonId);
            Write(stream, item.ResultOrdinal);
            Write(stream, item.SectionOrdinal);
            Write(stream, item.RowKind);
            Write(stream, item.RowKey);
            Write(stream, item.Payload);
            Write(stream, item.PayloadSha256);
        }
        Write(stream, evidence.Count);
        foreach (var item in evidence)
        {
            Write(stream, item.ComparisonId);
            Write(stream, item.ResultOrdinal);
            Write(stream, item.EvidenceOrdinal);
            Write(stream, item.FieldKey);
            Write(stream, item.Cohort);
            Write(stream, item.SessionId);
            Write(stream, item.AvailabilityState);
            WriteNullable(stream, item.SourceKind);
            WriteNullable(stream, item.SourceIdentity);
            WriteNullable(stream, item.TraceId);
            WriteNullable(stream, item.SpanId);
            WriteNullable(stream, item.EventId);
            WriteNullable(stream, item.RevisionSha256);
        }
        var payload = stream.ToArray();
        if (payload.Length is < 1 or > 1_048_576)
            throw new LocalComparisonTooLargeException();
        return payload;
    }

    private static void Write(Stream stream, string value)
    {
        LocalComparisonSelectionFrame.WriteFrame(stream, value);
        EnsureBound(stream);
    }

    private static void Write(Stream stream, byte[] value)
    {
        LocalComparisonSelectionFrame.WriteFrame(stream, value);
        EnsureBound(stream);
    }

    private static void Write(Stream stream, int value) =>
        Write(stream, value.ToString(CultureInfo.InvariantCulture));

    private static void WriteNullable(Stream stream, string? value)
    {
        Write(stream, value is null ? "0" : "1");
        if (value is not null)
            Write(stream, value);
    }

    private static void EnsureBound(Stream stream)
    {
        if (stream.Length > 1_048_576)
            throw new LocalComparisonTooLargeException();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

internal sealed class LocalComparisonTooLargeException : Exception
{
    internal LocalComparisonTooLargeException() : base("local_comparison_too_large") { }
}

internal sealed record LocalComparisonStoredEvidence(
    string ComparisonId,
    int ResultOrdinal,
    int EvidenceOrdinal,
    string FieldKey,
    string Cohort,
    string SessionId,
    string AvailabilityState,
    string? SourceKind,
    string? SourceIdentity,
    string? TraceId,
    string? SpanId,
    string? EventId,
    string? RevisionSha256);

internal sealed record LocalComparisonSnapshotWrite(
    string ComparisonId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    byte[] SelectionFrame,
    string SelectionSha256,
    byte[] ScopeConditionSha256,
    IReadOnlyList<LocalComparisonStoredMembership> Memberships,
    IReadOnlyList<LocalComparisonStoredResult> Results,
    IReadOnlyList<LocalComparisonStoredEvidence> Evidence);

internal sealed record LocalComparisonFrozenSnapshot(
    string ComparisonId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    byte[] SelectionFrame,
    string SelectionSha256,
    byte[] ScopeConditionSha256,
    IReadOnlyList<LocalComparisonStoredMembership> Memberships,
    IReadOnlyList<LocalComparisonStoredResult> Results,
    IReadOnlyList<LocalComparisonStoredEvidence> Evidence);

internal sealed record LocalComparisonReadResult(
    LocalComparisonReadStatus Status,
    LocalComparisonFrozenSnapshot? Snapshot);

internal static class LocalComparisonResultPayloadCodec
{
    private const string Domain =
        "copilot-agent-observability/local-comparison-result/v1";

    internal static IReadOnlyList<KeyValuePair<string, string>> Freeze(
        IReadOnlyList<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 4096)
            throw new LocalComparisonTooLargeException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new KeyValuePair<string, string>[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var pair = values[index];
            if (!LocalComparisonBoundedText.IsToken(pair.Key, 128)
                || !seen.Add(pair.Key))
            {
                throw new ArgumentException("local_comparison_result_values_invalid");
            }
            if (!LocalComparisonBoundedText.IsText(pair.Value, 16_384))
            {
                if (LocalComparisonBoundedText.IsText(pair.Value, int.MaxValue))
                    throw new LocalComparisonTooLargeException();
                throw new ArgumentException("local_comparison_result_values_invalid");
            }
            result[index] = pair;
        }
        return Array.AsReadOnly(result);
    }

    internal static byte[] Encode(
        int sectionOrdinal,
        string rowKind,
        string rowKey,
        IReadOnlyList<KeyValuePair<string, string>> values)
    {
        using var stream = new MemoryStream();
        Write(Domain);
        Write(
            sectionOrdinal.ToString(CultureInfo.InvariantCulture));
        Write(rowKind);
        Write(rowKey);
        Write(
            values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in values)
        {
            Write(pair.Key);
            Write(pair.Value);
        }
        var result = stream.ToArray();
        if (result.Length is < 1 or > 1_048_576)
            throw new LocalComparisonTooLargeException();
        return result;

        void Write(string value)
        {
            LocalComparisonSelectionFrame.WriteFrame(stream, value);
            if (stream.Length > 1_048_576)
                throw new LocalComparisonTooLargeException();
        }
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> Decode(
        byte[] payload,
        int expectedSectionOrdinal,
        string expectedRowKind,
        string expectedRowKey)
    {
        var reader = new LocalComparisonFrameReader(payload);
        if (reader.Read() != Domain
            || reader.Read() != expectedSectionOrdinal.ToString(CultureInfo.InvariantCulture)
            || reader.Read() != expectedRowKind
            || reader.Read() != expectedRowKey
            || !int.TryParse(reader.Read(), NumberStyles.None, CultureInfo.InvariantCulture,
                out var count)
            || count is < 0 or > 4096)
        {
            throw new InvalidOperationException("local_comparison_result_payload_invalid");
        }
        var values = new KeyValuePair<string, string>[count];
        for (var index = 0; index < count; index++)
            values[index] = new(reader.Read(), reader.Read());
        if (!reader.AtEnd)
            throw new InvalidOperationException("local_comparison_result_payload_invalid");
        try
        {
            return Freeze(values);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "local_comparison_result_payload_invalid",
                exception);
        }
    }
}

internal ref struct LocalComparisonFrameReader
{
    private readonly ReadOnlySpan<byte> bytes;
    private int offset;

    internal LocalComparisonFrameReader(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
        offset = 0;
    }

    internal bool AtEnd => offset == bytes.Length;

    internal string Read()
    {
        if (bytes.Length - offset < 4)
            throw new InvalidOperationException("local_comparison_frame_invalid");
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
            bytes.Slice(offset, 4));
        offset += 4;
        if (length > int.MaxValue || bytes.Length - offset < (int)length)
            throw new InvalidOperationException("local_comparison_frame_invalid");
        try
        {
            var value = new System.Text.UTF8Encoding(false, true).GetString(
                bytes.Slice(offset, (int)length));
            offset += (int)length;
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("local_comparison_frame_invalid", exception);
        }
    }
}

internal static class LocalComparisonBoundedText
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsToken(string? value, int maxBytes) =>
        IsText(value, maxBytes)
        && value!.All(static character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_' or '-' or '.' or ':' or '/');

    internal static bool IsText(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        try
        {
            return StrictUtf8.GetByteCount(value) <= maxBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

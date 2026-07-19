using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal static class RetentionOwnershipReceipt
{
    private static readonly byte[] Domain = "copilot-agent-observability/retention-owner-receipt/v1"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] CreateSession(RetentionSessionOwnershipReceiptInput input)
    {
        if (!Valid(input, RetentionStoreKind.SessionEventContent, out var writer)
            || !CanonicalGuid(input.EventId) || !CanonicalGuid(input.SessionId) || (input.RunId is not null && !CanonicalGuid(input.RunId))
            || !Timestamp(input.CapturedAtText, input.CapturedAtUtcTicks) || !Timestamp(input.ExpiresAtText, input.ExpiresAtUtcTicks)) throw Invalid();
        writer.Guid(input.EventId); writer.Text(input.Kind); writer.Timestamp(input.CapturedAtText, input.CapturedAtUtcTicks); writer.Timestamp(input.ExpiresAtText, input.ExpiresAtUtcTicks); writer.Guid(input.SessionId); writer.OptionalGuid(input.RunId); writer.Text(input.SourceAdapter); writer.Text(input.SourceEventId); return writer.Finish(input.OwnerToken);
    }

    internal static byte[] CreateRawRecord(RetentionRawRecordReceiptInput input)
    {
        if (!Valid(input, RetentionStoreKind.RawRecord, out var writer) || input.Id <= 0 || input.SchemaVersion <= 0 || !Timestamp(input.ReceivedAtText, input.ReceivedAtUtcTicks)) throw Invalid();
        writer.Int64(input.Id); writer.Timestamp(input.ReceivedAtText, input.ReceivedAtUtcTicks); writer.Int64(input.SchemaVersion); return writer.Finish(input.OwnerToken);
    }

    internal static byte[] CreateAnalysisRun(RetentionAnalysisRunOwnershipReceiptInput input)
    {
        if (!Valid(input, RetentionStoreKind.AnalysisRunRaw, out var writer) || input.RunId <= 0 || (input.RecordId is <= 0) || !Timestamp(input.RequestedAtText, input.RequestedAtUtcTicks)) throw Invalid();
        writer.Int64(input.RunId); writer.Timestamp(input.RequestedAtText, input.RequestedAtUtcTicks); writer.OptionalPositive(input.RecordId); writer.OptionalText(input.SpanId); return writer.Finish(input.OwnerToken);
    }

    internal static byte[] CreateSensitiveBundle(RetentionSensitiveBundleOwnershipReceiptInput input)
    {
        if (!Valid(input, RetentionStoreKind.SensitiveBundle, out var writer)
            || !CanonicalCaptureId(input.CaptureId)
            || !Timestamp(input.ReservedAtText, input.ReservedAtUtcTicks)
            || input.MarkerSha256 is not { Length: 32 }
            || input.ManifestSha256 is not { Length: 32 }) throw Invalid();
        writer.Text(input.CaptureId); writer.Timestamp(input.ReservedAtText, input.ReservedAtUtcTicks); writer.Bytes(input.MarkerSha256); writer.Bytes(input.ManifestSha256); return writer.Finish(input.OwnerToken);
    }

    internal static byte[] CreateAnalysisSdkDirectory(RetentionAnalysisSdkDirectoryOwnershipReceiptInput input)
    {
        if (!Valid(input, RetentionStoreKind.AnalysisSdkDirectory, out var writer) || !CanonicalCaptureId(input.CaptureId) || input.AnalysisRunId <= 0 || !Timestamp(input.RequestedAtText, input.RequestedAtUtcTicks) || input.MarkerSha256 is not { Length: 32 }) throw Invalid();
        writer.Text(input.CaptureId); writer.Int64(input.AnalysisRunId); writer.Timestamp(input.RequestedAtText, input.RequestedAtUtcTicks); writer.Bytes(input.MarkerSha256); return writer.Finish(input.OwnerToken);
    }

    internal static bool Matches(byte[] expected, byte[] actual) =>
        expected is { Length: 32 } && actual is { Length: 32 } && CryptographicOperations.FixedTimeEquals(expected, actual);

    private static bool Valid(RetentionOwnershipReceiptInput input, RetentionStoreKind kind, out CanonicalWriter writer)
    {
        writer = null!;
        if (input is null || input.OwnerToken is not { Length: 32 } || !TryStoreInstance(input.StoreInstanceId, out var storeInstance)) return false;
        writer = new CanonicalWriter(storeInstance, kind); return true;
    }

    private static bool TryStoreInstance(string value, out byte[] bytes) =>
        value is { Length: 32 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? TryDecode(value, out bytes)
            : Fail(out bytes);

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = Convert.FromHexString(value);
        return bytes.Length == 16;
    }

    private static bool Fail(out byte[] bytes) { bytes = []; return false; }

    private static bool CanonicalGuid(string? value) => value is not null && Guid.TryParseExact(value, "D", out var guid) && string.Equals(value, guid.ToString("D"), StringComparison.Ordinal);
    private static bool CanonicalCaptureId(string? value) => value is { Length: 32 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Timestamp(string? text, long ticks) => text is not null && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && value.UtcDateTime.Ticks == ticks;
    private static ArgumentException Invalid() => new("Invalid retention ownership receipt input.");

    private sealed class CanonicalWriter
    {
        private readonly MemoryStream stream = new();
        internal CanonicalWriter(byte[] storeInstance, RetentionStoreKind kind) { Bytes(Domain); Bytes(storeInstance); Text(kind switch { RetentionStoreKind.SessionEventContent => "session_event_content", RetentionStoreKind.RawRecord => "raw_record", RetentionStoreKind.AnalysisRunRaw => "analysis_run_raw", RetentionStoreKind.SensitiveBundle => "sensitive_bundle", RetentionStoreKind.AnalysisSdkDirectory => "analysis_sdk_directory", _ => throw Invalid() }); }
        internal void Text(string value)
        {
            if (value is null) throw Invalid();
            try { Bytes(StrictUtf8.GetBytes(value)); }
            catch (EncoderFallbackException) { throw Invalid(); }
        }
        internal void OptionalText(string? value) { stream.WriteByte(value is null ? (byte)0 : (byte)1); if (value is not null) Text(value); }
        internal void Guid(string value) { var guid = System.Guid.ParseExact(value, "D"); Span<byte> bytes = stackalloc byte[16]; guid.TryWriteBytes(bytes, bigEndian: true, out _); Bytes(bytes); }
        internal void OptionalGuid(string? value) { stream.WriteByte(value is null ? (byte)0 : (byte)1); if (value is not null) Guid(value); }
        internal void OptionalPositive(long? value) { stream.WriteByte(value.HasValue ? (byte)1 : (byte)0); if (value is not null) { if (value <= 0) throw Invalid(); Int64(value.Value); } }
        internal void Timestamp(string text, long ticks) { Text(text); Int64(ticks); }
        internal void Int64(long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); stream.Write(bytes); }
        internal void Bytes(ReadOnlySpan<byte> value) { Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, value.Length); stream.Write(length); stream.Write(value); }
        internal byte[] Finish(byte[] token) { Bytes(token); return SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)); }
    }
}

internal abstract record RetentionOwnershipReceiptInput(string StoreInstanceId, byte[] OwnerToken);
internal sealed record RetentionSessionOwnershipReceiptInput(
    string StoreInstanceId, string EventId, string Kind, string CapturedAtText, long CapturedAtUtcTicks, string ExpiresAtText, long ExpiresAtUtcTicks, string SessionId, string? RunId, string SourceAdapter, string SourceEventId, byte[] OwnerToken) : RetentionOwnershipReceiptInput(StoreInstanceId, OwnerToken);
internal sealed record RetentionRawRecordReceiptInput(
    string StoreInstanceId,
    long Id,
    string ReceivedAtText,
    long ReceivedAtUtcTicks,
    int SchemaVersion,
    byte[] OwnerToken) : RetentionOwnershipReceiptInput(StoreInstanceId, OwnerToken);
internal sealed record RetentionAnalysisRunOwnershipReceiptInput(string StoreInstanceId, long RunId, string RequestedAtText, long RequestedAtUtcTicks, long? RecordId, string? SpanId, byte[] OwnerToken) : RetentionOwnershipReceiptInput(StoreInstanceId, OwnerToken);
internal sealed record RetentionSensitiveBundleOwnershipReceiptInput(string StoreInstanceId, string CaptureId, string ReservedAtText, long ReservedAtUtcTicks, byte[] MarkerSha256, byte[] ManifestSha256, byte[] OwnerToken) : RetentionOwnershipReceiptInput(StoreInstanceId, OwnerToken);
internal sealed record RetentionAnalysisSdkDirectoryOwnershipReceiptInput(string StoreInstanceId, string CaptureId, long AnalysisRunId, string RequestedAtText, long RequestedAtUtcTicks, byte[] MarkerSha256, byte[] OwnerToken) : RetentionOwnershipReceiptInput(StoreInstanceId, OwnerToken);

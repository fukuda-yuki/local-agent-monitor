using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal static class RetentionAnalysisSdkDirectoryOwnershipMarker
{
    private static readonly byte[] Domain = "copilot-agent-observability/retention-analysis-sdk-directory-owner-marker/v1"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Create(string storeInstanceId, string captureId, long analysisRunId, string requestedAtText, long requestedAtUtcTicks, byte[] ownerToken)
    {
        if (storeInstanceId is not { Length: 32 } || !storeInstanceId.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || captureId is not { Length: 32 } || !captureId.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || analysisRunId <= 0 || ownerToken is not { Length: 32 } || !DateTimeOffset.TryParseExact(requestedAtText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var requested) || requested.UtcDateTime.Ticks != requestedAtUtcTicks) throw new ArgumentException("Invalid retention analysis SDK directory ownership marker input.");
        using var stream = new MemoryStream();
        Frame(stream, Domain); Frame(stream, Convert.FromHexString(storeInstanceId)); Frame(stream, StrictUtf8.GetBytes("analysis_sdk_directory")); Frame(stream, StrictUtf8.GetBytes(captureId));
        Span<byte> run = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(run, analysisRunId); Frame(stream, run); Frame(stream, StrictUtf8.GetBytes(requestedAtText)); BinaryPrimitives.WriteInt64BigEndian(run, requestedAtUtcTicks); Frame(stream, run); Frame(stream, ownerToken);
        return stream.ToArray();
    }

    private static void Frame(Stream stream, ReadOnlySpan<byte> value) { Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, value.Length); stream.Write(length); stream.Write(value); }
}

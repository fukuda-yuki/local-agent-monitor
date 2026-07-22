using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.RawReplay;

internal static class RawReplayHash
{
    internal static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string Framed(string domain, params byte[][] values)
    {
        using var stream = new MemoryStream();
        Write(stream, Encoding.UTF8.GetBytes(domain));
        foreach (var value in values) Write(stream, value);
        return Sha256(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static void Write(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal static class SetupHash
{
    private const byte MissingFile = 0;
    private const byte PresentFile = 1;

    public static string File(bool exists, ReadOnlySpan<byte> bytes)
    {
        if (!exists && !bytes.IsEmpty)
        {
            throw new ArgumentException("Missing files cannot have content.", nameof(bytes));
        }

        Span<byte> header = stackalloc byte[9];
        header[0] = exists ? PresentFile : MissingFile;
        BinaryPrimitives.WriteUInt64BigEndian(header[1..], exists ? (ulong)bytes.Length : 0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        if (exists)
        {
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

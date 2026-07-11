using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal sealed record ApplyHunk(string HunkId, string ReplacementText, bool Selected);

internal static class LineDiff
{
    public static IReadOnlyList<ApplyHunk> Create(string original, string replacement)
    {
        if (string.Equals(original, replacement, StringComparison.Ordinal)) return [];
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{original.Length}:{original}\n{replacement}"))).ToLowerInvariant()[..24];
        return [new ApplyHunk(id, replacement, true)];
    }

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

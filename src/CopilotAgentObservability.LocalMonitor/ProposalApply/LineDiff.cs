using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal sealed record ApplyHunk(string HunkId, string RelativePath, int StartLine, int BaseLineCount, string ReplacementText, bool Selected);

internal static class LineDiff
{
    public static IReadOnlyList<ApplyHunk> Create(string relativePath, string original, string replacement)
    {
        if (string.Equals(original, replacement, StringComparison.Ordinal)) return [];
        var source = SplitLines(original);
        var output = SplitLines(replacement);
        var lcs = new int[source.Count + 1, output.Count + 1];
        for (var i = source.Count - 1; i >= 0; i--)
        for (var j = output.Count - 1; j >= 0; j--)
            lcs[i, j] = source[i] == output[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var hunks = new List<ApplyHunk>();
        var sourceIndex = 0;
        var outputIndex = 0;
        while (sourceIndex < source.Count || outputIndex < output.Count)
        {
            if (sourceIndex < source.Count && outputIndex < output.Count && source[sourceIndex] == output[outputIndex]) { sourceIndex++; outputIndex++; continue; }
            var start = sourceIndex;
            var replacementLines = new List<string>();
            while (sourceIndex < source.Count || outputIndex < output.Count)
            {
                if (sourceIndex < source.Count && outputIndex < output.Count && source[sourceIndex] == output[outputIndex]) break;
                if (outputIndex < output.Count && (sourceIndex == source.Count || lcs[sourceIndex, outputIndex + 1] >= lcs[sourceIndex + 1, outputIndex])) replacementLines.Add(output[outputIndex++]);
                else sourceIndex++;
            }
            var text = string.Concat(replacementLines);
            var id = Sha256($"{relativePath}\n{start}\n{sourceIndex - start}\n{text}")[..24];
            hunks.Add(new ApplyHunk(id, relativePath, start, sourceIndex - start, text, true));
        }
        return hunks;
    }

    public static string Replay(string original, IEnumerable<ApplyHunk> selected)
    {
        var lines = SplitLines(original);
        var result = new List<string>();
        var cursor = 0;
        foreach (var hunk in selected.OrderBy(item => item.StartLine))
        {
            if (hunk.StartLine < cursor || hunk.StartLine + hunk.BaseLineCount > lines.Count) throw new ApplyPathException("invalid_selection");
            result.AddRange(lines.Skip(cursor).Take(hunk.StartLine - cursor));
            result.AddRange(SplitLines(hunk.ReplacementText));
            cursor = hunk.StartLine + hunk.BaseLineCount;
        }
        result.AddRange(lines.Skip(cursor));
        return string.Concat(result);
    }

    private static List<string> SplitLines(string value)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
            if (value[index] == '\n') { result.Add(value[start..(index + 1)]); start = index + 1; }
        if (start < value.Length) result.Add(value[start..]);
        return result;
    }

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

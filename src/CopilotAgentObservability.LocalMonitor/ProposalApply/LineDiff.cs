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
        var anchors = FindAnchors(source, output);

        var hunks = new List<ApplyHunk>();
        var sourceIndex = 0;
        var outputIndex = 0;
        foreach (var anchor in anchors.Append((Source: source.Count, Output: output.Count)))
        {
            if (sourceIndex != anchor.Source || outputIndex != anchor.Output)
            {
                var text = string.Concat(output.Skip(outputIndex).Take(anchor.Output - outputIndex));
                var id = Sha256($"{relativePath}\n{sourceIndex}\n{anchor.Source - sourceIndex}\n{text}")[..24];
                hunks.Add(new ApplyHunk(id, relativePath, sourceIndex, anchor.Source - sourceIndex, text, true));
            }
            sourceIndex = anchor.Source + 1;
            outputIndex = anchor.Output + 1;
        }
        return hunks;
    }

    // Unique line anchors keep the auxiliary memory linear while preserving separate edits around stable lines.
    private static IReadOnlyList<(int Source, int Output)> FindAnchors(IReadOnlyList<string> source, IReadOnlyList<string> output)
    {
        var sourcePositions = Positions(source);
        var outputPositions = Positions(output);
        var candidates = sourcePositions
            .Where(pair => pair.Value.Count == 1 && outputPositions.TryGetValue(pair.Key, out var positions) && positions.Count == 1)
            .Select(pair => (Source: pair.Value[0], Output: outputPositions[pair.Key][0]))
            .OrderBy(item => item.Source)
            .ToArray();
        var tails = new List<int>();
        var previous = new int[candidates.Length];
        var tailIndexes = new List<int>();
        Array.Fill(previous, -1);
        for (var index = 0; index < candidates.Length; index++)
        {
            var position = tails.BinarySearch(candidates[index].Output);
            if (position < 0) position = ~position;
            if (position > 0) previous[index] = tailIndexes[position - 1];
            if (position == tails.Count) { tails.Add(candidates[index].Output); tailIndexes.Add(index); }
            else { tails[position] = candidates[index].Output; tailIndexes[position] = index; }
        }
        var result = new List<(int Source, int Output)>();
        for (var current = tailIndexes.Count == 0 ? -1 : tailIndexes[^1]; current >= 0; current = previous[current]) result.Add(candidates[current]);
        result.Reverse();
        return result;
    }

    private static Dictionary<string, List<int>> Positions(IReadOnlyList<string> lines)
    {
        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            if (!positions.TryGetValue(lines[index], out var values)) positions.Add(lines[index], values = []);
            values.Add(index);
        }
        return positions;
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

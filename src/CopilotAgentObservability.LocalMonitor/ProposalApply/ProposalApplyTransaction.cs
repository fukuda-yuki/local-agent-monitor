using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal enum ApplyTransactionResult { Applied, Stale, Failed, RolledBack, RollbackStale, RollbackUnavailable }

internal sealed record ApplyTarget(string RelativePath, string FullPath, string OriginalText, string ReplacementText, string BaseSha256, string ReplacementSha256)
{
    public static ApplyTarget Create(ConfiguredApplyRoot root, string relativePath, string original, string replacement)
    {
        var resolved = ApplyPathPolicy.Resolve(root, relativePath);
        return new ApplyTarget(resolved.RelativePath, resolved.FullPath, original, replacement, LineDiff.Sha256(original), LineDiff.Sha256(replacement));
    }
}

internal sealed class ProposalApplyTransaction(string runtimePath, Action<string>? fault = null)
{
    public ApplyTransactionResult Apply(Guid applyId, IReadOnlyList<ApplyTarget> targets)
    {
        if (targets.Count is < 1 or > 10 || targets.Any(target => Encoding.UTF8.GetByteCount(target.ReplacementText) > 262_144)) return ApplyTransactionResult.Failed;
        if (targets.Any(target => !string.Equals(HashFile(target.FullPath), target.BaseSha256, StringComparison.Ordinal))) return ApplyTransactionResult.Stale;

        var directory = Path.Combine(runtimePath, applyId.ToString("N"));
        Directory.CreateDirectory(directory);
        var journalPath = Path.Combine(directory, "journal.json");
        var journal = new ApplyJournal("prepared", targets.Select((target, index) => new ApplyJournalFile(target.FullPath, target.BaseSha256, target.ReplacementSha256, Path.Combine(directory, $"{index}.snapshot"))).ToArray());
        try
        {
            foreach (var item in journal.Files)
            {
                File.Copy(item.Path, item.SnapshotPath, true);
                Flush(item.SnapshotPath);
            }
            fault?.Invoke("after_snapshots");
            WriteJournal(journalPath, journal);
            fault?.Invoke("after_prepared_journal");
            for (var index = 0; index < targets.Count; index++)
            {
                var staged = Path.Combine(Path.GetDirectoryName(targets[index].FullPath)!, $".{applyId:N}.{index}.tmp");
                File.WriteAllText(staged, targets[index].ReplacementText);
                Flush(staged);
                File.Move(staged, targets[index].FullPath, true);
                WriteJournal(journalPath, journal with { State = $"replaced:{index}" });
                fault?.Invoke("after_replace");
            }
            WriteJournal(journalPath, journal with { State = "committed" });
            return ApplyTransactionResult.Applied;
        }
        catch
        {
            Restore(journal.Files);
            WriteJournal(journalPath, journal with { State = "restored" });
            return ApplyTransactionResult.Failed;
        }
    }

    public void RecoverUncommitted()
    {
        if (!Directory.Exists(runtimePath)) return;
        foreach (var journalPath in Directory.EnumerateFiles(runtimePath, "journal.json", SearchOption.AllDirectories))
        {
            var journal = JsonSerializer.Deserialize<ApplyJournal>(File.ReadAllText(journalPath));
            if (journal is not null && journal.State != "committed" && journal.State != "rolled_back")
            {
                Restore(journal.Files);
                WriteJournal(journalPath, journal with { State = "restored" });
            }
        }
    }

    public ApplyTransactionResult Rollback(Guid applyId)
    {
        var journalPath = Path.Combine(runtimePath, applyId.ToString("N"), "journal.json");
        if (!File.Exists(journalPath)) return ApplyTransactionResult.RollbackUnavailable;
        var journal = JsonSerializer.Deserialize<ApplyJournal>(File.ReadAllText(journalPath));
        if (journal is null || journal.State != "committed") return ApplyTransactionResult.RollbackUnavailable;
        if (journal.Files.Any(item => !string.Equals(HashFile(item.Path), item.ReplacementSha256, StringComparison.Ordinal))) return ApplyTransactionResult.RollbackStale;
        try
        {
            Restore(journal.Files);
            WriteJournal(journalPath, journal with { State = "rolled_back" });
            return ApplyTransactionResult.RolledBack;
        }
        catch { return ApplyTransactionResult.Failed; }
    }

    private static void Restore(IReadOnlyList<ApplyJournalFile> files)
    {
        foreach (var item in files) { File.Copy(item.SnapshotPath, item.Path, true); Flush(item.Path); }
    }

    private static string HashFile(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : string.Empty;
    private static void Flush(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); stream.Flush(true); }
    private static void WriteJournal(string path, ApplyJournal journal) { File.WriteAllText(path, JsonSerializer.Serialize(journal)); Flush(path); }
    private sealed record ApplyJournal(string State, IReadOnlyList<ApplyJournalFile> Files);
    private sealed record ApplyJournalFile(string Path, string OriginalSha256, string ReplacementSha256, string SnapshotPath);
}

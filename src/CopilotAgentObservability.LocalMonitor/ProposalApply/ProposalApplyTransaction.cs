using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal enum ApplyTransactionResult { Applied, Stale, Failed, RolledBack, RollbackStale, RollbackUnavailable }

internal sealed record ApplyTarget(ConfiguredApplyRoot Root, string RelativePath, string OriginalText, string ReplacementText, string BaseSha256, string ReplacementSha256)
{
    public static ApplyTarget Create(ConfiguredApplyRoot root, string relativePath, string original, string replacement)
    {
        var resolved = ApplyPathPolicy.Resolve(root, relativePath);
        return new ApplyTarget(root, resolved.RelativePath, original, replacement, LineDiff.Sha256(original), LineDiff.Sha256(replacement));
    }
}

internal sealed class ProposalApplyTransaction
{
    private readonly string runtimePath;
    private readonly Action<string>? fault;
    private readonly Dictionary<Guid, ConfiguredApplyRoot> roots;

    public ProposalApplyTransaction(string runtimePath, Action<string>? fault = null) : this(runtimePath, [], fault) { }
    public ProposalApplyTransaction(string runtimePath, IReadOnlyList<ConfiguredApplyRoot> configuredRoots, Action<string>? fault = null)
    {
        this.runtimePath = runtimePath;
        this.fault = fault;
        roots = configuredRoots.ToDictionary(root => root.RootId);
    }

    public ApplyTransactionResult Apply(Guid applyId, IReadOnlyList<ApplyTarget> targets)
    {
        if (targets.Count is < 1 or > 10 || targets.Any(target => Encoding.UTF8.GetByteCount(target.ReplacementText) > 262_144)) return ApplyTransactionResult.Failed;
        foreach (var target in targets) roots[target.Root.RootId] = target.Root;
        if (!Validate(targets, usePostHash: false)) return ApplyTransactionResult.Stale;

        var directory = Path.Combine(runtimePath, applyId.ToString("N"));
        Directory.CreateDirectory(directory);
        var journalPath = Path.Combine(directory, "journal.json");
        var journal = new ApplyJournal("snapshotting", targets.Select((target, index) => new ApplyJournalFile(target.Root.RootId, target.RelativePath, target.BaseSha256, target.ReplacementSha256, $"{index}.snapshot")).ToArray());
        try
        {
            foreach (var item in journal.Files)
            {
                var path = Resolve(item);
                var snapshot = Path.Combine(directory, item.SnapshotName);
                File.Copy(path, snapshot, true);
                Flush(snapshot);
            }
            fault?.Invoke("after_snapshots");
            journal = journal with { State = "prepared" };
            WriteJournal(journalPath, journal);
            fault?.Invoke("after_prepared_journal");
            for (var index = 0; index < targets.Count; index++)
            {
                fault?.Invoke($"before_replace:{index}");
                var path = Resolve(journal.Files[index]);
                if (!string.Equals(HashFile(path), journal.Files[index].OriginalSha256, StringComparison.Ordinal)) throw new IOException("stale_target");
                Replace(path, targets[index].ReplacementText, applyId, index);
                journal = journal with { State = $"replaced:{index}" };
                WriteJournal(journalPath, journal);
                fault?.Invoke("after_replace");
            }
            journal = journal with { State = "committed" };
            WriteJournal(journalPath, journal);
            return ApplyTransactionResult.Applied;
        }
        catch
        {
            Restore(directory, journal.Files.Take(ReplacedCount(journal.State)), onlyExistingSnapshots: true);
            WriteJournal(journalPath, journal with { State = "restored" });
            return ApplyTransactionResult.Failed;
        }
    }

    public void RecoverUncommitted()
    {
        if (!Directory.Exists(runtimePath)) return;
        foreach (var journalPath in Directory.EnumerateFiles(runtimePath, "journal.json", SearchOption.AllDirectories))
        {
            var journal = ReadJournal(journalPath);
            if (journal is null || journal.State is "committed" or "rolled_back") continue;
            var directory = Path.GetDirectoryName(journalPath)!;
            var filesToRestore = journal.State.StartsWith("rollback_", StringComparison.Ordinal)
                ? journal.Files
                : journal.Files.Take(ReplacedCount(journal.State));
            Restore(directory, filesToRestore, onlyExistingSnapshots: true);
            WriteJournal(journalPath, journal with { State = "restored" });
        }
    }

    public ApplyTransactionResult Rollback(Guid applyId)
    {
        var journalPath = Path.Combine(runtimePath, applyId.ToString("N"), "journal.json");
        var journal = File.Exists(journalPath) ? ReadJournal(journalPath) : null;
        if (journal is null || journal.State != "committed") return ApplyTransactionResult.RollbackUnavailable;
        if (!Validate(journal.Files, usePostHash: true)) return ApplyTransactionResult.RollbackStale;
        var directory = Path.GetDirectoryName(journalPath)!;
        try
        {
            journal = journal with { State = "rollback_prepared" };
            WriteJournal(journalPath, journal);
            fault?.Invoke("after_rollback_prepared");
            for (var index = 0; index < journal.Files.Count; index++)
            {
                var item = journal.Files[index];
                var path = Resolve(item);
                if (!string.Equals(HashFile(path), item.ReplacementSha256, StringComparison.Ordinal)) throw new IOException("rollback_stale");
                ReplaceFromSnapshot(path, Path.Combine(directory, item.SnapshotName), applyId, index);
                journal = journal with { State = $"rollback_replaced:{index}" };
                WriteJournal(journalPath, journal);
                fault?.Invoke("after_rollback_replace");
            }
            WriteJournal(journalPath, journal with { State = "rolled_back" });
            return ApplyTransactionResult.RolledBack;
        }
        catch
        {
            Restore(directory, journal.Files, onlyExistingSnapshots: true);
            WriteJournal(journalPath, journal with { State = "rolled_back" });
            return ApplyTransactionResult.Failed;
        }
    }

    private bool Validate(IEnumerable<ApplyTarget> targets, bool usePostHash) => targets.All(target =>
    {
        try { var path = ApplyPathPolicy.Resolve(target.Root, target.RelativePath).FullPath; return string.Equals(HashFile(path), usePostHash ? target.ReplacementSha256 : target.BaseSha256, StringComparison.Ordinal); }
        catch (ApplyPathException) { return false; }
    });

    private bool Validate(IEnumerable<ApplyJournalFile> files, bool usePostHash) => files.All(file =>
    {
        try { return string.Equals(HashFile(Resolve(file)), usePostHash ? file.ReplacementSha256 : file.OriginalSha256, StringComparison.Ordinal); }
        catch (ApplyPathException) { return false; }
    });

    private string Resolve(ApplyJournalFile item)
    {
        if (!roots.TryGetValue(item.RootId, out var root)) throw new ApplyPathException("invalid_root_id");
        return ApplyPathPolicy.Resolve(root, item.RelativePath).FullPath;
    }

    private void Restore(string directory, IEnumerable<ApplyJournalFile> files, bool onlyExistingSnapshots)
    {
        foreach (var item in files)
        {
            var snapshot = Path.Combine(directory, item.SnapshotName);
            if (onlyExistingSnapshots && !File.Exists(snapshot)) continue;
            ReplaceFromSnapshot(Resolve(item), snapshot, Guid.Empty, 0);
        }
    }

    private static int ReplacedCount(string state) => state.StartsWith("replaced:", StringComparison.Ordinal)
        && int.TryParse(state["replaced:".Length..], out var last) ? last + 1 : 0;

    private static void Replace(string path, string content, Guid applyId, int index)
    {
        var staged = Path.Combine(Path.GetDirectoryName(path)!, $".{applyId:N}.{index}.tmp");
        File.WriteAllText(staged, content);
        Flush(staged);
        File.Move(staged, path, true);
        Flush(path);
    }

    private static void ReplaceFromSnapshot(string path, string snapshot, Guid applyId, int index)
    {
        var staged = Path.Combine(Path.GetDirectoryName(path)!, $".{applyId:N}.restore.{index}.tmp");
        File.Copy(snapshot, staged, true);
        Flush(staged);
        File.Move(staged, path, true);
        Flush(path);
    }

    private static string HashFile(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : string.Empty;
    private static void Flush(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); stream.Flush(true); }
    private static void WriteJournal(string path, ApplyJournal journal) { File.WriteAllText(path, JsonSerializer.Serialize(journal)); Flush(path); }
    private static ApplyJournal? ReadJournal(string path) => JsonSerializer.Deserialize<ApplyJournal>(File.ReadAllText(path));
    private sealed record ApplyJournal(string State, IReadOnlyList<ApplyJournalFile> Files);
    private sealed record ApplyJournalFile(Guid RootId, string RelativePath, string OriginalSha256, string ReplacementSha256, string SnapshotName);
}

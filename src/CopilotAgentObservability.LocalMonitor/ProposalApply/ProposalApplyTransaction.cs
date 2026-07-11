using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal enum ApplyTransactionResult { Applied, Stale, Failed, RolledBack, RollbackStale, RollbackUnavailable }
internal sealed class ApplyTransactionCrashException : Exception { }
internal sealed class ApplyRecoveryException : Exception { }

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
    private bool recoveryBlocked;

    public ProposalApplyTransaction(string runtimePath, Action<string>? fault = null) : this(runtimePath, [], fault) { }
    public ProposalApplyTransaction(string runtimePath, IReadOnlyList<ConfiguredApplyRoot> configuredRoots, Action<string>? fault = null)
    {
        this.runtimePath = runtimePath;
        this.fault = fault;
        roots = LoadRoots(configuredRoots).ToDictionary(root => root.RootId);
    }

    public IReadOnlyList<ConfiguredApplyRoot> ConfiguredRoots => roots.Values.OrderBy(root => root.Kind).ToArray();

    public string? GetJournalState(Guid applyId)
    {
        var path = Path.Combine(runtimePath, applyId.ToString("N"), "journal.json");
        return File.Exists(path) ? ReadJournal(path)?.State : null;
    }

    public ApplyTransactionResult Apply(Guid applyId, IReadOnlyList<ApplyTarget> targets)
    {
        if (recoveryBlocked || targets.Count is < 1 or > 10 || targets.Any(target => Encoding.UTF8.GetByteCount(target.ReplacementText) > 262_144)) return ApplyTransactionResult.Failed;
        targets = targets.Select(target =>
        {
            var configured = roots.Values.SingleOrDefault(root => root.Kind == target.Root.Kind && string.Equals(root.CanonicalPath, target.Root.CanonicalPath, StringComparison.OrdinalIgnoreCase));
            if (configured is not null) return target with { Root = configured };
            roots[target.Root.RootId] = target.Root;
            return target;
        }).ToArray();
        PersistRoots(targets.Select(target => target.Root));
        if (!Validate(targets, usePostHash: false)) return ApplyTransactionResult.Stale;

        var directory = Path.Combine(runtimePath, applyId.ToString("N"));
        Directory.CreateDirectory(directory);
        var journalPath = Path.Combine(directory, "journal.json");
        var journal = new ApplyJournal("snapshotting", targets.Select((target, index) => new ApplyJournalFile(target.Root.RootId, target.RelativePath, target.BaseSha256, target.ReplacementSha256, $"{index}.snapshot")).ToArray());
        var replacementStarted = false;
        try
        {
            for (var index = 0; index < journal.Files.Count; index++)
            {
                var item = journal.Files[index];
                var path = Resolve(item);
                var snapshot = Path.Combine(directory, item.SnapshotName);
                File.Copy(path, snapshot, true);
                Flush(snapshot);
                fault?.Invoke($"after_snapshot:{index}");
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
                Replace(path, targets[index].ReplacementText, applyId, index, () => replacementStarted = true);
                fault?.Invoke($"after_atomic_replace:{index}");
                journal = journal with { State = $"replaced:{index}" };
                WriteJournal(journalPath, journal);
                fault?.Invoke("after_replace");
            }
            journal = journal with { State = "committed" };
            WriteJournal(journalPath, journal);
            fault?.Invoke("after_committed_journal");
            return ApplyTransactionResult.Applied;
        }
        catch (ApplyTransactionCrashException) { throw; }
        catch
        {
            Restore(directory, replacementStarted ? journal.Files : [], onlyExistingSnapshots: true);
            WriteJournal(journalPath, journal with { State = "restored" });
            return ApplyTransactionResult.Failed;
        }
    }

    public void RecoverUncommitted()
    {
        if (!Directory.Exists(runtimePath)) return;
        foreach (var journalPath in Directory.EnumerateFiles(runtimePath, "journal.json", SearchOption.AllDirectories))
        {
            try
            {
                var journal = ReadJournal(journalPath);
                if (journal is null || journal.State is "committed" or "rolled_back") continue;
                foreach (var file in journal.Files) _ = Resolve(file);
                var directory = Path.GetDirectoryName(journalPath)!;
                Restore(directory, journal.Files, onlyExistingSnapshots: true);
                WriteJournal(journalPath, journal with { State = "restored" });
            }
            catch
            {
                recoveryBlocked = true;
                throw new ApplyRecoveryException();
            }
        }
    }

    public ApplyTransactionResult Rollback(Guid applyId)
    {
        if (recoveryBlocked) return ApplyTransactionResult.Failed;
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
                fault?.Invoke($"after_atomic_rollback_replace:{index}");
                journal = journal with { State = $"rollback_replaced:{index}" };
                WriteJournal(journalPath, journal);
                fault?.Invoke("after_rollback_replace");
            }
            WriteJournal(journalPath, journal with { State = "rolled_back" });
            fault?.Invoke("after_rolled_back_journal");
            return ApplyTransactionResult.RolledBack;
        }
        catch (ApplyTransactionCrashException) { throw; }
        catch
        {
            Restore(directory, journal.Files, onlyExistingSnapshots: true);
            WriteJournal(journalPath, journal with { State = "rolled_back" });
            return ApplyTransactionResult.RolledBack;
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

    private IReadOnlyList<ConfiguredApplyRoot> LoadRoots(IReadOnlyList<ConfiguredApplyRoot> configuredRoots)
    {
        Directory.CreateDirectory(runtimePath);
        var mapPath = Path.Combine(runtimePath, "apply-root-map.json");
        var saved = File.Exists(mapPath) ? JsonSerializer.Deserialize<List<RootMapEntry>>(File.ReadAllText(mapPath)) ?? [] : [];
        var resolved = new List<ConfiguredApplyRoot>();
        foreach (var root in configuredRoots)
        {
            ApplyPathPolicy.EnsureSafeExistingPath(root.CanonicalPath, "invalid_apply_root");
            var existing = saved.SingleOrDefault(entry => entry.Kind == root.Kind && string.Equals(entry.CanonicalPath, root.CanonicalPath, StringComparison.OrdinalIgnoreCase));
            var rootId = existing?.RootId ?? Guid.CreateVersion7();
            resolved.Add(root with { RootId = rootId });
            if (existing is null) saved.Add(new RootMapEntry(rootId, root.Kind, root.CanonicalPath));
        }
        WriteRootMap(mapPath, saved);
        return resolved;
    }

    private void PersistRoots(IEnumerable<ConfiguredApplyRoot> configuredRoots)
    {
        Directory.CreateDirectory(runtimePath);
        var mapPath = Path.Combine(runtimePath, "apply-root-map.json");
        var saved = File.Exists(mapPath) ? JsonSerializer.Deserialize<List<RootMapEntry>>(File.ReadAllText(mapPath)) ?? [] : [];
        foreach (var root in configuredRoots)
        {
            if (saved.Any(entry => entry.RootId == root.RootId)) continue;
            saved.Add(new RootMapEntry(root.RootId, root.Kind, root.CanonicalPath));
        }
        WriteRootMap(mapPath, saved);
    }

    private void Replace(string path, string content, Guid applyId, int index, Action replacementCompleted)
    {
        var staged = Path.Combine(Path.GetDirectoryName(path)!, $".{applyId:N}.{index}.tmp");
        File.WriteAllText(staged, content);
        Flush(staged);
        fault?.Invoke($"after_staged_replacement:{index}");
        File.Move(staged, path, true);
        replacementCompleted();
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
    private static void WriteRootMap(string path, IReadOnlyList<RootMapEntry> map) { File.WriteAllText(path, JsonSerializer.Serialize(map)); Flush(path); }
    private static ApplyJournal? ReadJournal(string path) => JsonSerializer.Deserialize<ApplyJournal>(File.ReadAllText(path));
    private sealed record ApplyJournal(string State, IReadOnlyList<ApplyJournalFile> Files);
    private sealed record ApplyJournalFile(Guid RootId, string RelativePath, string OriginalSha256, string ReplacementSha256, string SnapshotName);
    private sealed record RootMapEntry(Guid RootId, ApplyRootKind Kind, string CanonicalPath);
}

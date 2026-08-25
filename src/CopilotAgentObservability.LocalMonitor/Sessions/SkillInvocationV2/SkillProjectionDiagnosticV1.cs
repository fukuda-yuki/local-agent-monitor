namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

public enum SkillProjectionDiagnosticV1Outcome
{
    Current,
    Stale,
    Invalid,
    Unavailable
}

public sealed record SkillProjectionDiagnosticV1RegistryRevision(
    int Revision,
    IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> Entries);

public static class SkillProjectionDiagnosticV1
{
    public static SkillProjectionDiagnosticV1Outcome Diagnose(
        bool isSnapshotAvailable,
        SkillInvocationV2CompatibilityTuple tuple,
        IReadOnlyList<SkillProjectionDiagnosticV1RegistryRevision> history)
    {
        if (!isSnapshotAvailable)
        {
            return SkillProjectionDiagnosticV1Outcome.Invalid;
        }

        ArgumentNullException.ThrowIfNull(tuple);
        ArgumentNullException.ThrowIfNull(history);

        if (!TryValidateHistory(history, out var validatedHistory))
        {
            return SkillProjectionDiagnosticV1Outcome.Unavailable;
        }

        var greatestEntry = FindEntry(validatedHistory[^1].Entries, tuple);

        // Absence from the greatest revision is not revocation: there is no history to
        // prove for a tuple that was never written, so the outcome is `invalid`, never
        // `stale`/`unavailable`. A dropped-then-never-revoked tuple falls here too.
        if (greatestEntry is null)
        {
            return SkillProjectionDiagnosticV1Outcome.Invalid;
        }

        if (greatestEntry.Disposition == SkillInvocationV2CompatibilityDisposition.Accepted)
        {
            return SkillProjectionDiagnosticV1Outcome.Current;
        }

        // Gate 3: consulting revisions below the greatest solely to prove this exact
        // tuple's earlier `accepted` predecessor is not admission fallback. New
        // writes/admissions still consult only the greatest current complete file; this
        // diagnostic is the one reader allowed to look lower, and only for this proof.
        for (var index = 0; index < validatedHistory.Count - 1; index++)
        {
            var priorEntry = FindEntry(validatedHistory[index].Entries, tuple);
            if (priorEntry is not null && priorEntry.Disposition == SkillInvocationV2CompatibilityDisposition.Accepted)
            {
                return SkillProjectionDiagnosticV1Outcome.Stale;
            }
        }

        return SkillProjectionDiagnosticV1Outcome.Unavailable;
    }

    public static SkillProjectionDiagnosticV1Outcome Diagnose(bool isSnapshotAvailable, SkillInvocationV2CompatibilityTuple tuple)
    {
        if (!isSnapshotAvailable)
        {
            return SkillProjectionDiagnosticV1Outcome.Invalid;
        }

        var registry = SkillInvocationV2ArtifactRegistry.Load();
        var history = registry.History.Select(revision =>
            new SkillProjectionDiagnosticV1RegistryRevision(revision.Revision, revision.Entries)).ToArray();

        return Diagnose(isSnapshotAvailable, tuple, history);
    }

    private static bool TryValidateHistory(
        IReadOnlyList<SkillProjectionDiagnosticV1RegistryRevision> history,
        out IReadOnlyList<SkillProjectionDiagnosticV1RegistryRevision> validatedHistory)
    {
        validatedHistory = Array.Empty<SkillProjectionDiagnosticV1RegistryRevision>();

        if (history.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < history.Count; index++)
        {
            var revision = history[index];
            if (revision is null || revision.Entries is null || revision.Revision != index + 1)
            {
                return false;
            }
        }

        validatedHistory = history;
        return true;
    }

    private static SkillInvocationV2CompatibilityRegistryEntry? FindEntry(
        IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> entries,
        SkillInvocationV2CompatibilityTuple tuple) =>
        entries.FirstOrDefault(entry => entry.Tuple == tuple);
}

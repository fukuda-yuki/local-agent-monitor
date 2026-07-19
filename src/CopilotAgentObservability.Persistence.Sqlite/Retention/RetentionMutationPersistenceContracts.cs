namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed record RetentionMutationVersionVector(
    IReadOnlyList<RetentionMutationExpectedStateItem> ExpectedItems,
    IReadOnlyList<RetentionMutationDigestItem> TargetItems,
    string ExpectedStateVersion,
    string TargetItemSetDigest);

internal enum RetentionMutationCasDisposition { Committed, Stale }

internal sealed record RetentionMutationCasResult(
    RetentionMutationCasDisposition Disposition,
    string? ResultVersion);

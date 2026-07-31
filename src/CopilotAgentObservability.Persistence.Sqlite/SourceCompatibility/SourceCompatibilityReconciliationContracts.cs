namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum SourceCompatibilityReconciliationTrigger
{
    DecoderRevision,
    RegistryRevision,
}

internal enum SourceCompatibilityReconciliationOutcome
{
    Changed,
    NoChange,
    InputUnavailable,
}

internal sealed record SourceCompatibilityReconciliationRequest(
    string OperationKey,
    long SourceObservationId,
    string TraceId,
    long ExpectedInterpretationRevision,
    SourceCompatibilityReconciliationTrigger Trigger,
    string ResolverRevision,
    string RegistryRevision,
    string ProjectorVersion)
{
    internal static SourceCompatibilityReconciliationRequest Create(
        string operationKey,
        long sourceObservationId,
        string traceId,
        long expectedInterpretationRevision,
        SourceCompatibilityReconciliationTrigger trigger,
        string resolverRevision,
        string registryRevision,
        string projectorVersion)
    {
        var request = new SourceCompatibilityReconciliationRequest(
            operationKey,
            sourceObservationId,
            traceId,
            expectedInterpretationRevision,
            trigger,
            resolverRevision,
            registryRevision,
            projectorVersion);
        request.Validate();
        return request;
    }

    internal void Validate()
    {
        if (!IsToken(OperationKey) || SourceObservationId <= 0 || !IsTraceId(TraceId)
            || ExpectedInterpretationRevision < 0 || !Enum.IsDefined(Trigger)
            || !IsToken(ResolverRevision) || !IsToken(RegistryRevision)
            || !IsToken(ProjectorVersion))
        {
            throw new ArgumentException("The reconciliation request is invalid.");
        }
    }

    internal static void ValidateInterpretation(
        TraceSourceVersionResolutionState state,
        string? exactVersion)
    {
        if ((state == TraceSourceVersionResolutionState.Resolved && !IsRevisionToken(exactVersion))
            || (state is TraceSourceVersionResolutionState.Missing or TraceSourceVersionResolutionState.Conflicting
                && exactVersion is not null)
            || (state == TraceSourceVersionResolutionState.Unrecognised
                && exactVersion is not null
                && !IsRevisionToken(exactVersion)))
        {
            throw new ArgumentException("The interpretation state and exact version are inconsistent.");
        }
    }

    private static bool IsTraceId(string? value) =>
        value is { Length: 32 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsToken(string? value) => IsRevisionToken(value);

    internal static bool IsRevisionToken(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(static character =>
            character is >= '!' and <= '~'
            && character is not '/' and not '\\');
}

internal sealed record SourceCompatibilityAcceptedRevision(
    string ResolverRevision,
    string RegistryRevision,
    VerifiedSourceFingerprintRegistry Registry);

internal sealed class SourceCompatibilityReconciliationAuthority
{
    private readonly IReadOnlyDictionary<(string Resolver, string Registry), VerifiedSourceFingerprintRegistry>
        revisions;

    private SourceCompatibilityReconciliationAuthority(
        IReadOnlyDictionary<(string Resolver, string Registry), VerifiedSourceFingerprintRegistry> revisions)
    {
        this.revisions = revisions;
    }

    internal static SourceCompatibilityReconciliationAuthority Empty { get; } =
        new(new Dictionary<(string, string), VerifiedSourceFingerprintRegistry>());

    internal static SourceCompatibilityReconciliationAuthority Create(
        IEnumerable<SourceCompatibilityAcceptedRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        var accepted = new Dictionary<(string, string), VerifiedSourceFingerprintRegistry>();
        foreach (var revision in revisions)
        {
            ArgumentNullException.ThrowIfNull(revision);
            if (!SourceCompatibilityReconciliationRequest.IsRevisionToken(revision.ResolverRevision)
                || !SourceCompatibilityReconciliationRequest.IsRevisionToken(revision.RegistryRevision))
            {
                throw new ArgumentException("An accepted reconciliation revision is invalid.");
            }
            ArgumentNullException.ThrowIfNull(revision.Registry);
            if (!accepted.TryAdd(
                    (revision.ResolverRevision, revision.RegistryRevision),
                    revision.Registry))
            {
                throw new ArgumentException("An accepted reconciliation revision is duplicated.");
            }
        }
        return new(accepted);
    }

    internal bool TryGetRegistry(
        string resolverRevision,
        string registryRevision,
        out VerifiedSourceFingerprintRegistry registry) =>
        revisions.TryGetValue((resolverRevision, registryRevision), out registry!);
}

internal sealed record SourceCompatibilityReconciliationResult(
    SourceCompatibilityReconciliationOutcome Outcome,
    long? SupersessionId,
    long InterpretationRevision,
    long? CompatibilityRevision,
    long? GenerationId);

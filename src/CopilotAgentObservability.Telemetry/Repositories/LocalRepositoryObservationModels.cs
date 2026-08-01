namespace CopilotAgentObservability.Telemetry.Repositories;

internal enum LocalRepositoryObservationScopeKind
{
    Resource,
    Span,
}

internal enum LocalRepositoryOccurrenceClassification
{
    Admitted,
    InvalidLocator,
    InvalidType,
    DuplicateKey,
}

internal enum LocalRepositoryAdmissionState
{
    Admitted,
    Shadowed,
    InvalidLocator,
    InvalidType,
    DuplicateKey,
}

internal sealed record LocalRepositorySourceIdentityInput
{
    private LocalRepositorySourceIdentityInput(
        long rawRecordId,
        int resourceSpanOrdinal,
        int? scopeSpanOrdinal,
        int? spanOrdinal,
        LocalRepositoryObservationScopeKind scopeKind,
        int attributeOrdinal,
        string attributeKey)
    {
        RawRecordId = rawRecordId;
        ResourceSpanOrdinal = resourceSpanOrdinal;
        ScopeSpanOrdinal = scopeSpanOrdinal;
        SpanOrdinal = spanOrdinal;
        ScopeKind = scopeKind;
        AttributeOrdinal = attributeOrdinal;
        AttributeKey = attributeKey;
    }

    public long RawRecordId { get; }
    public int ResourceSpanOrdinal { get; }
    public int? ScopeSpanOrdinal { get; }
    public int? SpanOrdinal { get; }
    public LocalRepositoryObservationScopeKind ScopeKind { get; }
    public int AttributeOrdinal { get; }
    public string AttributeKey { get; }

    public static LocalRepositorySourceIdentityInput Resource(
        long rawRecordId,
        int resourceSpanOrdinal,
        int attributeOrdinal,
        string attributeKey) =>
        new(rawRecordId, resourceSpanOrdinal, null, null, LocalRepositoryObservationScopeKind.Resource, attributeOrdinal, attributeKey);

    public static LocalRepositorySourceIdentityInput Span(
        long rawRecordId,
        int resourceSpanOrdinal,
        int scopeSpanOrdinal,
        int spanOrdinal,
        int attributeOrdinal,
        string attributeKey) =>
        new(rawRecordId, resourceSpanOrdinal, scopeSpanOrdinal, spanOrdinal, LocalRepositoryObservationScopeKind.Span, attributeOrdinal, attributeKey);
}

internal sealed record LocalRepositoryContextIdentityInput(
    string SourceIdentitySha256,
    string SessionId,
    string SessionEventId,
    string TraceId,
    string SpanId);

internal sealed record LocalRepositoryOperationFingerprintInput(
    string? Method,
    string? RouteTemplate,
    string? Operation,
    string? TargetId,
    string? ExpectedRevision,
    string? DisplayName,
    string? CanonicalLocator,
    string? SessionAction,
    string? RepositoryId);

internal sealed record LocalRepositoryAssignmentState
{
    public LocalRepositoryAssignmentState(
        string state,
        string authority,
        string? repositoryId,
        IReadOnlyList<string> candidateRepositoryIds)
    {
        State = state;
        Authority = authority;
        RepositoryId = repositoryId;
        CandidateRepositoryIds = candidateRepositoryIds;
    }

    public string State { get; init; }
    public string Authority { get; init; }
    public string? RepositoryId { get; init; }
    public IReadOnlyList<string> CandidateRepositoryIds { get; init; }
}

internal enum LocalRepositoryReconciliationEvidenceKind
{
    PayloadSha256,
    InputUnavailable,
}

internal sealed record LocalRepositoryReconciliationEvidence
{
    private LocalRepositoryReconciliationEvidence(
        long rawRecordId,
        LocalRepositoryReconciliationEvidenceKind kind,
        string? rawPayloadSha256)
    {
        RawRecordId = rawRecordId;
        Kind = kind;
        RawPayloadSha256 = rawPayloadSha256;
    }

    public long RawRecordId { get; }
    public LocalRepositoryReconciliationEvidenceKind Kind { get; }
    public string? RawPayloadSha256 { get; }

    public static LocalRepositoryReconciliationEvidence PayloadSha256(long rawRecordId, string rawPayloadSha256) =>
        new(rawRecordId, LocalRepositoryReconciliationEvidenceKind.PayloadSha256, rawPayloadSha256);

    public static LocalRepositoryReconciliationEvidence InputUnavailable(long rawRecordId) =>
        new(rawRecordId, LocalRepositoryReconciliationEvidenceKind.InputUnavailable, null);
}

internal sealed record LocalRepositoryPhysicalOccurrence(
    LocalRepositorySourceIdentityInput SourceIdentityInput,
    string SourceIdentitySha256,
    string RawPayloadSha256,
    string SourceSurface,
    string? SourceApplicationVersion,
    DateTimeOffset ObservedAt,
    LocalRepositoryOccurrenceClassification Classification,
    GitHubRepositoryLocator? Locator)
{
    public long RawRecordId => SourceIdentityInput.RawRecordId;
    public int ResourceSpanOrdinal => SourceIdentityInput.ResourceSpanOrdinal;
    public int? ScopeSpanOrdinal => SourceIdentityInput.ScopeSpanOrdinal;
    public int? SpanOrdinal => SourceIdentityInput.SpanOrdinal;
    public LocalRepositoryObservationScopeKind ScopeKind => SourceIdentityInput.ScopeKind;
    public int AttributeOrdinal => SourceIdentityInput.AttributeOrdinal;
    public string AttributeKey => SourceIdentityInput.AttributeKey;
}

internal sealed record LocalRepositoryObservationContextLink
{
    public LocalRepositoryObservationContextLink(
        LocalRepositoryPhysicalOccurrence occurrence,
        string? traceId,
        string? spanId,
        int contextScopeSpanOrdinal,
        int contextSpanOrdinal,
        LocalRepositoryAdmissionState admissionState)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (contextScopeSpanOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextScopeSpanOrdinal));
        }
        if (contextSpanOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextSpanOrdinal));
        }

        Occurrence = occurrence;
        TraceId = traceId;
        SpanId = spanId;
        ContextScopeSpanOrdinal = contextScopeSpanOrdinal;
        ContextSpanOrdinal = contextSpanOrdinal;
        AdmissionState = admissionState;
    }

    public LocalRepositoryPhysicalOccurrence Occurrence { get; }
    public string? TraceId { get; }
    public string? SpanId { get; }
    public int ContextScopeSpanOrdinal { get; }
    public int ContextSpanOrdinal { get; }
    public LocalRepositoryAdmissionState AdmissionState { get; }
}

internal sealed record LocalRepositoryObservationParseResult(
    IReadOnlyList<LocalRepositoryPhysicalOccurrence> Occurrences,
    IReadOnlyList<LocalRepositoryObservationContextLink> ContextLinks);

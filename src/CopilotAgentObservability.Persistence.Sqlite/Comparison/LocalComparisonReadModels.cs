namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalComparisonSectionReadModel(
    int Ordinal,
    string Token,
    IReadOnlyList<LocalComparisonStoredResult> Rows);

internal sealed record LocalComparisonNamedUnionReadModel(
    string Family,
    int TotalCount,
    IReadOnlyList<LocalComparisonStoredResult> Rows);

internal sealed record LocalComparisonEvidenceReadModel(
    int ResultOrdinal,
    string FieldKey,
    IReadOnlyList<LocalComparisonStoredEvidence> Items);

internal sealed record LocalComparisonMemberReadModel(
    string Cohort,
    int Ordinal,
    string SessionId,
    string WorkspaceRevision,
    bool IsArchived,
    LocalComparisonFactState ValueAvailabilityState,
    LocalComparisonFactState ObservedAtState,
    DateTimeOffset? ObservedAt);

internal sealed record LocalComparisonFrozenReadModel(
    string ComparisonId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<LocalComparisonMemberReadModel> Members,
    IReadOnlyList<LocalComparisonSectionReadModel> Sections,
    IReadOnlyList<LocalComparisonNamedUnionReadModel> NamedUnions,
    IReadOnlyList<LocalComparisonEvidenceReadModel> Evidence)
{
    internal static LocalComparisonFrozenReadModel Create(
        LocalComparisonFrozenSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LocalComparisonSnapshotValidation.Validate(snapshot);
        var rows = snapshot.Results
            .Where(static item => item.ResultOrdinal != 0)
            .ToArray();
        var members = snapshot.Memberships.Select(item =>
        {
            var fact = LocalComparisonFactFrame.Decode(item.FactFrame);
            return new LocalComparisonMemberReadModel(
                item.Cohort,
                item.Ordinal,
                item.SessionId,
                item.WorkspaceRevision,
                fact.IsArchived,
                fact.Target.ValueAvailabilityState,
                fact.Target.ObservedAtState,
                fact.Target.ObservedAt);
        }).ToArray();
        var sections = LocalComparisonRegistryV1.Sections.Select(section =>
            new LocalComparisonSectionReadModel(
                section.Ordinal,
                section.Token,
                Array.AsReadOnly(rows
                    .Where(item => item.SectionOrdinal == section.Ordinal)
                    .OrderBy(static item => item.ResultOrdinal)
                    .ToArray()))).ToArray();
        var named = LocalComparisonRegistryV1.NamedFamilies.Select(family =>
        {
            var union = rows
                .Where(item => item.RowKind == family.Key)
                .OrderBy(static item => item.ResultOrdinal)
                .ToArray();
            return new LocalComparisonNamedUnionReadModel(
                family.Key,
                union.Length,
                Array.AsReadOnly(union));
        }).ToArray();
        var evidence = snapshot.Evidence
            .GroupBy(static item => (item.ResultOrdinal, item.FieldKey))
            .OrderBy(static group => group.Key.ResultOrdinal)
            .ThenBy(static group => group.Key.FieldKey, StringComparer.Ordinal)
            .Select(static group => new LocalComparisonEvidenceReadModel(
                group.Key.ResultOrdinal,
                group.Key.FieldKey,
                Array.AsReadOnly(group.OrderBy(static item => item.EvidenceOrdinal).ToArray())))
            .ToArray();
        return new(
            snapshot.ComparisonId,
            snapshot.RepositoryId,
            snapshot.CreatedAt,
            snapshot.ExpiresAt,
            Array.AsReadOnly(members),
            Array.AsReadOnly(sections),
            Array.AsReadOnly(named),
            Array.AsReadOnly(evidence));
    }
}

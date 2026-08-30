using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed class LocalAiScopeSnapshotException(string error) : Exception(error)
{
    internal string Error { get; } = error;
}

internal interface ILocalAiComparisonSnapshotAdapterV1
{
    LocalAiSnapshotProjectionV1 Read(string repositoryId, string comparisonId, CancellationToken token);
}

internal sealed class LocalAiComparisonSnapshotAdapterV1(SqliteLocalComparisonStore store) : ILocalAiComparisonSnapshotAdapterV1
{
    public LocalAiSnapshotProjectionV1 Read(string repositoryId, string comparisonId, CancellationToken token)
    {
        LocalComparisonReadResult read;
        try { read = store.Read(repositoryId, comparisonId, token); }
        catch (InvalidOperationException) { throw new LocalAiScopeSnapshotException("comparison_not_found"); }
        if (read.Status == LocalComparisonReadStatus.NotFound) throw new LocalAiScopeSnapshotException("comparison_not_found");
        if (read.Status == LocalComparisonReadStatus.Expired) throw new LocalAiScopeSnapshotException("comparison_expired");
        if (read.Status == LocalComparisonReadStatus.PersistenceBusy) throw new LocalAiScopeSnapshotException("persistence_busy");
        var snapshot = read.Snapshot ?? throw new LocalAiScopeSnapshotException("comparison_not_found");
        var receipt = snapshot.Results.Single(static item => item.ResultOrdinal == 0);
        var evidenceRefs = snapshot.Evidence.Select(item => LocalMonitorV1CanonicalUrlBuilder.BuildSessionEvidence(
                item.SessionId,
                item.SourceKind == "session_run" ? item.SourceIdentity : item.SourceKind == "workspace_node" ? item.EventId : null,
                item.SourceKind == "workspace_node" ? item.SourceIdentity : null))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var payload = LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new
        {
            schema_version = "local_ai_comparison_snapshot:1",
            repository_id = snapshot.RepositoryId,
            comparison_id = snapshot.ComparisonId,
            receipt_sha256 = receipt.PayloadSha256,
            selection_sha256 = snapshot.SelectionSha256,
            scope_condition_sha256 = Convert.ToHexStringLower(snapshot.ScopeConditionSha256),
            created_at = snapshot.CreatedAt.ToUniversalTime().ToString("O"),
            expires_at = snapshot.ExpiresAt.ToUniversalTime().ToString("O"),
            memberships = snapshot.Memberships.OrderBy(static item => item.Cohort, StringComparer.Ordinal).ThenBy(static item => item.Ordinal)
                .Select(static item => new { cohort=item.Cohort, ordinal=item.Ordinal, session_id=item.SessionId,
                    workspace_revision=item.WorkspaceRevision, fact_sha256=item.FactSha256 }).ToArray(),
            results = snapshot.Results.Where(static item => item.ResultOrdinal != 0).OrderBy(static item => item.ResultOrdinal)
                .Select(static item => new { result_ordinal=item.ResultOrdinal, section_ordinal=item.SectionOrdinal,
                    row_kind=item.RowKind, row_key=item.RowKey, values=item.Values }).ToArray(),
            evidence = snapshot.Evidence.OrderBy(static item => item.ResultOrdinal).ThenBy(static item => item.EvidenceOrdinal)
                .Select(item => new { result_ordinal=item.ResultOrdinal, evidence_ordinal=item.EvidenceOrdinal,
                    field_key=item.FieldKey, cohort=item.Cohort, session_id=item.SessionId, state=item.AvailabilityState,
                    consumed_value=item.ConsumedValue, consumed_revision=item.RevisionSha256,
                    session_location=LocalMonitorV1CanonicalUrlBuilder.BuildSessionEvidence(item.SessionId,
                        item.SourceKind == "session_run" ? item.SourceIdentity : item.SourceKind == "workspace_node" ? item.EventId : null,
                        item.SourceKind == "workspace_node" ? item.SourceIdentity : null) }).ToArray(),
        }));
        if (payload.Length > LocalAiAnalysisStoreV1.MaximumSnapshotDocumentBytes) throw new LocalAiScopeTooLargeException();
        var index = LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new { evidence_refs=evidenceRefs }));
        return new(Guid.CreateVersion7().ToString(), "comparison", null, null, comparisonId, receipt.PayloadSha256,
            payload, index, Convert.ToHexStringLower(SHA256.HashData(payload)), evidenceRefs.ToHashSet(StringComparer.Ordinal),
            RepositoryId:repositoryId, ComparisonId:comparisonId, ExpiresAt:snapshot.ExpiresAt);
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor;

internal sealed class LocalMonitorV1ComparisonProductionApplication : ILocalMonitorV1ComparisonApplication
{
    private readonly ILocalRepositoryComparisonInputSnapshotService inputs;
    private readonly SqliteLocalComparisonStore store;
    private readonly LocalComparisonApplicationService core;
    private readonly LocalMonitorV1ComparisonCursorCodec cursors;

    internal LocalMonitorV1ComparisonProductionApplication(ILocalRepositoryComparisonInputSnapshotService inputs, SqliteLocalComparisonStore store, TimeProvider? clock = null, Func<DateTimeOffset, string>? ids = null, byte[]? cursorKey = null)
    { this.inputs = inputs; this.store = store; core = new(store, clock, ids); cursors = new(cursorKey ?? RandomNumberGenerator.GetBytes(32)); }

    public async ValueTask<LocalMonitorV1ComparisonResponse> ExecuteAsync(LocalMonitorV1ComparisonOperation op, string repositoryId, string? comparisonId, ReadOnlyMemory<byte> body, string query, CancellationToken ct)
    {
        try
        {
            return op switch
            {
                LocalMonitorV1ComparisonOperation.Preview => await Preview(repositoryId, LocalMonitorV1ComparisonParser.ParsePreview(body.Span), ct),
                LocalMonitorV1ComparisonOperation.Create => await Create(repositoryId, LocalMonitorV1ComparisonParser.ParseCreate(body.Span), ct),
                LocalMonitorV1ComparisonOperation.Read => Load(repositoryId, comparisonId!, ct, s => new(200, ComparisonJson.Read(s))),
                LocalMonitorV1ComparisonOperation.Rows => Rows(repositoryId, comparisonId!, LocalMonitorV1ComparisonQueryParser.ParseRows(query), ct),
                _ => Evidence(repositoryId, comparisonId!, LocalMonitorV1ComparisonQueryParser.ParseEvidence(query), ct)
            };
        }
        catch (LocalMonitorV1ComparisonCursorException) { return Error(400, "invalid_cursor"); }
        catch (LocalMonitorV1ComparisonQueryException) { return Error(400, "invalid_request"); }
        catch (LocalMonitorV1ComparisonRequestException) { return Error(400, "invalid_request"); }
        catch (ArgumentException) { return Error(400, "invalid_request"); }
        catch (LocalRepositoryScopeSnapshotException x) when (x.Error == LocalRepositoryScopeSnapshotError.PersistenceBusy) { return Error(503, "persistence_busy"); }
        catch (LocalComparisonTooLargeException) { return Error(409, "workspace_too_large"); }
    }

    private async ValueTask<LocalMonitorV1ComparisonResponse> Preview(string repositoryId, LocalMonitorV1ComparisonPreviewRequest request, CancellationToken ct)
    { var p = await Project(repositoryId, request.CohortA, request.CohortB, request.IncludeArchived, ct); return new(200, ComparisonJson.Preview(p.Preview)); }
    private async ValueTask<LocalMonitorV1ComparisonResponse> Create(string repositoryId, LocalMonitorV1ComparisonCreateRequest request, CancellationToken ct)
    {
        var p = await Project(repositoryId, request.CohortA, request.CohortB, request.IncludeArchived, ct);
        if (!p.Preview.Valid) return Error(409, "comparison_selection_invalid");
        if (p.Preview.SelectionSha256 != request.SelectionSha256 || p.Preview.PreviewRevision != request.PreviewRevision) return Error(409, "comparison_preview_stale");
        var result = core.Create(p.Draft, ct);
        if (result.Status == LocalComparisonCreateStatus.PersistenceBusy) return Error(503, "persistence_busy");
        if (result.Status == LocalComparisonCreateStatus.TooLarge) return Error(409, "workspace_too_large");
        if (result.Status != LocalComparisonCreateStatus.Accepted) return Error(409, "comparison_selection_invalid");
        var location = $"/repositories/{repositoryId}/comparisons/{result.Snapshot!.ComparisonId}";
        return new(201, ComparisonJson.Create(result.Snapshot, location), location);
    }
    private LocalMonitorV1ComparisonResponse Rows(string repositoryId, string id, LocalMonitorV1ComparisonRowsQuery q, CancellationToken ct) => Load(repositoryId, id, ct, s =>
    {
        var binding = q.Family + "\n" + (q.Search ?? ""); var after = q.After is null ? 0 : cursors.Decode(q.After, repositoryId, id, "rows", binding);
        var all = s.Results.Where(x => x.ResultOrdinal > after && x.RowKind == q.Family).Where(x => q.Search is null || Normalize(x.RowKey).Contains(q.Search, StringComparison.Ordinal) || Normalize(Display(x)).Contains(q.Search, StringComparison.Ordinal)).OrderBy(x => x.ResultOrdinal).Take(q.Limit + 1).ToArray();
        var page = all.Take(q.Limit).ToArray(); var next = all.Length > q.Limit ? cursors.Encode(repositoryId, id, "rows", binding, page[^1].ResultOrdinal) : null;
        return new(200, ComparisonJson.Rows(id, q.Family, page, next));
    });
    private LocalMonitorV1ComparisonResponse Evidence(string repositoryId, string id, LocalMonitorV1ComparisonEvidenceQuery q, CancellationToken ct) => Load(repositoryId, id, ct, s =>
    {
        var result = s.Results.SingleOrDefault(x => x.ResultOrdinal == q.ResultOrdinal);
        if (result is null) return Error(404, "comparison_not_found");
        var storedField = ResolveEvidenceField(result, q.FieldKey);
        if (q.FieldKey is not null && storedField is null) return Error(404, "comparison_not_found");
        var binding = q.ResultOrdinal.ToString(CultureInfo.InvariantCulture) + "\n" + (q.FieldKey ?? ""); var after = q.After is null ? -1 : cursors.Decode(q.After, repositoryId, id, "evidence", binding) - 1;
        var all = s.Evidence.Where(x => x.ResultOrdinal == q.ResultOrdinal && x.EvidenceOrdinal > after && (storedField is null || x.FieldKey == storedField)).OrderBy(x => x.EvidenceOrdinal).Take(q.Limit + 1).ToArray();
        var page = all.Take(q.Limit).ToArray(); var next = all.Length > q.Limit ? cursors.Encode(repositoryId, id, "evidence", binding, page[^1].EvidenceOrdinal + 1) : null;
        return new(200, ComparisonJson.Evidence(id, q.ResultOrdinal, q.FieldKey, page, next));
    });
    private LocalMonitorV1ComparisonResponse Load(string repositoryId, string id, CancellationToken ct, Func<LocalComparisonFrozenSnapshot, LocalMonitorV1ComparisonResponse> found)
    { var r = store.Read(repositoryId, id, ct); return r.Status switch { LocalComparisonReadStatus.Found => found(r.Snapshot!), LocalComparisonReadStatus.Expired => Error(410, "comparison_expired"), LocalComparisonReadStatus.PersistenceBusy => Error(503, "persistence_busy"), _ => Error(404, "comparison_not_found") }; }

    private async ValueTask<Projected> Project(string repositoryId, IReadOnlyList<string> a, IReadOnlyList<string> b, bool archived, CancellationToken ct)
    {
        var requested = a.Concat(b).Distinct(StringComparer.Ordinal).ToArray();
        var snapshot = await inputs.ReadComparisonInputAsync(new(LocalRepositoryScopeKind.Repository, repositoryId, ExactTargetSessionIds: requested), ct);
        var candidates = snapshot.Sessions.Select(Candidate).ToArray();
        var revision = Hash(snapshot.Scope.Repositories.OrderBy(x => x.RepositoryId).SelectMany(x => new[] { x.RepositoryId, x.Revision.ToString(CultureInfo.InvariantCulture), x.ArchiveRevision.ToString(CultureInfo.InvariantCulture) }).Concat(snapshot.Sessions.Select(x => x.WorkspaceRevision)));
        var preview = LocalComparisonInputProjection.Project(repositoryId, a, b, archived, candidates, revision); var map = snapshot.Sessions.ToDictionary(x => x.Session.SessionId, StringComparer.Ordinal);
        LocalComparisonSessionFact Fact(string id) { var x = map[id]; return LocalComparisonInputProjection.MapSessionFact(x.Session, x.Detail, x.WorkspaceRevision, archived); }
        var aa = preview.Included.Where(x => a.Contains(x.SessionId)).Select(x => Fact(x.SessionId)).ToArray(); var bb = preview.Included.Where(x => b.Contains(x.SessionId) && !a.Contains(x.SessionId)).Select(x => Fact(x.SessionId)).ToArray();
        var draft = new LocalComparisonDraft(repositoryId, new(Array.AsReadOnly(aa), preview.Excluded.Count(x => x.Cohort == "a")), new(Array.AsReadOnly(bb), preview.Excluded.Count(x => x.Cohort == "b")), SHA256.HashData(Encoding.ASCII.GetBytes(preview.PreviewRevision)));
        return new(preview, draft);
    }
    private static LocalComparisonProjectionCandidate Candidate(LocalRepositoryComparisonSessionInput x) { var s = x.Session; var row = (LocalWorkspaceProjectionRow)s.Session; var state = s.RepositoryId is null ? LocalComparisonCandidateState.RepositoryMismatch : s.IsEffectivelyEligible ? LocalComparisonCandidateState.Included : LocalComparisonCandidateState.UnsupportedSelection; return new(s.SessionId, s.RepositoryId ?? "", state, s.ArchiveState == LocalArchiveState.Archived, s.ArchiveState == LocalArchiveState.Archived ? "archived" : "active", row.Sources.Values, row.Sources.State, row.Models.Values, row.Models.State, 1, row.Completeness, ["tokens", "time_and_execution", "skills", "tools", "subagents", "errors_and_retries", "conditions"], s.AssignmentRevision, x.WorkspaceRevision); }
    private static string Hash(IEnumerable<string> values) { using var s = new MemoryStream(); foreach (var x in values) LocalComparisonSelectionFrame.WriteFrame(s, x); return Convert.ToHexStringLower(SHA256.HashData(s.ToArray())); }
    private static string Display(LocalComparisonStoredResult x) => x.Values.FirstOrDefault(v => v.Key == "display_name").Value ?? x.RowKey;
    private static string? ResolveEvidenceField(LocalComparisonStoredResult result, string? field)
    {
        if (field is null) return null;
        if (result.SectionOrdinal == 1)
        {
            if (field == "count" && result.RowKey is "included_session_count" or "available_session_count") return "selection";
            if (field == "condition" && result.RowKind == "condition")
                return result.RowKey switch { "period" => "observed_at", "archived_inclusion" => "selection", _ => null };
            return null;
        }
        if (result.RowKind is "skill" or "tool" or "subagent")
        {
            return (result.RowKind, field) switch
            {
                ("skill", "count") => "invocation_count",
                ("tool", "count") => "call_count",
                ("tool", "error_count") => "failure_count",
                ("tool", "retry_count") => "retry_count",
                ("subagent", "count") => "start_count",
                ("subagent", "total_tokens") => "recorded_tokens",
                _ => null,
            };
        }
        if (result.RowKind == "condition") return field == "condition" ? "value" : null;
        if (field is "available_count" or "median" or "minimum" or "maximum" or "total" or "absolute_difference" or "relative_difference_percent")
            return result.RowKind == "scalar" ? "value" : null;
        if (field == "duration_ms" && result.RowKey == "session_duration") return "value";
        if (field is "input_tokens" or "output_tokens" or "total_tokens" or "cache_read" or "cache_creation" or "new_input" or "error_count" or "retry_count")
        {
            var expected = field switch { "cache_read" => "cache_read_tokens", "cache_creation" => "cache_creation_tokens", "new_input" => "new_input_tokens", _ => field };
            return result.RowKey == expected ? "value" : null;
        }
        return result.RowKind == "scalar" && field == "value" ? "value" : null;
    }
    private static string Normalize(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    private static LocalMonitorV1ComparisonResponse Error(int status, string code) => new(status, Encoding.UTF8.GetBytes($"{{\"error\":\"{code}\"}}"));
    private sealed record Projected(LocalComparisonProjectionPreview Preview, LocalComparisonDraft Draft);
}

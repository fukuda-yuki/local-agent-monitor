using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal static class LocalMonitorV1CandidateApplication
{
    internal static async Task<byte[]> ResolveAsync(JsonElement body, ILocalRepositoryScopeSnapshotService service,
        ILocalRepositoryComparisonInputSnapshotService? comparisonService, Func<Guid, Guid, CancellationToken, Task<string?>>? historicalDigest,
        byte[] cursorKey, CancellationToken cancellationToken)
    {
        if (!LocalMonitorV1InvestigationStore.Keys(body, "method", "search", "skill_name", "skill_digest")
            || body.GetProperty("method").ValueKind != JsonValueKind.String
            || body.GetProperty("method").GetString() is not ("filters" or "skill_digest" or "skill_options")
            || LocalMonitorV1SessionSearchRequestParser.Parse(System.Text.Encoding.UTF8.GetBytes(body.GetProperty("search").GetRawText()), out var request)
                != LocalMonitorV1SessionSearchParseStatus.Success
            || request!.Scope != "repository" || request.Cursor is not null) throw new LocalMonitorV1CollectionException("invalid_request");
        var method = body.GetProperty("method").GetString()!;
        var name = body.GetProperty("skill_name"); var digest = body.GetProperty("skill_digest");
        if (method == "skill_digest"
                ? name.ValueKind != JsonValueKind.String || name.GetString()!.Length is < 1 or > 200 || !LocalMonitorV1InvestigationStore.Hex(digest)
                : name.ValueKind != JsonValueKind.Null || digest.ValueKind != JsonValueKind.Null) throw new LocalMonitorV1CollectionException("invalid_request");
        var scopeRequest = new LocalRepositoryScopeRequest(LocalRepositoryScopeKind.Repository, request.RepositoryId);
        LocalRepositoryComparisonInputSnapshot? comparison = null;
        var snapshot = await service.ReadAsync(scopeRequest, cancellationToken);
        var candidates = snapshot.Sessions.Where(row => LocalMonitorV1CollectionApplication.SelectorMatches(row, request with { ArchiveScope = "include_archived" }))
            .OrderBy(row => ((LocalWorkspaceProjectionRow)row.Session).SessionId, StringComparer.Ordinal).ToArray();
        if (candidates.Length > 200) throw new LocalMonitorV1CollectionException("workspace_too_large");
        using var collection = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request with { Cursor = null }, cursorKey));
        var revision = collection.RootElement.GetProperty("workspace_revision").GetString()!;
        var ids = candidates.Select(row => ((LocalWorkspaceProjectionRow)row.Session).SessionId).ToHashSet(StringComparer.Ordinal);
        if (method != "filters" && comparisonService is not null && ids.Count != 0)
            comparison = await comparisonService.ReadComparisonInputAsync(scopeRequest with { ExactTargetSessionIds = ids.Order(StringComparer.Ordinal).ToArray() }, cancellationToken);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var options = new HashSet<(string Name, string Digest)>();
        var unavailable = method == "filters" ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(ids, StringComparer.Ordinal);
        if (method != "filters")
        {
            if (comparison is not null && historicalDigest is not null)
            {
                var references = comparison.Sessions.Where(row => ids.Contains(((LocalWorkspaceProjectionRow)row.Session.Session).SessionId)
                        && LocalMonitorV1CollectionApplication.SelectorMatches(row.Session, request with { ArchiveScope = "include_archived" }))
                    .SelectMany(row => row.ComparisonDetail.Nodes.Where(node => node.Kind == "skill")
                        .Select(node => (SessionId: ((LocalWorkspaceProjectionRow)row.Session.Session).SessionId, Node: node))).ToArray();
                foreach (var reference in references)
                {
                    var node = reference.Node;
                    if (node.NameState != "recorded" || node.NameText is null || node.SkillMetadata is not { CurrentValidState: "current", HistoricalSnapshotReferenceState: "recorded" } metadata
                        || !Guid.TryParseExact(metadata.HistoricalSnapshotReference, "D", out var snapshotId)) continue;
                    var bodyDigest = await historicalDigest(Guid.Parse(reference.SessionId), snapshotId, cancellationToken);
                    if (bodyDigest is null) continue;
                    unavailable.Remove(reference.SessionId);
                    options.Add((node.NameText, bodyDigest));
                    if (options.Count > 256) throw new LocalMonitorV1CollectionException("workspace_too_large");
                    if (method == "skill_digest" && node.NameText == name.GetString() && bodyDigest == digest.GetString()) matched.Add(reference.SessionId);
                }
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { method, session_ids = method == "filters" ? ids.Order(StringComparer.Ordinal).ToArray() : matched.Order(StringComparer.Ordinal).ToArray(), workspace_revision = revision,
            skill_digests = options.OrderBy(option => option.Name, StringComparer.Ordinal).ThenBy(option => option.Digest, StringComparer.Ordinal).Select(option => new { name = option.Name, digest = option.Digest }).ToArray(), unavailable_count = unavailable.Count });
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed record LocalAiRepositoryPreviewResultV1(byte[] ResponseJson,LocalAiSnapshotProjectionV1 Snapshot);
internal interface ILocalAiRepositorySnapshotAdapterV1
{
    ValueTask<LocalAiRepositoryPreviewResultV1> PreviewAsync(LocalAiRepositoryPreviewRequestV1 request,CancellationToken token);
    ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 snapshot,CancellationToken token);
    ValueTask<LocalAiSnapshotProjectionV1?> RehydrateCurrentAsync(LocalAiSnapshotProjectionV1 snapshot,CancellationToken token);
}

internal sealed class LocalAiRepositorySnapshotAdapterV1(ILocalRepositoryScopeSnapshotService scopes,ILocalAiSnapshotProjectionServiceV1 projections,IHistoricalEvidenceSnapshotSourceV1 historicalEvidence,TimeProvider? timeProvider=null):ILocalAiRepositorySnapshotAdapterV1
{
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    public async ValueTask<LocalAiRepositoryPreviewResultV1> PreviewAsync(LocalAiRepositoryPreviewRequestV1 request,CancellationToken token)
    {
        var scopeRequest=new LocalRepositoryScopeRequest(LocalRepositoryScopeKind.Repository,request.RepositoryId,ExactTargetSessionIds:request.Kind=="explicit"?request.SessionIds:null);
        var scope=await scopes.ReadAsync(scopeRequest,token).ConfigureAwait(false);
        var candidates=request.Kind=="filter"?scope.Sessions.Where(row=>LocalMonitorV1CollectionApplication.SelectorMatches(row,request.Filter!)).ToArray():
            request.SessionIds.Select(id=>scope.Sessions.SingleOrDefault(row=>row.SessionId==id)).Where(static row=>row is not null).Cast<LocalRepositoryScopeSessionSnapshot>().ToArray();
        if(candidates.Length>200)throw new LocalAiScopeTooLargeException();
        var included=new List<object>();var excluded=new List<object>();var frozen=new List<object>();var selected=new List<LocalAiSnapshotProjectionV1>();var evidence=new HashSet<string>(StringComparer.Ordinal);var raw=new Dictionary<string,LocalAiRawEvidenceV1>(StringComparer.Ordinal);
        foreach(var id in request.Kind=="explicit"?request.SessionIds:candidates.Select(static row=>row.SessionId))
        {
            var row=candidates.SingleOrDefault(item=>item.SessionId==id);
            if(row is null){excluded.Add(Excluded(id,"session_not_found",null));continue;}
            if(PreviewExclusion(row,request.ArchiveScope) is { } exclusion){excluded.Add(Excluded(id,exclusion,row));continue;}
            LocalAiSnapshotProjectionV1 projection;
            try{projection=await projections.ReadSessionAsync(id,token).ConfigureAwait(false);}catch(LocalWorkspaceSessionDetailException){excluded.Add(Excluded(id,"projection_unavailable",row));continue;}
            selected.Add(projection);
            foreach(var item in projection.EvidenceIdentifiers)evidence.Add(LocalMonitorV1CanonicalUrlBuilder.BuildSessionEvidence(id,null,item));
            foreach(var item in projection.RawEvidence??new Dictionary<string,LocalAiRawEvidenceV1>()){var handle=id+":"+item.Key;raw.Add(handle,item.Value with{EvidenceId=handle,SessionId=id});}
            var p=(LocalWorkspaceProjectionRow)row.Session;var value=new{session_id=id,session_archive_state=Archive(row.ArchiveState),session_archive_revision=row.ArchiveRevision,
                repository_archive_state=RepositoryState(scope,request.RepositoryId),repository_archive_revision=RepositoryRevision(scope,request.RepositoryId),archive_exclusion_reason=(string?)null,
                source=new{state=p.Sources.State,values=p.Sources.Values},model=new{state=p.Models.State,values=p.Models.Values},completeness=p.Completeness,content_state=PreviewContentState(projection.RawEvidence),workspace_revision=projection.Revision,truncated=false};
            included.Add(value);frozen.Add(new{session_id=id,assignment_revision=row.AssignmentRevision,session_archive_revision=row.ArchiveRevision,repository_archive_revision=RepositoryRevision(scope,request.RepositoryId),workspace_revision=projection.Revision,payload_sha256=projection.PayloadSha256,evidence_refs=projection.EvidenceIdentifiers.Select(node=>LocalMonitorV1CanonicalUrlBuilder.BuildSessionEvidence(id,null,node)).Order(StringComparer.Ordinal).ToArray()});
        }
        var expires=clock.GetUtcNow().AddHours(24);var snapshotId=Guid.CreateVersion7().ToString();
        var repositorySafeEvidence=selected.Count==0
            ? JsonSerializer.SerializeToUtf8Bytes(new{schema_version=HistoricalEvidenceContractsV1.RepositorySafeSchemaVersion,sessions=Array.Empty<object>(),evidence_groups=Array.Empty<object>(),distribution=new{completeness=Array.Empty<object>(),source_kinds=Array.Empty<object>(),capabilities=Array.Empty<object>()}})
            :(await HistoricalEvidenceExtractorV1.ExtractAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds:selected.Select(x=>Guid.Parse(x.SessionId!)).ToArray(),maximumSessionCount:selected.Count),historicalEvidence,token).ConfigureAwait(false)).RepositorySafeBytes;
        var selectedPayload=ComposeSelectedPayload(request.RepositoryId,selected,repositorySafeEvidence);
        using var selectedDocument=JsonDocument.Parse(selectedPayload);
        var payload=LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new{schema_version="local_ai_repository_snapshot:1",repository_id=request.RepositoryId,selection_kind=request.Kind,expires_at=expires.ToUniversalTime().ToString("O"),members=frozen,repository_safe_sha256=Convert.ToHexStringLower(SHA256.HashData(repositorySafeEvidence)),selected_projection=selectedDocument.RootElement.Clone(),raw_content=raw.Select(x=>new{evidence_id=x.Key,session_id=x.Value.SessionId,citation_ref=LocalMonitorV1CanonicalUrlBuilder.BuildSessionEvidence(x.Value.SessionId!,null,x.Value.NodeId),state=x.Value.Locator.State,selected_utf8_bytes=x.Value.Locator.SelectedUtf8Bytes}).ToArray()}));
        var index=LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new{evidence_refs=evidence.Order(StringComparer.Ordinal).ToArray()}));var hash=Convert.ToHexStringLower(SHA256.HashData(payload));
        if(payload.Length>LocalAiAnalysisStoreV1.MaximumSnapshotDocumentBytes||index.Length>LocalAiAnalysisStoreV1.MaximumSnapshotDocumentBytes)throw new LocalAiScopeTooLargeException();
        var snapshot=new LocalAiSnapshotProjectionV1(snapshotId,"repository_selection",null,null,request.RepositoryId,hash,payload,index,hash,evidence,raw,RepositoryId:request.RepositoryId,ExpiresAt:expires);
        var response=JsonSerializer.SerializeToUtf8Bytes(new{schema_version="local-ai-repository-preview.response.v1",snapshot_id=snapshotId,payload_sha256=hash,expires_at=expires.ToUniversalTime().ToString("O"),included,excluded,truncated=false});
        return new(response,snapshot);
        object Excluded(string id,string reason,LocalRepositoryScopeSessionSnapshot? row)=>new{session_id=id,reason,session_archive_state=row is null?null:Archive(row.ArchiveState),session_archive_revision=row?.ArchiveRevision,
            repository_archive_state=row is null?null:RepositoryState(scope,request.RepositoryId),repository_archive_revision=row is null?(long?)null:RepositoryRevision(scope,request.RepositoryId),archive_exclusion_reason=row?.ArchiveExclusionReason,
            source=row?.Session is LocalWorkspaceProjectionRow p?new{state=p.Sources.State,values=p.Sources.Values}:null,model=row?.Session is LocalWorkspaceProjectionRow q?new{state=q.Models.State,values=q.Models.Values}:null,completeness=(row?.Session as LocalWorkspaceProjectionRow)?.Completeness,content_state=(string?)null,workspace_revision=(string?)null,truncated=(bool?)null};
    }

    public async ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 snapshot,CancellationToken token)=>await RehydrateCurrentAsync(snapshot,token).ConfigureAwait(false) is not null;
    public async ValueTask<LocalAiSnapshotProjectionV1?> RehydrateCurrentAsync(LocalAiSnapshotProjectionV1 snapshot,CancellationToken token)
    {
        try
        {
            using var document=JsonDocument.Parse(snapshot.PayloadCanonicalJson);var members=document.RootElement.GetProperty("members");var ids=members.EnumerateArray().Select(x=>x.GetProperty("session_id").GetString()!).ToArray();
            var scope=await scopes.ReadAsync(new(LocalRepositoryScopeKind.Repository,snapshot.RepositoryId,ExactTargetSessionIds:ids),token).ConfigureAwait(false);
            var raw=new Dictionary<string,LocalAiRawEvidenceV1>(StringComparer.Ordinal);
            foreach(var member in members.EnumerateArray()){var id=member.GetProperty("session_id").GetString()!;var row=scope.Sessions.SingleOrDefault(x=>x.SessionId==id);if(row is null||!MatchesFrozenMembership(row,RepositoryRevision(scope,snapshot.RepositoryId!),member))return null;var current=await projections.ReadSessionAsync(id,token).ConfigureAwait(false);if(current.Revision!=member.GetProperty("workspace_revision").GetString()||current.PayloadSha256!=member.GetProperty("payload_sha256").GetString())return null;foreach(var item in current.RawEvidence??new Dictionary<string,LocalAiRawEvidenceV1>()){var handle=id+":"+item.Key;raw.Add(handle,item.Value with{EvidenceId=handle,SessionId=id});}}
            var currentEvidence=ids.Length==0?JsonSerializer.SerializeToUtf8Bytes(new{schema_version=HistoricalEvidenceContractsV1.RepositorySafeSchemaVersion,sessions=Array.Empty<object>(),evidence_groups=Array.Empty<object>(),distribution=new{completeness=Array.Empty<object>(),source_kinds=Array.Empty<object>(),capabilities=Array.Empty<object>()}}):(await HistoricalEvidenceExtractorV1.ExtractAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds:ids.Select(Guid.Parse).ToArray(),maximumSessionCount:ids.Length),historicalEvidence,token).ConfigureAwait(false)).RepositorySafeBytes;
            if(Convert.ToHexStringLower(SHA256.HashData(currentEvidence))!=document.RootElement.GetProperty("repository_safe_sha256").GetString())return null;
            return snapshot with{RawEvidence=raw};
        }
        catch(Exception exception) when(exception is LocalWorkspaceSessionDetailException or InvalidOperationException or JsonException){return null;}
    }
    private static string Archive(LocalArchiveState value)=>value==LocalArchiveState.Active?"active":"archived";
    private static long RepositoryRevision(LocalRepositoryScopeSnapshot scope,string id)=>scope.Repositories.Single(x=>x.RepositoryId==id).ArchiveRevision;
    private static string RepositoryState(LocalRepositoryScopeSnapshot scope,string id)=>Archive(scope.Repositories.Single(x=>x.RepositoryId==id).ArchiveState);

    internal static byte[] ComposeSelectedPayload(string repositoryId,IReadOnlyList<LocalAiSnapshotProjectionV1> projections,byte[] repositorySafeEvidence)
    {
        var rows=projections.Select(projection=>new{session_id=projection.SessionId,workspace_revision=projection.Revision,payload=NormalizeProjection(projection),evidence_locations=projection.EvidenceIdentifiers.Select(node=>LocalMonitorV1CanonicalUrlBuilder.BuildSessionEvidence(projection.SessionId!,null,node)).Order(StringComparer.Ordinal).ToArray()}).ToArray();
        using var evidence=JsonDocument.Parse(repositorySafeEvidence);var bytes=LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new{repository_id=repositoryId,sessions=rows,repository_safe_evidence=evidence.RootElement.Clone()}));
        if(bytes.Length>LocalAiAnalysisStoreV1.MaximumSnapshotDocumentBytes)throw new LocalAiScopeTooLargeException();return bytes;
    }
    private static JsonNode NormalizeProjection(LocalAiSnapshotProjectionV1 projection)
    {
        var root=JsonNode.Parse(projection.PayloadCanonicalJson)!.AsObject();
        if(root["raw_content"] is JsonArray raw)
        {
            foreach(var item in raw.OfType<JsonObject>()){var evidenceId=item["evidence_id"]?.GetValue<string>();var citation=item["citation_ref"]?.GetValue<string>();if(evidenceId is not null)item["evidence_id"]=projection.SessionId+":"+evidenceId;if(citation is not null)item["citation_ref"]=LocalMonitorV1CanonicalUrlBuilder.BuildSessionEvidence(projection.SessionId!,null,citation);}
        }
        if(root["sanitized_span_observations"] is JsonArray observations)
            foreach(var item in observations.OfType<JsonObject>()){var citation=item["citation_ref"]?.GetValue<string>();if(citation is not null)item["citation_ref"]=LocalMonitorV1CanonicalUrlBuilder.BuildSessionEvidence(projection.SessionId!,null,citation);}
        return root;
    }
    internal static bool MatchesFrozenMembership(LocalRepositoryScopeSessionSnapshot row,long repositoryArchiveRevision,JsonElement member)=>row.IsRequestedScopeMember&&row.AssignmentRevision==member.GetProperty("assignment_revision").GetInt64()&&row.ArchiveRevision==member.GetProperty("session_archive_revision").GetInt64()&&repositoryArchiveRevision==member.GetProperty("repository_archive_revision").GetInt64();
    internal static string? PreviewExclusion(LocalRepositoryScopeSessionSnapshot row,string archiveScope)=>!row.IsRequestedScopeMember?"repository_mismatch":archiveScope=="active_only"&&!row.IsEffectivelyEligible?(row.ArchiveExclusionReason=="repository_archived"?"repository_archived":"session_archived"):null;
    internal static string PreviewContentState(IReadOnlyDictionary<string,LocalAiRawEvidenceV1>? evidence)
    {
        var states=(evidence?.Values??[]).Select(static item=>item.Locator.State).ToArray();
        if(states.Contains("available",StringComparer.Ordinal))return "available";
        if(states.Any(static state=>state is "expired" or "deleted"))return "expired_pending_deletion";
        return "not_captured";
    }
}

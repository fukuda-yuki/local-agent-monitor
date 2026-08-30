using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed record LocalAiRepositoryPreviewResultV1(byte[] ResponseJson,LocalAiSnapshotProjectionV1 Snapshot);
internal interface ILocalAiRepositorySnapshotAdapterV1
{
    ValueTask<LocalAiRepositoryPreviewResultV1> PreviewAsync(LocalAiRepositoryPreviewRequestV1 request,CancellationToken token);
    ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 snapshot,CancellationToken token);
}

internal sealed class LocalAiRepositorySnapshotAdapterV1(ILocalRepositoryScopeSnapshotService scopes,ILocalAiSnapshotProjectionServiceV1 projections,TimeProvider? timeProvider=null):ILocalAiRepositorySnapshotAdapterV1
{
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    public async ValueTask<LocalAiRepositoryPreviewResultV1> PreviewAsync(LocalAiRepositoryPreviewRequestV1 request,CancellationToken token)
    {
        var scopeRequest=new LocalRepositoryScopeRequest(LocalRepositoryScopeKind.Repository,request.RepositoryId,ExactTargetSessionIds:request.Kind=="explicit"?request.SessionIds:null);
        var scope=await scopes.ReadAsync(scopeRequest,token).ConfigureAwait(false);
        var candidates=request.Kind=="filter"?scope.Sessions.Where(row=>LocalMonitorV1CollectionApplication.SelectorMatches(row,request.Filter!)).ToArray():
            request.SessionIds.Select(id=>scope.Sessions.SingleOrDefault(row=>row.SessionId==id)).Where(static row=>row is not null).Cast<LocalRepositoryScopeSessionSnapshot>().ToArray();
        if(candidates.Length>200)throw new LocalAiScopeTooLargeException();
        var included=new List<object>();var excluded=new List<object>();var frozen=new List<object>();var evidence=new HashSet<string>(StringComparer.Ordinal);
        foreach(var id in request.Kind=="explicit"?request.SessionIds:candidates.Select(static row=>row.SessionId))
        {
            var row=candidates.SingleOrDefault(item=>item.SessionId==id);
            if(row is null){excluded.Add(Excluded(id,"session_not_found",null));continue;}
            if(!row.IsRequestedScopeMember){excluded.Add(Excluded(id,"repository_mismatch",row));continue;}
            if(request.ArchiveScope=="active_only"&&!row.IsEffectivelyEligible){excluded.Add(Excluded(id,row.ArchiveExclusionReason=="repository_archived"?"repository_archived":"session_archived",row));continue;}
            LocalAiSnapshotProjectionV1 projection;
            try{projection=await projections.ReadSessionAsync(id,token).ConfigureAwait(false);}catch(LocalWorkspaceSessionDetailException){excluded.Add(Excluded(id,"projection_unavailable",row));continue;}
            foreach(var item in projection.EvidenceIdentifiers)evidence.Add(item);
            var p=(LocalWorkspaceProjectionRow)row.Session;var value=new{session_id=id,session_archive_state=Archive(row.ArchiveState),session_archive_revision=row.ArchiveRevision,
                repository_archive_state=RepositoryState(scope,request.RepositoryId),repository_archive_revision=RepositoryRevision(scope,request.RepositoryId),archive_exclusion_reason=(string?)null,
                source=p.Sources,model=p.Models,completeness=p.Completeness,content_state=projection.RawEvidence?.Count>0?"available":"sanitized_only",workspace_revision=projection.Revision,truncated=false};
            included.Add(value);frozen.Add(new{session_id=id,session_archive_revision=row.ArchiveRevision,repository_archive_revision=RepositoryRevision(scope,request.RepositoryId),workspace_revision=projection.Revision,payload_sha256=projection.PayloadSha256,evidence_refs=projection.EvidenceIdentifiers.Order(StringComparer.Ordinal).ToArray()});
        }
        var expires=clock.GetUtcNow().AddHours(24);var snapshotId=Guid.CreateVersion7().ToString();
        var payload=LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new{schema_version="local_ai_repository_snapshot:1",repository_id=request.RepositoryId,selection_kind=request.Kind,expires_at=expires.ToUniversalTime().ToString("O"),members=frozen}));
        var index=LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new{evidence_refs=evidence.Order(StringComparer.Ordinal).ToArray()}));var hash=Convert.ToHexStringLower(SHA256.HashData(payload));
        var snapshot=new LocalAiSnapshotProjectionV1(snapshotId,"repository_selection",null,null,request.RepositoryId,hash,payload,index,hash,evidence,RepositoryId:request.RepositoryId,ExpiresAt:expires);
        var response=JsonSerializer.SerializeToUtf8Bytes(new{schema_version="local-ai-repository-preview.response.v1",snapshot_id=snapshotId,payload_sha256=hash,expires_at=expires.ToUniversalTime().ToString("O"),included,excluded,truncated=false});
        return new(response,snapshot);
        object Excluded(string id,string reason,LocalRepositoryScopeSessionSnapshot? row)=>new{session_id=id,reason,session_archive_state=row is null?null:Archive(row.ArchiveState),session_archive_revision=row?.ArchiveRevision,
            repository_archive_state=row is null?null:RepositoryState(scope,request.RepositoryId),repository_archive_revision=row is null?(long?)null:RepositoryRevision(scope,request.RepositoryId),archive_exclusion_reason=row?.ArchiveExclusionReason,
            source=row?.Session is LocalWorkspaceProjectionRow p?p.Sources:null,model=row?.Session is LocalWorkspaceProjectionRow q?q.Models:null,completeness=(row?.Session as LocalWorkspaceProjectionRow)?.Completeness,content_state=(string?)null,workspace_revision=(string?)null,truncated=(bool?)null};
    }

    public async ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 snapshot,CancellationToken token)
    {
        try
        {
            using var document=JsonDocument.Parse(snapshot.PayloadCanonicalJson);var members=document.RootElement.GetProperty("members");var ids=members.EnumerateArray().Select(x=>x.GetProperty("session_id").GetString()!).ToArray();
            var scope=await scopes.ReadAsync(new(LocalRepositoryScopeKind.Repository,snapshot.RepositoryId,ExactTargetSessionIds:ids),token).ConfigureAwait(false);
            foreach(var member in members.EnumerateArray()){var id=member.GetProperty("session_id").GetString()!;var row=scope.Sessions.SingleOrDefault(x=>x.SessionId==id);if(row is null||row.ArchiveRevision!=member.GetProperty("session_archive_revision").GetInt64()||RepositoryRevision(scope,snapshot.RepositoryId!)!=member.GetProperty("repository_archive_revision").GetInt64())return false;var current=await projections.ReadSessionAsync(id,token).ConfigureAwait(false);if(current.Revision!=member.GetProperty("workspace_revision").GetString()||current.PayloadSha256!=member.GetProperty("payload_sha256").GetString())return false;}return true;
        }
        catch(Exception exception) when(exception is LocalWorkspaceSessionDetailException or InvalidOperationException or JsonException){return false;}
    }
    private static string Archive(LocalArchiveState value)=>value==LocalArchiveState.Active?"active":"archived";
    private static long RepositoryRevision(LocalRepositoryScopeSnapshot scope,string id)=>scope.Repositories.Single(x=>x.RepositoryId==id).ArchiveRevision;
    private static string RepositoryState(LocalRepositoryScopeSnapshot scope,string id)=>Archive(scope.Repositories.Single(x=>x.RepositoryId==id).ArchiveState);
}

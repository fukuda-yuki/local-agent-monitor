using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalAiExtendedScopeTests
{
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000001";
    private const string ComparisonId = "018f0000-0000-7000-8000-000000000002";
    private const string SnapshotId = "018f0000-0000-7000-8000-000000000003";

    [Theory]
    [InlineData(ProviderBehavior.Complete)]
    [InlineData(ProviderBehavior.Partial)]
    [InlineData(ProviderBehavior.Exception)]
    [InlineData(ProviderBehavior.Timeout)]
    [InlineData(ProviderBehavior.Cancellation)]
    public async Task ProviderSession_IsDisposedThenDeletedExactlyOnceBeforeClientDisposal(
        ProviderBehavior behavior)
    {
        var events = new List<string>();
        var client = new ProviderClient(events, behavior);
        var adapter = new GitHubCopilotLocalAiProviderAdapterV1(() => client, "synthetic-model");
        using var cancellation = new CancellationTokenSource();
        if (behavior == ProviderBehavior.Cancellation) cancellation.Cancel();

        if (behavior is ProviderBehavior.Complete or ProviderBehavior.Partial)
            _ = await adapter.ExecuteAsync(ProviderRequest("repository_selection"), cancellation.Token);
        else
            _ = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await adapter.ExecuteAsync(ProviderRequest("repository_selection"), cancellation.Token));

        Assert.Equal(["session.dispose", "session.delete", "client.dispose"], events);
        Assert.Equal(1, client.DeleteCalls);
        Assert.Equal("synthetic-session", client.DeletedSessionId);
        Assert.False(client.DeleteTokenCanBeCanceled);
        Assert.False(client.SessionMarkerPresent);
    }

    [Fact]
    public async Task ProviderSession_DeleteFailureCannotReturnComplete()
    {
        var events = new List<string>();
        var client = new ProviderClient(events, ProviderBehavior.Complete, failDelete: true);
        var adapter = new GitHubCopilotLocalAiProviderAdapterV1(() => client, "synthetic-model");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await adapter.ExecuteAsync(ProviderRequest("repository_selection"), CancellationToken.None));

        Assert.Equal("synthetic_cleanup_failure", error.Message);
        Assert.Equal(["session.dispose", "session.delete", "client.dispose"], events);
        Assert.Equal(1, client.DeleteCalls);
        Assert.True(client.SessionMarkerPresent);
    }

    [Fact]
    public void RepositoryRunRequest_RequiresClosedVersionedSnapshotReceipt()
    {
        Assert.True(LocalAiExtendedScopeRequestParser.TryRepositoryRun(
            Encoding.UTF8.GetBytes($$"""{"schema_version":"local-ai-repository-run.request.v1","snapshot_id":"{{SnapshotId}}","payload_sha256":"{{new string('a',64)}}","timeout_seconds":60}"""), out var request));
        Assert.Equal(SnapshotId, request!.SnapshotId);
        Assert.False(LocalAiExtendedScopeRequestParser.TryRepositoryRun(
            Encoding.UTF8.GetBytes($$"""{"schema_version":"local-ai-repository-run.request.v1","snapshot_id":"{{SnapshotId}}","payload_sha256":"{{new string('a',64)}}","timeout_seconds":60,"repository_id":"{{RepositoryId}}"}"""), out _));
    }

    [Fact]
    public void ComparisonRunRequest_RequiresOnlyFrozenIdentityAndTimeout()
    {
        Assert.True(LocalAiExtendedScopeRequestParser.TryComparisonRun(
            Encoding.UTF8.GetBytes($$"""{"schema_version":"local-ai-comparison-run.request.v1","repository_id":"{{RepositoryId}}","comparison_id":"{{ComparisonId}}","timeout_seconds":600}"""), out var request));
        Assert.Equal(ComparisonId, request!.ComparisonId);
        Assert.False(LocalAiExtendedScopeRequestParser.TryComparisonRun(
            Encoding.UTF8.GetBytes($$"""{"schema_version":"local-ai-comparison-run.request.v1","repository_id":"{{RepositoryId}}","comparison_id":"{{ComparisonId}}","timeout_seconds":601}"""), out _));
        Assert.False(LocalAiExtendedScopeRequestParser.TryComparisonRun(
            Encoding.UTF8.GetBytes($$"""{"schema_version":"local-ai-comparison-run.request.v1","repository_id":"{{RepositoryId}}","comparison_id":"{{ComparisonId}}","timeout_seconds":60,"cohorts":[]}"""), out _));
    }
    [Fact]
    public void RepositoryPreview_RejectsUnknownSelectionFieldsAndMoreThanTwoHundredIds()
    {
        var valid=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new{schema_version="local-ai-repository-preview.request.v1",repository_id=RepositoryId,selection=new{kind="explicit",archive_scope="active_only",session_ids=new[]{ComparisonId}}});
        Assert.Equal(LocalAiPreviewParseStatus.Success,LocalAiExtendedScopeRequestParser.TryRepositoryPreview(valid,out _));
        var tooMany=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new{schema_version="local-ai-repository-preview.request.v1",repository_id=RepositoryId,selection=new{kind="explicit",archive_scope="active_only",session_ids=Enumerable.Range(0,201).Select(_=>Guid.CreateVersion7().ToString()).ToArray()}});
        Assert.Equal(LocalAiPreviewParseStatus.ScopeTooLarge,LocalAiExtendedScopeRequestParser.TryRepositoryPreview(tooMany,out _));
        var unknown=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new{schema_version="local-ai-repository-preview.request.v1",repository_id=RepositoryId,selection=new{kind="explicit",archive_scope="active_only",session_ids=Array.Empty<string>(),extra=true}});
        Assert.Equal(LocalAiPreviewParseStatus.InvalidRequest,LocalAiExtendedScopeRequestParser.TryRepositoryPreview(unknown,out _));
    }

    [Fact]
    public void GenericRunJson_ExposesRepositoryAndComparisonIdentity()
    {
        var run = new LocalAiRunStatusV1(SnapshotId, "running", "comparison", null, null, null,
            RepositoryId: RepositoryId, ComparisonId: ComparisonId);
        using var document = System.Text.Json.JsonDocument.Parse(LocalAiRoutesV1.SerializeRun(run));
        Assert.Equal(RepositoryId, document.RootElement.GetProperty("repository_id").GetString());
        Assert.Equal(ComparisonId, document.RootElement.GetProperty("comparison_id").GetString());
        Assert.DoesNotContain("payload", Encoding.UTF8.GetString(LocalAiRoutesV1.SerializeRun(run)), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderPrompt_ConstrainsRepositoryAndComparisonInterpretation()
    {
        const string structuredResultInstruction = """
Return raw JSON only: no Markdown, code fences, or surrounding prose.
Return one closed object with exactly these root fields: summary (string), findings (array), improvement_suggestions (array), limitations (array of strings).
Each findings item is one closed object with exactly: finding_id (non-blank string: not empty or whitespace-only), title (non-blank string: not empty or whitespace-only), explanation (non-blank string: not empty or whitespace-only), evidence_state (one of "supported" or "limited"), evidence_refs (array of 1 to 16 non-blank strings, each not empty or whitespace-only and exactly matching an identifier in the supplied evidence index), limitation (non-blank string: not empty or whitespace-only).
Each improvement_suggestions item is one closed object with exactly: suggestion_id (non-blank string: not empty or whitespace-only), target_kind (one of "instructions", "skill", "agent", "subagent_input", or "tool_configuration"), target_label (non-blank string: not empty or whitespace-only), concrete_change (non-blank string: not empty or whitespace-only), rationale (non-blank string: not empty or whitespace-only), expected_effect (non-blank string: not empty or whitespace-only), risks_or_limitations (non-blank string: not empty or whitespace-only), evidence_refs (array of 1 to 16 non-blank strings, each not empty or whitespace-only and exactly matching an identifier in the supplied evidence index).
Never include credentials, paths, prompts, tool payloads, scope, snapshot, or provider metadata.
""";
        var comparison=ProviderRequest("comparison");
        var comparisonPrompt=GitHubCopilotLocalAiProviderAdapterV1.BuildPrompt(comparison);
        Assert.Contains(structuredResultInstruction,comparisonPrompt.ReplaceLineEndings("\n"),StringComparison.Ordinal);
        Assert.Contains("stored observed differences",comparisonPrompt,StringComparison.Ordinal);
        Assert.Contains("Cite only the exact supplied evidence locations",comparisonPrompt,StringComparison.Ordinal);
        Assert.Contains("Do not state an effect verdict",comparisonPrompt,StringComparison.Ordinal);
        var repositoryPrompt=GitHubCopilotLocalAiProviderAdapterV1.BuildPrompt(ProviderRequest("repository_selection"));
        Assert.Contains(structuredResultInstruction,repositoryPrompt.ReplaceLineEndings("\n"),StringComparison.Ordinal);
        Assert.Contains("Do not explore",repositoryPrompt,StringComparison.Ordinal);
        Assert.Contains("never cite a bare node ID",repositoryPrompt,StringComparison.Ordinal);
        Assert.Contains("Do not state or promote AI output as a deterministic fact",repositoryPrompt,StringComparison.Ordinal);
        Assert.DoesNotContain("only canonical node IDs",repositoryPrompt,StringComparison.Ordinal);
        Assert.Contains("only canonical node IDs",GitHubCopilotLocalAiProviderAdapterV1.BuildPrompt(ProviderRequest("session")),StringComparison.Ordinal);
        Assert.Contains("only canonical node IDs",GitHubCopilotLocalAiProviderAdapterV1.BuildPrompt(ProviderRequest("node")),StringComparison.Ordinal);
    }

    private static LocalAiProviderRequestV1 ProviderRequest(string scope)
    {
        var snapshot=new CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1(SnapshotId,scope,null,null,
            scope=="comparison"?ComparisonId:RepositoryId,"revision","{}"u8.ToArray(),"{\"evidence_refs\":[]}"u8.ToArray(),new string('a',64),new HashSet<string>(),
            RepositoryId:RepositoryId,ComparisonId:scope=="comparison"?ComparisonId:null);
        var run=new LocalAiRunStatusV1(SnapshotId,"running",scope,null,null,null,RepositoryId:RepositoryId,ComparisonId:snapshot.ComparisonId);
        return new(snapshot,run,new LocalAiRawReadCapabilityV1([],static (_,_)=>ValueTask.FromResult(Array.Empty<byte>())),null,[]);
    }

    [Fact]
    public void RepositoryProjection_ComposesActualSelectedPayloadAndSessionOwnedLocations()
    {
        var projection=new CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1(SnapshotId,"session",RepositoryId,null,RepositoryId,"revision",
            "{\"session_facts\":{\"status\":\"completed\",\"citation_ref\":\"user-citation\",\"evidence_id\":\"user-evidence\"},\"sanitized_span_observations\":[{\"citation_ref\":\"node-11111111111111111111111111111111\",\"observation\":\"safe\"}],\"raw_content\":[{\"evidence_id\":\"raw-1\",\"citation_ref\":\"node-11111111111111111111111111111111\",\"state\":\"available\"}]}"u8.ToArray(),
            "{\"evidence_refs\":[\"node-11111111111111111111111111111111\"]}"u8.ToArray(),new string('a',64),new HashSet<string>{"node-11111111111111111111111111111111"});
        var payload=LocalAiRepositorySnapshotAdapterV1.ComposeSelectedPayload(RepositoryId,[projection],"{\"distribution\":{\"source_kinds\":[]}}"u8.ToArray());
        var text=Encoding.UTF8.GetString(payload);
        Assert.Contains("completed",text,StringComparison.Ordinal);
        Assert.Contains($"/sessions/{RepositoryId}?node=node-11111111111111111111111111111111",text,StringComparison.Ordinal);
        Assert.Contains($"\"evidence_id\":\"{RepositoryId}:raw-1\"",text,StringComparison.Ordinal);
        Assert.DoesNotContain("\"citation_ref\":\"node-11111111111111111111111111111111\"",text,StringComparison.Ordinal);
        Assert.Contains("\"citation_ref\":\"user-citation\"",text,StringComparison.Ordinal);
        Assert.Contains("\"evidence_id\":\"user-evidence\"",text,StringComparison.Ordinal);
        Assert.Contains("source_kinds",text,StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonProjection_PreservesSelectionFactReceiptAndResultBytes()
    {
        var bytes=LocalAiComparisonSnapshotAdapterV1.ComposePayloadForTest("selection"u8.ToArray(),"fact-a"u8.ToArray(),"receipt"u8.ToArray(),"result"u8.ToArray());
        var text=Encoding.UTF8.GetString(bytes);
        Assert.Contains(Convert.ToBase64String("selection"u8),text,StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String("fact-a"u8),text,StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String("receipt"u8),text,StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String("result"u8),text,StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepositoryRun_ProviderUnavailableCreatesNoRun()
    {
        var snapshot=new CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1(SnapshotId,"repository_selection",null,null,RepositoryId,"revision","{\"members\":[]}"u8.ToArray(),"{\"evidence_refs\":[]}"u8.ToArray(),new string('a',64),new HashSet<string>(),RepositoryId:RepositoryId,ExpiresAt:DateTimeOffset.MaxValue);
        var runs=new AcceptedRuns(snapshot);var application=new LocalAiAnalysisApplicationV1(_=>ValueTask.FromResult(false),new NoSnapshots(),runs,new NoProvider(),timeProvider:TimeProvider.System,repositories:new CurrentRepository(snapshot));
        var result=await application.StartRepositoryAsync(new(SnapshotId,new string('a',64),60),CancellationToken.None);
        Assert.Equal("provider_unavailable",result.ErrorCode);Assert.Equal(0,runs.CreateCount);
    }
    [Fact]
    public async Task RepositoryRun_PreservesPersistenceBusyFromRehydration()
    {
        var snapshot=new CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1(SnapshotId,"repository_selection",null,null,RepositoryId,"revision","{\"members\":[]}"u8.ToArray(),"{\"evidence_refs\":[]}"u8.ToArray(),new string('a',64),new HashSet<string>(),RepositoryId:RepositoryId,ExpiresAt:DateTimeOffset.MaxValue);
        var application=new LocalAiAnalysisApplicationV1(_=>ValueTask.FromResult(true),new NoSnapshots(),new AcceptedRuns(snapshot),new NoProvider(),repositories:new BusyRepository());
        var result=await application.StartRepositoryAsync(new(SnapshotId,new string('a',64),60),CancellationToken.None);
        Assert.Equal("persistence_busy",result.ErrorCode);
    }
    [Fact]
    public void ComparisonAdapter_ReadsFrozenStoreExactlyOnce()
    {
        var count=0;var adapter=new LocalAiComparisonSnapshotAdapterV1((repository,comparison,token)=>{count++;return new(CopilotAgentObservability.Persistence.Sqlite.LocalComparisonReadStatus.NotFound,null);});
        var exception=Assert.Throws<LocalAiScopeSnapshotException>(()=>adapter.Read(RepositoryId,ComparisonId,CancellationToken.None));
        Assert.Equal("comparison_not_found",exception.Error);Assert.Equal(1,count);
    }
    [Fact]
    public void RepositoryProjection_RejectsAggregateOverflow()
    {
        var oversized=Encoding.UTF8.GetBytes("{\"value\":\""+new string('x',CopilotAgentObservability.Persistence.Sqlite.LocalAi.LocalAiAnalysisStoreV1.MaximumSnapshotDocumentBytes)+"\"}");
        var projection=new CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1(SnapshotId,"session",RepositoryId,null,RepositoryId,"revision",oversized,"{\"evidence_refs\":[]}"u8.ToArray(),new string('a',64),new HashSet<string>());
        Assert.Throws<CopilotAgentObservability.Persistence.Sqlite.LocalAiScopeTooLargeException>(()=>LocalAiRepositorySnapshotAdapterV1.ComposeSelectedPayload(RepositoryId,[projection],"{}"u8.ToArray()));
    }
    [Fact]
    public async Task RepositoryPreview_PreservesPersistenceBusy()
    {
        var adapter=new LocalAiRepositorySnapshotAdapterV1(new BusyScope(),new NoSnapshots(),new NoHistoricalEvidence());
        var request=new LocalAiRepositoryPreviewRequestV1(RepositoryId,"explicit","active_only",[ComparisonId],null);
        var error=await Assert.ThrowsAsync<CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSnapshotException>(()=>adapter.PreviewAsync(request,CancellationToken.None).AsTask());
        Assert.Equal("persistence_busy",error.ErrorCode);
    }
    [Fact]
    public void RepositoryMembership_ReassignmentInvalidatesFrozenPreview()
    {
        var row=new CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSessionSnapshot(RepositoryId,new CopilotAgentObservability.Persistence.Sqlite.LocalUnavailableRepositorySessionSnapshotRow(RepositoryId),7,CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeAssignmentState.Assigned,CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeAssignmentAuthority.Manual,ComparisonId,[],true,false,false,CopilotAgentObservability.Persistence.Sqlite.LocalArchiveState.Active,3,true,null,5);
        using var member=System.Text.Json.JsonDocument.Parse("{\"assignment_revision\":7,\"session_archive_revision\":3,\"repository_archive_revision\":5}");
        Assert.False(LocalAiRepositorySnapshotAdapterV1.MatchesFrozenMembership(row,5,member.RootElement));
        Assert.True(LocalAiRepositorySnapshotAdapterV1.MatchesFrozenMembership(row with{IsRequestedScopeMember=true},5,member.RootElement));
        Assert.False(LocalAiRepositorySnapshotAdapterV1.MatchesFrozenMembership(row with{IsRequestedScopeMember=true,AssignmentRevision=8},5,member.RootElement));
    }
    [Fact]
    public void RepositoryPreview_ArchiveScopeUsesSessionFirstAuthority()
    {
        var row=new CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSessionSnapshot(RepositoryId,new CopilotAgentObservability.Persistence.Sqlite.LocalUnavailableRepositorySessionSnapshotRow(RepositoryId),1,CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeAssignmentState.Assigned,CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeAssignmentAuthority.Manual,ComparisonId,[],true,false,true,CopilotAgentObservability.Persistence.Sqlite.LocalArchiveState.Archived,2,false,"session_archived",3);
        Assert.Equal("session_archived",LocalAiRepositorySnapshotAdapterV1.PreviewExclusion(row,"active_only"));
        Assert.Null(LocalAiRepositorySnapshotAdapterV1.PreviewExclusion(row,"include_archived"));
        Assert.Equal("repository_archived",LocalAiRepositorySnapshotAdapterV1.PreviewExclusion(row with{ArchiveState=CopilotAgentObservability.Persistence.Sqlite.LocalArchiveState.Active,ArchiveExclusionReason="repository_archived"},"active_only"));
    }

    [Theory]
    [InlineData("available", "available")]
    [InlineData("not_captured", "not_captured")]
    [InlineData("expired", "expired_pending_deletion")]
    [InlineData(null, "not_captured")]
    public void RepositoryPreview_ContentStateUsesProjectedLocatorAuthority(string? locatorState, string expected)
    {
        IReadOnlyDictionary<string, LocalAiRawEvidenceV1> evidence = locatorState is null
            ? new Dictionary<string, LocalAiRawEvidenceV1>()
            : new Dictionary<string, LocalAiRawEvidenceV1>
            {
                ["raw-1"] = new("raw-1", "node-11111111111111111111111111111111",
                    new("node-11111111111111111111111111111111", "instruction", locatorState)),
            };

        Assert.Equal(expected, LocalAiRepositorySnapshotAdapterV1.PreviewContentState(evidence));
    }

    private sealed class CurrentRepository(LocalAiSnapshotProjectionV1 snapshot):ILocalAiRepositorySnapshotAdapterV1
    {public ValueTask<LocalAiRepositoryPreviewResultV1> PreviewAsync(LocalAiRepositoryPreviewRequestV1 request,CancellationToken token)=>throw new NotSupportedException();public ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 value,CancellationToken token)=>ValueTask.FromResult(true);public ValueTask<LocalAiSnapshotProjectionV1?> RehydrateCurrentAsync(LocalAiSnapshotProjectionV1 value,CancellationToken token)=>ValueTask.FromResult<LocalAiSnapshotProjectionV1?>(snapshot);}
    private sealed class BusyRepository:ILocalAiRepositorySnapshotAdapterV1
    {public ValueTask<LocalAiRepositoryPreviewResultV1> PreviewAsync(LocalAiRepositoryPreviewRequestV1 request,CancellationToken token)=>throw new NotSupportedException();public ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 value,CancellationToken token)=>throw new NotSupportedException();public ValueTask<LocalAiSnapshotProjectionV1?> RehydrateCurrentAsync(LocalAiSnapshotProjectionV1 value,CancellationToken token)=>ValueTask.FromException<LocalAiSnapshotProjectionV1?>(new CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSnapshotException(CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSnapshotError.PersistenceBusy,"persistence_busy",new Exception()));}
    private sealed class AcceptedRuns(LocalAiSnapshotProjectionV1 snapshot):ILocalAiRunRepositoryV1,ILocalAiAcceptedSnapshotRepositoryV1
    {public int CreateCount{get;private set;}public void StoreAccepted(LocalAiSnapshotProjectionV1 value){}public LocalAiSnapshotProjectionV1? ReadAccepted(string id)=>snapshot;public LocalAiRunStatusV1 Create(LocalAiSnapshotProjectionV1 value,int timeout){CreateCount++;throw new InvalidOperationException();}public void Start(string id){}public LocalAiRunStatusV1 Complete(string id,LocalAiProviderOutcomeV1 o,DateTimeOffset at)=>throw new NotSupportedException();public LocalAiRunStatusV1 Fail(string id,string code)=>throw new NotSupportedException();public LocalAiRunStatusV1 Read(string id)=>throw new NotSupportedException();public bool Cancel(string id)=>false;public LocalAiReportPageResponseV1 Reports(string id,int? limit,string? cursor,string hash)=>throw new NotSupportedException();}
    private sealed class NoSnapshots:CopilotAgentObservability.Persistence.Sqlite.ILocalAiSnapshotProjectionServiceV1
    {public ValueTask<CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1> ReadSessionAsync(string id,CancellationToken token)=>throw new NotSupportedException();public ValueTask<CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1> ReadNodeAsync(string id,string node,CancellationToken token)=>throw new NotSupportedException();public ValueTask<bool> IsCurrentAsync(CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1 value,CancellationToken token)=>ValueTask.FromResult(true);}
    private sealed class NoProvider:ILocalAiProviderAdapterV1{public ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request,CancellationToken token)=>throw new NotSupportedException();}
    private sealed class BusyScope:CopilotAgentObservability.Persistence.Sqlite.ILocalRepositoryScopeSnapshotService
    {public ValueTask<CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSnapshot> ReadAsync(CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeRequest request,CancellationToken token)=>ValueTask.FromException<CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSnapshot>(new CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSnapshotException(CopilotAgentObservability.Persistence.Sqlite.LocalRepositoryScopeSnapshotError.PersistenceBusy,"persistence_busy",new Exception()));}
    private sealed class NoHistoricalEvidence:CopilotAgentObservability.LocalMonitor.Analysis.IHistoricalEvidenceSnapshotSourceV1
    {public ValueTask<CopilotAgentObservability.LocalMonitor.Analysis.IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(CopilotAgentObservability.LocalMonitor.Analysis.HistoricalEvidenceSelectionV1 selection,CancellationToken token)=>throw new NotSupportedException();}

    public enum ProviderBehavior { Complete, Partial, Exception, Timeout, Cancellation }

    private sealed class ProviderClient(
        List<string> events,
        ProviderBehavior behavior,
        bool failDelete = false) : IOwnedCopilotClientV1
    {
        public int DeleteCalls { get; private set; }
        public string? DeletedSessionId { get; private set; }
        public bool DeleteTokenCanBeCanceled { get; private set; }
        public bool SessionMarkerPresent { get; private set; } = true;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CopilotRuntimeStatusObservationV1?>(null);
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) =>
            Task.FromResult<IOwnedCopilotSessionV1>(new ProviderSession(events, behavior));
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            DeletedSessionId = sessionId;
            DeleteTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            events.Add("session.delete");
            if (failDelete) return Task.FromException(new InvalidOperationException("synthetic_cleanup_failure"));
            SessionMarkerPresent = false;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { events.Add("client.dispose"); return ValueTask.CompletedTask; }
    }

    private sealed class ProviderSession(List<string> events, ProviderBehavior behavior) : IOwnedCopilotSessionV1
    {
        public string SessionId => "synthetic-session";
        public Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>(null);
        public Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<string?> SendAndReadFinalContentAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken) =>
            behavior switch
            {
                ProviderBehavior.Complete => Task.FromResult<string?>("synthetic_complete_marker"),
                ProviderBehavior.Partial => Task.FromResult<string?>(null),
                ProviderBehavior.Exception => Task.FromException<string?>(new InvalidOperationException("synthetic_provider_failure")),
                ProviderBehavior.Timeout => Task.FromException<string?>(new TimeoutException("synthetic_timeout")),
                ProviderBehavior.Cancellation => Task.FromException<string?>(new OperationCanceledException(cancellationToken)),
                _ => throw new InvalidOperationException(),
            };
        public ValueTask DisposeAsync() { events.Add("session.dispose"); return ValueTask.CompletedTask; }
    }
}

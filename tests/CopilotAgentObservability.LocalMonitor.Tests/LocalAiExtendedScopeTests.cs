using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalAi;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalAiExtendedScopeTests
{
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000001";
    private const string ComparisonId = "018f0000-0000-7000-8000-000000000002";
    private const string SnapshotId = "018f0000-0000-7000-8000-000000000003";

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
        var comparison=ProviderRequest("comparison");
        var comparisonPrompt=GitHubCopilotLocalAiProviderAdapterV1.BuildPrompt(comparison);
        Assert.Contains("stored observed differences",comparisonPrompt,StringComparison.Ordinal);
        Assert.Contains("Do not state an effect verdict",comparisonPrompt,StringComparison.Ordinal);
        var repositoryPrompt=GitHubCopilotLocalAiProviderAdapterV1.BuildPrompt(ProviderRequest("repository_selection"));
        Assert.Contains("Do not explore",repositoryPrompt,StringComparison.Ordinal);
    }

    private static LocalAiProviderRequestV1 ProviderRequest(string scope)
    {
        var snapshot=new CopilotAgentObservability.Persistence.Sqlite.LocalAiSnapshotProjectionV1(SnapshotId,scope,null,null,
            scope=="comparison"?ComparisonId:RepositoryId,"revision","{}"u8.ToArray(),"{\"evidence_refs\":[]}"u8.ToArray(),new string('a',64),new HashSet<string>(),
            RepositoryId:RepositoryId,ComparisonId:scope=="comparison"?ComparisonId:null);
        var run=new LocalAiRunStatusV1(SnapshotId,"running",scope,null,null,null,RepositoryId:RepositoryId,ComparisonId:snapshot.ComparisonId);
        return new(snapshot,run,new LocalAiRawReadCapabilityV1([],static (_,_)=>ValueTask.FromResult(Array.Empty<byte>())),null,[]);
    }
}

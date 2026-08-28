using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text;
using System.Reflection;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProjectionGenerationTests_CurrentInvocationMatrix
{
    [Fact]
    public void GlobalWorkspaceRefreshPinsOneRegistryGenerationAcrossSessionBatches()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        for (var index = 0; index < 201; index++)
            fixture.SeedSdkOnly($"batch-{index:D3}", $"skill-{index:D3}");
        var before = (fixture.RegistryCaptureCount, fixture.RegistryLeaseCount,
            fixture.RegistryVerifyCount, fixture.RegistryTupleAuthorizationCount);

        fixture.RefreshWorkspace();

        Assert.Equal(1, fixture.RegistryCaptureCount - before.RegistryCaptureCount);
        Assert.Equal(1, fixture.RegistryLeaseCount - before.RegistryLeaseCount);
        Assert.Equal(1, fixture.RegistryVerifyCount - before.RegistryVerifyCount);
        Assert.Equal(2, fixture.RegistryTupleAuthorizationCount - before.RegistryTupleAuthorizationCount);
    }

    [Fact]
    public async Task DynamicRegistryRunBoundSkillSurvivesRealBackupInspectAndRestore()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("backup", "dynamic-skill");
        fixture.RefreshWorkspace();
        var archive = fixture.File("dynamic-registry.zip");
        var restored = fixture.File("dynamic-registry-restored.sqlite");
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T01:00:00Z"));
        string? lastCheckpoint = null;
        var service = new SqliteRuntimeBackupService(clock, value => lastCheckpoint = value);
        service.ConfigureSkillRegistryAuthority(fixture.RegistryAuthority);

        var created = service.CreateAndPublish(fixture.DatabasePath, archive);
        var inspected = service.Inspect(archive);
        var restore = service.Restore(archive, restored, new RuntimeRestoreOptions());

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.True(restore.Success, $"{restore.ErrorCode}:{lastCheckpoint}");
        var detailService = new SqliteLocalRepositoryScopeSnapshotService(
            restored,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: fixture.RegistryAuthority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(
                registryAuthority: fixture.RegistryAuthority,
                timeProvider: clock),
            skillRegistryAuthority: fixture.RegistryAuthority);
        var detail = await detailService.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Summary, fixture.SessionId("backup")),
            CancellationToken.None);
        var execution = Assert.Single(detail.Detail.Executions);
        var timeline = await detailService.ReadDetailAsync(
            new(LocalRepositorySessionDetailRequestKind.Timeline, fixture.SessionId("backup"), ExecutionId: execution.ExecutionId),
            CancellationToken.None);
        Assert.Single(timeline.Detail.Nodes, node => node.Kind == "skill" && node.NameText == "dynamic-skill");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualRegistryPublicationCommitsOrRollsBackGenerationSensitiveV5SkillRows(bool injectFailure)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("registry-publication", "generation-sensitive-skill");

        var result = fixture.PublishActualRegistryGeneration("registry-publication", injectFailure);

        Assert.Equal(0, result.BeforeRows);
        Assert.Equal(injectFailure ? 0 : 1, result.AfterRows);
        Assert.Equal(injectFailure, result.OldPointerStillCurrent);
    }
    [Fact]
    public void PersistedSqliteMatrix_CanonicalSdkReaderHasFixedPreparedStatementSequenceForOneTwoHundredFiftySixAndTenThousandClaims()
    {
        int? expectedExecutions = null;
        foreach (var count in new[] { 1, 256, 10_000 })
        {
            using var fixture = new CurrentInvocationProjectionFixture();
            fixture.SeedSdkClaims("fixed-cardinality", count);

            var observed = fixture.ReadAllObserved();

            Assert.Equal(count, observed.Result[fixture.SessionId("fixed-cardinality")].InvocationCount);
            Assert.True(observed.CommandExecutions > 0);
            expectedExecutions ??= observed.CommandExecutions;
            Assert.Equal(expectedExecutions, observed.CommandExecutions);
            Assert.Equal(1, fixture.RegistryCaptureCount);
            Assert.Equal(1, fixture.RegistryLeaseCount);
            Assert.Equal(1, fixture.RegistryVerifyCount);
            Assert.Equal(1, fixture.RegistryTupleAuthorizationCount);
        }
    }

    [Fact]
    public void SdkPublicationParticipantRefreshesCanonicalV5NodesInsideOwningCommitAndRollback()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("publication", "baseline-skill");
        fixture.RefreshWorkspace();
        Assert.Equal(1, fixture.CountDetailSkillNodes("publication"));

        Assert.Equal((Inside: 2, Persisted: 1),
            fixture.PublishSdkClaim("publication", "rolled-back-skill", commit: false));
        Assert.Equal((Inside: 2, Persisted: 2),
            fixture.PublishSdkClaim("publication", "committed-skill", commit: true));
    }

    [Fact]
    public void PersistedSqliteMatrix_UsesOneCurrentValidAuthority()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedOtelOnly("otel-only", "otel-skill", "aa", "01");
        fixture.SeedSdkOnly("sdk-one", "sdk-skill");
        fixture.SeedSdkOnly("sdk-two", "sdk-skill");
        fixture.SeedExactPair("exact-pair", "paired-skill", "bb", "02", duplicateObservations: 2);
        fixture.SeedMismatchedPair("mismatch", "pending-skill", "cc", "03", "dd", "04");
        fixture.SeedSdkOnly("stale", "stale-skill", registryAccepted: false);
        fixture.SeedSdkOnly("invalid", "invalid-skill", state: "malformed", reason: "duplicate_property");
        fixture.SeedSdkOnly("expired", "expired-skill", expired: true);

        var results = fixture.ReadAll();

        fixture.AssertCurrent(results, "otel-only", 1, "otel-skill");
        fixture.AssertCurrent(results, "sdk-one", 1, "sdk-skill");
        fixture.AssertCurrent(results, "sdk-two", 1, "sdk-skill");
        var otelOnly = Assert.Single(results[fixture.SessionId("otel-only")].Invocations);
        Assert.NotNull(otelOnly.OtelSourceIdentity);
        Assert.Null(otelOnly.SdkSourceIdentity);
        Assert.Equal("session_run", otelOnly.ExecutionSourceKind);
        Assert.NotNull(otelOnly.ExecutionSourceIdentity);
        Assert.NotNull(otelOnly.OtelCarrierEventId);
        var sdkOnly = Assert.Single(results[fixture.SessionId("sdk-one")].Invocations);
        Assert.Null(sdkOnly.OtelSourceIdentity);
        Assert.NotNull(sdkOnly.SdkSourceIdentity);
        Assert.Equal("session_run", sdkOnly.ExecutionSourceKind);
        Assert.NotNull(sdkOnly.ExecutionSourceIdentity);
        Assert.NotNull(sdkOnly.SdkCarrierEventId);
        fixture.AssertCurrent(results, "exact-pair", 1, "paired-skill");
        var exact = Assert.Single(results[fixture.SessionId("exact-pair")].Invocations);
        Assert.NotNull(exact.OtelSourceIdentity);
        Assert.NotNull(exact.SdkSourceIdentity);
        Assert.Equal("session_run", exact.ExecutionSourceKind);
        Assert.NotNull(exact.ExecutionSourceIdentity);
        Assert.NotNull(exact.OtelCarrierEventId);
        Assert.NotNull(exact.SdkCarrierEventId);
        Assert.Equal("bb".PadRight(32, 'b'), exact.ProducerTraceId);
        Assert.Equal("02".PadRight(16, '0'), exact.ProducerSpanId);
        fixture.AssertPending(results, "mismatch");
        fixture.AssertAbsent(results, "stale", "invalid", "expired");
    }

    [Fact]
    public void PersistedSqliteMatrix_TwoIdLessSdkClaimsCountTwice()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("same-session", "first-skill");
        fixture.SeedSdkOnly("same-session", "second-skill");

        var session = fixture.Read("same-session");

        Assert.Equal("current", session.State);
        Assert.Equal(2, session.InvocationCount);
        Assert.Equal(2, session.Invocations.Count);
        Assert.All(session.Invocations, invocation =>
        {
            Assert.Null(invocation.OtelSourceIdentity);
            Assert.NotNull(invocation.SdkSourceIdentity);
        });
        Assert.Equal(["first-skill", "second-skill"], session.SearchFacts.Select(static fact => fact.SkillName).Order());
    }

    [Fact]
    public void PersistedSqliteMatrix_RegistryUnavailableFailsClosed()
    {
        using var fixture = new CurrentInvocationProjectionFixture(registryAvailable: false);
        fixture.SeedSdkOnly("unavailable", "unavailable-skill");

        var session = fixture.Read("unavailable");

        Assert.Equal("unavailable", session.State);
        Assert.Null(session.InvocationCount);
        Assert.Empty(session.SearchFacts);
    }

    [Fact]
    public void PersistedSqliteMatrix_WorkspaceQSummaryAndHasSkillStayConsistent()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("admitted", "Needle-Skill");
        fixture.SeedMismatchedPair("pending", "Needle-Skill", "ee", "05", "ff", "06");

        fixture.AssertSdkAuthorized("pending");
        fixture.AssertPending(fixture.ReadAll(), "pending");
        fixture.RefreshWorkspace();

        fixture.AssertWorkspaceSkill("admitted", "recorded", 1, ["needle-skill"]);
        fixture.AssertWorkspaceSkill("pending", "certification_pending", null, []);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_DetailNodesUseOnlyCanonicalExecutionProof()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedOtelOnly("otel", "otel-skill", "21", "31");
        fixture.SeedSdkOnly("sdk", "sdk-skill");
        fixture.SeedExactPair("pair", "pair-skill", "22", "32", duplicateObservations: 2);
        fixture.SeedMismatchedPair("pending", "pending-skill", "23", "33", "24", "34");
        fixture.RefreshWorkspace();

        Assert.Equal(1, fixture.CountDetailSkillNodes("otel"));
        Assert.Equal(1, fixture.CountDetailSkillNodes("sdk"));
        Assert.Equal(1, fixture.CountDetailSkillNodes("pair"));
        Assert.Equal(2, fixture.CountDetailSkillNodes("pending"));
        Assert.Equal(1, fixture.ExecutionSkillCount("sdk"));
        Assert.Equal(["event"], fixture.RawSkillEventKinds("sdk"));
        var pairSummary = await fixture.ReadDetailAsync("pair", LocalRepositorySessionDetailRequestKind.Summary);
        var pairExecution = Assert.Single(pairSummary.Detail.Executions);
        var pairTimeline = await fixture.ReadDetailAsync("pair", LocalRepositorySessionDetailRequestKind.Timeline, pairExecution.ExecutionId);
        var pairRoot = Assert.Single(pairTimeline.Detail.Nodes, static node => node.SourceKind == "execution_root");
        Assert.Equal(pairExecution.ChildCount, pairRoot.ChildCount);
        Assert.Equal(fixture.PersistedRootChildCount("pair"), pairRoot.ChildCount);
    }

    [Fact]
    public void PersistedSqliteMatrix_SdkSourceParentIsExplicitOnlyWithinExactExecution()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkWithExplicitParent("sdk-parent", "sdk-skill");
        fixture.RefreshWorkspace();

        Assert.Equal(["explicit"], fixture.SkillRelationshipAuthorities("sdk-parent"));
        Assert.Equal(["explicit"], fixture.SkillParentEdgeAuthorities("sdk-parent"));
    }

    [Fact]
    public void PersistedSqliteMatrix_SdkWithoutParentProofUsesExactExecutionRootWithoutPhantomGroup()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("sdk-root", "sdk-skill");
        fixture.RefreshWorkspace();

        Assert.Equal(["exact"], fixture.SkillRelationshipAuthorities("sdk-root"));
        Assert.Equal(0, fixture.UnknownRelationshipGroupCount("sdk-root"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("cross-run")]
    [InlineData("cross-adapter")]
    public void PersistedSqliteMatrix_InvalidSdkExplicitParentUsesUnknownGroup(string defect)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkWithExplicitParent("sdk-parent", "sdk-skill");
        fixture.CorruptExplicitParent("sdk-parent", defect);
        fixture.RefreshWorkspace();

        Assert.Equal(["unknown"], fixture.SkillRelationshipAuthorities("sdk-parent"));
        Assert.Empty(fixture.SkillParentEdgeAuthorities("sdk-parent"));
    }

    [Fact]
    public void PersistedSqliteMatrix_NodeBoundaryIncludesExecutionRootUnknownGroupAndCanonicalSkill()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkWithExplicitParent("boundary", "sdk-skill");
        fixture.CorruptExplicitParent("boundary", "missing");
        fixture.RefreshWorkspace();
        fixture.AddGenericEvents("boundary", 4096 - fixture.DetailNodeCount("boundary"));

        fixture.RefreshWorkspaceProjection("boundary");
        Assert.Equal(4096, fixture.DetailNodeCount("boundary"));
        Assert.Equal(1, fixture.UnknownRelationshipGroupCount("boundary"));
        Assert.Equal(1, fixture.CountDetailSkillNodes("boundary"));

        fixture.AddGenericEvents("boundary", 1);
        fixture.RefreshWorkspaceProjection("boundary");
        Assert.Equal(4097, fixture.DetailNodeCount("boundary"));
    }

    [Fact]
    public async Task PersistedSqliteMatrix_ProductionCollectionQOnlyIncludesAdmittedAndExcludesPending()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("admitted", "Needle-Skill");
        fixture.SeedMismatchedPair("pending", "Needle-Skill", "11", "12", "13", "14");

        using var filtered = JsonDocument.Parse(await fixture.SerializeCollectionAsync(q: "needle-skill", hasSkill: null));
        AssertFilteredAdmittedSummary(fixture, filtered);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_ProductionCollectionHasSkillOnlyIncludesAdmittedAndExcludesPending()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("admitted", "Needle-Skill");
        fixture.SeedMismatchedPair("pending", "Needle-Skill", "11", "12", "13", "14");

        using var filtered = JsonDocument.Parse(await fixture.SerializeCollectionAsync(q: null, hasSkill: true));
        AssertFilteredAdmittedSummary(fixture, filtered);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_ProductionCollectionSerializesPendingSummaryWithoutPromotingIt()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("admitted", "Needle-Skill");
        fixture.SeedMismatchedPair("pending", "Needle-Skill", "11", "12", "13", "14");

        using var unfiltered = JsonDocument.Parse(await fixture.SerializeCollectionAsync(q: null, hasSkill: null));
        var pending = Assert.Single(unfiltered.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("session_id").GetString() == fixture.SessionId("pending"));
        var pendingSkill = pending.GetProperty("summary").GetProperty("skill");
        Assert.Equal("certification_pending", pendingSkill.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, pendingSkill.GetProperty("count").ValueKind);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_ProductionCollectionReevaluatesSdkExpiryWithoutProjectionRefresh()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("crossing", "Needle-Skill");
        fixture.RefreshWorkspace();

        fixture.AdvancePastLatestSdkExpiry("crossing");

        using var unfiltered = JsonDocument.Parse(await fixture.SerializeCollectionAsync(q: null, hasSkill: null, refresh: false));
        var item = Assert.Single(unfiltered.RootElement.GetProperty("items").EnumerateArray());
        var skill = item.GetProperty("summary").GetProperty("skill");
        Assert.Equal("not_observed", skill.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, skill.GetProperty("count").ValueKind);
        using var q = JsonDocument.Parse(await fixture.SerializeCollectionAsync(q: "needle-skill", hasSkill: null, refresh: false));
        Assert.Empty(q.RootElement.GetProperty("items").EnumerateArray());
        using var hasSkill = JsonDocument.Parse(await fixture.SerializeCollectionAsync(q: null, hasSkill: true, refresh: false));
        Assert.Empty(hasSkill.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PersistedSqliteMatrix_DetailRevisionAndExecutionFactsReevaluateSdkExpiryWithoutRefresh()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("crossing", "Needle-Skill");
        fixture.RefreshWorkspace();

        var before = await fixture.ReadDetailAsync("crossing", LocalRepositorySessionDetailRequestKind.Summary);
        var executionId = Assert.Single(before.Detail.Executions).ExecutionId;
        var beforeTimeline = await fixture.ReadDetailAsync(
            "crossing", LocalRepositorySessionDetailRequestKind.Timeline, executionId);
        Assert.Equal(1, Assert.Single(before.Detail.Executions).Activity.Skill.Value);
        Assert.Contains(beforeTimeline.Detail.Nodes, static node => node.SourceKind == "skill_invocation");

        fixture.AdvancePastLatestSdkExpiry("crossing");

        var after = await fixture.ReadDetailAsync("crossing", LocalRepositorySessionDetailRequestKind.Summary);
        var afterTimeline = await fixture.ReadDetailAsync(
            "crossing", LocalRepositorySessionDetailRequestKind.Timeline, executionId);
        Assert.Equal("not_observed", Assert.Single(after.Detail.Executions).Activity.Skill.State);
        Assert.Null(Assert.Single(after.Detail.Executions).Activity.Skill.Value);
        Assert.DoesNotContain(afterTimeline.Detail.Nodes, static node => node.SourceKind == "skill_invocation");
        Assert.NotEqual(before.WorkspaceRevision, after.WorkspaceRevision);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_MismatchedPairAdmitsExactOtelFactsWhenSdkExpiresWithoutRefresh()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("crossing", "Needle-Skill", "31", "41", "32", "42");
        fixture.RefreshWorkspace();

        var before = await fixture.ReadDetailAsync("crossing", LocalRepositorySessionDetailRequestKind.Summary);
        Assert.Equal("certification_pending", Assert.IsType<LocalWorkspaceProjectionRow>(before.Session.Session).Activity.Skill.State);
        using (var beforeQ = JsonDocument.Parse(await fixture.SerializeCollectionAsync("needle-skill", null, refresh: false)))
            Assert.Empty(beforeQ.RootElement.GetProperty("items").EnumerateArray());

        fixture.AdvancePastLatestSdkExpiry("crossing");

        using (var afterQ = JsonDocument.Parse(await fixture.SerializeCollectionAsync("needle-skill", null, refresh: false)))
            Assert.Single(afterQ.RootElement.GetProperty("items").EnumerateArray());
        using (var afterHasSkill = JsonDocument.Parse(await fixture.SerializeCollectionAsync(null, true, refresh: false)))
            Assert.Single(afterHasSkill.RootElement.GetProperty("items").EnumerateArray());
        var after = await fixture.ReadDetailAsync("crossing", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(after.Detail.Executions);
        Assert.Equal(1, execution.Activity.Skill.Value);
        var timeline = await fixture.ReadDetailAsync("crossing", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId);
        var node = Assert.Single(timeline.Detail.Nodes, static value => value.SourceKind == "skill_invocation");
        var source = Assert.Single(node.SourceReferences!);
        Assert.Equal("skill_claim", source.SourceKind);
        Assert.NotNull(source.SourceIdentity);
        Assert.Equal(node.TraceId, source.TraceId);
        Assert.Equal(node.SpanId, source.SpanId);
        var root = Assert.Single(timeline.Detail.Nodes, static value => value.SourceKind == "execution_root");
        Assert.Equal(execution.ChildCount, root.ChildCount);
        Assert.Equal(LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", node.SourceIdentity), node.NodeId);
        Assert.Equal("Needle-Skill", node.NameText);
        Assert.NotNull(node.TraceId);
        Assert.NotNull(node.SpanId);
        var inspected = await fixture.ReadDetailAsync("crossing", LocalRepositorySessionDetailRequestKind.Node, nodeId: node.NodeId);
        Assert.Contains(inspected.Detail.Nodes, value => value.NodeId == node.NodeId && value.ParentNodeId is not null);
        var children = await fixture.ReadDetailAsync("crossing", LocalRepositorySessionDetailRequestKind.Timeline,
            execution.ExecutionId, parentNodeId: node.NodeId);
        Assert.DoesNotContain(children.Detail.Nodes, value => value.ParentNodeId == node.NodeId);
        Assert.NotEqual(before.WorkspaceRevision, after.WorkspaceRevision);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_DetailKeepsPreviouslyExactSkillNodeCertificationPending()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("pending", "pending-skill");
        fixture.RefreshWorkspace();
        Assert.Equal(1, fixture.CountDetailSkillNodes("pending"));
        var current = await fixture.ReadDetailAsync("pending", LocalRepositorySessionDetailRequestKind.Summary);
        fixture.SeedOtelOnly("pending", "pending-skill", "51", "61");
        fixture.RefreshWorkspaceProjection("pending");

        var summary = await fixture.ReadDetailAsync("pending", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(summary.Detail.Executions);
        Assert.Equal("certification_pending", execution.Activity.Skill.State);
        var timeline = await fixture.ReadDetailAsync(
            "pending", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId);
        var pendingNodes = timeline.Detail.Nodes.Where(static node => node.SourceKind == "skill_invocation").ToArray();
        Assert.Equal(2, pendingNodes.Length);
        Assert.All(pendingNodes, node =>
        {
            var metadata = Assert.IsType<LocalWorkspaceSkillMetadataDetail>(node.SkillMetadata);
            Assert.Equal("certification_pending", metadata.CurrentValidState);
            Assert.Equal("unavailable", metadata.InventoryReferenceState);
        });
        Assert.Equal(2, fixture.CountDetailSkillNodes("pending"));
        Assert.NotEqual(current.WorkspaceRevision, summary.WorkspaceRevision);

        fixture.RefreshWorkspaceProjection("pending");
        var rerun = await fixture.ReadDetailAsync("pending", LocalRepositorySessionDetailRequestKind.Summary);
        Assert.Equal(summary.WorkspaceRevision, rerun.WorkspaceRevision);
        Assert.Equal(2, fixture.CountDetailSkillNodes("pending"));

        fixture.ValidateWorkspaceBackup();
        fixture.MutatePendingSkillMetadata("UPDATE local_workspace_skill_metadata SET current_valid_state='current';");
        Assert.Equal("local_workspace_projection_backup_invalid",
            Assert.Throws<InvalidOperationException>(fixture.ValidateWorkspaceBackup).Message);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_FirstRefreshMaterializesFreshMismatchedSkillClaimsAsPendingPerArmFacts()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("fresh-pending", "pending-skill", "91", "a1", "92", "a2");

        fixture.RefreshWorkspace();

        var summary = await fixture.ReadDetailAsync("fresh-pending", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(summary.Detail.Executions);
        Assert.Equal("certification_pending", execution.Activity.Skill.State);
        Assert.Null(execution.Activity.Skill.Value);
        var timeline = await fixture.ReadDetailAsync(
            "fresh-pending", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId);
        var nodes = timeline.Detail.Nodes
            .Where(static node => node.SourceKind == "skill_invocation")
            .OrderBy(static node => node.SourceIdentity, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.All(nodes, node =>
        {
            Assert.Equal(execution.ExecutionId, node.ExecutionId);
            Assert.Equal(LocalWorkspaceProjectionStore.StableNodeId("skill_invocation", node.SourceIdentity), node.NodeId);
            Assert.Equal("certification_pending", Assert.IsType<LocalWorkspaceSkillMetadataDetail>(node.SkillMetadata).CurrentValidState);
            var reference = Assert.Single(node.SourceReferences!);
            Assert.Equal("skill_claim", reference.SourceKind);
            Assert.Equal(node.EventId, reference.EventId);
        });
        Assert.Single(nodes, static node => node.TraceId is not null && node.SpanId is not null
            && Assert.Single(node.SourceReferences!).TraceId == node.TraceId
            && Assert.Single(node.SourceReferences!).SpanId == node.SpanId);
        Assert.Single(nodes, static node => Assert.Single(node.SourceReferences!).TraceId is null
            && Assert.Single(node.SourceReferences!).SpanId is null);

        fixture.RefreshWorkspaceProjection("fresh-pending");
        var rerun = await fixture.ReadDetailAsync("fresh-pending", LocalRepositorySessionDetailRequestKind.Summary);
        Assert.Equal(summary.WorkspaceRevision, rerun.WorkspaceRevision);
        fixture.ValidateWorkspaceBackup();
    }

    [Fact]
    public async Task PersistedPendingSkillNodesAreNotDoubleCountedAsSynthesizedChildren()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("pending-count", "pending-skill", "a1", "b1", "a2", "b2");
        fixture.RefreshWorkspace();

        var summary = await fixture.ReadDetailAsync("pending-count", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(summary.Detail.Executions);
        var root = Assert.Single(summary.Detail.Nodes, static node => node.SourceKind == "execution_root");
        Assert.Equal(fixture.PersistedRootChildCount("pending-count"), execution.ChildCount);
        Assert.Equal(fixture.PersistedRootChildCount("pending-count"), root.ChildCount);
    }

    [Fact]
    public async Task PendingSkillActivityIsScopedToItsExactExecutionSourceIdentity()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("pending-scope", "pending-skill", "c1", "d1", "c2", "d2");
        fixture.AddUnrelatedExecution("pending-scope");
        fixture.RefreshWorkspaceProjection("pending-scope");

        var summary = await fixture.ReadDetailAsync("pending-scope", LocalRepositorySessionDetailRequestKind.Summary);
        Assert.Equal(2, summary.Detail.Executions.Count);
        Assert.Single(summary.Detail.Executions, static execution => execution.Activity.Skill.State == "certification_pending");
        Assert.Single(summary.Detail.Executions, static execution => execution.Activity.Skill.State == "not_observed");
    }

    [Fact]
    public async Task PendingRefreshRemovesOnlyTheStaleExactArmAndKeepsTheCurrentArm()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("pending-arms", "pending-skill", "e1", "f1", "e2", "f2");
        fixture.RefreshWorkspace();
        Assert.Equal(2, fixture.CountDetailSkillNodes("pending-arms"));

        fixture.AdvancePastLatestSdkExpiry("pending-arms");
        fixture.RefreshWorkspaceProjection("pending-arms");

        var summary = await fixture.ReadDetailAsync("pending-arms", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(summary.Detail.Executions);
        var timeline = await fixture.ReadDetailAsync("pending-arms", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId);
        var pending = Assert.Single(timeline.Detail.Nodes, static node => node.SourceKind == "skill_invocation");
        Assert.NotNull(pending.TraceId);
        Assert.NotNull(pending.SpanId);
        Assert.Equal(1, fixture.CountDetailSkillNodes("pending-arms"));
    }

    [Fact]
    public void CurrentSkillReadRejectsPartialInvocationMetadataShape()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedOtelOnly("partial-shape", "pending-skill", "11", "22");
        fixture.DropSkillInvocationMetadataColumn();

        Assert.Equal("skill_projection_schema_unsupported",
            Assert.Throws<InvalidOperationException>(() => fixture.ReadAll()).Message);
    }

    [Theory]
    [InlineData("missing_projection_stamp")]
    [InlineData("future_projection_stamp")]
    [InlineData("missing_snapshot_stamp")]
    [InlineData("future_snapshot_stamp")]
    public void CurrentSkillReadRejectsOwnedShapesWithoutAnExactCurrentStamp(string mutation)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedOtelOnly("owned-shape", "shape-skill", "19", "29");
        fixture.MutateComponentStamp(mutation);

        Assert.Throws<InvalidOperationException>(() => fixture.ReadAll());
    }

    [Fact]
    public void CurrentSkillReadRejectsObsoleteOwnerObjectsAlongsideCurrentAuthority()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedOtelOnly("obsolete-owner", "shape-skill", "1e", "2e");
        fixture.AddObsoleteSkillOwnerObject();

        Assert.Equal("skill_projection_schema_unsupported",
            Assert.Throws<InvalidOperationException>(() => fixture.ReadAll()).Message);
    }

    [Theory]
    [InlineData("claim_name")]
    [InlineData("receipt")]
    [InlineData("event_trace")]
    public void CurrentSkillReadMarksSdkAggregateContradictionsUnavailableInsteadOfFallingThroughToOtel(string mutation)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPair("contradiction", "exact-skill", "1a", "2a", 1);
        fixture.CorruptLatestSdkAggregate("contradiction", mutation);

        var result = fixture.Read("contradiction");

        Assert.Equal("unavailable", result.State);
        Assert.Null(result.InvocationCount);
        Assert.Empty(result.Invocations);
        Assert.Empty(result.SearchFacts);
    }

    [Theory]
    [InlineData("claim_name")]
    [InlineData("receipt")]
    [InlineData("event_trace")]
    public async Task ExactPairExpiryWithCorruptedSdkOwnerGraphIsUnavailable(string mutation)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPair("expired-contradiction", "exact-skill", "1e", "2e", 1);
        fixture.RefreshWorkspace();
        fixture.AdvancePastLatestSdkExpiry("expired-contradiction");
        fixture.CorruptLatestSdkAggregate("expired-contradiction", mutation);

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await fixture.ReadDetailAsync("expired-contradiction", LocalRepositorySessionDetailRequestKind.Summary));

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public void CurrentSkillReadRejectsConflictingDuplicateOtelProducerFacts()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedOtelOnly("duplicate-producer", "first-skill", "1f", "2f");
        fixture.SeedOtelOnly("duplicate-producer", "second-skill", "1f", "2f");

        var projection = fixture.Read("duplicate-producer");

        Assert.Equal("unavailable", projection.State);
        Assert.Empty(projection.Invocations);
    }

    [Theory]
    [InlineData("otel")]
    [InlineData("sdk")]
    public async Task MatchedPairRemainsOneCurrentNodeWhenOnlyTheThirdArmIsPending(string unmatchedArm)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPairWithNames("three-arms", "paired-otel", "paired-sdk", "1b", "2b");
        fixture.SeedUnmatchedThirdArm("three-arms", unmatchedArm, "pending-third");

        var projection = fixture.Read("three-arms");

        Assert.Equal("certification_pending", projection.State);
        Assert.Null(projection.InvocationCount);
        Assert.Equal(2, projection.Invocations.Count);
        var exact = Assert.Single(projection.Invocations,
            static invocation => invocation.OtelSourceIdentity is not null && invocation.SdkSourceIdentity is not null);
        Assert.Equal("producer:" + "1b".PadRight(32, '1') + ":" + "2b".PadRight(16, '2'), exact.CanonicalIdentity);
        Assert.DoesNotContain(projection.SearchFacts, static fact => fact.SkillName == "pending-third");
        Assert.Contains(projection.SearchFacts, static fact => fact.SkillName == "paired-otel");
        Assert.Contains(projection.SearchFacts, static fact => fact.SkillName == "paired-sdk");

        fixture.RefreshWorkspace();
        var summary = await fixture.ReadDetailAsync("three-arms", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(summary.Detail.Executions);
        Assert.Equal("certification_pending", execution.Activity.Skill.State);
        var timeline = await fixture.ReadDetailAsync(
            "three-arms", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId);
        var skills = timeline.Detail.Nodes.Where(static node => node.SourceKind == "skill_invocation").ToArray();
        Assert.Equal(2, skills.Length);
        Assert.Single(skills, static node => Assert.IsType<LocalWorkspaceSkillMetadataDetail>(node.SkillMetadata).CurrentValidState == "current");
        Assert.Single(skills, static node => Assert.IsType<LocalWorkspaceSkillMetadataDetail>(node.SkillMetadata).CurrentValidState == "certification_pending");
    }

    [Fact]
    public async Task ProductionCoordinatorRejectsShiftedSkillSourceReferenceOrdinals()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPair("reference-ordinal", "paired-skill", "2c", "3c", 1);
        fixture.RefreshWorkspace();
        fixture.ShiftCurrentSkillSourceReferenceOrdinals("reference-ordinal");

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await fixture.ReadDetailAsync("reference-ordinal", LocalRepositorySessionDetailRequestKind.Summary));

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ExactPairSdkExpiryChangesVisibleFactsAndRevisionWithoutRefresh()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPairWithNames("exact-expiry", "otel-name", "sdk-name", "1c", "2c");
        fixture.RefreshWorkspace();

        var before = await fixture.ReadDetailAsync("exact-expiry", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(before.Detail.Executions);
        var beforeTimeline = await fixture.ReadDetailAsync(
            "exact-expiry", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId);
        var beforeSkill = Assert.Single(beforeTimeline.Detail.Nodes, static node => node.SourceKind == "skill_invocation");
        Assert.Equal("sdk-name", beforeSkill.NameText);
        Assert.Equal(2, beforeSkill.SourceReferences!.Count);
        Assert.Equal("recorded", Assert.IsType<LocalWorkspaceSkillMetadataDetail>(beforeSkill.SkillMetadata).HistoricalSnapshotReferenceState);

        fixture.AdvancePastLatestSdkExpiry("exact-expiry");

        var after = await fixture.ReadDetailAsync("exact-expiry", LocalRepositorySessionDetailRequestKind.Summary);
        var afterTimeline = await fixture.ReadDetailAsync(
            "exact-expiry", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId);
        var afterSkill = Assert.Single(afterTimeline.Detail.Nodes, static node => node.SourceKind == "skill_invocation");
        Assert.Equal("otel-name", afterSkill.NameText);
        Assert.Single(afterSkill.SourceReferences!);
        Assert.Equal("not_observed", Assert.IsType<LocalWorkspaceSkillMetadataDetail>(afterSkill.SkillMetadata).HistoricalSnapshotReferenceState);
        Assert.NotEqual(before.WorkspaceRevision, after.WorkspaceRevision);
    }

    [Fact]
    public async Task WorkspaceRevisionChangesOnCurrentSdkRetentionOwnerRevisionOnly()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("retention-revision", "sdk-skill");
        fixture.RefreshWorkspace();
        var before = await fixture.ReadDetailAsync("retention-revision", LocalRepositorySessionDetailRequestKind.Summary);

        fixture.IncrementLatestSdkRetentionRevision("retention-revision");

        var after = await fixture.ReadDetailAsync("retention-revision", LocalRepositorySessionDetailRequestKind.Summary);
        var timeline = await fixture.ReadDetailAsync("retention-revision", LocalRepositorySessionDetailRequestKind.Timeline,
            Assert.Single(after.Detail.Executions).ExecutionId);
        Assert.NotEqual(before.WorkspaceRevision, after.WorkspaceRevision);
        Assert.Equal("sdk-skill", Assert.Single(timeline.Detail.Nodes, static node => node.SourceKind == "skill_invocation").NameText);
    }

    [Fact]
    public async Task ProductionCoordinatorRejectsForgedPersistedSkillMetadataAcrossExactPairExpiry()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPairWithNames("expiry-forgery", "otel-name", "sdk-name", "1d", "2d");
        fixture.RefreshWorkspace();
        fixture.AdvancePastLatestSdkExpiry("expiry-forgery");
        fixture.MutatePendingSkillMetadata(
            "UPDATE local_workspace_skill_metadata SET source_state='recorded',source='forged-transition-source';");

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await fixture.ReadDetailAsync("expiry-forgery", LocalRepositorySessionDetailRequestKind.Summary));
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorRejectsExpiredSkillOwnerSwapWithinSession()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPair("expired-owner-swap", "paired-skill", "1f", "2f", 1);
        fixture.SeedUnmatchedThirdArm("expired-owner-swap", "sdk", "other-skill");
        fixture.RefreshWorkspace();
        fixture.AdvancePastLatestSdkExpiry("expired-owner-swap");
        fixture.SwapPairedNodeToOtherExpiredSdkOwner("expired-owner-swap");

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await fixture.ReadDetailAsync("expired-owner-swap", LocalRepositorySessionDetailRequestKind.Summary));

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task TimelineLimitIsAppliedAfterExpiredSkillRowsAreExcluded()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedOtelOnly("timeline-filter", "expires-first", "31", "41");
        fixture.RefreshWorkspaceProjection("timeline-filter");
        fixture.AddGenericEvents("timeline-filter", 2);
        fixture.RefreshWorkspaceProjection("timeline-filter");
        fixture.MovePersistedSkillBeforeRawChildren("timeline-filter");
        fixture.MakeOtelArmStale("timeline-filter");

        var summary = await fixture.ReadDetailAsync("timeline-filter", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(summary.Detail.Executions);
        var timeline = await fixture.ReadDetailAsync(
            "timeline-filter", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId, limit: 1);

        Assert.Equal(2, timeline.Detail.Nodes.Count(static node => node.SourceKind != "execution_root"));
        Assert.True(Assert.Single(timeline.Detail.Nodes, static node => node.SourceKind == "execution_root").HasMoreChildren);
        Assert.DoesNotContain(timeline.Detail.Nodes, static node => node.SourceKind == "skill_invocation");
    }

    [Fact]
    public async Task NodeChildrenFilterExpiredSkillsBeforeLookaheadAndCountThemExactlyOnce()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("node-filter", "expires-first", expired: false);
        fixture.RefreshWorkspace();
        fixture.AddGenericEvents("node-filter", 201);
        fixture.RefreshWorkspaceProjection("node-filter");
        fixture.MovePersistedSkillBeforeRawChildren("node-filter");
        fixture.AdvancePastLatestSdkExpiry("node-filter");

        var summary = await fixture.ReadDetailAsync("node-filter", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(summary.Detail.Executions);
        var root = Assert.Single(summary.Detail.Nodes, static node => node.SourceKind == "execution_root");
        var expectedChildren = fixture.EffectiveRootChildCount("node-filter");
        Assert.Equal(expectedChildren, execution.ChildCount);
        Assert.Equal(expectedChildren, root.ChildCount);

        var detail = await fixture.ReadDetailAsync(
            "node-filter", LocalRepositorySessionDetailRequestKind.Node, nodeId: root.NodeId);
        Assert.Equal(201, detail.Detail.Nodes.Count(node => node.ParentNodeId == root.NodeId));
        Assert.DoesNotContain(detail.Detail.Nodes, static node => node.SourceKind == "skill_invocation");
        var error = Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeNode(detail, root.NodeId));
        Assert.Equal("workspace_too_large", error.Error);
    }

    [Theory]
    [InlineData("UPDATE local_workspace_node_source_references SET source_kind='session_event' WHERE node_id IN (SELECT node_id FROM local_workspace_skill_metadata WHERE current_valid_state='certification_pending');")]
    [InlineData("UPDATE local_workspace_node_source_references SET source_identity='tampered-claim' WHERE node_id IN (SELECT node_id FROM local_workspace_skill_metadata WHERE current_valid_state='certification_pending');")]
    [InlineData("UPDATE local_workspace_node_source_references SET revision_input=revision_input||'|tampered' WHERE node_id IN (SELECT node_id FROM local_workspace_skill_metadata WHERE current_valid_state='certification_pending');")]
    [InlineData("UPDATE local_workspace_skill_metadata SET source_state='recorded',source='foreign-source' WHERE current_valid_state='certification_pending';")]
    [InlineData("INSERT INTO local_workspace_node_source_references(node_id,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input) SELECT node_id,1,source_kind,source_identity,trace_id,span_id,event_id,revision_input FROM local_workspace_node_source_references WHERE node_id IN (SELECT node_id FROM local_workspace_skill_metadata WHERE current_valid_state='certification_pending') AND source_ordinal=0 LIMIT 1;")]
    public void PersistedSqliteMatrix_BackupAuthenticatesFreshPendingSkillClaimGraph(string mutation)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("fresh-pending-backup", "pending-skill", "93", "a3", "94", "a4");
        fixture.RefreshWorkspace();
        fixture.ValidateWorkspaceBackup();

        fixture.MutatePendingSkillMetadata(mutation);

        Assert.Equal("local_workspace_projection_backup_invalid",
            Assert.Throws<InvalidOperationException>(fixture.ValidateWorkspaceBackup).Message);
    }

    [Theory]
    [InlineData("otel")]
    [InlineData("sdk")]
    public void PersistedSqliteMatrix_BackupRejectsCoherentlyRekeyedPendingSkillOwnerArm(string arm)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("coherent-pending-backup", "pending-skill", "95", "a5", "96", "a6");
        fixture.RefreshWorkspace();
        fixture.ValidateWorkspaceBackup();

        fixture.CoherentlyRekeyPendingSkillOwnerArm(arm);
        fixture.AssertSkillOwnerSchemaIsExact();

        Assert.Equal("local_workspace_projection_backup_invalid",
            Assert.Throws<InvalidOperationException>(fixture.ValidateWorkspaceBackup).Message);
    }

    [Theory]
    [InlineData("otel")]
    [InlineData("sdk")]
    public void PersistedSqliteMatrix_BackupRejectsStalePendingSkillNodeAfterOwnerArmIsRemoved(string arm)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("stale-pending-backup", "pending-skill", "97", "a7", "98", "a8");
        fixture.RefreshWorkspace();
        fixture.ValidateWorkspaceBackup();

        fixture.RemovePendingSkillOwnerArm(arm);
        fixture.AssertSkillOwnerSchemaIsExact();

        Assert.Equal("local_workspace_projection_backup_invalid",
            Assert.Throws<InvalidOperationException>(fixture.ValidateWorkspaceBackup).Message);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_TimeAdmittedNodeParticipatesInNodeBound()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("crossing", "Needle-Skill", "71", "81", "72", "82");
        fixture.RefreshWorkspace();
        fixture.AddGenericEvents("crossing", 4098 - fixture.DetailNodeCount("crossing"));
        fixture.RefreshWorkspaceProjection("crossing");
        fixture.AdvancePastLatestSdkExpiry("crossing");

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await fixture.ReadDetailAsync("crossing", LocalRepositorySessionDetailRequestKind.Summary));
        Assert.Equal("workspace_too_large", error.Error);
    }

    [Fact]
    public async Task PersistedSqliteMatrix_ExactPairSearchUsesBothCanonicalAuthorityNames()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPairWithNames("pair", "otel-name", "sdk-name", "51", "61");

        using var otel = JsonDocument.Parse(await fixture.SerializeCollectionAsync("otel-name", null));
        using var sdk = JsonDocument.Parse(await fixture.SerializeCollectionAsync("sdk-name", null, refresh: false));
        Assert.Single(otel.RootElement.GetProperty("items").EnumerateArray());
        Assert.Single(sdk.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, otel.RootElement.GetProperty("items")[0].GetProperty("summary").GetProperty("skill").GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("otel:")]
    [InlineData("sdk:")]
    public async Task ProductionCoordinatorRejectsForeignSameKindSkillClaim(string sourcePrefix)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPair("authority", "exact-skill", "51", "61", 1);
        fixture.RefreshWorkspace();
        string nodeId;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var find = connection.CreateCommand();
            find.CommandText = "SELECT node_id FROM local_workspace_nodes WHERE source_kind='skill_invocation';";
            nodeId = Assert.IsType<string>(find.ExecuteScalar());
            using var mutation = connection.CreateCommand();
            mutation.CommandText = "UPDATE local_workspace_node_source_references SET source_identity=$foreign WHERE node_id=$node AND source_identity LIKE $prefix||'%';";
            mutation.Parameters.AddWithValue("$foreign", sourcePrefix + "foreign-claim");
            mutation.Parameters.AddWithValue("$prefix", sourcePrefix);
            mutation.Parameters.AddWithValue("$node", nodeId);
            Assert.Equal(1, mutation.ExecuteNonQuery());
        }

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await fixture.ReadDetailAsync("authority", LocalRepositorySessionDetailRequestKind.Node, nodeId: nodeId));
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("otel", "rekey")]
    [InlineData("sdk", "rekey")]
    [InlineData("otel", "remove")]
    [InlineData("sdk", "remove")]
    [InlineData("metadata", "source")]
    [InlineData("metadata", "trigger")]
    [InlineData("metadata", "history")]
    [InlineData("metadata", "registry")]
    [InlineData("metadata", "revision")]
    [InlineData("metadata", "duplicate_reference")]
    public async Task ProductionCoordinatorRejectsPendingSkillOwnerOrMetadataDrift(string arm, string mutation)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("pending-live-proof", "pending-skill", "9a", "aa", "9b", "ab");
        fixture.RefreshWorkspace();
        if (mutation == "rekey") fixture.CoherentlyRekeyPendingSkillOwnerArm(arm);
        else if (mutation == "remove") fixture.RemovePendingSkillOwnerArm(arm);
        else fixture.MutatePendingSkillLiveFact(mutation);

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await fixture.ReadDetailAsync("pending-live-proof", LocalRepositorySessionDetailRequestKind.Summary));
        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("parent")]
    [InlineData("relationship")]
    [InlineData("ordinal")]
    [InlineData("kind")]
    [InlineData("name")]
    [InlineData("lifecycle")]
    [InlineData("status")]
    [InlineData("time")]
    [InlineData("carrier")]
    public async Task ProductionCoordinatorRejectsCurrentSkillNodeProjectionCopyDrift(string mutation)
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedOtelOnly("skill-row-drift", "owned-skill", "5a", "6a");
        fixture.RefreshWorkspace();
        fixture.MutateCurrentSkillNode("skill-row-drift", mutation);

        var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
            await fixture.ReadDetailAsync("skill-row-drift", LocalRepositorySessionDetailRequestKind.Summary));

        Assert.Equal("local_monitor_ui_unavailable", error.Error);
    }

    [Fact]
    public async Task ProductionCoordinatorReadsExactOtelSkillWhenSdkAuthorityIsGenuinelyAbsent()
    {
        using var fixture = new CurrentInvocationProjectionFixture(installSdkAuthority: false);
        fixture.SeedOtelOnly("otel-without-sdk", "otel-skill", "5b", "6b");
        fixture.RefreshWorkspace();

        var summary = await fixture.ReadDetailAsync("otel-without-sdk", LocalRepositorySessionDetailRequestKind.Summary);
        var execution = Assert.Single(summary.Detail.Executions);
        var timeline = await fixture.ReadDetailAsync(
            "otel-without-sdk", LocalRepositorySessionDetailRequestKind.Timeline, execution.ExecutionId);

        Assert.Single(timeline.Detail.Nodes,
            static node => node.SourceKind == "skill_invocation" && node.NameText == "otel-skill");
    }

    [Fact]
    public void CurrentSkillReadAllowsSdkOnlyClaimWithoutSourceCompatibilityTable()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("sdk-without-compatibility", "sdk-skill");
        fixture.DropSourceCompatibilityRevisionTable();

        var projection = fixture.Read("sdk-without-compatibility");

        Assert.Equal("current", projection.State);
        Assert.Equal("sdk-skill", Assert.Single(projection.Invocations).SdkSkillName);
    }

    [Fact]
    public async Task ExactPairWithSdkMetadataOmittedUsesOtelMetadataAfterSdkExpiry()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedExactPairWithSdkMetadataOmitted("metadata-fallback", "metadata-skill", "5c", "6c");
        fixture.RefreshWorkspace();
        fixture.AdvancePastLatestSdkExpiry("metadata-fallback");

        var summary = await fixture.ReadDetailAsync("metadata-fallback", LocalRepositorySessionDetailRequestKind.Summary);
        var timeline = await fixture.ReadDetailAsync(
            "metadata-fallback", LocalRepositorySessionDetailRequestKind.Timeline, Assert.Single(summary.Detail.Executions).ExecutionId);
        var skill = Assert.Single(timeline.Detail.Nodes, static node => node.SourceKind == "skill_invocation");
        var metadata = Assert.IsType<LocalWorkspaceSkillMetadataDetail>(skill.SkillMetadata);

        Assert.Equal("recorded", metadata.SourceState);
        Assert.Equal("project", metadata.Source);
        Assert.Equal("recorded", metadata.TriggerState);
        Assert.Equal("user-invoked", metadata.Trigger);
    }

    private static void AssertFilteredAdmittedSummary(CurrentInvocationProjectionFixture fixture, JsonDocument filtered)
    {
        var admitted = Assert.Single(filtered.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(fixture.SessionId("admitted"), admitted.GetProperty("session_id").GetString());
        Assert.NotEqual(fixture.SessionId("pending"), admitted.GetProperty("session_id").GetString());
        var admittedSkill = admitted.GetProperty("summary").GetProperty("skill");
        Assert.Equal("recorded", admittedSkill.GetProperty("state").GetString());
        Assert.Equal(1, admittedSkill.GetProperty("count").GetInt32());
    }
}

internal sealed class CurrentInvocationProjectionFixture : IDisposable
{
    private static readonly DateTimeOffset WrittenAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InitialReadAt = WrittenAt.AddHours(1);
    private DateTimeOffset readAt = InitialReadAt;
    private const string Fingerprint = "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c";
    private readonly string directory;
    private readonly bool ownsDirectory;
    private readonly Dictionary<string, string> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionSkillInvocationWrite> latestWrites = new(StringComparer.Ordinal);
    private readonly MatrixRegistryAuthority authority;
    private readonly bool sdkAuthorityInstalled;
    private long otelOrdinal;

    internal CurrentInvocationProjectionFixture(
        bool registryAvailable = true,
        string? databasePath = null,
        bool installSdkAuthority = true)
    {
        directory = databasePath is null
            ? Path.Combine(Path.GetTempPath(), $"skill-current-matrix-{Guid.NewGuid():N}")
            : Path.GetDirectoryName(databasePath)!;
        ownsDirectory = databasePath is null;
        Directory.CreateDirectory(directory);
        DatabasePath = databasePath ?? Path.Combine(directory, "monitor.sqlite");
        sdkAuthorityInstalled = installSdkAuthority;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
        RetentionSchemaMigrator.Apply(connection, transaction);
        transaction.Commit();
        new SqliteSourceCompatibilityStore(DatabasePath).CreateSchema();
        new SqliteSessionStore(DatabasePath).CreateSchema();
        using var install = Open();
        using var installTransaction = install.BeginTransaction();
        SkillProjectionSchemaV1.Ensure(install, installTransaction);
        if (installSdkAuthority) SkillInvocationSnapshotSchemaV1.Ensure(install, installTransaction);
        LocalRepositoryCatalogSchemaV1.Ensure(install, installTransaction);
        LocalArchiveSchemaV1.Ensure(install, installTransaction);
        installTransaction.Commit();
        LocalWorkspaceProjectionSchemaV1.Ensure(install, WrittenAt);
        authority = new MatrixRegistryAuthority(registryAvailable);
    }

    internal string DatabasePath { get; }
    internal ISkillRegistryGenerationAuthority RegistryAuthority => authority;
    internal string File(string name) => Path.Combine(directory, name);

    internal void SeedSdkOnly(
        string sessionKey,
        string skillName,
        bool registryAccepted = true,
        string state = "available",
        string reason = "none",
        bool expired = false)
    {
        var sourceVersion = registryAccepted ? "1.0.65" : "0.9.0";
        var write = NewWrite(sessionKey, skillName, sourceVersion, state, reason, expired);
        Commit(write);
        sessions[sessionKey] = ResolveSession(sessionKey);
        latestWrites[sessionKey] = write;
    }

    internal void SeedSdkClaims(string sessionKey, int count)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        for (var index = 0; index < count; index++)
        {
            var write = NewWrite(sessionKey, $"skill-{index:D5}", "1.0.65", "available", "none", expired: false);
            var outcome = SessionSkillInvocationParticipant.InsertOrVerify(
                connection, transaction, write, NoOpWorkspaceParticipant.Instance, out _);
            Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
            latestWrites[sessionKey] = write;
        }
        transaction.Commit();
        sessions[sessionKey] = ResolveSession(sessionKey);
    }

    internal (int Inside, int Persisted) PublishSdkClaim(string sessionKey, string skillName, bool commit)
    {
        var write = NewWrite(sessionKey, skillName, "1.0.65", "available", "none", expired: false);
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction())
        {
            var outcome = SessionSkillInvocationParticipant.InsertOrVerify(
                connection, transaction, write, new LocalWorkspaceProjectionTransactionParticipant(authority), out _);
            Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=$session AND source_kind='skill_invocation';";
            count.Parameters.AddWithValue("$session", sessions[sessionKey]);
            var inside = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (commit) transaction.Commit(); else transaction.Rollback();
            return (inside, CountDetailSkillNodes(sessionKey));
        }
    }

    internal void SeedSdkWithExplicitParent(string sessionKey, string skillName)
    {
        var parentSourceId = Guid.NewGuid().ToString("D");
        var write = NewWrite(sessionKey, skillName, "1.0.65", "available", "none", expired: false, parentSourceId);
        Commit(write);
        sessions[sessionKey] = ResolveSession(sessionKey);
        latestWrites[sessionKey] = write;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,
                occurred_at,content_state)
            SELECT $event_id,s.session_id,s.run_id,'copilot-sdk','copilot-sdk-stream',$source_event_id,'event',
                   $at,'not_captured'
            FROM skill_invocation_snapshots s WHERE s.snapshot_id=$snapshot_id;
            """;
        command.Parameters.AddWithValue("$event_id", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$source_event_id", parentSourceId);
        command.Parameters.AddWithValue("$snapshot_id", write.SnapshotId.ToString("D"));
        command.Parameters.AddWithValue("$at", WrittenAt.ToString("O"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal void SeedExactPair(string sessionKey, string skillName, string traceSeed, string spanSeed, int duplicateObservations)
    {
        SeedSdkOnly(sessionKey, skillName);
        var traceId = traceSeed.PadRight(32, traceSeed[0]);
        var spanId = spanSeed.PadRight(16, spanSeed[0]);
        BindLatestSdkProducer(sessionKey, traceId, spanId);
        for (var index = 0; index < duplicateObservations; index++)
            SeedOtel(sessionKey, skillName, traceId, spanId);
    }

    internal void SeedMismatchedPair(string sessionKey, string skillName, string otelTrace, string otelSpan, string sdkTrace, string sdkSpan)
    {
        SeedSdkOnly(sessionKey, skillName);
        var sdkTraceId = sdkTrace.PadRight(32, sdkTrace[0]);
        var sdkSpanId = sdkSpan.PadRight(16, sdkSpan[0]);
        BindLatestSdkProducer(sessionKey, sdkTraceId, sdkSpanId);
        SeedOtel(sessionKey, skillName, otelTrace.PadRight(32, otelTrace[0]), otelSpan.PadRight(16, otelSpan[0]));
    }

    internal void SeedOtelOnly(string sessionKey, string skillName, string traceSeed, string spanSeed)
    {
        EnsureSession(sessionKey);
        SeedOtel(sessionKey, skillName, traceSeed.PadRight(32, traceSeed[0]), spanSeed.PadRight(16, spanSeed[0]));
    }

    internal void SeedExactPairWithNames(string sessionKey, string otelName, string sdkName, string traceSeed, string spanSeed)
    {
        SeedSdkOnly(sessionKey, sdkName);
        var traceId = traceSeed.PadRight(32, traceSeed[0]);
        var spanId = spanSeed.PadRight(16, spanSeed[0]);
        BindLatestSdkProducer(sessionKey, traceId, spanId);
        SeedOtel(sessionKey, otelName, traceId, spanId);
    }

    internal void SeedExactPairWithSdkMetadataOmitted(
        string sessionKey,
        string skillName,
        string traceSeed,
        string spanSeed)
    {
        var write = NewWrite(sessionKey, skillName, "1.0.65", "available", "none", expired: false) with
        {
            Source = null,
            Trigger = null,
        };
        Commit(write);
        sessions[sessionKey] = ResolveSession(sessionKey);
        latestWrites[sessionKey] = write;
        var traceId = traceSeed.PadRight(32, traceSeed[0]);
        var spanId = spanSeed.PadRight(16, spanSeed[0]);
        BindLatestSdkProducer(sessionKey, traceId, spanId);
        SeedOtel(sessionKey, skillName, traceId, spanId);
    }

    internal IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> ReadAll()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        return SkillProjectionReadService.ReadCurrentInvocationProjection(connection, transaction, sessions.Values, readAt, authority);
    }

    internal (IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> Result, int CommandExecutions) ReadAllObserved()
    {
        authority.ResetObservations();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var observer = new LocalWorkspaceProjectionSchemaTests.NativeStatementExecutionObserver(connection);
        var result = SkillProjectionReadService.ReadCurrentInvocationProjection(
            connection, transaction, sessions.Values, readAt, authority);
        return (result, observer.ExecutionCount);
    }

    internal int RegistryCaptureCount => authority.CaptureCount;
    internal int RegistryLeaseCount => authority.LeaseCount;
    internal int RegistryVerifyCount => authority.VerifyCount;
    internal int RegistryTupleAuthorizationCount => authority.TupleAuthorizationCount;

    internal (int BeforeRows, int AfterRows, bool OldPointerStillCurrent) PublishActualRegistryGeneration(string sessionKey, bool injectFailure)
    {
        var tuple = new SkillInvocationV2CompatibilityTuple(
            "1.0.65", "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1", "github-copilot-sdk.skill-invoked.normalize.v2",
            "github-copilot-sdk.skill-invoked.v1", Fingerprint);
        var rejected = Registry([]);
        var accepted = Registry([new(tuple, SkillInvocationV2CompatibilityDisposition.Accepted)]);
        var gate = new LocalWorkspacePublicationGate();
        var provider = new CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2.SkillInvocationV2RegistryProviderV1(rejected, gate);
        using var connection = Open();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, readAt, provider);
        var before = CountSkillRows(connection, sessions[sessionKey]);
        var old = Assert.IsAssignableFrom<ISkillRegistryGenerationCapture>(provider.CaptureGeneration());
        provider.CurrentGenerationChanging += proposed =>
        {
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction, [sessions[sessionKey]], readAt, proposed);
            if (injectFailure) throw new InvalidOperationException("injected_generation_sensitive_refresh_failure");
            transaction.Commit();
        };
        if (injectFailure)
            Assert.Throws<InvalidOperationException>(() => provider.PublishGeneration(accepted));
        else
            provider.PublishGeneration(accepted);
        var oldStillCurrent = provider.TryAcquireGenerationReadLease(old, out var lease);
        lease?.Dispose();
        return (before, CountSkillRows(connection, sessions[sessionKey]), oldStillCurrent);
    }

    private static int CountSkillRows(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=$session AND source_kind='skill_invocation';";
        command.Parameters.AddWithValue("$session", sessionId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SkillInvocationV2ArtifactRegistry Registry(IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> entries) =>
        (SkillInvocationV2ArtifactRegistry)typeof(SkillInvocationV2ArtifactRegistry)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(int), typeof(IReadOnlyList<SkillInvocationV2CompatibilityRegistryRevision>), typeof(IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry>)], null)!
            .Invoke([1, Array.Empty<SkillInvocationV2CompatibilityRegistryRevision>(), entries]);

    internal SkillProjectionCurrentInvocationProjection Read(string sessionKey) => ReadAll()[sessions[sessionKey]];

    internal string SessionId(string sessionKey) => sessions[sessionKey];

    internal void RefreshWorkspace()
    {
        using var connection = Open();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, readAt);
        using var transaction = connection.BeginTransaction();
        LocalWorkspaceProjectionStore.Refresh(connection, transaction, readAt, authority);
        transaction.Commit();
    }

    internal void AdvancePastLatestSdkExpiry(string sessionKey) =>
        readAt = latestWrites[sessionKey].ExpiresAt.AddTicks(1);

    internal void AddUnrelatedExecution(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session_runs(run_id,session_id,source_surface,native_run_id,status) VALUES($run,$session,'copilot-sdk',$native,'active');";
        command.Parameters.AddWithValue("$run", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$native", "unrelated-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal void DeleteLatestSdkArm(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM skill_projection_sdk_claims WHERE session_id=$session; DELETE FROM skill_invocation_snapshot_receipts WHERE snapshot_id IN (SELECT snapshot_id FROM skill_invocation_snapshots WHERE session_id=$session); DELETE FROM skill_invocation_snapshots WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        command.ExecuteNonQuery();
    }

    internal void DropSkillInvocationMetadataColumn()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE skill_projection_invocations DROP COLUMN skill_source;";
        command.ExecuteNonQuery();
    }

    internal void MutateComponentStamp(string mutation)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = mutation switch
        {
            "missing_projection_stamp" => "DELETE FROM schema_version WHERE component='skill_projection';",
            "future_projection_stamp" => "UPDATE schema_version SET version=2 WHERE component='skill_projection';",
            "missing_snapshot_stamp" => "DELETE FROM schema_version WHERE component='skill_invocation_snapshot';",
            "future_snapshot_stamp" => "UPDATE schema_version SET version=2 WHERE component='skill_invocation_snapshot';",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal void AddObsoleteSkillOwnerObject()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE monitor_skill_invocations(marker INTEGER);";
        command.ExecuteNonQuery();
    }

    internal void CorruptLatestSdkAggregate(string sessionKey, string mutation)
    {
        using var connection = Open();
        var triggers = mutation switch
        {
            "claim_name" => new[] { "skill_projection_sdk_claims_update_rejected" },
            "receipt" => new[] { "skill_invocation_snapshot_receipts_update_rejected" },
            "event_trace" => new[] { "skill_invocation_snapshot_session_event_update_rejected" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var definitions = new List<string>();
        foreach (var trigger in triggers)
        {
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name=$name;";
            read.Parameters.AddWithValue("$name", trigger);
            definitions.Add(Assert.IsType<string>(read.ExecuteScalar()));
        }
        using var transaction = connection.BeginTransaction();
        foreach (var trigger in triggers)
        {
            using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = "DROP TRIGGER " + trigger + ";";
            drop.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = mutation switch
            {
                "claim_name" => "UPDATE skill_projection_sdk_claims SET skill_name='forged-name' WHERE session_id=$session;",
                "receipt" => "UPDATE skill_invocation_snapshot_receipts SET request_fingerprint_sha256=lower(hex(randomblob(32))) WHERE snapshot_id IN (SELECT snapshot_id FROM skill_invocation_snapshots WHERE session_id=$session);",
                "event_trace" => "UPDATE session_events SET trace_id='ffffffffffffffffffffffffffffffff' WHERE event_id IN (SELECT event_id FROM skill_invocation_snapshots WHERE session_id=$session);",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
            command.Parameters.AddWithValue("$session", sessions[sessionKey]);
            Assert.True(command.ExecuteNonQuery() > 0);
        }
        foreach (var definition in definitions)
        {
            using var restore = connection.CreateCommand();
            restore.Transaction = transaction;
            restore.CommandText = definition;
            restore.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal void SeedUnmatchedThirdArm(string sessionKey, string arm, string skillName)
    {
        if (arm == "otel")
        {
            SeedOtel(sessionKey, skillName, "3b".PadRight(32, '3'), "4b".PadRight(16, '4'));
            return;
        }
        if (arm == "sdk")
        {
            SeedSdkOnly(sessionKey, skillName);
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(arm));
    }

    internal void MakeOtelArmStale(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE skill_projection_generations SET lifecycle='superseded' WHERE generation_id IN (SELECT generation_id FROM skill_projection_invocations WHERE session_id=$session);";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        Assert.True(command.ExecuteNonQuery() > 0);
    }

    internal async Task<byte[]> SerializeCollectionAsync(string? q, bool? hasSkill, bool refresh = true)
    {
        if (refresh) RefreshWorkspace();
        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(
                DatabasePath,
                new LocalWorkspaceSessionSnapshotContributor(new FixedTimeProvider(readAt), registryAuthority: authority),
                SqliteLocalArchiveFactSnapshotContributor.Instance,
                skillRegistryAuthority: authority)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);
        var requestJson = $$"""{"schema_version":"local-monitor-session-search.request.v1","scope":"all","repository_id":null,"archive_scope":"active_only","from":null,"to":null,"source":[],"model":[],"status":[],"has_skill":{{(hasSkill is null ? "null" : hasSkill.Value ? "true" : "false")}},"has_subagent":null,"has_error":null,"has_retry":null,"q":{{(q is null ? "null" : JsonSerializer.Serialize(q))}},"cursor":null,"limit":null}""";
        Assert.Equal(LocalMonitorV1SessionSearchParseStatus.Success,
            LocalMonitorV1SessionSearchRequestParser.Parse(Encoding.UTF8.GetBytes(requestJson), out var request));
        return LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request!, new byte[32]);
    }

    internal ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(
        string sessionKey,
        LocalRepositorySessionDetailRequestKind kind,
        string? executionId = null,
        string? nodeId = null,
        string? parentNodeId = null,
        int limit = 100)
    {
        var clock = new FixedTimeProvider(readAt);
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(
                registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority);
        return service.ReadDetailAsync(new(kind, sessions[sessionKey], ExecutionId: executionId, NodeId: nodeId, ParentNodeId: parentNodeId, Limit: limit), CancellationToken.None);
    }

    internal void AssertWorkspaceSkill(string sessionKey, string state, int? count, string[] searchFacts)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state,count FROM local_workspace_session_activity WHERE session_id=$session AND kind='skill';";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(state, reader.GetString(0));
        Assert.Equal(count, reader.IsDBNull(1) ? null : reader.GetInt32(1));
        reader.Close();
        command.CommandText = "SELECT normalized_text FROM local_workspace_session_search_facts WHERE session_id=$session AND kind='skill' ORDER BY normalized_text COLLATE BINARY;";
        using var facts = command.ExecuteReader();
        var actual = new List<string>();
        while (facts.Read()) actual.Add(facts.GetString(0));
        Assert.Equal(searchFacts, actual);
        Assert.Equal(searchFacts.Length != 0, state == "recorded" && count > 0);
    }

    internal int CountDetailSkillNodes(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=$session AND source_kind='skill_invocation' AND kind='skill';";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal void ValidateWorkspaceBackup()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup.LocalWorkspaceProjectionBackupValidation.Validate(
            connection, transaction, readAt, authority);
    }

    internal void MutatePendingSkillMetadata(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal void SwapPairedNodeToOtherExpiredSdkOwner(string sessionKey)
    {
        using var connection = Open();
        using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT paired.node_id,paired.source_identity,paired.otel_source_identity,
                   other.sdk_source_identity,reference.event_id,snapshot.name,snapshot.snapshot_id
            FROM local_workspace_nodes paired
            JOIN local_workspace_nodes other ON other.session_id=paired.session_id
              AND other.source_kind='skill_invocation' AND other.otel_source_identity IS NULL
              AND other.sdk_source_identity IS NOT NULL
            JOIN local_workspace_node_source_references reference ON reference.node_id=other.node_id
              AND reference.source_identity=other.sdk_source_identity
            JOIN skill_invocation_snapshots snapshot
              ON 'sdk:'||snapshot.claim_id=other.sdk_source_identity AND snapshot.event_id=reference.event_id
            WHERE paired.session_id=$session AND paired.source_kind='skill_invocation'
              AND paired.otel_source_identity IS NOT NULL AND paired.sdk_source_identity IS NOT NULL;
            """;
        read.Parameters.AddWithValue("$session", sessions[sessionKey]);
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        var nodeId = reader.GetString(0);
        var canonicalIdentity = reader.GetString(1);
        var otelIdentity = reader.GetString(2);
        var sdkIdentity = reader.GetString(3);
        var eventId = reader.GetString(4);
        var skillName = reader.GetString(5);
        var snapshotId = reader.GetString(6);
        Assert.False(reader.Read());
        reader.Close();

        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE local_workspace_nodes
            SET sdk_source_identity=$sdk,event_id=$event,name_state='recorded',name_text=$name
            WHERE node_id=$node;
            UPDATE local_workspace_node_source_references
            SET source_identity=$sdk,event_id=$event
            WHERE node_id=$node AND source_identity LIKE 'sdk:%';
            UPDATE local_workspace_node_source_references
            SET revision_input=$revision
            WHERE node_id=$node;
            UPDATE local_workspace_skill_metadata
            SET historical_snapshot_reference_state='recorded',historical_snapshot_reference=$snapshot
            WHERE node_id=$node;
            """;
        update.Parameters.AddWithValue("$node", nodeId);
        update.Parameters.AddWithValue("$sdk", sdkIdentity);
        update.Parameters.AddWithValue("$event", eventId);
        update.Parameters.AddWithValue("$name", skillName);
        update.Parameters.AddWithValue("$snapshot", snapshotId);
        update.Parameters.AddWithValue("$revision", canonicalIdentity + "|" + otelIdentity + "|" + sdkIdentity);
        Assert.Equal(5, update.ExecuteNonQuery());
    }

    internal void MutateCurrentSkillNode(string sessionKey, string mutation)
    {
        if (mutation == "execution")
        {
            var foreignRun = Guid.CreateVersion7().ToString("D");
            using (var seed = Open())
            using (var insert = seed.CreateCommand())
            {
                insert.CommandText = "INSERT INTO session_runs(run_id,session_id,source_surface,status) VALUES($run,$session,'copilot-cli','completed');";
                insert.Parameters.AddWithValue("$run", foreignRun);
                insert.Parameters.AddWithValue("$session", sessions[sessionKey]);
                Assert.Equal(1, insert.ExecuteNonQuery());
            }
            RefreshWorkspaceProjection(sessionKey);
            using var mutate = Open();
            using var update = mutate.CreateCommand();
            update.CommandText = """
                UPDATE local_workspace_nodes SET execution_id=(
                  SELECT execution_id FROM local_workspace_execution_headers
                  WHERE session_id=$session AND source_identity=$run)
                WHERE session_id=$session AND source_kind='skill_invocation';
                """;
            update.Parameters.AddWithValue("$session", sessions[sessionKey]);
            update.Parameters.AddWithValue("$run", foreignRun);
            Assert.Equal(1, update.ExecuteNonQuery());
            return;
        }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = mutation switch
        {
            "parent" => "UPDATE local_workspace_nodes SET parent_node_id=NULL WHERE session_id=$session AND source_kind='skill_invocation';",
            "relationship" => "UPDATE local_workspace_nodes SET relationship_authority='unknown' WHERE session_id=$session AND source_kind='skill_invocation';",
            "ordinal" => "UPDATE local_workspace_nodes SET source_ordinal=source_ordinal+10000 WHERE session_id=$session AND source_kind='skill_invocation';",
            "kind" => "UPDATE local_workspace_nodes SET kind='event' WHERE session_id=$session AND source_kind='skill_invocation';",
            "name" => "UPDATE local_workspace_nodes SET name_text='forged-skill' WHERE session_id=$session AND source_kind='skill_invocation';",
            "lifecycle" => "UPDATE local_workspace_nodes SET lifecycle='started' WHERE session_id=$session AND source_kind='skill_invocation';",
            "status" => "UPDATE local_workspace_nodes SET status='active' WHERE session_id=$session AND source_kind='skill_invocation';",
            "time" => "UPDATE local_workspace_nodes SET time_authority='missing',start_utc_ticks=NULL,end_utc_ticks=NULL,duration_ms=NULL WHERE session_id=$session AND source_kind='skill_invocation';",
            "carrier" => "UPDATE local_workspace_nodes SET event_id=$foreign_event,trace_id=$foreign_trace,span_id=$foreign_span WHERE session_id=$session AND source_kind='skill_invocation';",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        command.Parameters.AddWithValue("$foreign_event", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("$foreign_trace", new string('e', 32));
        command.Parameters.AddWithValue("$foreign_span", new string('f', 16));
        command.Parameters.AddWithValue("$at", WrittenAt.ToString("O"));
        Assert.True(command.ExecuteNonQuery() > 0);
    }

    internal void ShiftCurrentSkillSourceReferenceOrdinals(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE local_workspace_node_source_references SET source_ordinal=source_ordinal+2
            WHERE node_id IN (SELECT node_id FROM local_workspace_nodes
              WHERE session_id=$session AND source_kind='skill_invocation');
            """;
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        Assert.True(command.ExecuteNonQuery() > 0);
    }

    internal void IncrementLatestSdkRetentionRevision(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE retention_items SET revision=revision+1
            WHERE item_id=(SELECT content_item_id FROM skill_invocation_snapshots
              WHERE snapshot_id=$snapshot);
            """;
        command.Parameters.AddWithValue("$snapshot", latestWrites[sessionKey].SnapshotId.ToString("D"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal void DropSourceCompatibilityRevisionTable()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE source_trace_compatibility_revisions;";
        command.ExecuteNonQuery();
    }

    internal void CoherentlyRekeyPendingSkillOwnerArm(string arm)
    {
        using var connection = Open();
        var triggerNames = arm == "otel"
            ? new[] { "skill_projection_invocations_update_rejected" }
            : new[] { "skill_projection_sdk_claims_update_rejected", "skill_invocation_snapshot_rows_update_rejected" };
        var triggerDefinitions = new List<string>();
        using (var readTriggers = connection.CreateCommand())
        {
            readTriggers.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name IN (SELECT CAST(value AS TEXT) FROM json_each($names)) ORDER BY name;";
            readTriggers.Parameters.AddWithValue("$names", JsonSerializer.Serialize(triggerNames));
            using var reader = readTriggers.ExecuteReader();
            while (reader.Read()) triggerDefinitions.Add(reader.GetString(0));
        }
        Assert.Equal(triggerNames.Length, triggerDefinitions.Count);
        using var disableForeignKeys = connection.CreateCommand();
        disableForeignKeys.CommandText = "PRAGMA foreign_keys=OFF;";
        disableForeignKeys.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        string oldIdentity;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = arm == "otel"
                ? "SELECT otel_source_identity FROM local_workspace_nodes WHERE source_kind='skill_invocation' AND otel_source_identity IS NOT NULL AND sdk_source_identity IS NULL LIMIT 1;"
                : "SELECT sdk_source_identity FROM local_workspace_nodes WHERE source_kind='skill_invocation' AND sdk_source_identity IS NOT NULL AND otel_source_identity IS NULL LIMIT 1;";
            oldIdentity = Assert.IsType<string>(read.ExecuteScalar());
        }
        if (arm == "otel")
        {
            var newRawId = 9_000_000L;
            Assert.Equal(1, Execute("DROP TRIGGER skill_projection_invocations_update_rejected; UPDATE skill_projection_invocations SET raw_record_id=$raw WHERE 'otel:'||raw_record_id||':'||span_ordinal=$old;",
                ("$raw", newRawId), ("$old", oldIdentity)));
        }
        else if (arm == "sdk")
        {
            var newClaimId = Guid.CreateVersion7().ToString("D");
            Assert.Equal(2, Execute("DROP TRIGGER IF EXISTS skill_projection_sdk_claims_update_rejected; DROP TRIGGER IF EXISTS skill_invocation_snapshot_rows_update_rejected; UPDATE skill_projection_sdk_claims SET claim_id=$claim WHERE 'sdk:'||claim_id=$old; UPDATE skill_invocation_snapshots SET claim_id=$claim WHERE 'sdk:'||claim_id=$old;",
                ("$claim", newClaimId), ("$old", oldIdentity)));
        }
        else throw new ArgumentOutOfRangeException(nameof(arm));
        foreach (var definition in triggerDefinitions) Execute(definition);
        transaction.Commit();

        int Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return command.ExecuteNonQuery();
        }
    }

    internal void AssertSkillOwnerSchemaIsExact()
    {
        using var connection = Open();
        Assert.True(SkillProjectionSchemaV1.HasExactOwnedSchema(connection, null));
        Assert.True(SkillInvocationSnapshotSchemaV1.HasExactOwnedSchema(connection, null));
    }

    internal void RemovePendingSkillOwnerArm(string arm)
    {
        using var connection = Open();
        var triggerNames = arm == "otel"
            ? new[] { "skill_projection_invocations_delete_rejected" }
            : new[] { "skill_projection_sdk_claims_delete_rejected", "skill_invocation_snapshot_rows_delete_rejected" };
        var definitions = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name IN (SELECT CAST(value AS TEXT) FROM json_each($names)) ORDER BY name;";
            read.Parameters.AddWithValue("$names", JsonSerializer.Serialize(triggerNames));
            using var reader = read.ExecuteReader();
            while (reader.Read()) definitions.Add(reader.GetString(0));
        }
        Assert.Equal(triggerNames.Length, definitions.Count);
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys=OFF;";
        foreignKeys.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = arm == "otel"
                ? "DROP TRIGGER skill_projection_invocations_delete_rejected; DELETE FROM skill_projection_invocations WHERE 'otel:'||raw_record_id||':'||span_ordinal=(SELECT otel_source_identity FROM local_workspace_nodes WHERE otel_source_identity IS NOT NULL AND sdk_source_identity IS NULL LIMIT 1);"
                : "DROP TRIGGER skill_projection_sdk_claims_delete_rejected; DROP TRIGGER skill_invocation_snapshot_rows_delete_rejected; DELETE FROM skill_invocation_snapshots WHERE 'sdk:'||claim_id=(SELECT sdk_source_identity FROM local_workspace_nodes WHERE sdk_source_identity IS NOT NULL AND otel_source_identity IS NULL LIMIT 1); DELETE FROM skill_projection_sdk_claims WHERE 'sdk:'||claim_id=(SELECT sdk_source_identity FROM local_workspace_nodes WHERE sdk_source_identity IS NOT NULL AND otel_source_identity IS NULL LIMIT 1);";
            Assert.Equal(arm == "otel" ? 1 : 2, delete.ExecuteNonQuery());
        }
        foreach (var definition in definitions)
        {
            using var restore = connection.CreateCommand();
            restore.Transaction = transaction;
            restore.CommandText = definition;
            restore.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal int ExecutionSkillCount(string sessionKey)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT skill_activity_count FROM local_workspace_execution_headers WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal long PersistedRootChildCount(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=$session AND parent_node_id=(SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND source_kind='execution_root' LIMIT 1);";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal long EffectiveRootChildCount(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=$session AND parent_node_id=(SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND source_kind='execution_root' LIMIT 1) AND source_kind<>'skill_invocation';";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal void MutatePendingSkillLiveFact(string mutation)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = mutation switch
        {
            "source" => "UPDATE local_workspace_skill_metadata SET source_state='recorded',source='forged-source' WHERE current_valid_state='certification_pending';",
            "trigger" => "UPDATE local_workspace_skill_metadata SET trigger_state='recorded',trigger='forged-trigger' WHERE current_valid_state='certification_pending';",
            "history" => "UPDATE local_workspace_skill_metadata SET historical_snapshot_reference_state='recorded',historical_snapshot_reference='01990000-0000-7000-8000-000000000099' WHERE current_valid_state='certification_pending';",
            "registry" => "UPDATE local_workspace_skill_metadata SET registry_generation_identity='forged-generation' WHERE current_valid_state='certification_pending';",
            "revision" => "UPDATE local_workspace_node_source_references SET revision_input=revision_input||'|forged' WHERE node_id IN (SELECT node_id FROM local_workspace_skill_metadata WHERE current_valid_state='certification_pending');",
            "duplicate_reference" => "INSERT INTO local_workspace_node_source_references(node_id,source_ordinal,source_kind,source_identity,trace_id,span_id,event_id,revision_input) SELECT node_id,15,source_kind,source_identity,trace_id,span_id,event_id,revision_input FROM local_workspace_node_source_references WHERE node_id IN (SELECT node_id FROM local_workspace_skill_metadata WHERE current_valid_state='certification_pending') ORDER BY node_id,source_ordinal LIMIT 1;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.True(command.ExecuteNonQuery() > 0);
    }

    internal string[] RawSkillEventKinds(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT n.kind FROM local_workspace_nodes n JOIN session_events e ON n.source_kind='session_event' AND n.source_identity=e.event_id WHERE n.session_id=$session AND e.type='skill.invoked' ORDER BY n.node_id;";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        using var reader = command.ExecuteReader();
        var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray();
    }

    internal string[] SkillRelationshipAuthorities(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT relationship_authority FROM local_workspace_nodes WHERE session_id=$session AND source_kind='skill_invocation' ORDER BY node_id;";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        using var reader = command.ExecuteReader();
        var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray();
    }

    internal string[] SkillParentEdgeAuthorities(string sessionKey)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.relationship_authority FROM local_workspace_node_edges e JOIN local_workspace_nodes n ON n.node_id=e.node_id WHERE n.session_id=$session AND n.source_kind='skill_invocation' AND e.relation_kind='parent' ORDER BY e.node_id;";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray();
    }

    internal int UnknownRelationshipGroupCount(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=$session AND source_kind='unknown_relation_group';";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal int DetailNodeCount(string sessionKey)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_workspace_nodes WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal void AddGenericEvents(string sessionKey, int count)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) VALUES($event,$session,$run,'copilot-sdk','synthetic',$source,'event','2026-08-24T00:00:00.0000000+00:00','not_captured');";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        using (var run = connection.CreateCommand())
        {
            run.Transaction = transaction;
            run.CommandText = "SELECT run_id FROM session_runs WHERE session_id=$session ORDER BY run_id LIMIT 1;";
            run.Parameters.AddWithValue("$session", sessions[sessionKey]);
            command.Parameters.AddWithValue("$run", Assert.IsType<string>(run.ExecuteScalar()));
        }
        var eventParameter = command.Parameters.Add("$event", SqliteType.Text);
        var sourceParameter = command.Parameters.Add("$source", SqliteType.Text);
        for (var index = 0; index < count; index++)
        {
            eventParameter.Value = Guid.CreateVersion7().ToString("D");
            sourceParameter.Value = $"boundary-{Guid.NewGuid():N}";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal void MovePersistedSkillBeforeRawChildren(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE local_workspace_nodes SET start_utc_ticks=0,end_utc_ticks=0,duration_ms=0,time_authority='recorded',source_ordinal=0 WHERE session_id=$session AND source_kind='skill_invocation';";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        Assert.True(command.ExecuteNonQuery() > 0);
    }

    internal void DeleteRawSkillNodes(string sessionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM local_workspace_nodes WHERE session_id=$session AND source_kind='session_event' AND source_identity IN (SELECT event_id FROM session_events WHERE session_id=$session AND type='skill.invoked');";
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        Assert.True(command.ExecuteNonQuery() > 0);
    }

    internal void RefreshWorkspaceProjection(string sessionKey)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        LocalWorkspaceProjectionStore.RefreshSessions(connection, transaction, [sessions[sessionKey]], readAt, authority);
        transaction.Commit();
    }

    internal void CorruptExplicitParent(string sessionKey, string defect)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = defect switch
        {
            "missing" => "DELETE FROM session_events WHERE session_id=$session AND source_event_id=(SELECT source_parent_event_id FROM skill_invocation_snapshots WHERE session_id=$session LIMIT 1);",
            "cross-run" => "INSERT INTO session_runs(run_id,session_id,source_surface,status) VALUES($other,$session,'copilot-sdk','completed'); UPDATE session_events SET run_id=$other WHERE session_id=$session AND source_event_id=(SELECT source_parent_event_id FROM skill_invocation_snapshots WHERE session_id=$session LIMIT 1);",
            "cross-adapter" => "UPDATE session_events SET source_adapter='other-sdk-stream' WHERE session_id=$session AND source_event_id=(SELECT source_parent_event_id FROM skill_invocation_snapshots WHERE session_id=$session LIMIT 1);",
            "ambiguous" => "INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state) SELECT $event,session_id,run_id,source_surface,'other-sdk-stream',source_event_id,type,occurred_at,content_state FROM session_events WHERE session_id=$session AND source_event_id=(SELECT source_parent_event_id FROM skill_invocation_snapshots WHERE session_id=$session LIMIT 1);",
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        command.Parameters.AddWithValue("$session", sessions[sessionKey]); command.Parameters.AddWithValue("$other", Guid.CreateVersion7().ToString("D")); command.Parameters.AddWithValue("$event", Guid.CreateVersion7().ToString("D")); command.ExecuteNonQuery();
    }

    internal void AssertSdkAuthorized(string sessionKey)
    {
        var write = latestWrites[sessionKey];
        var result = new SkillProjectionReadService(DatabasePath, authority)
            .TryAcquireCurrentSdkClaimAuthorization(Guid.Parse(sessions[sessionKey]), write.SnapshotId, new FixedTimeProvider(readAt));
        Assert.Equal(SkillRegistryCurrentAuthorizationOutcome.Acquired, result.Outcome);
        result.Authorization?.Dispose();
    }

    internal void AssertCurrent(IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> results, string key, int count, string name)
    {
        var value = results[sessions[key]];
        Assert.Equal("current", value.State);
        Assert.Equal(count, value.InvocationCount);
        Assert.Contains(value.SearchFacts, fact => fact.SkillName == name);
    }

    internal void AssertPending(IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> results, string key)
    {
        var value = results[sessions[key]];
        Assert.Equal("certification_pending", value.State);
        Assert.Null(value.InvocationCount);
        Assert.Empty(value.SearchFacts);
    }

    internal void AssertAbsent(IReadOnlyDictionary<string, SkillProjectionCurrentInvocationProjection> results, params string[] keys)
    {
        foreach (var key in keys) Assert.False(results.ContainsKey(sessions[key]), key);
    }

    private SessionSkillInvocationWrite NewWrite(string sessionKey, string name, string sourceVersion, string state, string reason, bool expired, string? sourceParentEventId = null)
    {
        var available = state == "available";
        return new(
            "copilot-sdk-stream", "copilot-sdk", Guid.NewGuid().ToString("D"), sourceParentEventId, sessionKey, sessionKey + "-run", false,
            WrittenAt, sourceVersion, "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1", "github-copilot-sdk.skill-invoked.normalize.v2", "github-copilot-sdk.skill-invoked.v1",
            Fingerprint, "{\"skill\":\"demo\"}"u8.ToArray(), state, reason, available ? name : null,
            available ? "project" : null, available ? "user-invoked" : null, available ? new string('b', 64) : null,
            available ? 7 : null, available ? new string('c', 64) : null, available ? 12 : null,
            Guid.CreateVersion7(), Guid.CreateVersion7(), available ? Guid.CreateVersion7() : null,
            Guid.CreateVersion7(), Guid.CreateVersion7(), WrittenAt, expired ? WrittenAt.AddMinutes(30) : WrittenAt.AddDays(90));
    }

    private void Commit(SessionSkillInvocationWrite write)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var outcome = SessionSkillInvocationParticipant.InsertOrVerify(
            connection, transaction, write, new LocalWorkspaceProjectionTransactionParticipant(authority), out _);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        transaction.Commit();
    }

    private void EnsureSession(string sessionKey)
    {
        if (sessions.ContainsKey(sessionKey)) return;
        var sessionId = Guid.CreateVersion7().ToString("D");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at) VALUES($id,'completed','full',$at,'not_captured',$at,$at);";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$at", WrittenAt.ToString("O"));
        command.ExecuteNonQuery();
        sessions[sessionKey] = sessionId;
    }

    private string ResolveSession(string nativeSessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM session_native_ids WHERE native_session_id=$native;";
        command.Parameters.AddWithValue("$native", nativeSessionId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private void SeedOtel(string sessionKey, string skillName, string traceId, string spanId)
    {
        var ordinal = Interlocked.Increment(ref otelOrdinal);
        var fallbackRunId = Guid.CreateVersion7().ToString("D");
        var eventId = Guid.CreateVersion7().ToString("D");
        using var connection = Open();
        using (var foreignKeys = connection.CreateCommand()) { foreignKeys.CommandText = "PRAGMA foreign_keys=OFF;"; foreignKeys.ExecuteNonQuery(); }
        using var command = connection.CreateCommand();
        var sessionOwnerSql = sdkAuthorityInstalled
            ? """
              INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status)
              SELECT $fallback_run,$session,'copilot-cli',$trace,'completed'
              WHERE NOT EXISTS(SELECT 1 FROM skill_invocation_snapshots WHERE session_id=$session);
              INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
              SELECT $event,$session,COALESCE((SELECT run_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY created_at DESC,snapshot_id DESC LIMIT 1),$fallback_run),
                     COALESCE((SELECT source_surface FROM session_runs WHERE run_id=(SELECT run_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY created_at DESC,snapshot_id DESC LIMIT 1)),'copilot-cli'),
                     $trace,'otel-exact',$trace||'/'||$span,'otel.span',$at,'not_captured'
              WHERE NOT EXISTS(SELECT 1 FROM session_events WHERE session_id=$session AND source_adapter='otel-exact' AND source_event_id=$trace||'/'||$span);
              """
            : """
              INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status)
              VALUES($fallback_run,$session,'copilot-cli',$trace,'completed');
              INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES($event,$session,$fallback_run,'copilot-cli',$trace,'otel-exact',$trace||'/'||$span,'otel.span',$at,'not_captured');
              """;
        command.CommandText = """
            INSERT OR IGNORE INTO source_trace_compatibility_revisions(trace_id,current_revision,current_effective_state,current_exact_version,updated_at)
            VALUES($trace,7,'resolved','1.0.65',$at);
            INSERT INTO skill_projection_generations(trace_id,compatibility_revision,input_frontier_sha256,projector_version,lifecycle,created_at,updated_at)
            SELECT $trace,7,$digest,'matrix-v1','current',$at,$at
            WHERE NOT EXISTS(SELECT 1 FROM skill_projection_trace_heads WHERE trace_id=$trace);
            INSERT INTO skill_projection_trace_heads(trace_id,desired_generation_id,current_generation_id,updated_at)
            VALUES($trace,(SELECT generation_id FROM skill_projection_generations WHERE trace_id=$trace AND lifecycle='current'),(SELECT generation_id FROM skill_projection_generations WHERE trace_id=$trace AND lifecycle='current'),$at)
            ON CONFLICT(trace_id) DO NOTHING;
            INSERT INTO skill_projection_invocations(generation_id,source_arm,raw_record_id,trace_id,span_id,span_ordinal,session_id,skill_name,skill_source,invocation_trigger,source_application_version,projected_at)
            VALUES((SELECT current_generation_id FROM skill_projection_trace_heads WHERE trace_id=$trace),'otel_trace_span',$raw,$trace,$span,$ordinal,$session,$skill,'project','user-invoked','1.0.65',$at);
            """ + sessionOwnerSql;
        command.Parameters.AddWithValue("$trace", traceId);
        command.Parameters.AddWithValue("$span", spanId);
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        command.Parameters.AddWithValue("$skill", skillName);
        command.Parameters.AddWithValue("$raw", 100000 + ordinal);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$digest", new string(traceId[0], 64));
        command.Parameters.AddWithValue("$at", WrittenAt.ToString("O"));
        command.Parameters.AddWithValue("$fallback_run", fallbackRunId);
        command.Parameters.AddWithValue("$event", eventId);
        command.ExecuteNonQuery();
        using var restoreForeignKeys = connection.CreateCommand(); restoreForeignKeys.CommandText = "PRAGMA foreign_keys=ON;"; restoreForeignKeys.ExecuteNonQuery();
    }

    private void BindLatestSdkProducer(string sessionKey, string traceId, string spanId)
    {
        using var connection = Open();
        var triggerDefinitions = new List<string>();
        using (var triggers = connection.CreateCommand())
        {
            triggers.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name IN ('skill_invocation_snapshot_rows_update_rejected','skill_invocation_snapshot_session_event_update_rejected','skill_invocation_snapshot_receipts_update_rejected','skill_projection_sdk_claims_update_rejected') ORDER BY name;";
            using var reader = triggers.ExecuteReader();
            while (reader.Read()) triggerDefinitions.Add(reader.GetString(0));
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS skill_invocation_snapshot_rows_update_rejected;
            DROP TRIGGER IF EXISTS skill_invocation_snapshot_session_event_update_rejected;
            DROP TRIGGER IF EXISTS skill_invocation_snapshot_receipts_update_rejected;
            DROP TRIGGER IF EXISTS skill_projection_sdk_claims_update_rejected;
            UPDATE skill_invocation_snapshots SET trace_id=$trace,span_id=$span WHERE snapshot_id=(SELECT snapshot_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY created_at DESC,snapshot_id DESC LIMIT 1);
            UPDATE session_events SET trace_id=$trace WHERE event_id=(SELECT event_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY created_at DESC,snapshot_id DESC LIMIT 1);
            UPDATE session_runs SET trace_id=$trace WHERE run_id=(SELECT run_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY created_at DESC,snapshot_id DESC LIMIT 1);
            UPDATE skill_projection_sdk_claims SET producer_trace_id=$trace,producer_span_id=$span WHERE claim_id=(SELECT claim_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY created_at DESC,snapshot_id DESC LIMIT 1);
            """;
        command.Parameters.AddWithValue("$trace", traceId);
        command.Parameters.AddWithValue("$span", spanId);
        command.Parameters.AddWithValue("$session", sessions[sessionKey]);
        command.ExecuteNonQuery();
        RecomputeLatestReceipt(connection, latestWrites[sessionKey], traceId, spanId);
        foreach (var definition in triggerDefinitions)
        {
            using var restore = connection.CreateCommand();
            restore.CommandText = definition;
            restore.ExecuteNonQuery();
        }
    }

    private static void RecomputeLatestReceipt(SqliteConnection connection, SessionSkillInvocationWrite write, string traceId, string spanId)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT payload_sha256,content_document_sha256 FROM skill_invocation_snapshots WHERE snapshot_id=$snapshot;";
        read.Parameters.AddWithValue("$snapshot", write.SnapshotId.ToString("D"));
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        var payloadSha256 = reader.GetString(0);
        var documentSha256 = reader.GetString(1);
        reader.Close();
        var input = new SkillInvocationSnapshotReceiptFingerprintInput(
            write.SourceAdapter, write.SourceEventId, write.SourceSurface, write.NativeSessionId, write.RunNativeId, write.SourceParentEventId,
            write.SourceEphemeral, traceId, spanId, write.OccurredAt, write.SourceApplicationVersion, write.AdapterVersion,
            write.NormalizationVersion, write.PayloadSchema, write.SchemaFingerprint, payloadSha256, checked((ulong)write.PayloadTokenUtf8.Length),
            write.State, write.Reason, write.Name, write.Source, write.Trigger, write.BodySha256, (ulong?)write.BodyUtf8Bytes,
            write.DefinitionPathSha256, (ulong?)write.DefinitionPathUtf8Bytes, documentSha256);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE skill_invocation_snapshot_receipts SET request_fingerprint_sha256=$fingerprint WHERE snapshot_id=$snapshot;";
        update.Parameters.AddWithValue("$fingerprint", SkillInvocationSnapshotReceiptFingerprint.Compute(input));
        update.Parameters.AddWithValue("$snapshot", write.SnapshotId.ToString("D"));
        update.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (ownsDirectory)
            Directory.Delete(directory, recursive: true);
    }

    private sealed class MatrixRegistryAuthority(bool available) : ISkillRegistryGenerationAuthority
    {
        internal int CaptureCount { get; private set; }
        internal int LeaseCount { get; private set; }
        internal int VerifyCount { get; private set; }
        internal int TupleAuthorizationCount { get; private set; }
        internal void ResetObservations() => CaptureCount = LeaseCount = VerifyCount = TupleAuthorizationCount = 0;
        public ISkillRegistryGenerationCapture? CaptureGeneration()
        {
            CaptureCount++;
            return available ? new Capture() : null;
        }
        public bool TryAcquireGenerationReadLease(ISkillRegistryGenerationCapture capture, [NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            LeaseCount++;
            lease = available ? new Lease() : null;
            return lease is not null;
        }
        public bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease)
        {
            VerifyCount++;
            return available;
        }
        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple)
        {
            TupleAuthorizationCount++;
            return tuple.SourceApplicationVersion == "1.0.65";
        }
        private sealed class Capture : ISkillRegistryGenerationCapture { }
        private sealed class Lease : ISkillRegistryGenerationLease { public void Dispose() { } }
    }

    private sealed class NoOpWorkspaceParticipant : ILocalWorkspaceProjectionTransactionParticipant
    {
        internal static NoOpWorkspaceParticipant Instance { get; } = new();
        public void RefreshSessions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyCollection<string> sessionIds, DateTimeOffset now) { }
        public void CompleteSessionEventContentDeletion(SqliteConnection connection, SqliteTransaction transaction, string sourceItemId, DateTimeOffset now) { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

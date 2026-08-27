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

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProjectionGenerationTests_CurrentInvocationMatrix
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualRegistryPublicationCommitsOrRollsBackGenerationSensitiveV4SkillRows(bool injectFailure)
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
    public void SdkPublicationParticipantRefreshesCanonicalV4NodesInsideOwningCommitAndRollback()
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
        Assert.Equal(0, fixture.CountDetailSkillNodes("pending"));
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
    public async Task PersistedSqliteMatrix_TimeAdmittedNodeParticipatesInNodeBound()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedMismatchedPair("crossing", "Needle-Skill", "71", "81", "72", "82");
        fixture.RefreshWorkspace();
        fixture.AddGenericEvents("crossing", 4096 - fixture.DetailNodeCount("crossing"));
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
    private long otelOrdinal;

    internal CurrentInvocationProjectionFixture(bool registryAvailable = true, string? databasePath = null)
    {
        directory = databasePath is null
            ? Path.Combine(Path.GetTempPath(), $"skill-current-matrix-{Guid.NewGuid():N}")
            : Path.GetDirectoryName(databasePath)!;
        ownsDirectory = databasePath is null;
        Directory.CreateDirectory(directory);
        DatabasePath = databasePath ?? Path.Combine(directory, "monitor.sqlite");
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
        SkillInvocationSnapshotSchemaV1.Ensure(install, installTransaction);
        LocalRepositoryCatalogSchemaV1.Ensure(install, installTransaction);
        LocalArchiveSchemaV1.Ensure(install, installTransaction);
        installTransaction.Commit();
        LocalWorkspaceProjectionSchemaV1.Ensure(install, WrittenAt);
        authority = new MatrixRegistryAuthority(registryAvailable);
    }

    internal string DatabasePath { get; }

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
        string? parentNodeId = null)
    {
        var clock = new FixedTimeProvider(readAt);
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            detailContributor: new LocalWorkspaceSessionDetailSnapshotContributor(
                registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority);
        return service.ReadDetailAsync(new(kind, sessions[sessionKey], ExecutionId: executionId, NodeId: nodeId, ParentNodeId: parentNodeId), CancellationToken.None);
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
            run.CommandText = "SELECT run_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY snapshot_id LIMIT 1;";
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
            INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status)
            SELECT $fallback_run,$session,'copilot-cli',$trace,'completed'
            WHERE NOT EXISTS(SELECT 1 FROM skill_invocation_snapshots WHERE session_id=$session);
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
            SELECT $event,$session,COALESCE((SELECT run_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY created_at DESC,snapshot_id DESC LIMIT 1),$fallback_run),
                   COALESCE((SELECT source_surface FROM session_runs WHERE run_id=(SELECT run_id FROM skill_invocation_snapshots WHERE session_id=$session ORDER BY created_at DESC,snapshot_id DESC LIMIT 1)),'copilot-cli'),
                   $trace,'otel-exact',$trace||'/'||$span,'otel.span',$at,'not_captured'
            WHERE NOT EXISTS(SELECT 1 FROM session_events WHERE session_id=$session AND source_adapter='otel-exact' AND source_event_id=$trace||'/'||$span);
            """;
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

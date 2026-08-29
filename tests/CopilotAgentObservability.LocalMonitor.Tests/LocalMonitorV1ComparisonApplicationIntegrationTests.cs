using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1ComparisonApplicationIntegrationTests
{
    [Theory]
    [InlineData("source", 17)]
    [InlineData("adapter", 17)]
    [InlineData("source", 63)]
    [InlineData("adapter", 63)]
    [InlineData("source", 64)]
    [InlineData("adapter", 64)]
    public async Task ProductionPreviewAndCreateEnforceIndependentVersionBoundaries(string dimension, int count)
    {
        using var db = new Database();
        using var storeDb = new Database(); storeDb.Initialize();
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002";
        db.InitializeProductionScope(a, b, archiveSecond: false);
        db.SeedVersions(a, dimension, count);
        var clock = new FixedClock();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(db.Path,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority, timeProvider: clock);
        var store = new SqliteLocalComparisonStore(storeDb.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(service, store, clock, cursorKey: new byte[32]);
        var body = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":true}}");

        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, body, "", default);
        if (count == 64)
        {
            Assert.Equal(409, preview.StatusCode);
            Assert.Equal("{\"error\":\"workspace_too_large\"}", Encoding.UTF8.GetString(preview.Entity));
            return;
        }

        Assert.Equal(200, preview.StatusCode);
        using var json = JsonDocument.Parse(preview.Entity);
        var metadata = json.RootElement.GetProperty("included")[0].GetProperty("metadata");
        var versions = metadata.GetProperty(dimension == "source" ? "source_application_versions" : "adapter_versions").EnumerateArray().Select(static item => item.GetString()).ToArray();
        Assert.Equal(count, versions.Length);
        Assert.Contains("V1+meta", versions);
        Assert.All(versions, static value => Assert.InRange(value!.Length, 1, 256));
        Assert.Empty(metadata.GetProperty(dimension == "source" ? "adapter_versions" : "source_application_versions").EnumerateArray());
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":true,\"selection_sha256\":\"{json.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{json.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var created = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        Assert.True(created.StatusCode == 201, Encoding.UTF8.GetString(created.Entity));
        var recreated = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        Assert.Equal(201, recreated.StatusCode);
        using var createdJson = JsonDocument.Parse(created.Entity); using var recreatedJson = JsonDocument.Parse(recreated.Entity);
        var frozen = store.Read(LocalComparisonInputProjectionTests.RepositoryId, createdJson.RootElement.GetProperty("comparison_id").GetString()!, default).Snapshot!;
        var refrozen = store.Read(LocalComparisonInputProjectionTests.RepositoryId, recreatedJson.RootElement.GetProperty("comparison_id").GetString()!, default).Snapshot!;
        var row = frozen.Results.Single(item => item.RowKey == (dimension == "source" ? "source_versions" : "adapter_versions"));
        var rerow = refrozen.Results.Single(item => item.RowKey == row.RowKey);
        var summary = frozen.Evidence.Single(item => item.ResultOrdinal == row.ResultOrdinal && item.Cohort == "a").ConsumedValue;
        var resummary = refrozen.Evidence.Single(item => item.ResultOrdinal == rerow.ResultOrdinal && item.Cohort == "a").ConsumedValue;
        Assert.Matches($"^set-sha256:[0-9a-f]{{64}}:count:{count}$", summary);
        Assert.Equal(summary, resummary);
    }

    [Fact]
    public async Task ProductionInvalidLegacyVersionTokenFailsOnlyThatTargetClosed()
    {
        using var db = new Database();
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002";
        db.InitializeProductionScope(a, b, archiveSecond: false);
        db.SeedVersion(a, "source", "V" + new string('x', 256));
        var clock = new FixedClock(); var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(db.Path,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority), SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock), skillRegistryAuthority: authority, timeProvider: clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(service, new SqliteLocalComparisonStore(db.Path, clock), clock, cursorKey: new byte[32]);
        var body = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":true}}");

        var response = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, body, "", default);
        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Entity);
        Assert.Equal("projection_unavailable", json.RootElement.GetProperty("excluded")[0].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("excluded")[0].GetProperty("metadata").GetProperty("source_application_versions").ValueKind);
    }

    [Fact]
    public async Task ProductionInitialSessionContributorFailureBecomesTargetProjectionExclusion()
    {
        using var db = new Database();
        const string unavailable = "018f0000-0000-7000-8000-000000000001", available = "018f0000-0000-7000-8000-000000000002";
        db.InitializeProductionScope(unavailable, available);
        db.CorruptProjection(unavailable);
        var clock = new FixedClock();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(db.Path,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority, timeProvider: clock);
        var store = new SqliteLocalComparisonStore(db.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(service, store, clock, cursorKey: new byte[32]);
        var body = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{unavailable}\"],\"b\":[\"{available}\"]}},\"include_archived\":true}}");

        var response = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, body, "", default);

        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Entity);
        Assert.Equal("projection_unavailable", json.RootElement.GetProperty("excluded")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ProductionScopeAndArchiveFactsDriveArchivePrecedenceAndCreate()
    {
        using var db = new Database();
        const string active = "018f0000-0000-7000-8000-000000000001", archived = "018f0000-0000-7000-8000-000000000002";
        db.InitializeProductionScope(active, archived);
        var clock = new FixedClock();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            db.Path,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority,
            timeProvider: clock);
        var store = new SqliteLocalComparisonStore(db.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(service, store, clock, cursorKey: new byte[32]);
        var uncheckedBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{active}\"],\"b\":[\"{archived}\"]}},\"include_archived\":false}}");
        var checkedBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{active}\"],\"b\":[\"{archived}\"]}},\"include_archived\":true}}");

        var uncheckedPreview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, uncheckedBody, "", default);
        using (var json = JsonDocument.Parse(uncheckedPreview.Entity))
        {
            var excluded = json.RootElement.GetProperty("excluded")[0];
            Assert.Equal("session_archived", excluded.GetProperty("reason").GetString());
            var metadata = excluded.GetProperty("metadata");
            Assert.Equal("archived", metadata.GetProperty("archive_state").GetString());
            Assert.Equal(1, metadata.GetProperty("session_archive_revision").GetInt64());
            Assert.Equal("active", metadata.GetProperty("assigned_repository_archive_state").GetString());
            Assert.Equal(0, metadata.GetProperty("assigned_repository_archive_revision").GetInt64());
            Assert.Equal("session_archived", metadata.GetProperty("archive_exclusion_reason").GetString());
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("source").ValueKind);
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("model").ValueKind);
            Assert.Equal(1, metadata.GetProperty("projection_version").GetInt64());
            Assert.Equal("partial", metadata.GetProperty("completeness").GetString());
            Assert.NotEmpty(metadata.GetProperty("metric_coverage").EnumerateArray());
            Assert.Empty(metadata.GetProperty("source_application_versions").EnumerateArray());
            Assert.Empty(metadata.GetProperty("adapter_versions").EnumerateArray());
            Assert.True(metadata.GetProperty("session_revision").GetInt64() > 0);
            Assert.Matches("^[0-9a-f]{64}$", metadata.GetProperty("projection_revision").GetString());
        }
        var checkedPreview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, checkedBody, "", default);
        using var checkedJson = JsonDocument.Parse(checkedPreview.Entity);
        Assert.True(checkedJson.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal([active, archived], checkedJson.RootElement.GetProperty("included").EnumerateArray().Select(item => item.GetProperty("session_id").GetString()));

        db.ArchiveRepository();
        var repositoryPreview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, checkedBody, "", default);
        using var repositoryJson = JsonDocument.Parse(repositoryPreview.Entity);
        Assert.True(repositoryJson.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal([active, archived], repositoryJson.RootElement.GetProperty("included").EnumerateArray().Select(item => item.GetProperty("session_id").GetString()));
        var archivedMetadata = repositoryJson.RootElement.GetProperty("included")[1].GetProperty("metadata");
        Assert.Equal("session_archived", archivedMetadata.GetProperty("archive_exclusion_reason").GetString());
        Assert.Equal("archived", archivedMetadata.GetProperty("archive_state").GetString());
        Assert.Equal("archived", archivedMetadata.GetProperty("assigned_repository_archive_state").GetString());
        Assert.Equal(1, archivedMetadata.GetProperty("session_archive_revision").GetInt64());
        Assert.Equal(1, archivedMetadata.GetProperty("assigned_repository_archive_revision").GetInt64());

    }

    [Fact]
    public async Task ProductionSqlitePreviewCreatePersistedRowsMatchExactBytes()
    {
        using var db = new Database();
        using var storeDb = new Database(); storeDb.Initialize();
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002";
        db.InitializeProductionNamedComparison(a, b);
        var clock = new FixedClock(new(2026, 8, 26, 0, 10, 0, TimeSpan.Zero));
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            db.Path,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority,
            timeProvider: clock);
        var store = new SqliteLocalComparisonStore(storeDb.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(
            service,
            store,
            clock,
            _ => "018f0000-0000-7000-8000-000000000010",
            new byte[32]);
        var previewBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false}}");
        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        Assert.True(preview.StatusCode == 200, Encoding.UTF8.GetString(preview.Entity));
        using var previewJson = JsonDocument.Parse(preview.Entity);
        Assert.True(previewJson.RootElement.GetProperty("valid").GetBoolean(), Encoding.UTF8.GetString(preview.Entity));
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false,\"selection_sha256\":\"{previewJson.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{previewJson.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var created = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        Assert.True(created.StatusCode == 201, Encoding.UTF8.GetString(created.Entity));

        var frozen = store.Read(LocalComparisonInputProjectionTests.RepositoryId, "018f0000-0000-7000-8000-000000000010", default).Snapshot!;
        var tool = Assert.Single(frozen.Results, result => result.RowKind == "tool" && result.Values.ToDictionary()["display_name"] == "Read");
        Assert.Equal("2", tool.Values.ToDictionary()["a_call_count_total"]);
        Assert.Equal("1", tool.Values.ToDictionary()["b_call_count_total"]);
        var toolEvidence = frozen.Evidence.Where(item => item.ResultOrdinal == tool.ResultOrdinal && item.FieldKey == "call_count").ToArray();
        Assert.Equal(3, toolEvidence.Count(item => item.SourceKind == "workspace_node"));
        Assert.Equal(3, toolEvidence.Count(item => item.SourceKind == "otel_span"));
        Assert.All(toolEvidence.Where(item => item.SourceKind == "workspace_node"), item =>
        {
            Assert.Null(item.TraceId);
            Assert.Null(item.SpanId);
            Assert.Null(item.EventId);
        });
        Assert.All(toolEvidence.Where(item => item.SourceKind == "otel_span"), item =>
        {
            Assert.NotNull(item.TraceId);
            Assert.NotNull(item.SpanId);
            Assert.NotNull(item.EventId);
        });

        var restarted = new LocalMonitorV1ComparisonProductionApplication(
            new ThrowingInput(),
            new SqliteLocalComparisonStore(storeDb.Path, clock),
            clock,
            cursorKey: new byte[32]);
        var subagent = Assert.Single(frozen.Results, result => result.RowKind == "subagent" && result.Values.ToDictionary()["display_name"] == "識別名なし");
        Assert.Equal("識別名なし", subagent.Values.ToDictionary()["sort_key"]);
        var subagentRows = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, "018f0000-0000-7000-8000-000000000010", ReadOnlyMemory<byte>.Empty, "?family=subagent&limit=1", default);
        Assert.Equal(200, subagentRows.StatusCode);
        using (var subagentJson = JsonDocument.Parse(subagentRows.Entity))
        {
            var persistedSubagent = Assert.Single(subagentJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("識別名なし", persistedSubagent.GetProperty("display_name").GetString());
            Assert.Equal("識別名なし", persistedSubagent.GetProperty("values").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "sort_key").GetProperty("value").GetString());
        }
        var rows = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, "018f0000-0000-7000-8000-000000000010", ReadOnlyMemory<byte>.Empty, "?family=tool&q=Read&limit=1", default);
        var expected = (await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "LocalMonitorV1Comparison", "local-monitor-comparison-production-rows.response.json")))
            .AsSpan().TrimEnd([(byte)'\r', (byte)'\n']).ToArray();

        Assert.Equal(expected, rows.Entity);
    }

    [Fact]
    public async Task ProductionSqliteRepeatedSkillPersistsOneSummedRowAcrossRestart()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnly("skill-a", "review-skill");
        fixture.SeedSdkOnly("skill-a", "review-skill");
        fixture.SeedSdkOnly("skill-b", "review-skill");
        fixture.RefreshWorkspace();
        var a = fixture.SessionId("skill-a");
        var b = fixture.SessionId("skill-b");
        AssignRepository(fixture.DatabasePath, a, b);
        using var storeDb = new Database(); storeDb.Initialize();
        var clock = new FixedClock(new(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            fixture.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: fixture.RegistryAuthority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: fixture.RegistryAuthority, timeProvider: clock),
            skillRegistryAuthority: fixture.RegistryAuthority,
            timeProvider: clock);
        var store = new SqliteLocalComparisonStore(storeDb.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(service, store, clock, _ => ComparisonId, CursorKey);
        var previewBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false}}");
        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        using var previewJson = JsonDocument.Parse(preview.Entity);
        Assert.True(previewJson.RootElement.GetProperty("valid").GetBoolean(), Encoding.UTF8.GetString(preview.Entity));
        Assert.True(previewJson.RootElement.GetProperty("valid").GetBoolean(), Encoding.UTF8.GetString(preview.Entity));
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false,\"selection_sha256\":\"{previewJson.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{previewJson.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var created = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        Assert.True(created.StatusCode == 201, Encoding.UTF8.GetString(created.Entity));

        var restarted = new LocalMonitorV1ComparisonProductionApplication(new ThrowingInput(), new SqliteLocalComparisonStore(storeDb.Path, clock), clock, cursorKey: CursorKey);
        var rows = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, ReadOnlyMemory<byte>.Empty, "?family=skill&q=review-skill&limit=1", default);
        using var rowsJson = JsonDocument.Parse(rows.Entity);
        var item = Assert.Single(rowsJson.RootElement.GetProperty("items").EnumerateArray());
        var values = item.GetProperty("values").EnumerateArray().ToDictionary(value => value.GetProperty("key").GetString()!, value => value.GetProperty("value").GetString()!);
        Assert.Equal("review-skill", item.GetProperty("display_name").GetString());
        Assert.Equal("2", values["a_invocation_count_total"]);
        Assert.Equal("1", values["b_invocation_count_total"]);
    }

    [Fact]
    public async Task ProductionSqliteNamedLifecycleFailureRetryAndTokensRemainExactAfterRestart()
    {
        using var db = new Database();
        using var storeDb = new Database(); storeDb.Initialize();
        const string a = SessionA, b = SessionB;
        db.InitializeProductionNamedComparison(a, b);
        db.SeedClaudeNamedSubagent(a);
        var clock = new FixedClock(new(2026, 8, 26, 0, 10, 0, TimeSpan.Zero));
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(db.Path,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority, timeProvider: clock);
        var store = new SqliteLocalComparisonStore(storeDb.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(service, store, clock, _ => ComparisonId, CursorKey);
        await CreateComparison(application);
        var restarted = new LocalMonitorV1ComparisonProductionApplication(new ThrowingInput(), new SqliteLocalComparisonStore(storeDb.Path, clock), clock, cursorKey: CursorKey);

        var toolRows = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, ReadOnlyMemory<byte>.Empty, "?family=tool&q=Read&limit=1", default);
        using var toolJson = JsonDocument.Parse(toolRows.Entity);
        var tool = Assert.Single(toolJson.RootElement.GetProperty("items").EnumerateArray());
        var toolValues = Values(tool);
        Assert.Equal("0", toolValues["a_failure_count_total"]);
        Assert.Equal("not_available", toolValues["a_retry_count_total"]);
        Assert.Contains("not_observed", toolValues["a_retry_count_unavailable_states"], StringComparison.Ordinal);

        var subagentRows = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, ReadOnlyMemory<byte>.Empty, "?family=subagent&limit=10", default);
        using var subagentJson = JsonDocument.Parse(subagentRows.Entity);
        var subagents = subagentJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var named = Assert.Single(subagents, item => item.GetProperty("display_name").GetString() == "reviewer");
        var unidentified = Assert.Single(subagents, item => item.GetProperty("display_name").GetString() == "識別名なし");
        var namedValues = Values(named);
        Assert.Equal("1", namedValues["a_start_count_total"]);
        Assert.Equal("1", namedValues["a_completed_count_total"]);
        Assert.Equal("not_available", namedValues["a_failed_count_total"]);
        Assert.Equal("not_available", namedValues["a_recorded_tokens_total"]);
        Assert.Contains("not_observed", namedValues["a_recorded_tokens_unavailable_states"], StringComparison.Ordinal);
        Assert.Equal("1", Values(unidentified)["a_start_count_total"]);
        Assert.Equal("1", Values(unidentified)["b_start_count_total"]);
    }

    [Fact]
    public async Task ProductionSqlitePositiveToolAggregateWithoutNamedDetailPersistsUnavailableNotZero()
    {
        using var db = new Database();
        using var storeDb = new Database(); storeDb.Initialize();
        db.InitializeProductionNamedComparison(SessionA, SessionB);
        db.HideNamedToolDetail(SessionA);
        var clock = new FixedClock(new(2026, 8, 26, 0, 10, 0, TimeSpan.Zero));
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(db.Path,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority, timeProvider: clock);
        var store = new SqliteLocalComparisonStore(storeDb.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(service, store, clock, _ => ComparisonId, CursorKey);
        await CreateComparison(application);
        var restarted = new LocalMonitorV1ComparisonProductionApplication(new ThrowingInput(), new SqliteLocalComparisonStore(storeDb.Path, clock), clock, cursorKey: CursorKey);
        var rows = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, ReadOnlyMemory<byte>.Empty, "?family=tool&q=Read&limit=1", default);
        using var json = JsonDocument.Parse(rows.Entity);
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        var values = Values(item);
        Assert.Equal("0", values["a_available_session_count"]);
        Assert.Equal("0", values["a_called_session_count"]);
        Assert.Equal("not_available", values["a_call_count_total"]);
        Assert.Contains("capture_gap", values["a_call_count_unavailable_states"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivedInclusionEvidencePreservesEachFrozenMembershipIndicatorAcrossRestart()
    {
        using var db = new Database(); db.Initialize();
        const string active = "018f0000-0000-7000-8000-000000000001", archived = "018f0000-0000-7000-8000-000000000002";
        var store = new SqliteLocalComparisonStore(db.Path, new FixedClock());
        var application = new LocalMonitorV1ComparisonProductionApplication(new FakeInput([Input(active), Input(archived, archived: true)]), store, new FixedClock(), cursorKey: new byte[32]);
        var previewBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{active}\"],\"b\":[\"{archived}\"]}},\"include_archived\":true}}");
        var uncheckedBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{active}\"],\"b\":[\"{archived}\"]}},\"include_archived\":false}}");
        var uncheckedPreview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, uncheckedBody, "", default);
        using (var uncheckedJson = JsonDocument.Parse(uncheckedPreview.Entity))
        {
            Assert.Equal(200, uncheckedPreview.StatusCode);
            Assert.Equal("session_archived", uncheckedJson.RootElement.GetProperty("excluded")[0].GetProperty("reason").GetString());
        }
        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        using var previewJson = JsonDocument.Parse(preview.Entity);
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{active}\"],\"b\":[\"{archived}\"]}},\"include_archived\":true,\"selection_sha256\":\"{previewJson.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{previewJson.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var created = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        using var createdJson = JsonDocument.Parse(created.Entity);
        var comparisonId = createdJson.RootElement.GetProperty("comparison_id").GetString()!;
        var frozen = store.Read(LocalComparisonInputProjectionTests.RepositoryId, comparisonId, default).Snapshot!;
        var archivedResult = frozen.Results.Single(result => result.RowKey == "archived_inclusion");
        var restarted = new LocalMonitorV1ComparisonProductionApplication(new ThrowingInput(), new SqliteLocalComparisonStore(db.Path, new FixedClock()), new FixedClock(), cursorKey: new byte[32]);

        var evidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, comparisonId, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={archivedResult.ResultOrdinal}&field_key=condition", default);
        using var evidenceJson = JsonDocument.Parse(evidence.Entity);
        var items = evidenceJson.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal([active, archived], items.Select(item => item.GetProperty("session_id").GetString()));
        Assert.Equal(["active", "session_archived"], items.Select(item => item.GetProperty("consumed_value").GetString()));
        Assert.All(items, item => Assert.Equal("included", item.GetProperty("state").GetString()));
        Assert.Equal([new string('1', 64), new string('2', 64)], items.Select(item => item.GetProperty("consumed_revision").GetString()));

        var archivedRepositoryApplication = new LocalMonitorV1ComparisonProductionApplication(new FakeInput([Input(active), Input(archived, archived: true)], repositoryArchived: true), store, new FixedClock(), cursorKey: new byte[32]);
        var repositoryPreview = await archivedRepositoryApplication.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        using var repositoryJson = JsonDocument.Parse(repositoryPreview.Entity);
        Assert.True(repositoryJson.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal([active, archived], repositoryJson.RootElement.GetProperty("included").EnumerateArray().Select(item => item.GetProperty("session_id").GetString()));
        var repositoryCreateBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{active}\"],\"b\":[\"{archived}\"]}},\"include_archived\":true,\"selection_sha256\":\"{repositoryJson.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{repositoryJson.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var repositoryCreated = await archivedRepositoryApplication.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, repositoryCreateBody, "", default);
        using var repositoryCreatedJson = JsonDocument.Parse(repositoryCreated.Entity);
        var repositoryFrozen = store.Read(LocalComparisonInputProjectionTests.RepositoryId, repositoryCreatedJson.RootElement.GetProperty("comparison_id").GetString()!, default).Snapshot!;
        var archiveValues = repositoryFrozen.Results.Single(result => result.RowKey == "archived_inclusion").Values.ToDictionary();
        Assert.Equal("1", archiveValues["a_assigned_repository_archived_count"]);
        Assert.Equal("1", archiveValues["b_direct_session_archived_count"]);
        Assert.Equal("1", archiveValues["b_assigned_repository_archived_count"]);
    }

    [Fact]
    public async Task WorkspaceTooLargeDetailFailureMapsToFixedTransportError()
    {
        using var db = new Database(); db.Initialize();
        var application = new LocalMonitorV1ComparisonProductionApplication(new DetailFailureInput("workspace_too_large"), new SqliteLocalComparisonStore(db.Path, new FixedClock()), new FixedClock(), cursorKey: new byte[32]);
        var body = Encoding.UTF8.GetBytes("{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{\"a\":[\"018f0000-0000-7000-8000-000000000001\"],\"b\":[\"018f0000-0000-7000-8000-000000000002\"]},\"include_archived\":false}");

        var response = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, body, "", default);

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("{\"error\":\"workspace_too_large\"}", Encoding.UTF8.GetString(response.Entity));
    }

    [Fact]
    public async Task PreviewKeepsUnavailableSourceAndModelMetadataNull()
    {
        using var db = new Database(); db.Initialize();
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002";
        var first = Input(a);
        var row = (LocalWorkspaceProjectionRow)first.Session.Session;
        first = first with { Session = first.Session with { Session = row with { Sources = new("source_unsupported", []), Models = new("not_observed", []) } } };
        var application = new LocalMonitorV1ComparisonProductionApplication(new FakeInput([first, Input(b)]), new SqliteLocalComparisonStore(db.Path, new FixedClock()), new FixedClock(), cursorKey: new byte[32]);
        var body = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false}}");

        var response = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, body, "", default);
        using var json = JsonDocument.Parse(response.Entity);
        var metadata = json.RootElement.GetProperty("included")[0].GetProperty("metadata");

        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("source").ValueKind);
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("model").ValueKind);
    }

    [Fact]
    public async Task CreatePersistsDistinctSnapshotsAndRestartReadsFrozenState()
    {
        using var db = new Database(); db.Initialize();
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002";
        var authority = new FakeInput([Input(a), Input(b)]); var store = new SqliteLocalComparisonStore(db.Path, new FixedClock());
        var application = new LocalMonitorV1ComparisonProductionApplication(authority, store, new FixedClock(), cursorKey: new byte[32]);
        var previewBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false}}");
        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        using var p = JsonDocument.Parse(preview.Entity); var selection = p.RootElement.GetProperty("selection_sha256").GetString(); var revision = p.RootElement.GetProperty("preview_revision").GetString();
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false,\"selection_sha256\":\"{selection}\",\"preview_revision\":\"{revision}\"}}");

        var first = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        var second = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        using var one = JsonDocument.Parse(first.Entity); using var two = JsonDocument.Parse(second.Entity); var id = one.RootElement.GetProperty("comparison_id").GetString()!;
        Assert.Equal(201, first.StatusCode); Assert.Equal(201, second.StatusCode); Assert.NotEqual(id, two.RootElement.GetProperty("comparison_id").GetString());

        var restarted = new LocalMonitorV1ComparisonProductionApplication(new ThrowingInput(), new SqliteLocalComparisonStore(db.Path, new FixedClock()), new FixedClock(), cursorKey: new byte[32]);
        var read = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Read, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "", default);
        Assert.Equal(200, read.StatusCode); Assert.Contains(a, Encoding.UTF8.GetString(read.Entity), StringComparison.Ordinal); Assert.Contains(b, Encoding.UTF8.GetString(read.Entity), StringComparison.Ordinal);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Read, "018f0000-0000-7000-8000-000000000099", id, ReadOnlyMemory<byte>.Empty, "", default)).StatusCode);
        var rows = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "?family=subagent&q=helper", default);
        Assert.Equal(200, rows.StatusCode); Assert.Contains("helper", Encoding.UTF8.GetString(rows.Entity), StringComparison.Ordinal);
        var frozen = store.Read(LocalComparisonInputProjectionTests.RepositoryId, id, default).Snapshot!;
        var storedRow = frozen.Results.Single(result => result.RowKind == "subagent");
        using (var rowsJson = JsonDocument.Parse(rows.Entity))
        {
            var serialized = rowsJson.RootElement.GetProperty("items")[0].GetProperty("values")
                .EnumerateArray()
                .Select(value => new KeyValuePair<string, string>(
                    value.GetProperty("key").GetString()!,
                    value.GetProperty("value").GetString()!))
                .ToArray();
            Assert.Equal(storedRow.Values, serialized);
        }
        var inputTokens = frozen.Results.Single(result => result.RowKey == "input_tokens");
        var scalarEvidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={inputTokens.ResultOrdinal}&field_key=median", default);
        using (var scalarJson = JsonDocument.Parse(scalarEvidence.Entity))
        {
            var items = scalarJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.Equal("10", item.GetProperty("consumed_value").GetString()));
            Assert.All(items, item => Assert.Equal(new string(item.GetProperty("session_id").GetString()![^1], 64), item.GetProperty("consumed_revision").GetString()));
        }
        var unavailableResult = frozen.Results.Single(result => result.RowKey == "model_turn_count");
        var unavailableEvidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={unavailableResult.ResultOrdinal}&field_key=median", default);
        using (var unavailableJson = JsonDocument.Parse(unavailableEvidence.Entity))
        {
            var items = unavailableJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.Equal("unavailable", item.GetProperty("state").GetString()));
            Assert.All(items, item => Assert.Equal("source_unsupported", item.GetProperty("unavailable_reason").GetString()));
            Assert.All(items, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("consumed_value").ValueKind));
        }
        var namedEvidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={storedRow.ResultOrdinal}&field_key=count", default);
        using (var namedJson = JsonDocument.Parse(namedEvidence.Entity))
        {
            Assert.All(namedJson.RootElement.GetProperty("items").EnumerateArray(), item =>
            {
                Assert.NotEqual(JsonValueKind.Null, item.GetProperty("consumed_value").ValueKind);
                var execution = item.GetProperty("execution_id").GetString();
                var node = item.GetProperty("node_id").GetString();
                Assert.Null(execution);
                Assert.NotNull(node);
                Assert.Equal($"/sessions/{item.GetProperty("session_id").GetString()}?node={node}", item.GetProperty("session_location").GetString());
            });
        }
        var acceptedFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["value"] = "input_tokens",
            ["available_count"] = "input_tokens",
            ["median"] = "input_tokens",
            ["minimum"] = "input_tokens",
            ["maximum"] = "input_tokens",
            ["total"] = "input_tokens",
            ["absolute_difference"] = "input_tokens",
            ["relative_difference_percent"] = "input_tokens",
            ["condition"] = "period",
            ["count"] = storedRow.RowKey,
            ["duration_ms"] = "session_duration",
            ["input_tokens"] = "input_tokens",
            ["output_tokens"] = "output_tokens",
            ["total_tokens"] = "total_tokens",
            ["cache_read"] = "cache_read_tokens",
            ["cache_creation"] = "cache_creation_tokens",
            ["new_input"] = "new_input_tokens",
            ["error_count"] = "error_count",
            ["retry_count"] = "retry_count",
        };
        foreach (var accepted in acceptedFields)
        {
            var acceptedResult = accepted.Key == "count"
                ? storedRow
                : frozen.Results.First(result => result.RowKey == accepted.Value);
            var response = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={acceptedResult.ResultOrdinal}&field_key={accepted.Key}", default);
            Assert.Equal(200, response.StatusCode);
            using var responseJson = JsonDocument.Parse(response.Entity);
            Assert.True(responseJson.RootElement.GetProperty("items").GetArrayLength() > 0, accepted.Key);
        }
        var period = frozen.Results.Single(result => result.RowKey == "period");
        var includedCount = frozen.Results.Single(result => result.RowKey == "included_session_count");
        var targetCount = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={includedCount.ResultOrdinal}&field_key=count", default);
        using (var targetJson = JsonDocument.Parse(targetCount.Entity))
        {
            Assert.Equal(200, targetCount.StatusCode);
            Assert.All(targetJson.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal("1", item.GetProperty("consumed_value").GetString()));
        }
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={includedCount.ResultOrdinal}&field_key=median", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={period.ResultOrdinal}&field_key=median", default)).StatusCode);
        var skillRow = frozen.Results.Single(result => result.RowKind == "skill");
        var toolRow = frozen.Results.Single(result => result.RowKind == "tool");
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={skillRow.ResultOrdinal}&field_key=count", default)).StatusCode);
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={toolRow.ResultOrdinal}&field_key=error_count", default)).StatusCode);
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={storedRow.ResultOrdinal}&field_key=total_tokens", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={skillRow.ResultOrdinal}&field_key=error_count", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={toolRow.ResultOrdinal}&field_key=total_tokens", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={storedRow.ResultOrdinal}&field_key=retry_count", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal={inputTokens.ResultOrdinal}&field_key=count", default)).StatusCode);
        Assert.Equal(404, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "?result_ordinal=999999&field_key=value", default)).StatusCode);
        var evidence = await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, "?result_ordinal=1&limit=1", default);
        using var evidenceJson = JsonDocument.Parse(evidence.Entity); var cursor = evidenceJson.RootElement.GetProperty("next_cursor").GetString(); Assert.NotNull(cursor);
        Assert.Equal(200, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal=1&after={cursor}&limit=1", default)).StatusCode);
        Assert.Equal(400, (await restarted.ExecuteAsync(LocalMonitorV1ComparisonOperation.Evidence, LocalComparisonInputProjectionTests.RepositoryId, id, ReadOnlyMemory<byte>.Empty, $"?result_ordinal=1&after={cursor}x&limit=1", default)).StatusCode);
    }

    [Theory]
    [InlineData("rows", "invalid_unknown", 400, "invalid_cursor")]
    [InlineData("rows", "invalid_mismatch", 400, "invalid_cursor")]
    [InlineData("rows", "invalid_expired", 400, "invalid_cursor")]
    [InlineData("rows", "wrong_binding_unknown", 400, "invalid_cursor")]
    [InlineData("rows", "wrong_binding_mismatch", 400, "invalid_cursor")]
    [InlineData("rows", "wrong_binding_expired", 400, "invalid_cursor")]
    [InlineData("rows", "valid_unknown", 404, "comparison_not_found")]
    [InlineData("rows", "valid_mismatch", 404, "comparison_not_found")]
    [InlineData("rows", "valid_expired", 410, "comparison_expired")]
    [InlineData("evidence", "invalid_unknown", 400, "invalid_cursor")]
    [InlineData("evidence", "invalid_mismatch", 400, "invalid_cursor")]
    [InlineData("evidence", "invalid_expired", 400, "invalid_cursor")]
    [InlineData("evidence", "wrong_binding_unknown", 400, "invalid_cursor")]
    [InlineData("evidence", "wrong_binding_mismatch", 400, "invalid_cursor")]
    [InlineData("evidence", "wrong_binding_expired", 400, "invalid_cursor")]
    [InlineData("evidence", "valid_unknown", 404, "comparison_not_found")]
    [InlineData("evidence", "valid_mismatch", 404, "comparison_not_found")]
    [InlineData("evidence", "valid_expired", 410, "comparison_expired")]
    public async Task CursorAuthenticationPrecedesLookupRepositoryBindingAndExpiry(string operationName, string scenario, int expectedStatus, string expectedError)
    {
        var operation = operationName == "rows" ? LocalMonitorV1ComparisonOperation.Rows : LocalMonitorV1ComparisonOperation.Evidence;
        using var db = new Database(); db.Initialize();
        var initialClock = new FixedClock();
        var store = new SqliteLocalComparisonStore(db.Path, initialClock);
        var application = new LocalMonitorV1ComparisonProductionApplication(new FakeInput([Input(SessionA), Input(SessionB)]), store, initialClock, _ => ComparisonId, CursorKey);
        await CreateComparison(application);
        var snapshot = store.Read(LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, default).Snapshot!;
        var evidenceResultOrdinal = snapshot.Evidence[0].ResultOrdinal;
        var requestedRepository = scenario.EndsWith("mismatch", StringComparison.Ordinal) ? OtherRepositoryId : LocalComparisonInputProjectionTests.RepositoryId;
        var requestedComparison = scenario.EndsWith("unknown", StringComparison.Ordinal) ? UnknownComparisonId : ComparisonId;
        var binding = operation == LocalMonitorV1ComparisonOperation.Rows ? "tool\n" : evidenceResultOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
        var cursorBinding = scenario.StartsWith("wrong_binding", StringComparison.Ordinal) ? binding + "wrong" : binding;
        var cursor = new LocalMonitorV1ComparisonCursorCodec(CursorKey).Encode(requestedRepository, requestedComparison, operation == LocalMonitorV1ComparisonOperation.Rows ? "rows" : "evidence", cursorBinding, 1);
        if (scenario.StartsWith("invalid", StringComparison.Ordinal)) cursor = cursor[..^1] + (cursor[^1] == 'A' ? "B" : "A");
        if (scenario.EndsWith("expired", StringComparison.Ordinal))
        {
            var expiredClock = new FixedClock(initialClock.GetUtcNow().AddHours(25));
            application = new LocalMonitorV1ComparisonProductionApplication(new ThrowingInput(), new SqliteLocalComparisonStore(db.Path, expiredClock), expiredClock, cursorKey: CursorKey);
        }
        var query = operation == LocalMonitorV1ComparisonOperation.Rows ? $"?family=tool&after={cursor}&limit=1" : $"?result_ordinal={evidenceResultOrdinal}&after={cursor}&limit=1";

        var response = await application.ExecuteAsync(operation, requestedRepository, requestedComparison, ReadOnlyMemory<byte>.Empty, query, default);

        var entity = Encoding.UTF8.GetString(response.Entity);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal($"{{\"error\":\"{expectedError}\"}}", entity);
        Assert.DoesNotContain(requestedRepository, entity, StringComparison.Ordinal);
        Assert.DoesNotContain(requestedComparison, entity, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("evidence")]
    public async Task AuthenticatedCursorPositionMustBeAFrozenPagingBoundary(string operationName)
    {
        var operation = operationName == "rows" ? LocalMonitorV1ComparisonOperation.Rows : LocalMonitorV1ComparisonOperation.Evidence;
        using var db = new Database(); db.Initialize();
        var clock = new FixedClock();
        var store = new SqliteLocalComparisonStore(db.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(new FakeInput([Input(SessionA), Input(SessionB)]), store, clock, _ => ComparisonId, CursorKey);
        await CreateComparison(application);
        var snapshot = store.Read(LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, default).Snapshot!;
        var codec = new LocalMonitorV1ComparisonCursorCodec(CursorKey);
        string query;
        if (operation == LocalMonitorV1ComparisonOperation.Rows)
        {
            var impossible = snapshot.Results.First(result => result.ResultOrdinal > 0 && result.RowKind != "tool").ResultOrdinal;
            var cursor = codec.Encode(LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, "rows", "tool\n", impossible);
            query = $"?family=tool&after={cursor}&limit=1";
        }
        else
        {
            var resultOrdinal = snapshot.Evidence[0].ResultOrdinal;
            var cursor = codec.Encode(LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, "evidence", resultOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", int.MaxValue);
            query = $"?result_ordinal={resultOrdinal}&after={cursor}&limit=1";
        }

        var response = await application.ExecuteAsync(operation, LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, ReadOnlyMemory<byte>.Empty, query, default);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_cursor\"}", Encoding.UTF8.GetString(response.Entity));
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("evidence")]
    public async Task ValidAndAbsentCursorsPreserveFrozenPaging(string operationName)
    {
        var operation = operationName == "rows" ? LocalMonitorV1ComparisonOperation.Rows : LocalMonitorV1ComparisonOperation.Evidence;
        using var db = new Database(); db.Initialize();
        var clock = new FixedClock();
        var store = new SqliteLocalComparisonStore(db.Path, clock);
        var application = new LocalMonitorV1ComparisonProductionApplication(new FakeInput([Input(SessionA), Input(SessionB)]), store, clock, _ => ComparisonId, CursorKey);
        await CreateComparison(application);
        var snapshot = store.Read(LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, default).Snapshot!;
        string absentQuery;
        string continuedQuery;
        if (operation == LocalMonitorV1ComparisonOperation.Rows)
        {
            var boundary = snapshot.Results.First(result => result.RowKind == "tool").ResultOrdinal;
            var cursor = new LocalMonitorV1ComparisonCursorCodec(CursorKey).Encode(LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, "rows", "tool\n", boundary);
            absentQuery = "?family=tool&limit=1";
            continuedQuery = $"?family=tool&after={cursor}&limit=1";
        }
        else
        {
            var evidence = snapshot.Evidence.GroupBy(item => item.ResultOrdinal).First(group => group.Count() >= 2).OrderBy(item => item.EvidenceOrdinal).ToArray();
            var resultOrdinal = evidence[0].ResultOrdinal;
            var cursor = new LocalMonitorV1ComparisonCursorCodec(CursorKey).Encode(LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, "evidence", resultOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", evidence[0].EvidenceOrdinal + 1);
            absentQuery = $"?result_ordinal={resultOrdinal}&limit=1";
            continuedQuery = $"?result_ordinal={resultOrdinal}&after={cursor}&limit=1";
        }

        var absent = await application.ExecuteAsync(operation, LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, ReadOnlyMemory<byte>.Empty, absentQuery, default);
        var continued = await application.ExecuteAsync(operation, LocalComparisonInputProjectionTests.RepositoryId, ComparisonId, ReadOnlyMemory<byte>.Empty, continuedQuery, default);

        Assert.Equal(200, absent.StatusCode);
        Assert.Equal(200, continued.StatusCode);
        Assert.StartsWith("{\"schema_version\":\"local-monitor-comparison-", Encoding.UTF8.GetString(absent.Entity), StringComparison.Ordinal);
        Assert.StartsWith("{\"schema_version\":\"local-monitor-comparison-", Encoding.UTF8.GetString(continued.Entity), StringComparison.Ordinal);
    }

    private const string SessionA = "018f0000-0000-7000-8000-000000000001";
    private const string SessionB = "018f0000-0000-7000-8000-000000000002";
    private const string ComparisonId = "018f0000-0000-7000-8000-000000000010";
    private const string UnknownComparisonId = "018f0000-0000-7000-8000-000000000011";
    private const string OtherRepositoryId = "018f0000-0000-7000-8000-000000000099";
    private static readonly byte[] CursorKey = new byte[32];

    private static async Task CreateComparison(LocalMonitorV1ComparisonProductionApplication application)
    {
        var previewBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{SessionA}\"],\"b\":[\"{SessionB}\"]}},\"include_archived\":false}}");
        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        using var previewJson = JsonDocument.Parse(preview.Entity);
        Assert.True(previewJson.RootElement.GetProperty("valid").GetBoolean(), Encoding.UTF8.GetString(preview.Entity));
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{SessionA}\"],\"b\":[\"{SessionB}\"]}},\"include_archived\":false,\"selection_sha256\":\"{previewJson.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{previewJson.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var created = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        Assert.True(created.StatusCode == 201, Encoding.UTF8.GetString(created.Entity));
    }

    private static void AssignRepository(string databasePath, params string[] sessionIds)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES('{LocalComparisonInputProjectionTests.RepositoryId}','Repository',1,'2026-01-01T01:00:00.0000000+00:00','2026-01-01T01:00:00.0000000+00:00');" + string.Concat(sessionIds.Select(id => $"INSERT INTO session_repository_assignment_revisions(session_id,revision,updated_at) VALUES('{id}',1,'2026-01-01T01:00:00.0000000+00:00'); INSERT INTO session_repository_manual_overrides(session_id,state,repository_id,revision,updated_at) VALUES('{id}','assigned','{LocalComparisonInputProjectionTests.RepositoryId}',1,'2026-01-01T01:00:00.0000000+00:00');"));
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, string> Values(JsonElement item) =>
        item.GetProperty("values").EnumerateArray().ToDictionary(value => value.GetProperty("key").GetString()!, value => value.GetProperty("value").GetString()!);

    private static LocalRepositoryComparisonSessionInput Input(string id, bool archived = false) { var s = LocalComparisonInputProjectionTests.ScopeSession(id, archived); var detail = LocalComparisonInputProjectionTests.Detail(id, false); return new(s, detail, new string(id[^1], 64), new(detail.Nodes, detail.Versions ?? [], [], detail.CanonicalRevisionInput!, detail.SkillRegistryGenerationIdentity!)); }
    private sealed class FakeInput(IReadOnlyList<LocalRepositoryComparisonSessionInput> sessions, bool repositoryArchived = false) : ILocalRepositoryComparisonInputSnapshotService { public ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) { var repo = new LocalRepositoryCatalogSnapshot(LocalComparisonInputProjectionTests.RepositoryId, "Repository", 1, null, 0, repositoryArchived ? LocalArchiveState.Archived : LocalArchiveState.Active, repositoryArchived ? 2 : 1); return ValueTask.FromResult(new LocalRepositoryComparisonInputSnapshot(new(request, [repo], sessions.Select(x => x.Session).ToArray()), sessions)); } }
    private sealed class ThrowingInput : ILocalRepositoryComparisonInputSnapshotService { public ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("current_session_state_was_queried"); }
    private sealed class DetailFailureInput(string error) : ILocalRepositoryComparisonInputSnapshotService { public ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(LocalRepositoryScopeRequest request, CancellationToken cancellationToken) => throw new LocalWorkspaceSessionDetailException(error); }
    private sealed class FixedClock(DateTimeOffset? now = null) : TimeProvider { public override DateTimeOffset GetUtcNow() => now ?? new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Database : IDisposable { private readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"comparison-app-{Guid.NewGuid():N}"); internal Database() { Directory.CreateDirectory(dir); Path = System.IO.Path.Combine(dir, "db.sqlite"); } internal string Path { get; } internal void Initialize() { new SqliteSessionStore(Path).CreateSchema(); using var c = Open(); LocalRepositoryCatalogSchemaV1.Ensure(c); LocalArchiveSchemaV1.Ensure(c); LocalWorkspaceProjectionSchemaV1.Ensure(c, DateTimeOffset.UnixEpoch); LocalComparisonSchemaV1.Ensure(c); using var cmd = c.CreateCommand(); cmd.CommandText = $"INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES('{LocalComparisonInputProjectionTests.RepositoryId}','Repository',1,'2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');"; cmd.ExecuteNonQuery(); }
        internal void InitializeProductionScope(string active, string archived, bool archiveSecond = true) { new SqliteSessionStore(Path).CreateSchema(); using var c = Open(); using (var skills = c.BeginTransaction()) { MonitorSchemaMigrator.ApplyBaseSchema(c, skills); CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSchemaMigrator.Apply(c, skills); SkillProjectionSchemaV1.Ensure(c, skills); skills.Commit(); } var authority = FixedSkillRegistryGenerationAuthority.Load(); LocalRepositoryCatalogSchemaV1.Ensure(c); LocalWorkspaceProjectionSchemaV1.Ensure(c, DateTimeOffset.Parse("2026-08-29T00:00:00Z"), authority); LocalArchiveSchemaV1.Ensure(c); LocalComparisonSchemaV1.Ensure(c); using (var seed = c.CreateCommand()) { seed.CommandText = $"INSERT INTO sessions VALUES('{active}','completed','partial',NULL,NULL,NULL,NULL,'2026-08-29T00:00:00.0000000+00:00','not_captured','2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00'),('{archived}','completed','partial',NULL,NULL,NULL,NULL,'2026-08-29T00:00:00.0000000+00:00','not_captured','2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');"; seed.ExecuteNonQuery(); } using (var refresh = c.BeginTransaction()) { LocalWorkspaceProjectionStore.RefreshStructural(c, refresh, DateTimeOffset.Parse("2026-08-29T00:00:00Z")); refresh.Commit(); } using (var assignments = c.CreateCommand()) { assignments.CommandText = $"""
            INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES('{LocalComparisonInputProjectionTests.RepositoryId}','Repository',1,'2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');
            INSERT INTO session_repository_assignment_revisions(session_id,revision,updated_at) VALUES('{active}',1,'2026-08-29T00:00:00.0000000+00:00'),('{archived}',1,'2026-08-29T00:00:00.0000000+00:00');
            INSERT INTO session_repository_manual_overrides(session_id,state,repository_id,revision,updated_at) VALUES('{active}','assigned','{LocalComparisonInputProjectionTests.RepositoryId}',1,'2026-08-29T00:00:00.0000000+00:00'),('{archived}','assigned','{LocalComparisonInputProjectionTests.RepositoryId}',1,'2026-08-29T00:00:00.0000000+00:00');
            """; assignments.ExecuteNonQuery(); } if (!archiveSecond) return; using var cmd = c.CreateCommand(); cmd.CommandText = $"""
            INSERT INTO local_archive_current(target_kind,target_id,state,revision,archived_at,updated_at) VALUES('session','{archived}','archived',1,'2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');
            """; cmd.ExecuteNonQuery(); }
        internal void ArchiveRepository() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = $"INSERT INTO local_archive_current(target_kind,target_id,state,revision,archived_at,updated_at) VALUES('repository','{LocalComparisonInputProjectionTests.RepositoryId}','archived',1,'2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');"; cmd.ExecuteNonQuery(); }
        internal void CorruptProjection(string sessionId) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE local_workspace_sessions SET label_state='invalid_owner_state' WHERE session_id=$session_id; PRAGMA ignore_check_constraints=OFF;"; cmd.Parameters.AddWithValue("$session_id", sessionId); cmd.ExecuteNonQuery(); }
        internal void SeedVersions(string sessionId, string dimension, int count) { for (var index = 0; index < count; index++) SeedVersion(sessionId, dimension, index == 0 ? "V1+meta" : index == count - 1 ? char.ToUpperInvariant(dimension[0]) + new string('x', 253) + "+1" : $"{dimension}-{index:D3}", index); }
        internal void SeedVersion(string sessionId, string dimension, string version, int index = 0) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = $"INSERT INTO session_events(event_id,session_id,source_adapter,source_event_id,type,occurred_at,content_state,{(dimension == "source" ? "source_application_version" : "adapter_version")}) VALUES($event,$session,'compare-version-test',$source,'version.fact','2026-08-29T00:00:00.0000000+00:00','not_captured',$version);"; cmd.Parameters.AddWithValue("$event", $"018f0000-0000-7000-8000-{index + 0x100:x12}"); cmd.Parameters.AddWithValue("$session", sessionId); cmd.Parameters.AddWithValue("$source", $"{dimension}-{index:D3}"); cmd.Parameters.AddWithValue("$version", version); cmd.ExecuteNonQuery(); }
        internal void InitializeProductionNamedComparison(string a, string b)
        {
            const string aOtel = "018f0000-0000-7000-8000-000000000010", aSdk = "018f0000-0000-7000-8000-000000000020";
            const string bOtel = "018f0000-0000-7000-8000-000000000110", bSdk = "018f0000-0000-7000-8000-000000000120";
            LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(Path, a, aOtel, aSdk);
            using var c = Open();
            using var seed = c.CreateCommand();
            seed.CommandText = $"""
                INSERT INTO sessions SELECT '{b}',status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at FROM sessions WHERE session_id='{a}';
                INSERT INTO session_native_ids VALUES('{b}','copilot-sdk','native-session-sdk-b','native','2026-08-26T00:00:00.0000000+00:00');
                INSERT INTO session_runs VALUES
                  ('{bOtel}','{b}','claude-code','native-run-otel-b','cccccccccccccccccccccccccccccccc',NULL,NULL,'2026-08-26T00:00:00.0000000+00:00','2026-08-26T00:00:04.0000000+00:00',NULL,NULL,NULL,'completed'),
                  ('{bSdk}','{b}','copilot-sdk','native-child-sdk-b',NULL,NULL,NULL,'2026-08-26T00:00:05.0000000+00:00','2026-08-26T00:00:06.0000000+00:00',NULL,NULL,NULL,'completed');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES
                  ('018f0000-0000-7000-8000-000000000111','{b}','{bOtel}','claude-code','cccccccccccccccccccccccccccccccc','otel-exact','cccccccccccccccccccccccccccccccc/dddddddddddddddd','otel.span','2026-08-26T09:00:00.0000000+00:00','not_captured'),
                  ('018f0000-0000-7000-8000-000000000121','{b}','{bSdk}','copilot-sdk',NULL,'copilot-sdk-stream','sdk-subagent-started-b','subagent.started','2026-08-26T00:00:05.0000000+00:00','not_captured'),
                  ('018f0000-0000-7000-8000-000000000122','{b}','{bSdk}','copilot-sdk',NULL,'copilot-sdk-stream','sdk-subagent-completed-b','subagent.completed','2026-08-26T00:00:06.0000000+00:00','not_captured'),
                  ('018f0000-0000-7000-8000-000000000041','{a}','{aOtel}','claude-code','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/eeeeeeeeeeeeeeee','otel.span','2026-08-26T09:00:01.0000000+00:00','not_captured');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,parent_event_id,type,occurred_at,content_state) VALUES
                  ('018f0000-0000-7000-8000-000000000131','{b}','{bSdk}','copilot-sdk','copilot-sdk-stream','sdk-tool-start-b',NULL,'tool.execution_start','2026-08-26T00:00:03.0000000+00:00','not_captured'),
                  ('018f0000-0000-7000-8000-000000000132','{b}','{bSdk}','copilot-sdk','copilot-sdk-stream','sdk-tool-complete-b','018f0000-0000-7000-8000-000000000131','tool.execution_complete','2026-08-26T00:00:04.0000000+00:00','not_captured');
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,mcp_tool_name,mcp_server_hash,status,duration_ms,start_time,end_time,projected_at) VALUES
                  (2,'cccccccccccccccccccccccccccccccc','dddddddddddddddd',NULL,0,'execute_tool','tool_call','Read','ReadMcp','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','ok',2000,'2026-08-26T00:00:02.0000000+00:00','2026-08-26T00:00:04.0000000+00:00','2026-08-26T00:00:04.0000000+00:00'),
                  (3,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','eeeeeeeeeeeeeeee',NULL,1,'execute_tool','tool_call','Read','ReadMcp','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','ok',1000,'2026-08-26T00:00:03.0000000+00:00','2026-08-26T00:00:04.0000000+00:00','2026-08-26T00:00:04.0000000+00:00');
                """;
            seed.ExecuteNonQuery();
            using (var refresh = c.BeginTransaction()) { LocalWorkspaceProjectionStore.RefreshStructural(c, refresh, DateTimeOffset.Parse("2026-08-26T00:10:00Z")); refresh.Commit(); }
            using var assignment = c.CreateCommand();
            assignment.CommandText = $"""
                INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES('{LocalComparisonInputProjectionTests.RepositoryId}','Repository',1,'2026-08-26T00:10:00.0000000+00:00','2026-08-26T00:10:00.0000000+00:00');
                INSERT INTO session_repository_assignment_revisions(session_id,revision,updated_at) VALUES('{a}',1,'2026-08-26T00:10:00.0000000+00:00'),('{b}',1,'2026-08-26T00:10:00.0000000+00:00');
                INSERT INTO session_repository_manual_overrides(session_id,state,repository_id,revision,updated_at) VALUES('{a}','assigned','{LocalComparisonInputProjectionTests.RepositoryId}',1,'2026-08-26T00:10:00.0000000+00:00'),('{b}','assigned','{LocalComparisonInputProjectionTests.RepositoryId}',1,'2026-08-26T00:10:00.0000000+00:00');
                """;
            assignment.ExecuteNonQuery();
        }
        internal void HideNamedToolDetail(string sessionId)
        {
            using var c = Open(); using var command = c.CreateCommand();
            command.CommandText = "DELETE FROM monitor_spans WHERE trace_id IN (SELECT trace_id FROM session_runs WHERE session_id=$session); DELETE FROM session_events WHERE session_id=$session AND (type='otel.span' OR type='tool.execution_complete');";
            command.Parameters.AddWithValue("$session", sessionId); command.ExecuteNonQuery();
            using var refresh = c.BeginTransaction(); LocalWorkspaceProjectionStore.RefreshStructural(c, refresh, DateTimeOffset.Parse("2026-08-26T00:10:00Z")); refresh.Commit();
        }
        internal void SeedClaudeNamedSubagent(string sessionId)
        {
            using var c = Open(); using var command = c.CreateCommand();
            command.CommandText = """
                INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
                  VALUES($session,'claude-code','native-claude-session','native','2026-08-26T00:00:00.0000000+00:00');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,normalization_version) VALUES
                  ('018f0000-0000-7000-8000-000000000061',$session,'018f0000-0000-7000-8000-000000000010','claude-code','claude-code-hook','hook-subagent-start','SubagentStart','2026-08-26T00:00:01.0000000+00:00','available','2.1.145','hook-v1','normalization-v1'),
                  ('018f0000-0000-7000-8000-000000000062',$session,'018f0000-0000-7000-8000-000000000010','claude-code','claude-code-hook','hook-subagent-stop','SubagentStop','2026-08-26T00:00:02.0000000+00:00','available','2.1.145','hook-v1','normalization-v1');
                INSERT INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token) VALUES
                  ('018f0000-0000-7000-8000-000000000061','application/json','{"agent_type":"reviewer"}','2026-08-26T00:00:01.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32)),
                  ('018f0000-0000-7000-8000-000000000062','application/json','{"agent_type":"reviewer"}','2026-08-26T00:00:02.0000000+00:00','2026-09-01T00:00:00.0000000+00:00',randomblob(32));
                """;
            command.Parameters.AddWithValue("$session", sessionId); command.ExecuteNonQuery();
            using var refresh = c.BeginTransaction(); LocalWorkspaceProjectionStore.RefreshStructural(c, refresh, DateTimeOffset.Parse("2026-08-26T00:10:00Z")); refresh.Commit();
        }
        private SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); c.Open(); return c; } public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); } }
}

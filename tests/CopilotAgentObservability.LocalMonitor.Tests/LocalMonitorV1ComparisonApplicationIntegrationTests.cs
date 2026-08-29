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
            Assert.Equal("session_archived", json.RootElement.GetProperty("excluded")[0].GetProperty("reason").GetString());
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
    public async Task PersistedNamedRowsSerializerMatchesNormativeGoldenBytes()
    {
        using var db = new Database(); db.Initialize();
        const string a = "018f0000-0000-7000-8000-000000000001", b = "018f0000-0000-7000-8000-000000000002";
        var application = new LocalMonitorV1ComparisonProductionApplication(
            new FakeInput([Input(a), Input(b)]),
            new SqliteLocalComparisonStore(db.Path, new FixedClock()),
            new FixedClock(),
            _ => "018f0000-0000-7000-8000-000000000010",
            new byte[32]);
        var previewBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-preview.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false}}");
        var preview = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Preview, LocalComparisonInputProjectionTests.RepositoryId, null, previewBody, "", default);
        using var previewJson = JsonDocument.Parse(preview.Entity);
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{a}\"],\"b\":[\"{b}\"]}},\"include_archived\":false,\"selection_sha256\":\"{previewJson.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{previewJson.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var created = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        Assert.Equal(201, created.StatusCode);

        var rows = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Rows, LocalComparisonInputProjectionTests.RepositoryId, "018f0000-0000-7000-8000-000000000010", ReadOnlyMemory<byte>.Empty, "?family=tool&q=tool-helper", default);
        var expected = (await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "LocalMonitorV1Comparison", "local-monitor-comparison-rows.response.json")))
            .AsSpan().TrimEnd([(byte)'\r', (byte)'\n']).ToArray();

        Assert.Equal(expected, rows.Entity);
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
                Assert.NotNull(execution);
                Assert.NotNull(node);
                Assert.Equal($"/sessions/{item.GetProperty("session_id").GetString()}?execution={execution}&node={node}", item.GetProperty("session_location").GetString());
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
        var createBody = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"local-monitor-comparison-create.request.v1\",\"cohorts\":{{\"a\":[\"{SessionA}\"],\"b\":[\"{SessionB}\"]}},\"include_archived\":false,\"selection_sha256\":\"{previewJson.RootElement.GetProperty("selection_sha256").GetString()}\",\"preview_revision\":\"{previewJson.RootElement.GetProperty("preview_revision").GetString()}\"}}");
        var created = await application.ExecuteAsync(LocalMonitorV1ComparisonOperation.Create, LocalComparisonInputProjectionTests.RepositoryId, null, createBody, "", default);
        Assert.Equal(201, created.StatusCode);
    }

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
        private SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); c.Open(); return c; } public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); } }
}

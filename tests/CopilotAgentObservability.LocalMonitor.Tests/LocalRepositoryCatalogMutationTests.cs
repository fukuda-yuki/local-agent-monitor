using System.Globalization;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Repositories;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryCatalogMutationTests
{
    [Fact]
    public async Task CreateRenameAndLocatorChanges_PersistTheExactRepositoryHistory()
    {
        using var fixture = new LocalRepositoryCatalogFixture();

        var create = await fixture.CreateAsync("Cafe\u0301", null, fixture.Key(2));
        var repository = fixture.Repository(create);
        Assert.Equal("Caf\u00e9", repository.DisplayName);
        Assert.Equal(1, repository.Revision);
        Assert.Equal("create|0|1|", fixture.ScalarText("SELECT action||'|'||previous_revision||'|'||new_revision||'|'||coalesce(locator_id,'') FROM local_repository_history;"));

        var renamed = await fixture.RenameAsync(repository.RepositoryId, 1, "Renamed", fixture.Key(3));
        var renamedRepository = fixture.Repository(renamed);
        Assert.Equal(2, renamedRepository.Revision);

        var located = await fixture.SetLocatorAsync(repository.RepositoryId, 2, "https://github.com/Example/One", fixture.Key(4));
        var locatedRepository = fixture.Repository(located);
        Assert.Equal(3, locatedRepository.Revision);
        Assert.Equal(["create", "rename", "add_locator"], fixture.QueryStrings("SELECT action FROM local_repository_history ORDER BY new_revision;"));

        var replaced = await fixture.SetLocatorAsync(repository.RepositoryId, 3, "git@github.com:Example/Two.git", fixture.Key(5));
        Assert.Equal(4, fixture.Repository(replaced).Revision);
        Assert.Equal(["create", "rename", "add_locator", "replace_locator"], fixture.QueryStrings("SELECT action FROM local_repository_history ORDER BY new_revision;"));

        var historical = await fixture.SetLocatorAsync(repository.RepositoryId, 4, "https://github.com/example/one", fixture.Key(6));
        Assert.Equal(5, fixture.Repository(historical).Revision);
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_locators;"));
        Assert.Equal("replace_locator", fixture.ScalarText("SELECT action FROM local_repository_history WHERE new_revision=5;"));
    }

    [Fact]
    public async Task SetCurrentLocator_IsASemanticNoOpWithAReceiptAndNoRevisionWrite()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", "https://github.com/example/one", fixture.Key(10)));

        var result = await fixture.SetLocatorAsync(repository.RepositoryId, 1, "git@github.com:EXAMPLE/ONE.git", fixture.Key(11));

        Assert.Equal(1, fixture.Repository(result).Revision);
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task ObservedHistoricalLocator_CanBecomeCurrentAgainUnderAUserOperation()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        _ = await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(20),
                LocalRepositoryAdmissionFixture.Span(20),
                "https://github.com/Example/Observed.git")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(20)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        var application = ApplicationFor(fixture.DatabasePath);

        var manual = await ExecuteSetLocator(application, repositoryId, 1, "https://github.com/example/manual", OperationKey(20));
        Assert.IsType<LocalRepositoryMutationSucceeded>(manual);
        var returned = await ExecuteSetLocator(application, repositoryId, 2, "https://github.com/example/observed", OperationKey(21));
        Assert.IsType<LocalRepositoryMutationSucceeded>(returned);
        var renamed = await ExecuteRename(application, repositoryId, 3, "Renamed", OperationKey(22));

        Assert.IsType<LocalRepositoryMutationSucceeded>(renamed);
        Assert.Equal("observed", fixture.ScalarText($"SELECT source FROM local_repository_locators WHERE locator_id=(SELECT locator_id FROM local_repository_locator_heads WHERE repository_id='{repositoryId}');"));
        Assert.Equal("replace_locator|user_operation", fixture.ScalarText($"SELECT action||'|'||cause_kind FROM local_repository_history WHERE repository_id='{repositoryId}' AND new_revision=3;"));
    }

    [Fact]
    public async Task MissingUserOperationReceipt_FaultsBeforeARepositoryMutation()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(25)));
        fixture.Execute("DROP TRIGGER local_repository_operation_receipts_delete_rejected;");
        fixture.Execute($"DELETE FROM local_repository_operation_receipts WHERE operation_key='{fixture.Key(25)}';");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.RenameAsync(repository.RepositoryId, 1, "Renamed", fixture.Key(26)));

        Assert.Equal("One", fixture.ScalarText($"SELECT display_name FROM local_repositories WHERE repository_id='{repository.RepositoryId}';"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task MismatchedObservedCreationContext_FaultsBeforeARepositoryMutation()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        _ = await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(
                new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(30), LocalRepositoryAdmissionFixture.Span(30), "https://github.com/example/one"),
                new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(31), LocalRepositoryAdmissionFixture.Span(31), "https://github.com/example/two")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(30), LocalRepositoryAdmissionFixture.MatchedEvent(31)]);
        var repositoryId = fixture.QueryStrings("SELECT repository_id FROM local_repositories ORDER BY repository_id;")[0];
        var foreignContext = fixture.ScalarText($"SELECT context_identity_sha256 FROM session_repository_observation_contexts WHERE repository_id<>'{repositoryId}' LIMIT 1;");
        fixture.Execute("DROP TRIGGER local_repository_history_update_rejected;");
        fixture.Execute($"UPDATE local_repository_history SET context_identity_sha256='{foreignContext}' WHERE repository_id='{repositoryId}';");
        var application = ApplicationFor(fixture.DatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ExecuteRename(application, repositoryId, 1, "Renamed", OperationKey(30)));

        Assert.Equal(1, fixture.ScalarLong($"SELECT revision FROM local_repositories WHERE repository_id='{repositoryId}';"));
    }

    [Fact]
    public async Task LocatorOwnedByAnotherRepository_BeatsThe128LocatorLimit()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var first = fixture.Repository(await fixture.CreateAsync("One", "https://github.com/example/one", fixture.Key(20)));
        var second = fixture.Repository(await fixture.CreateAsync("Two", "https://github.com/example/two", fixture.Key(21)));
        fixture.SeedHistoricalLocators(first.RepositoryId, 127);

        var conflict = await fixture.SetLocatorAsync(first.RepositoryId, 128, "https://github.com/example/two", fixture.Key(22));
        var limit = await fixture.SetLocatorAsync(first.RepositoryId, 128, "https://github.com/example/new", fixture.Key(23));
        var historicalMove = await fixture.SetLocatorAsync(first.RepositoryId, 128, "https://github.com/example/historical0", fixture.Key(24));

        Assert.Equal(LocalRepositoryMutationFailure.LocatorConflict, Assert.IsType<LocalRepositoryMutationRejected>(conflict).Failure);
        Assert.Equal(LocalRepositoryMutationFailure.LocatorLimitReached, Assert.IsType<LocalRepositoryMutationRejected>(limit).Failure);
        Assert.Equal(129, fixture.Repository(historicalMove).Revision);
        Assert.Equal(128, fixture.ScalarLong($"SELECT COUNT(*) FROM local_repository_locators WHERE repository_id='{first.RepositoryId}';"));
        Assert.Equal(130, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
        var locators = Assert.IsType<LocalRepositoryLocatorsFound>(
            await fixture.Application.ReadLocatorsAsync(first.RepositoryId, CancellationToken.None));
        Assert.Equal(128, locators.Value.Locators.Count);
        Assert.Equal("github.com/example/historical0", locators.Value.Locators[0].CanonicalLocator);
    }

    [Fact]
    public async Task TargetAndRevisionPreflight_PrecedeLocatorConflictAndNeverStoreErrors()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var first = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(30)));
        _ = fixture.Repository(await fixture.CreateAsync("Two", "https://github.com/example/two", fixture.Key(31)));

        var missing = await fixture.SetLocatorAsync(LocalRepositoryCatalogFixture.RepositoryId(999), 1, "https://github.com/example/two", fixture.Key(32));
        var stale = await fixture.SetLocatorAsync(first.RepositoryId, 2, "https://github.com/example/two", fixture.Key(33));

        Assert.Equal(LocalRepositoryMutationFailure.RepositoryNotFound, Assert.IsType<LocalRepositoryMutationRejected>(missing).Failure);
        Assert.Equal(LocalRepositoryMutationFailure.RevisionConflict, Assert.IsType<LocalRepositoryMutationRejected>(stale).Failure);
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData("missing_middle")]
    [InlineData("revision_ahead")]
    [InlineData("head_disagrees")]
    [InlineData("cause_union")]
    [InlineData("orphan_locator")]
    public async Task RepositoryFrontierCorruption_FaultsAndRollsBackTheMutation(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", "https://github.com/example/one", fixture.Key(40)));
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(41));
        fixture.CorruptRepositoryFrontier(repository.RepositoryId, corruption);
        var beforeRevision = fixture.ScalarLong($"SELECT revision FROM local_repositories WHERE repository_id='{repository.RepositoryId}';");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.RenameAsync(repository.RepositoryId, 2, "Three", fixture.Key(42)));

        Assert.Equal(beforeRevision, fixture.ScalarLong($"SELECT revision FROM local_repositories WHERE repository_id='{repository.RepositoryId}';"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData("add_after_locator_create")]
    [InlineData("replace_after_locatorless_create")]
    [InlineData("replace_current_head")]
    [InlineData("manual_action_observed_locator")]
    [InlineData("observed_action_manual_locator")]
    [InlineData("missing_first")]
    [InlineData("missing_latest")]
    [InlineData("head_wrong_kind")]
    [InlineData("head_wrong_owner")]
    public async Task RepositoryHistoryTransitionCorruption_FaultsBeforeAppending(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var initialLocator = corruption == "replace_after_locatorless_create" ? null : "https://github.com/example/one";
        var repository = fixture.Repository(await fixture.CreateAsync("One", initialLocator, fixture.Key(43)));
        _ = await fixture.SetLocatorAsync(repository.RepositoryId, 1, "https://github.com/example/two", fixture.Key(44));
        fixture.CorruptRepositoryHistoryTransition(repository.RepositoryId, corruption);
        var beforeRevision = fixture.ScalarLong($"SELECT revision FROM local_repositories WHERE repository_id='{repository.RepositoryId}';");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.RenameAsync(repository.RepositoryId, beforeRevision, "Changed", fixture.Key(45)));

        Assert.Equal(beforeRevision, fixture.ScalarLong($"SELECT revision FROM local_repositories WHERE repository_id='{repository.RepositoryId}';"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    private static LocalRepositoryCatalogApplication ApplicationFor(string databasePath)
    {
        var queue = new SqliteLocalRepositoryReconciliationStore(databasePath, TimeProvider.System, static () => new string('d', 64));
        return new(new SqliteLocalRepositoryCatalogStore(
            databasePath,
            queue,
            new LocalRepositoryAssignmentResolver(),
            TimeProvider.System));
    }

    private static async ValueTask<LocalRepositoryMutationResult> ExecuteSetLocator(
        LocalRepositoryCatalogApplication application,
        string repositoryId,
        long revision,
        string locator,
        string operationKey) => await application.ExecutePreparedAsync(
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSetLocator>>(
                application.PrepareSetGitHubLocator(new(repositoryId, revision, locator))).Prepared,
            operationKey,
            LocalRepositoryCatalogFixture.RepositoryEntity,
            CancellationToken.None);

    private static async ValueTask<LocalRepositoryMutationResult> ExecuteRename(
        LocalRepositoryCatalogApplication application,
        string repositoryId,
        long revision,
        string displayName,
        string operationKey) => await application.ExecutePreparedAsync(
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedRename>>(
                application.PrepareRename(new(repositoryId, revision, displayName))).Prepared,
            operationKey,
            LocalRepositoryCatalogFixture.RepositoryEntity,
            CancellationToken.None);

    private static string OperationKey(byte value) => "lrc1_" + Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray())
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class LocalRepositoryCatalogFixture : IDisposable
{
    internal const string At = "2026-08-01T01:02:03.1234567+00:00";
    private readonly MonitorTempDirectory temp = new();
    private readonly RawTelemetryStore rawStore;
    private readonly LocalRepositoryAssignmentResolver assignmentResolver;
    private readonly Dictionary<string, string> sessionEvents = new(StringComparer.Ordinal);
    private int nextId = 1000;

    internal LocalRepositoryCatalogFixture()
    {
        temp.TimeProvider = new MutableTimeProvider(DateTimeOffset.ParseExact(At, "O", CultureInfo.InvariantCulture));
        rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using (var connection = Open())
            LocalRepositoryCatalogSchemaV1.Ensure(connection);
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider, static () => new string('a', 64));
        assignmentResolver = new LocalRepositoryAssignmentResolver(NextUuid);
        Store = new SqliteLocalRepositoryCatalogStore(
            temp.DatabasePath,
            queue,
            assignmentResolver,
            temp.TimeProvider,
            NextUuid);
        Application = new LocalRepositoryCatalogApplication(Store);
    }

    internal string DatabasePath => temp.DatabasePath;
    internal RawTelemetryStore RawStore => rawStore;
    internal SqliteLocalRepositoryCatalogStore Store { get; }
    internal LocalRepositoryCatalogApplication Application { get; }

    internal LocalRepositoryCatalogApplication NewApplication()
    {
        var queue = new SqliteLocalRepositoryReconciliationStore(temp.DatabasePath, temp.TimeProvider, static () => new string('b', 64));
        return new(new SqliteLocalRepositoryCatalogStore(
            temp.DatabasePath,
            queue,
            new LocalRepositoryAssignmentResolver(NextUuid),
            temp.TimeProvider,
            NextUuid));
    }

    internal string Key(byte value) => "lrc1_" + Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray())
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal async Task<LocalRepositoryMutationResult> CreateAsync(string displayName, string? locator, string key) =>
        await Application.ExecutePreparedAsync(
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
                Application.PrepareCreate(new(displayName, locator))).Prepared,
            key,
            RepositoryEntity,
            CancellationToken.None);

    internal async Task<LocalRepositoryMutationResult> RenameAsync(string repositoryId, long revision, string displayName, string key) =>
        await Application.ExecutePreparedAsync(
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedRename>>(
                Application.PrepareRename(new(repositoryId, revision, displayName))).Prepared,
            key,
            RepositoryEntity,
            CancellationToken.None);

    internal async Task<LocalRepositoryMutationResult> SetLocatorAsync(string repositoryId, long revision, string locator, string key) =>
        await Application.ExecutePreparedAsync(
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSetLocator>>(
                Application.PrepareSetGitHubLocator(new(repositoryId, revision, locator))).Prepared,
            key,
            RepositoryEntity,
            CancellationToken.None);

    internal async Task<LocalRepositoryMutationResult> SessionActionAsync(string sessionId, long revision, string action, string? repositoryId, string key) =>
        await Application.ExecutePreparedAsync(
            Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                Application.PrepareSessionAction(new(sessionId, revision, action, repositoryId))).Prepared,
            key,
            AssignmentEntity,
            CancellationToken.None);

    internal static ReadOnlyMemory<byte> RepositoryEntity(LocalRepositoryMutationRepository repository) =>
        LocalRepositoryJson.WriteRepository(200, repository);

    internal static ReadOnlyMemory<byte> AssignmentEntity(LocalRepositoryMutationAssignment assignment) =>
        LocalRepositoryJson.WriteAssignment(assignment);

    internal LocalRepositoryMutationRepository Repository(LocalRepositoryMutationResult result)
    {
        var response = Assert.IsType<LocalRepositoryMutationSucceeded>(result).Response;
        using var document = System.Text.Json.JsonDocument.Parse(response.CopyEntity());
        var id = document.RootElement.GetProperty(LocalRepositoryExactResponse.RepositoryV1.RepositoryId).GetString()!;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT display_name,revision,created_at,updated_at FROM local_repositories WHERE repository_id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new(id, reader.GetString(0), reader.GetInt64(1), DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture), DateTimeOffset.ParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture));
    }

    internal void CreateSession(string sessionId)
    {
        var runId = NextUuid(default);
        var eventId = NextUuid(default);
        var startEventId = NextUuid(default);
        var instructionEventId = NextUuid(default);
        var terminalEventId = NextUuid(default);
        sessionEvents.Add(sessionId, eventId);
        Execute($"""
            INSERT INTO sessions(session_id,status,completeness,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES('{sessionId}','completed','full','{At}','{At}','{At}','not_captured','{At}','{At}');
            INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
            VALUES('{sessionId}','copilot-sdk','native-{sessionId}','native','{At}');
            INSERT INTO session_runs(run_id,session_id,source_surface,started_at,ended_at,status)
            VALUES('{runId}','{sessionId}','copilot-sdk','{At}','{At}','completed');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version)
            VALUES('{startEventId}','{sessionId}','{runId}','copilot-sdk','copilot-sdk-stream','{startEventId}','session.start','{At}','not_captured','1.2.3');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version)
            VALUES('{instructionEventId}','{sessionId}','{runId}','copilot-sdk','copilot-sdk-stream','{instructionEventId}','user.message','{At}','not_captured','1.2.3');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version)
            VALUES('{eventId}','{sessionId}','{runId}','vscode','otel-exact','11111111111111111111111111111111/2222222222222222','otel.span','{At}','not_captured','1.2.3');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,terminal_outcome,terminal_policy_version)
            VALUES('{terminalEventId}','{sessionId}','{runId}','copilot-sdk','copilot-sdk-stream','{terminalEventId}','session.task_complete','{At}','not_captured','1.2.3','clean',1);
            """);
    }

    internal void SeedAutomaticCandidate(string sessionId, string repositoryId, long rawRecordId)
    {
        var eventId = sessionEvents[sessionId];
        var locatorId = ScalarText($"SELECT locator_id FROM local_repository_locators WHERE repository_id='{repositoryId}' ORDER BY created_at,locator_id LIMIT 1;");
        var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(LocalRepositorySourceIdentityInput.Span(rawRecordId, 0, 0, 0, 0, "vcs.repository.url.full"));
        var contextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(sourceIdentity, sessionId, eventId, "11111111111111111111111111111111", "2222222222222222"));
        var contextId = NextUuid(default);
        var observationId = NextUuid(default);
        var rawPayloadSha256 = new string('b', 64);
        var reconciliationFingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, rawPayloadSha256));
        var prospective = new LocalRepositoryProspectiveAssignmentContext(contextId, contextIdentity, sessionId, repositoryId, locatorId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_repository_reconciliation_queue(
                    queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,
                    reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,
                    terminal_reason,created_at,updated_at)
                VALUES($queue_id,$raw_record_id,'payload_sha256',$digest,'local-repository-catalog:1',
                    $fingerprint,'completed',0,NULL,NULL,NULL,$at,$at);
                """;
            command.Parameters.AddWithValue("$queue_id", NextUuid(default));
            command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
            command.Parameters.AddWithValue("$digest", rawPayloadSha256);
            command.Parameters.AddWithValue("$fingerprint", reconciliationFingerprint);
            command.Parameters.AddWithValue("$at", At);
            command.ExecuteNonQuery();
        }
        var preparation = assignmentResolver.PrepareAutomatic(connection, transaction, rawRecordId, [sessionId], [prospective], reconciliationFingerprint, DateTimeOffset.ParseExact(At, "O", CultureInfo.InvariantCulture));
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO session_repository_observations VALUES(
                    '{observationId}','{sourceIdentity}',{rawRecordId},'{rawPayloadSha256}',0,0,0,0,'span','vcs.repository.url.full','admitted',
                    'github_repository',(SELECT canonical_locator FROM local_repository_locators WHERE locator_id='{locatorId}'),
                    (SELECT locator_sha256 FROM local_repository_locators WHERE locator_id='{locatorId}'),
                    (SELECT display_owner FROM local_repository_locators WHERE locator_id='{locatorId}'),
                    (SELECT display_repository FROM local_repository_locators WHERE locator_id='{locatorId}'),
                    'github-copilot-vscode','1.2.3','{At}');
                INSERT INTO session_repository_observation_contexts VALUES(
                    '{contextId}','{observationId}','{contextIdentity}','{eventId}','{sessionId}','11111111111111111111111111111111','2222222222222222','admitted','{repositoryId}','{locatorId}','{At}');
                """;
            command.ExecuteNonQuery();
        }
        assignmentResolver.ApplyAutomatic(connection, transaction, preparation);
        transaction.Commit();
    }

    internal void SeedHistoricalLocators(string repositoryId, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var previousRevision = index + 1;
            var result = SetLocatorAsync(
                repositoryId,
                previousRevision,
                $"https://github.com/example/historical{index}",
                HistoricalSeedOperationKey(repositoryId, index)).GetAwaiter().GetResult();
            Assert.IsType<LocalRepositoryMutationSucceeded>(result);
        }
    }

    private static string HistoricalSeedOperationKey(string repositoryId, int index) => "lrc1_" + Convert.ToBase64String(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"local-repository-test-history\0{repositoryId}\0{index}")))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal void CorruptRepositoryFrontier(string repositoryId, string corruption)
    {
        switch (corruption)
        {
            case "missing_middle":
                Execute("DROP TRIGGER local_repository_history_delete_rejected;");
                Execute($"DELETE FROM local_repository_history WHERE repository_id='{repositoryId}' AND new_revision=1;");
                break;
            case "revision_ahead":
                Execute($"UPDATE local_repositories SET revision=3 WHERE repository_id='{repositoryId}';");
                break;
            case "head_disagrees":
                var other = NextUuid(default);
                Assert.True(GitHubRepositoryLocatorParser.TryParse("https://github.com/example/other", out var locator));
                Execute($"INSERT INTO local_repository_locators VALUES('{other}','{repositoryId}','github_repository','{locator!.CanonicalLocator}','{locator.LocatorSha256}','manual','{locator.DisplayOwner}','{locator.DisplayRepository}','{At}'); UPDATE local_repository_locator_heads SET locator_id='{other}' WHERE repository_id='{repositoryId}';");
                break;
            case "cause_union":
                Execute($"DROP TRIGGER local_repository_history_update_rejected; PRAGMA ignore_check_constraints=ON; UPDATE local_repository_history SET context_identity_sha256='{new string('a', 64)}' WHERE repository_id='{repositoryId}' AND new_revision=2;");
                break;
            case "orphan_locator":
                var orphanId = NextUuid(default);
                Assert.True(GitHubRepositoryLocatorParser.TryParse("https://github.com/example/orphan", out var orphan));
                Execute($"INSERT INTO local_repository_locators VALUES('{orphanId}','{repositoryId}','github_repository','{orphan!.CanonicalLocator}','{orphan.LocatorSha256}','manual','{orphan.DisplayOwner}','{orphan.DisplayRepository}','{At}');");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    internal void CorruptRepositoryHistoryTransition(string repositoryId, string corruption)
    {
        Execute("DROP TRIGGER local_repository_history_update_rejected; DROP TRIGGER local_repository_history_delete_rejected; DROP TRIGGER local_repository_locators_update_rejected; DROP TRIGGER local_repository_locators_delete_rejected;");
        switch (corruption)
        {
            case "add_after_locator_create":
                Execute($"UPDATE local_repository_history SET action='add_locator' WHERE repository_id='{repositoryId}' AND new_revision=2;");
                break;
            case "replace_after_locatorless_create":
                Execute($"UPDATE local_repository_history SET locator_id=NULL WHERE repository_id='{repositoryId}' AND new_revision=1; UPDATE local_repository_history SET action='replace_locator' WHERE repository_id='{repositoryId}' AND new_revision=2;");
                break;
            case "replace_current_head":
                {
                    var replacedLocator = ScalarText($"SELECT locator_id FROM local_repository_history WHERE repository_id='{repositoryId}' AND new_revision=2;");
                    Execute($"UPDATE local_repository_history SET locator_id=(SELECT locator_id FROM local_repository_history WHERE repository_id='{repositoryId}' AND new_revision=1) WHERE repository_id='{repositoryId}' AND new_revision=2; UPDATE local_repository_locator_heads SET locator_id=(SELECT locator_id FROM local_repository_history WHERE repository_id='{repositoryId}' AND new_revision=1) WHERE repository_id='{repositoryId}'; DELETE FROM local_repository_locators WHERE locator_id='{replacedLocator}';");
                    break;
                }
            case "manual_action_observed_locator":
                Execute($"UPDATE local_repository_locators SET source='observed' WHERE locator_id=(SELECT locator_id FROM local_repository_history WHERE repository_id='{repositoryId}' AND new_revision=2);");
                break;
            case "observed_action_manual_locator":
                Execute($"UPDATE local_repository_history SET action='create_observed',cause_kind='source_context',operation_key=NULL,context_identity_sha256='{new string('a', 64)}' WHERE repository_id='{repositoryId}' AND new_revision=1;");
                break;
            case "missing_first":
                Execute($"DELETE FROM local_repository_history WHERE repository_id='{repositoryId}' AND new_revision=1;");
                break;
            case "missing_latest":
                Execute($"DELETE FROM local_repository_history WHERE repository_id='{repositoryId}' AND new_revision=2;");
                break;
            case "head_wrong_kind":
                ExecuteUnchecked($"UPDATE local_repository_locator_heads SET kind='other' WHERE repository_id='{repositoryId}';");
                break;
            case "head_wrong_owner":
                ExecuteUnchecked($"UPDATE local_repository_locator_heads SET locator_id='{NextUuid(default)}' WHERE repository_id='{repositoryId}';");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    internal void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal void ExecuteUnchecked(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; {sql}";
        command.ExecuteNonQuery();
    }

    internal long ScalarLong(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal string ScalarText(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal string[] QueryStrings(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1;";
        command.ExecuteNonQuery();
        return connection;
    }

    internal static string RepositoryId(int value) => $"01900000-{value & 0xffff:x4}-7000-8000-{value:x12}";
    internal static string SessionId(int value) => $"02900000-{value & 0xffff:x4}-7000-8000-{value:x12}";

    private string NextUuid(DateTimeOffset _) => RepositoryId(Interlocked.Increment(ref nextId));

    public void Dispose() => temp.Dispose();
}

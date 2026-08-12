using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CostConfigurationApplicationServiceTests
{
    [Fact]
    public void ReadSurfaces_ProjectOnlyTheCurrentProviderCatalogAndPersistedConfiguration()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var application = database.CreateApplication(clock, catalog);

        var current = application.ReadCurrentConfiguration();
        var catalogPage = application.ReadCatalog(null, 50);

        Assert.True(current.Success);
        Assert.Equal("cost.configuration-read.v1", current.Value!.SchemaVersion);
        Assert.Equal("unconfigured", current.Value!.CatalogState);
        Assert.Equal(catalog.CatalogSha256, current.Value.ProviderCatalogSha256);
        Assert.Null(current.Value.Configuration);
        Assert.True(catalogPage.Success);
        Assert.Equal("cost.catalog.v1", catalogPage.Value!.SchemaVersion);
        Assert.Equal(catalog.CatalogSha256, catalogPage.Value!.CatalogSha256);
        Assert.NotEmpty(catalogPage.Value.Entries);
        var projection = System.Text.Json.JsonSerializer.Serialize(catalogPage.Value);
        Assert.DoesNotContain("rate", projection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quantity", projection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alias", projection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limitation", projection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreviewCommitAndReplay_PreserveExactHeadCatalogSelectionAndLocation()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var application = database.CreateApplication(clock, catalog);
        var sessionId = database.InsertResolvedSession();
        var proposal = Proposal("1.2.3");

        var preview = application.PreviewConfiguration(proposal);
        var committed = application.CommitConfiguration(preview.Value!);
        var replay = application.CommitConfiguration(preview.Value!);
        var current = application.ReadCurrentConfiguration();
        var version = application.ReadConfigurationVersion(committed.Value!.ConfigurationId);

        Assert.True(preview.Success);
        Assert.Equal(1, preview.Value!.ProposedMatchCount);
        Assert.Equal(0, preview.Value.CurrentMatchCount);
        Assert.Equal("exact", preview.Value.CurrentMatchCountState);
        Assert.Equal(0, preview.Value.OverlapCount);
        Assert.True(committed.Success);
        Assert.Equal(committed.Value, replay.Value);
        Assert.Equal(
            $"/api/costs/v1/configurations/{committed.Value.ConfigurationId}",
            committed.Location);
        Assert.Equal(committed.Location, replay.Location);
        Assert.Equal(1, current.Value!.SelectedSessionCount);
        Assert.Equal("exact", current.Value.SelectedSessionCountState);
        Assert.Equal(committed.Value.ConfigurationId, version.Value!.ConfigurationId);
        Assert.Equal("cost.configuration-version.v1", version.Value.SchemaVersion);
        Assert.Equal(sessionId, Assert.Single(database.ReadSessionIds()));
    }

    [Fact]
    public void PreviewRequestConsumer_RequiresExactCanonicalVersionedBytes()
    {
        var request = Proposal("1.2.3");
        var canonical = CostConfigurationPreviewRequestCanonicalJsonV1.Serialize(request);
        var future = canonical
            .AsSpan()
            .ToArray();
        var schema = System.Text.Encoding.UTF8.GetBytes(
            "cost.configuration-preview-request.v1");
        var schemaStart = future.AsSpan().IndexOf(schema);
        future[schemaStart + schema.Length - 1] = (byte)'2';
        var noncanonical = canonical.Concat([(byte)' ']).ToArray();

        var accepted = CostConfigurationPreviewRequestConsumerV1.Consume(canonical);
        var unsupported =
            CostConfigurationPreviewRequestConsumerV1.Consume(future);
        var rejected =
            CostConfigurationPreviewRequestConsumerV1.Consume(noncanonical);

        Assert.Equal(CostConsumerStatus.Success, accepted.Status);
        Assert.Equal(CostConsumerStatus.Unsupported, unsupported.Status);
        Assert.Equal(CostConsumerStatus.Invalid, rejected.Status);
    }

    [Fact]
    public void PreviewRequest_RejectsProviderBillingAndRouteInference()
    {
        Assert.Throws<ArgumentException>(() =>
            CostConfigurationPreviewRequestCanonicalJsonV1.Create(
                [
                    new(
                        "github-copilot-vscode",
                        "1.2.3",
                        "synthetic-capability.v1",
                        PricingProviders.GitHubCopilot,
                        PricingBillingModes.AnthropicApiTokens,
                        PricingRoutes.StandardGlobal)
                ],
                []));
    }

    [Fact]
    public void PreviewConfiguration_ExpiresReceiptsAtFifteenMinutesAndCapsAtThirtyTwo()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var application = database.CreateApplication(
            clock,
            PricingCatalog.Create(BundledPricingRegistry.Load()));

        for (var index = 0; index < 32; index++)
            Assert.True(application.PreviewConfiguration(Proposal($"1.2.{index}")).Success);

        var capacity = application.PreviewConfiguration(Proposal("1.2.32"));
        clock.UtcNow = clock.UtcNow.AddMinutes(15);
        var afterExpiry = application.PreviewConfiguration(Proposal("1.2.33"));

        Assert.False(capacity.Success);
        Assert.Equal("cost_preview_capacity_reached", capacity.ErrorCode);
        Assert.True(afterExpiry.Success);
        Assert.Equal(clock.UtcNow, afterExpiry.Value!.Configuration.CreatedAtUtc);
    }

    [Fact]
    public void CommitConfiguration_UsesClosedConflictPrecedence()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var application = database.CreateApplication(clock, catalog);
        var preview = application.PreviewConfiguration(Proposal("1.2.3")).Value!;
        var changedSelection = preview with { SelectionDigest = new string('f', 64) };

        var stalePreview = application.CommitConfiguration(changedSelection);

        Assert.False(stalePreview.Success);
        Assert.Equal("cost_invalid_configuration", stalePreview.ErrorCode);
    }

    [Fact]
    public void PreviewConfiguration_UsesTwoThousandAndOneOnlyAsALowerBoundForCurrent()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var application = database.CreateApplication(clock, catalog);
        var broad = application.PreviewConfiguration(Proposal("1.2.3")).Value!;
        Assert.True(application.CommitConfiguration(broad).Success);
        database.InsertResolvedSessions(2_001);

        var narrowed = application.PreviewConfiguration(
            new CostConfigurationPreviewRequestV1(
                "cost.configuration-preview-request.v1",
                [],
                []));
        var current = application.ReadCurrentConfiguration();

        Assert.True(narrowed.Success);
        Assert.Equal(0, narrowed.Value!.ProposedMatchCount);
        Assert.Equal(2_001, narrowed.Value.CurrentMatchCount);
        Assert.Equal("lower_bound", narrowed.Value.CurrentMatchCountState);
        Assert.Equal(0, narrowed.Value.OverlapCount);
        Assert.Equal("lower_bound", narrowed.Value.OverlapCountState);
        Assert.Equal(2_001, current.Value!.SelectedSessionCount);
        Assert.Equal("lower_bound", current.Value.SelectedSessionCountState);
    }

    [Fact]
    public void CommitConfiguration_DetectsSelectionChangeAfterPreview()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var application = database.CreateApplication(
            clock,
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var preview = application.PreviewConfiguration(Proposal("1.2.3")).Value!;
        database.InsertResolvedSession();

        var result = application.CommitConfiguration(preview);

        Assert.False(result.Success);
        Assert.Equal("cost_selection_changed", result.ErrorCode);
    }

    [Fact]
    public void PreviewAndCurrentConfiguration_ExcludeSessionsWithoutCoreCurrentUseEligibility()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var application = database.CreateApplication(
            clock,
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        database.InsertResolvedSession();
        database.InsertResolvedSession(completeness: "rich");
        database.InsertResolvedSession(hasTerminalFact: false);

        var preview = application.PreviewConfiguration(Proposal("1.2.3"));
        var committed = application.CommitConfiguration(preview.Value!);
        var current = application.ReadCurrentConfiguration();

        Assert.True(preview.Success);
        Assert.Equal(1, preview.Value!.ProposedMatchCount);
        Assert.True(committed.Success);
        Assert.True(current.Success);
        Assert.Equal(1, current.Value!.SelectedSessionCount);
        Assert.Equal("exact", current.Value.SelectedSessionCountState);
    }

    [Fact]
    public void CommitConfiguration_RejectsEligibilityLossAfterPreviewWithoutAppendingHistory()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var application = database.CreateApplication(
            clock,
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var sessionId = database.InsertResolvedSession();
        var preview = application.PreviewConfiguration(Proposal("1.2.3")).Value!;
        database.RemoveTerminalFact(sessionId);

        var result = application.CommitConfiguration(preview);

        Assert.False(result.Success);
        Assert.Equal("cost_selection_changed", result.ErrorCode);
        Assert.Equal((0L, 0L, 0L), database.ReadConfigurationHistoryCounts());
    }

    [Fact]
    public void CurrentConfiguration_DropsEligibilityLossWithoutRewritingCommittedHistory()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var application = database.CreateApplication(
            clock,
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var sessionId = database.InsertResolvedSession();
        var preview = application.PreviewConfiguration(Proposal("1.2.3")).Value!;
        var committed = application.CommitConfiguration(preview).Value!;
        var before = database.ReadConfigurationHistoryBytes();
        database.SetCompleteness(sessionId, "rich");

        var current = application.ReadCurrentConfiguration();
        var version = application.ReadConfigurationVersion(committed.ConfigurationId);

        Assert.True(current.Success);
        Assert.Equal(0, current.Value!.SelectedSessionCount);
        Assert.Equal("exact", current.Value.SelectedSessionCountState);
        Assert.True(version.Success);
        Assert.Equal(committed.ConfigurationId, version.Value!.ConfigurationId);
        Assert.Equal(before, database.ReadConfigurationHistoryBytes());
    }

    [Fact]
    public void CommitConfiguration_DifferentOccupiedSuccessorWinsAsIdempotencyConflict()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var application = database.CreateApplication(
            clock,
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var first = application.PreviewConfiguration(Proposal("1.2.3")).Value!;
        var second = application.PreviewConfiguration(Proposal("1.2.4")).Value!;
        Assert.True(application.CommitConfiguration(first).Success);

        var result = application.CommitConfiguration(second);

        Assert.False(result.Success);
        Assert.Equal("cost_idempotency_conflict", result.ErrorCode);
    }

    [Fact]
    public void CommitConfiguration_ExpiredReceiptIsStalePreview()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var application = database.CreateApplication(
            clock,
            PricingCatalog.Create(BundledPricingRegistry.Load()));
        var preview = application.PreviewConfiguration(Proposal("1.2.3")).Value!;
        clock.UtcNow = clock.UtcNow.AddMinutes(15);

        var result = application.CommitConfiguration(preview);

        Assert.False(result.Success);
        Assert.Equal("cost_stale_preview", result.ErrorCode);
    }

    [Fact]
    public void CommitConfiguration_ProviderCatalogChangePrecedesHeadAndSelectionChecks()
    {
        using var database = new ApplicationDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var firstApplication = database.CreateApplication(clock, catalog);
        var preview = firstApplication.PreviewConfiguration(Proposal("1.2.3")).Value!;
        var changedCatalog = PricingCatalog.Create(
            BundledPricingRegistry.Load() with { SourceId = "changed-reviewed" });
        var changedApplication = database.CreateApplicationFacade(clock, changedCatalog);

        var result = changedApplication.CommitConfiguration(preview);

        Assert.False(result.Success);
        Assert.Equal("cost_catalog_changed", result.ErrorCode);
    }

    private static CostConfigurationPreviewRequestV1 Proposal(string applicationVersion) =>
        new(
            "cost.configuration-preview-request.v1",
            [
                new(
                    "github-copilot-vscode",
                    applicationVersion,
                    "synthetic-capability.v1",
                    PricingProviders.GitHubCopilot,
                    PricingBillingModes.PlanIncluded,
                    PricingRoutes.CodeCompletion)
            ],
            []);

    private sealed class ApplicationDatabase : IDisposable
    {
        private readonly string root;

        internal ApplicationDatabase()
        {
            root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cao-cost-config-app-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "monitor.db");
        }

        internal string Path { get; }

        internal SqliteCostConfigurationApplicationService CreateApplication(
            MutableTimeProvider clock,
            PricingCatalog catalog)
        {
            new SqliteSessionStore(Path).CreateSchema();
            var alertStore = new SqliteAlertEngineStore(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            Assert.Equal(AlertEngineStoreStatusV2.Success, alertStore.InitializeV2().Status);
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                RuntimeBackupSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }
            var store = new SqlitePricingStore(Path, clock);
            store.CreateSchema();
            var canonical = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
            Assert.Equal(
                PricingStoreStatus.Success,
                store.InitializeForMonitorStartup(canonical, catalog.CatalogSha256).Status);
            return CreateApplicationFacade(clock, catalog);
        }

        internal SqliteCostConfigurationApplicationService CreateApplicationFacade(
            MutableTimeProvider clock,
            PricingCatalog catalog)
        {
            var canonical = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
            return new(
                new SqlitePricingStore(Path, clock),
                new SqlitePricingReadStore(Path),
                catalog,
                canonical,
                catalog.CatalogSha256);
        }

        internal string InsertResolvedSession(
            string completeness = "full",
            bool hasTerminalFact = true)
        {
            var sessionId = Guid.NewGuid().ToString("D");
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sessions(
                    session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES($id,'completed',$completeness,'2026-07-24T01:00:00.0000000+00:00',
                    'not_captured','2026-07-24T01:00:00.0000000+00:00',
                    '2026-07-24T01:00:00.0000000+00:00');
                INSERT INTO session_runs(run_id,session_id,source_surface,status)
                VALUES($run,$id,'vscode','completed');
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,source_adapter,source_event_id,
                    type,occurred_at,content_state,source_application_version,
                    terminal_outcome,terminal_policy_version)
                VALUES($event,$id,$run,'vscode','copilot-compatible-hook',$source,
                    'SessionEnd','2026-07-24T01:00:00.0000000+00:00','not_captured','1.2.3',
                    $terminal_outcome,$terminal_policy_version);
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$completeness", completeness);
            command.Parameters.AddWithValue("$run", "run-" + sessionId);
            command.Parameters.AddWithValue("$event", "event-" + sessionId);
            command.Parameters.AddWithValue("$source", "source-" + sessionId);
            command.Parameters.AddWithValue("$terminal_outcome", hasTerminalFact ? "clean" : DBNull.Value);
            command.Parameters.AddWithValue("$terminal_policy_version", hasTerminalFact ? 1 : DBNull.Value);
            command.ExecuteNonQuery();
            return sessionId;
        }

        internal void InsertResolvedSessions(int count)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            for (var index = 0; index < count; index++)
            {
                var sessionId = Guid.NewGuid().ToString("D");
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO sessions(
                        session_id,status,completeness,last_seen_at,raw_retention_state,
                        created_at,updated_at)
                    VALUES($id,'completed','full','2026-07-24T01:00:00.0000000+00:00',
                        'not_captured','2026-07-24T01:00:00.0000000+00:00',
                        '2026-07-24T01:00:00.0000000+00:00');
                    INSERT INTO session_runs(run_id,session_id,source_surface,status)
                    VALUES($run,$id,'vscode','completed');
                    INSERT INTO session_events(
                        event_id,session_id,run_id,source_surface,source_adapter,
                        source_event_id,type,occurred_at,content_state,
                        source_application_version,terminal_outcome,terminal_policy_version)
                    VALUES($event,$id,$run,'vscode','copilot-compatible-hook',$source,
                        'SessionEnd','2026-07-24T01:00:00.0000000+00:00',
                        'not_captured','1.2.3','clean',1);
                    """;
                command.Parameters.AddWithValue("$id", sessionId);
                command.Parameters.AddWithValue("$run", "run-" + sessionId);
                command.Parameters.AddWithValue("$event", "event-" + sessionId);
                command.Parameters.AddWithValue("$source", "source-" + sessionId);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        internal void RemoveTerminalFact(string sessionId)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE session_events SET terminal_outcome=NULL,terminal_policy_version=NULL WHERE session_id=$session;";
            command.Parameters.AddWithValue("$session", sessionId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal void SetCompleteness(string sessionId, string completeness)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET completeness=$completeness WHERE session_id=$session;";
            command.Parameters.AddWithValue("$completeness", completeness);
            command.Parameters.AddWithValue("$session", sessionId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal (long Configurations, long Heads, long Commits) ReadConfigurationHistoryCounts()
        {
            using var connection = Open();
            return (
                Scalar("SELECT COUNT(*) FROM pricing_configurations;"),
                Scalar("SELECT COUNT(*) FROM pricing_configuration_heads;"),
                Scalar("SELECT COUNT(*) FROM pricing_configuration_commits;"));

            long Scalar(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        internal string ReadConfigurationHistoryBytes()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT hex(canonical_blob) FROM pricing_configurations ORDER BY configuration_id;";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            return string.Join("|", values);
        }

        internal IReadOnlyList<string> ReadSessionIds()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id FROM sessions ORDER BY session_id;";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            return values;
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
                ForeignKeys = true,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

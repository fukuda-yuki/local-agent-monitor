using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class PricingQueryFoundationTests
{
    [Fact]
    public void CurrentConfiguration_StrictlyReloadsCanonicalBytesAndCountsExactSelection()
    {
        using var database = new QueryDatabase();
        var clock = new MutableTimeProvider(new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        var store = database.CreatePricingStore(clock);
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(catalogBytes).Status);
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            catalog.CatalogSha256,
            [new(
                "github-copilot-vscode",
                "1.2.3",
                "synthetic-capability.v1",
                PricingProviders.GitHubCopilot,
                PricingBillingModes.PlanIncluded,
                PricingRoutes.CodeCompletion)],
            [],
            clock.UtcNow);
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            0,
            null,
            catalog.CatalogSha256,
            PricingConfigurationSelectionDigestV1.Create([]),
            0,
            0,
            "exact",
            0,
            "exact");
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        Assert.Equal(
            PricingStoreStatus.Success,
            store.AppendConfigurationCommitApplication(
                preview,
                new(catalog.CatalogSha256, catalogBytes),
                []).Status);
        var sessionId = database.InsertResolvedSession();
        var queries = new SqlitePricingReadStore(database.Path);

        var matching = queries.ReadCurrentConfiguration(catalog.CatalogSha256);
        var changed = queries.ReadCurrentConfiguration(new string('f', 64));

        Assert.Equal(PricingReadStatus.Success, matching.Status);
        Assert.Equal("matching", matching.Value!.CatalogState);
        Assert.Equal(1, matching.Value.HeadRevision);
        Assert.Equal(configuration.ConfigurationId, matching.Value.ConfigurationId);
        Assert.Equal(1, matching.Value.SelectedSessionCount);
        Assert.Equal("exact", matching.Value.SelectedSessionCountState);
        Assert.Equal("changed", changed.Value!.CatalogState);
        var version = queries.ReadConfigurationVersion(configuration.ConfigurationId);
        Assert.Equal(PricingReadStatus.Success, version.Status);
        Assert.Equal(1, version.Value!.HeadRevision);
        Assert.Equal(configuration.ConfigurationId, version.Value.ConfigurationId);
        Assert.Equal(configuration.CreatedAtUtc, version.Value.CommittedAtUtc);
        Assert.Equal(
            PricingReadStatus.NotFound,
            queries.ReadConfigurationVersion(
                "cost-configuration-" + new string('a', 64)).Status);

        using var connection = database.Open();
        Execute(connection, "DROP TRIGGER pricing_configurations_no_update;");
        Execute(connection, "PRAGMA ignore_check_constraints=ON;");
        Execute(
            connection,
            "UPDATE pricing_configurations SET canonical_blob=X'7B7D',canonical_sha256='"
            + new string('0', 64)
            + "';");

        Assert.Equal(
            PricingReadStatus.Unavailable,
            queries.ReadCurrentConfiguration(catalog.CatalogSha256).Status);
    }

    [Fact]
    public void CatalogPage_UsesExactCursorAndProjectsNoRatesQuantitiesOrPrivateLocators()
    {
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());

        var first = PricingCatalogReadProjectorV1.Read(catalog, null, 1);
        var second = PricingCatalogReadProjectorV1.Read(catalog, first.Value!.NextAfter, 1);

        Assert.Equal(PricingReadStatus.Success, first.Status);
        Assert.NotNull(first.Value.NextAfter);
        Assert.Equal(PricingReadStatus.Success, second.Status);
        Assert.NotEqual(
            Assert.Single(first.Value.Entries).EntryKey,
            Assert.Single(second.Value!.Entries).EntryKey);
        var json = JsonSerializer.Serialize(first.Value);
        Assert.DoesNotContain("rate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quantity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limitation", json, StringComparison.OrdinalIgnoreCase);
        var encoded = first.Value.NextAfter!["cost-catalog-cursor-v1.".Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        using var cursor = JsonDocument.Parse(Convert.FromBase64String(encoded));
        Assert.Equal(
            ["schema_version", "catalog_sha256", "entry_key"],
            cursor.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            PricingReadStatus.InvalidCursor,
            PricingCatalogReadProjectorV1.Read(catalog, "not-a-cursor", 1).Status);
        Assert.Equal(
            PricingReadStatus.InvalidCursor,
            PricingCatalogReadProjectorV1.Read(catalog, first.Value.NextAfter + "=", 1).Status);
        Assert.Equal(
            PricingReadStatus.InvalidCursor,
            PricingCatalogReadProjectorV1.Read(catalog, first.Value.NextAfter + " ", 1).Status);
        var standardAlphabetAlias = first.Value.NextAfter
            .Replace('-', '+')
            .Replace('_', '/');
        if (standardAlphabetAlias != first.Value.NextAfter)
            Assert.Equal(
                PricingReadStatus.InvalidCursor,
                PricingCatalogReadProjectorV1.Read(catalog, standardAlphabetAlias, 1).Status);
        var changedBytes = Encoding.UTF8.GetBytes(
            $$"""{"schema_version":"cost.catalog.cursor.v1","catalog_sha256":"{{new string('f', 64)}}","entry_key":"{{Assert.Single(first.Value.Entries).EntryKey}}"}""");
        var changedCursor = "cost-catalog-cursor-v1."
            + Convert.ToBase64String(changedBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(
            PricingReadStatus.CatalogChanged,
            PricingCatalogReadProjectorV1.Read(catalog, changedCursor, 1).Status);
    }

    [Fact]
    public void RecalculationRead_ProjectsRequestedRunningAndFixedFailureWithoutCanonicalBytes()
    {
        using var database = new QueryDatabase();
        var calculationTime = new DateTimeOffset(2026, 7, 24, 2, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddMinutes(-2));
        var (store, catalog, configuration) = database.CreateConfiguredPricingStore(clock);
        var sessionId = database.InsertResolvedSession();
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-query-run-0001");
        var target = database.CaptureTarget(sessionId);
        var runId = Guid.CreateVersion7().ToString("D");
        Assert.Equal(
            PricingStoreStatus.Success,
            store.StartRecalculationApplication(
                runId,
                request,
                [target],
                calculationTime).Status);
        var queries = new SqlitePricingReadStore(database.Path);

        var requested = queries.ReadRecalculation(runId);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        var running = queries.ReadRecalculation(runId);
        Assert.Equal(PricingStoreStatus.Success, store.RecoverInterruptedRuns().Status);
        var failed = queries.ReadRecalculation(runId);

        Assert.Equal("requested", requested.Value!.State);
        Assert.Equal("running", running.Value!.State);
        Assert.Equal("failed", failed.Value!.State);
        Assert.Equal("recalculation_interrupted", failed.Value.FailureCode);
        var targetResult = Assert.Single(failed.Value.Targets).Result;
        Assert.Equal("failed", targetResult!.Kind);
        Assert.Equal("recalculation_interrupted", targetResult.Code);
        Assert.Equal(["requested", "running", "failed"], failed.Value.Events.Select(item => item.State));
        Assert.Empty(failed.Value.BudgetResults);
    }

    [Fact]
    public void RecalculationRead_RetainsExactCanonicalBudgetScopeObjects()
    {
        using var database = new QueryDatabase();
        var calculationTime = new DateTimeOffset(2026, 7, 24, 2, 30, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddMinutes(-2));
        var (store, catalog, configuration) = database.CreateConfiguredPricingStore(clock);
        var sessionId = database.InsertResolvedSession();
        using (var connection = database.Open())
            Execute(
                connection,
                $$"""
                INSERT INTO alert_evaluations(
                    evaluation_id,schema_version,input_hash,configuration_version,
                    configuration_hash,canonical_json)
                VALUES
                    ('{{new string('a', 64)}}','alert.evaluation.v2','{{new string('1', 64)}}',
                        'fixture-v2','{{new string('2', 64)}}',
                        '{"evaluation_id":"{{new string('a', 64)}}"}'),
                    ('{{new string('b', 64)}}','alert.evaluation.v2','{{new string('3', 64)}}',
                        'fixture-v2','{{new string('4', 64)}}',
                        '{"evaluation_id":"{{new string('b', 64)}}"}'),
                    ('{{new string('c', 64)}}','alert.evaluation.v2','{{new string('5', 64)}}',
                        'fixture-v2','{{new string('6', 64)}}',
                        '{"evaluation_id":"{{new string('c', 64)}}"}');
                """);
        var cutoff = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);
        CostBudgetScopeV1[] scopes =
        [
            new("session", sessionId, null, null, null),
            new("utc_day", null, "2026-07-24", null, null),
            new("rolling_period", null, null, cutoff, 7),
        ];
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            scopes,
            "pricing-query-scopes-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        Assert.Equal(
            PricingStoreStatus.Success,
            store.StartRecalculationApplication(
                runId,
                request,
                [database.CaptureTarget(sessionId)],
                calculationTime).Status);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        var eligibilityDigest = new string('9', 64);
        var eligible = new[] { sessionId };
        PricingBudgetResultWrite[] results =
        [
            new(
                0, "session",
                PricingAlertCostScopeIdentityV2.Create(
                    "session", null, null, eligibilityDigest, eligible),
                eligibilityDigest, eligible, null, null,
                "session-estimated-cost-threshold", "1", new string('a', 64),
                "no_match", null, null, null),
            new(
                1, "utc_day",
                PricingAlertCostScopeIdentityV2.Create(
                    "utc_day", cutoff.AddDays(-1), cutoff, eligibilityDigest, eligible),
                eligibilityDigest, eligible, cutoff.AddDays(-1), cutoff,
                "daily-estimated-cost-threshold", "1", new string('b', 64),
                "no_match", null, null, null),
            new(
                2, "rolling_period",
                PricingAlertCostScopeIdentityV2.Create(
                    "rolling_period", cutoff.AddDays(-7), cutoff, eligibilityDigest, eligible),
                eligibilityDigest, eligible, cutoff.AddDays(-7), cutoff,
                "period-estimated-cost-threshold", "1", new string('c', 64),
                "no_match", null, null, null),
        ];
        clock.UtcNow = calculationTime.AddSeconds(2);
        Assert.Equal(
            PricingStoreStatus.Success,
            store.AppendRecalculationCompletionApplication(
                runId,
                [PricingTargetCompletionWrite.Unavailable(0, "source_mapping_unavailable")],
                results,
                failure: null).Status);

        var read = new SqlitePricingReadStore(database.Path).ReadRecalculation(runId);

        Assert.Equal(PricingReadStatus.Success, read.Status);
        Assert.Equal(scopes, read.Value!.BudgetResults.Select(result => result.Scope));
        Assert.Equal(sessionId, read.Value.BudgetResults[0].Scope.SessionId);
        Assert.Equal("2026-07-24", read.Value.BudgetResults[1].Scope.UtcDate);
        Assert.Equal(cutoff, read.Value.BudgetResults[2].Scope.CutoffUtc);
        Assert.Equal(7, read.Value.BudgetResults[2].Scope.WindowDays);
    }

    [Fact]
    public void SessionRecalculationHistory_OrdersContiguousAttemptsAndValidatesCursorMembership()
    {
        using var database = new QueryDatabase();
        var calculationTime = new DateTimeOffset(2026, 7, 24, 3, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddMinutes(-2));
        var (store, catalog, configuration) = database.CreateConfiguredPricingStore(clock);
        var sessionId = database.InsertResolvedSession();
        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            var request = CostRecalculationRequestCanonicalJsonV1.Create(
                configuration.ConfigurationId,
                1,
                catalog.CatalogSha256,
                [sessionId],
                [],
                $"pricing-query-history-000{ordinal}");
            var started = store.StartRecalculationApplication(
                Guid.CreateVersion7().ToString("D"),
                request,
                [database.CaptureTarget(sessionId)],
                calculationTime.AddMinutes(ordinal));
            Assert.True(
                started.Status == PricingStoreStatus.Success,
                $"ordinal {ordinal}: {started.Status}");
            clock.UtcNow = calculationTime.AddMinutes(ordinal).AddSeconds(1);
            Assert.Equal(PricingStoreStatus.Success, store.RecoverInterruptedRuns().Status);
        }
        var queries = new SqlitePricingReadStore(database.Path);
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);

        var first = queries.ReadSessionRecalculations(sessionId, catalogBytes, null, 1);
        var second = queries.ReadSessionRecalculations(
            sessionId,
            catalogBytes,
            Assert.Single(first.Value!.Attempts).AttemptRevision,
            1);

        Assert.Equal(2, Assert.Single(first.Value.Attempts).AttemptRevision);
        Assert.Equal(1, Assert.Single(second.Value!.Attempts).AttemptRevision);
        Assert.Null(first.Value.Active);
        Assert.Equal(2, first.Value.NextAfter);
        Assert.Null(second.Value.NextAfter);
        Assert.All(
            first.Value.Attempts.Concat(second.Value.Attempts),
            attempt =>
            {
                Assert.Equal("failed", attempt.Kind);
                Assert.Equal("recalculation_interrupted", attempt.Code);
                Assert.Equal("fresh", attempt.Freshness);
            });
        using (var connection = database.Open())
            Execute(
                connection,
                $"""
                UPDATE sessions SET updated_at='2026-07-24T04:00:00.0000000+00:00'
                WHERE session_id='{sessionId}';
                """);
        Assert.All(
            queries.ReadSessionRecalculations(sessionId, catalogBytes, null, 2).Value!.Attempts,
            attempt => Assert.Equal("stale", attempt.Freshness));
        Assert.Equal(
            PricingReadStatus.InvalidCursor,
            queries.ReadSessionRecalculations(
                database.InsertResolvedSession(),
                catalogBytes,
                1,
                1).Status);
    }

    [Fact]
    public void ActiveRecalculation_IsStaleAfterBudgetOnlyConfigurationHeadChange()
    {
        using var database = new QueryDatabase();
        var calculationTime = new DateTimeOffset(2026, 7, 24, 4, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddMinutes(-2));
        var (store, catalog, configuration) = database.CreateConfiguredPricingStore(clock);
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var sessionId = database.InsertResolvedSession();
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-active-freshness-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        Assert.Equal(
            PricingStoreStatus.Success,
            store.StartRecalculationApplication(
                runId,
                request,
                [database.CaptureTarget(sessionId)],
                calculationTime).Status);
        var queries = new SqlitePricingReadStore(database.Path);
        Assert.Equal(
            "fresh",
            queries.ReadSessionRecalculations(sessionId, catalogBytes, null).Value!.Active!.Freshness);
        var unrelatedCatalog = CreateUnrelatedCatalog();
        var unrelatedCatalogBytes =
            PricingCanonicalJson.SerializeCatalogSnapshot(unrelatedCatalog);
        Assert.Equal(
            "stale",
            queries.ReadSessionRecalculations(
                sessionId,
                unrelatedCatalogBytes,
                null).Value!.Active!.Freshness);

        clock.UtcNow = calculationTime.AddMinutes(1);
        var changed = CostConfigurationCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            catalog.CatalogSha256,
            configuration.SourceEntries,
            [new(
                "session-estimated-cost-threshold",
                "1",
                false,
                "USD",
                "1",
                "2",
                5000,
                "session",
                null)],
            clock.UtcNow);
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            changed,
            1,
            configuration.ConfigurationId,
            catalog.CatalogSha256,
            PricingConfigurationSelectionDigestV1.Create([]),
            0,
            0,
            "exact",
            0,
            "exact");
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        Assert.Equal(
            PricingStoreStatus.Success,
            store.AppendConfigurationCommitApplication(
                preview,
                new(catalog.CatalogSha256, catalogBytes),
                []).Status);

        Assert.Equal(
            "stale",
            queries.ReadSessionRecalculations(sessionId, catalogBytes, null).Value!.Active!.Freshness);
        clock.UtcNow = calculationTime.AddMinutes(2);
        Assert.Equal(PricingStoreStatus.Success, store.RecoverInterruptedRuns().Status);
        Assert.Equal(
            "fresh",
            Assert.Single(
                queries.ReadSessionRecalculations(
                    sessionId,
                    unrelatedCatalogBytes,
                    null).Value!.Attempts).Freshness);
    }

    [Fact]
    public void EstimateAttempt_UsesExactPricingSelectionSemanticFreshness()
    {
        using var database = new QueryDatabase();
        var calculationTime = new DateTimeOffset(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddMinutes(-2));
        var (store, catalog, configuration) =
            database.CreateConfiguredPricingStore(clock, estimateCapable: true);
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var sessionId = database.InsertResolvedSession();
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-estimate-freshness-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var target = database.CaptureTarget(sessionId);
        Assert.Equal(
            PricingStoreStatus.Success,
            store.StartRecalculationApplication(
                runId,
                request,
                [target],
                calculationTime).Status);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        var quantityProvenance = new PricingValueProvenance(
            "synthetic-adapter",
            "pricing-capability.v1",
            "event-1",
            "not_captured",
            "pricing-normalization.v1");
        var configurationProvenance = new PricingValueProvenance(
            "local-monitor-cost-configuration",
            "cost.configuration.v1",
            configuration.ConfigurationId + ".source-entry-000",
            "not_captured",
            "cost-configuration-provenance.v1");
        var estimateRequest = new PricingEstimateRequest(
            PricingContractVersions.EstimateRequest,
            calculationTime,
            null,
            new(
                "github-copilot-vscode",
                "1.2.3",
                sessionId,
                target.SessionEffectiveAtUtc,
                PricingProviders.GitHubCopilot,
                "GPT-5 mini",
                PricingBillingModes.PlanIncluded,
                PricingRoutes.CreditConsumingInteraction,
                PricingSourceCompleteness.Full,
                [],
                quantityProvenance,
                quantityProvenance,
                quantityProvenance,
                configurationProvenance,
                configurationProvenance),
            PricingUsage.Empty);
        var estimate = new PricingEstimationEngine(catalog).Estimate(estimateRequest);
        clock.UtcNow = calculationTime.AddSeconds(2);
        Assert.Equal(
            PricingStoreStatus.Success,
            store.AppendEstimateSuccessApplication(
                runId,
                0,
                0,
                estimateRequest,
                PricingCanonicalJson.Serialize(estimate)).Status);
        var queries = new SqlitePricingReadStore(database.Path);
        Assert.Equal(
            "fresh",
            Assert.Single(
                queries.ReadSessionRecalculations(
                    sessionId,
                    catalogBytes,
                    null).Value!.Attempts).Freshness);
        Assert.Equal(
            "fresh",
            Assert.Single(
                queries.ReadSessionRecalculations(
                    sessionId,
                    PricingCanonicalJson.SerializeCatalogSnapshot(CreateUnrelatedCatalog()),
                    null).Value!.Attempts).Freshness);

        var bundled = BundledPricingRegistry.Load();
        var selected = catalog.Select(
            PricingProviders.GitHubCopilot,
            "GPT-5 mini",
            PricingBillingModes.PlanIncluded,
            PricingRoutes.CreditConsumingInteraction,
            target.SessionEffectiveAtUtc);
        var local = selected.Document with
        {
            RegistryVersion = "query-local-v1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "query-local",
            SourceLabel = "Query freshness local override",
            Entries =
            [
                selected.Entry with
                {
                    EntryId = "query-local-plan",
                    SupersedesEntryKey = selected.EntryKey,
                },
            ],
        };
        var changedCatalog = PricingCatalog.Create(bundled, local);

        Assert.Equal(
            "stale",
            Assert.Single(
                queries.ReadSessionRecalculations(
                    sessionId,
                    PricingCanonicalJson.SerializeCatalogSnapshot(changedCatalog),
                    null).Value!.Attempts).Freshness);
    }

    private static PricingCatalog CreateUnrelatedCatalog()
    {
        var bundled = BundledPricingRegistry.Load();
        var source = bundled.Entries[0];
        return PricingCatalog.Create(
            bundled,
            bundled with
            {
                RegistryVersion = "query-unrelated-v1",
                SourceKind = PricingRegistrySourceKinds.LocalOverride,
                SourceId = "query-unrelated",
                SourceLabel = "Query freshness unrelated entry",
                Entries =
                [
                    source with
                    {
                        EntryId = "query-unrelated-entry",
                        CanonicalModelId = "query-unrelated-model",
                        Aliases = [],
                        SupersedesEntryKey = null,
                    },
                ],
            });
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class QueryDatabase : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"pricing-query-{Guid.NewGuid():N}");

        internal QueryDatabase()
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "monitor.db");
        }

        internal string Path { get; }

        internal SqliteConnection Open()
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

        internal SqlitePricingStore CreatePricingStore(MutableTimeProvider clock)
        {
            new SqliteSessionStore(Path).CreateSchema();
            var alertStore = new SqliteAlertEngineStore(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            Assert.Equal(
                CopilotAgentObservability.Alerts.AlertEngineStoreStatusV2.Success,
                alertStore.InitializeV2().Status);
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                RuntimeBackupSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }
            var store = new SqlitePricingStore(Path, clock);
            store.CreateSchema();
            return store;
        }

        internal (SqlitePricingStore Store, PricingCatalog Catalog, CostConfigurationV1 Configuration)
            CreateConfiguredPricingStore(
                MutableTimeProvider clock,
                bool estimateCapable = false)
        {
            var store = CreatePricingStore(clock);
            var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
            var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
            Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(catalogBytes).Status);
            var configuration = CostConfigurationCanonicalJsonV1.Create(
                null,
                catalog.CatalogSha256,
                [new(
                    "github-copilot-vscode",
                    "1.2.3",
                    estimateCapable ? "pricing-capability.v1" : "synthetic-capability.v1",
                    PricingProviders.GitHubCopilot,
                    PricingBillingModes.PlanIncluded,
                    estimateCapable
                        ? PricingRoutes.CreditConsumingInteraction
                        : PricingRoutes.CodeCompletion)],
                [],
                clock.UtcNow);
            var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
                configuration,
                0,
                null,
                catalog.CatalogSha256,
                PricingConfigurationSelectionDigestV1.Create([]),
                0,
                0,
                "exact",
                0,
                "exact");
            Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
            Assert.Equal(
                PricingStoreStatus.Success,
                store.AppendConfigurationCommitApplication(
                    preview,
                    new(catalog.CatalogSha256, catalogBytes),
                    []).Status);
            return (store, catalog, configuration);
        }

        internal string InsertResolvedSession()
        {
            var sessionId = Guid.NewGuid().ToString("D");
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sessions(
                    session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES($id,'completed','full','2026-07-24T01:00:00.0000000+00:00',
                    'not_captured','2026-07-24T01:00:00.0000000+00:00',
                    '2026-07-24T01:00:00.0000000+00:00');
                INSERT INTO session_runs(run_id,session_id,source_surface,status)
                VALUES($run,$id,'vscode','completed');
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,source_adapter,source_event_id,
                    type,occurred_at,content_state,source_application_version)
                VALUES($event,$id,$run,'vscode','synthetic',$source,
                    'turn','2026-07-24T01:00:00.0000000+00:00','not_captured','1.2.3');
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$run", "run-" + sessionId);
            command.Parameters.AddWithValue("$event", "event-" + sessionId);
            command.Parameters.AddWithValue("$source", "source-" + sessionId);
            command.ExecuteNonQuery();
            return sessionId;
        }

        internal PricingRecalculationTargetCapture CaptureTarget(string sessionId)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var source = SqliteCostSessionSourcePartitionResolverV1.Resolve(
                connection,
                transaction,
                sessionId);
            var attemptRevision = Scalar(
                connection,
                transaction,
                """
                SELECT COALESCE(MAX(attempt_revision),0)
                FROM pricing_session_attempts WHERE session_id=$session;
                """,
                sessionId);
            transaction.Rollback();
            return new(
                sessionId,
                "completed",
                new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero),
                new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero),
                "resolved",
                source.ObservationCount,
                source.Digest,
                source.SourceSurface,
                source.SourceApplicationVersion,
                null,
                null,
                attemptRevision);
        }

        private static long Scalar(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            string sessionId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$session", sessionId);
            return Convert.ToInt64(
                command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture);
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

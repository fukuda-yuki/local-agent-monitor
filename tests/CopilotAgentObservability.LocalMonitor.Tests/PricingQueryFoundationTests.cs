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
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Assert.True(SqlitePricingReadStore.IsEstimateFreshForBudget(
                connection,
                transaction,
                sessionId,
                estimate.EstimateId,
                catalog));
            transaction.Rollback();
        }
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
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Assert.False(SqlitePricingReadStore.IsEstimateFreshForBudget(
                connection,
                transaction,
                sessionId,
                estimate.EstimateId,
                changedCatalog));
            transaction.Rollback();
        }
        using (var connection = database.Open())
        {
            Execute(
                connection,
                $"""
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,source_adapter,source_event_id,
                    type,occurred_at,content_state,source_application_version)
                SELECT
                    'freshness-drift-event','{sessionId}',run_id,'vscode','synthetic',
                    'freshness-drift-event','turn',
                    '2026-07-24T01:00:01.0000000+00:00','not_captured','2.0.0'
                FROM session_runs WHERE session_id='{sessionId}' LIMIT 1;
                """);
            using var transaction = connection.BeginTransaction();
            Assert.False(SqlitePricingReadStore.IsEstimateFreshForBudget(
                connection,
                transaction,
                sessionId,
                estimate.EstimateId,
                catalog));
            transaction.Rollback();
        }
    }

    [Fact]
    public void SessionEstimateHistory_ProjectsExactHeadsDeltaAndSafeFields()
    {
        using var database = new QueryDatabase();
        var calculationTime = new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddMinutes(-2));
        var (store, catalog, configuration) =
            database.CreateConfiguredPricingStore(clock, estimateCapable: true);
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var sessionId = database.InsertResolvedSession();
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
        PricingEstimateRecord? predecessor = null;
        for (var ordinal = 0; ordinal < 2; ordinal++)
        {
            var target = database.CaptureTarget(sessionId);
            var runId = Guid.CreateVersion7().ToString("D");
            var runTime = calculationTime.AddMinutes(ordinal);
            var request = CostRecalculationRequestCanonicalJsonV1.Create(
                configuration.ConfigurationId,
                1,
                catalog.CatalogSha256,
                [sessionId],
                [],
                $"pricing-estimate-history-000{ordinal}");
            Assert.Equal(
                PricingStoreStatus.Success,
                store.StartRecalculationApplication(runId, request, [target], runTime).Status);
            clock.UtcNow = runTime.AddSeconds(1);
            Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
            var estimateRequest = new PricingEstimateRequest(
                PricingContractVersions.EstimateRequest,
                runTime,
                predecessor?.EstimateId,
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
            predecessor = new PricingEstimationEngine(catalog).Estimate(estimateRequest);
            clock.UtcNow = runTime.AddSeconds(2);
            Assert.Equal(
                PricingStoreStatus.Success,
                store.AppendEstimateSuccessApplication(
                    runId,
                    0,
                    0,
                    estimateRequest,
                    PricingCanonicalJson.Serialize(predecessor)).Status);
        }

        var queries = new SqlitePricingReadStore(database.Path);
        var firstPage = queries.ReadSessionEstimates(sessionId, catalogBytes, null, 1);

        Assert.Equal(PricingReadStatus.Success, firstPage.Status);
        Assert.Equal("estimated", firstPage.Value!.CalculationState);
        Assert.Equal(2, firstPage.Value.ActiveHeadRevision);
        Assert.Equal(2, firstPage.Value.LatestAttemptRevision);
        var latest = Assert.Single(firstPage.Value.Items);
        Assert.Equal("complete_total", latest.AmountKind);
        Assert.Equal("available", latest.Delta.State);
        Assert.Equal("both_fresh", latest.Delta.BasisFreshness);
        Assert.Equal(0m, latest.Delta.Amount);
        Assert.DoesNotContain(
            latest.Components.SelectMany(component => new[]
            {
                component.Category,
                component.State,
                component.MissingReason,
            }),
            value => value?.Contains("event-1", StringComparison.Ordinal) == true);
        Assert.NotNull(firstPage.Value.NextAfter);
        var twoItemResponse = firstPage.Value with
        {
            Items = Array.AsReadOnly(new[] { latest, latest with { HeadRevision = 1 } }),
            NextAfter = null,
        };
        var oneItemBytes = JsonSerializer.SerializeToUtf8Bytes(
            firstPage.Value with { NextAfter = latest.EstimateId },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            .Length;
        var fitted = SqlitePricingReadStore.ApplyEstimatePageByteLimit(
            twoItemResponse,
            sourceHasMore: false,
            oneItemBytes);
        Assert.Equal(PricingReadStatus.Success, fitted.Status);
        Assert.Single(fitted.Value!.Items);
        Assert.Equal(latest.EstimateId, fitted.Value.NextAfter);
        var singleItemBytes = JsonSerializer.SerializeToUtf8Bytes(
            firstPage.Value with { NextAfter = null },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            .Length;
        Assert.Equal(
            PricingReadStatus.ResponseTooLarge,
            SqlitePricingReadStore.ApplyEstimatePageByteLimit(
                firstPage.Value,
                sourceHasMore: false,
                singleItemBytes - 1).Status);

        var secondPage = queries.ReadSessionEstimates(
            sessionId,
            catalogBytes,
            firstPage.Value.NextAfter,
            1);
        Assert.Equal(1, Assert.Single(secondPage.Value!.Items).HeadRevision);
        Assert.Null(secondPage.Value.NextAfter);
        Assert.Equal(
            PricingReadStatus.InvalidCursor,
            queries.ReadSessionEstimates(
                database.InsertResolvedSession(),
                catalogBytes,
                firstPage.Value.NextAfter,
                1).Status);

        var exact = queries.ReadSessionEstimate(
            sessionId,
            predecessor!.EstimateId,
            catalogBytes);
        Assert.Equal(PricingReadStatus.Success, exact.Status);
        Assert.Equal(latest.EstimateId, exact.Value!.Item.EstimateId);
        Assert.Equal(latest.HeadRevision, exact.Value.Item.HeadRevision);
        Assert.Equal(latest.Delta.State, exact.Value.Item.Delta.State);
        Assert.Equal(latest.Delta.Amount, exact.Value.Item.Delta.Amount);
        Assert.Equal(latest.Delta.BasisFreshness, exact.Value.Item.Delta.BasisFreshness);
        Assert.Equal(latest.Delta.ChangedFields, exact.Value.Item.Delta.ChangedFields);
        Assert.Equal(latest.Components, exact.Value.Item.Components);
        Assert.Equal(latest.Coverage.RequiredCategories, exact.Value.Item.Coverage.RequiredCategories);
        Assert.Equal(latest.Reasons, exact.Value.Item.Reasons);
        Assert.Equal(
            PricingReadStatus.NotFound,
            queries.ReadSessionEstimate(
                database.InsertResolvedSession(),
                predecessor.EstimateId,
                catalogBytes).Status);

        var failedRunId = Guid.CreateVersion7().ToString("D");
        var failedRequest = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-estimate-history-failure");
        Assert.Equal(
            PricingStoreStatus.Success,
            store.StartRecalculationApplication(
                failedRunId,
                failedRequest,
                [database.CaptureTarget(sessionId)],
                calculationTime.AddMinutes(3)).Status);
        clock.UtcNow = calculationTime.AddMinutes(3).AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.RecoverInterruptedRuns().Status);
        var headWins = queries.ReadSessionEstimates(sessionId, catalogBytes, null, 1).Value!;
        Assert.Equal("estimated", headWins.CalculationState);
        Assert.Equal("failed", headWins.LatestAttempt!.Kind);
        Assert.Equal("recalculation_interrupted", headWins.LatestAttempt.Code);

        var changedCatalog = CreateRelevantChangedCatalog(catalog);
        var catalogStale = queries.ReadSessionEstimates(
            sessionId,
            PricingCanonicalJson.SerializeCatalogSnapshot(changedCatalog),
            null,
            1).Value!;
        Assert.Equal("stale", catalogStale.CalculationState);
        Assert.Equal("stale", Assert.Single(catalogStale.Items).Freshness);

        clock.UtcNow = calculationTime.AddMinutes(4);
        var budgetOnlyConfiguration = database.CommitConfiguration(
            store,
            catalog,
            configuration,
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
            expectedHeadRevision: 1,
            createdAtUtc: clock.UtcNow);
        Assert.Equal(
            "estimated",
            queries.ReadSessionEstimates(sessionId, catalogBytes, null, 1).Value!.CalculationState);

        clock.UtcNow = calculationTime.AddMinutes(5);
        var changedSelectionConfiguration = database.CommitConfiguration(
            store,
            catalog,
            budgetOnlyConfiguration,
            [configuration.SourceEntries[0] with { AdapterCapabilityVersion = "pricing-capability.v2" }],
            budgetOnlyConfiguration.BudgetEntries,
            expectedHeadRevision: 2,
            createdAtUtc: clock.UtcNow);
        Assert.Equal(
            "stale",
            queries.ReadSessionEstimates(sessionId, catalogBytes, null, 1).Value!.CalculationState);

        clock.UtcNow = calculationTime.AddMinutes(6);
        _ = database.CommitConfiguration(
            store,
            catalog,
            changedSelectionConfiguration,
            configuration.SourceEntries,
            changedSelectionConfiguration.BudgetEntries,
            expectedHeadRevision: 3,
            createdAtUtc: clock.UtcNow);
        Assert.Equal(
            "estimated",
            queries.ReadSessionEstimates(sessionId, catalogBytes, null, 1).Value!.CalculationState);

        using (var connection = database.Open())
            Execute(
                connection,
                $"""
                UPDATE sessions SET updated_at='2026-07-25T04:00:00.0000000+00:00'
                WHERE session_id='{sessionId}';
                """);
        var stale = queries.ReadSessionEstimates(sessionId, catalogBytes, null, 1).Value!;
        Assert.Equal("stale", stale.CalculationState);
        var staleLatest = Assert.Single(stale.Items);
        Assert.Equal("stale", staleLatest.Freshness);
        Assert.Equal("not_applicable", staleLatest.AmountKind);
        Assert.Null(staleLatest.Amount);
        Assert.Equal("includes_stale", staleLatest.Delta.BasisFreshness);

        var predecessorCorruptPath = database.Backup("predecessor-corrupt.db");
        var canonicalCorruptPath = database.Backup("canonical-corrupt.db");
        var scalarCorruptPath = database.Backup("scalar-corrupt.db");
        using (var connection = QueryDatabase.OpenAt(predecessorCorruptPath, foreignKeys: false))
        {
            Execute(connection, "DROP TRIGGER pricing_estimate_heads_no_update;");
            Execute(
                connection,
                "UPDATE pricing_estimate_heads SET previous_estimate_id='pricing-estimate-"
                + new string('a', 64)
                + "' WHERE head_revision=2;");
        }
        Assert.Equal(
            PricingReadStatus.Unavailable,
            new SqlitePricingReadStore(predecessorCorruptPath)
                .ReadSessionEstimates(sessionId, catalogBytes, null, 1).Status);
        using (var connection = QueryDatabase.OpenAt(canonicalCorruptPath))
        {
            Execute(connection, "DROP TRIGGER pricing_estimates_no_update;");
            Execute(connection, "PRAGMA ignore_check_constraints=ON;");
            Execute(
                connection,
                "UPDATE pricing_estimates SET canonical_blob=X'7B7D',canonical_sha256='"
                + new string('0', 64)
                + "' WHERE estimate_id='"
                + predecessor.EstimateId
                + "';");
        }
        Assert.Equal(
            PricingReadStatus.Unavailable,
            new SqlitePricingReadStore(canonicalCorruptPath)
                .ReadSessionEstimates(sessionId, catalogBytes, null, 1).Status);
        using (var connection = QueryDatabase.OpenAt(scalarCorruptPath))
        {
            Execute(connection, "DROP TRIGGER pricing_estimates_no_update;");
            Execute(
                connection,
                "UPDATE pricing_estimates SET amount_text='1' WHERE estimate_id='"
                + predecessor.EstimateId
                + "';");
        }
        Assert.Equal(
            PricingReadStatus.Unavailable,
            new SqlitePricingReadStore(scalarCorruptPath)
                .ReadSessionEstimates(sessionId, catalogBytes, null, 1).Status);
    }

    [Fact]
    public void SessionEstimateHistory_ProjectsNoHeadActiveAndTerminalStates()
    {
        using var database = new QueryDatabase();
        var calculationTime = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddMinutes(-1));
        var (store, catalog, configuration) = database.CreateConfiguredPricingStore(clock);
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var sessionId = database.InsertResolvedSession();
        var runId = Guid.CreateVersion7().ToString("D");
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-estimate-no-head-state");
        Assert.Equal(
            PricingStoreStatus.Success,
            store.StartRecalculationApplication(
                runId,
                request,
                [database.CaptureTarget(sessionId)],
                calculationTime).Status);
        var queries = new SqlitePricingReadStore(database.Path);

        var requested = queries.ReadSessionEstimates(sessionId, catalogBytes, null);
        Assert.Equal("requested", requested.Value!.CalculationState);
        Assert.Equal("requested", requested.Value.LatestAttempt!.Kind);
        Assert.Empty(requested.Value.Items);

        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        Assert.Equal(
            "running",
            queries.ReadSessionEstimates(sessionId, catalogBytes, null).Value!.CalculationState);

        clock.UtcNow = calculationTime.AddSeconds(2);
        Assert.Equal(PricingStoreStatus.Success, store.RecoverInterruptedRuns().Status);
        var failed = queries.ReadSessionEstimates(sessionId, catalogBytes, null);
        Assert.Equal("failed", failed.Value!.CalculationState);
        Assert.Equal("failed", failed.Value.LatestAttempt!.Kind);
        Assert.Equal("recalculation_interrupted", failed.Value.LatestAttempt.Code);
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

    private static PricingCatalog CreateRelevantChangedCatalog(PricingCatalog catalog)
    {
        var bundled = BundledPricingRegistry.Load();
        var selected = catalog.Select(
            PricingProviders.GitHubCopilot,
            "GPT-5 mini",
            PricingBillingModes.PlanIncluded,
            PricingRoutes.CreditConsumingInteraction,
            new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        return PricingCatalog.Create(
            bundled,
            selected.Document with
            {
                RegistryVersion = "query-relevant-v1",
                SourceKind = PricingRegistrySourceKinds.LocalOverride,
                SourceId = "query-relevant",
                SourceLabel = "Query relevant local override",
                Entries =
                [
                    selected.Entry with
                    {
                        EntryId = "query-relevant-plan",
                        SupersedesEntryKey = selected.EntryKey,
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
            => OpenAt(Path);

        internal static SqliteConnection OpenAt(string path, bool foreignKeys = true)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                ForeignKeys = foreignKeys,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal string Backup(string fileName)
        {
            var path = System.IO.Path.Combine(root, fileName);
            using var source = Open();
            using var destination = OpenAt(path);
            source.BackupDatabase(destination);
            return path;
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

        internal CostConfigurationV1 CommitConfiguration(
            SqlitePricingStore store,
            PricingCatalog catalog,
            CostConfigurationV1 predecessor,
            IReadOnlyList<CostSourceEntryV1> sourceEntries,
            IReadOnlyList<CostBudgetEntryV1> budgetEntries,
            long expectedHeadRevision,
            DateTimeOffset createdAtUtc)
        {
            var configuration = CostConfigurationCanonicalJsonV1.Create(
                predecessor.ConfigurationId,
                catalog.CatalogSha256,
                sourceEntries,
                budgetEntries,
                createdAtUtc);
            var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
                configuration,
                expectedHeadRevision,
                predecessor.ConfigurationId,
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
                    new(
                        catalog.CatalogSha256,
                        PricingCanonicalJson.SerializeCatalogSnapshot(catalog)),
                    []).Status);
            return configuration;
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
            long? baseHeadRevision;
            string? baseEstimateId;
            using (var head = connection.CreateCommand())
            {
                head.Transaction = transaction;
                head.CommandText =
                    """
                    SELECT head_revision,estimate_id
                    FROM pricing_estimate_heads
                    WHERE session_id=$session
                    ORDER BY head_revision DESC LIMIT 1;
                    """;
                head.Parameters.AddWithValue("$session", sessionId);
                using var reader = head.ExecuteReader();
                if (reader.Read())
                {
                    baseHeadRevision = reader.GetInt64(0);
                    baseEstimateId = reader.GetString(1);
                }
                else
                {
                    baseHeadRevision = null;
                    baseEstimateId = null;
                }
            }
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
                baseHeadRevision,
                baseEstimateId,
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

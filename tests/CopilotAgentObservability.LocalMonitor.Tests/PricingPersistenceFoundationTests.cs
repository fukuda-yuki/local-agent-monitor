using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class PricingPersistenceFoundationTests
{
    [Fact]
    public void PricingSchemaV1_Ensure_CreatesExactComponentAfterRequiredDependencies()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();

        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            PricingSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        using var read = database.Open();
        Assert.Equal(1L, Scalar<long>(read, "SELECT version FROM schema_version WHERE component='pricing';"));
        Assert.True(PricingSchemaV1.IsValid(read, null));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));
        Assert.Equal(
            PricingSchemaV1.OwnedObjects.Select(item => item.Name),
            Names(read, "SELECT name FROM sqlite_schema WHERE name GLOB 'pricing_*' ORDER BY type,name;"));
    }

    [Fact]
    public void PricingSchemaV1_Ensure_RejectsRuntimeBackupMissingWithoutMutation()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies(includeRuntimeBackup: false);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        Assert.Throws<InvalidOperationException>(() => PricingSchemaV1.Ensure(connection, transaction));
        transaction.Rollback();
        Assert.Empty(Names(connection, "SELECT name FROM sqlite_schema WHERE name GLOB 'pricing_*';"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM schema_version WHERE component='pricing';"));
    }

    [Fact]
    public void PricingSchemaV1_Ensure_RejectsPartialNamespaceWithoutRepair()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        using var connection = database.Open();
        Execute(connection, "CREATE TABLE pricing_catalog_snapshots(catalog_sha256 TEXT PRIMARY KEY);");

        using var transaction = connection.BeginTransaction(deferred: false);
        Assert.Throws<InvalidOperationException>(() => PricingSchemaV1.Ensure(connection, transaction));
        transaction.Rollback();

        Assert.Equal(
            ["pricing_catalog_snapshots"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE name GLOB 'pricing_*';"));
    }

    [Fact]
    public void SqlitePricingStore_PutCatalogSnapshot_PreservesExactCanonicalBytesAndFirstTimestamp()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var canonical = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var first = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(first);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();

        var initial = store.PutCatalogSnapshot(canonical);
        clock.UtcNow = first.AddDays(1);
        var replay = store.PutCatalogSnapshot(canonical);
        var loaded = store.GetCatalogSnapshot(catalog.CatalogSha256);

        Assert.Equal(PricingStoreStatus.Success, initial.Status);
        Assert.Equal(PricingStoreStatus.Success, replay.Status);
        Assert.NotNull(loaded);
        Assert.Equal(canonical, loaded.CanonicalBytes);
        Assert.Equal(first, loaded.FirstRecordedAtUtc);
        Assert.Equal(1, loaded.DocumentCount);
    }

    [Fact]
    public void SqlitePricingStore_PutCatalogSnapshot_RejectsHashCollisionWithoutMutation()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var canonical = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(canonical).Status);
        using (var connection = database.Open())
        {
            Execute(connection, "DROP TRIGGER pricing_catalog_snapshots_no_update;");
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pricing_catalog_snapshots SET canonical_blob=$blob WHERE catalog_sha256=$sha;";
            command.Parameters.AddWithValue("$blob", canonical.Concat(new byte[] { 0x20 }).ToArray());
            command.Parameters.AddWithValue("$sha", catalog.CatalogSha256);
            command.ExecuteNonQuery();
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var result = store.PutCatalogSnapshot(canonical);

        Assert.Equal(PricingStoreStatus.Unavailable, result.Status);
        Assert.Null(store.GetCatalogSnapshot(catalog.CatalogSha256));
    }

    [Fact]
    public void CostConfigurationConsumerV1_ReloadsOnlyCanonicalIdentityBoundBytes()
    {
        var catalogSha = new string('a', 64);
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            predecessorConfigurationId: null,
            catalogSha,
            [
                new("github-copilot-vscode", "1.2.3", "pricing-capability.v1", "github_copilot", "github_ai_credits", "credit_consuming_interaction"),
            ],
            [
                new("session-estimated-cost-threshold", "1", false, "USD", "1.25", "2.5", 7500, "session", null),
            ],
            createdAt);
        var canonical = CostConfigurationCanonicalJsonV1.Serialize(configuration);

        var result = CostConfigurationConsumerV1.Consume(canonical);
        canonical[0] = (byte)'[';

        Assert.Equal(CostConsumerStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(configuration.ConfigurationId, result.Value.ConfigurationId);
        Assert.Equal("github-copilot-vscode", Assert.Single(result.Value.SourceEntries).SourceSurface);
        Assert.Equal("session-estimated-cost-threshold", Assert.Single(result.Value.BudgetEntries).RuleId);
        Assert.Equal(createdAt, result.Value.CreatedAtUtc);
    }

    [Fact]
    public void CostConfigurationConsumerV1_RejectsUnknownDuplicateAndFutureSchema()
    {
        var canonical = CostConfigurationCanonicalJsonV1.Serialize(
            CostConfigurationCanonicalJsonV1.Create(
                null,
                new string('a', 64),
                [],
                [],
                new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero)));
        var json = System.Text.Encoding.UTF8.GetString(canonical);

        Assert.Equal(
            CostConsumerStatus.Invalid,
            CostConfigurationConsumerV1.Consume(System.Text.Encoding.UTF8.GetBytes(json.Replace(
                "\"configuration_id\"",
                "\"unexpected\":true,\"configuration_id\"",
                StringComparison.Ordinal))).Status);
        Assert.Equal(
            CostConsumerStatus.Invalid,
            CostConfigurationConsumerV1.Consume(System.Text.Encoding.UTF8.GetBytes(json.Replace(
                "\"catalog_sha256\"",
                "\"catalog_sha256\":\"" + new string('a', 64) + "\",\"catalog_sha256\"",
                StringComparison.Ordinal))).Status);
        Assert.Equal(
            CostConsumerStatus.Unsupported,
            CostConfigurationConsumerV1.Consume(System.Text.Encoding.UTF8.GetBytes(json.Replace(
                "cost.configuration.v1",
                "cost.configuration.v2",
                StringComparison.Ordinal))).Status);
    }

    [Fact]
    public void CostRecalculationRequestCanonicalJsonV1_RejectsDuplicateScopes()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var scope = new CostBudgetScopeV1("session", sessionId, null, null, null);

        Assert.Throws<ArgumentException>(() => CostRecalculationRequestCanonicalJsonV1.Create(
            "cost-configuration-" + new string('a', 64),
            1,
            new string('b', 64),
            [sessionId],
            [scope, scope],
            "pricing-request-test-0001"));
    }

    [Fact]
    public void CostConfigurationPreviewCanonicalJsonV1_RejectsInconsistentBoundedCounts()
    {
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            new string('a', 64),
            [],
            [],
            new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero));

        Assert.Throws<ArgumentException>(() => CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            0,
            null,
            configuration.CatalogSha256,
            new string('b', 64),
            1,
            2001,
            "exact",
            0,
            "exact"));
        Assert.Throws<ArgumentException>(() => CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            0,
            null,
            configuration.CatalogSha256,
            new string('b', 64),
            1,
            2001,
            "lower_bound",
            1,
            "exact"));
    }

    [Fact]
    public void CostConfigurationCommitConsumerV1_ChangesOnlyTheRootSchemaToken()
    {
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            new string('a', 64),
            [new("surface", "cost.configuration-preview.v1", "capability.v1", "provider", "billing", "route")],
            [],
            new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero));
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            0,
            null,
            configuration.CatalogSha256,
            new string('b', 64),
            1,
            0,
            "exact",
            0,
            "exact");

        var canonical = CostConfigurationCommitConsumerV1.SerializeRequest(preview);
        var consumed = CostConfigurationCommitConsumerV1.ConsumeRequest(canonical);

        Assert.Contains(
            "\"application_version\":\"cost.configuration-preview.v1\"",
            System.Text.Encoding.UTF8.GetString(canonical),
            StringComparison.Ordinal);
        Assert.Equal(CostConsumerStatus.Success, consumed.Status);
        Assert.Equal(
            "cost.configuration-preview.v1",
            Assert.Single(consumed.Value!.Configuration.SourceEntries).ApplicationVersion);
    }

    [Fact]
    public void SqlitePricingStore_PutConfigurationPreview_EnforcesFifteenMinuteTtlAndCapacity()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(createdAt);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(catalogBytes).Status);

        for (var index = 0; index < 32; index++)
        {
            var preview = CreatePreview(catalog.CatalogSha256, createdAt, index);
            Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        }

        var overflow = CreatePreview(catalog.CatalogSha256, createdAt.AddSeconds(1), 32);
        Assert.Equal(PricingStoreStatus.CapacityReached, store.PutConfigurationPreview(overflow).Status);

        clock.UtcNow = createdAt.AddMinutes(15);
        var afterExpiry = CreatePreview(catalog.CatalogSha256, createdAt.AddMinutes(15), 33);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(afterExpiry).Status);
        Assert.Equal(1, store.CountConfigurationPreviews());
    }

    [Fact]
    public void SqlitePricingStore_CommitConfiguration_AppendsImmutableHeadReceiptAndConsumesPreviewAtomically()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(createdAt);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var preview = CreatePreview(
            catalog.CatalogSha256,
            createdAt,
            1);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);

        var committedAt = new DateTimeOffset(2026, 7, 24, 1, 3, 0, TimeSpan.Zero);
        clock.UtcNow = committedAt;
        var initial = store.CommitConfiguration(preview);
        clock.UtcNow = committedAt.AddDays(1);
        var replay = store.CommitConfiguration(preview);

        Assert.Equal(PricingStoreStatus.Success, initial.Status);
        Assert.Equal(PricingStoreStatus.Success, replay.Status);
        Assert.Equal(preview.Configuration.ConfigurationId, initial.Value!.ConfigurationId);
        Assert.Equal(1, initial.Value.HeadRevision);
        Assert.Equal(initial.Value, replay.Value);
        Assert.Equal(0, store.CountConfigurationPreviews());
        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pricing_configurations;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pricing_configuration_heads;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pricing_configuration_commits;"));
        Assert.Throws<SqliteException>(() => Execute(
            connection,
            "UPDATE pricing_configuration_heads SET committed_at_utc=committed_at_utc;"));
    }

    [Fact]
    public void SqlitePricingStore_RecoverInterruptedRuns_AppendsFailedResultEventAndAttempt()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var createdAt = new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero);
        var clock = new MutableTimeProvider(createdAt);
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var preview = CreatePreview(catalog.CatalogSha256, createdAt, 1);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = preview.Configuration.CreatedAtUtc.AddMinutes(1);
        Assert.Equal(PricingStoreStatus.Success, store.CommitConfiguration(preview).Status);
        var sessionId = Guid.NewGuid().ToString("D");
        using (var connection = database.Open())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO sessions(session_id) VALUES($id);";
            insert.Parameters.AddWithValue("$id", sessionId);
            insert.ExecuteNonQuery();
        }
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            preview.Configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-recovery-test-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var calculationTime = new DateTimeOffset(2026, 7, 24, 2, 0, 0, TimeSpan.Zero);
        var target = new PricingRecalculationTargetWrite(
            sessionId,
            "completed",
            calculationTime.AddHours(-1),
            calculationTime.AddMinutes(-1),
            "resolved",
            1,
            new string('b', 64),
            "github-copilot-vscode",
            "1.2.3",
            null,
            null,
            0);

        var started = store.StartRecalculation(runId, request, [target], calculationTime);
        var replayed = store.StartRecalculation(
            Guid.CreateVersion7().ToString("D"),
            request,
            [target],
            calculationTime.AddDays(1));
        Assert.Equal(PricingStoreStatus.Success, started.Status);
        Assert.Equal(runId, started.Value);
        Assert.Equal(PricingStoreStatus.Success, replayed.Status);
        Assert.Equal(runId, replayed.Value);
        clock.UtcNow = calculationTime.AddMinutes(5);
        Assert.Equal(PricingStoreStatus.Success, store.RecoverInterruptedRuns().Status);

        using var read = database.Open();
        Assert.Equal(
            ["requested", "failed"],
            Names(read, "SELECT event_kind FROM pricing_recalculation_events ORDER BY event_sequence;"));
        Assert.Equal("recalculation_interrupted", Scalar<string>(read, "SELECT failure_code FROM pricing_recalculation_events WHERE event_kind='failed';"));
        Assert.Equal("failed", Scalar<string>(read, "SELECT result_kind FROM pricing_recalculation_target_results;"));
        Assert.Equal(1L, Scalar<long>(read, "SELECT attempt_revision FROM pricing_session_attempts;"));
        Assert.Equal("recalculation_interrupted", Scalar<string>(read, "SELECT result_code FROM pricing_session_attempts;"));
    }

    [Fact]
    public void SqlitePricingStore_AppendEstimateSuccess_PersistsExactBytesAndContiguousHead()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var sessionId = Guid.NewGuid().ToString("D");
        var calculationTime = new DateTimeOffset(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddHours(-1));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(
            PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            catalog.CatalogSha256,
            [new("github-copilot-vscode", "1.2.3", "pricing-capability.v1", "github_copilot", "github_ai_credits", "credit_consuming_interaction")],
            [],
            calculationTime.AddHours(-1));
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration, 0, null, catalog.CatalogSha256, new string('c', 64), 1, 0, "exact", 0, "exact");
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = configuration.CreatedAtUtc.AddMinutes(1);
        Assert.Equal(PricingStoreStatus.Success, store.CommitConfiguration(preview).Status);
        using (var connection = database.Open())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO sessions(session_id) VALUES($id);";
            insert.Parameters.AddWithValue("$id", sessionId);
            insert.ExecuteNonQuery();
        }
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId, 1, catalog.CatalogSha256, [sessionId], [], "pricing-estimate-test-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var target = new PricingRecalculationTargetWrite(
            sessionId, "completed", calculationTime.AddMinutes(-30), calculationTime.AddMinutes(-1),
            "resolved", 1, new string('d', 64), "github-copilot-vscode", "1.2.3", null, null, 0);
        Assert.Equal(PricingStoreStatus.Success, store.StartRecalculation(runId, request, [target], calculationTime).Status);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        var quantityProvenance = new PricingValueProvenance(
            "synthetic-adapter", "pricing-capability.v1", "event-1", "not_captured", "pricing-normalization.v1");
        var configurationProvenance = new PricingValueProvenance(
            "local-monitor-cost-configuration", "cost.configuration.v1",
            configuration.ConfigurationId + ".source-entry-000", "not_captured", "cost-configuration-provenance.v1");
        var estimate = new PricingEstimationEngine(catalog).Estimate(new PricingEstimateRequest(
            PricingContractVersions.EstimateRequest,
            calculationTime,
            null,
            new PricingEstimateSource(
                "github-copilot-vscode", "1.2.3", sessionId, target.SessionEffectiveAtUtc,
                PricingProviders.GitHubCopilot, "GPT-5 mini", PricingBillingModes.GitHubAiCredits,
                PricingRoutes.CreditConsumingInteraction, PricingSourceCompleteness.Full, [],
                quantityProvenance, quantityProvenance, quantityProvenance,
                configurationProvenance, configurationProvenance),
            new PricingUsage(
                new(1000, quantityProvenance),
                new(2000, quantityProvenance),
                new(500, quantityProvenance),
                null, null, null, null, null)));
        var estimateBytes = PricingCanonicalJson.Serialize(estimate);

        clock.UtcNow = calculationTime.AddSeconds(2);
        Assert.Equal(
            PricingStoreStatus.Success,
            store.AppendEstimateSuccess(runId, 0, 0, estimateBytes).Status);

        using var read = database.Open();
        Assert.Equal(estimateBytes, (byte[])Scalar<object>(read, "SELECT canonical_blob FROM pricing_estimates;"));
        Assert.Equal(1L, Scalar<long>(read, "SELECT head_revision FROM pricing_estimate_heads;"));
        Assert.Equal(estimate.EstimateId, Scalar<string>(read, "SELECT estimate_id FROM pricing_estimate_heads;"));
        Assert.Equal("estimate", Scalar<string>(read, "SELECT result_kind FROM pricing_session_attempts;"));
        Assert.Equal("succeeded", Scalar<string>(read, "SELECT event_kind FROM pricing_recalculation_events WHERE event_sequence=2;"));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));
        Execute(read, "DROP TRIGGER pricing_estimates_no_update;");
        using (var corrupt = read.CreateCommand())
        {
            corrupt.CommandText = "UPDATE pricing_estimates SET canonical_blob=$blob;";
            corrupt.Parameters.AddWithValue("$blob", estimateBytes.Concat(new byte[] { 0x20 }).ToArray());
            corrupt.ExecuteNonQuery();
        }
        Execute(
            read,
            Assert.Single(PricingSchemaV1.OwnedObjects, item => item.Name == "pricing_estimates_no_update").Sql);
        Assert.False(PricingSchemaV1.ValidateRows(read, null));
    }

    [Fact]
    public void SqlitePricingStore_CompleteRecalculation_PersistsUnavailableAttemptAndBudgetResult()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var calculationTime = new DateTimeOffset(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddHours(-1));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            catalog.CatalogSha256,
            [],
            [new("session-estimated-cost-threshold", "1", true, "USD", "1", "2", 5000, "session", null)],
            calculationTime.AddHours(-1));
        var preview = CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration, 0, null, catalog.CatalogSha256, new string('c', 64), 1, 0, "exact", 0, "exact");
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = calculationTime.AddMinutes(-59);
        Assert.Equal(PricingStoreStatus.Success, store.CommitConfiguration(preview).Status);
        var sessionId = Guid.NewGuid().ToString("D");
        using (var connection = database.Open())
        {
            Execute(connection, $"INSERT INTO sessions(session_id) VALUES('{sessionId}');");
            Execute(
                connection,
                $"""
                INSERT INTO alert_evaluations(evaluation_id,schema_version)
                VALUES('{new string('e', 64)}','alert.evaluation.v2'),
                      ('{new string('f', 64)}','alert.evaluation.v2'),
                      ('{new string('a', 64)}','alert.evaluation.v2');
                INSERT INTO alert_receipts(alert_id,evaluation_id,receipt_ordinal)
                VALUES('{new string('1', 64)}','{new string('f', 64)}',0);
                INSERT INTO alert_suppressions(evaluation_id,suppression_ordinal)
                VALUES('{new string('a', 64)}',0);
                """);
        }
        var rollingCutoff = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [
                new("session", sessionId, null, null, null),
                new("utc_day", null, "2026-07-24", null, null),
                new("rolling_period", null, null, rollingCutoff, 7),
            ],
            "pricing-budget-test-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var target = new PricingRecalculationTargetWrite(
            sessionId, "completed", calculationTime.AddMinutes(-30), calculationTime.AddMinutes(-1),
            "missing", 0, new string('d', 64), null, null, null, null, 0);
        Assert.Equal(PricingStoreStatus.Success, store.StartRecalculation(runId, request, [target], calculationTime).Status);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        clock.UtcNow = calculationTime.AddSeconds(2);

        var result = store.CompleteRecalculation(
            runId,
            [PricingTargetCompletionWrite.Unavailable(0, "source_mapping_unavailable")],
            [
                new PricingBudgetResultWrite(
                    0,
                    "session",
                    "cost-scope-" + new string('a', 64),
                    null,
                    null,
                    "session-estimated-cost-threshold",
                    "1",
                    new string('e', 64),
                    "no_match",
                    null,
                    null,
                    null),
                new PricingBudgetResultWrite(
                    1,
                    "utc_day",
                    "cost-scope-" + new string('b', 64),
                    new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero),
                    "daily-estimated-cost-threshold",
                    "1",
                    new string('f', 64),
                    "receipt",
                    new string('1', 64),
                    null,
                    null),
                new PricingBudgetResultWrite(
                    2,
                    "rolling_period",
                    "cost-scope-" + new string('c', 64),
                    rollingCutoff.AddDays(-7),
                    rollingCutoff,
                    "period-estimated-cost-threshold",
                    "1",
                    new string('a', 64),
                    "suppression",
                    null,
                    0,
                    "rule_disabled"),
            ],
            failure: null);

        Assert.Equal(PricingStoreStatus.Success, result.Status);
        using var read = database.Open();
        Assert.Equal("unavailable", Scalar<string>(read, "SELECT result_kind FROM pricing_recalculation_target_results;"));
        Assert.Equal("source_mapping_unavailable", Scalar<string>(read, "SELECT result_code FROM pricing_session_attempts;"));
        Assert.Equal(
            ["no_match", "receipt", "suppression"],
            Names(read, "SELECT outcome_kind FROM pricing_recalculation_budget_results ORDER BY scope_ordinal;"));
        Assert.Equal("succeeded", Scalar<string>(read, "SELECT event_kind FROM pricing_recalculation_events WHERE event_sequence=2;"));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));

        Execute(read, "UPDATE alert_evaluations SET schema_version='alert.evaluation.v1';");
        Assert.False(PricingSchemaV1.ValidateRows(read, null));
        Execute(read, "UPDATE alert_evaluations SET schema_version='alert.evaluation.v2';");
        Assert.True(PricingSchemaV1.ValidateRows(read, null));

        Execute(read, "DROP TRIGGER pricing_session_attempts_no_update;");
        Execute(read, "PRAGMA ignore_check_constraints=ON;");
        Execute(read, "UPDATE pricing_session_attempts SET result_code='invented_code';");
        Execute(read, "PRAGMA ignore_check_constraints=OFF;");
        Execute(
            read,
            Assert.Single(
                PricingSchemaV1.OwnedObjects,
                item => item.Name == "pricing_session_attempts_no_update").Sql);
        Assert.False(PricingSchemaV1.ValidateRows(read, null));
    }

    [Fact]
    public void SqlitePricingStore_CompleteRecalculation_PersistsClosedFailedResultAndAttempt()
    {
        using var database = new PricingDatabase();
        database.CreateDependencies();
        var calculationTime = new DateTimeOffset(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(calculationTime.AddHours(-1));
        var store = new SqlitePricingStore(database.Path, clock);
        store.CreateSchema();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(PricingCanonicalJson.SerializeCatalogSnapshot(catalog)).Status);
        var preview = CreatePreview(catalog.CatalogSha256, calculationTime.AddHours(-1), 1);
        Assert.Equal(PricingStoreStatus.Success, store.PutConfigurationPreview(preview).Status);
        clock.UtcNow = calculationTime.AddMinutes(-59);
        Assert.Equal(PricingStoreStatus.Success, store.CommitConfiguration(preview).Status);
        var sessionId = Guid.NewGuid().ToString("D");
        using (var connection = database.Open())
            Execute(connection, $"INSERT INTO sessions(session_id) VALUES('{sessionId}');");
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            preview.Configuration.ConfigurationId,
            1,
            catalog.CatalogSha256,
            [sessionId],
            [],
            "pricing-failure-test-0001");
        var runId = Guid.CreateVersion7().ToString("D");
        var target = new PricingRecalculationTargetWrite(
            sessionId, "failed", calculationTime.AddMinutes(-30), calculationTime.AddMinutes(-1),
            "incomplete", 257, new string('d', 64), null, null, null, null, 0);
        Assert.Equal(PricingStoreStatus.Success, store.StartRecalculation(runId, request, [target], calculationTime).Status);
        clock.UtcNow = calculationTime.AddSeconds(1);
        Assert.Equal(PricingStoreStatus.Success, store.MarkRecalculationRunning(runId).Status);
        clock.UtcNow = calculationTime.AddSeconds(2);

        var result = store.CompleteRecalculation(
            runId,
            [PricingTargetCompletionWrite.Failed(0, "source_adapter_failed")],
            [],
            new PricingRunFailureWrite("adapter", "target", 0, "source_adapter_failed"));

        Assert.Equal(PricingStoreStatus.Success, result.Status);
        using var read = database.Open();
        Assert.Equal("failed", Scalar<string>(read, "SELECT result_kind FROM pricing_recalculation_target_results;"));
        Assert.Equal("source_adapter_failed", Scalar<string>(read, "SELECT result_code FROM pricing_session_attempts;"));
        Assert.Equal("failed", Scalar<string>(read, "SELECT event_kind FROM pricing_recalculation_events WHERE event_sequence=2;"));
        Assert.Equal("adapter", Scalar<string>(read, "SELECT failure_phase FROM pricing_recalculation_events WHERE event_sequence=2;"));
        Assert.True(PricingSchemaV1.ValidateRows(read, null));
    }

    private static CostConfigurationPreviewV1 CreatePreview(
        string catalogSha,
        DateTimeOffset createdAt,
        int discriminator)
    {
        var configuration = CostConfigurationCanonicalJsonV1.Create(
            null,
            catalogSha,
            [],
            [],
            createdAt);
        return CostConfigurationPreviewCanonicalJsonV1.Create(
            configuration,
            expectedHeadRevision: 0,
            expectedConfigurationId: null,
            catalogSha,
            Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(discriminator))).ToLowerInvariant(),
            proposedMatchCount: discriminator,
            currentMatchCount: 0,
            currentMatchCountState: "exact",
            overlapCount: 0,
            overlapCountState: "exact");
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar()!;
        return value is T typed
            ? typed
            : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Names(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class PricingDatabase : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"pricing-persistence-{Guid.NewGuid():N}");

        internal PricingDatabase()
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

        internal void CreateDependencies(bool includeRuntimeBackup = true)
        {
            using var connection = Open();
            Execute(
                connection,
                """
                CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);
                INSERT INTO schema_version(component,version) VALUES('session',13),('alert_engine',2);
                CREATE TABLE sessions(session_id TEXT PRIMARY KEY);
                CREATE TABLE alert_evaluations(evaluation_id TEXT PRIMARY KEY,schema_version TEXT NOT NULL);
                CREATE TABLE alert_receipts(alert_id TEXT PRIMARY KEY,evaluation_id TEXT NOT NULL,receipt_ordinal INTEGER NOT NULL,UNIQUE(evaluation_id,receipt_ordinal));
                CREATE TABLE alert_suppressions(evaluation_id TEXT NOT NULL,suppression_ordinal INTEGER NOT NULL,PRIMARY KEY(evaluation_id,suppression_ordinal));
                """);
            if (includeRuntimeBackup)
            {
                Execute(connection, "INSERT INTO schema_version(component,version) VALUES('runtime_backup',1);");
            }
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

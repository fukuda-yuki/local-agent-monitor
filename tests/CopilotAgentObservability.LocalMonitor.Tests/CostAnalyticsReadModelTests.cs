using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CostAnalyticsReadModelTests
{
    private static readonly DateTimeOffset From =
        new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadAnalytics_uses_pricing_store_snapshot_and_returns_complete_empty_unconfigured_view()
    {
        using var database = new AnalyticsDatabase();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var store = database.CreatePricingStore();
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(catalogBytes).Status);

        var result = new SqlitePricingReadStore(database.Path)
            .ReadAnalytics(Query(limit: 50), catalogBytes);

        Assert.Equal(PricingReadStatus.Success, result.Status);
        Assert.Equal("complete", result.Value!.State);
        Assert.Equal(0, result.Value.EligibleSessionCount);
        Assert.Empty(result.Value.Groups);
        Assert.Empty(result.Value.RangeTotals);
        Assert.Empty(result.Value.DailyTotals);
    }

    [Fact]
    public void ReadAnalytics_uses_exact_resolved_selection_and_withholds_unsafe_session_labels()
    {
        using var database = new AnalyticsDatabase();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var catalogBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var store = database.CreatePricingStore();
        Assert.Equal(
            PricingStoreStatus.Success,
            store.PutCatalogSnapshot(catalogBytes).Status);
        database.CommitConfiguration(store, catalog, catalogBytes);
        var sessionId = database.InsertResolvedSession(
            "safe-repository",
            @"C:\private\workspace");

        var result = new SqlitePricingReadStore(database.Path)
            .ReadAnalytics(Query(limit: 50), catalogBytes);

        Assert.Equal(PricingReadStatus.Success, result.Status);
        Assert.Equal(1, result.Value!.EligibleSessionCount);
        Assert.Equal(1, result.Value.Overall!.MissingSessionCount);
        var group = Assert.Single(result.Value.Groups);
        Assert.Equal("safe-repository", group.Repository);
        Assert.Null(group.Workspace);
        Assert.Contains("workspace", group.UnknownDimensions);
        Assert.DoesNotContain(sessionId, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public void ReadAnalytics_rejects_query_and_cursor_before_opening_the_database()
    {
        var store = new SqlitePricingReadStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".db"));
        var catalog = PricingCanonicalJson.SerializeCatalogSnapshot(
            PricingCatalog.Create(BundledPricingRegistry.Load()));

        Assert.Equal(
            PricingReadStatus.InvalidQuery,
            store.ReadAnalytics(
                Query(limit: 50) with { Repository = "access_token=secret" },
                catalog).Status);
        Assert.Equal(
            PricingReadStatus.InvalidCursor,
            store.ReadAnalytics(
                Query(limit: 50, after: "cost-analytics-cursor-v1.not-base64"),
                catalog).Status);
    }

    [Theory]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("-----BEGIN CERTIFICATE-----")]
    [InlineData("access_token=secret")]
    [InlineData("refresh-token:secret")]
    [InlineData("client_secret=value")]
    public void Analytics_safe_label_guard_rejects_all_canonical_credential_markers(string value)
    {
        Assert.Null(SqlitePricingReadStore.SafeAnalyticsLabel(value));
    }

    [Fact]
    public void Project_keeps_seven_states_and_separates_complete_and_provisional_totals()
    {
        var members = new[]
        {
            Member("00000000-0000-7000-8000-000000000001", "estimated", 1m,
                [new("input_tokens", 0.4m, null), new("output_tokens", 0.6m, null)]),
            Member("00000000-0000-7000-8000-000000000002", "partial", 0.25m,
                [new("input_tokens", 0.25m, null), new("output_tokens", null, "quantity_missing")],
                ["quantity_missing"]),
            Member("00000000-0000-7000-8000-000000000003", "not_estimable"),
            Member("00000000-0000-7000-8000-000000000004", "missing"),
            Member("00000000-0000-7000-8000-000000000005", "failed"),
            Member("00000000-0000-7000-8000-000000000006", "unavailable"),
            Member("00000000-0000-7000-8000-000000000007", "stale", 9m,
                [new("input_tokens", 9m, null)]),
        };
        var query = Query(limit: 100);

        var result = SqliteCostAnalyticsProjectorV1.Project(
            query,
            3,
            "cost-configuration-" + new string('a', 64),
            new string('b', 64),
            members);

        Assert.Equal(PricingReadStatus.Success, result.Status);
        var value = Assert.IsType<CostAnalyticsReadV1>(result.Value);
        Assert.Equal("complete", value.State);
        Assert.Equal(7, value.EligibleSessionCount);
        Assert.Equal(7, value.Overall!.EligibleSessionCount);
        Assert.Equal(1, value.Overall.EstimatedSessionCount);
        Assert.Equal(1, value.Overall.PartialSessionCount);
        Assert.Equal(1, value.Overall.NotEstimableSessionCount);
        Assert.Equal(1, value.Overall.MissingSessionCount);
        Assert.Equal(1, value.Overall.FailedSessionCount);
        Assert.Equal(1, value.Overall.UnavailableSessionCount);
        Assert.Equal(1, value.Overall.StaleSessionCount);
        Assert.Equal(1_428, value.Overall.CoverageBasisPoints);

        var total = Assert.Single(value.RangeTotals);
        Assert.Equal("available", total.EstimatedAmountState);
        Assert.Equal(1m, total.EstimatedAmount);
        Assert.Equal("available", total.PartialKnownComponentAmountState);
        Assert.Equal(0.25m, total.PartialKnownComponentAmount);
        Assert.Equal(3, value.Groups.Count(group => group.ComponentCategory is not null));
        Assert.DoesNotContain(value.Groups, group => group.EstimatedAmount == 9m);
    }

    [Fact]
    public void Cursor_is_canonical_bound_to_filters_limit_snapshot_and_member_group()
    {
        var members = new[]
        {
            Member("00000000-0000-7000-8000-000000000001", "estimated", 1m,
                [new("input_tokens", 1m, null)]),
            Member("00000000-0000-7000-8000-000000000002", "estimated", 2m,
                [new("output_tokens", 2m, null)]),
        };
        var first = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 1),
            3,
            "cost-configuration-" + new string('a', 64),
            new string('b', 64),
            members).Value!;

        Assert.Single(first.Groups);
        Assert.StartsWith("cost-analytics-cursor-v1.", first.NextCursor);

        var second = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 1, after: first.NextCursor),
            3,
            "cost-configuration-" + new string('a', 64),
            new string('b', 64),
            members);
        Assert.Equal(PricingReadStatus.Success, second.Status);
        Assert.Single(second.Value!.Groups);
        Assert.Null(second.Value.NextCursor);

        var wrongLimit = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 2, after: first.NextCursor),
            3,
            "cost-configuration-" + new string('a', 64),
            new string('b', 64),
            members);
        Assert.Equal(PricingReadStatus.InvalidCursor, wrongLimit.Status);

        var changed = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 1, after: first.NextCursor),
            4,
            "cost-configuration-" + new string('a', 64),
            new string('b', 64),
            members);
        Assert.Equal(PricingReadStatus.SnapshotChanged, changed.Status);
    }

    [Fact]
    public void Project_withholds_all_totals_groups_and_cursor_at_the_eligible_sentinel()
    {
        var members = Enumerable.Range(1, 2_001)
            .Select(index => Member(
                $"00000000-0000-7000-8000-{index:000000000000}",
                "missing"))
            .ToArray();

        var result = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 100),
            1,
            null,
            new string('b', 64),
            members).Value!;

        Assert.Equal("incomplete", result.State);
        Assert.Equal("eligible_session_limit", result.CapReason);
        Assert.Null(result.EligibleSessionCount);
        Assert.Equal(2_001, result.EligibleSessionLowerBound);
        Assert.Null(result.GroupLowerBound);
        Assert.Null(result.Overall);
        Assert.Empty(result.RangeTotals);
        Assert.Empty(result.DailyTotals);
        Assert.Empty(result.Groups);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public void Incomplete_snapshot_rejects_a_same_snapshot_nonmember_cursor()
    {
        var small = new[]
        {
            Member("00000000-0000-7000-8000-000000000001", "estimated", 1m,
                [new("input_tokens", 1m, null)]),
            Member("00000000-0000-7000-8000-000000000002", "estimated", 1m,
                [new("output_tokens", 1m, null)]),
        };
        var template = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 1),
            1,
            null,
            new string('b', 64),
            small).Value!;
        var overflowMembers = Enumerable.Range(1, 2_001)
            .Select(index => Member(
                $"00000000-0000-7000-8000-{index:000000000000}",
                "missing"))
            .ToArray();
        var overflow = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 1),
            1,
            null,
            new string('b', 64),
            overflowMembers).Value!;
        var forged = ReplaceCursorSnapshot(
            Assert.IsType<string>(template.NextCursor),
            template.SnapshotId,
            overflow.SnapshotId);

        var result = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 1, after: forged),
            1,
            null,
            new string('b', 64),
            overflowMembers);

        Assert.Equal(PricingReadStatus.InvalidCursor, result.Status);
    }

    [Fact]
    public void Project_withholds_all_aggregates_at_the_group_sentinel()
    {
        var members = Enumerable.Range(1, 2_000)
            .Select(index => Member(
                $"00000000-0000-7000-8000-{index:000000000000}",
                "estimated",
                1m,
                [new("input_tokens", 1m, null)]) with
            {
                Model = $"model-{index:0000}",
            })
            .ToArray();
        members[0] = members[0] with
        {
            Amount = 2m,
            Components =
            [
                new("input_tokens", 1m, null),
                new("output_tokens", 1m, null),
            ],
        };

        var result = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 100),
            1,
            null,
            new string('b', 64),
            members).Value!;

        Assert.Equal("incomplete", result.State);
        Assert.Equal("group_limit", result.CapReason);
        Assert.Equal(2_000, result.EligibleSessionCount);
        Assert.Null(result.EligibleSessionLowerBound);
        Assert.Equal(2_001, result.GroupLowerBound);
        Assert.Null(result.Overall);
        Assert.Empty(result.RangeTotals);
        Assert.Empty(result.DailyTotals);
        Assert.Empty(result.Groups);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public void Project_withholds_unrepresentable_amount_without_wrapping_or_merging_it()
    {
        var members = new[]
        {
            Member("00000000-0000-7000-8000-000000000001", "estimated", decimal.MaxValue,
                [new("input_tokens", decimal.MaxValue, null)]),
            Member("00000000-0000-7000-8000-000000000002", "estimated", 1m,
                [new("input_tokens", 1m, null)]),
        };

        var result = SqliteCostAnalyticsProjectorV1.Project(
            Query(limit: 100),
            1,
            null,
            new string('b', 64),
            members).Value!;

        var total = Assert.Single(result.RangeTotals);
        Assert.Equal("unrepresentable", total.EstimatedAmountState);
        Assert.Null(total.EstimatedAmount);
        var component = Assert.Single(result.Groups);
        Assert.Equal("unrepresentable", component.EstimatedAmountState);
        Assert.Null(component.EstimatedAmount);
    }

    [Fact]
    public void Explicit_filter_never_matches_an_unknown_dimension()
    {
        var unknown = Member("00000000-0000-7000-8000-000000000001", "missing");
        unknown = unknown with { Repository = null };
        var query = Query(limit: 100) with { Repository = "repo" };

        var result = SqliteCostAnalyticsProjectorV1.Project(
            query,
            1,
            null,
            new string('b', 64),
            [unknown]).Value!;

        Assert.Equal(0, result.EligibleSessionCount);
        Assert.Empty(result.Groups);
    }

    private static CostAnalyticsQueryV1 Query(int limit, string? after = null) => new(
        From,
        From.AddDays(1),
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        limit,
        after);

    private static string ReplaceCursorSnapshot(
        string cursor,
        string currentSnapshot,
        string replacementSnapshot)
    {
        const string prefix = "cost-analytics-cursor-v1.";
        var payload = cursor[prefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload))
            .Replace(currentSnapshot, replacementSnapshot, StringComparison.Ordinal);
        return prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static CostAnalyticsMemberV1 Member(
        string sessionId,
        string state,
        decimal? amount = null,
        IReadOnlyList<CostAnalyticsComponentV1>? components = null,
        IReadOnlyList<string>? reasons = null) => new(
            sessionId,
            "completed",
            From.AddHours(1),
            From.AddHours(2),
            "resolved",
            2,
            new string('c', 64),
            "github-copilot-vscode",
            "1.2.3",
            "repo",
            "workspace",
            state,
            state is "estimated" or "partial" or "stale" ? 1 : null,
            state is "estimated" or "partial" or "stale"
                ? "pricing-estimate-" + new string(sessionId[^1], 64)
                : null,
            1,
            "identity-" + sessionId,
            state is "estimated" or "partial" ? "github_copilot" : null,
            state is "estimated" or "partial" ? "GPT-5 mini" : null,
            state is "estimated" or "partial" ? "plan_included" : null,
            state is "estimated" or "partial" ? "bundled-2026-07" : null,
            state is "estimated" or "partial" ? "USD" : null,
            amount,
            components ?? [],
            reasons ?? []);

    private sealed class AnalyticsDatabase : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"pricing-analytics-{Guid.NewGuid():N}");

        internal AnalyticsDatabase()
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "monitor.db");
        }

        internal string Path { get; }

        internal SqlitePricingStore CreatePricingStore()
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
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString()))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction(deferred: false);
                RuntimeBackupSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }
            var store = new SqlitePricingStore(Path, TimeProvider.System);
            store.CreateSchema();
            return store;
        }

        internal void CommitConfiguration(
            SqlitePricingStore store,
            PricingCatalog catalog,
            byte[] catalogBytes)
        {
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
                TimeProvider.System.GetUtcNow());
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
        }

        internal string InsertResolvedSession(string repository, string workspace)
        {
            var sessionId = Guid.NewGuid().ToString("D");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sessions(
                    session_id,status,completeness,repository,workspace,last_seen_at,
                    raw_retention_state,created_at,updated_at)
                VALUES($id,'completed','full',$repository,$workspace,
                    '2026-07-01T01:00:00.0000000+00:00','not_captured',
                    '2026-07-01T01:00:00.0000000+00:00',
                    '2026-07-01T02:00:00.0000000+00:00');
                INSERT INTO session_runs(run_id,session_id,source_surface,status)
                VALUES($run,$id,'vscode','completed');
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,source_adapter,source_event_id,
                    type,occurred_at,content_state,source_application_version)
                VALUES($event,$id,$run,'vscode','synthetic',$source,'turn',
                    '2026-07-01T01:00:00.0000000+00:00','not_captured','1.2.3');
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$repository", repository);
            command.Parameters.AddWithValue("$workspace", workspace);
            command.Parameters.AddWithValue("$run", "run-" + sessionId);
            command.Parameters.AddWithValue("$event", "event-" + sessionId);
            command.Parameters.AddWithValue("$source", "source-" + sessionId);
            command.ExecuteNonQuery();
            return sessionId;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}

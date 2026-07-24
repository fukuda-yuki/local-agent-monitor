using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CostRouteTests
{
    public static TheoryData<string, HttpStatusCode, string> RecalculationAdmissionFailures => new()
    {
        { "idempotency", HttpStatusCode.Conflict, "cost_idempotency_conflict" },
        { "stale-head", HttpStatusCode.Conflict, "cost_stale_head" },
        { "catalog-changed", HttpStatusCode.Conflict, "cost_catalog_changed" },
        { "overlap", HttpStatusCode.Conflict, "cost_recalculation_in_progress" },
        { "missing", HttpStatusCode.NotFound, "cost_session_not_found" },
        { "ineligible", HttpStatusCode.Conflict, "cost_session_not_eligible" },
        { "budget-ineligible", HttpStatusCode.Conflict, "cost_session_not_eligible" },
    };

    [Fact]
    public async Task ConfigurationAndCatalogReadsExposeOnlySealedNoStoreProjections()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        using var configuration = await host.Client.GetAsync("/api/costs/v1/configuration");
        Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        Assert.Equal("no-store", configuration.Headers.CacheControl?.ToString());
        Assert.False(configuration.Headers.Contains("Access-Control-Allow-Origin"));
        var configurationText = await configuration.Content.ReadAsStringAsync();
        Assert.Equal(
            """{"schema_version":"cost.configuration-read.v1","head_revision":0,"configuration_id":null,"configuration_catalog_sha256":null,"provider_catalog_sha256":""" + JsonSerializer.Serialize(CurrentCatalogSha()) +
            ""","catalog_state":"unconfigured","configuration":null,"selected_session_count":0,"selected_session_count_state":"exact"}""",
            configurationText);

        using var catalog = await host.Client.GetAsync("/api/costs/v1/catalog?limit=1");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Equal("no-store", catalog.Headers.CacheControl?.ToString());
        var text = await catalog.Content.ReadAsStringAsync();
        Assert.Contains("""{"schema_version":"cost.catalog.v1","catalog_sha256":""", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rate", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("override", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CostRoutesEnforceHostOriginQueryAndFixedErrorBytes()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, "/api/costs/v1/configuration");
        invalidHostRequest.Headers.Host = "example.invalid";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);
        await AssertError(invalidHost, HttpStatusCode.BadRequest, "invalid_host");

        using var crossSiteRequest = new HttpRequestMessage(HttpMethod.Get, "/api/costs/v1/configuration");
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(crossSiteRequest);
        await AssertError(crossSite, HttpStatusCode.Forbidden, "cross_origin_forbidden");

        using var duplicate = await host.Client.GetAsync("/api/costs/v1/catalog?limit=1&limit=2");
        await AssertError(duplicate, HttpStatusCode.BadRequest, "cost_invalid_query");

        using var unknown = await host.Client.GetAsync("/api/costs/v1/catalog?unknown=1");
        await AssertError(unknown, HttpStatusCode.BadRequest, "cost_invalid_query");

        using var empty = await host.Client.GetAsync("/api/costs/v1/catalog?limit=");
        await AssertError(empty, HttpStatusCode.BadRequest, "cost_invalid_query");

        using var missing = await host.Client.GetAsync(
            "/api/costs/v1/configurations/cost-configuration-" + new string('a', 64));
        await AssertError(missing, HttpStatusCode.NotFound, "cost_configuration_not_found");
    }

    [Fact]
    public async Task CostPostsEnforceOriginCsrfJsonSizeAndStrictCanonicalBody()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        using var noCsrf = Post("/api/costs/v1/configuration/preview", "{}");
        using var noCsrfResponse = await host.Client.SendAsync(noCsrf);
        await AssertError(noCsrfResponse, HttpStatusCode.Forbidden, "csrf_required");

        using var wrongMedia = Post("/api/costs/v1/configuration/preview", "{}", csrf: true);
        wrongMedia.Content!.Headers.ContentType = new("text/plain");
        using var wrongMediaResponse = await host.Client.SendAsync(wrongMedia);
        await AssertError(wrongMediaResponse, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

        using var malformed = Post("/api/costs/v1/configuration/preview", "{", csrf: true);
        using var malformedResponse = await host.Client.SendAsync(malformed);
        await AssertError(malformedResponse, HttpStatusCode.BadRequest, "cost_invalid_configuration");

        using var unknown = Post("/api/costs/v1/configuration/preview", """{"schema_version":"cost.configuration-preview-request.v1","unexpected":true}""", csrf: true);
        using var unknownResponse = await host.Client.SendAsync(unknown);
        await AssertError(unknownResponse, HttpStatusCode.BadRequest, "cost_invalid_configuration");

        using var oversized = Post(
            "/api/costs/v1/configuration/preview",
            "\"" + new string('x', 1_048_576) + "\"",
            csrf: true);
        using var oversizedResponse = await host.Client.SendAsync(oversized);
        await AssertError(oversizedResponse, HttpStatusCode.RequestEntityTooLarge, "cost_request_too_large");
    }

    [Fact]
    public async Task SessionAndRunRoutesDistinguishInvalidIdsFromAbsentOwners()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        var absent = Guid.CreateVersion7().ToString("D");

        using var invalid = await host.Client.GetAsync("/api/costs/v1/recalculations/not-a-uuid");
        await AssertError(invalid, HttpStatusCode.BadRequest, "cost_invalid_id");

        using var absentRun = await host.Client.GetAsync("/api/costs/v1/recalculations/" + absent);
        await AssertError(absentRun, HttpStatusCode.NotFound, "cost_recalculation_not_found");

        using var absentSession = await host.Client.GetAsync(
            $"/api/costs/v1/sessions/{absent}/estimates");
        await AssertError(absentSession, HttpStatusCode.NotFound, "cost_session_not_found");
    }

    [Fact]
    public async Task PreviewCommitAndImmutableVersionUseExactCanonicalRequestsAndLocations()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        var previewBytes = CostConfigurationPreviewRequestCanonicalJsonV1.Serialize(
            CostConfigurationPreviewRequestCanonicalJsonV1.Create([], []));
        using var previewRequest = Post(
            "/api/costs/v1/configuration/preview",
            Encoding.UTF8.GetString(previewBytes),
            csrf: true);
        using var previewResponse = await host.Client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var previewResponseBytes = await previewResponse.Content.ReadAsByteArrayAsync();
        var preview = CostConfigurationPreviewConsumerV1.Consume(previewResponseBytes);
        Assert.Equal(CostConsumerStatus.Success, preview.Status);

        var commitBytes = CostConfigurationCommitConsumerV1.SerializeRequest(preview.Value!);
        using var commitRequest = Post(
            "/api/costs/v1/configurations",
            Encoding.UTF8.GetString(commitBytes),
            csrf: true);
        using var commit = await host.Client.SendAsync(commitRequest);
        Assert.Equal(HttpStatusCode.Created, commit.StatusCode);
        Assert.NotNull(commit.Headers.Location);
        Assert.StartsWith("/api/costs/v1/configurations/cost-configuration-", commit.Headers.Location!.OriginalString);
        using var commitJson = JsonDocument.Parse(await commit.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["schema_version", "configuration_id", "head_revision", "catalog_sha256"],
            commitJson.RootElement.EnumerateObject().Select(property => property.Name));

        using var version = await host.Client.GetAsync(commit.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        using var versionJson = JsonDocument.Parse(await version.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["schema_version", "head_revision", "configuration_id", "catalog_sha256", "committed_at_utc", "configuration"],
            versionJson.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.EndsWith("Z", versionJson.RootElement.GetProperty("committed_at_utc").GetString());
    }

    [Fact]
    public async Task KnownSessionWithoutEstimateAndEmptyAnalyticsRemainExplicitSuccessfulStates()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        var sessionId = Guid.CreateVersion7().ToString("D");
        InsertSession(temp.DatabasePath, sessionId);

        using var estimates = await host.Client.GetAsync(
            $"/api/costs/v1/sessions/{sessionId}/estimates");
        Assert.Equal(HttpStatusCode.OK, estimates.StatusCode);
        using var estimateJson = JsonDocument.Parse(await estimates.Content.ReadAsStreamAsync());
        Assert.Equal("cost.session-estimates.v1", estimateJson.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("not_calculated", estimateJson.RootElement.GetProperty("calculation_state").GetString());
        Assert.Equal(JsonValueKind.Null, estimateJson.RootElement.GetProperty("active_estimate_id").ValueKind);
        Assert.Empty(estimateJson.RootElement.GetProperty("items").EnumerateArray());

        const string range = "from=2026-07-01T00%3A00%3A00.0000000Z&to=2026-07-02T00%3A00%3A00.0000000Z";
        using var analytics = await host.Client.GetAsync("/api/costs/v1/analytics?" + range);
        Assert.Equal(HttpStatusCode.OK, analytics.StatusCode);
        using var analyticsJson = JsonDocument.Parse(await analytics.Content.ReadAsStreamAsync());
        Assert.Equal("cost.analytics.v1", analyticsJson.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("complete", analyticsJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, analyticsJson.RootElement.GetProperty("next_cursor").ValueKind);

        using var badCurrency = await host.Client.GetAsync(
            "/api/costs/v1/analytics?" + range + "&currency=EUR");
        await AssertError(badCurrency, HttpStatusCode.BadRequest, "cost_invalid_query");
    }

    [Fact]
    public async Task RecalculationStartReturnsAcceptedProjectionAndWorkerCompletesDefaultUnavailableResult()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        var sessionId = Guid.CreateVersion7().ToString("D");
        InsertResolvedSession(temp.DatabasePath, sessionId);
        var source = new CostSourceEntryV1(
            "github-copilot-vscode",
            "1.2.3",
            "source-capability.v1",
            PricingProviders.GitHubCopilot,
            PricingBillingModes.PlanIncluded,
            PricingRoutes.CodeCompletion);
        var configuration = await CommitConfiguration(host.Client, [source]);
        var request = CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            configuration.HeadRevision,
            configuration.CatalogSha256,
            [sessionId],
            [],
            "route-test-idempotency");
        using var startRequest = Post(
            "/api/costs/v1/recalculations",
            Encoding.UTF8.GetString(CostRecalculationRequestCanonicalJsonV1.Serialize(request)),
            csrf: true);
        using var started = await host.Client.SendAsync(startRequest);

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.NotNull(started.Headers.Location);
        using var startedJson = JsonDocument.Parse(await started.Content.ReadAsStreamAsync());
        Assert.Equal(
            [
                "schema_version", "run_id", "request_digest", "state",
                "target_count", "scope_count", "targets", "events",
                "budget_results", "failure_code",
            ],
            startedJson.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("requested", startedJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, startedJson.RootElement
            .GetProperty("targets")[0].GetProperty("result").ValueKind);

        using var replayRequest = Post(
            "/api/costs/v1/recalculations",
            Encoding.UTF8.GetString(CostRecalculationRequestCanonicalJsonV1.Serialize(request)),
            csrf: true);
        using var replayed = await host.Client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Accepted, replayed.StatusCode);
        Assert.Equal(started.Headers.Location, replayed.Headers.Location);
        using var replayedJson = JsonDocument.Parse(
            await replayed.Content.ReadAsStreamAsync());
        Assert.Equal(
            startedJson.RootElement.GetProperty("run_id").GetString(),
            replayedJson.RootElement.GetProperty("run_id").GetString());

        JsonDocument? completed = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var poll = await host.Client.GetAsync(started.Headers.Location);
            var candidate = JsonDocument.Parse(await poll.Content.ReadAsStreamAsync());
            if (candidate.RootElement.GetProperty("state").GetString() == "succeeded")
            {
                completed = candidate;
                break;
            }
            candidate.Dispose();
            await Task.Delay(10);
        }
        using (completed)
        {
            Assert.NotNull(completed);
            var target = completed.RootElement.GetProperty("targets")[0];
            Assert.Equal("unavailable", target.GetProperty("result").GetProperty("kind").GetString());
            Assert.Equal("source_adapter_unavailable", target.GetProperty("result").GetProperty("code").GetString());
            Assert.False(target.GetProperty("result").TryGetProperty("status", out _));
            Assert.False(target.GetProperty("result").TryGetProperty("estimate_id", out _));
        }
    }

    [Theory]
    [MemberData(nameof(RecalculationAdmissionFailures))]
    public async Task RecalculationAdmissionFailuresPreserveFixedStoreApplicationAndRouteOutcomes(
        string scenarioName,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        await using var scenario = await CreateRecalculationFailureScenario(scenarioName);
        var requestBytes = CostRecalculationRequestCanonicalJsonV1.Serialize(scenario.Request);
        using var mutation = new RecalculationMutationProbe(scenario.Temp.DatabasePath);

        var coordinator = new SqliteCostRecalculationCoordinatorV1(
            scenario.Temp.DatabasePath,
            timeProvider: scenario.Temp.TimeProvider);
        var storeResult = coordinator.Start(
            Guid.CreateVersion7().ToString("D"),
            scenario.Request,
            scenario.Provider.CanonicalCatalogBytes,
            scenario.Temp.TimeProvider.GetUtcNow());

        Assert.Equal(PricingStoreStatus.Conflict, storeResult.Status);
        Assert.Equal(expectedCode, storeResult.ErrorCode);
        mutation.AssertUnchanged();

        using var application = new CostHttpApplication(
            scenario.Temp.DatabasePath,
            new SqlitePricingStore(
                scenario.Temp.DatabasePath,
                scenario.Temp.TimeProvider),
            scenario.Provider,
            scenario.Temp.TimeProvider,
            alertParticipant: null);
        var applicationResult = application.StartRecalculation(requestBytes);

        Assert.Equal((int)expectedStatus, applicationResult.Status);
        Assert.Equal(expectedCode, applicationResult.Error);
        mutation.AssertUnchanged();

        using var request = Post(
            "/api/costs/v1/recalculations",
            Encoding.UTF8.GetString(requestBytes),
            csrf: true);
        using var response = await scenario.Host.Client.SendAsync(request);

        await AssertError(response, expectedStatus, expectedCode);
        mutation.AssertUnchanged();
    }

    [Fact]
    public async Task QueryParserPinsCursorPrecedenceUtf8BoundAndPostQueryRejection()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        using var invalidCatalogLimit = await host.Client.GetAsync(
            "/api/costs/v1/catalog?limit=0");
        await AssertError(
            invalidCatalogLimit,
            HttpStatusCode.BadRequest,
            "cost_invalid_cursor");

        var sessionId = Guid.CreateVersion7().ToString("D");
        InsertSession(temp.DatabasePath, sessionId);
        using var invalidRevision = await host.Client.GetAsync(
            $"/api/costs/v1/sessions/{sessionId}/recalculations?after=0");
        await AssertError(
            invalidRevision,
            HttpStatusCode.BadRequest,
            "cost_invalid_cursor");

        using var invalidUtf8 = await host.Client.GetAsync(
            "/api/costs/v1/catalog?after=%FF");
        await AssertError(
            invalidUtf8,
            HttpStatusCode.BadRequest,
            "cost_invalid_query");

        using var longQuery = await host.Client.GetAsync(
            "/api/costs/v1/catalog?after=" + new string('a', 8_193));
        await AssertError(
            longQuery,
            HttpStatusCode.BadRequest,
            "cost_invalid_query");

        using var post = Post(
            "/api/costs/v1/configuration/preview?unexpected=client_secret%3Dvalue",
            "{}",
            csrf: true);
        using var postResponse = await host.Client.SendAsync(post);
        await AssertError(
            postResponse,
            HttpStatusCode.BadRequest,
            "cost_invalid_query");
        Assert.DoesNotContain(
            "client_secret",
            await postResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private static MonitorHostTestOptions Options(
        IPricingCatalogProvider? pricingCatalogProvider = null) => new()
    {
        PricingCatalogProvider = pricingCatalogProvider,
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
    };

    private static HttpRequestMessage Post(string path, string body, bool csrf = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static async Task AssertError(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(
            $$"""{"schema_version":"cost.error.v1","error":"{{code}}"}""",
            await response.Content.ReadAsStringAsync());
    }

    private static string CurrentCatalogSha()
    {
        var provider = DefaultPricingCatalogProvider.Create([]);
        return provider.CatalogSha256;
    }

    private static void InsertSession(
        string databasePath,
        string sessionId,
        string status = "completed")
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions(
                session_id,status,completeness,repository,workspace,last_seen_at,
                raw_retention_state,created_at,updated_at)
            VALUES($id,$status,'full',NULL,NULL,
                '2026-07-01T01:00:00.0000000+00:00','not_captured',
                '2026-07-01T01:00:00.0000000+00:00',
                '2026-07-01T01:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$status", status);
        command.ExecuteNonQuery();
    }

    private static void InsertResolvedSession(string databasePath, string sessionId)
    {
        InsertSession(databasePath, sessionId);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO session_runs(run_id,session_id,source_surface,status)
            VALUES($run,$session,'vscode','completed');
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,source_adapter,
                source_event_id,type,occurred_at,content_state,
                source_application_version)
            VALUES($event,$session,$run,'vscode','synthetic',$source,'turn',
                '2026-07-01T01:00:00.0000000+00:00','not_captured','1.2.3');
            """;
        command.Parameters.AddWithValue("$run", "run-" + sessionId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$event", "event-" + sessionId);
        command.Parameters.AddWithValue("$source", "source-" + sessionId);
        command.ExecuteNonQuery();
    }

    private static async Task<(string ConfigurationId, long HeadRevision, string CatalogSha256)>
        CommitConfiguration(
            HttpClient client,
            IReadOnlyList<CostSourceEntryV1> sources)
    {
        var request = CostConfigurationPreviewRequestCanonicalJsonV1.Serialize(
            CostConfigurationPreviewRequestCanonicalJsonV1.Create(sources, []));
        using var previewRequest = Post(
            "/api/costs/v1/configuration/preview",
            Encoding.UTF8.GetString(request),
            csrf: true);
        using var previewResponse = await client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = CostConfigurationPreviewConsumerV1.Consume(
            await previewResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(CostConsumerStatus.Success, preview.Status);
        using var commitRequest = Post(
            "/api/costs/v1/configurations",
            Encoding.UTF8.GetString(
                CostConfigurationCommitConsumerV1.SerializeRequest(preview.Value!)),
            csrf: true);
        using var commit = await client.SendAsync(commitRequest);
        Assert.Equal(HttpStatusCode.Created, commit.StatusCode);
        using var json = JsonDocument.Parse(await commit.Content.ReadAsStreamAsync());
        return (
            json.RootElement.GetProperty("configuration_id").GetString()!,
            json.RootElement.GetProperty("head_revision").GetInt64(),
            json.RootElement.GetProperty("catalog_sha256").GetString()!);
    }

    private static async Task<RecalculationFailureScenario>
        CreateRecalculationFailureScenario(string scenarioName)
    {
        var temp = NewTemp();
        RunningMonitorHost? host = null;
        try
        {
            var provider = DefaultPricingCatalogProvider.Create([]);
            host = await MonitorTestHost.StartAsync(
                temp,
                testOptions: Options(provider));
            var firstSessionId = Guid.CreateVersion7().ToString("D");
            var secondSessionId = Guid.CreateVersion7().ToString("D");
            var ineligibleSessionId = Guid.CreateVersion7().ToString("D");
            var budgetIneligibleSessionId = Guid.CreateVersion7().ToString("D");
            var missingSessionId = Guid.CreateVersion7().ToString("D");
            InsertResolvedSession(temp.DatabasePath, firstSessionId);
            InsertResolvedSession(temp.DatabasePath, secondSessionId);
            InsertSession(temp.DatabasePath, ineligibleSessionId, "active");
            InsertSession(temp.DatabasePath, budgetIneligibleSessionId);
            var source = new CostSourceEntryV1(
                "github-copilot-vscode",
                "1.2.3",
                "source-capability.v1",
                PricingProviders.GitHubCopilot,
                PricingBillingModes.PlanIncluded,
                PricingRoutes.CodeCompletion);
            var configuration = await CommitConfiguration(host.Client, [source]);
            var requestProvider = (IPricingCatalogProvider)provider;
            CostRecalculationRequestV1 request;

            switch (scenarioName)
            {
                case "idempotency":
                {
                    var key = "status-mapping-idempotency";
                    var initial = RecalculationRequest(
                        configuration,
                        [firstSessionId],
                        key);
                    StartSeedRun(temp, provider, initial);
                    await CommitConfiguration(host.Client, [source]);
                    request = RecalculationRequest(
                        configuration,
                        [firstSessionId, missingSessionId],
                        key);
                    requestProvider = ChangedCatalogProvider();
                    break;
                }
                case "stale-head":
                    request = RecalculationRequest(
                        configuration,
                        [missingSessionId],
                        "status-mapping-stale-head");
                    await CommitConfiguration(host.Client, [source]);
                    requestProvider = ChangedCatalogProvider();
                    break;
                case "catalog-changed":
                    StartSeedRun(
                        temp,
                        provider,
                        RecalculationRequest(
                            configuration,
                            [firstSessionId],
                            "status-mapping-catalog-seed"));
                    request = RecalculationRequest(
                        configuration,
                        [firstSessionId, missingSessionId],
                        "status-mapping-catalog");
                    requestProvider = ChangedCatalogProvider();
                    break;
                case "overlap":
                    StartSeedRun(
                        temp,
                        provider,
                        RecalculationRequest(
                            configuration,
                            [firstSessionId],
                            "status-mapping-overlap-seed"));
                    request = RecalculationRequest(
                        configuration,
                        [firstSessionId, missingSessionId],
                        "status-mapping-overlap");
                    break;
                case "missing":
                    request = RecalculationRequest(
                        configuration,
                        [ineligibleSessionId, missingSessionId],
                        "status-mapping-missing");
                    break;
                case "ineligible":
                    request = RecalculationRequest(
                        configuration,
                        [ineligibleSessionId],
                        "status-mapping-ineligible");
                    break;
                case "budget-ineligible":
                    request = CostRecalculationRequestCanonicalJsonV1.Create(
                        configuration.ConfigurationId,
                        configuration.HeadRevision,
                        configuration.CatalogSha256,
                        [budgetIneligibleSessionId],
                        [
                            new(
                                "session",
                                budgetIneligibleSessionId,
                                null,
                                null,
                                null),
                        ],
                        "status-mapping-budget-ineligible");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(scenarioName),
                        scenarioName,
                        "Recalculation failure scenario is unknown.");
            }

            if (!ReferenceEquals(requestProvider, provider))
            {
                await host.DisposeAsync();
                host = await MonitorTestHost.StartAsync(
                    temp,
                    testOptions: Options(requestProvider));
            }

            return new(temp, host, requestProvider, request);
        }
        catch
        {
            if (host is not null) await host.DisposeAsync();
            temp.Dispose();
            throw;
        }
    }

    private static CostRecalculationRequestV1 RecalculationRequest(
        (string ConfigurationId, long HeadRevision, string CatalogSha256) configuration,
        IReadOnlyList<string> sessionIds,
        string idempotencyKey) =>
        CostRecalculationRequestCanonicalJsonV1.Create(
            configuration.ConfigurationId,
            configuration.HeadRevision,
            configuration.CatalogSha256,
            sessionIds,
            [],
            idempotencyKey);

    private static void StartSeedRun(
        MonitorTempDirectory temp,
        IPricingCatalogProvider provider,
        CostRecalculationRequestV1 request)
    {
        var result = new SqliteCostRecalculationCoordinatorV1(
            temp.DatabasePath,
            timeProvider: temp.TimeProvider).Start(
                Guid.CreateVersion7().ToString("D"),
                request,
                provider.CanonicalCatalogBytes,
                temp.TimeProvider.GetUtcNow());
        Assert.Equal(PricingStoreStatus.Success, result.Status);
        Assert.Null(result.ErrorCode);
    }

    private static IPricingCatalogProvider ChangedCatalogProvider()
    {
        var bundled = BundledPricingRegistry.Load();
        var source = bundled.Entries[0];
        var localOverride = bundled with
        {
            RegistryVersion = "status-mapping-override-v1",
            SourceKind = PricingRegistrySourceKinds.LocalOverride,
            SourceId = "status-mapping-override",
            SourceLabel = "status mapping reviewed source",
            Entries =
            [
                source with
                {
                    EntryId = "status-mapping-override-entry",
                    Revision = 1,
                    SupersedesEntryKey = null,
                    CanonicalModelId = "status-mapping-model",
                    Aliases = [],
                },
            ],
        };
        return new FixedCatalogProvider(PricingCatalog.Create(bundled, [localOverride]));
    }

    private sealed class FixedCatalogProvider(PricingCatalog catalog)
        : IPricingCatalogProvider
    {
        private readonly byte[] canonicalBytes =
            PricingCanonicalJson.SerializeCatalogSnapshot(catalog);

        public PricingCatalog Catalog { get; } = catalog;
        public ReadOnlyMemory<byte> CanonicalCatalogBytes => canonicalBytes.ToArray();
        public string CatalogSha256 => Catalog.CatalogSha256;
    }

    private sealed class RecalculationFailureScenario(
        MonitorTempDirectory temp,
        RunningMonitorHost host,
        IPricingCatalogProvider provider,
        CostRecalculationRequestV1 request) : IAsyncDisposable
    {
        internal MonitorTempDirectory Temp { get; } = temp;
        internal RunningMonitorHost Host { get; } = host;
        internal IPricingCatalogProvider Provider { get; } = provider;
        internal CostRecalculationRequestV1 Request { get; } = request;

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Temp.Dispose();
        }
    }

    private sealed class RecalculationMutationProbe : IDisposable
    {
        private readonly SqliteConnection connection;
        private readonly long dataVersion;
        private readonly string recalculationState;

        internal RecalculationMutationProbe(string databasePath)
        {
            connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ToString());
            connection.Open();
            dataVersion = Scalar<long>("PRAGMA data_version;");
            recalculationState = Scalar<string>(
                """
                SELECT
                    (SELECT COUNT(*) FROM pricing_recalculation_runs) || ':' ||
                    (SELECT COUNT(*) FROM pricing_recalculation_targets) || ':' ||
                    (SELECT COUNT(*) FROM pricing_recalculation_events) || ':' ||
                    (SELECT COUNT(*) FROM pricing_recalculation_target_results) || ':' ||
                    (SELECT COUNT(*) FROM pricing_session_attempts) || ':' ||
                    (SELECT COUNT(*) FROM pricing_recalculation_budget_results) || ':' ||
                    (SELECT COUNT(*) FROM pricing_estimates) || ':' ||
                    (SELECT COUNT(*) FROM pricing_estimate_heads);
                """);
        }

        internal void AssertUnchanged()
        {
            Assert.Equal(dataVersion, Scalar<long>("PRAGMA data_version;"));
            Assert.Equal(
                recalculationState,
                Scalar<string>(
                    """
                    SELECT
                        (SELECT COUNT(*) FROM pricing_recalculation_runs) || ':' ||
                        (SELECT COUNT(*) FROM pricing_recalculation_targets) || ':' ||
                        (SELECT COUNT(*) FROM pricing_recalculation_events) || ':' ||
                        (SELECT COUNT(*) FROM pricing_recalculation_target_results) || ':' ||
                        (SELECT COUNT(*) FROM pricing_session_attempts) || ':' ||
                        (SELECT COUNT(*) FROM pricing_recalculation_budget_results) || ':' ||
                        (SELECT COUNT(*) FROM pricing_estimates) || ':' ||
                        (SELECT COUNT(*) FROM pricing_estimate_heads);
                    """));
        }

        public void Dispose() => connection.Dispose();

        private T Scalar<T>(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar()!;
            return value is T typed
                ? typed
                : (T)Convert.ChangeType(
                    value,
                    typeof(T),
                    System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static MonitorTempDirectory NewTemp() => new();
}

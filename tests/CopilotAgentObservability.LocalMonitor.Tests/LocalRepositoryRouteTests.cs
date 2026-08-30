using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Repositories;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class LocalRepositoryRouteTests
{
    private const string CreateBody = "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":null}";
    private const string Key = "lrc1_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE";
    private const string SessionId = "01900000-0000-7000-8000-0000000000a1";

    [Fact]
    public async Task RawDefault_MapsExactlySevenMethodTemplatePairs()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());

        Assert.Equal(
            [
                ("GET", "/api/local-monitor/v1/repositories"),
                ("HEAD", "/api/local-monitor/v1/repositories"),
                ("POST", "/api/local-monitor/v1/repositories"),
                ("PATCH", "/api/local-monitor/v1/repositories/{repositoryId}"),
                ("GET", "/api/local-monitor/v1/repositories/{repositoryId}/locators"),
                ("POST", "/api/local-monitor/v1/session-repository-actions"),
                ("GET", "/api/local-monitor/v1/sessions/{sessionId}/repository-assignment"),
            ],
            host.RouteMethods.Where(static item => item.Pattern.StartsWith("/api/local-monitor/v1", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task CreateAndReplay_ReturnExactStoredBytesAndHeaders()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());

        using var first = await SendCreateAsync(host.Client);
        using var replay = await SendCreateAsync(host.Client);
        var firstBytes = await first.Content.ReadAsByteArrayAsync();
        var replayBytes = await replay.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(firstBytes, replayBytes);
        Assert.Equal("application/json; charset=utf-8", first.Content.Headers.ContentType?.ToString());
        Assert.True(first.Headers.CacheControl?.NoStore);
        Assert.Single(first.Headers.CacheControl!.ToString().Split(','));
        Assert.False(first.Headers.Contains("Location"));
        Assert.False(first.Headers.Contains("ETag"));
        Assert.Equal(1, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(1, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData(false, null, null, HttpStatusCode.Forbidden, "{\"error\":\"csrf_rejected\"}")]
    [InlineData(true, null, null, HttpStatusCode.UnsupportedMediaType, "{\"error\":\"unsupported_media_type\"}")]
    [InlineData(true, "application/json", null, HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}")]
    public async Task MutationAdmission_IsOrdered(
        bool csrf,
        string? contentType,
        string? key,
        HttpStatusCode status,
        string body)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-monitor/v1/repositories")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody)),
        };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/repositories", "GET,HEAD,POST")]
    [InlineData("/api/local-monitor/v1/repositories/01900000-0000-7000-8000-000000000001", "PATCH")]
    [InlineData("/api/local-monitor/v1/repositories/01900000-0000-7000-8000-000000000001/locators", "GET")]
    [InlineData("/api/local-monitor/v1/session-repository-actions", "POST")]
    [InlineData("/api/local-monitor/v1/sessions/01900000-0000-7000-8000-000000000001/repository-assignment", "GET")]
    public async Task Framework405_IsAdaptedForEveryOwnedTemplate(string path, string allowedMethod)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());

        using var owned = await host.Client.DeleteAsync(path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, owned.StatusCode);
        Assert.Equal("{\"error\":\"method_not_allowed\"}", await owned.Content.ReadAsStringAsync());
        Assert.True(owned.Headers.CacheControl?.NoStore);
        Assert.Equal(allowedMethod.Split(','), AllowedMethods(owned));
    }

    [Fact]
    public async Task Head_ExecutesRepositoryCollectionGetAndSuppressesItsBody()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());

        using var postOnly = await host.Client.SendAsync(new(HttpMethod.Head, "/api/local-monitor/v1/repositories"));
        using var getRoute = await host.Client.SendAsync(new(
            HttpMethod.Head,
            "/api/local-monitor/v1/repositories/01900000-0000-7000-8000-000000000001/locators"));
        using var assignmentRoute = await host.Client.SendAsync(new(
            HttpMethod.Head,
            "/api/local-monitor/v1/sessions/01900000-0000-7000-8000-000000000001/repository-assignment"));

        Assert.Equal(HttpStatusCode.OK, postOnly.StatusCode);
        Assert.Empty(await postOnly.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json; charset=utf-8", postOnly.Content.Headers.ContentType!.ToString());
        Assert.Equal(272, postOnly.Content.Headers.ContentLength);
        Assert.True(postOnly.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getRoute.StatusCode);
        Assert.Empty(await getRoute.Content.ReadAsByteArrayAsync());
        Assert.Equal(["GET"], AllowedMethods(getRoute));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, assignmentRoute.StatusCode);
        Assert.Empty(await assignmentRoute.Content.ReadAsByteArrayAsync());
        Assert.Equal(["GET"], AllowedMethods(assignmentRoute));
    }

    [Fact]
    public async Task ActivatedCollectionGet_IsSelectedAndContributesToAllow()
    {
        using var temp = new MonitorTempDirectory();
        var options = QuietHost();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: options);

        using var get = await host.Client.GetAsync("/api/local-monitor/v1/repositories");
        using var delete = await host.Client.DeleteAsync("/api/local-monitor/v1/repositories");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("application/json; charset=utf-8", get.Content.Headers.ContentType?.ToString());
        Assert.Contains("\"schema_version\":\"local-monitor-repositories.response.v1\"", await get.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Contains("POST", AllowedMethods(delete));
        Assert.Contains("GET", AllowedMethods(delete));
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/repositories", true)]
    [InlineData("/api/local-monitor/v1/repositories/01900000-0000-7000-8000-000000000001", true)]
    [InlineData("/api/local-monitor/v1/repositories/01900000-0000-7000-8000-000000000001/locators", true)]
    [InlineData("/api/local-monitor/v1/session-repository-actions", true)]
    [InlineData("/api/local-monitor/v1/sessions/01900000-0000-7000-8000-000000000001/repository-assignment", true)]
    [InlineData("/api/local-monitor/v1/repositories//locators", false)]
    [InlineData("/api/local-monitor/v1/repositories/one/extra", false)]
    [InlineData("/api/local-monitor/v1/repositories/one%2Ftwo", true)]
    [InlineData("/api/local-monitor/v1/repositories/", true)]
    [InlineData("/api/local-monitor/v1/repositories/one", true)]
    [InlineData("/api/local-monitor/v1/sessions//repository-assignment", false)]
    [InlineData("/api/local-monitor/v1/sessions/one/repository-assignment/extra", false)]
    [InlineData("/prefix/api/local-monitor/v1/repositories", false)]
    [InlineData("/api/local-monitor/v1/repositories-suffix", false)]
    [InlineData("/api/local-monitor/v10/repositories", false)]
    public void OwnedTemplateClassifier_UsesExactRoutePatterns(string path, bool expected) =>
        Assert.Equal(expected, LocalRepositoryRoutes.IsOwnedPath(path));

    [Theory]
    [InlineData("/api/local-monitor/v1/repositories", true)]
    [InlineData("/API/LOCAL-MONITOR/V1/REPOSITORIES", true)]
    [InlineData("/api/local-monitor/v1/repositories/01900000-0000-7000-8000-000000000001", true)]
    [InlineData("/api/local-monitor/v1/repositories/01900000-0000-7000-8000-000000000001/locators", true)]
    [InlineData("/api/local-monitor/v1/session-repository-actions", true)]
    [InlineData("/api/local-monitor/v1/sessions/01900000-0000-7000-8000-000000000001/repository-assignment", true)]
    [InlineData("/api/local-monitor/v1/repositories//locators", false)]
    [InlineData("/api/local-monitor/v1/repositories/one/extra", false)]
    [InlineData("/api/local-monitor/v1/repositories/one/two/locators", false)]
    [InlineData("/api/local-monitor/v1/repositories/one%2Ftwo", true)]
    [InlineData("/api/local-monitor/v1/repositories/", true)]
    [InlineData("/api/local-monitor/v1/repositories/one", true)]
    [InlineData("/api/local-monitor/v1/sessions//repository-assignment", false)]
    [InlineData("/api/local-monitor/v1/sessions/one/repository-assignment/extra", false)]
    [InlineData("/prefix/api/local-monitor/v1/repositories", false)]
    [InlineData("/api/local-monitor/v1/repositories-suffix", false)]
    [InlineData("/api/local-monitor/v10/repositories", false)]
    [InlineData("/api/local-monitor/v1/repository", false)]
    public async Task OwnedTemplateClassifier_HasRouterParity(string path, bool expected)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());

        using var response = await host.Client.DeleteAsync(path);

        Assert.Equal(expected, LocalRepositoryRoutes.IsOwnedPath(path));
        Assert.Equal(expected ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            expected
                ? "{\"error\":\"method_not_allowed\"}"
                : "{\"accepted\":false,\"error\":\"unsupported_endpoint\",\"message\":\"Only /v1/traces is supported.\"}",
            await response.Content.ReadAsStringAsync());
        if (LocalRepositoryRoutes.IsNamespacePath(path))
            Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/repositories", 16384, HttpStatusCode.BadRequest)]
    [InlineData("/api/local-monitor/v1/repositories", 16385, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("/api/local-monitor/v1/session-repository-actions", 4096, HttpStatusCode.BadRequest)]
    [InlineData("/api/local-monitor/v1/session-repository-actions", 4097, HttpStatusCode.RequestEntityTooLarge)]
    public async Task DeclaredBodyLimits_AreExact(string path, int length, HttpStatusCode status)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = Mutation(path, new ByteArrayContent(Enumerable.Repeat((byte)' ', length).ToArray()));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            status == HttpStatusCode.RequestEntityTooLarge ? "{\"error\":\"request_too_large\"}" : "{\"error\":\"invalid_request\"}",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task ConfiguredKestrelLimit_UsesRepository413BytesForStreamingBody()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, maxRequestBodyBytes: 64, testOptions: QuietHost());
        using var request = Mutation("/api/local-monitor/v1/repositories", new StreamingContent(Encoding.UTF8.GetBytes(CreateBody)));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("{\"error\":\"request_too_large\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native405_DerivesMissingAllowAndPreservesExistingAllow(bool existingAllow)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseRouting();
        app.Use((context, next) => LocalRepositoryRoutes.AdaptMethodNotAllowedAsync(
            context,
            next,
            ((IEndpointRouteBuilder)app).DataSources));
        if (existingAllow)
        {
            app.Use(async (context, next) =>
            {
                await next(context);
                if (context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed)
                    context.Response.Headers.Allow = "FRAMEWORK";
            });
        }
        app.MapPost(LocalRepositoryContracts.CollectionRoute, () => Results.NoContent());
        await app.StartAsync();
        try
        {
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new(address) };
            using var put = await client.PutAsync(LocalRepositoryContracts.CollectionRoute, new ByteArrayContent([]));
            using var head = await client.SendAsync(new(HttpMethod.Head, LocalRepositoryContracts.CollectionRoute));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
            Assert.Equal("{\"error\":\"method_not_allowed\"}", await put.Content.ReadAsStringAsync());
            Assert.Equal(existingAllow ? ["FRAMEWORK"] : ["POST"], AllowedMethods(put));
            AssertContractHeaders(put);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            Assert.Equal(existingAllow ? ["FRAMEWORK"] : ["POST"], AllowedMethods(head));
            AssertContractHeaders(head);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AllMutationAndReadRoutes_UseActualPersistenceAndExactReplayBytes()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());

        using var created = await SendMutationAsync(
            host.Client,
            HttpMethod.Post,
            LocalRepositoryContracts.CollectionRoute,
            "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":\"https://github.com/Example/One\"}",
            OperationKey(1));
        var repositoryId = Property(await created.Content.ReadAsByteArrayAsync(), "repository_id");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertContractHeaders(created);

        var renameBody = $"{{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Renamed\",\"github_locator\":null}}";
        using var renamed = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", renameBody, OperationKey(2));
        using var renameReplay = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", renameBody, OperationKey(2));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(await renamed.Content.ReadAsByteArrayAsync(), await renameReplay.Content.ReadAsByteArrayAsync());
        AssertContractHeaders(renamed);
        AssertContractHeaders(renameReplay);

        var locatorBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":2,\"operation\":\"set_github_locator\",\"display_name\":null,\"github_locator\":\"git@github.com:Example/Two.git\"}";
        using var located = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", locatorBody, OperationKey(3));
        Assert.Equal(HttpStatusCode.OK, located.StatusCode);
        AssertContractHeaders(located);

        using var locators = await host.Client.GetAsync($"/api/local-monitor/v1/repositories/{repositoryId}/locators?");
        var locatorBytes = await locators.Content.ReadAsByteArrayAsync();
        using (var document = JsonDocument.Parse(locatorBytes))
        {
            var items = document.RootElement.GetProperty("locators");
            Assert.Equal(2, items.GetArrayLength());
            Assert.True(items[0].GetProperty("is_current").GetBoolean());
            Assert.Equal("github.com/example/two", items[0].GetProperty("canonical_locator").GetString());
            Assert.False(items[1].GetProperty("is_current").GetBoolean());
            Assert.Equal("github.com/example/one", items[1].GetProperty("canonical_locator").GetString());
            Assert.Null(items[0].GetProperty("provenance").GetString());
        }
        AssertContractHeaders(locators);

        CreateSession(temp.DatabasePath, SessionId);
        var assignBody = $"{{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"{SessionId}\",\"expected_revision\":0,\"action\":\"assign\",\"repository_id\":\"{repositoryId}\"}}";
        using var assigned = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.SessionActionRoute, assignBody, OperationKey(4));
        using var assignedReplay = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.SessionActionRoute, assignBody, OperationKey(4));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal(await assigned.Content.ReadAsByteArrayAsync(), await assignedReplay.Content.ReadAsByteArrayAsync());
        Assert.Contains("\"state\":\"assigned\",\"authority\":\"manual\"", await assigned.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertContractHeaders(assigned);

        using var assignment = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/repository-assignment");
        Assert.Equal(await assigned.Content.ReadAsByteArrayAsync(), await assignment.Content.ReadAsByteArrayAsync());
        AssertContractHeaders(assignment);

        var unassignBody = $"{{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"{SessionId}\",\"expected_revision\":1,\"action\":\"explicitly_unassign\",\"repository_id\":null}}";
        using var unassigned = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.SessionActionRoute, unassignBody, OperationKey(5));
        Assert.Contains("\"state\":\"explicitly_unassigned\",\"authority\":\"manual\"", await unassigned.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertContractHeaders(unassigned);

        var resumeBody = $"{{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"{SessionId}\",\"expected_revision\":2,\"action\":\"resume_automatic\",\"repository_id\":null}}";
        using var resumed = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.SessionActionRoute, resumeBody, OperationKey(6));
        Assert.Contains("\"state\":\"unassigned\",\"authority\":\"none\"", await resumed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("\"observed_label_candidates\":[]", await resumed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertContractHeaders(resumed);
    }

    [Fact]
    public async Task LocatorRead_PreservesLogicalLowercaseGitSuffixFromExactPersistedLocator()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var created = await SendMutationAsync(
            host.Client,
            HttpMethod.Post,
            LocalRepositoryContracts.CollectionRoute,
            "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"Repository.git\",\"github_locator\":\"https://github.com/Owner/Repository.git.git\"}",
            OperationKey(7));
        var repositoryId = Property(await created.Content.ReadAsByteArrayAsync(), "repository_id");

        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/repositories/{repositoryId}/locators");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "\"canonical_locator\":\"github.com/owner/repository.git\",\"display_owner\":\"Owner\",\"display_repository\":\"Repository.git\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        AssertContractHeaders(response);
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/json; profile=\"synthetic\"", true)]
    [InlineData(null, false)]
    [InlineData("text/json", false)]
    [InlineData("application/json; charset", false)]
    [InlineData("application/json,application/json", false)]
    public async Task MutationMediaType_UsesOneParsedJsonValue(string? contentType, bool accepted)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody));
        using var request = Mutation(LocalRepositoryContracts.CollectionRoute, content);
        content.Headers.Remove("Content-Type");
        if (contentType is not null) content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(accepted ? HttpStatusCode.Created : HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        AssertContractHeaders(response);
        Assert.Equal(accepted ? 1 : 0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task RepeatedMediaTypeHeader_IsRejectedBeforePersistence()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody));
        using var request = Mutation(LocalRepositoryContracts.CollectionRoute, content);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", ["application/json", "application/json"]);

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lrc1_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=")]
    [InlineData("lrc1_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ+")]
    [InlineData("LRC1_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE")]
    [InlineData("lrc1_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQF")]
    [InlineData("lrc1_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE,lrc1_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE")]
    public async Task InvalidOperationKey_IsRejectedAfterSemanticPreparation(string? key)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = Mutation(LocalRepositoryContracts.CollectionRoute, new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody)));
        request.Headers.Remove("Idempotency-Key");
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task RepeatedOperationKey_IsRejectedBeforePersistence()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = Mutation(LocalRepositoryContracts.CollectionRoute, new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody)));
        request.Headers.Remove("Idempotency-Key");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", [Key, OperationKey(2)]);

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData("{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"\",\"github_locator\":null}", "invalid_request")]
    [InlineData("{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":\"not-a-locator\"}", "invalid_locator")]
    public async Task SemanticFailure_PrecedesInvalidOperationKeyAndWritesNothing(string body, string error)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = Mutation(LocalRepositoryContracts.CollectionRoute, new ByteArrayContent(Encoding.UTF8.GetBytes(body)));
        request.Headers.Remove("Idempotency-Key");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "invalid");

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, error);
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task InvalidTargetPreparation_PrecedesInvalidKeyAndDomainAccess()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        var patchBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}";
        using var patch = await SendMutationAsync(host.Client, HttpMethod.Patch, "/api/local-monitor/v1/repositories/NOT-CANONICAL", patchBody, "invalid");
        var actionBody = "{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"NOT-CANONICAL\",\"expected_revision\":0,\"action\":\"resume_automatic\",\"repository_id\":null}";
        using var action = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.SessionActionRoute, actionBody, "invalid");

        await AssertErrorAsync(patch, HttpStatusCode.NotFound, "repository_not_found");
        await AssertErrorAsync(action, HttpStatusCode.NotFound, "session_not_found");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("padded")]
    [InlineData("noncanonical")]
    public async Task InvalidLocatorPreparation_PrecedesEveryInvalidOperationKeyShape(string keyShape)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = Mutation(
            LocalRepositoryContracts.CollectionRoute,
            new ByteArrayContent(Encoding.UTF8.GetBytes(
                "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":\"not-a-locator\"}")));
        SetInvalidOperationKey(request, keyShape);

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_locator");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("padded")]
    [InlineData("noncanonical")]
    public async Task InvalidCanonicalTargetPreparation_PrecedesEveryInvalidOperationKeyShape(string keyShape)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            "/api/local-monitor/v1/repositories/01900000-0000-4000-8000-000000000001")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}")),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        SetInvalidOperationKey(request, keyShape);

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "repository_not_found");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task CrossSiteAndQueryAdmission_PrecedeRouteSemantics()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var crossSiteRead = new HttpRequestMessage(HttpMethod.Get, $"/api/local-monitor/v1/repositories/{SessionId}/locators");
        crossSiteRead.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        using var readResponse = await host.Client.SendAsync(crossSiteRead);
        using var crossSiteMutation = Mutation(LocalRepositoryContracts.CollectionRoute, new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody)));
        crossSiteMutation.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        using var mutationResponse = await host.Client.SendAsync(crossSiteMutation);
        using var queryMutation = new HttpRequestMessage(HttpMethod.Patch, "/api/local-monitor/v1/repositories/NOT-CANONICAL?x=1")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody)),
        };
        queryMutation.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        using var queryResponse = await host.Client.SendAsync(queryMutation);

        await AssertErrorAsync(readResponse, HttpStatusCode.Forbidden, "csrf_rejected");
        await AssertErrorAsync(mutationResponse, HttpStatusCode.Forbidden, "csrf_rejected");
        await AssertErrorAsync(queryResponse, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task MissingOrWrongCsrf_IsRejectedWithExactHeaders(string? csrf)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = Mutation(LocalRepositoryContracts.CollectionRoute, new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody)));
        request.Headers.Remove("x-monitor-csrf");
        if (csrf is not null) request.Headers.TryAddWithoutValidation("x-monitor-csrf", csrf);

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "csrf_rejected");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task ReadQuery_IsRejectedBeforeInvalidUuidAndBareQueryIsAllowed()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var withMember = await host.Client.GetAsync("/api/local-monitor/v1/repositories/NOT-CANONICAL/locators?x=1");
        using var bare = await host.Client.GetAsync($"/api/local-monitor/v1/repositories/{SessionId}/locators?");

        await AssertErrorAsync(withMember, HttpStatusCode.BadRequest, "invalid_request");
        await AssertErrorAsync(bare, HttpStatusCode.NotFound, "repository_not_found");
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/repositories", 16384, HttpStatusCode.BadRequest)]
    [InlineData("/api/local-monitor/v1/repositories", 16385, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("/api/local-monitor/v1/session-repository-actions", 4096, HttpStatusCode.BadRequest)]
    [InlineData("/api/local-monitor/v1/session-repository-actions", 4097, HttpStatusCode.RequestEntityTooLarge)]
    public async Task StreamedRouteBodyLimits_AreExact(string path, int length, HttpStatusCode status)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = Mutation(path, new StreamingContent(Enumerable.Repeat((byte)' ', length).ToArray()));

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, status, status == HttpStatusCode.RequestEntityTooLarge ? "request_too_large" : "invalid_request");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/repositories", false)]
    [InlineData("/api/local-monitor/v1/repositories", true)]
    [InlineData("/api/local-monitor/v1/session-repository-actions", false)]
    [InlineData("/api/local-monitor/v1/session-repository-actions", true)]
    public async Task LowerKestrelLimit_MapsDeclaredAndStreamedBodiesForBothMutationFamilies(string path, bool streamed)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, maxRequestBodyBytes: 64, testOptions: QuietHost());
        var bytes = Enumerable.Repeat((byte)' ', 128).ToArray();
        using HttpContent content = streamed ? new StreamingContent(bytes) : new ByteArrayContent(bytes);
        using var request = Mutation(path, content);

        using var response = await host.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task RepositoryAdapter_DoesNotChangeNeighboringTrace413Bytes()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, maxRequestBodyBytes: 64, testOptions: QuietHost());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new StreamingContent(Enumerable.Repeat((byte)' ', 128).ToArray()),
        };
        request.Content.Headers.ContentType = new("application/json");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            "{\"accepted\":false,\"error\":\"request_too_large\",\"message\":\"Trace payload exceeds the configured request body size limit.\"}",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DomainErrorsAreNotReceiptedAndReevaluateCurrentState()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var first = await SendCreateAsync(host.Client);
        var repositoryId = Property(await first.Content.ReadAsByteArrayAsync(), "repository_id");
        var key = OperationKey(40);
        var staleBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":2,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}";
        using var stale = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", staleBody, key);
        Assert.Equal(1, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
        var currentBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}";
        using var current = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", currentBody, key);

        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(2, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
        AssertContractHeaders(current);
    }

    [Fact]
    public async Task MissingTargetsAndIdempotencyConflict_HaveExactErrorsAndNoErrorReceipts()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        var missingId = "01900000-0000-7000-8000-0000000000f1";
        var patchBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Two\",\"github_locator\":null}";
        using var missingPatch = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{missingId}", patchBody, OperationKey(50));
        using var missingLocators = await host.Client.GetAsync($"/api/local-monitor/v1/repositories/{missingId}/locators");
        using var missingAssignment = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{missingId}/repository-assignment");
        var actionBody = $"{{\"schema_version\":\"local-session-repository-action.v1\",\"session_id\":\"{missingId}\",\"expected_revision\":0,\"action\":\"resume_automatic\",\"repository_id\":null}}";
        using var missingAction = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.SessionActionRoute, actionBody, OperationKey(51));
        using var created = await SendCreateAsync(host.Client);
        using var conflict = await SendMutationAsync(
            host.Client,
            HttpMethod.Post,
            LocalRepositoryContracts.CollectionRoute,
            "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"Different\",\"github_locator\":null}",
            Key);

        await AssertErrorAsync(missingPatch, HttpStatusCode.NotFound, "repository_not_found");
        await AssertErrorAsync(missingLocators, HttpStatusCode.NotFound, "repository_not_found");
        await AssertErrorAsync(missingAssignment, HttpStatusCode.NotFound, "session_not_found");
        await AssertErrorAsync(missingAction, HttpStatusCode.NotFound, "session_not_found");
        await AssertErrorAsync(conflict, HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(1, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task LocatorConflict_IsNotReceiptedAndCanReevaluateWithTheSameKey()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var first = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.CollectionRoute,
            "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":null}", OperationKey(60));
        using var second = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.CollectionRoute,
            "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"Two\",\"github_locator\":\"https://github.com/example/two\"}", OperationKey(61));
        var firstId = Property(await first.Content.ReadAsByteArrayAsync(), "repository_id");
        var conflictBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"set_github_locator\",\"display_name\":null,\"github_locator\":\"https://github.com/example/two\"}";
        var key = OperationKey(62);
        using var conflict = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{firstId}", conflictBody, key);
        var acceptedBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"set_github_locator\",\"display_name\":null,\"github_locator\":\"https://github.com/example/three\"}";
        using var accepted = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{firstId}", acceptedBody, key);

        await AssertErrorAsync(conflict, HttpStatusCode.Conflict, "locator_conflict");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(3, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task LocatorLimitAndComplete128Read_AreDeterministicAndErrorIsNotReceipted()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var created = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.CollectionRoute,
            "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"Many\",\"github_locator\":\"https://github.com/example/initial\"}", OperationKey(0));
        var repositoryId = Property(await created.Content.ReadAsByteArrayAsync(), "repository_id");
        for (var index = 1; index <= 127; index++)
        {
            var body = $"{{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":{index},\"operation\":\"set_github_locator\",\"display_name\":null,\"github_locator\":\"https://github.com/example/item{index:D3}\"}}";
            using var response = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", body, OperationKey(index));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var complete = await host.Client.GetAsync($"/api/local-monitor/v1/repositories/{repositoryId}/locators");
        using (var document = JsonDocument.Parse(await complete.Content.ReadAsByteArrayAsync()))
        {
            var locators = document.RootElement.GetProperty("locators");
            Assert.Equal(128, locators.GetArrayLength());
            Assert.Equal("github.com/example/item127", locators[0].GetProperty("canonical_locator").GetString());
            Assert.True(locators[0].GetProperty("is_current").GetBoolean());
            Assert.All(locators.EnumerateArray().Skip(1), static item => Assert.False(item.GetProperty("is_current").GetBoolean()));
        }
        var limitKey = OperationKey(200);
        var limitBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":128,\"operation\":\"set_github_locator\",\"display_name\":null,\"github_locator\":\"https://github.com/example/overflow\"}";
        using var limit = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", limitBody, limitKey);
        var historicalBody = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":128,\"operation\":\"set_github_locator\",\"display_name\":null,\"github_locator\":\"https://github.com/example/initial\"}";
        using var historical = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", historicalBody, limitKey);

        await AssertErrorAsync(limit, HttpStatusCode.Conflict, "locator_limit_reached");
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        Assert.Equal(129, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task AssignmentRead_ReturnsTheCompleteDeterministicallySorted128ConflictSet()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        CreateSession(temp.DatabasePath, SessionId);
        var eventId = "01900000-0000-7000-8000-0000000000e1";
        CreateSessionEvent(temp.DatabasePath, SessionId, eventId);
        var repositoryIds = new List<string>(128);
        for (var index = 0; index < 128; index++)
        {
            using var created = await SendMutationAsync(
                host.Client,
                HttpMethod.Post,
                LocalRepositoryContracts.CollectionRoute,
                $"{{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"Repo{index:D3}\",\"github_locator\":\"https://github.com/Owner/Repo{index:D3}\"}}",
                OperationKey(1000 + index));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var repositoryId = Property(await created.Content.ReadAsByteArrayAsync(), "repository_id");
            repositoryIds.Add(repositoryId);
        }
        ApplyAutomaticCandidates(temp.DatabasePath, SessionId, eventId, repositoryIds);

        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/repository-assignment");
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Unexpected {(int)response.StatusCode}: {Encoding.UTF8.GetString(responseBytes)}");
        using var document = JsonDocument.Parse(responseBytes);
        var root = document.RootElement;
        var conflicts = root.GetProperty("conflicting_repository_ids").EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("conflict", root.GetProperty("state").GetString());
        Assert.Equal("automatic", root.GetProperty("authority").GetString());
        Assert.Equal(1, root.GetProperty("assignment_revision").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("repository_id").ValueKind);
        Assert.Equal(repositoryIds.OrderBy(static value => value, StringComparer.Ordinal), conflicts);
        Assert.Equal(128, conflicts.Length);
        Assert.Empty(root.GetProperty("observed_label_candidates").EnumerateArray());
        Assert.Equal("1970-01-01T00:00:00.0000000+00:00", root.GetProperty("updated_at").GetString());
        AssertContractHeaders(response);
    }

    [Fact]
    public async Task RequestCancellationEscapesBeforeAnyReceiptOrDomainWrite()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var request = Mutation(LocalRepositoryContracts.CollectionRoute, new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client.SendAsync(request, cancellation.Token));

        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task BodyCancellationEscapesBeforeAnyReceiptOrDomainWrite()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var cancellation = new CancellationTokenSource();
        using var request = Mutation(LocalRepositoryContracts.CollectionRoute, new CancelingContent(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client.SendAsync(request, cancellation.Token));

        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task ApplicationCancellationEscapesBeforeAnyReceiptOrDomainWrite()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        var application = CreateApplication(temp.DatabasePath, temp.TimeProvider);
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(
            application.PrepareCreate(new("One", null))).Prepared;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application.ExecutePreparedAsync(
            prepared,
            Key,
            value => LocalRepositoryJson.WriteRepository(201, value),
            cancellation.Token).AsTask());

        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task TypedSqliteBusyOnly_MapsToPersistenceBusy()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var created = await SendCreateAsync(host.Client);
        var repositoryId = Property(await created.Content.ReadAsByteArrayAsync(), "repository_id");
        using var lockConnection = Open(temp.DatabasePath);
        using var transaction = lockConnection.BeginTransaction(deferred: false);
        var body = "{\"schema_version\":\"local-repository-update.v1\",\"expected_revision\":1,\"operation\":\"rename\",\"display_name\":\"Blocked\",\"github_locator\":null}";

        using var response = await SendMutationAsync(host.Client, HttpMethod.Patch, $"/api/local-monitor/v1/repositories/{repositoryId}", body, OperationKey(90));

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "persistence_busy");
        Assert.Equal(1, ScalarLong(lockConnection, transaction, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task CorruptReadEscapesAsInternalErrorRatherThanPersistenceBusy()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        using var created = await SendMutationAsync(host.Client, HttpMethod.Post, LocalRepositoryContracts.CollectionRoute,
            "{\"schema_version\":\"local-repository-create.v1\",\"display_name\":\"One\",\"github_locator\":\"https://github.com/example/one\"}", OperationKey(91));
        var repositoryId = Property(await created.Content.ReadAsByteArrayAsync(), "repository_id");
        Execute(temp.DatabasePath, "DROP TRIGGER local_repository_locators_update_rejected; UPDATE local_repository_locators SET canonical_locator='invalid';");

        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/repositories/{repositoryId}/locators");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("{\"accepted\":false,\"error\":\"internal_error\",\"message\":\"The request could not be processed.\"}", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("persistence_busy", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendCreateAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-monitor/v1/repositories")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(CreateBody)),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        request.Headers.Add("Idempotency-Key", Key);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string body,
        string key)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Mutation(string path, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        request.Headers.Add("Idempotency-Key", Key);
        return request;
    }

    private static MonitorHostTestOptions QuietHost() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        LocalRepositoryApplicationFactory = CreateApplication,
    };

    private static LocalRepositoryCatalogApplication CreateApplication(string databasePath, TimeProvider timeProvider)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            LocalRepositoryCatalogSchemaV1.Ensure(connection);
        }
        var queue = new SqliteLocalRepositoryReconciliationStore(databasePath, timeProvider);
        return new(new SqliteLocalRepositoryCatalogStore(
            databasePath,
            queue,
            new LocalRepositoryAssignmentResolver(),
            timeProvider));
    }

    private static long ScalarLong(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        return ScalarLong(connection, null, sql);
    }

    private static long ScalarLong(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CreateSession(string databasePath, string sessionId) => Execute(
        databasePath,
        $"INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at) VALUES('{sessionId}','completed','full','1970-01-01T00:00:00.0000000+00:00','not_captured','1970-01-01T00:00:00.0000000+00:00','1970-01-01T00:00:00.0000000+00:00');");

    private static void CreateSessionEvent(string databasePath, string sessionId, string eventId) => Execute(
        databasePath,
        $"INSERT INTO session_events(event_id,session_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version) VALUES('{eventId}','{sessionId}','vscode','11111111111111111111111111111111','otel-exact','11111111111111111111111111111111/2222222222222222','otel.span','1970-01-01T00:00:00.0000000+00:00','not_captured','1.2.3');");

    private static void ApplyAutomaticCandidates(
        string databasePath,
        string sessionId,
        string eventId,
        IReadOnlyList<string> repositoryIds)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        const long rawRecordId = 1;
        var rawPayloadSha256 = new string('a', 64);
        var reconciliationFingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, rawPayloadSha256));
        var contexts = new List<(LocalRepositoryProspectiveAssignmentContext Assignment, string ObservationId, string SourceIdentity, string LocatorId, string CanonicalLocator, string LocatorSha256, string DisplayOwner, string DisplayRepository)>();
        for (var index = 0; index < repositoryIds.Count; index++)
        {
            var repositoryId = repositoryIds[index];
            using var locatorCommand = connection.CreateCommand();
            locatorCommand.Transaction = transaction;
            locatorCommand.CommandText = "SELECT locator_id,canonical_locator,locator_sha256,display_owner,display_repository FROM local_repository_locators WHERE repository_id=$repository_id;";
            locatorCommand.Parameters.AddWithValue("$repository_id", repositoryId);
            using var locatorReader = locatorCommand.ExecuteReader();
            Assert.True(locatorReader.Read());
            var locatorId = locatorReader.GetString(0);
            var canonicalLocator = locatorReader.GetString(1);
            var locatorSha256 = locatorReader.GetString(2);
            var displayOwner = locatorReader.GetString(3);
            var displayRepository = locatorReader.GetString(4);
            Assert.False(locatorReader.Read());
            var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
                LocalRepositorySourceIdentityInput.Span(rawRecordId, 0, 0, 0, index, "vcs.repository.url.full"));
            var contextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(
                sourceIdentity,
                sessionId,
                eventId,
                "11111111111111111111111111111111",
                "2222222222222222"));
            contexts.Add((
                new(
                    $"01900000-0000-7000-8002-{index + 1:000000000000}",
                    contextIdentity,
                    sessionId,
                    repositoryId,
                    locatorId),
                $"01900000-0000-7000-8001-{index + 1:000000000000}",
                sourceIdentity,
                locatorId,
                canonicalLocator,
                locatorSha256,
                displayOwner,
                displayRepository));
        }

        var resolver = new LocalRepositoryAssignmentResolver();
        var occurredAt = DateTimeOffset.UnixEpoch;
        var preparation = resolver.PrepareAutomatic(
            connection,
            transaction,
            rawRecordId,
            [sessionId],
            contexts.Select(static item => item.Assignment).ToArray(),
            reconciliationFingerprint,
            occurredAt);
        for (var index = 0; index < contexts.Count; index++)
        {
            var context = contexts[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            INSERT INTO session_repository_observations(
              observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,scope_kind,attribute_key,value_classification,
              locator_kind,canonical_locator,locator_sha256,display_owner,display_repository,source_surface,source_application_version,observed_at)
            VALUES($observation_id,$source_identity,$raw_record_id,$payload,0,0,0,$attribute_ordinal,'span','vcs.repository.url.full','admitted',
              'github_repository',$canonical_locator,$locator_sha256,$display_owner,$display_repository,'github-copilot-vscode','1.2.3','1970-01-01T00:00:00.0000000+00:00');
            INSERT INTO session_repository_observation_contexts(
              context_id,observation_id,context_identity_sha256,session_event_id,session_id,trace_id,span_id,admission_state,repository_id,locator_id,observed_at)
            VALUES($context_id,$observation_id,$context_identity,$event_id,$session_id,'11111111111111111111111111111111','2222222222222222','admitted',$repository_id,$locator_id,'1970-01-01T00:00:00.0000000+00:00');
            """;
            command.Parameters.AddWithValue("$observation_id", context.ObservationId);
            command.Parameters.AddWithValue("$context_id", context.Assignment.ContextId);
            command.Parameters.AddWithValue("$source_identity", context.SourceIdentity);
            command.Parameters.AddWithValue("$context_identity", context.Assignment.ContextIdentitySha256);
            command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
            command.Parameters.AddWithValue("$payload", rawPayloadSha256);
            command.Parameters.AddWithValue("$attribute_ordinal", index);
            command.Parameters.AddWithValue("$canonical_locator", context.CanonicalLocator);
            command.Parameters.AddWithValue("$locator_sha256", context.LocatorSha256);
            command.Parameters.AddWithValue("$display_owner", context.DisplayOwner);
            command.Parameters.AddWithValue("$display_repository", context.DisplayRepository);
            command.Parameters.AddWithValue("$event_id", eventId);
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$repository_id", context.Assignment.RepositoryId);
            command.Parameters.AddWithValue("$locator_id", context.LocatorId);
            command.ExecuteNonQuery();
        }
        using (var queue = connection.CreateCommand())
        {
            queue.Transaction = transaction;
            queue.CommandText = """
                INSERT INTO local_repository_reconciliation_queue(
                  queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,terminal_reason,created_at,updated_at)
                VALUES($queue_id,$raw_record_id,'payload_sha256',$payload,'local-repository-catalog:1',$fingerprint,'completed',0,NULL,NULL,NULL,'1970-01-01T00:00:00.0000000+00:00','1970-01-01T00:00:00.0000000+00:00');
                """;
            queue.Parameters.AddWithValue("$queue_id", "01900000-0000-7000-8003-000000000001");
            queue.Parameters.AddWithValue("$raw_record_id", rawRecordId);
            queue.Parameters.AddWithValue("$payload", rawPayloadSha256);
            queue.Parameters.AddWithValue("$fingerprint", reconciliationFingerprint);
            queue.ExecuteNonQuery();
        }
        var result = resolver.ApplyAutomatic(connection, transaction, preparation);
        Assert.Equal(LocalRepositoryAssignmentReconcileStatus.Applied, result.Status);
        transaction.Commit();
    }

    private static void SetInvalidOperationKey(HttpRequestMessage request, string shape)
    {
        request.Headers.Remove("Idempotency-Key");
        switch (shape)
        {
            case "missing": return;
            case "repeated": request.Headers.TryAddWithoutValidation("Idempotency-Key", [Key, OperationKey(999)]); return;
            case "padded": request.Headers.TryAddWithoutValidation("Idempotency-Key", Key + "="); return;
            case "noncanonical": request.Headers.TryAddWithoutValidation("Idempotency-Key", "LRC1_" + Key[5..]); return;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private static string Property(byte[] entity, string property)
    {
        using var document = JsonDocument.Parse(entity);
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private static string OperationKey(int seed)
    {
        var bytes = SHA256.HashData(BitConverter.GetBytes(seed));
        return "lrc1_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal($"{{\"error\":\"{error}\"}}", await response.Content.ReadAsStringAsync());
        AssertContractHeaders(response);
    }

    private static void AssertContractHeaders(HttpResponseMessage response)
    {
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.TryGetValues("Cache-Control", out var values));
        Assert.Equal(["no-store"], values);
        Assert.False(response.Headers.Contains("Location"));
        Assert.False(response.Headers.Contains("ETag"));
    }

    private static IReadOnlyList<string> AllowedMethods(HttpResponseMessage response) =>
        response.Content.Headers.Allow.ToArray();

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await stream.WriteAsync(bytes);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CancelingContent(CancellationTokenSource cancellation) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, cancellation.Token);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync("{\"schema_version\":\"local-repository-create.v1\","u8.ToArray(), cancellationToken);
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

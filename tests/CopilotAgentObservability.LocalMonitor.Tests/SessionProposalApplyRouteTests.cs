using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.ProposalApply;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionProposalApplyRouteTests
{
    [Theory]
    [InlineData("GET", "/api/session-workspace/proposal-applies/roots", false)]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts", true)]
    [InlineData("GET", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001", false)]
    [InlineData("PUT", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/selection", true)]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/approve", true)]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/apply", false)]
    [InlineData("POST", "/api/session-workspace/proposal-applies/0197d7c0-0000-7000-8000-000000000001/rollback", false)]
    public async Task Every_proposal_apply_route_applies_policy_before_configuration(string method, string path, bool body)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        using var crossSite = CreateRequest(method, path, body);
        crossSite.Headers.TryAddWithoutValidation("Origin", "https://example.test");

        var denied = await host.Client.SendAsync(crossSite);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        using var noRoots = CreateRequest(method, path, body);
        noRoots.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");
        var unavailable = await host.Client.SendAsync(noRoots);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("apply_not_configured", await unavailable.Content.ReadAsStringAsync());
        Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Apply_rejects_a_nonempty_body_before_mutation()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{Guid.CreateVersion7()}/apply")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_apply_request", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Apply_rejects_chunked_body_over_one_mebibyte_before_mutation()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{Guid.CreateVersion7()}/apply")
        {
            Content = new StreamContent(new MemoryStream(new byte[1_048_577])),
        };
        request.Headers.TransferEncodingChunked = true;
        request.Content.Headers.ContentLength = null;
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("request_too_large", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts")]
    [InlineData("PUT", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/selection")]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/approve")]
    public async Task Body_routes_require_json_before_ids_or_mutation(string method, string path)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("unsupported_media_type", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts")]
    [InlineData("PUT", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/selection")]
    [InlineData("POST", "/api/session-workspace/proposal-applies/drafts/0197d7c0-0000-7000-8000-000000000001/approve")]
    public async Task Body_routes_reject_declared_body_over_one_mebibyte(string method, string path)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent(new string('x', 1_048_577), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("request_too_large", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/session-workspace/proposal-applies/drafts/invalid/apply")]
    [InlineData("/api/session-workspace/proposal-applies/invalid/rollback")]
    public async Task Mutation_routes_reject_invalid_id_once_after_empty_body_validation(string path)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_apply_request", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Empty_mutation_body_accepts_arbitrary_content_type_then_validates_id()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts/invalid/apply") { Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain") };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_apply_request", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Roots_requires_same_origin_and_returns_only_opaque_root_metadata()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        await using var host = await StartAsync(temp, root);

        using var forbidden = new HttpRequestMessage(HttpMethod.Get, "/api/session-workspace/proposal-applies/roots");
        forbidden.Headers.TryAddWithoutValidation("Origin", "https://example.test");
        var denied = await host.Client.SendAsync(forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session-workspace/proposal-applies/roots");
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"kind\":\"repository\"", body);
        Assert.DoesNotContain(rootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Draft_creation_checks_the_persisted_proposal_before_target_filesystem_access()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        var missingProposal = Guid.CreateVersion7();
        var marker = "missing-target-should-not-be-probed.txt";
        var rootId = (await GetSingleRootIdAsync(host.Client)).ToString("D");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts")
        {
            Content = new StringContent($$"""{"proposal_id":"{{missingProposal:D}}","root_id":"{{rootId}}","files":[{"relative_path":"{{marker}}","replacement_text":"replacement"}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("{\"error\":\"proposal_not_found\"}", body);
        Assert.False(File.Exists(Path.Combine(rootPath, marker)));
        Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_workflow_persists_apply_and_allows_exactly_one_rollback_across_restarts()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        var first = Path.Combine(rootPath, "one.txt");
        var second = Path.Combine(rootPath, "two.txt");
        File.WriteAllText(first, "one\n");
        File.WriteAllText(second, "two\n");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        var proposalId = Guid.CreateVersion7();
        Guid draftId;
        Guid applyId;

        await using (var host = await StartAsync(temp, root))
        {
            InsertPersistedProposal(temp.DatabasePath, proposalId);
            var rootId = await GetSingleRootIdAsync(host.Client);
            using var create = JsonRequest(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts", $$"""{"proposal_id":"{{proposalId:D}}","root_id":"{{rootId:D}}","files":[{"relative_path":"one.txt","replacement_text":"ONE\n"},{"relative_path":"two.txt","replacement_text":"TWO\n"}]}""");
            using var created = await host.Client.SendAsync(create);
            var createBody = await created.Content.ReadAsStringAsync();
            Assert.True(created.StatusCode == HttpStatusCode.Created, createBody);
            Assert.Contains("--- a/one.txt", createBody);
            using var draft = JsonDocument.Parse(createBody);
            draftId = draft.RootElement.GetProperty("draft_id").GetGuid();
            var hunkId = draft.RootElement.GetProperty("hunks")[0].GetProperty("hunk_id").GetString();

            using var selection = JsonRequest(HttpMethod.Put, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/selection", $$"""{"selection_revision":1,"selected_hunk_ids":["{{hunkId}}"]}""");
            using var selected = await host.Client.SendAsync(selection);
            Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
            var selectedBody = await selected.Content.ReadAsStringAsync();
            Assert.DoesNotContain("one.txt", selectedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("ONE", selectedBody, StringComparison.Ordinal);
            using var selectedJson = JsonDocument.Parse(selectedBody);
            var revision = selectedJson.RootElement.GetProperty("selection_revision").GetInt32();
            var digest = selectedJson.RootElement.GetProperty("approval_digest").GetString();

            using var approve = JsonRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/approve", $$"""{"selection_revision":{{revision}},"approval_digest":"{{digest}}"}""");
            using var approved = await host.Client.SendAsync(approve);
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            Assert.DoesNotContain("one.txt", await approved.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var apply = CsrfRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/apply");
            using var applied = await host.Client.SendAsync(apply);
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            using var appliedJson = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
            applyId = appliedJson.RootElement.GetProperty("apply_id").GetGuid();
            Assert.Equal("ONE\n", File.ReadAllText(first));
            Assert.Equal("two\n", File.ReadAllText(second));
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_applies WHERE state='applied';"));
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_apply_audit WHERE state='applied';"));
        }

        await using (var restarted = await StartAsync(temp, root))
        {
            using var rollback = CsrfRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/{applyId:D}/rollback");
            using var rolledBack = await restarted.Client.SendAsync(rollback);
            Assert.Equal(HttpStatusCode.OK, rolledBack.StatusCode);
            Assert.Equal("one\n", File.ReadAllText(first));
            Assert.Equal("two\n", File.ReadAllText(second));
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_apply_audit WHERE state='rolled_back';"));
        }

        await using var afterRollback = await StartAsync(temp, root);
        using var secondRollback = CsrfRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/{applyId:D}/rollback");
        Assert.Equal(HttpStatusCode.NotFound, (await afterRollback.Client.SendAsync(secondRollback)).StatusCode);
    }

    private static HttpRequestMessage CreateRequest(string method, string path, bool body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<Guid> GetSingleRootIdAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session-workspace/proposal-applies/roots");
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items")[0].GetProperty("root_id").GetGuid();
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, string body)
    {
        var request = CsrfRequest(method, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage CsrfRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Monitor-Csrf", "local-monitor");
        return request;
    }

    private static void InsertPersistedProposal(string databasePath, Guid proposalId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO improvement_proposals(proposal_id,status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at) VALUES($id,'candidate','skill','fixture','fixture','fixture','fixture','fixture','2026-07-12T00:00:00+00:00','2026-07-12T00:00:00+00:00');";
        command.Parameters.AddWithValue("$id", proposalId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static async Task<RunningMonitorHost> StartAsync(MonitorTempDirectory temp, params ConfiguredApplyRoot[] roots)
    {
        var app = MonitorHost.Build(new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes, ApplyRoots: roots),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        await app.StartAsync();
        var address = app.Urls.Single();
        return new RunningMonitorHost(app, new HttpClient { BaseAddress = new Uri(address) }, address);
    }
}

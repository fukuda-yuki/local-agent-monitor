using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.LocalMonitor.ProposalApply;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionProposalApplyRouteTests
{
    [Theory]
    [InlineData("GET", "/api/session-workspace/proposal-applies/roots", false)]
    [InlineData("GET", "/api/session-workspace/proposal-applies/receipts?proposal_id=0197d7c0-0000-7000-8000-000000000001", false)]
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
    public async Task Receipt_query_rejects_malformed_id_with_no_store_and_no_sensitive_echo()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "receipt-root-marker");
        Directory.CreateDirectory(rootPath);
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        using var request = CsrfRequest(HttpMethod.Get, "/api/session-workspace/proposal-applies/receipts?proposal_id=bad-path-marker");
        using var response = await host.Client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("{\"error\":\"invalid_apply_request\"}", body);
        Assert.DoesNotContain("bad-path-marker", body, StringComparison.Ordinal);
        Assert.DoesNotContain(rootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_receipt_get_does_not_mutate_private_draft_after_startup_migration()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "legacy-receipt-root");
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "legacy.txt"), "before\n");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        var proposalId = Guid.CreateVersion7();
        Guid draftId;

        await using (var host = await StartAsync(temp, root))
        {
            InsertPersistedProposal(temp.DatabasePath, proposalId);
            var rootId = await GetSingleRootIdAsync(host.Client);
            using var create = JsonRequest(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts", $$"""{"proposal_id":"{{proposalId:D}}","root_id":"{{rootId:D}}","files":[{"relative_path":"legacy.txt","replacement_text":"after\n"}]}""");
            using var created = await host.Client.SendAsync(create);
            created.EnsureSuccessStatusCode();
            using var draft = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            draftId = draft.RootElement.GetProperty("draft_id").GetGuid();
            var revision = draft.RootElement.GetProperty("selection_revision").GetInt32();
            var digest = draft.RootElement.GetProperty("approval_digest").GetString();
            using var approve = JsonRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/approve", $$"""{"selection_revision":{{revision}},"approval_digest":"{{digest}}"}""");
            using var approved = await host.Client.SendAsync(approve);
            approved.EnsureSuccessStatusCode();
            using var apply = CsrfRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/apply");
            using var applied = await host.Client.SendAsync(apply);
            applied.EnsureSuccessStatusCode();
        }

        var privatePath = Path.Combine(temp.Path, "proposal-apply", "drafts", draftId.ToString("N") + ".json");
        WriteMigratedVersionSixPrivateDraft(temp.DatabasePath, privatePath, draftId);

        await using var restarted = await StartAsync(temp, root);
        var migratedBytes = File.ReadAllBytes(privatePath);
        var migratedWriteTime = File.GetLastWriteTimeUtc(privatePath);
        using var first = await GetReceiptAsync(restarted.Client, proposalId);
        using var second = await GetReceiptAsync(restarted.Client, proposalId);

        Assert.Equal("active", first.RootElement.GetProperty("items")[0].GetProperty("current_state").GetString());
        Assert.Equal("active", second.RootElement.GetProperty("items")[0].GetProperty("current_state").GetString());
        Assert.Equal(migratedBytes, File.ReadAllBytes(privatePath));
        Assert.Equal(migratedWriteTime, File.GetLastWriteTimeUtc(privatePath));
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
            using var activeReceipt = await GetReceiptAsync(host.Client, proposalId);
            Assert.Equal("active", activeReceipt.RootElement.GetProperty("items")[0].GetProperty("current_state").GetString());
            AssertNoReceiptSecrets(activeReceipt.RootElement.GetProperty("items")[0].GetRawText(), rootPath);
            File.WriteAllText(first, "external-edit-marker\n");
            using var staleReceipt = await GetReceiptAsync(host.Client, proposalId);
            Assert.Equal("stale", staleReceipt.RootElement.GetProperty("items")[0].GetProperty("current_state").GetString());
            Directory.Delete(rootPath, recursive: true);
            using var unavailableReceipt = await GetReceiptAsync(host.Client, proposalId);
            Assert.Equal("unavailable", unavailableReceipt.RootElement.GetProperty("items")[0].GetProperty("current_state").GetString());
            Directory.CreateDirectory(rootPath);
            File.WriteAllText(first, "ONE\n");
            File.WriteAllText(second, "two\n");
        }

        await using (var restarted = await StartAsync(temp, root))
        {
            using var rollback = CsrfRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/{applyId:D}/rollback");
            using var rolledBack = await restarted.Client.SendAsync(rollback);
            Assert.Equal(HttpStatusCode.OK, rolledBack.StatusCode);
            Assert.Equal("one\n", File.ReadAllText(first));
            Assert.Equal("two\n", File.ReadAllText(second));
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_apply_audit WHERE state='rolled_back';"));
            using var rolledBackReceipt = await GetReceiptAsync(restarted.Client, proposalId);
            Assert.Equal("rolled_back", rolledBackReceipt.RootElement.GetProperty("items")[0].GetProperty("current_state").GetString());
        }

        await using var afterRollback = await StartAsync(temp, root);
        using var secondRollback = CsrfRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/{applyId:D}/rollback");
        Assert.Equal(HttpStatusCode.NotFound, (await afterRollback.Client.SendAsync(secondRollback)).StatusCode);
    }

    [Theory]
    [InlineData("missing_private")]
    [InlineData("invalid_private")]
    [InlineData("replacement_text")]
    [InlineData("is_approved")]
    [InlineData("approval_digest")]
    [InlineData("selection_revision")]
    [InlineData("file_hash")]
    [InlineData("hunk_hash")]
    [InlineData("root_id")]
    [InlineData("proposal_id")]
    [InlineData("sqlite_draft")]
    [InlineData("sqlite_file")]
    [InlineData("sqlite_hunk")]
    public async Task Restart_rejects_tampered_draft_without_echo_or_mutation(string tamper)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        var target = Path.Combine(rootPath, "target.txt");
        const string original = "original-private-marker\n";
        const string replacement = "replacement-private-marker\n";
        File.WriteAllText(target, original);
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        var proposalId = Guid.CreateVersion7();
        Guid draftId;

        await using (var host = await StartAsync(temp, root))
        {
            InsertPersistedProposal(temp.DatabasePath, proposalId);
            var rootId = await GetSingleRootIdAsync(host.Client);
            using var create = JsonRequest(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts", $$"""{"proposal_id":"{{proposalId:D}}","root_id":"{{rootId:D}}","files":[{"relative_path":"target.txt","replacement_text":"replacement-private-marker\n"}]}""");
            using var response = await host.Client.SendAsync(create);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            draftId = document.RootElement.GetProperty("draft_id").GetGuid();
            var revision = document.RootElement.GetProperty("selection_revision").GetInt32();
            var digest = document.RootElement.GetProperty("approval_digest").GetString();
            using var approve = JsonRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/approve", $$"""{"selection_revision":{{revision}},"approval_digest":"{{digest}}"}""");
            using var approved = await host.Client.SendAsync(approve);
            Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        }

        TamperApprovedDraft(temp, draftId, tamper);

        await using var restarted = await StartAsync(temp, root);
        using var get = CsrfRequest(HttpMethod.Get, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}");
        using var getResponse = await restarted.Client.SendAsync(get);
        using var apply = CsrfRequest(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/apply");
        using var applyResponse = await restarted.Client.SendAsync(apply);
        var combined = (await getResponse.Content.ReadAsStringAsync()) + (await applyResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, applyResponse.StatusCode);
        Assert.Equal("no-store", getResponse.Headers.CacheControl?.ToString());
        Assert.Equal("no-store", applyResponse.Headers.CacheControl?.ToString());
        Assert.Equal(original, File.ReadAllText(target));
        Assert.DoesNotContain("target.txt", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(replacement.Trim(), combined, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_apply_pending;"));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_apply_audit;"));
    }

    [Theory]
    [InlineData("../escape.txt", "invalid_relative_path")]
    [InlineData("C:\\escape.txt", "invalid_relative_path")]
    [InlineData("\\\\server\\share\\escape.txt", "invalid_relative_path")]
    [InlineData("\\\\.\\PhysicalDrive0", "invalid_relative_path")]
    public async Task Draft_creation_rejects_unsafe_paths_without_echo_or_audit(string relativePath, string error)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        var proposalId = Guid.CreateVersion7();
        await using var host = await StartAsync(temp, root);
        InsertPersistedProposal(temp.DatabasePath, proposalId);
        var rootId = await GetSingleRootIdAsync(host.Client);
        const string replacement = "unsafe-replacement-marker";
        using var request = JsonRequest(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts", JsonSerializer.Serialize(new { proposal_id = proposalId, root_id = rootId, files = new[] { new { relative_path = relativePath, replacement_text = replacement } } }));
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal($"{{\"error\":\"{error}\"}}", body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(relativePath, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(replacement, body, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_apply_audit;"));
    }

    [Fact]
    public async Task Invalid_root_id_does_not_access_or_create_the_supplied_target()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        var target = "invalid-root-target-marker.txt";
        var proposalId = Guid.CreateVersion7();
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        InsertPersistedProposal(temp.DatabasePath, proposalId);
        using var request = JsonRequest(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts", $$"""{"proposal_id":"{{proposalId:D}}","root_id":"{{Guid.CreateVersion7():D}}","files":[{"relative_path":"{{target}}","replacement_text":"replacement"}]}""");
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_root_id\"}", body);
        Assert.False(File.Exists(Path.Combine(rootPath, target)));
        Assert.DoesNotContain(target, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_creation_rejects_duplicate_and_directory_targets_without_echo_or_audit()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "same.txt"), "original\n");
        Directory.CreateDirectory(Path.Combine(rootPath, "directory-marker"));
        var proposalId = Guid.CreateVersion7();
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        InsertPersistedProposal(temp.DatabasePath, proposalId);
        var rootId = await GetSingleRootIdAsync(host.Client);
        foreach (var scenario in new[]
        {
            ("duplicate_target", new[] { new { relative_path = "same.txt", replacement_text = "replacement-marker" }, new { relative_path = "SAME.TXT", replacement_text = "replacement-marker" } }),
            ("target_not_regular_file", new[] { new { relative_path = "directory-marker", replacement_text = "replacement-marker" } }),
        })
        {
            using var request = JsonRequest(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts", JsonSerializer.Serialize(new { proposal_id = proposalId, root_id = rootId, files = scenario.Item2 }));
            using var response = await host.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal($"{{\"error\":\"{scenario.Item1}\"}}", body);
            Assert.DoesNotContain("replacement-marker", body, StringComparison.Ordinal);
        }
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(rootPath, "same.txt")));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_apply_audit;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Draft_creation_rejects_reparse_target_and_ancestor_without_echo(bool ancestor)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        var externalPath = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(externalPath);
        File.WriteAllText(Path.Combine(externalPath, "linked-marker.txt"), "original\n");
        var link = ancestor ? Path.Combine(rootPath, "linked-directory") : Path.Combine(rootPath, "linked-marker.txt");
        try
        {
            if (ancestor) _ = Directory.CreateSymbolicLink(link, externalPath);
            else _ = File.CreateSymbolicLink(link, Path.Combine(externalPath, "linked-marker.txt"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Cannot create {(ancestor ? "ancestor" : "target")} reparse fixture: {exception.GetType().Name}");
        }
        var proposalId = Guid.CreateVersion7();
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        InsertPersistedProposal(temp.DatabasePath, proposalId);
        var rootId = await GetSingleRootIdAsync(host.Client);
        var relative = ancestor ? "linked-directory/linked-marker.txt" : "linked-marker.txt";
        using var request = JsonRequest(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts", JsonSerializer.Serialize(new { proposal_id = proposalId, root_id = rootId, files = new[] { new { relative_path = relative, replacement_text = "replacement-marker" } } }));
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"unsafe_reparse_path\"}", body);
        Assert.DoesNotContain(relative, body, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-marker", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("source")]
    public async Task Draft_creation_rejects_over_256_kib_content_without_echo_or_audit(string kind)
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);
        var target = Path.Combine(rootPath, "large-marker.txt");
        File.WriteAllText(target, kind == "source" ? new string('s', 262_145) : "small");
        var proposalId = Guid.CreateVersion7();
        await using var host = await StartAsync(temp, ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath));
        InsertPersistedProposal(temp.DatabasePath, proposalId);
        var rootId = await GetSingleRootIdAsync(host.Client);
        var marker = kind == "replacement" ? new string('r', 262_145) : "replacement";
        using var request = JsonRequest(HttpMethod.Post, "/api/session-workspace/proposal-applies/drafts", JsonSerializer.Serialize(new { proposal_id = proposalId, root_id = rootId, files = new[] { new { relative_path = "large-marker.txt", replacement_text = marker } } }));
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("{\"error\":\"request_too_large\"}", body);
        Assert.DoesNotContain("large-marker.txt", body, StringComparison.Ordinal);
        Assert.DoesNotContain(marker[..Math.Min(16, marker.Length)], body, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM proposal_apply_audit;"));
    }

    private static void TamperApprovedDraft(MonitorTempDirectory temp, Guid draftId, string tamper)
    {
        var privatePath = Path.Combine(temp.Path, "proposal-apply", "drafts", draftId.ToString("N") + ".json");
        if (tamper == "missing_private") { File.Delete(privatePath); return; }
        if (tamper == "invalid_private") { File.WriteAllText(privatePath, "{"); return; }
        if (tamper.StartsWith("sqlite_", StringComparison.Ordinal))
        {
            var sql = tamper switch
            {
                "sqlite_draft" => "UPDATE proposal_apply_drafts SET selection_revision=99 WHERE draft_id=$id;",
                "sqlite_file" => "UPDATE proposal_apply_files SET base_sha256='tampered-file-hash' WHERE draft_id=$id;",
                _ => "UPDATE proposal_apply_hunks SET replacement_sha256='tampered-hunk-hash' WHERE draft_id=$id;",
            };
            Execute(temp.DatabasePath, sql, draftId);
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(privatePath))!.AsObject();
        switch (tamper)
        {
            case "replacement_text": node["Files"]![0]!["ReplacementText"] = "tampered-replacement-marker\n"; break;
            case "is_approved": node["IsApproved"] = false; break;
            case "approval_digest": node["ApprovalDigest"] = "tampered-digest"; break;
            case "selection_revision": node["SelectionRevision"] = 99; break;
            case "file_hash": node["Files"]![0]!["BaseSha256"] = "tampered-file-hash"; break;
            case "hunk_hash": node["Hunks"]![0]!["ReplacementText"] = "tampered-hunk-marker\n"; break;
            case "root_id": node["RootId"] = Guid.CreateVersion7(); break;
            case "proposal_id": node["ProposalId"] = Guid.CreateVersion7(); break;
            default: throw new ArgumentOutOfRangeException(nameof(tamper));
        }
        File.WriteAllText(privatePath, node.ToJsonString());
    }

    private static void Execute(string databasePath, string sql, Guid draftId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", draftId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static void WriteMigratedVersionSixPrivateDraft(string databasePath, string privatePath, Guid draftId)
    {
        var current = JsonSerializer.Deserialize<ProposalApplyDraft>(File.ReadAllText(privatePath))!;
        var legacyDigest = LegacyDigest(current);
        var legacy = JsonNode.Parse(File.ReadAllText(privatePath))!.AsObject();
        legacy.Remove("ProposalRevision");
        legacy["ApprovalDigest"] = legacyDigest;
        File.WriteAllText(privatePath, legacy.ToJsonString());
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE proposal_apply_drafts SET approval_digest=$digest WHERE draft_id=$id; UPDATE proposal_apply_revisions SET approval_digest=$digest WHERE draft_id=$id;";
        command.Parameters.AddWithValue("$digest", legacyDigest);
        command.Parameters.AddWithValue("$id", draftId.ToString("D"));
        Assert.Equal(2, command.ExecuteNonQuery());
    }

    private static string LegacyDigest(ProposalApplyDraft draft)
    {
        var values = new[]
        {
            draft.DraftId.ToString("D"), draft.ProposalId.ToString("D"), draft.RootId.ToString("D"), draft.SelectionRevision.ToString(CultureInfo.InvariantCulture),
        }.Concat(draft.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).Select(file => $"{file.RelativePath}|{file.BaseSha256}|{file.ReplacementSha256}"))
            .Concat(draft.Hunks.Where(hunk => hunk.Selected).OrderBy(hunk => hunk.HunkId, StringComparer.Ordinal).Select(hunk => $"{hunk.HunkId}|{LineDiff.Sha256(hunk.ReplacementText)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)))).ToLowerInvariant();
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

    private static async Task<JsonDocument> GetReceiptAsync(HttpClient client, Guid proposalId)
    {
        using var request = CsrfRequest(HttpMethod.Get, $"/api/session-workspace/proposal-applies/receipts?proposal_id={proposalId:D}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertNoReceiptSecrets(string body, string rootPath)
    {
        foreach (var sentinel in new[] { rootPath, "one.txt", "two.txt", "sha256", "replacement", "snapshot", "journal", "token", "exception" })
        {
            Assert.DoesNotContain(sentinel, body, StringComparison.OrdinalIgnoreCase);
        }
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

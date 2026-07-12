using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.ProposalApply;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionEffectComparisonRouteTests
{
    [Theory]
    [InlineData("{\"proposal_id\":\"00000000-0000-4000-8000-000000000000\",\"proposal_revision\":1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[]}")]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"apply_id\":\"00000000-0000-7000-8000-000000000001\",\"sessions\":[]}")]
    public void Parser_rejects_non_v7_and_duplicate_request_fields(string body)
    {
        Assert.False(Sessions.EffectComparisonRequestParser.TryParse(System.Text.Encoding.UTF8.GetBytes(body), out _));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("pre","unexpected")]
    [InlineData("post","")]
    [InlineData("excluded","not-a-fixed-reason")]
    public void Parser_rejects_invalid_classification_case_key_and_exclusion_reason(string classification, string? exclusionReason = null)
    {
        var id = "00000000-0000-7000-8000-000000000000";
        var caseKey = classification == "excluded" ? "" : "case-1";
        var reason = exclusionReason is null ? "null" : $"\"{exclusionReason}\"";
        var body = $$"""{"proposal_id":"{{id}}","proposal_revision":1,"apply_id":"{{id}}","sessions":[{"session_id":"{{id}}","classification":"{{classification}}","case_key":"{{caseKey}}","exclusion_reason":{{reason}}}]}""";

        Assert.False(Sessions.EffectComparisonRequestParser.TryParse(Encoding.UTF8.GetBytes(body), out _));
    }

    [Fact]
    public void Parser_rejects_duplicate_sessions_before_application_linkage_is_checked()
    {
        const string id = "00000000-0000-7000-8000-000000000000";
        var body = $$"""{"proposal_id":"{{id}}","proposal_revision":1,"apply_id":"{{id}}","sessions":[{"session_id":"{{id}}","classification":"pre","case_key":"case-1","exclusion_reason":null},{"session_id":"{{id}}","classification":"post","case_key":"case-1","exclusion_reason":null}]}""";

        Assert.False(Sessions.EffectComparisonRequestParser.TryParse(Encoding.UTF8.GetBytes(body), out _));
    }

    [Fact]
    public async Task Candidate_query_rejects_invalid_identifiers_with_fixed_no_store_error()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var response = await host.Client.GetAsync("/api/session-workspace/effect-comparisons/candidates?proposal_id=not-a-uuid&apply_id=also-not-a-uuid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("{\"error\":\"invalid_comparison_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Candidate_query_rejects_unlinked_ids_and_comparison_read_maps_safe_lookup_errors()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var proposal = Guid.CreateVersion7();
        var apply = Guid.CreateVersion7();
        using var unlinked = await host.Client.GetAsync($"/api/session-workspace/effect-comparisons/candidates?proposal_id={proposal:D}&apply_id={apply:D}");
        Assert.Equal(HttpStatusCode.BadRequest, unlinked.StatusCode);
        Assert.Equal("no-store", unlinked.Headers.CacheControl?.ToString());
        Assert.Equal("{\"error\":\"application_not_active\"}", await unlinked.Content.ReadAsStringAsync());

        using var malformed = await host.Client.GetAsync("/api/session-workspace/effect-comparisons/not-an-id");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("no-store", malformed.Headers.CacheControl?.ToString());
        Assert.Equal("{\"error\":\"invalid_comparison_request\"}", await malformed.Content.ReadAsStringAsync());

        using var missing = await host.Client.GetAsync($"/api/session-workspace/effect-comparisons/{Guid.CreateVersion7():D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("no-store", missing.Headers.CacheControl?.ToString());
        Assert.Equal("{\"error\":\"comparison_not_found\"}", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_rejects_cross_origin_csrf_media_and_oversize_before_persistence()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var cases = new[]
        {
            Request("{}", origin: "http://example.test", csrf: true, media: true),
            Request("{}", origin: null, csrf: false, media: true),
            Request("{}", origin: null, csrf: true, media: false),
            Request(new string('x', 1_048_577), origin: null, csrf: true, media: true),
        };
        var expected = new[] { "cross_origin_forbidden", "csrf_required", "unsupported_media_type", "request_too_large" };
        foreach (var (request, error) in cases.Zip(expected))
        {
            using (request)
            {
                using var response = await host.Client.SendAsync(request);
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                Assert.Equal($"{{\"error\":\"{error}\"}}", await response.Content.ReadAsStringAsync());
            }
        }
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparisons;"));
    }

    [Theory]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[],\"unknown\":true}")]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":0,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[]}")]
    public async Task Post_rejects_invalid_json_without_echo(string payload)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var response = await host.Client.SendAsync(Request(payload, null, true, true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_comparison_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[]}")]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":1.1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[]}")]
    [InlineData("{\"proposal_id\":\"00000000-0000-7000-8000-000000000000\",\"proposal_revision\":1,\"apply_id\":\"00000000-0000-7000-8000-000000000000\",\"sessions\":[{\"session_id\":\"00000000-0000-7000-8000-000000000000\",\"classification\":\"pre\",\"case_key\":\"case-1\",\"exclusion_reason\":null,\"unknown\":true}]}")]
    public void Parser_requires_nonempty_integer_cohort_and_exact_nested_members(string body)
    {
        Assert.False(Sessions.EffectComparisonRequestParser.TryParse(Encoding.UTF8.GetBytes(body), out _));
    }

    [Fact]
    public async Task Post_rejects_streamed_oversize_and_invalid_cohort_before_application_or_persistence()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var streamed = new HttpRequestMessage(HttpMethod.Post, "/api/session-workspace/effect-comparisons")
        {
            Content = new StreamContent(new MemoryStream(new byte[1_048_577])),
        };
        streamed.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        streamed.Headers.TransferEncodingChunked = true;
        streamed.Content.Headers.ContentType = new("application/json");
        streamed.Content.Headers.ContentLength = null;
        using var tooLarge = await host.Client.SendAsync(streamed);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("no-store", tooLarge.Headers.CacheControl?.ToString());
        Assert.Equal("{\"error\":\"request_too_large\"}", await tooLarge.Content.ReadAsStringAsync());

        const string marker = "rejected-case-key-marker";
        var id = Guid.CreateVersion7();
        var invalid = $$"""{"proposal_id":"{{id:D}}","proposal_revision":1,"apply_id":"{{Guid.CreateVersion7():D}}","sessions":[{"session_id":"{{Guid.CreateVersion7():D}}","classification":"excluded","case_key":"{{marker}}","exclusion_reason":"wrong_case"}]}""";
        using var response = await host.Client.SendAsync(Request(invalid, null, true, true));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("{\"error\":\"invalid_comparison_request\"}", body);
        Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparisons;"));
    }

    [Fact]
    public async Task Candidate_get_reads_active_sqlite_application_and_returns_only_non_authoritative_sanitized_facts()
    {
        using var temp = new MonitorTempDirectory();
        var rootPath = Path.Combine(temp.Path, "candidate-root");
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "candidate.txt"), "before\n");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        await using var host = await StartAsync(temp, root);
        var proposalId = Guid.CreateVersion7();
        InsertRecommendedProposal(temp.DatabasePath, proposalId);
        var sessionId = await IngestTerminalSessionAsync(host.Client, "candidate-session");
        var rootId = await GetRootIdAsync(host.Client);
        using var create = Request($$"""{"proposal_id":"{{proposalId:D}}","root_id":"{{rootId:D}}","files":[{"relative_path":"candidate.txt","replacement_text":"after\n"}]}""", null, true, true, "/api/session-workspace/proposal-applies/drafts");
        using var created = await host.Client.SendAsync(create);
        created.EnsureSuccessStatusCode();
        using var draft = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = draft.RootElement.GetProperty("draft_id").GetGuid();
        var revision = draft.RootElement.GetProperty("selection_revision").GetInt32();
        var digest = draft.RootElement.GetProperty("approval_digest").GetString();
        using var approve = Request($$"""{"selection_revision":{{revision}},"approval_digest":"{{digest}}"}""", null, true, true, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/approve");
        (await host.Client.SendAsync(approve)).EnsureSuccessStatusCode();
        using var apply = new HttpRequestMessage(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/apply");
        apply.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        using var applied = await host.Client.SendAsync(apply);
        applied.EnsureSuccessStatusCode();
        using var applyJson = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
        var applyId = applyJson.RootElement.GetProperty("apply_id").GetGuid();

        using var response = await host.Client.GetAsync($"/api/session-workspace/effect-comparisons/candidates?proposal_id={proposalId:D}&apply_id={applyId:D}");
        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(sessionId, item.GetProperty("session_id").GetGuid());
        Assert.Equal("pre", item.GetProperty("boundary_eligibility").GetString());
        Assert.False(item.GetProperty("evidence_available").GetBoolean());
        Assert.Contains("missing_evidence", item.GetProperty("suggestion_reasons").EnumerateArray().Select(value => value.GetString()));
        Assert.False(item.TryGetProperty("classification", out _));
        Assert.DoesNotContain(rootPath, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate.txt", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_exact_three_by_three_efficiency_improvement_verifies_and_get_preserves_stored_evidence_after_restart()
    {
        using var temp = new MonitorTempDirectory();
        await using var fixture = await CreateComparisonFixtureAsync(temp);
        var pre = Enumerable.Range(0, 3).Select(index => InsertComparableSession(temp.DatabasePath, "pre", index, "expected", 1000)).ToArray();
        var post = Enumerable.Range(0, 3).Select(index => InsertComparableSession(temp.DatabasePath, "post", index, "expected", 900)).ToArray();

        using var created = await fixture.Host.Client.SendAsync(Request(ComparisonBody(fixture, pre, post), null, true, true));
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var receipt = JsonDocument.Parse(createdBody);
        Assert.Equal("improved", receipt.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("active", receipt.RootElement.GetProperty("verification_state").GetString());
        var comparisonId = receipt.RootElement.GetProperty("comparison_id").GetGuid();
        Assert.Equal("verified", Text(temp.DatabasePath, "SELECT status FROM improvement_proposals WHERE proposal_id=$id", fixture.ProposalId));

        using var beforeRestart = await fixture.Host.Client.GetAsync($"/api/session-workspace/effect-comparisons/{comparisonId:D}");
        var detail = await beforeRestart.Content.ReadAsStringAsync();
        Assert.Equal("no-store", beforeRestart.Headers.CacheControl?.ToString());
        Assert.Equal(6, JsonDocument.Parse(detail).RootElement.GetProperty("sessions").GetArrayLength());
        Assert.Equal(6, JsonDocument.Parse(detail).RootElement.GetProperty("evidence").GetArrayLength());
        await fixture.RestartAsync();
        using var afterRestart = await fixture.Host.Client.GetAsync($"/api/session-workspace/effect-comparisons/{comparisonId:D}");
        Assert.Equal(detail, await afterRestart.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_maps_stale_proposal_revision_to_the_canonical_fixed_error_without_a_receipt()
    {
        using var temp = new MonitorTempDirectory();
        await using var fixture = await CreateComparisonFixtureAsync(temp);
        var pre = Enumerable.Range(0, 3).Select(index => InsertComparableSession(temp.DatabasePath, "pre", index, "expected", 1000)).ToArray();
        var post = Enumerable.Range(0, 3).Select(index => InsertComparableSession(temp.DatabasePath, "post", index, "expected", 1000)).ToArray();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE improvement_proposals SET revision=2 WHERE proposal_id=$id;";
            command.Parameters.AddWithValue("$id", fixture.ProposalId.ToString("D")); command.ExecuteNonQuery();
        }

        using var response = await fixture.Host.Client.SendAsync(Request(ComparisonBody(fixture, pre, post), null, true, true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("{\"error\":\"proposal_revision_stale\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparisons;"));
    }

    [Fact]
    public async Task Post_real_sqlite_verdict_matrix_returns_fixed_verdicts_and_preserves_non_improved_maturity()
    {
        using var noChangeTemp = new MonitorTempDirectory();
        await using (var noChange = await CreateComparisonFixtureAsync(noChangeTemp))
        {
            var pre = Enumerable.Range(0, 3).Select(i => InsertComparableSession(noChangeTemp.DatabasePath, "pre", i, "expected", 1000)).ToArray();
            var post = Enumerable.Range(0, 3).Select(i => InsertComparableSession(noChangeTemp.DatabasePath, "post", i, "expected", 1000)).ToArray();
            await AssertVerdictAsync(noChange, pre, post, "no_change", "recommended");
        }

        using var regressedTemp = new MonitorTempDirectory();
        await using (var regressed = await CreateComparisonFixtureAsync(regressedTemp))
        {
            var pre = Enumerable.Range(0, 3).Select(i => InsertComparableSession(regressedTemp.DatabasePath, "pre", i, "expected", 1000)).ToArray();
            var post = Enumerable.Range(0, 3).Select(i => InsertComparableSession(regressedTemp.DatabasePath, "post", i, "problem", 900)).ToArray();
            await AssertVerdictAsync(regressed, pre, post, "regressed", "recommended");
        }

        using var severeTemp = new MonitorTempDirectory();
        await using (var severe = await CreateComparisonFixtureAsync(severeTemp))
        {
            var pre = Enumerable.Range(0, 3).Select(i => InsertComparableSession(severeTemp.DatabasePath, "pre", i, "expected", 1000)).ToArray();
            var post = Enumerable.Range(0, 3).Select(i => InsertComparableSession(severeTemp.DatabasePath, "post", i, "expected", 900)).ToArray();
            InsertSevereObjective(severeTemp.DatabasePath, post[0]);
            await AssertVerdictAsync(severe, pre, post, "regressed", "recommended");
        }

        using var insufficientTemp = new MonitorTempDirectory();
        await using (var insufficient = await CreateComparisonFixtureAsync(insufficientTemp))
        {
            var pre = Enumerable.Range(0, 2).Select(i => InsertComparableSession(insufficientTemp.DatabasePath, "pre", i, "expected", 1000)).ToArray();
            var post = Enumerable.Range(0, 3).Select(i => InsertComparableSession(insufficientTemp.DatabasePath, "post", i, "expected", 900)).ToArray();
            await AssertVerdictAsync(insufficient, pre, post, "insufficient_evidence", "recommended");
        }
    }

    private static HttpRequestMessage Request(string body, string? origin, bool csrf, bool media, string path = "/api/session-workspace/effect-comparisons")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, media ? "application/json" : "text/plain") };
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string? Text(string databasePath, string sql, Guid id)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteScalar() as string;
    }

    private static async Task<ComparisonFixture> CreateComparisonFixtureAsync(MonitorTempDirectory temp)
    {
        var rootPath = Path.Combine(temp.Path, "comparison-root");
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "comparison.txt"), "before\n");
        var root = ConfiguredApplyRoot.Create(ApplyRootKind.Repository, rootPath);
        var host = await StartAsync(temp, root);
        var proposalId = Guid.CreateVersion7();
        InsertRecommendedProposal(temp.DatabasePath, proposalId);
        var rootId = await GetRootIdAsync(host.Client);
        using var create = Request($$"""{"proposal_id":"{{proposalId:D}}","root_id":"{{rootId:D}}","files":[{"relative_path":"comparison.txt","replacement_text":"after\n"}]}""", null, true, true, "/api/session-workspace/proposal-applies/drafts");
        using var created = await host.Client.SendAsync(create);
        created.EnsureSuccessStatusCode();
        using var draft = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var draftId = draft.RootElement.GetProperty("draft_id").GetGuid();
        var revision = draft.RootElement.GetProperty("selection_revision").GetInt32();
        var digest = draft.RootElement.GetProperty("approval_digest").GetString();
        using var approve = Request($$"""{"selection_revision":{{revision}},"approval_digest":"{{digest}}"}""", null, true, true, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/approve");
        (await host.Client.SendAsync(approve)).EnsureSuccessStatusCode();
        using var apply = new HttpRequestMessage(HttpMethod.Post, $"/api/session-workspace/proposal-applies/drafts/{draftId:D}/apply");
        apply.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        using var applied = await host.Client.SendAsync(apply);
        applied.EnsureSuccessStatusCode();
        using var applyJson = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
        var applyId = applyJson.RootElement.GetProperty("apply_id").GetGuid();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE proposal_applies SET created_at='2026-01-02T00:00:00+00:00' WHERE apply_id=$id;";
            command.Parameters.AddWithValue("$id", applyId.ToString("D")); command.ExecuteNonQuery();
        }
        return new(temp, root, host, proposalId, applyId);
    }

    private static Guid InsertComparableSession(string databasePath, string side, int index, string quality, long durationMilliseconds)
    {
        var id = Guid.CreateVersion7();
        var started = side == "pre" ? DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(index) : DateTimeOffset.Parse("2026-01-03T00:00:00Z").AddMinutes(index);
        var ended = started.AddMilliseconds(durationMilliseconds);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($id,'completed','full',NULL,NULL,$started,$ended,$ended,'not_captured',$started,$ended);
            INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
            VALUES($id,'copilot-sdk',$native,'native',$started);
            INSERT INTO session_human_evaluation(session_id,verdict,recorded_at) VALUES($id,$quality,$ended);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$native", "comparison-" + id.ToString("N"));
        command.Parameters.AddWithValue("$started", started.ToString("O")); command.Parameters.AddWithValue("$ended", ended.ToString("O"));
        command.Parameters.AddWithValue("$quality", quality); command.ExecuteNonQuery();
        return id;
    }

    private static void InsertSevereObjective(string databasePath, Guid sessionId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        connection.Open(); using var command = connection.CreateCommand();
        var runId = Guid.CreateVersion7();
        command.CommandText = "INSERT INTO session_runs(run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,started_at,ended_at,input_tokens,output_tokens,total_tokens,status) VALUES($run,$session,NULL,NULL,'trace',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'completed'); INSERT INTO objective_evaluations(objective_evaluation_id,session_id,run_id,trace_id,result,severity,evaluator_id,evaluator_version,criterion_id,case_key,recorded_at) VALUES($id,$session,$run,'trace','fail','severe','eval','v1','quality','case','2026-01-03T00:00:00+00:00');";
        command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString("D")); command.Parameters.AddWithValue("$session", sessionId.ToString("D")); command.Parameters.AddWithValue("$run", runId.ToString("D")); command.ExecuteNonQuery();
    }

    private static async Task AssertVerdictAsync(ComparisonFixture fixture, IReadOnlyList<Guid> pre, IReadOnlyList<Guid> post, string verdict, string status)
    {
        using var response = await fixture.Host.Client.SendAsync(Request(ComparisonBody(fixture, pre, post), null, true, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(verdict, body.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(status, Text(fixture.DatabasePath, "SELECT status FROM improvement_proposals WHERE proposal_id=$id", fixture.ProposalId));
    }

    private static string ComparisonBody(ComparisonFixture fixture, IReadOnlyList<Guid> pre, IReadOnlyList<Guid> post) =>
        $$"""{"proposal_id":"{{fixture.ProposalId:D}}","proposal_revision":1,"apply_id":"{{fixture.ApplyId:D}}","sessions":[{{string.Join(',', pre.Select((id, index) => $$"""{"session_id":"{{id:D}}","classification":"pre","case_key":"case-{{index}}","exclusion_reason":null}"""))}},{{string.Join(',', post.Select((id, index) => $$"""{"session_id":"{{id:D}}","classification":"post","case_key":"case-{{index}}","exclusion_reason":null}"""))}}]}""";

    private sealed class ComparisonFixture(MonitorTempDirectory temp, ConfiguredApplyRoot root, RunningMonitorHost host, Guid proposalId, Guid applyId) : IAsyncDisposable
    {
        public RunningMonitorHost Host { get; private set; } = host;
        public Guid ProposalId { get; } = proposalId;
        public Guid ApplyId { get; } = applyId;
        public string DatabasePath => temp.DatabasePath;
        public async Task RestartAsync()
        {
            await Host.DisposeAsync();
            Host = await StartAsync(temp, root);
        }
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<RunningMonitorHost> StartAsync(MonitorTempDirectory temp, ConfiguredApplyRoot root)
    {
        var app = MonitorHost.Build(new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes, ApplyRoots: [root]),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        await app.StartAsync();
        var address = app.Urls.Single();
        return new RunningMonitorHost(app, new HttpClient { BaseAddress = new Uri(address) }, address);
    }

    private static void InsertRecommendedProposal(string databasePath, Guid proposalId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO improvement_proposals(proposal_id,status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at,recommended_at) VALUES($id,'recommended','skill','fixture','fixture','fixture','fixture','fixture','2026-07-12T00:00:00+00:00','2026-07-12T00:00:00+00:00','2026-07-12T00:00:00+00:00');";
        command.Parameters.AddWithValue("$id", proposalId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static async Task<Guid> GetRootIdAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session-workspace/proposal-applies/roots");
        request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items")[0].GetProperty("root_id").GetGuid();
    }

    private static async Task<Guid> IngestTerminalSessionAsync(HttpClient client, string nativeSessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
        {
            Content = new StringContent("{\"schema_version\":1,\"source_adapter\":\"copilot-compatible-hook\",\"source_surface\":\"hook-unknown\",\"native_session_id\":\""
                + nativeSessionId + "\",\"events\":[{\"source_event_id\":\"" + nativeSessionId + "-event\",\"type\":\"Stop\",\"occurred_at\":\"2026-07-11T00:00:00Z\",\"payload\":{}}]}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CAO-Session-Event-Version", "1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var sessions = await client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
            var item = sessions?.RootElement.GetProperty("items").EnumerateArray().SingleOrDefault();
            if (item is { ValueKind: JsonValueKind.Object } persisted) return persisted.GetProperty("session_id").GetGuid();
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("The local session writer did not persist the candidate fixture.");
    }
}

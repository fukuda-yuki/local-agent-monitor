using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

[Collection(PlaywrightBrowserPathCollection.Name)]
public sealed class RetentionMutationUiPlaywrightTests
{
    [Fact(Timeout = 60_000)]
    public async Task SessionPin_ReissuedConfirmationUsesOnlyFreshTokenAndShowsAuthoritativeResult()
    {
        await using var fixture = await RetentionUiFixture.CreateAsync();
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var requests = new List<(string Url, string? Body)>();
        var retentionPosts = new List<IRequest>();
        page.Request += (_, request) =>
        {
            requests.Add((request.Url, request.PostData));
            if (request.Method == "POST" && request.Url.Contains("/api/retention/v1/", StringComparison.Ordinal))
                retentionPosts.Add(request);
        };

        await page.GotoAsync($"{fixture.Host.Url}/retention/session/{fixture.SessionId}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });
        await Expect(page.Locator("#retention-dialog")).ToBeVisibleAsync();
        await Expect(page.Locator("#retention-dialog-title")).ToBeFocusedAsync();
        Assert.False(
            requests.Any(request => request.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal)),
            "The browser issued a preview before explicit user input.");

        await page.GetByLabel("ピン留め", new PageGetByLabelOptions { Exact = true }).CheckAsync();
        await page.GetByLabel("理由").SelectOptionAsync("research_needed");
        var previewResponseTask = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "影響を確認" }).ClickAsync();
        var previewResponse = await previewResponseTask;
        var workflowKey = await previewResponse.Request.HeaderValueAsync("Idempotency-Key");
        Assert.NotNull(workflowKey);
        using var previewJson = JsonDocument.Parse(await previewResponse.TextAsync());
        var previewId = previewJson.RootElement.GetProperty("preview_id").GetString()!;
        var previewDigest = previewJson.RootElement.GetProperty("preview_digest").GetString()!;

        await Expect(page.Locator("#retention-preview-surface")).ToBeVisibleAsync();
        await Expect(page.Locator("#retention-preview-content")).ToContainTextAsync("retention_backup_not_purged");
        await Expect(page.Locator("#retention-preview-content")).ToContainTextAsync("期待状態バージョン");
        await Expect(page.Locator("#retention-preview-content")).ToContainTextAsync("preview digest");
        await Expect(page.Locator("#retention-preview-content")).ToContainTextAsync("確認期限（5 分）");

        var oldToken = await fixture.IssueConfirmationAsync(workflowKey!, previewId, previewDigest);
        var confirmationResponseTask = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "この内容で確定" }).ClickAsync();
        var confirmationResponse = await confirmationResponseTask;
        using var confirmationJson = JsonDocument.Parse(await confirmationResponse.TextAsync());
        var freshToken = confirmationJson.RootElement.GetProperty("confirmation_token").GetString()!;
        Assert.False(string.Equals(oldToken, freshToken, StringComparison.Ordinal), "Confirmation reissue returned the prior token.");

        await Expect(page.Locator("#retention-result")).ToBeVisibleAsync();
        await Expect(page.Locator("#retention-operation-status")).ToHaveTextAsync("committed");
        await Expect(page.Locator("#retention-result-content")).ToContainTextAsync("retention_pin_applied");
        await Expect(page.Locator("#retention-result-content")).ToContainTextAsync("retention_backup_not_purged");

        var mutationBodies = requests
            .Where(request => request.Url.EndsWith("/api/retention/v1/mutations", StringComparison.Ordinal))
            .Select(request => request.Body ?? string.Empty)
            .ToArray();
        Assert.True(mutationBodies.Length == 1, "The browser did not issue exactly one mutation request.");
        var mutationBody = mutationBodies[0];
        Assert.False(mutationBody.Contains(oldToken, StringComparison.Ordinal), "The mutation request used the invalidated token.");
        Assert.True(mutationBody.Contains(freshToken, StringComparison.Ordinal), "The mutation request omitted the fresh token.");
        Assert.False(requests.Any(request => request.Url.Contains(oldToken, StringComparison.Ordinal)
            || request.Url.Contains(freshToken, StringComparison.Ordinal)), "A confirmation token appeared in a request URL.");
        Assert.Equal(3, retentionPosts.Count);
        foreach (var retentionPost in retentionPosts)
            Assert.Equal(workflowKey, await retentionPost.HeaderValueAsync("Idempotency-Key"));
        await AssertValuesAbsentFromClientStateAsync(page, "confirmation token", oldToken, freshToken);
        await AssertValuesAbsentFromClientStateAsync(page, "raw/path marker", "RAW_SECRET_MARKER", "PATH_SECRET_MARKER");
    }

    [Fact(Timeout = 60_000)]
    public async Task ItemDeleteNow_FromDiagnosticsRequiresFinalKeyboardConfirmationAndDeniesReadsImmediately()
    {
        await using var fixture = await RetentionUiFixture.CreateAsync();
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var requests = new List<(string Url, string? Body)>();
        page.Request += (_, request) => requests.Add((request.Url, request.PostData));

        await page.GotoAsync($"{fixture.Host.Url}/diagnostics", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var itemLink = page.Locator("#retention-diagnostics-items a", new() { HasTextString = fixture.ItemId });
        await Expect(itemLink).ToBeVisibleAsync();
        await itemLink.ClickAsync();
        await page.WaitForURLAsync($"{fixture.Host.Url}/retention/item/{fixture.ItemId}");

        var dialog = page.Locator("#retention-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog).ToHaveAttributeAsync("aria-labelledby", "retention-dialog-title");
        await Expect(dialog).ToHaveAttributeAsync("aria-describedby", "retention-dialog-description");
        await Expect(page.GetByLabel("理由")).ToHaveAttributeAsync("aria-describedby", "retention-reason-help");
        await Expect(page.GetByLabel("コメント（任意）")).ToHaveAttributeAsync("aria-describedby", "retention-comment-help");
        await Expect(page.Locator("#retention-live")).ToHaveAttributeAsync("aria-live", "polite");
        await Expect(page.Locator(".retention-operation-destructive .retention-warning"))
            .ToContainTextAsync("今すぐ削除は読み取りを直ちに拒否し、物理削除をキューへ渡します。");
        await Expect(page.GetByLabel("今すぐ削除", new() { Exact = true })).Not.ToBeFocusedAsync();
        await Expect(page.Locator("input[name='retention-operation']:checked")).ToHaveCountAsync(0);

        await page.Keyboard.PressAsync("Enter");
        Assert.False(
            requests.Any(request => request.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal)
                || request.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal)
                || request.Url.EndsWith("/api/retention/v1/mutations", StringComparison.Ordinal)),
            "The browser issued a mutation workflow request before explicit user input.");
        await page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(page.Locator("#retention-manage-trigger")).ToBeFocusedAsync();
        await page.Locator("#retention-manage-trigger").ClickAsync();
        await Expect(page.Locator("#retention-dialog-title")).ToBeFocusedAsync();

        await page.GetByLabel("今すぐ削除", new() { Exact = true }).CheckAsync();
        await page.GetByLabel("理由").SelectOptionAsync("test_cleanup");
        await page.GetByRole(AriaRole.Button, new() { Name = "影響を確認" }).ClickAsync();
        await Expect(page.Locator("#retention-preview-surface")).ToBeVisibleAsync();
        var previewContent = page.Locator("#retention-preview-content");
        foreach (var requiredPreviewText in new[]
        {
            fixture.ItemId, "delete_now", "single_item", "正確な項目数", "現在のライフサイクル・ピン・削除状態",
            "保存先種別の内訳", "ピン解除を含む正確な削除対象", "取得・有効期限・ポリシーの原状態",
            "保持されるメタデータ・証拠への影響", "除外と進行中 cleanup の競合", "retention_backup_not_purged",
            "期待状態バージョン", "対象集合 digest", "preview digest", "確認期限（5 分）",
        })
            await Expect(previewContent).ToContainTextAsync(requiredPreviewText);
        await Expect(page.Locator("#retention-preview-title")).ToBeFocusedAsync();

        var confirm = page.GetByRole(AriaRole.Button, new() { Name = "この内容で確定" });
        await confirm.FocusAsync();
        var confirmationResponseTask = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal));
        await page.Keyboard.PressAsync("Enter");
        var confirmationResponse = await confirmationResponseTask;
        using var confirmationJson = JsonDocument.Parse(await confirmationResponse.TextAsync());
        var token = confirmationJson.RootElement.GetProperty("confirmation_token").GetString()!;

        await Expect(page.Locator("#retention-result")).ToBeVisibleAsync();
        await Expect(page.Locator("#retention-result-content")).ToContainTextAsync("retention_delete_queued");
        await Expect(page.Locator("#retention-result-content")).ToContainTextAsync("読み取り拒否はい");
        await Expect(page.Locator("#retention-result-content")).ToContainTextAsync("deletion_queued");
        await Expect(page.Locator("#retention-result-content")).ToContainTextAsync("削除日時—");
        await Expect(page.Locator("#retention-live")).ToContainTextAsync("物理削除の完了は #89 worker 状態で確認してください");

        var mutationBodies = requests
            .Where(request => request.Url.EndsWith("/api/retention/v1/mutations", StringComparison.Ordinal))
            .Select(request => request.Body ?? string.Empty)
            .ToArray();
        Assert.True(mutationBodies.Length == 1, "The browser did not issue exactly one mutation request.");
        var mutationBody = mutationBodies[0];
        Assert.True(mutationBody.Contains(token, StringComparison.Ordinal), "The mutation request omitted the issued token.");
        Assert.False(requests.Any(request => request.Url.Contains(token, StringComparison.Ordinal)), "A confirmation token appeared in a request URL.");
        await AssertValuesAbsentFromClientStateAsync(page, "confirmation token", token);
        await AssertValuesAbsentFromClientStateAsync(page, "raw/path marker", "RAW_SECRET_MARKER", "PATH_SECRET_MARKER");
        var content = await page.ContentAsync();
        Assert.False(content.Contains("物理削除が完了", StringComparison.Ordinal), "The UI claimed physical deletion completion.");

        var confirmationCount = requests.Count(request => request.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal));
        await page.GetByLabel("ピン留め", new PageGetByLabelOptions { Exact = true }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "影響を確認" }).ClickAsync();
        await Expect(page.Locator("#retention-preview-content")).ToContainTextAsync("retention_pin_read_denied");
        await Expect(confirm).ToBeDisabledAsync();
        Assert.Equal(confirmationCount, requests.Count(request => request.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal)));
    }

    [Fact(Timeout = 60_000)]
    public async Task ConsumedConfirmation_FollowsRelativeOperationLocationWithoutReissueOrMutation()
    {
        await using var fixture = await RetentionUiFixture.CreateAsync();
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var requests = new List<(string Method, string Url)>();
        page.Request += (_, request) => requests.Add((request.Method, request.Url));

        await page.GotoAsync($"{fixture.Host.Url}/retention/session/{fixture.SessionId}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });
        await page.GetByLabel("ピン留め", new PageGetByLabelOptions { Exact = true }).CheckAsync();
        await page.GetByLabel("理由").SelectOptionAsync("review_complete");
        var previewResponseTask = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "影響を確認" }).ClickAsync();
        var previewResponse = await previewResponseTask;
        var workflowKey = (await previewResponse.Request.HeaderValueAsync("Idempotency-Key"))!;
        using var previewJson = JsonDocument.Parse(await previewResponse.TextAsync());
        var previewId = previewJson.RootElement.GetProperty("preview_id").GetString()!;
        var previewDigest = previewJson.RootElement.GetProperty("preview_digest").GetString()!;
        var token = await fixture.IssueConfirmationAsync(workflowKey, previewId, previewDigest);
        var operationId = await fixture.MutateSessionAsync(workflowKey, token, "pin");

        var beforeConfirmation = requests.Count(request => request.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal));
        var beforeMutation = requests.Count(request => request.Url.EndsWith("/api/retention/v1/mutations", StringComparison.Ordinal));
        var beforePreview = requests.Count(request => request.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal));
        var consumedTask = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "この内容で確定" }).ClickAsync();
        var consumed = await consumedTask;
        Assert.Equal(409, consumed.Status);
        Assert.True(
            string.Equals($"/api/retention/v1/mutations/{operationId}", await consumed.HeaderValueAsync("Location"), StringComparison.Ordinal),
            "Consumed confirmation returned an invalid operation-status location.");

        await Expect(page.Locator("#retention-result")).ToBeVisibleAsync();
        await Expect(page.Locator("#retention-operation-status")).ToHaveTextAsync("committed");
        await Expect(page.Locator("#retention-result-content")).ToContainTextAsync(operationId);
        Assert.Equal(beforeConfirmation + 1, requests.Count(request => request.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal)));
        Assert.Equal(beforeMutation, requests.Count(request => request.Url.EndsWith("/api/retention/v1/mutations", StringComparison.Ordinal)));
        Assert.Equal(beforePreview, requests.Count(request => request.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal)));
        Assert.True(
            requests.Count(request => request.Method == "GET"
                && request.Url.EndsWith($"/api/retention/v1/mutations/{operationId}", StringComparison.Ordinal)) == 1,
            "The browser did not follow the consumed operation status exactly once.");
        await AssertValuesAbsentFromClientStateAsync(page, "confirmation token", token);
        Assert.False(requests.Any(request => request.Url.Contains(token, StringComparison.Ordinal)), "A confirmation token appeared in a request URL.");
    }

    [Fact(Timeout = 60_000)]
    public async Task ConfirmationFailure_PerformsOnlyOneStatusAndFreshPreviewRecoveryWithoutMutationLoop()
    {
        await using var fixture = await RetentionUiFixture.CreateAsync();
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var requests = new List<(string Method, string Url)>();
        page.Request += (_, request) => requests.Add((request.Method, request.Url));
        await page.RouteAsync("**/api/retention/v1/confirmations", async route =>
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 503,
                ContentType = "application/json",
                Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                Body = "{\"error\":\"retention_catalog_unavailable\"}",
            }));

        await page.GotoAsync($"{fixture.Host.Url}/retention/session/{fixture.SessionId}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });
        await page.GetByLabel("ピン留め", new PageGetByLabelOptions { Exact = true }).CheckAsync();
        await page.GetByLabel("理由").SelectOptionAsync("research_needed");
        await page.GetByRole(AriaRole.Button, new() { Name = "影響を確認" }).ClickAsync();
        await Expect(page.Locator("#retention-preview-surface")).ToBeVisibleAsync();

        var recoveryPreviewTask = page.WaitForResponseAsync(response =>
            response.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal)
            && requests.Count(request => request.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal)) == 2);
        await page.GetByRole(AriaRole.Button, new() { Name = "この内容で確定" }).ClickAsync();
        await recoveryPreviewTask;
        await Expect(page.Locator("#retention-error")).ToBeVisibleAsync();
        await Expect(page.Locator("#retention-error")).ToContainTextAsync("retention_catalog_unavailable");
        await Expect(page.Locator("#retention-live")).ToContainTextAsync("最終確定してください");

        Assert.Equal(2, requests.Count(request => request.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal)));
        Assert.True(
            requests.Count(request => request.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal)) == 1,
            "The browser did not issue exactly one confirmation request.");
        Assert.False(
            requests.Any(request => request.Url.EndsWith("/api/retention/v1/mutations", StringComparison.Ordinal)),
            "The browser issued a mutation after confirmation failure.");
        Assert.Equal(2, requests.Count(request => request.Method == "GET"
            && request.Url.EndsWith($"/api/retention/v1/sessions/{fixture.SessionId}", StringComparison.Ordinal)));
        Assert.False(
            (await page.ContentAsync()).Contains(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, StringComparison.Ordinal),
            "Plaintext confirmation material reached the rendered page.");
    }

    [Fact(Timeout = 60_000)]
    public async Task EmptySessionPreview_UsesCanonicalServerProjectionAndCannotIssueConfirmation()
    {
        await using var fixture = await RetentionUiFixture.CreateAsync(itemCount: 0);
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        var confirmationCalls = 0;
        page.Request += (_, request) =>
        {
            if (request.Url.EndsWith("/api/retention/v1/confirmations", StringComparison.Ordinal)) confirmationCalls += 1;
        };

        await page.GotoAsync($"{fixture.Host.Url}/retention/session/{fixture.SessionId}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });
        await page.GetByLabel("ピン留め", new PageGetByLabelOptions { Exact = true }).CheckAsync();
        await page.GetByLabel("理由").SelectOptionAsync("research_needed");
        await page.GetByRole(AriaRole.Button, new() { Name = "影響を確認" }).ClickAsync();

        await Expect(page.Locator("#retention-preview-content")).ToContainTextAsync("empty_not_applicable");
        await Expect(page.Locator("#retention-preview-content")).ToContainTextAsync("no_exact_owned_items");
        await Expect(page.Locator("#retention-preview-content")).ToContainTextAsync("正確な項目数0");
        await Expect(page.Locator("#retention-confirm")).ToBeDisabledAsync();
        Assert.Equal(0, confirmationCalls);
    }

    [Fact(Timeout = 120_000)]
    public async Task HttpErrorClasses_RenderFirstCodeAndNeverRetryConfirmationOrMutation()
    {
        await using var fixture = await RetentionUiFixture.CreateAsync();
        var actionablePreview = await fixture.CreatePreviewBodyAsync();
        PlaywrightBrowserPath.ConfigureDefault();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        var scenarios = new[]
        {
            new UiErrorScenario(UiErrorStage.PreviewRequest, 400, "retention_mutation_request_invalid"),
            new UiErrorScenario(UiErrorStage.PreviewRequest, 404, "retention_target_not_found"),
            new UiErrorScenario(UiErrorStage.PreviewRequest, 413, "retention_mutation_target_limit_exceeded"),
            new UiErrorScenario(UiErrorStage.PreviewRequest, 503, "retention_catalog_unavailable"),
            new UiErrorScenario(UiErrorStage.Confirmation, 409, "retention_preview_expired"),
            new UiErrorScenario(UiErrorStage.Confirmation, 503, "retention_confirmation_generation_failed"),
            new UiErrorScenario(UiErrorStage.Mutation, 401, "retention_confirmation_invalid"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_idempotency_conflict"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_confirmation_expired"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_confirmation_binding_mismatch"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_confirmation_target_changed"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_confirmation_pin_changed"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_confirmation_retention_changed"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_confirmation_conflict_changed"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_confirmation_version_changed"),
            new UiErrorScenario(UiErrorStage.Mutation, 409, "retention_pin_expired"),
            new UiErrorScenario(UiErrorStage.Mutation, 503, "retention_mutation_transaction_failed"),
            new UiErrorScenario(UiErrorStage.Mutation, 503, "retention_audit_write_failed"),
            new UiErrorScenario(UiErrorStage.Mutation, 503, "retention_catalog_unavailable"),
            new UiErrorScenario(UiErrorStage.MutationConsumed, 409, "retention_confirmation_consumed"),
        };

        foreach (var scenario in scenarios)
        {
            var page = await browser.NewPageAsync();
            page.SetDefaultTimeout(5_000);
            var previewCalls = 0;
            var confirmationCalls = 0;
            var mutationCalls = 0;
            var targetStatusCalls = 0;
            var consumedStatusCalls = 0;
            var token = RetentionMutationToken.Generate();
            var consumedOperationId = $"consumed-{Guid.NewGuid():N}";
            page.Request += (_, request) =>
            {
                if (request.Method == "GET"
                    && request.Url.EndsWith($"/api/retention/v1/sessions/{fixture.SessionId}", StringComparison.Ordinal))
                    targetStatusCalls += 1;
            };

            await page.RouteAsync("**/api/retention/v1/previews", async route =>
            {
                previewCalls += 1;
                if (scenario.Stage == UiErrorStage.PreviewRequest && previewCalls == 1)
                    await FulfillErrorAsync(route, scenario.Status, scenario.Code);
                else
                    await FulfillJsonAsync(route, 200, actionablePreview);
            });
            await page.RouteAsync("**/api/retention/v1/confirmations", async route =>
            {
                confirmationCalls += 1;
                if (scenario.Stage == UiErrorStage.Confirmation)
                    await FulfillErrorAsync(route, scenario.Status, scenario.Code);
                else
                    await FulfillJsonAsync(route, 200, JsonSerializer.Serialize(new
                    {
                        schema_version = 1,
                        confirmation_id = RetentionMutationIdentifiers.GenerateConfirmationId(),
                        confirmation_token = token,
                        confirmation_expires_at = "2026-07-20T12:05:00.0000000+00:00",
                    }));
            });
            await page.RouteAsync("**/api/retention/v1/mutations", async route =>
            {
                mutationCalls += 1;
                if (scenario.Stage == UiErrorStage.MutationConsumed)
                {
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = 409,
                        ContentType = "application/json",
                        Headers = new Dictionary<string, string>
                        {
                            ["Cache-Control"] = "no-store",
                            ["Location"] = $"/api/retention/v1/mutations/{consumedOperationId}",
                        },
                        Body = "{\"error\":\"retention_confirmation_consumed\"}",
                    });
                }
                else
                    await FulfillErrorAsync(route, scenario.Status, scenario.Code);
            });
            await page.RouteAsync($"**/api/retention/v1/mutations/{consumedOperationId}", async route =>
            {
                consumedStatusCalls += 1;
                await FulfillJsonAsync(route, 200, MutationStatusJson(consumedOperationId, fixture.SessionId));
            });

            await page.GotoAsync($"{fixture.Host.Url}/retention/session/{fixture.SessionId}", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
            });
            await page.GetByLabel("ピン留め", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await page.GetByLabel("理由").SelectOptionAsync("research_needed");
            Task<IResponse>? recoveryPreview = scenario.Stage == UiErrorStage.PreviewRequest
                ? page.WaitForResponseAsync(response => response.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal) && previewCalls == 2)
                : null;
            await page.GetByRole(AriaRole.Button, new() { Name = "影響を確認" }).ClickAsync();

            if (scenario.Stage == UiErrorStage.PreviewRequest)
            {
                await recoveryPreview!;
                await Expect(page.Locator("#retention-error")).ToContainTextAsync(scenario.Code);
                Assert.Equal(2, previewCalls);
            }
            else
            {
                await Expect(page.Locator("#retention-preview-surface")).ToBeVisibleAsync();
                if (scenario.Stage != UiErrorStage.MutationConsumed)
                    recoveryPreview = page.WaitForResponseAsync(response => response.Url.EndsWith("/api/retention/v1/previews", StringComparison.Ordinal) && previewCalls == 2);
                await page.GetByRole(AriaRole.Button, new() { Name = "この内容で確定" }).ClickAsync();
                if (scenario.Stage == UiErrorStage.MutationConsumed)
                {
                    await Expect(page.Locator("#retention-result")).ToBeVisibleAsync();
                    await Expect(page.Locator("#retention-operation-status")).ToHaveTextAsync("committed");
                    Assert.Equal(1, previewCalls);
                }
                else
                {
                    await recoveryPreview!;
                    await Expect(page.Locator("#retention-error")).ToContainTextAsync(scenario.Code);
                    Assert.Equal(2, previewCalls);
                }
            }

            var recoveryExpected = scenario.Stage is UiErrorStage.PreviewRequest or UiErrorStage.Confirmation or UiErrorStage.Mutation;
            var expectedTargetStatusCalls = scenario.Stage == UiErrorStage.MutationConsumed ? 2 : recoveryExpected ? 2 : 1;
            Assert.True(targetStatusCalls == expectedTargetStatusCalls, $"Unexpected target status call count for {scenario.Stage}:{scenario.Code}.");
            Assert.True(confirmationCalls == (scenario.Stage is UiErrorStage.Confirmation or UiErrorStage.Mutation or UiErrorStage.MutationConsumed ? 1 : 0),
                $"Unexpected confirmation call count for {scenario.Stage}:{scenario.Code}.");
            Assert.True(mutationCalls == (scenario.Stage is UiErrorStage.Mutation or UiErrorStage.MutationConsumed ? 1 : 0),
                $"Unexpected mutation call count for {scenario.Stage}:{scenario.Code}.");
            Assert.True(consumedStatusCalls == (scenario.Stage == UiErrorStage.MutationConsumed ? 1 : 0),
                $"Unexpected consumed status call count for {scenario.Stage}:{scenario.Code}.");
            await AssertValuesAbsentFromClientStateAsync(page, "confirmation token", token);
            Assert.True(
                string.Equals("{}|{}", await page.EvaluateAsync<string>("() => JSON.stringify(localStorage) + '|' + JSON.stringify(sessionStorage)"), StringComparison.Ordinal),
                "Retention state reached browser storage.");
            await page.CloseAsync();
        }
    }

    private static Task FulfillErrorAsync(IRoute route, int status, string code) =>
        FulfillJsonAsync(route, status, JsonSerializer.Serialize(new { error = code }));

    private static Task FulfillJsonAsync(IRoute route, int status, string body) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            Body = body,
        });

    private static string MutationStatusJson(string operationId, string sessionId) => JsonSerializer.Serialize(new
    {
        schema_version = 1,
        operation_id = operationId,
        operation = "pin",
        target_kind = "session",
        target_id = sessionId,
        status = "committed",
        result_code = "retention_pin_applied",
        lifecycle_counts = new { expiring = 0, retained_by_policy = 1, expired_pending_deletion = 0, deletion_queued = 0, deleting = 0, deleted = 0, deletion_failed = 0 },
        read_denied = false,
        audit_event_id = RetentionMutationIdentifiers.GenerateAuditEventId(),
        idempotent_replay = false,
        created_at = "2026-07-20T12:00:00.0000000+00:00",
        completed_at = "2026-07-20T12:00:00.0000000+00:00",
        backup_non_purge_warning_code = "retention_backup_not_purged",
    });

    private static async Task AssertValuesAbsentFromClientStateAsync(IPage page, string valueKind, params string[] values)
    {
        var clientState = await page.EvaluateAsync<string>("""
            () => JSON.stringify({
                url: location.href,
                html: document.documentElement.outerHTML,
                local: Object.fromEntries(Object.entries(localStorage)),
                session: Object.fromEntries(Object.entries(sessionStorage))
            })
            """);
        foreach (var value in values)
            Assert.False(clientState.Contains(value, StringComparison.Ordinal), $"Client state contained a {valueKind}.");
    }

    private sealed class RetentionUiFixture : IAsyncDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        private RetentionUiFixture(MonitorTempDirectory temp, RunningMonitorHost host, string sessionId, string itemId)
            => (Temp, Host, SessionId, ItemId) = (temp, host, sessionId, itemId);

        internal MonitorTempDirectory Temp { get; }
        internal RunningMonitorHost Host { get; }
        internal string SessionId { get; }
        internal string ItemId { get; }

        internal static async Task<RetentionUiFixture> CreateAsync(int itemCount = 1)
        {
            var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(Now) };
            try
            {
                var sessionId = SeedSession(temp, itemCount);
                var itemId = itemCount == 0 ? "no-item" : ReadItemId(temp);
                var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
                {
                    StartWriter = false,
                    StartProjectionWorker = false,
                    StartSessionWriter = false,
                    StartSessionOtelEnrichment = false,
                    StartRetentionCleanupWorker = false,
                    UseUserSecrets = false,
                });
                return new(temp, host, sessionId, itemId);
            }
            catch
            {
                temp.Dispose();
                throw;
            }
        }

        internal async Task<string> IssueConfirmationAsync(string workflowKey, string previewId, string previewDigest)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/retention/v1/confirmations")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    preview_id = previewId,
                    preview_digest = previewDigest,
                }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", workflowKey);
            request.Headers.Add("x-monitor-csrf", "local-monitor");
            using var response = await Host.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"confirmation issue failed with {(int)response.StatusCode}");
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("confirmation_token").GetString()!;
        }

        internal async Task<string> MutateSessionAsync(string workflowKey, string token, string operation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/retention/v1/mutations")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    confirmation_token = token,
                    operation,
                    scope = "session_items",
                    target_kind = "session",
                    target_id = SessionId,
                }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", workflowKey);
            request.Headers.Add("x-monitor-csrf", "local-monitor");
            using var response = await Host.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"mutation failed with {(int)response.StatusCode}");
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("operation_id").GetString()!;
        }

        internal async Task<string> CreatePreviewBodyAsync()
        {
            var workflowKey = RetentionMutationIdentifiers.GenerateWorkflowKey();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/retention/v1/previews")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    target = new { kind = "session", id = SessionId },
                    operation = "pin",
                    scope = "session_items",
                    reason_code = "research_needed",
                    comment = (string?)null,
                }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", workflowKey);
            request.Headers.Add("x-monitor-csrf", "local-monitor");
            using var response = await Host.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"preview failed with {(int)response.StatusCode}");
            return body;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Temp.Dispose();
        }

        private static string SeedSession(MonitorTempDirectory temp, int itemCount)
        {
            var time = (MutableTimeProvider)temp.TimeProvider;
            var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
            var sessionStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, time);
            sessionStore.CreateSchema();
            var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
                time.GetUtcNow().AddMinutes(-1), time.GetUtcNow(), time.GetUtcNow(), SessionRawRetentionState.Expiring,
                time.GetUtcNow().AddMinutes(-1), time.GetUtcNow());
            var events = Enumerable.Range(0, itemCount).Select(index => new ObservedSessionEvent(
                Guid.Parse($"018f2b4e-7c1a-7f1a-8a2b-{index + 0x6072:X12}"), sessionId, null,
                SessionSourceSurface.CopilotSdk, null, $"trace-retention-ui-{index}", "received", "copilot-sdk-stream",
                $"event-retention-ui-{index}", "user.message", time.GetUtcNow().AddSeconds(index), SessionContentState.Available)).ToArray();
            var content = events.Select(observedEvent => new SessionEventContent(observedEvent.EventId, "application/json",
                "{\"raw\":\"RAW_SECRET_MARKER\",\"path\":\"PATH_SECRET_MARKER\"}",
                time.GetUtcNow(), time.GetUtcNow().AddDays(90))).ToArray();
            sessionStore.Write(new(new(session, [], [], events), content));
            return sessionId.ToString("D");
        }

        private static string ReadItemId(MonitorTempDirectory temp)
        {
            using var connection = new SqliteConnection($"Data Source={temp.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_id FROM retention_items WHERE store_kind='session_event_content';";
            return (string)command.ExecuteScalar()!;
        }
    }

    private enum UiErrorStage { PreviewRequest, Confirmation, Mutation, MutationConsumed }

    private sealed record UiErrorScenario(UiErrorStage Stage, int Status, string Code);
}

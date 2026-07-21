using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(CanvasExtensionNodeCollection.Name)]
public class CanvasExtensionContractTests
{
    [Fact]
    public void ExtensionDistributionPackage_DeclaresStableMetadataAndPreview()
    {
        var directory = ExtensionDirectory();
        var manifestPath = Path.Combine(directory, "canvas.json");

        Assert.True(File.Exists(manifestPath), "canvas.json must exist in the copyable extension folder.");

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("otel-monitor-canvas", root.GetProperty("id").GetString());
        Assert.Equal("otel-monitor", root.GetProperty("canvas_id").GetString());
        Assert.Equal("OTel Monitor", root.GetProperty("display_name").GetString());
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.Equal("extension.mjs", root.GetProperty("entry").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));
        Assert.True(root.GetProperty("keywords").GetArrayLength() >= 3);

        var screenshots = root.GetProperty("screenshots");
        Assert.Equal(JsonValueKind.Array, screenshots.ValueKind);
        Assert.Single(screenshots.EnumerateArray());
        var screenshot = screenshots[0];
        Assert.Equal("assets/preview.png", screenshot.GetProperty("path").GetString());
        Assert.Equal("Synthetic OTel Monitor Canvas helper preview", screenshot.GetProperty("alt").GetString());

        AssertPathResolvesInside(directory, root.GetProperty("entry").GetString()!);
        var previewPath = AssertPathResolvesInside(directory, screenshot.GetProperty("path").GetString()!);
        Assert.True(File.Exists(previewPath), "assets/preview.png must exist.");

        var previewBytes = File.ReadAllBytes(previewPath);
        Assert.True(previewBytes.Length > 8, "preview.png must not be empty.");
        Assert.True(previewBytes.Length <= 500_000, "preview.png must stay small enough to remain a lightweight repository asset.");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, previewBytes.Take(8).ToArray());
    }

    [Fact]
    public void ExtensionDistributionPackage_DoesNotAddDependenciesMirrorsOrUnsafeArtifacts()
    {
        var directory = ExtensionDirectory();
        var forbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "package.json",
            "package-lock.json",
            "npm-shrinkwrap.json",
            "pnpm-lock.yaml",
            "yarn.lock",
            "bun.lockb",
            "node_modules",
        };

        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            Assert.DoesNotContain(name, forbiddenNames);
        }

        var extensionEntrypoints = Directory.EnumerateFiles(directory, "extension.mjs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .ToArray();
        Assert.Equal(new[] { "extension.mjs" }, extensionEntrypoints);

        var rawFixtureFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .Where(path => path.Contains("raw", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("extension.mjs", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("canvas-helpers.mjs", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("canvas-helpers.test.mjs", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(rawFixtureFiles);

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (IsBinaryFile(path))
            {
                AssertBinaryDoesNotContainUnsafeText(path, relative);
                continue;
            }

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("console.log", text);
            Assert.DoesNotMatch(@"(?i)(api[_-]?key|secret|password|credential|bearer)\s*[:=]\s*[""'][^""']+[""']", text);
            Assert.DoesNotMatch(@"(?i)(C:\\Users\\|/Users/|/home/)", text);
            Assert.DoesNotMatch(@"(?i)authorization:\s*basic\s+[A-Za-z0-9+/=]{12,}", text);
            Assert.DoesNotMatch(@"(?i)authorization:\s*bearer\s+[A-Za-z0-9._~+/=-]{12,}", text);
        }
    }

    [Fact]
    public void Extension_DeclaresM3AndM4CanvasActions()
    {
        var script = ReadExtension();

        foreach (var action in new[]
        {
            "monitor_health",
            "list_recent_traces",
            "get_trace_summary",
            "get_trace_span_tree",
            "get_cache_summary",
        })
        {
            Assert.Contains($"name: \"{action}\"", script);
        }
    }

    [Fact]
    public void Extension_DeclaresTokenGatedProposalProxiesWithoutCanvasActionOrAnalysisExpansion()
    {
        var script = ReadExtension();

        Assert.Contains("/api/session-workspace/improvement-proposals", script);
        Assert.Contains("x-monitor-csrf\": \"local-monitor", script);
        Assert.Contains("readRequestBodyAtMost", script);
        Assert.Contains("1048576", script);
        Assert.Contains("Cache-Control\", \"no-store", script);
        Assert.Contains("headerToken !== undefined && headerToken !== token", script);
        Assert.Contains("queryToken !== null && queryToken !== token", script);
        Assert.Contains("isProposalHelperPath(path)", script);
        Assert.Contains("invalid_proposal_id", script);
        Assert.DoesNotContain("name: \"create_proposal\"", script);
        Assert.DoesNotContain("name: \"promote_proposal\"", script);
        Assert.DoesNotContain("session.send({ prompt: payload", script);
        Assert.DoesNotContain("/analysis/runs", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_ConfinesProposalApplyToTheTokenGatedHelperBoundary()
    {
        var extension = File.ReadAllText(Path.Combine(ExtensionDirectory(), "extension.mjs"));
        var workspace = File.ReadAllText(Path.Combine(ExtensionDirectory(), "canvas-workspace-helpers.mjs"));

        Assert.Contains("proposal-applies", extension);
        Assert.Contains("proposalApplyRoute", extension);
        Assert.Contains("/drafts/([^/]+)/apply", extension);
        Assert.Contains("/${encodeURIComponent(id)}/rollback", extension);
        Assert.Contains("x-monitor-csrf\": \"local-monitor", extension);
        Assert.Contains("Cache-Control\", \"no-store", extension);
        Assert.Contains("split(\";\", 1)[0].trim().toLowerCase() !== \"application/json\"", extension);
        Assert.Contains("mutationRoute ? { method: req.method, headers: { \"x-monitor-csrf\": \"local-monitor\" } }", extension);
        Assert.Contains("fixedErrors", extension);
        Assert.Contains("Apply locally", workspace);
        Assert.Contains("workspaceApplyRequest()", workspace);
        Assert.Contains("textContent", workspace);
        Assert.DoesNotContain("innerHTML", workspace);
        Assert.DoesNotContain("name: \"apply_proposal\"", extension);
        Assert.DoesNotContain("session.send", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void CanvasEffectComparison_UsesClosedTokenGatedProxyAndExplicitWorkspaceConfirmation()
    {
        var extension = File.ReadAllText(Path.Combine(ExtensionDirectory(), "extension.mjs"));
        var workspace = File.ReadAllText(Path.Combine(ExtensionDirectory(), "canvas-workspace-helpers.mjs"));

        foreach (var route in new[]
        {
            "/api/session-workspace/objective-evaluations",
            "/api/session-workspace/proposal-applies/receipts",
            "effect-comparisons/candidates",
            "effect-comparisons/",
        }) Assert.Contains(route, extension);
        Assert.Contains("effectComparisonRoute", extension);
        Assert.Contains("handleEffectComparisonProxy", extension);
        Assert.Contains("x-monitor-csrf\": \"local-monitor", extension);
        Assert.Contains("Cache-Control\", \"no-store", extension);
        Assert.Contains("request_too_large", extension);
        Assert.Contains("invalid_comparison_request", extension);
        Assert.DoesNotContain("name: \"confirm_comparison\"", extension);
        Assert.DoesNotContain("session.send", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", workspace, StringComparison.Ordinal);
        Assert.Contains("比較を確定", workspace);
        Assert.Contains("not_comparable", workspace);
        Assert.Contains("wrong_case", workspace);
        Assert.Contains("missing_evidence", workspace);
        Assert.Contains("overlaps_application", workspace);
        Assert.Contains("user_excluded", workspace);
        Assert.Contains("invalidated", workspace);
        Assert.Contains("textContent", workspace);
    }

    [Fact]
    public void CanvasProposalLifecycle_RemainsMetadataOnlyAndNeverEnablesApply()
    {
        var extension = ReadExtension();
        var workspace = File.ReadAllText(Path.Combine(ExtensionDirectory(), "canvas-workspace-helpers.mjs"));

        var actionNames = Regex.Matches(extension, "(?m)^\\s*name: \\\"(?<name>[^\\\"]+)\\\"")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        var proposalCreationStatuses = ProposalCreationStatuses(workspace);
        var proposalPromotionStatuses = Regex.Matches(workspace, "updateProposalStatus\\(s\\.proposal_id,\\\"(?<status>[^\\\"]+)\\\"\\)")
            .Select(match => match.Groups["status"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "monitor_health",
            "list_recent_traces",
            "get_trace_summary",
            "get_trace_span_tree",
            "get_cache_summary",
        ],
        actionNames);
        Assert.Equal(["candidate"], proposalCreationStatuses);
        Assert.Equal(["approved"], ProposalCreationStatuses("const payload={status:\"approved\"};"));
        Assert.Equal(["recommended"], proposalPromotionStatuses);
        Assert.DoesNotContain("session.send", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_UsesStrictInputSchemasForTraceAndListActions()
    {
        var script = ReadExtension();

        Assert.Contains("TRACE_ID_PATTERN", script);
        Assert.Contains("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", script);
        Assert.Contains("minimum: 1", script);
        Assert.Contains("maximum: MAX_TRACE_LIST_LIMIT", script);
        Assert.Contains("additionalProperties: false", script);
    }

    [Fact]
    public void Extension_ActionsFetchOnlyBoundedMonitorEndpoints()
    {
        var script = ReadExtension();

        Assert.Contains("/health/ready", script);
        Assert.Contains("/api/monitor/traces", script);
        Assert.Contains("/spans", script);
        Assert.Contains("limit=${MAX_SPAN_PAGE_SIZE}", script);
        AssertNoRawReferenceOtherThanAuthorizedPreview(script);
        Assert.DoesNotContain("payload_json", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_CapturesSdkSessionEventsFromTheFirstCanvasOpen()
    {
        var script = ReadExtension();

        Assert.Contains("createSdkSessionCapture", script);
        Assert.Contains("nativeSessionId: ctx.sessionId", script);
        Assert.Contains("ensureSdkSessionCapture(sessionCaptures", script);
        Assert.Contains("source_adapter: \"copilot-sdk-stream\"", script);
        Assert.Contains("source_surface: \"copilot-sdk\"", script);
        Assert.Contains("/api/session-ingest/v1/events", script);
        Assert.Contains("X-CAO-Session-Event-Version", script);
        Assert.Contains("gap_before_capture: true", script);
        Assert.Contains("assistant.usage", script);
        Assert.Contains("MAX_EVENTS_PER_BATCH = 100", script);
        Assert.Contains("MAX_BATCH_BYTES = 1_048_576", script);
    }

    [Fact]
    public void Extension_BoundsTraceTreeAndCacheSummaryOutputs()
    {
        var script = ReadExtension();

        Assert.Contains("MAX_SPAN_PAGE_SIZE = 200", script);
        Assert.Contains("MAX_TREE_NODES = 50", script);
        Assert.Contains("MAX_CACHE_TURNS = 50", script);
        Assert.Contains("hierarchy_status", script);
        Assert.Contains("flat_missing_parent_ids", script);
        Assert.Contains("flat_incomplete_parent_links", script);
        Assert.Contains("cache_hit_rate", script);
    }

    [Fact]
    public void Extension_DeclaresM5UiTriggerSurface()
    {
        var script = ReadExtension();

        // M5 helper page + trigger button (Sprint15 A3: Japanese button text).
        Assert.Contains("Copilotでこのトレースを分析", script);
        Assert.Contains("renderHelperHtml", script);
        Assert.Contains("startHelperServer", script);

        // session.send() is the UI-to-Copilot trigger mechanism.
        Assert.Contains("session.send({ prompt })", script);

        // Per-launch token protection on proxy + analyze routes.
        Assert.Contains("randomUUID", script);
        Assert.Contains("x-canvas-token", script);
        Assert.Contains("/api/traces", script);
        Assert.Contains("/api/analysis/options", script);
        Assert.Contains("/analyze", script);

        // Focus enum unchanged by Sprint15 A2's Japanese label change.
        Assert.Contains("\"latency\"", script);
        Assert.Contains("\"tokens\"", script);
        Assert.Contains("\"cache\"", script);
        Assert.Contains("\"errors\"", script);

        // Canvas launch mode is not constrained here, but action/log/artifact boundaries remain.
        Assert.DoesNotContain("--sanitized-only", script);
        Assert.Contains("normal raw-default Local Monitor", script);
        Assert.Contains("must not contain raw telemetry or PII", script);

        // Boundary invariants preserved.
        AssertNoRawReferenceOtherThanAuthorizedPreview(script);
        Assert.DoesNotContain("/analysis/runs", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DeclaresSessionWorkspaceShellAndKeepsAnalysisAtItsOwnRoute()
    {
        var script = ReadExtension();

        Assert.Contains("renderWorkspaceHtml", script);
        Assert.Contains("path === \"/analysis\"", script);
        Assert.Contains("renderHelperHtml({ instanceId, monitorUrl, healthState, statusCode, healthBody, error, token, extensionScope })", script);
        Assert.Contains("/api/session-workspace/sessions", script);
        Assert.Contains("/api/session-workspace/resolve", script);
        Assert.Contains("/api/session-instruction/", script);
        Assert.Contains("human-evaluation", script);
        Assert.Contains("x-monitor-csrf\": \"local-monitor", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void CanvasSessionWorkspace_PresentsBackendClaudeDiagnosticWithoutParallelFetch()
    {
        var workspace = File.ReadAllText(Path.Combine(ExtensionDirectory(), "canvas-workspace-helpers.mjs"));

        foreach (var field in new[]
        {
            "source_surface",
            "source_application_version",
            "source_adapter",
            "adapter_version",
            "schema_fingerprint",
            "compatibility_state",
            "reason_codes",
            "next_action",
        })
        {
            Assert.Contains(field, workspace);
        }

        Assert.Contains("Claude Code セッション", workspace);
        Assert.Contains("binding_state", workspace);
        Assert.Contains("completeness_reason_codes", workspace);
        Assert.Contains("一致するコンテンツ状態なし", workspace);
        Assert.Contains("診断を確認", workspace);
        Assert.DoesNotContain("/api/claude", workspace, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/source-diagnostics", workspace, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("innerHTML", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_SessionInstructionRouteSetsNoStoreForEveryOutcome()
    {
        var script = ReadExtension();

        Assert.Matches("""(?s)if \(req\.method === "GET" && path\.startsWith\("/api/session-instruction/"\)\) \{\s*res\.setHeader\("Cache-Control", "no-store"\);""", script);
    }

    [Fact]
    public void Extension_DeclaresTokenGatedSanitizedSessionEvidenceProxies()
    {
        var script = ReadExtension();

        Assert.Contains("/api/session-evidence/traces/", script);
        Assert.Contains("agent-graph", script);
        Assert.Contains("spans?limit=", script);
        Assert.Contains("headerToken !== undefined && headerToken !== token", script);
        Assert.Contains("queryToken !== null && queryToken !== token", script);
        Assert.Contains("handleEvidenceProxy", script);
        Assert.Contains("next_cursor", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DeclaresSprint17RequestedAnalysisOptionsWithoutRawRunner()
    {
        var script = ReadExtension();

        Assert.Contains("希望モデル", script);
        Assert.Contains("推奨 reasoning", script);
        Assert.Contains("Timeout hint", script);
        Assert.Contains("requested_model", script);
        Assert.Contains("requested_reasoning_effort", script);
        Assert.Contains("requested_timeout_seconds", script);
        Assert.Contains("analysis_trigger_id", script);
        Assert.Contains("prompt_template_version", script);
        Assert.Contains("message_id", script);
        Assert.Contains("session.send({ prompt })", script);

        Assert.DoesNotContain("sendAndWait", script);
        Assert.DoesNotContain("/analysis/runs", script);
        Assert.DoesNotContain("/traces/{traceId}/analysis", script);
        AssertNoRawReferenceOtherThanAuthorizedPreview(script);
        Assert.DoesNotContain("payload_json", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DoesNotRequireSanitizedOnlyLaunch()
    {
        var script = ReadExtension();

        Assert.DoesNotContain("--sanitized-only", script);
        Assert.DoesNotContain("Canvas-safe" + " posture", script);
        Assert.DoesNotContain("Requires the Local" + " Monitor", script);
    }

    [Fact]
    public void Extension_UsesJapaneseHelperUiText()
    {
        var script = ReadExtension();

        // Sprint15 A2: focus option labels are Japanese; enum values (asserted
        // above) are unchanged.
        Assert.Contains("遅い原因", script);
        Assert.Contains("トークン消費", script);
        Assert.Contains("キャッシュ効率", script);
        Assert.Contains("エラー原因", script);

        // Sprint15 A4: concrete, state-specific health/error guidance.
        Assert.Contains("Local Monitor が起動していません。", script);
        Assert.Contains("Local Monitor は起動していますが ready ではありません。", script);
        Assert.Contains("次の操作", script);
        Assert.Contains("/health/ready", script);

        // Sprint15 A5/A6: collapsed health response + Japanese monitor-page link.
        Assert.Contains("Local Monitor の接続状態", script);
        Assert.Contains("<details>", script);
        Assert.Contains("Local Monitor をブラウザで開く", script);
    }

    [Fact]
    public void Extension_DeclaresDecisionSupportingTraceLineFormatters()
    {
        var script = ReadExtension();

        // Sprint15 A1: decision-supporting trace line, built only from
        // sanitized compactTrace fields (no new monitor endpoint).
        Assert.Contains("formatTraceLine", script);
        Assert.Contains("statusLabel", script);
        Assert.Contains("formatTokens", script);
        Assert.Contains("formatDuration", script);
        Assert.Contains("formatClock", script);
        Assert.Contains("shortTraceId", script);
    }

    [Fact]
    public void Extension_DeclaresTraceDetailSummaryCardSurface()
    {
        var script = ReadExtension();

        // Sprint15 M3 (child C, D037): minimal bounded trace-detail summary
        // route + card, gated by the same x-canvas-token as every other route.
        Assert.Contains("/api/trace-detail/", script);
        Assert.Contains("cache_hit_rate", script);
        Assert.Contains("traceDetailSummary", script);
        Assert.Contains("選択したトレースの要約", script);

        // The new route must not bypass the existing per-route token check or
        // introduce a new Local Monitor endpoint / raw field category.
        Assert.Contains("x-canvas-token", script);
        Assert.Contains("/api/monitor/traces", script);
        Assert.Contains("/spans", script);
        AssertNoRawReferenceOtherThanAuthorizedPreview(script);
        Assert.DoesNotContain("payload_json", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DeclaresDashboardSummaryCardSurface()
    {
        var script = ReadExtension();

        // Sprint15 M4 (child B remainder, D038) + D039/D050 local-screen
        // label: Canvas-owned /api/summary proxy route + "Local Monitor
        // 概要" card, gated by the same x-canvas-token as every other route.
        Assert.Contains("/api/summary", script);
        Assert.Contains("fetchHelperSummary", script);
        Assert.Contains("/api/monitor/summary", script);
        Assert.Contains("Local Monitor 概要", script);
        Assert.Contains("fetchHelperPromptLabel(monitorUrl, trace.trace_id)", script);
        Assert.Contains("prompt_label", script);
        Assert.Contains("dropdownOptionLabel(pair[1])", script);
        Assert.Contains("Cache-Control", script);
        Assert.Contains("no-store", script);

        // The proxy must not bypass the existing per-route token check or
        // introduce a new Local Monitor endpoint. Prompt labels are confined
        // to the token-gated helper screen, not Canvas actions.
        Assert.Contains("x-canvas-token", script);
        AssertNoRawReferenceOtherThanAuthorizedPreview(script);
        Assert.DoesNotContain("payload_json", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DeclaresRawPreviewSurface()
    {
        var script = ReadExtension();

        // Sprint15 M5 (child D, D038): the authorized page-navigation raw
        // route. Gated by the same x-canvas-token as every other route, and
        // never fetched as JSON from client-side JS.
        Assert.Contains("/raw-preview/", script);
        Assert.Contains("extractRawPreviewFragment", script);
        Assert.Contains("renderRawPreviewHtml", script);
        Assert.Contains("生データを表示", script);
        Assert.Contains("Cache-Control", script);
        Assert.Contains("no-store", script);

        // Page-navigation only: the helper page's client-side <script> must
        // never fetch() the raw-preview route (that would turn it into a
        // JSON-fetch-of-raw surface, which D038 explicitly forbids).
        Assert.DoesNotContain("fetch(\"/raw-preview", script);
        Assert.DoesNotContain("fetch('/raw-preview", script);

        // The route must not loosen action/log/repository-safe boundaries.
        Assert.Contains("x-canvas-token", script);
        Assert.DoesNotContain("payload_json", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DeclaresTraceContentPreviewSurface()
    {
        var script = ReadExtension();

        // D050: the helper-page surface may show selected-trace prompt/response
        // previews, but only through the Canvas-owned token-gated local route.
        // It reuses the existing Local Monitor span detail route and keeps
        // action responses/logs/repository-safe output raw-free.
        Assert.Contains("/api/trace-content/", script);
        Assert.Contains("fetchHelperTraceContentPreview", script);
        Assert.Contains("/spans/${encodedSpanId}/detail", script);
        Assert.Contains("prompt_preview", script);
        Assert.Contains("response_preview", script);
        Assert.Contains("tracePromptPreview.textContent", script);
        Assert.Contains("traceResponsePreview.textContent", script);
        Assert.Contains("x-canvas-token", script);
        Assert.Contains("Cache-Control", script);
        Assert.Contains("no-store", script);
        Assert.DoesNotContain("innerHTML", script);
        Assert.DoesNotContain("payload_json", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DeclaresPromptLabelSurface()
    {
        var script = ReadExtension();

        // Sprint15 M7 (D039): the /api/traces helper-page route fetches each
        // trace's prompt label server-to-server from the Local Monitor's own
        // /traces/{traceId}/prompt-label and merges it in additively.
        Assert.Contains("/prompt-label", script);
        Assert.Contains("fetchHelperPromptLabel", script);
        Assert.DoesNotContain("payload_json", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DeclaresCrossRepoHelperLabelAndFilterSurface()
    {
        var script = ReadExtension();

        Assert.Contains("repository_name", script);
        Assert.Contains("workspace_label", script);
        Assert.Contains("repo_snapshot", script);
        Assert.Contains("unknown repository", script);
        Assert.Contains("repositoryLabel", script);
        Assert.Contains("workspaceLabel", script);
        Assert.Contains("repositoryFilterKey", script);
        Assert.Contains("repositoryFilterOptions", script);
        Assert.Contains("extensionScopeFromModuleUrl", script);
        Assert.Contains("リポジトリ / ワークスペース", script);
        Assert.Contains("拡張スコープ", script);

        Assert.DoesNotContain("process.cwd", script);
        Assert.DoesNotMatch(@"(?i)(C:\\Users\\|/Users/|/home/)", script);
        AssertNoRawReferenceOtherThanAuthorizedPreview(script);
        Assert.DoesNotContain("payload_json", script);
        Assert.DoesNotContain("console.log", script);
    }

    [Fact]
    public void Extension_DoesNotSendRepositoryMetadataInAnalysisPrompt()
    {
        var script = ReadExtension();
        var promptStart = script.IndexOf("export function buildAnalysisPrompt", StringComparison.Ordinal);
        var promptEnd = script.IndexOf("// --------------- helper page", StringComparison.Ordinal);
        Assert.True(promptStart >= 0, "buildAnalysisPrompt must be present.");
        Assert.True(promptEnd > promptStart, "helper page section must follow buildAnalysisPrompt.");
        var promptScript = script[promptStart..promptEnd];

        Assert.DoesNotContain("repository_name", promptScript);
        Assert.DoesNotContain("workspace_label", promptScript);
        Assert.DoesNotContain("repo_snapshot", promptScript);
        Assert.Contains("Trace id: ${traceId}", promptScript);
        Assert.Contains("Selected span id: ${spanId}", promptScript);
    }

    // Sprint15 M5 (child D, D038) is the deliberate exception to the
    // otherwise-blanket "no /raw reference" contract enforced by the facts
    // above. Rather than deleting those pre-existing checks (which would
    // weaken the action/log/repository-safe boundary), this scopes them by
    // stripping the two "/raw" substrings D038 explicitly authorizes — the
    // "/raw-preview" route path itself, and the server-to-server fetch
    // target template literal `/traces/${...}/raw` (which always ends the
    // literal immediately after "/raw" with a closing backtick) — before
    // asserting no "/raw" remains. Both authorized substrings are
    // independently pinned as present by Extension_DeclaresRawPreviewSurface
    // above, so stripping them here does not hide their absence.
    private static void AssertNoRawReferenceOtherThanAuthorizedPreview(string script)
    {
        var withoutAuthorizedRawPreviewReferences = script
            .Replace("/raw-preview", string.Empty)
            .Replace("/raw`", string.Empty);
        Assert.DoesNotContain("/raw", withoutAuthorizedRawPreviewReferences);
    }

    [Fact]
    public void CanvasHelperJsPassesSyntaxCheckAndUnitSmoke()
    {
        // Sprint15 M1 A0 (F8 prerequisite): executable JS smoke wired into the
        // dotnet test gate, not just substring matching.
        var directory = ExtensionDirectory();

        var extensionCheck = RunNode(directory, "--check", "extension.mjs");
        Assert.True(extensionCheck.ExitCode == 0, $"node --check extension.mjs failed: {extensionCheck.Output}{extensionCheck.Error}");

        var helpersCheck = RunNode(directory, "--check", "canvas-helpers.mjs");
        Assert.True(helpersCheck.ExitCode == 0, $"node --check canvas-helpers.mjs failed: {helpersCheck.Output}{helpersCheck.Error}");

        var sessionEventsCheck = RunNode(directory, "--check", "canvas-session-events.mjs");
        Assert.True(sessionEventsCheck.ExitCode == 0, $"node --check canvas-session-events.mjs failed: {sessionEventsCheck.Output}{sessionEventsCheck.Error}");

        var workspaceHelpersCheck = RunNode(directory, "--check", "canvas-workspace-helpers.mjs");
        Assert.True(workspaceHelpersCheck.ExitCode == 0, $"node --check canvas-workspace-helpers.mjs failed: {workspaceHelpersCheck.Output}{workspaceHelpersCheck.Error}");

        var unitSmoke = RunNode(directory, "--test", "canvas-helpers.test.mjs");
        Assert.True(unitSmoke.ExitCode == 0, $"node --test canvas-helpers.test.mjs failed: {unitSmoke.Output}{unitSmoke.Error}");

        var sessionEventsUnitSmoke = RunNode(directory, "--test", "canvas-session-events.test.mjs");
        Assert.True(sessionEventsUnitSmoke.ExitCode == 0, $"node --test canvas-session-events.test.mjs failed: {sessionEventsUnitSmoke.Output}{sessionEventsUnitSmoke.Error}");

        var workspaceHelpersUnitSmoke = RunNode(directory, "--test", "canvas-workspace-helpers.test.mjs");
        Assert.True(workspaceHelpersUnitSmoke.ExitCode == 0, $"node --test canvas-workspace-helpers.test.mjs failed: {workspaceHelpersUnitSmoke.Output}{workspaceHelpersUnitSmoke.Error}");

        var evidenceHelpersSyntax = RunNode(directory, "--check", "canvas-evidence-helpers.mjs");
        Assert.True(evidenceHelpersSyntax.ExitCode == 0, $"node --check canvas-evidence-helpers.mjs failed: {evidenceHelpersSyntax.Output}{evidenceHelpersSyntax.Error}");

        var evidenceHelpersUnitSmoke = RunNode(directory, "--test", "canvas-evidence-helpers.test.mjs");
        Assert.True(evidenceHelpersUnitSmoke.ExitCode == 0, $"node --test canvas-evidence-helpers.test.mjs failed: {evidenceHelpersUnitSmoke.Output}{evidenceHelpersUnitSmoke.Error}");

        var evidenceProxySyntax = RunNode(directory, "--check", "canvas-evidence-proxy.mjs");
        Assert.True(evidenceProxySyntax.ExitCode == 0, $"node --check canvas-evidence-proxy.mjs failed: {evidenceProxySyntax.Output}{evidenceProxySyntax.Error}");

        var evidenceProxyUnitSmoke = RunNode(directory, "--test", "canvas-evidence-proxy.test.mjs");
        Assert.True(evidenceProxyUnitSmoke.ExitCode == 0, $"node --test canvas-evidence-proxy.test.mjs failed: {evidenceProxyUnitSmoke.Output}{evidenceProxyUnitSmoke.Error}");
    }

    private static string ReadExtension()
    {
        var directory = ExtensionDirectory();
        var extension = File.ReadAllText(Path.Combine(directory, "extension.mjs"));
        var helpers = File.ReadAllText(Path.Combine(directory, "canvas-helpers.mjs"));
        var sessionEvents = File.ReadAllText(Path.Combine(directory, "canvas-session-events.mjs"));
        var workspaceHelpers = File.ReadAllText(Path.Combine(directory, "canvas-workspace-helpers.mjs"));
        var evidenceHelpers = File.ReadAllText(Path.Combine(directory, "canvas-evidence-helpers.mjs"));
        var evidenceProxy = File.ReadAllText(Path.Combine(directory, "canvas-evidence-proxy.mjs"));
        return extension + "\n" + helpers + "\n" + sessionEvents + "\n" + workspaceHelpers + "\n" + evidenceHelpers + "\n" + evidenceProxy;
    }

    private static string[] ProposalCreationStatuses(string workspace) =>
        Regex.Matches(workspace, "(?:const\\s+)?payload\\s*=\\s*\\{\\s*status\\s*:\\s*\\\"(?<status>[^\\\"]+)\\\"")
            .Select(match => match.Groups["status"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string ExtensionDirectory()
    {
        var repoRoot = FindRepoRoot();
        return Path.Combine(repoRoot, ".github", "extensions", "otel-monitor-canvas");
    }

    private static string AssertPathResolvesInside(string directory, string relativePath)
    {
        Assert.False(Path.IsPathRooted(relativePath), $"{relativePath} must be relative to the extension directory.");
        var fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, fullPath, StringComparison.OrdinalIgnoreCase);
        return fullPath;
    }

    private static bool IsBinaryFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertBinaryDoesNotContainUnsafeText(string path, string relativePath)
    {
        var ascii = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("C:\\Users\\", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Users/", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw OTLP", ascii, StringComparison.OrdinalIgnoreCase);
    }

    private static (int ExitCode, string Output, string Error) RunNode(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo;
        try
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start node.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, error);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "node was not found on PATH. The otel-monitor-canvas extension is node-based; install Node.js to run its JS smoke tests.",
                ex);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}

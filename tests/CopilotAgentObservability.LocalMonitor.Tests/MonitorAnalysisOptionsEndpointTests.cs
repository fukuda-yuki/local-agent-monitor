using System.Net;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorAnalysisOptionsEndpointTests
{
    [Fact]
    public async Task AnalysisOptions_ReturnsDefaultProfilesAndModel()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/api/analysis/options");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("standard", root.GetProperty("default_profile").GetString());
        Assert.Equal("gpt-5", root.GetProperty("default_model").GetString());
        Assert.Equal(["low", "medium", "high"], root.GetProperty("reasoning_efforts").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(3, root.GetProperty("profiles").GetArrayLength());
        Assert.Contains(
            root.GetProperty("profiles").EnumerateArray(),
            profile => profile.GetProperty("id").GetString() == "deep"
                && profile.GetProperty("timeout_seconds").GetInt32() == 600
                && profile.GetProperty("default_reasoning_effort").GetString() == "high");
    }

    [Fact]
    public async Task AnalysisOptions_ReturnsConfiguredModelsWithoutSecrets()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(
            temp,
            new Dictionary<string, string?>
            {
                ["CopilotAnalysis:DefaultModel"] = "gpt-5.5",
                ["CopilotAnalysis:Provider:ApiKey"] = "secret-value",
                ["CopilotAnalysis:Models:gpt-5.5:DisplayName"] = "GPT-5.5",
                ["CopilotAnalysis:Models:gpt-5.5:Provider"] = "openai",
                ["CopilotAnalysis:Models:gpt-5.5:SupportsReasoningEffort"] = "true",
                ["CopilotAnalysis:Models:glm-5.2:DisplayName"] = "GLM-5.2",
                ["CopilotAnalysis:Models:glm-5.2:Provider"] = "opencode-go",
                ["CopilotAnalysis:Models:glm-5.2:SupportsReasoningEffort"] = "false",
            });

        var response = await host.Client.GetAsync("/api/analysis/options");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var models = root.GetProperty("models").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gpt-5.5", root.GetProperty("default_model").GetString());
        Assert.Contains(models, model => model.GetProperty("id").GetString() == "gpt-5.5"
            && model.GetProperty("display_name").GetString() == "GPT-5.5"
            && model.GetProperty("provider").GetString() == "openai"
            && model.GetProperty("supports_reasoning_effort").GetBoolean()
            && model.GetProperty("is_default").GetBoolean());
        Assert.Contains(models, model => model.GetProperty("id").GetString() == "glm-5.2"
            && !model.GetProperty("supports_reasoning_effort").GetBoolean());
        Assert.DoesNotContain("secret-value", body);
        Assert.DoesNotContain("api", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalysisOptions_GuardsUnsafeConfiguredMetadata()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(
            temp,
            new Dictionary<string, string?>
            {
                ["CopilotAnalysis:DefaultModel"] = "secret-model",
                ["CopilotAnalysis:Provider:Type"] = "https://provider.example.test/v1",
                ["CopilotAnalysis:Models:safe-model:DisplayName"] = @"C:\Users\someone\model.txt",
                ["CopilotAnalysis:Models:safe-model:Provider"] = "https://provider.example.test/v1",
                ["CopilotAnalysis:Models:safe-model:SupportsReasoningEffort"] = "true",
                ["CopilotAnalysis:Models:secret-model:DisplayName"] = "secret model",
                ["CopilotAnalysis:Models:secret-model:Provider"] = "secret-provider",
            });

        var response = await host.Client.GetAsync("/api/analysis/options");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var models = root.GetProperty("models").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("safe-model", root.GetProperty("default_model").GetString());
        Assert.Single(models);
        Assert.Equal("safe-model", models[0].GetProperty("id").GetString());
        Assert.Equal("safe-model", models[0].GetProperty("display_name").GetString());
        Assert.Equal("copilot", models[0].GetProperty("provider").GetString());
        Assert.DoesNotContain("secret-model", body);
        Assert.DoesNotContain("secret-provider", body);
        Assert.DoesNotContain("provider.example.test", body);
        Assert.DoesNotContain(@"C:\Users", body);
    }

    [Fact]
    public async Task AnalysisOptions_IsAvailableUnderSanitizedOnly()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync("/api/analysis/options");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<RunningMonitorHost> StartHostAsync(
        MonitorTempDirectory temp,
        IReadOnlyDictionary<string, string?>? configurationValues = null,
        bool sanitizedOnly = false) =>
        MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions
            {
                StartWriter = false,
                StartProjectionWorker = false,
                ConfigurationValues = configurationValues,
                UseUserSecrets = false,
            });
}

using CopilotAgentObservability.LocalMonitor.Analysis;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CopilotAnalysisSdkExecutorTests
{
    private static readonly string[] CoreToolNames =
    [
        "get_raw_trace",
        "get_raw_record",
        "get_raw_span_context",
        "get_trace_summary",
        "get_trace_span_tree",
        "get_cache_summary",
        "get_instruction_evidence",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CapturesEmptyModeChildBoundCustomOnlyConfiguration(
        bool includesSubmissionTool)
    {
        using var temp = new MonitorTempDirectory();
        var ownedChild = Path.GetFullPath(Path.Combine(temp.Path, "owned-child"));
        Directory.CreateDirectory(ownedChild);
        CopilotClientOptions? capturedOptions = null;
        SessionConfig? capturedSessionConfig = null;
        var sentinel = new InvalidOperationException("client factory sentinel");
        await using var data = await CreateToolDataAsync(includesSubmissionTool
            ? MonitorAnalysisFocus.InstructionDiagnosis
            : MonitorAnalysisFocus.Errors);
        var executor = new CopilotAnalysisSdkExecutor((options, sessionConfig) =>
        {
            capturedOptions = options;
            capturedSessionConfig = sessionConfig;
            throw sentinel;
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                ownedChild,
                new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
                new CopilotAnalysisToolRequest("synthetic prompt", data),
                CancellationToken.None));

        Assert.Same(sentinel, exception);
        var options = Assert.IsType<CopilotClientOptions>(capturedOptions);
        Assert.Equal(CopilotClientMode.Empty, options.Mode);
        Assert.Equal(ownedChild, options.BaseDirectory);
        Assert.Equal(ownedChild, options.WorkingDirectory);
        var expectedToolNames = includesSubmissionTool
            ? [.. CoreToolNames, "submit_instruction_finding"]
            : CoreToolNames;
        await AssertClosedSessionBoundaryAsync(capturedSessionConfig, ownedChild, expectedToolNames);
    }

    private static async Task AssertClosedSessionBoundaryAsync(
        SessionConfig? captured,
        string expectedDirectory,
        IReadOnlyList<string> expectedToolNames)
    {
        var config = Assert.IsType<SessionConfig>(captured);
        var availableTools = Assert.IsAssignableFrom<IList<string>>(config.AvailableTools);
        var tools = Assert.IsAssignableFrom<IList<Microsoft.Extensions.AI.AIFunctionDeclaration>>(config.Tools);
        Assert.Equal(expectedToolNames, tools.Select(static tool => tool.Name));
        Assert.Equal(expectedToolNames.Select(static name => $"custom:{name}"), availableTools);
        Assert.DoesNotContain(availableTools, static pattern => pattern.Contains('*', StringComparison.Ordinal));
        Assert.DoesNotContain(availableTools, static pattern => pattern.StartsWith("builtin:", StringComparison.Ordinal));
        Assert.DoesNotContain(availableTools, static pattern => pattern.StartsWith("mcp:", StringComparison.Ordinal));
        Assert.Equal(expectedDirectory, config.WorkingDirectory);
        var largeOutput = Assert.IsType<LargeToolOutputConfig>(config.LargeOutput);
        Assert.True(largeOutput.Enabled);
        Assert.Equal(expectedDirectory, largeOutput.OutputDirectory);
        Assert.True(config.McpServers is null or { Count: 0 });
#pragma warning disable GHCP001
        var permissionHandler = Assert.IsType<Func<PermissionRequest, PermissionInvocation, Task<GitHub.Copilot.Rpc.PermissionDecision>>>(config.OnPermissionRequest);
        var decision = await permissionHandler(
            new PermissionRequestRead { Intention = "synthetic", Path = expectedDirectory },
            new PermissionInvocation { SessionId = "synthetic-session" });
        Assert.Equal("user-not-available", decision.Kind);
#pragma warning restore GHCP001
    }

    private static ValueTask<MonitorAnalysisToolData> CreateToolDataAsync(MonitorAnalysisFocus focus) =>
        MonitorAnalysisToolData.CreateAsync(
                new EmptyProjectionStore(),
                new MonitorAnalysisContext(
                    7,
                    "trace-anchor",
                    RawRecordId: null,
                    SpanId: null,
                    focus,
                    OperationToken: new MonitorAnalysisOperationToken([1])),
                CancellationToken.None);

    private sealed class EmptyProjectionStore : ProjectionStoreTestDouble;
}

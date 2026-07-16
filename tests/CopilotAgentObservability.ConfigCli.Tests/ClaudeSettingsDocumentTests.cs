using CopilotAgentObservability.ConfigCli.Setup.Documents;
using System.Text;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeSettingsDocumentTests
{
    private static readonly ClaudeSettingsHook[] Hooks =
    [
        new("SessionStart", "monitor.exe", ["hook-forward", "--endpoint", "http://127.0.0.1:4320", "--timeout-ms", "250", "--source", "claude-code", "--source-version", "2.1.207"], 5),
        new("SessionEnd", "monitor.exe", ["hook-forward", "--endpoint", "http://127.0.0.1:4320", "--timeout-ms", "250", "--source", "claude-code", "--source-version", "2.1.207"], 5),
    ];

    [Fact]
    public void Plan_PreservesCommentsUnrelatedSettingsAndHookOrder()
    {
        const string source = "{\r\n  // root-comment\r\n  \"env\": {\r\n    \"UNRELATED\": \"keep\", // env-comment\r\n    \"CLAUDE_CODE_ENABLE_TELEMETRY\": \"0\"\r\n  },\r\n  \"hooks\": {\r\n    \"SessionStart\": [\r\n      { \"matcher\": \"synthetic\", \"hooks\": [{ \"type\": \"command\", \"command\": \"other.exe\" }] }\r\n    ]\r\n  },\r\n  \"unrelated\": { \"nested\": true }\r\n}\r\n";
        var desiredEnv = new[]
        {
            new ClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", "1"),
            new ClaudeSettingsEnvValue("OTEL_TRACES_EXPORTER", "otlp"),
        };

        var result = ClaudeSettingsDocument.Parse(source).Plan(desiredEnv, Hooks);

        Assert.Equal(ClaudeSettingsPlanDisposition.Change, result.Disposition);
        Assert.NotNull(result.RenderedContent);
        Assert.Contains("// root-comment", result.RenderedContent, StringComparison.Ordinal);
        Assert.Contains("// env-comment", result.RenderedContent, StringComparison.Ordinal);
        Assert.Contains("\"UNRELATED\": \"keep\"", result.RenderedContent, StringComparison.Ordinal);
        Assert.Contains("\"unrelated\": { \"nested\": true }", result.RenderedContent, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", result.RenderedContent!, StringComparison.Ordinal);
        Assert.True(result.RenderedContent.IndexOf("other.exe", StringComparison.Ordinal) <
            result.RenderedContent.IndexOf("monitor.exe", StringComparison.Ordinal));
        Assert.Equal(result.RenderedContent, ClaudeSettingsDocument.Parse(result.RenderedContent).Plan(desiredEnv, Hooks).RenderedContent);
        Assert.Equal(ClaudeSettingsPlanDisposition.NoOp, ClaudeSettingsDocument.Parse(result.RenderedContent).Plan(desiredEnv, Hooks).Disposition);
    }

    [Fact]
    public void Plan_ExactOwnedHookIsNoOpButDifferingOwnedHookIsConflict()
    {
        const string exact = "{\"env\":{\"CLAUDE_CODE_ENABLE_TELEMETRY\":\"1\"},\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"monitor.exe\",\"args\":[\"hook-forward\",\"--endpoint\",\"http://127.0.0.1:4320\",\"--timeout-ms\",\"250\",\"--source\",\"claude-code\",\"--source-version\",\"2.1.207\"],\"timeout\":5}]}]}}";
        var env = new[] { new ClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", "1") };
        var hook = new[] { Hooks[0] };

        var noOp = ClaudeSettingsDocument.Parse(exact).Plan(env, hook);
        var conflict = ClaudeSettingsDocument.Parse(exact.Replace("\"timeout\":5", "\"timeout\":4", StringComparison.Ordinal)).Plan(env, hook);

        Assert.Equal(ClaudeSettingsPlanDisposition.NoOp, noOp.Disposition);
        Assert.Equal(exact, noOp.RenderedContent);
        Assert.Equal(ClaudeSettingsPlanDisposition.HookCommandConflict, conflict.Disposition);
        Assert.Null(conflict.RenderedContent);
    }

    [Theory]
    [InlineData("changed_command")]
    [InlineData("changed_argument_value")]
    [InlineData("reordered_arguments")]
    public void Plan_SelectorIdentifiableHookChangesAreConflict(string mutation)
    {
        const string exact = "{\"env\":{\"CLAUDE_CODE_ENABLE_TELEMETRY\":\"1\"},\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"monitor.exe\",\"args\":[\"hook-forward\",\"--endpoint\",\"http://127.0.0.1:4320\",\"--timeout-ms\",\"250\",\"--source\",\"claude-code\",\"--source-version\",\"2.1.207\"],\"timeout\":5}]}]}}";
        var source = mutation switch
        {
            "changed_command" => exact.Replace("\"command\":\"monitor.exe\"", "\"command\":\"other-monitor.exe\"", StringComparison.Ordinal),
            "changed_argument_value" => exact.Replace("\"2.1.207\"", "\"2.1.208\"", StringComparison.Ordinal),
            "reordered_arguments" => exact.Replace("\"hook-forward\",\"--endpoint\"", "\"--endpoint\",\"hook-forward\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        var result = ClaudeSettingsDocument.Parse(source).Plan(
            [new ClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", "1")],
            [Hooks[0]]);

        Assert.Equal(ClaudeSettingsPlanDisposition.HookCommandConflict, result.Disposition);
        Assert.Null(result.RenderedContent);
    }

    [Fact]
    public void Plan_SemanticallyExactOwnedHookIgnoresJsonPropertyOrder()
    {
        const string source = "{\"env\":{\"CLAUDE_CODE_ENABLE_TELEMETRY\":\"1\"},\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"timeout\":5,\"args\":[\"hook-forward\",\"--endpoint\",\"http://127.0.0.1:4320\",\"--timeout-ms\",\"250\",\"--source\",\"claude-code\",\"--source-version\",\"2.1.207\"],\"command\":\"monitor.exe\",\"type\":\"command\"}]}]}}";

        var result = ClaudeSettingsDocument.Parse(source).Plan(
            [new ClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", "1")],
            [Hooks[0]]);

        Assert.Equal(ClaudeSettingsPlanDisposition.NoOp, result.Disposition);
        Assert.Equal(source, result.RenderedContent);
    }

    [Fact]
    public void Plan_DuplicateExactOwnedHookGroupsAreConflict()
    {
        const string group = "{\"hooks\":[{\"type\":\"command\",\"command\":\"monitor.exe\",\"args\":[\"hook-forward\",\"--endpoint\",\"http://127.0.0.1:4320\",\"--timeout-ms\",\"250\",\"--source\",\"claude-code\",\"--source-version\",\"2.1.207\"],\"timeout\":5}]}";
        var source = $"{{\"env\":{{\"CLAUDE_CODE_ENABLE_TELEMETRY\":\"1\"}},\"hooks\":{{\"SessionStart\":[{group},{group}]}}}}";

        var result = ClaudeSettingsDocument.Parse(source).Plan(
            [new ClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", "1")],
            [Hooks[0]]);

        Assert.Equal(ClaudeSettingsPlanDisposition.HookCommandConflict, result.Disposition);
        Assert.Null(result.RenderedContent);
    }

    [Theory]
    [InlineData("{\"env\":{\"A\":\"1\",\"A\":\"2\"}}")]
    [InlineData("{\"hooks\":{\"SessionStart\":[],\"SessionStart\":[]}}")]
    [InlineData("{\"env\":[]}")]
    [InlineData("{\"hooks\":[]}")]
    [InlineData("{\"env\":{")]
    public void ParseOrPlan_MalformedDuplicateOrWrongShapeFailsClosed(string source)
    {
        Assert.Throws<FormatException>(() => ClaudeSettingsDocument.Parse(source).Plan(
            [new ClaudeSettingsEnvValue("A", "1")],
            [Hooks[0]]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_RejectsDocumentOverOneMiBUtf8(bool includeMultibyteValue)
    {
        var source = CreateSizedDocument((1024 * 1024) + 1, includeMultibyteValue);

        Assert.Equal((1024 * 1024) + 1, Encoding.UTF8.GetByteCount(source));
        Assert.Throws<FormatException>(() => ClaudeSettingsDocument.Parse(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_AcceptsDocumentAtOneMiBUtf8(bool includeMultibyteValue)
    {
        var source = CreateSizedDocument(1024 * 1024, includeMultibyteValue);

        Assert.Equal(1024 * 1024, Encoding.UTF8.GetByteCount(source));
        Assert.Equal(source, ClaudeSettingsDocument.Parse(source).Content);
    }

    [Fact]
    public void Plan_NewDocumentCreatesNestedEnvAndHooksWithoutFinalNewline()
    {
        var result = ClaudeSettingsDocument.Parse("{}").Plan(
            [new ClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", "1")],
            [Hooks[0]]);

        Assert.Equal(ClaudeSettingsPlanDisposition.Change, result.Disposition);
        Assert.NotNull(result.RenderedContent);
        Assert.False(result.RenderedContent!.EndsWith('\n'));
        Assert.Contains("\"env\"", result.RenderedContent, StringComparison.Ordinal);
        Assert.Contains("\"hooks\"", result.RenderedContent, StringComparison.Ordinal);
        Assert.Equal(ClaudeSettingsPlanDisposition.NoOp,
            ClaudeSettingsDocument.Parse(result.RenderedContent).Plan(
                [new ClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", "1")],
                [Hooks[0]]).Disposition);
    }

    private static string CreateSizedDocument(int utf8ByteCount, bool includeMultibyteValue)
    {
        const string prefix = "{\"padding\":\"";
        const string suffix = "\"}";
        var marker = includeMultibyteValue ? "€" : string.Empty;
        var structuralBytes = Encoding.UTF8.GetByteCount(prefix + marker + suffix);
        return prefix + new string('x', utf8ByteCount - structuralBytes) + marker + suffix;
    }
}

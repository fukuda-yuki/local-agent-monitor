using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeCodeFirstTraceAdapterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 1, 2, 3, TimeSpan.Zero);
    private static readonly string ExpectedAgentSdkGuidance =
        "Caller-managed only. Merge, do not replace, the process environment and " +
        "flush telemetry before a short-lived process exits.\n\nPython:\n" +
        string.Join('\n',
        [
            "import os",
            "from claude_agent_sdk import ClaudeAgentOptions",
            string.Empty,
            "options = ClaudeAgentOptions(env={",
            "    **os.environ,",
            "    \"CLAUDE_CODE_ENABLE_TELEMETRY\": \"1\",",
            "    \"CLAUDE_CODE_ENHANCED_TELEMETRY_BETA\": \"1\",",
            "    \"OTEL_TRACES_EXPORTER\": \"otlp\",",
            "    \"OTEL_EXPORTER_OTLP_TRACES_PROTOCOL\": \"http/protobuf\",",
            "    \"OTEL_EXPORTER_OTLP_TRACES_ENDPOINT\": \"<canonical-origin>/v1/traces\",",
            "})",
            string.Empty,
            "# Flush telemetry before a short-lived process exits.",
        ]) +
        "\n\nTypeScript:\n" +
        string.Join('\n',
        [
            "const options = {",
            "  env: {",
            "    ...process.env,",
            "    CLAUDE_CODE_ENABLE_TELEMETRY: \"1\",",
            "    CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: \"1\",",
            "    OTEL_TRACES_EXPORTER: \"otlp\",",
            "    OTEL_EXPORTER_OTLP_TRACES_PROTOCOL: \"http/protobuf\",",
            "    OTEL_EXPORTER_OTLP_TRACES_ENDPOINT: \"<canonical-origin>/v1/traces\",",
            "  },",
            "};",
            string.Empty,
            "// Pass options.env to the Agent SDK and flush telemetry before a short-lived process exits.",
        ]) +
        "\nfirst-trace never edits caller-owned configuration.";

    [Fact]
    public void Guidance_ProvidesAllVariantsWithoutSecretsOrContentCaptureCommands()
    {
        var platform = new SetupTestPlatform(Now, userProfile: "C:\\Users\\guidance-user");
        var adapter = new ClaudeCodeFirstTraceAdapter(platform);

        var guidance = adapter.GetGuidance(interaction: null, includeSetupPlan: true);
        var guidanceText = string.Join('\n', guidance.SelectMany(item => new[] { item.Text, item.Command ?? string.Empty }));
        var envelope = new FirstTraceEnvelope(
            "begin",
            Success: false,
            FirstTraceCodes.Blocked,
            adapter.AdapterId,
            adapter.SourceSurface,
            VerificationId: null,
            Doctor: null,
            EvaluationPreview: null,
            guidance,
            Candidates: [],
            Truncated: false);
        var serializedEnvelope = FirstTraceJson.Serialize(envelope);

        Assert.Equal(["common", "interactive-cli", "print", "agent-sdk"], guidance.Select(item => item.Interaction));
        Assert.Contains("setup plan --adapter claude-code --target cli", guidance.Select(item => item.Command));
        Assert.Equal("claude", guidance.Single(item => item.Interaction == "interactive-cli").Command);
        Assert.Equal("claude -p \"Reply with exactly: OK\"", guidance.Single(item => item.Interaction == "print").Command);
        Assert.Equal(ExpectedAgentSdkGuidance, guidance.Single(item => item.Interaction == "agent-sdk").Text);

        foreach (var forbidden in new[]
        {
            "guidance-secret-marker",
            "prompt-content-marker",
            "OTEL_LOG_USER_PROMPTS",
            "OTEL_LOG_TOOL_DETAILS",
            "OTEL_LOG_TOOL_CONTENT",
            "C:\\Users\\guidance-user",
            "C:\\Users\\setup-test",
            "/home/",
            "/tmp/",
            "secret",
            "password",
            "authorization",
        })
        {
            Assert.DoesNotContain(forbidden, guidanceText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, serializedEnvelope, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Selection_AutoSelectsTheSingleSessionChainInOrdinalReferenceOrder()
    {
        var trace = "0123456789abcdef0123456789abcdef";
        var session = "00000000-0000-7000-8000-000000000001";
        var candidates = Candidates(
            Binding(trace, session),
            Completeness(session),
            Pipeline("projection", trace, "0123456789abcdef"),
            Pipeline("ingest", trace, "fedcba9876543210"));

        var selection = CreateAdapter().SelectEvidence(candidates, Now);

        Assert.False(selection.RequiresExplicitSelection);
        Assert.Equal(selection.EvidenceRefs.OrderBy(value => value, StringComparer.Ordinal), selection.EvidenceRefs);
        Assert.Equal(candidates.Select(value => value.EvidenceRef).OrderBy(value => value, StringComparer.Ordinal), selection.EvidenceRefs);
    }

    [Fact]
    public void Selection_RequiresExplicitChoiceForTwoSessionChains()
    {
        var candidates = Candidates(
            Binding("0123456789abcdef0123456789abcdef", "00000000-0000-7000-8000-000000000001"),
            Binding("fedcba9876543210fedcba9876543210", "00000000-0000-7000-8000-000000000002"));

        var selection = CreateAdapter().SelectEvidence(candidates, Now);

        Assert.True(selection.RequiresExplicitSelection);
    }

    [Fact]
    public void Selection_RequiresExplicitChoiceWhenOneTraceIsClaimedByTwoSessions()
    {
        var candidates = Candidates(
            Binding("0123456789abcdef0123456789abcdef", "00000000-0000-7000-8000-000000000001"),
            Binding("0123456789abcdef0123456789abcdef", "00000000-0000-7000-8000-000000000002"));

        var selection = CreateAdapter().SelectEvidence(candidates, Now);

        Assert.True(selection.RequiresExplicitSelection);
    }

    private static ClaudeCodeFirstTraceAdapter CreateAdapter() =>
        new(new SetupTestPlatform(Now));

    private static IReadOnlyList<DoctorEvidenceCandidate> Candidates(params string[] references) =>
        references.Select((reference, index) => new DoctorEvidenceCandidate(
            Guid.CreateVersion7(Now.AddTicks(index)).ToString("D"),
            "00000000-0000-7000-8000-000000000003",
            "claude-code",
            "claude-code-otel",
            DoctorEvidenceClass.RealSource,
            ParseKind(reference),
            reference,
            Now.AddTicks(index),
            Now.AddMinutes(10))).ToArray();

    private static string Binding(string trace, string session) =>
        $"claude-otel-binding-{trace}-{session}";

    private static string Completeness(string session) =>
        $"claude-otel-completeness-{session}";

    private static string Pipeline(string kind, string trace, string span) =>
        $"claude-otel-{kind}-{trace}-{span}";

    private static DoctorEvidenceKind ParseKind(string evidenceRef) =>
        evidenceRef switch
        {
            var value when value.StartsWith("claude-otel-binding-", StringComparison.Ordinal) => DoctorEvidenceKind.ExactSessionBinding,
            var value when value.StartsWith("claude-otel-completeness-", StringComparison.Ordinal) => DoctorEvidenceKind.CompletenessContent,
            var value when value.StartsWith("claude-otel-projection-", StringComparison.Ordinal) => DoctorEvidenceKind.Projection,
            _ => DoctorEvidenceKind.Ingest,
        };
}

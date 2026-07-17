using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeCodeFirstTraceAdapterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Guidance_ProvidesAllVariantsWithoutSecretsOrContentCaptureCommands()
    {
        var adapter = CreateAdapter();

        var guidance = adapter.GetGuidance(interaction: null, includeSetupPlan: true);
        var commands = guidance
            .Where(item => item.Command is not null)
            .Select(item => item.Command!)
            .ToArray();
        var rendered = string.Join('\n', guidance.SelectMany(item => new[] { item.Text, item.Command ?? string.Empty }));

        Assert.Equal(["common", "interactive-cli", "print", "agent-sdk"], guidance.Select(item => item.Interaction));
        Assert.Contains("setup plan --adapter claude-code --target cli", commands);
        Assert.Contains("claude", commands);
        Assert.Contains("claude -p \"Reply with exactly: OK\"", commands);
        Assert.DoesNotContain("OTEL_LOG_USER_PROMPTS", commands);
        Assert.DoesNotContain("OTEL_LOG_TOOL_DETAILS", commands);
        Assert.DoesNotContain("OTEL_LOG_TOOL_CONTENT", commands);
        Assert.DoesNotContain("secret", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", rendered, StringComparison.OrdinalIgnoreCase);
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

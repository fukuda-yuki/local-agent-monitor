namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class TraceSourceResolverTests
{
    private const string TraceA = "11111111111111111111111111111111";
    private const string TraceB = "22222222222222222222222222222222";

    [Theory]
    [InlineData("client.kind", "vscode-copilot-chat", "vscode-copilot-chat")]
    [InlineData("client.kind", "copilot-cli", "copilot-cli")]
    [InlineData("service.name", "copilot-chat", "vscode-copilot-chat")]
    [InlineData("service.name", "github-copilot", "copilot-cli")]
    public void Resolve_MapsOnlyExactRecognisedEvidence(string key, string value, string expectedFamily)
    {
        var resolution = Assert.Single(OtlpTraceSourceResolver.Resolve(Payload(
            Resource(TraceA, Attribute(key, value)))));

        Assert.Equal(TraceSourceResolutionState.Resolved, resolution.State);
        Assert.Equal(expectedFamily, resolution.SourceFamily);
        Assert.True(resolution.RelevantEvidenceObserved);
        Assert.False(resolution.UnknownCandidateObserved);
    }

    [Fact]
    public void Resolve_DuplicateAndMixedKeyAgreementResolveOneFamily()
    {
        var resolution = Assert.Single(OtlpTraceSourceResolver.Resolve(Payload(
            Resource(
                TraceA,
                Attribute("client.kind", "copilot-cli"),
                Attribute("client.kind", "copilot-cli"),
                Attribute("service.name", "github-copilot")))));

        Assert.Equal(TraceSourceResolutionState.Resolved, resolution.State);
        Assert.Equal("copilot-cli", resolution.SourceFamily);
        Assert.True(resolution.CliCandidateObserved);
        Assert.False(resolution.VsCodeCandidateObserved);
    }

    [Fact]
    public void Resolve_OpposingRecognisedEvidenceInOneBlockConflicts()
    {
        var resolution = Assert.Single(OtlpTraceSourceResolver.Resolve(Payload(
            Resource(
                TraceA,
                Attribute("client.kind", "copilot-cli"),
                Attribute("service.name", "copilot-chat")))));

        Assert.Equal(TraceSourceResolutionState.Conflicting, resolution.State);
        Assert.Null(resolution.SourceFamily);
        Assert.True(resolution.CliCandidateObserved);
        Assert.True(resolution.VsCodeCandidateObserved);
    }

    [Fact]
    public void Resolve_OpposingBlocksAndRecordOrderAlwaysConflict()
    {
        var cli = Payload(Resource(TraceA, Attribute("service.name", "github-copilot")));
        var vscode = Payload(Resource(TraceA, Attribute("service.name", "copilot-chat")));

        var forward = Assert.Single(OtlpTraceSourceResolver.Resolve([cli, vscode]));
        var reverse = Assert.Single(OtlpTraceSourceResolver.Resolve([vscode, cli]));

        Assert.Equal(TraceSourceResolutionState.Conflicting, forward.State);
        Assert.Equal(forward.State, reverse.State);
        Assert.Equal(forward.SourceFamily, reverse.SourceFamily);
        Assert.Equal(forward.CliCandidateObserved, reverse.CliCandidateObserved);
        Assert.Equal(forward.VsCodeCandidateObserved, reverse.VsCodeCandidateObserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_UnknownEvidenceTakesPrecedenceOverOneRecognisedFamily(bool includeRecognised)
    {
        var attributes = new List<string>
        {
            Attribute("service.name", "github-copilot-preview"),
        };
        if (includeRecognised)
        {
            attributes.Add(Attribute("client.kind", "copilot-cli"));
        }

        var resolution = Assert.Single(OtlpTraceSourceResolver.Resolve(Payload(
            Resource(TraceA, attributes.ToArray()))));

        Assert.Equal(TraceSourceResolutionState.Unrecognised, resolution.State);
        Assert.Null(resolution.SourceFamily);
        Assert.True(resolution.UnknownCandidateObserved);
    }

    [Theory]
    [InlineData("service.version", "1.0.75")]
    [InlineData("gen_ai.agent.id", "synthetic-agent")]
    [InlineData("gen_ai.request.model", "synthetic-model")]
    [InlineData("vcs.repository.name", "synthetic-repository")]
    [InlineData("file.path", "synthetic-path")]
    [InlineData("event.time", "2026-07-30T00:00:00Z")]
    [InlineData("service.Name", "github-copilot")]
    public void Resolve_IrrelevantAndUnknownAttributeKeysRemainMissing(
        string key,
        string value)
    {
        var resolution = Assert.Single(OtlpTraceSourceResolver.Resolve(Payload(
            Resource(TraceA, Attribute(key, value)))));

        Assert.Equal(TraceSourceResolutionState.Missing, resolution.State);
        Assert.Null(resolution.SourceFamily);
        Assert.False(resolution.RelevantEvidenceObserved);
    }

    [Theory]
    [InlineData("client.kind", "Copilot-Cli")]
    [InlineData("client.kind", "copilot-cli-preview")]
    [InlineData("client.kind", "preview-copilot-cli")]
    [InlineData("client.kind", "prefix-copilot-cli-suffix")]
    [InlineData("service.name", "GitHub-Copilot")]
    [InlineData("service.name", "github-copilot-preview")]
    [InlineData("service.name", "preview-github-copilot")]
    [InlineData("service.name", "prefix-github-copilot-suffix")]
    public void Resolve_CasePrefixSuffixAndSubstringCandidatesAreUnrecognised(string key, string value)
    {
        var resolution = Assert.Single(OtlpTraceSourceResolver.Resolve(Payload(
            Resource(TraceA, Attribute(key, value)))));

        Assert.Equal(TraceSourceResolutionState.Unrecognised, resolution.State);
        Assert.Null(resolution.SourceFamily);
    }

    [Fact]
    public void Resolve_MultipleTracesInOneRecordRemainResourceScoped()
    {
        var payload = Payload(
            Resource(TraceA, Attribute("service.name", "github-copilot")),
            Resource(TraceB, Attribute("service.name", "copilot-chat")));

        var resolutions = OtlpTraceSourceResolver.Resolve(payload)
            .ToDictionary(item => item.TraceId, StringComparer.Ordinal);

        Assert.Equal("copilot-cli", resolutions[TraceA].SourceFamily);
        Assert.Equal("vscode-copilot-chat", resolutions[TraceB].SourceFamily);
    }

    [Fact]
    public void Resolve_ResourceOrderAndDuplicateRecordsDoNotChangeEvidence()
    {
        var first = Resource(TraceA, Attribute("client.kind", "copilot-cli"));
        var second = Resource(TraceA, Attribute("service.name", "github-copilot"));

        var forward = Assert.Single(OtlpTraceSourceResolver.Resolve(
            [Payload(first, second), Payload(first)]));
        var reverse = Assert.Single(OtlpTraceSourceResolver.Resolve(
            [Payload(second), Payload(second, first)]));

        Assert.Equal(TraceSourceResolutionState.Resolved, forward.State);
        Assert.Equal("copilot-cli", forward.SourceFamily);
        Assert.Equal(forward.State, reverse.State);
        Assert.Equal(forward.SourceFamily, reverse.SourceFamily);
        Assert.Equal(forward.CliCandidateObserved, reverse.CliCandidateObserved);
        Assert.Equal(forward.VsCodeCandidateObserved, reverse.VsCodeCandidateObserved);
        Assert.Equal(forward.UnknownCandidateObserved, reverse.UnknownCandidateObserved);
        Assert.Equal(forward.RelevantEvidenceObserved, reverse.RelevantEvidenceObserved);
    }

    private static string Payload(params string[] resources) =>
        "{\"resourceSpans\":[" + string.Join(",", resources) + "]}";

    private static string Resource(string traceId, params string[] attributes) =>
        "{\"resource\":{\"attributes\":[" + string.Join(",", attributes) +
        "]},\"scopeSpans\":[{\"spans\":[{\"traceId\":\"" + traceId +
        "\",\"spanId\":\"1111111111111111\"}]}]}";

    private static string Attribute(string key, string value) =>
        "{\"key\":\"" + key + "\",\"value\":{\"stringValue\":\"" + value + "\"}}";
}

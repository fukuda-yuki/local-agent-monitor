using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorProjectionBuilderTests
{
    [Fact]
    public void Build_DerivesSanitizedTraceContributionFields()
    {
        var record = Record("trace-a", ChatToolErrorPayload);

        var projection = MonitorProjectionBuilder.Build(record);

        Assert.Equal("trace-a", projection.TraceId);
        Assert.Equal("vscode-copilot-chat", projection.ClientKind);
        Assert.Equal(3, projection.SpanCount);

        var contribution = Assert.Single(projection.TraceContributions);
        Assert.Equal("trace-a", contribution.TraceId);
        Assert.Equal("vscode-copilot-chat", contribution.ClientKind);
        Assert.Equal("exp-1", contribution.ExperimentId);
        Assert.Equal("task-1", contribution.TaskId);
        Assert.Equal("refactor", contribution.TaskCategory);
        Assert.Equal("v2", contribution.AgentVariant);
        Assert.Equal("p3", contribution.PromptVersion);
        Assert.Equal(3, contribution.SpanCount);
        Assert.Equal(1, contribution.ToolCallCount);
        Assert.Equal(1, contribution.ErrorCount);
        Assert.Equal("copilot-agent-observability", contribution.RepositoryName);
        Assert.Equal("codex-workspace", contribution.WorkspaceLabel);
        Assert.Equal("sprint16-m2", contribution.RepoSnapshot);
    }

    [Fact]
    public void Build_MissingRepositoryMetadataProjectsNulls()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-no-metadata","spanId":"1111111111111111","name":"chat gpt-4o"}
        ]}]}]}
        """;
        var record = Record("trace-no-metadata", payload);

        var projection = MonitorProjectionBuilder.Build(record);

        var contribution = Assert.Single(projection.TraceContributions);
        Assert.Null(contribution.RepositoryName);
        Assert.Null(contribution.WorkspaceLabel);
        Assert.Null(contribution.RepoSnapshot);
    }

    [Fact]
    public void Build_DropsUnsafeRepositoryMetadataValues()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"repo.name","value":{"stringValue":"leak@example.com"}},
          {"key":"workspace.name","value":{"stringValue":"C:\\Users\\person\\secret"}},
          {"key":"repo.snapshot","value":{"stringValue":"Bearer token-marker"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-unsafe","spanId":"1111111111111111","name":"chat gpt-4o"}
        ]}]}]}
        """;
        var record = Record("trace-unsafe", payload);

        var projection = MonitorProjectionBuilder.Build(record);

        var contribution = Assert.Single(projection.TraceContributions);
        Assert.Null(contribution.RepositoryName);
        Assert.Null(contribution.WorkspaceLabel);
        Assert.Null(contribution.RepoSnapshot);
    }

    [Fact]
    public void Build_DoesNotCopyRawContentOrPii()
    {
        // Synthetic raw/PII markers (never real captured payloads): the projection
        // must prove it never copies prompt/response/tool content or user identity.
        const string promptMarker = "SECRET_PROMPT_TEXT_MARKER";
        const string toolMarker = "SECRET_TOOL_ARGS_MARKER";
        const string userIdMarker = "USER-ID-SECRET-MARKER";
        const string emailMarker = "leak-marker@example.com";

        var record = Record("trace-pii", RawAndPiiPayload);

        var projection = MonitorProjectionBuilder.Build(record);
        var serialized = JsonSerializer.Serialize(projection);

        Assert.DoesNotContain(promptMarker, serialized);
        Assert.DoesNotContain(toolMarker, serialized);
        Assert.DoesNotContain(userIdMarker, serialized);
        Assert.DoesNotContain(emailMarker, serialized);
    }

    [Fact]
    public void Build_UnknownSpanNamesDoNotThrowAndStillProduceTrace()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-x","spanId":"1111111111111111","name":"totally.unknown.span.kind"}
        ]}]}]}
        """;
        var record = Record("trace-x", payload);

        var projection = MonitorProjectionBuilder.Build(record);

        var contribution = Assert.Single(projection.TraceContributions);
        Assert.Equal("trace-x", contribution.TraceId);
        Assert.Equal(1, contribution.SpanCount);
    }

    [Fact]
    public void Build_MultiTracePayloadFansOutToOneContributionPerTrace()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"repo.name","value":{"stringValue":"shared-repository"}},
          {"key":"workspace.name","value":{"stringValue":"shared-workspace"}},
          {"key":"repo.snapshot","value":{"stringValue":"shared-snapshot"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-1","spanId":"1111111111111111","name":"chat gpt-4o"},
          {"traceId":"trace-1","spanId":"2222222222222222","name":"execute_tool"},
          {"traceId":"trace-2","spanId":"3333333333333333","name":"chat gpt-4o"}
        ]}]}]}
        """;
        var record = Record("trace-1", payload);

        var projection = MonitorProjectionBuilder.Build(record);

        Assert.Equal(3, projection.SpanCount);
        Assert.Equal(2, projection.TraceContributions.Count);
        var first = Assert.Single(projection.TraceContributions, c => c.TraceId == "trace-1");
        var second = Assert.Single(projection.TraceContributions, c => c.TraceId == "trace-2");
        Assert.Equal(2, first.SpanCount);
        Assert.Equal(1, first.ToolCallCount);
        Assert.Equal(1, second.SpanCount);
        Assert.Equal("shared-repository", first.RepositoryName);
        Assert.Equal("shared-workspace", first.WorkspaceLabel);
        Assert.Equal("shared-snapshot", first.RepoSnapshot);
        Assert.Equal("shared-repository", second.RepositoryName);
        Assert.Equal("shared-workspace", second.WorkspaceLabel);
        Assert.Equal("shared-snapshot", second.RepoSnapshot);
    }

    [Fact]
    public void Build_PayloadWithoutTraceIdProducesNullTraceAndNoContributions()
    {
        var payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"spanId":"1111111111111111","name":"chat gpt-4o"}
        ]}]}]}
        """;
        var record = Record(traceId: null, payload);

        var projection = MonitorProjectionBuilder.Build(record);

        Assert.Null(projection.TraceId);
        Assert.Empty(projection.TraceContributions);
        Assert.Equal(1, projection.SpanCount);
    }

    private static RawTelemetryRecord Record(string? traceId, string payloadJson) =>
        new(
            Id: 1,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);

    private const string ChatToolErrorPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"experiment.id","value":{"stringValue":"exp-1"}},
          {"key":"task.id","value":{"stringValue":"task-1"}},
          {"key":"task.category","value":{"stringValue":"refactor"}},
          {"key":"agent.variant","value":{"stringValue":"v2"}},
          {"key":"prompt.version","value":{"stringValue":"p3"}},
          {"key":"repo.name","value":{"stringValue":"copilot-agent-observability"}},
          {"key":"workspace.name","value":{"stringValue":"codex-workspace"}},
          {"key":"repo.snapshot","value":{"stringValue":"sprint16-m2"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-a","spanId":"1111111111111111","name":"chat gpt-4o"},
          {"traceId":"trace-a","spanId":"2222222222222222","name":"execute_tool"},
          {"traceId":"trace-a","spanId":"3333333333333333","name":"do-thing","status":{"code":"STATUS_CODE_ERROR"}}
        ]}]}]}
        """;

    private const string RawAndPiiPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.id","value":{"stringValue":"USER-ID-SECRET-MARKER"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-pii","spanId":"1111111111111111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;
}

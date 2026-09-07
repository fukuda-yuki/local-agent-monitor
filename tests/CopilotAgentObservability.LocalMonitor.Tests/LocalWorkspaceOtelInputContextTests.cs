using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceOtelInputContextTests
{
    [Fact]
    public void SelectInput_ExactCallIncludesFullContextWithoutOtherCallContent()
    {
        var instruction = new string('x', 500) + "<script>inert</script>";
        var messages = JsonSerializer.Serialize(new[]
        {
            new { role = "developer", content = "captured developer" },
            new { role = "user", content = instruction },
            new { role = "user", content = "additional instruction" },
        });
        var result = LocalWorkspaceOtelInputContext.SelectInput(Payload(
            Span("a", [Attribute("gen_ai.system_instructions", "captured system"), Attribute("gen_ai.input.messages", messages)]),
            Span("b", [Attribute("gen_ai.input.messages", "foreign call content")])), "trace", "a");

        Assert.Equal(LocalWorkspaceNodeContentReadDisposition.Granted, result.Failure);
        var text = Encoding.UTF8.GetString(result.Bytes!);
        Assert.Contains(messages, text);
        Assert.Contains("captured system", text);
        Assert.DoesNotContain("foreign call content", text);
    }

    [Fact]
    public void SelectInput_MissingConflictingAndOversizedContextRemainUnavailable()
    {
        var missing = LocalWorkspaceOtelInputContext.SelectInput(Payload(Span("a", [])), "trace", "a");
        Assert.Null(missing.Bytes);
        Assert.Equal(LocalWorkspaceNodeContentReadDisposition.NotCaptured, missing.Failure);

        var duplicate = LocalWorkspaceOtelInputContext.SelectInput(Payload(Span("a",
            [Attribute("gen_ai.input.messages", "first"), Attribute("gen_ai.input.messages", "second")])), "trace", "a");
        Assert.Null(duplicate.Bytes);
        Assert.Equal(LocalWorkspaceNodeContentReadDisposition.Unavailable, duplicate.Failure);

        var conflicting = LocalWorkspaceOtelInputContext.SelectInput(Payload(
            Span("a", [Attribute("gen_ai.input.messages", "first")]),
            Span("a", [Attribute("gen_ai.input.messages", "second")])), "trace", "a");
        Assert.Null(conflicting.Bytes);
        Assert.Equal(LocalWorkspaceNodeContentReadDisposition.Unavailable, conflicting.Failure);

        var oversized = LocalWorkspaceOtelInputContext.SelectInput(Payload(Span("a",
            [Attribute("gen_ai.input.messages", new string('a', 1_048_577))])), "trace", "a");
        Assert.Null(oversized.Bytes);
        Assert.Equal(LocalWorkspaceNodeContentReadDisposition.Oversized, oversized.Failure);
    }

    private static object Attribute(string key, string text) => new { key, value = new { stringValue = text } };
    private static object Span(string id, object[] attributes) => new { traceId = "trace", spanId = id, attributes };
    private static byte[] Payload(params object[] spans) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        resourceSpans = new[] { new { scopeSpans = new[] { new { spans } } } },
    });
}

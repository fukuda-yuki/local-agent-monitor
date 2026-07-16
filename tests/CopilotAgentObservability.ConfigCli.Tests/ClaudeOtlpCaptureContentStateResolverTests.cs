namespace CopilotAgentObservability.ConfigCli.Tests;

public class ClaudeOtlpCaptureContentStateResolverTests
{
    [Fact]
    public void Derive_InteractionSpanWithUserPromptAttribute_ReturnsAvailable()
    {
        var payload = Payload("""
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"1111111111111111","name":"claude_code.interaction",
             "attributes":[{"key":"user_prompt","value":{"stringValue":"synthetic-marker"}}]}
            """);

        Assert.Equal(SourceCaptureContentState.Available, ClaudeOtlpCaptureContentStateResolver.Derive(payload));
    }

    [Fact]
    public void Derive_RecognizedSpanWithoutGatedContentField_ReturnsNotCaptured()
    {
        var payload = Payload("""
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"1111111111111111","name":"claude_code.interaction",
             "attributes":[{"key":"session.id","value":{"stringValue":"synthetic-marker"}}]}
            """);

        Assert.Equal(SourceCaptureContentState.NotCaptured, ClaudeOtlpCaptureContentStateResolver.Derive(payload));
    }

    [Fact]
    public void Derive_ToolSpanWithToolOutputEvent_ReturnsAvailable()
    {
        var payload = Payload("""
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"2222222222222222","name":"claude_code.tool",
             "events":[{"name":"tool.output","attributes":[{"key":"content","value":{"stringValue":"synthetic-marker"}}]}]}
            """);

        Assert.Equal(SourceCaptureContentState.Available, ClaudeOtlpCaptureContentStateResolver.Derive(payload));
    }

    [Fact]
    public void Derive_ToolSpanWithFilePathAttribute_ReturnsAvailable()
    {
        var payload = Payload("""
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"2222222222222222","name":"claude_code.tool",
             "attributes":[{"key":"file_path","value":{"stringValue":"synthetic-marker"}}]}
            """);

        Assert.Equal(SourceCaptureContentState.Available, ClaudeOtlpCaptureContentStateResolver.Derive(payload));
    }

    [Fact]
    public void Derive_ToolExecutionSpanWithErrorAttributeOnly_ReturnsNotCaptured()
    {
        var payload = Payload("""
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"3333333333333333","name":"claude_code.tool.execution",
             "attributes":[{"key":"error","value":{"stringValue":"synthetic-marker"}}]}
            """);

        Assert.Equal(SourceCaptureContentState.NotCaptured, ClaudeOtlpCaptureContentStateResolver.Derive(payload));
    }

    [Fact]
    public void Derive_NoRecognizedClaudeSpans_ReturnsNull()
    {
        var payload = Payload("""
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"4444444444444444","name":"chat gpt-4o",
             "attributes":[]}
            """);

        Assert.Null(ClaudeOtlpCaptureContentStateResolver.Derive(payload));
    }

    [Fact]
    public void Derive_UserPromptOnForeignSpanNameOnly_ReturnsNull()
    {
        var payload = Payload("""
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"5555555555555555","name":"chat gpt-4o",
             "attributes":[{"key":"user_prompt","value":{"stringValue":"synthetic-marker"}}]}
            """);

        Assert.Null(ClaudeOtlpCaptureContentStateResolver.Derive(payload));
    }

    [Fact]
    public void Derive_UserPromptOnForeignSpanAlongsideContentFreeClaudeSpan_ReturnsNotCaptured()
    {
        var payload = TwoSpanPayload(
            """
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"5555555555555555","name":"chat gpt-4o",
             "attributes":[{"key":"user_prompt","value":{"stringValue":"synthetic-marker"}}]}
            """,
            """
            {"traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","spanId":"6666666666666666","name":"claude_code.interaction",
             "attributes":[]}
            """);

        Assert.Equal(SourceCaptureContentState.NotCaptured, ClaudeOtlpCaptureContentStateResolver.Derive(payload));
    }

    private static string Payload(string span) =>
        "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[" + span + "]}]}]}";

    private static string TwoSpanPayload(string spanA, string spanB) =>
        "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[" + spanA + "," + spanB + "]}]}]}";
}

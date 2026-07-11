using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.HookForwarding;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HookForwarderTests
{
    [Theory]
    [InlineData("SessionStart", "SessionStart")]
    [InlineData("UserPromptSubmit", "UserPromptSubmit")]
    [InlineData("PreToolUse", "PreToolUse")]
    [InlineData("PostToolUse", "PostToolUse")]
    [InlineData("SubagentStart", "SubagentStart")]
    [InlineData("SubagentStop", "SubagentStop")]
    [InlineData("Stop", "Stop")]
    public async Task SupportedHookIsForwardedAsCanonicalV1Envelope(string hookName, string eventType)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var payload = $$"""
            {"session_id":"native-1","hook_event_name":"{{hookName}}","timestamp":"2026-07-11T01:02:03Z","source_surface":"copilot-cli","prompt":"hello"}
            """;

        var result = await RunAsync(payload, handler: handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.NotNull(handler.Request);
        Assert.Equal(new Uri("http://127.0.0.1:4320/api/session-ingest/v1/events"), handler.Request!.RequestUri);
        Assert.Equal("1", handler.Request.Headers.GetValues("X-CAO-Session-Event-Version").Single());
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("copilot-compatible-hook", root.GetProperty("source_adapter").GetString());
        Assert.Equal("copilot-cli", root.GetProperty("source_surface").GetString());
        Assert.Equal("native-1", root.GetProperty("native_session_id").GetString());
        var @event = root.GetProperty("events")[0];
        Assert.Equal(eventType, @event.GetProperty("type").GetString());
        Assert.Equal("2026-07-11T01:02:03.0000000+00:00", @event.GetProperty("occurred_at").GetString());
        Assert.Equal(64, @event.GetProperty("source_event_id").GetString()!.Length);
    }

    [Fact]
    public async Task AmbiguousSurfaceRemainsHookUnknownAndSecretsAreRemovedRecursively()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        const string payload = """
            {"sessionId":"native-2","eventName":"PreToolUse","transcript_path":"C:\\secret\\transcript.json","payload":{"authorization":"Bearer abc","nested":{"api_key":"sk-secret"},"safe":"kept","note":"embedded github_pat_sensitive"}}
            """;

        var result = await RunAsync(payload, handler: handler);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(handler.Body!);
        Assert.Equal("hook-unknown", document.RootElement.GetProperty("source_surface").GetString());
        var forwarded = document.RootElement.GetProperty("events")[0].GetProperty("payload").GetRawText();
        Assert.DoesNotContain("authorization", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-secret", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("github_pat_sensitive", forwarded, StringComparison.Ordinal);
        Assert.Contains("kept", forwarded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericCredentialFieldsAndEmbeddedAssignmentsAreRedacted()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        const string payload = """
            {"session_id":"native-secret","hook_event_name":"PreToolUse","payload":{"token":"plain-token","clientCredential":"plain-credential","notes":["password=hunter2","TOKEN: abc123","api_key: sk-embedded","safe=value"]}}
            """;

        await RunAsync(payload, handler: handler);

        var forwarded = handler.Body!;
        Assert.DoesNotContain("plain-token", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-credential", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-embedded", forwarded, StringComparison.Ordinal);
        Assert.Contains("safe=value", forwarded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalSourceIdIsIndependentOfPropertyOrder()
    {
        var first = new RecordingHandler(HttpStatusCode.NoContent);
        var second = new RecordingHandler(HttpStatusCode.NoContent);

        await RunAsync("{\"session_id\":\"s\",\"hook_event_name\":\"Stop\",\"value\":1}", handler: first);
        await RunAsync("{\"value\":1,\"hook_event_name\":\"Stop\",\"session_id\":\"s\"}", handler: second);

        Assert.Equal(ReadEventId(first.Body!), ReadEventId(second.Body!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"session_id\":\"s\",\"hook_event_name\":\"Unknown\"}")]
    public async Task InvalidInputAlwaysSucceedsSilentlyWithoutRequest(string stdin)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);

        var result = await RunAsync(stdin, handler: handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://192.0.2.10:4320")]
    [InlineData("not-a-url")]
    public async Task NonLoopbackOrInvalidEndpointAlwaysSucceedsSilently(string endpoint)
    {
        var result = await RunAsync("{\"session_id\":\"s\",\"hook_event_name\":\"Stop\"}", endpoint);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4320/api/session-ingest/v1/events")]
    [InlineData("http://[::1]:4320")]
    public async Task QualifiedAndIpv6LoopbackEndpointsAreAccepted(string endpoint)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);

        var result = await RunAsync("{\"session_id\":\"s\",\"hook_event_name\":\"Stop\"}", endpoint, handler: handler);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(handler.Request);
        Assert.Equal("/api/session-ingest/v1/events", handler.Request!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("parent_event_id", 256, true)]
    [InlineData("parent_event_id", 257, false)]
    [InlineData("run_native_id", 256, true)]
    [InlineData("run_native_id", 257, false)]
    [InlineData("trace_id", 128, true)]
    [InlineData("trace_id", 129, false)]
    public async Task OptionalIdentifiersRespectV1Bounds(string propertyName, int length, bool shouldSend)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["session_id"] = "s",
            ["hook_event_name"] = "Stop",
            [propertyName] = new string('x', length),
        });

        await RunAsync(payload, handler: handler);

        Assert.Equal(shouldSend, handler.Request is not null);
    }

    [Fact]
    public async Task TimeoutDefaultsToExactly250MillisecondsWhenOptionIsOmitted()
    {
        Assert.Equal(250, HookForwardCommand.DefaultTimeoutMilliseconds);
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await HookForwardCommand.RunAsync(
            ["--endpoint", "http://127.0.0.1:4320"],
            new StringReader("{\"session_id\":\"s\",\"hook_event_name\":\"Stop\"}"),
            output,
            error,
            handler,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(handler.Request);
    }

    [Theory]
    [InlineData("--endpoint", "http://127.0.0.1:4320", "--endpoint", "http://localhost:4320")]
    [InlineData("--endpoint", "http://127.0.0.1:4320", "--timeout-ms", "250", "--timeout-ms", "300")]
    [InlineData("--endpoint")]
    public async Task MalformedOrDuplicateOptionsFailOpenWithoutRequest(params string[] args)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await HookForwardCommand.RunAsync(
            args,
            new StringReader("{\"session_id\":\"s\",\"hook_event_name\":\"Stop\"}"),
            output,
            error,
            handler,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(handler.Request);
        Assert.Empty(output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task NetworkFailureAlwaysSucceedsSilently()
    {
        var result = await RunAsync(
            "{\"session_id\":\"s\",\"hook_event_name\":\"Stop\"}",
            handler: new ThrowingHandler());

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task TimeoutAlwaysSucceedsSilentlyAndUsesConfiguredBudget()
    {
        var handler = new BlockingHandler();
        var result = await RunAsync(
            "{\"session_id\":\"s\",\"hook_event_name\":\"Stop\"}",
            timeoutMs: 20,
            handler: handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.True(handler.WasCancelled);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string input,
        string endpoint = "http://127.0.0.1:4320",
        int timeoutMs = 250,
        HttpMessageHandler? handler = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await HookForwardCommand.RunAsync(
            ["--endpoint", endpoint, "--timeout-ms", timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            new StringReader(input),
            output,
            error,
            handler,
            CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static string ReadEventId(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("events")[0].GetProperty("source_event_id").GetString()!;
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("collector unavailable");
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public bool WasCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.HookForwarding;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HookForwarderTests
{
    private static readonly DateTimeOffset ClaudeCaptureTime = DateTimeOffset.Parse("2026-07-13T12:34:56Z");

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
    [InlineData("--endpoint", "http://127.0.0.1:4320", "--timeout-ms", "0")]
    [InlineData("--endpoint", "http://127.0.0.1:4320", "--timeout-ms", "invalid")]
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
        var handler = new ThrowingHandler();
        var result = await RunAsync(
            "{\"session_id\":\"s\",\"hook_event_name\":\"Stop\"}",
            handler: handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(1, handler.Attempts);
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
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ClaudeSelector_ForwardsOfficialFixtureWithCallerProvenance(bool sourceVersion, bool schemaFingerprint)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var args = new List<string>
        {
            "--endpoint", "http://127.0.0.1:4320",
            "--timeout-ms", "250",
            "--source", "claude-code",
        };
        if (sourceVersion) args.AddRange(["--source-version", "3.4.5-caller"]);
        if (schemaFingerprint) args.AddRange(["--schema-fingerprint", new string('a', 64)]);

        var result = await RunWithArgsAsync(ReadClaudeFixture("session-end.json"), args.ToArray(), handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(1, handler.Attempts);
        Assert.NotNull(handler.Request);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("claude-code-hook", root.GetProperty("source_adapter").GetString());
        Assert.Equal("claude-code", root.GetProperty("source_surface").GetString());
        Assert.Equal(sourceVersion ? "3.4.5-caller" : null, root.GetProperty("source_application_version").GetString());
        Assert.Equal("claude-hook-v1", root.GetProperty("adapter_version").GetString());
        Assert.Equal(schemaFingerprint ? new string('a', 64) : null, root.GetProperty("schema_fingerprint").GetString());
        Assert.Equal("session-normalization-v1", root.GetProperty("normalization_version").GetString());
        var mappedEvent = root.GetProperty("events")[0];
        Assert.Equal("SessionEnd", mappedEvent.GetProperty("type").GetString());
        Assert.Equal(ClaudeCaptureTime, mappedEvent.GetProperty("occurred_at").GetDateTimeOffset());
        Assert.Equal("SYNTHETIC_TRANSCRIPT_PATH", mappedEvent.GetProperty("payload").GetProperty("transcript_path").GetString());
        Assert.Equal(64, mappedEvent.GetProperty("source_event_id").GetString()!.Length);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task ClaudeSelector_AcceptsOptionsInAnyOrder()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var fingerprint = new string('b', 64);

        var result = await RunWithArgsAsync(
            ReadClaudeFixture("session-end.json"),
            [
                "--schema-fingerprint", fingerprint,
                "--timeout-ms", "250",
                "--source", "claude-code",
                "--endpoint", "http://127.0.0.1:4320",
                "--source-version", "3.4.5-reordered",
            ],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(1, handler.Attempts);
        using var document = JsonDocument.Parse(handler.Body!);
        Assert.Equal("3.4.5-reordered", document.RootElement.GetProperty("source_application_version").GetString());
        Assert.Equal(fingerprint, document.RootElement.GetProperty("schema_fingerprint").GetString());
    }

    [Theory]
    [InlineData("bad version", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("3.4.5-valid", "ABCDEF")]
    public async Task ClaudeSelector_BothProvenanceValuesWithEitherInvalid_RejectsWithoutHttpAttempt(
        string sourceVersion,
        string schemaFingerprint)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);

        var result = await RunWithArgsAsync(
            ReadClaudeFixture("session-end.json"),
            [
                "--endpoint", "http://127.0.0.1:4320",
                "--source", "claude-code",
                "--source-version", sourceVersion,
                "--schema-fingerprint", schemaFingerprint,
            ],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, handler.Attempts);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData("--source", "claude-code")]
    [InlineData("--source", "Claude-Code", "--source-version", "3.4.5")]
    [InlineData("--source", "copilot", "--source-version", "3.4.5")]
    [InlineData("--source-version", "3.4.5")]
    [InlineData("--schema-fingerprint", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("--source", "claude-code", "--source-version", "bad version")]
    [InlineData("--source", "claude-code", "--schema-fingerprint", "ABCDEF")]
    [InlineData("--source", "claude-code", "--unknown", "value")]
    [InlineData("--source", "claude-code", "--source", "claude-code", "--source-version", "3.4.5")]
    [InlineData("--source", "claude-code", "--source-version", "3.4.5", "--source-version", "3.4.6")]
    [InlineData("--source", "claude-code", "--schema-fingerprint", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--schema-fingerprint", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task InvalidSelectorOrProvenance_FailsOpenSilentlyWithoutHttpAttempt(params string[] extraArgs)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var args = new[] { "--endpoint", "http://127.0.0.1:4320", "--timeout-ms", "250" }.Concat(extraArgs).ToArray();

        var result = await RunWithArgsAsync(ReadClaudeFixture("session-end.json"), args, handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Null(handler.Request);
        Assert.Equal(0, handler.Attempts);
    }

    [Theory]
    [InlineData("unsupported-event.json")]
    public async Task ClaudeInvalidProducerPayload_FailsOpenSilentlyWithoutHttpAttempt(string fixtureName)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);

        var result = await RunWithArgsAsync(
            ReadClaudeFixture(fixtureName),
            ["--endpoint", "http://127.0.0.1:4320", "--source", "claude-code", "--source-version", "3.4.5-caller"],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task ClaudePayloadProvenanceCannotReplaceMissingOutOfBandProvenance()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var payload = ReadClaudeFixture("session-end.json").Replace(
            "\"reason\": \"other\"",
            "\"reason\": \"other\", \"source_application_version\": \"payload-must-not-win\"",
            StringComparison.Ordinal);

        var result = await RunWithArgsAsync(
            payload,
            ["--endpoint", "http://127.0.0.1:4320", "--source", "claude-code"],
            handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task LegacyCopilotWithoutSelectorPreservesExistingEnvelopeAndSanitization()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        const string payload = """
            {"session_id":"legacy","hook_event_name":"Stop","source_surface":"vscode","payload":{"token":"must-not-leak","safe":"kept"}}
            """;

        var result = await RunAsync(payload, handler: handler);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("copilot-compatible-hook", root.GetProperty("source_adapter").GetString());
        Assert.Equal("vscode", root.GetProperty("source_surface").GetString());
        Assert.False(root.TryGetProperty("adapter_version", out _));
        var forwarded = root.GetProperty("events")[0].GetProperty("payload").GetRawText();
        Assert.DoesNotContain("must-not-leak", forwarded, StringComparison.Ordinal);
        Assert.Contains("kept", forwarded, StringComparison.Ordinal);
        Assert.Equal(1, handler.Attempts);
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

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunWithArgsAsync(
        string input,
        string[] args,
        HttpMessageHandler handler)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await HookForwardCommand.RunAsync(
            args,
            new StringReader(input),
            output,
            error,
            handler,
            CancellationToken.None,
            new FixedTimeProvider(ClaudeCaptureTime));
        return (exitCode, output.ToString(), error.ToString());
    }

    private static string ReadClaudeFixture(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "TestData", "Claude", "hooks", name));

    private static string ReadEventId(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("events")[0].GetProperty("source_event_id").GetString()!;
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new HttpRequestException("collector unavailable");
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public bool WasCancelled { get; private set; }
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
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

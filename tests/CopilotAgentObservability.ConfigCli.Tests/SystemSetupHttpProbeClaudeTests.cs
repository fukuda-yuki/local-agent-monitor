using System.Net;
using System.Net.Sockets;
using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

[Collection(nameof(SetupLoopbackHttpCollection))]
public sealed class SystemSetupHttpProbeClaudeTests
{
    private const string CanonicalReadyBody =
        "{\"status\":\"ready\",\"checks\":{" +
        "\"loopback_bound\":true," +
        "\"db_open\":true," +
        "\"migration_complete\":true," +
        "\"writer_running\":true," +
        "\"projection_worker_running\":true," +
        "\"ingestion_accepting\":true," +
        "\"projection_lag_seconds\":0," +
        "\"projection_backlog\":0," +
        "\"span_projection_lag_seconds\":0," +
        "\"span_projection_backlog\":0," +
        "\"projection_failure_count\":0}," +
        "\"degraded_reasons\":[]}";

    [Fact]
    public async Task HealthReady_ReachesClaudeReadinessProbeThroughProductionHttpSeam()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var origin = $"http://127.0.0.1:{endpoint.Port}";
        using var cancellation = new CancellationTokenSource();
        var responseTask = RespondOnceAsync(listener, CanonicalReadyBody, cancellation.Token);
        var platform = new SystemSetupPlatform();

        var detection = await Task.Run(() => ClaudeCodeReadinessProbe.Probe(
            platform,
            origin,
            ClaudeCodeExecutionContext.WindowsNative));
        if (!detection.Reachable)
        {
            cancellation.Cancel();
        }

        var requestedPath = await ObserveRequestedPathAsync(responseTask);

        Assert.True(detection.Reachable);
        Assert.Equal("/health/ready", requestedPath);
    }

    [Fact]
    public async Task UnknownPath_ReturnsTransportFailureWithoutConnecting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var origin = $"http://127.0.0.1:{endpoint.Port}";
        using var cancellation = new CancellationTokenSource();
        var responseTask = RespondOnceAsync(listener, "ok", cancellation.Token);
        var platform = new SystemSetupPlatform();

        var observation = await Task.Run(() => platform.HttpProbe.Get(
            origin,
            "/health/unknown",
            totalBudgetMilliseconds: 500,
            maxBodyBytes: 4096));
        cancellation.Cancel();
        var requestedPath = await ObserveRequestedPathAsync(responseTask);

        Assert.Equal(SetupHttpProbeOutcome.TransportFailure, observation.Outcome);
        Assert.Null(requestedPath);
    }

    private static async Task<string?> ObserveRequestedPathAsync(Task<string> responseTask)
    {
        try
        {
            return await responseTask;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> RespondOnceAsync(
        TcpListener listener,
        string body,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var request = new byte[4096];
        var length = 0;
        while (length < request.Length && !HasHeaderTerminator(request.AsSpan(0, length)))
        {
            var read = await stream.ReadAsync(
                request.AsMemory(length, request.Length - length),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        var requestLine = Encoding.ASCII.GetString(request, 0, length).Split("\r\n", 2)[0];
        var requestedPath = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        var response = new byte[headers.Length + bodyBytes.Length];
        headers.CopyTo(response, 0);
        bodyBytes.CopyTo(response, headers.Length);
        await stream.WriteAsync(response, cancellationToken);
        return requestedPath;
    }

    private static bool HasHeaderTerminator(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf("\r\n\r\n"u8) >= 0;
}

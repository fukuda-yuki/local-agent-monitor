using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

internal sealed class FakeSkillRuntimeClient : ICopilotSkillRuntimeClient
{
    private string? recordedSessionStartCopilotVersion;

    public bool StartThrows { get; set; }
    public bool GetStatusThrows { get; set; }
    public CopilotRuntimeStatusObservationV1? StatusResult { get; set; }
    public Action? AfterStatusCallback { get; set; }
    public bool DiscoverThrows { get; set; }
    public IReadOnlyList<CopilotDiscoveredSkillFactV1>? DiscoverResult { get; set; }
    public Action<CancellationToken>? DiscoveringCallback { get; set; }

    public int StartCalls { get; private set; }
    public int GetStatusCalls { get; private set; }
    public int DiscoverCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool DisposeThrows { get; set; }
    public IReadOnlyList<string>? LastProjectPaths { get; private set; }
    public IReadOnlyList<string>? LastSkillDirectories { get; private set; }
    public CancellationToken LastDiscoverToken { get; private set; }
    public string? RecordedSessionStartCopilotVersion => Volatile.Read(ref recordedSessionStartCopilotVersion);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCalls++;
        if (StartThrows)
        {
            throw new InvalidOperationException("synthetic start failure");
        }

        return Task.CompletedTask;
    }

    public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken)
    {
        GetStatusCalls++;
        if (GetStatusThrows)
        {
            throw new InvalidOperationException("synthetic status failure");
        }

        AfterStatusCallback?.Invoke();
        return Task.FromResult(StatusResult);
    }

    public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> skillDirectories,
        CancellationToken cancellationToken)
    {
        DiscoverCalls++;
        LastProjectPaths = projectPaths;
        LastSkillDirectories = skillDirectories;
        LastDiscoverToken = cancellationToken;
        DiscoveringCallback?.Invoke(cancellationToken);
        if (DiscoverThrows)
        {
            throw new InvalidOperationException("synthetic discovery failure");
        }

        return Task.FromResult(DiscoverResult);
    }

    public Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        => Task.FromResult<CopilotSession>(null!);

    public void RecordSessionStartCopilotVersion(string? copilotVersion)
        => Volatile.Write(ref recordedSessionStartCopilotVersion, copilotVersion);

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        if (DisposeThrows)
        {
            throw new InvalidOperationException("synthetic dispose failure");
        }

        return ValueTask.CompletedTask;
    }
}

public enum FakeBridgeTransportBehavior
{
    Accept,
    Refuse,
    Throw
}

internal sealed class FakeBridgeTransport : ISkillRuntimeBridgeTransport
{
    private readonly List<(string Token, byte[] Body)> sends = [];

    public FakeBridgeTransportBehavior NextBehavior { get; set; } = FakeBridgeTransportBehavior.Accept;
    public Action<string, ReadOnlyMemory<byte>, CancellationToken>? SendingCallback { get; set; }
    public IReadOnlyList<(string Token, byte[] Body)> Sends => sends;

    public Task<bool> SendAsync(string capabilityToken, ReadOnlyMemory<byte> bodyUtf8, CancellationToken cancellationToken)
    {
        SendingCallback?.Invoke(capabilityToken, bodyUtf8, cancellationToken);
        sends.Add((capabilityToken, bodyUtf8.ToArray()));
        if (NextBehavior == FakeBridgeTransportBehavior.Throw)
        {
            throw new InvalidOperationException("synthetic transport failure");
        }

        return Task.FromResult(NextBehavior == FakeBridgeTransportBehavior.Accept);
    }
}

internal sealed class ManualClock
{
    public long NowTicks { get; set; }

    public long Ticks() => NowTicks;
}

internal sealed class FixedTokenSource
{
    private readonly Queue<byte[]?> queued = new();
    private byte[]? fallback;

    public void Enqueue(params byte[]?[] tokens)
    {
        foreach (var token in tokens)
        {
            queued.Enqueue(token);
        }
    }

    public void SetFallback(byte[]? token) => fallback = token;

    public byte[]? Next() => queued.Count > 0 ? queued.Dequeue() : fallback;
}

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal enum SkillRuntimeBridgeForwardOutcome
{
    Forwarded,
    Unavailable
}

internal sealed class SkillRuntimeBridgeTransfer
{
    private readonly CopilotRuntimeOperationCapabilityV1 concreteCapability;
    private int releaseCalls;

    internal SkillRuntimeBridgeTransfer(
        CopilotRuntimeOperationCapabilityV1 capability,
        int expectedBodyLength,
        byte[] expectedBodySha256)
    {
        concreteCapability = capability;
        RuntimeCapability = capability;
        ExpectedBodyLength = expectedBodyLength;
        ExpectedBodySha256 = expectedBodySha256;
    }

    public ISkillInvocationV2RuntimeCapability RuntimeCapability { get; }

    public CancellationToken WorkToken => concreteCapability.WorkToken;

    public int ExpectedBodyLength { get; }

    internal byte[] ExpectedBodySha256 { get; }

    public bool TrySealV2NonCommitResponse() => concreteCapability.TrySealV2NonCommitResponse();

    public bool TrySealCommit() => concreteCapability.TrySealCommit();

    public bool TrySealReplaySuccess() => concreteCapability.TrySealReplaySuccess();

    public void ReleaseTransferredCapability()
    {
        if (Interlocked.Exchange(ref releaseCalls, 1) == 0)
        {
            concreteCapability.Release();
        }
    }
}

internal sealed class SkillRuntimeCapabilityBridgeV1
{
    internal const int MaxPendingEntries = 64;
    internal const int TokenByteLength = 32;
    internal const int TokenStringLength = 43;
    internal static readonly long EntryLifetimeTicks = TimeSpan.FromSeconds(30).Ticks;

    private readonly object sync = new();
    private readonly Dictionary<string, PendingEntry> pendingEntries = [];
    private readonly ISkillRuntimeBridgeTransport transport;
    private readonly Func<long> monotonicClockTicks;
    private readonly Func<byte[]?> randomTokenSource;

    public SkillRuntimeCapabilityBridgeV1(
        CopilotRuntimeAdmissionV1 admission,
        ISkillRuntimeBridgeTransport transport,
        Func<long> monotonicClockTicks,
        Func<byte[]?> randomTokenSource)
    {
        ArgumentNullException.ThrowIfNull(admission);
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.monotonicClockTicks = monotonicClockTicks ?? throw new ArgumentNullException(nameof(monotonicClockTicks));
        this.randomTokenSource = randomTokenSource ?? throw new ArgumentNullException(nameof(randomTokenSource));
        admission.RegisterInvalidationObserver((CopilotRuntimeGenerationV1 candidate) =>
            ClearPendingEntriesAndReleaseCapabilities(candidate));
    }

    internal int PendingCount
    {
        get
        {
            lock (sync)
            {
                return pendingEntries.Count;
            }
        }
    }

    internal static Func<long> CreateMonotonicClock()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        return () => checked((Stopwatch.GetTimestamp() - startTimestamp) * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
    }

    internal static byte[] CreateCryptographicToken() => RandomNumberGenerator.GetBytes(TokenByteLength);

    public async Task<SkillRuntimeBridgeForwardOutcome> ForwardPreparedBodyAsync(
        CopilotRuntimeGenerationV1 owningGeneration,
        ReadOnlyMemory<byte> preparedBodyUtf8,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(owningGeneration);
        if (callerToken.IsCancellationRequested || preparedBodyUtf8.IsEmpty
            || preparedBodyUtf8.Length > OwnedSessionPreparedBufferV1.MaxAggregateBodyBytes
            || !owningGeneration.TryAcquireOperationCapability(callerToken, out var capability))
        {
            return SkillRuntimeBridgeForwardOutcome.Unavailable;
        }

        var workToken = capability.WorkToken;
        var registered = false;
        string? token = null;
        try
        {
            var tokenBytes = randomTokenSource();
            if (tokenBytes is null || tokenBytes.Length != TokenByteLength) return SkillRuntimeBridgeForwardOutcome.Unavailable;
            token = EncodeBase64Url(tokenBytes);
            long expiresAtTicks;
            try { expiresAtTicks = checked(monotonicClockTicks() + EntryLifetimeTicks); }
            catch (OverflowException) { return SkillRuntimeBridgeForwardOutcome.Unavailable; }

            lock (sync)
            {
                PurgeExpiredUnderLock(monotonicClockTicks());
                if (pendingEntries.Count >= MaxPendingEntries || pendingEntries.ContainsKey(token))
                    return SkillRuntimeBridgeForwardOutcome.Unavailable;
                pendingEntries[token] = new PendingEntry(capability, preparedBodyUtf8.Length,
                    SHA256.HashData(preparedBodyUtf8.Span), expiresAtTicks);
                registered = true;
            }

            bool sent;
            try { sent = await transport.SendAsync(token, preparedBodyUtf8, workToken).ConfigureAwait(false); }
            catch { sent = false; }
            if (!sent || workToken.IsCancellationRequested)
            {
                RemoveUnconsumedEntry(token);
                return SkillRuntimeBridgeForwardOutcome.Unavailable;
            }

            return SkillRuntimeBridgeForwardOutcome.Forwarded;
        }
        finally
        {
            if (!registered) capability.Release();
        }
    }

    public bool TryConsume(string? capabilityHeader, [NotNullWhen(true)] out SkillRuntimeBridgeTransfer? transfer)
    {
        transfer = null;
        if (!IsValidTokenGrammar(capabilityHeader))
        {
            return false;
        }

        lock (sync)
        {
            PurgeExpiredUnderLock(monotonicClockTicks());

            if (!pendingEntries.TryGetValue(capabilityHeader!, out var entry))
            {
                return false;
            }

            pendingEntries.Remove(capabilityHeader!);
            if (entry.Capability.WorkToken.IsCancellationRequested)
            {
                entry.Capability.Release();
                return false;
            }

            transfer = new SkillRuntimeBridgeTransfer(entry.Capability, entry.ExpectedBodyLength, entry.ExpectedBodySha256);
            return true;
        }
    }

    public void PurgeExpired()
    {
        lock (sync)
        {
            PurgeExpiredUnderLock(monotonicClockTicks());
        }
    }

    public void ClearPendingEntriesAndReleaseCapabilities()
    {
        PendingEntry[] doomed;
        lock (sync)
        {
            doomed = [.. pendingEntries.Values];
            pendingEntries.Clear();
        }

        foreach (var entry in doomed)
        {
            entry.Capability.Release();
        }
    }

    private void ClearPendingEntriesAndReleaseCapabilities(CopilotRuntimeGenerationV1 candidate)
    {
        PendingEntry[] doomed;
        lock (sync)
        {
            doomed = [.. pendingEntries.Values.Where(entry => ReferenceEquals(entry.Capability.Owner, candidate))];
            foreach (var pair in pendingEntries.Where(pair => ReferenceEquals(pair.Value.Capability.Owner, candidate)).ToArray())
                pendingEntries.Remove(pair.Key);
        }
        foreach (var entry in doomed) entry.Capability.Release();
    }

    internal static bool IsValidTokenGrammar(string? token)
    {
        if (token is null || token.Length != TokenStringLength)
        {
            return false;
        }

        foreach (var c in token)
        {
            var valid = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    internal static string EncodeBase64Url(ReadOnlySpan<byte> tokenBytes)
        => Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void RemoveUnconsumedEntry(string token)
    {
        CopilotRuntimeOperationCapabilityV1? capability;
        lock (sync)
        {
            capability = pendingEntries.TryGetValue(token, out var entry) ? entry.Capability : null;
            pendingEntries.Remove(token);
        }

        capability?.Release();
    }

    private void PurgeExpiredUnderLock(long nowTicks)
    {
        List<KeyValuePair<string, PendingEntry>>? expired = null;
        foreach (var pair in pendingEntries)
        {
            if (nowTicks >= pair.Value.ExpiresAtTicks)
            {
                (expired ??= []).Add(pair);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var pair in expired)
        {
            pendingEntries.Remove(pair.Key);
            pair.Value.Capability.Release();
        }
    }

    private sealed class PendingEntry(
        CopilotRuntimeOperationCapabilityV1 capability,
        int expectedBodyLength,
        byte[] expectedBodySha256,
        long expiresAtTicks)
    {
        internal CopilotRuntimeOperationCapabilityV1 Capability { get; } = capability;

        internal int ExpectedBodyLength { get; } = expectedBodyLength;

        internal byte[] ExpectedBodySha256 { get; } = expectedBodySha256;

        internal long ExpiresAtTicks { get; } = expiresAtTicks;
    }
}

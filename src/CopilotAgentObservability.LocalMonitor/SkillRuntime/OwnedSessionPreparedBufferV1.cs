using System.Security.Cryptography;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed record OwnedSessionPreparedBodyV1(int Ordinal, ReadOnlyMemory<byte> BodyUtf8, int Length, ReadOnlyMemory<byte> Sha256);

internal sealed record OwnedSessionPreparedImportV1(
    string NativeSessionId,
    string SourceVersion,
    ReadOnlyMemory<byte> StartEnvelopeUtf8,
    IReadOnlyList<OwnedSessionPreparedBodyV1> Bodies,
    ReadOnlyMemory<byte> TerminalEnvelopeUtf8);

internal sealed class OwnedSessionPreparedBufferV1
{
    internal const int MaxInvocationCount = 64;
    internal const int MaxAggregateBodyBytes = 8_388_608;

    private readonly object sync = new();
    private readonly List<OwnedSessionPreparedBodyV1> bodies = [];
    private string? nativeSessionId;
    private string? sourceVersion;
    private byte[]? startEnvelope;
    private byte[]? terminalEnvelope;
    private int aggregateBodyBytes;
    private bool poisoned;
    private bool frozen;

    public void AcceptStart(string sessionId, string version, ReadOnlySpan<byte> envelopeUtf8)
    {
        lock (sync)
        {
            if (poisoned || frozen || nativeSessionId is not null || string.IsNullOrEmpty(sessionId)
                || string.IsNullOrEmpty(version) || envelopeUtf8.IsEmpty)
            {
                poisoned = true;
                return;
            }

            nativeSessionId = sessionId;
            sourceVersion = version;
            startEnvelope = envelopeUtf8.ToArray();
        }
    }

    public bool TryAcceptInvocation(string sessionId, ReadOnlySpan<byte> bodyUtf8)
    {
        lock (sync)
        {
            if (poisoned || frozen || terminalEnvelope is not null || nativeSessionId is null
                || !string.Equals(nativeSessionId, sessionId, StringComparison.Ordinal)
                || bodyUtf8.IsEmpty || bodies.Count == MaxInvocationCount)
            {
                poisoned = true;
                return false;
            }

            int nextSize;
            try { nextSize = checked(aggregateBodyBytes + bodyUtf8.Length); }
            catch (OverflowException) { poisoned = true; return false; }
            if (nextSize > MaxAggregateBodyBytes)
            {
                poisoned = true;
                return false;
            }

            var owned = bodyUtf8.ToArray();
            bodies.Add(new OwnedSessionPreparedBodyV1(bodies.Count, owned, owned.Length, SHA256.HashData(owned)));
            aggregateBodyBytes = nextSize;
            return true;
        }
    }

    public void AcceptSuccessfulTerminal(string sessionId, ReadOnlySpan<byte> envelopeUtf8)
    {
        lock (sync)
        {
            if (poisoned || frozen || terminalEnvelope is not null || nativeSessionId is null
                || !string.Equals(nativeSessionId, sessionId, StringComparison.Ordinal) || envelopeUtf8.IsEmpty)
            {
                poisoned = true;
                return;
            }

            terminalEnvelope = envelopeUtf8.ToArray();
        }
    }

    public void Poison()
    {
        lock (sync) poisoned = true;
    }

    public OwnedSessionPreparedImportV1? TryFreeze(string sessionId, string version)
    {
        lock (sync)
        {
            if (poisoned || frozen || terminalEnvelope is null || startEnvelope is null
                || !string.Equals(nativeSessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(sourceVersion, version, StringComparison.Ordinal))
            {
                poisoned = true;
                return null;
            }

            frozen = true;
            return new OwnedSessionPreparedImportV1(
                nativeSessionId!, sourceVersion!, startEnvelope.ToArray(),
                bodies.Select(static body => body with
                {
                    BodyUtf8 = body.BodyUtf8.ToArray(),
                    Sha256 = body.Sha256.ToArray(),
                }).ToArray(),
                terminalEnvelope.ToArray());
        }
    }
}

using System.Security.Cryptography;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class SkillRuntimeCapabilityBridgeV1Tests
{
    [Fact]
    public async Task ForwardThenConsume_TransfersCapabilityWithBodyEvidence_ExactlyOnce()
    {
        var fixture = NewBridge();

        var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded, outcome);
        Assert.Equal(1, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(1, fixture.Bridge.PendingCount);
        var send = Assert.Single(fixture.Transport.Sends);

        Assert.True(fixture.Bridge.TryConsume(send.Token, out var transfer));
        Assert.NotNull(transfer);
        Assert.Equal(send.Body.Length, transfer!.ExpectedBodyLength);
        Assert.Equal(SHA256.HashData(send.Body), transfer.ExpectedBodySha256);
        Assert.IsAssignableFrom<ISkillInvocationV2RuntimeCapability>(transfer.RuntimeCapability);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(1, fixture.Generation.OutstandingCapabilityCount);

        Assert.False(fixture.Bridge.TryConsume(send.Token, out _));

        transfer.ReleaseTransferredCapability();
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa+a")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/a")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=a")]
    public void TryConsume_TokenGrammarViolations_ReturnFalse(string? header)
    {
        var fixture = NewBridge();

        Assert.False(fixture.Bridge.TryConsume(header, out _));
    }

    [Fact]
    public void EncodeBase64Url_32RandomBytes_IsExactly43UnpaddedUrlSafeCharacters()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var token = SkillRuntimeCapabilityBridgeV1.EncodeBase64Url(RandomNumberGenerator.GetBytes(32));
            AssertTokenGrammar(token);
        }

        var slashAndPlusHeavy = new byte[32];
        Array.Fill(slashAndPlusHeavy, (byte)0xff);
        var encoded = SkillRuntimeCapabilityBridgeV1.EncodeBase64Url(slashAndPlusHeavy);
        AssertTokenGrammar(encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Fact]
    public async Task PendingCapacity_64EntriesAdmitted_65thRefusedWithoutSend()
    {
        var fixture = NewBridge();
        foreach (var _ in Enumerable.Range(0, SkillRuntimeCapabilityBridgeV1.MaxPendingEntries))
        {
            var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None);
            Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded, outcome);
        }

        Assert.Equal(SkillRuntimeCapabilityBridgeV1.MaxPendingEntries, fixture.Bridge.PendingCount);

        var refused = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable, refused);
        Assert.Equal(SkillRuntimeCapabilityBridgeV1.MaxPendingEntries, fixture.Bridge.PendingCount);
        Assert.Equal(SkillRuntimeCapabilityBridgeV1.MaxPendingEntries, fixture.Transport.Sends.Count);
        Assert.Equal(64, fixture.Generation.OutstandingCapabilityCount);

        var consumed = fixture.Bridge.TryConsume(fixture.Transport.Sends[0].Token, out var transfer);
        Assert.True(consumed);
        transfer!.ReleaseTransferredCapability();

        var admittedAgain = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None);
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded, admittedAgain);
        Assert.Equal(SkillRuntimeCapabilityBridgeV1.MaxPendingEntries, fixture.Bridge.PendingCount);
    }

    [Fact]
    public async Task PendingCapacity_ExpiredEntriesPurgedBeforeAdmissionCheck()
    {
        var fixture = NewBridge();
        foreach (var _ in Enumerable.Range(0, SkillRuntimeCapabilityBridgeV1.MaxPendingEntries))
        {
            Assert.Equal(
                SkillRuntimeBridgeForwardOutcome.Forwarded,
                await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None));
        }

        fixture.Clock.NowTicks += SkillRuntimeCapabilityBridgeV1.EntryLifetimeTicks + 1;

        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None));
        Assert.Equal(1, fixture.Bridge.PendingCount);
        Assert.Equal(1, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task EntryExpiry_ValidOnlyWhileNowStrictlyBeforeExpiresAt(long offsetTicks, bool consumable)
    {
        var fixture = NewBridge();
        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None));
        var token = Assert.Single(fixture.Transport.Sends).Token;

        fixture.Clock.NowTicks += SkillRuntimeCapabilityBridgeV1.EntryLifetimeTicks + offsetTicks;

        Assert.Equal(consumable, fixture.Bridge.TryConsume(token, out var transfer));
        if (consumable)
        {
            transfer!.ReleaseTransferredCapability();
        }

        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(0, fixture.Bridge.PendingCount);
    }

    [Fact]
    public async Task PurgeExpired_RemovesEntriesAtOrPastLifetime()
    {
        var fixture = NewBridge();
        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None));
        Assert.Equal(1, fixture.Bridge.PendingCount);

        fixture.Clock.NowTicks += SkillRuntimeCapabilityBridgeV1.EntryLifetimeTicks;
        fixture.Bridge.PurgeExpired();

        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task ClockOverflow_AtExpiryComputation_ReturnsUnavailableWithoutSend()
    {
        var fixture = NewBridge();
        fixture.Clock.NowTicks = long.MaxValue - SkillRuntimeCapabilityBridgeV1.EntryLifetimeTicks + 1;

        var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable, outcome);
        Assert.Empty(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task RngTokenSource_NullOrWrongLength_ReturnsUnavailableWithoutSend(int tokenLength)
    {
        var fixture = NewBridge(tokenSource: new FixedTokenSource { });
        fixture.TokenSource.SetFallback(tokenLength == 0 ? null : new byte[tokenLength]);

        var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable, outcome);
        Assert.Empty(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task ForcedTokenCollision_SecondForwardRefused_FirstEntrySurvives()
    {
        var fixture = NewBridge(tokenSource: new FixedTokenSource());
        var sharedBytes = RandomNumberGenerator.GetBytes(32);
        fixture.TokenSource.SetFallback(sharedBytes);

        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None));
        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Unavailable,
            await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None));

        Assert.Equal(1, fixture.Bridge.PendingCount);
        Assert.Equal(1, fixture.Generation.OutstandingCapabilityCount);
        Assert.Single(fixture.Transport.Sends);

        var token = SkillRuntimeCapabilityBridgeV1.EncodeBase64Url(sharedBytes);
        Assert.True(fixture.Bridge.TryConsume(token, out var transfer));
        transfer!.ReleaseTransferredCapability();
    }

    [Fact]
    public async Task BodyPreflight_ExactlyAtLimit_Forwards()
    {
        var fixture = NewBridge();
        var sourceEvent = RequiredOnlyEvent();
        sourceEvent.Data.Content = new string('a', ContentLengthForTotalBytes(SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes));

        var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", sourceEvent, CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded, outcome);
        Assert.Equal(SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes, Assert.Single(fixture.Transport.Sends).Body.Length);
    }

    [Fact]
    public async Task BodyPreflight_OneByteOverLimit_UnavailableWithoutSend()
    {
        var fixture = NewBridge();
        var sourceEvent = RequiredOnlyEvent();
        sourceEvent.Data.Content = new string('a', ContentLengthForTotalBytes(SkillInvocationNormalizedJsonV1.MaxProducerBodyBytes + 1));

        var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", sourceEvent, CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable, outcome);
        Assert.Empty(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task StaleCallback_OwningGenerationReplaced_ZeroSendAndNoBorrowing()
    {
        var fixture = NewBridge();
        var staleGeneration = fixture.Generation;
        var replacement = fixture.Admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _);
        Assert.NotNull(replacement);

        var outcome = await fixture.Bridge.ForwardCallbackAsync(staleGeneration, "native-session", RequiredOnlyEvent(), CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable, outcome);
        Assert.Empty(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, replacement!.OutstandingCapabilityCount);
        Assert.Equal(0, staleGeneration.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task InvalidationAfterRegistration_RemovesTokenAndReleasesCapability()
    {
        var fixture = NewBridge();
        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None));
        var token = Assert.Single(fixture.Transport.Sends).Token;

        fixture.Admission.InvalidateCurrentGeneration();

        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.False(fixture.Bridge.TryConsume(token, out _));
    }

    [Fact]
    public async Task SerializerFailure_UnavailableWithoutSendAndCapabilityReleased()
    {
        var fixture = NewBridge();
        var invalidEvent = RequiredOnlyEvent();
        invalidEvent.Data.Content = "invalid-\ud800";

        var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", invalidEvent, CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable, outcome);
        Assert.Empty(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(FakeBridgeTransportBehavior.Refuse)]
    [InlineData(FakeBridgeTransportBehavior.Throw)]
    public async Task TransportFailure_RemovesEntryAndReleasesCapability(FakeBridgeTransportBehavior behavior)
    {
        var fixture = NewBridge();
        fixture.Transport.NextBehavior = behavior;

        var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable, outcome);
        Assert.Single(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task InvalidationDuringSend_RemovesUnconsumedEntryAndReportsUnavailable()
    {
        var fixture = NewBridge();
        fixture.Transport.SendingCallback = (_, _, _) => fixture.Admission.InvalidateCurrentGeneration();

        var outcome = await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable, outcome);
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task CallerCancellationAfterForward_ConsumeReleasesAndRefuses()
    {
        var fixture = NewBridge();
        using var callerCancellation = new CancellationTokenSource();
        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), callerCancellation.Token));
        var token = Assert.Single(fixture.Transport.Sends).Token;

        callerCancellation.Cancel();

        Assert.False(fixture.Bridge.TryConsume(token, out _));
        Assert.Equal(0, fixture.Bridge.PendingCount);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task ReleaseTransferredCapability_ManyThreads_ExactlyOnce()
    {
        var fixture = NewBridge();
        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardCallbackAsync(fixture.Generation, "native-session", RequiredOnlyEvent(), CancellationToken.None));
        Assert.True(fixture.Bridge.TryConsume(Assert.Single(fixture.Transport.Sends).Token, out var transfer));

        Parallel.For(0, 64, _ => transfer!.ReleaseTransferredCapability());

        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    private static void AssertTokenGrammar(string token)
    {
        Assert.Equal(SkillRuntimeCapabilityBridgeV1.TokenStringLength, token.Length);
        Assert.All(token, c => Assert.True(
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-',
            $"Unexpected token character '{c}'."));
    }

    private static int ContentLengthForTotalBytes(int totalBytes)
    {
        var baselineEvent = RequiredOnlyEvent();
        baselineEvent.Data.Content = "a";
        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-session", baselineEvent, out var baselineBody));
        return totalBytes - Assert.IsType<byte[]>(baselineBody).Length + 1;
    }

    private static SkillInvokedEvent RequiredOnlyEvent() => new()
    {
        Id = Guid.Parse("018f0f4e-7b2a-4c11-8a3b-123456789abc"),
        Timestamp = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        Data = new SkillInvokedData
        {
            Name = "skill-name",
            Path = "skills/SKILL.md",
            Content = "body"
        }
    };

    private static BridgeFixture NewBridge(FixedTokenSource? tokenSource = null) => new(tokenSource);

    private sealed class BridgeFixture
    {
        public BridgeFixture(FixedTokenSource? fixedTokenSource)
        {
            Admission = new CopilotRuntimeAdmissionV1();
            Generation = Admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _)!;
            Transport = new FakeBridgeTransport();
            Clock = new ManualClock();
            TokenSource = fixedTokenSource ?? new FixedTokenSource();
            Func<byte[]?> tokenSource = fixedTokenSource is null
                ? static () => RandomNumberGenerator.GetBytes(SkillRuntimeCapabilityBridgeV1.TokenByteLength)
                : TokenSource.Next;
            Bridge = new SkillRuntimeCapabilityBridgeV1(Admission, Transport, Clock.Ticks, tokenSource);
        }

        public CopilotRuntimeAdmissionV1 Admission { get; }
        public CopilotRuntimeGenerationV1 Generation { get; }
        public FakeBridgeTransport Transport { get; }
        public ManualClock Clock { get; }
        public FixedTokenSource TokenSource { get; }
        public SkillRuntimeCapabilityBridgeV1 Bridge { get; }
    }
}

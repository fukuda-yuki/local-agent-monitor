using System.Text.Json;
using System.Security.Cryptography;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class OwnedSessionPostCompletionImporterV1Tests
{
    [Fact]
    public void OutcomeWireMapsEveryClosedFailureWithoutSensitiveValues()
    {
        Assert.Equal(
            ["candidate_not_admitted", "prepared_body_rejected", "candidate_lost_during_first_v2", "first_v2_forward_unavailable", "candidate_lost_during_later_v2", "later_v2_forward_unavailable", "start_envelope_rejected", "terminal_envelope_rejected", "start_validation_rejected", "start_queue_refused", "start_commit_busy", "start_commit_failed", "start_commit_timeout", "start_commit_canceled", "terminal_validation_rejected", "terminal_queue_refused", "terminal_commit_busy", "terminal_commit_failed", "terminal_commit_timeout", "terminal_commit_canceled", "unexpected_import_exception"],
            Enum.GetValues<OwnedSessionPostFreezeOutcomeV1>().Skip(1).Select(OwnedSessionPostFreezeOutcomeObservationV1.Wire));
    }

    [Fact]
    public async Task ImportAsync_SendsExactBodiesInOrderThenCommitsStartAndTerminal()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var generation = Assert.IsType<CopilotRuntimeGenerationV1>(admission.PublishReadyTestCandidate(
            new FakeSkillRuntimeClient(), SkillInvocationV2TestIdentity.V1065, out _));
        var transport = new FakeBridgeTransport();
        var tokens = new FixedTokenSource();
        tokens.Enqueue(new byte[32], Enumerable.Repeat((byte)1, 32).ToArray());
        var bridge = new SkillRuntimeCapabilityBridgeV1(admission, transport, () => 0, tokens.Next);
        var queue = new SessionEventQueue(2);
        var importer = new OwnedSessionPostCompletionImporterV1(
            bridge, queue, TimeSpan.FromSeconds(1), TimeProvider.System);
        var start = Envelope("session.start");
        var terminal = Envelope("session.task_complete");
        var prepared = new OwnedSessionPreparedImportV1("session-1", "1.0.65", Serialize(start),
        [
            Body(0, 7),
            Body(1, 8),
        ], Serialize(terminal));

        var import = importer.ImportAsync(generation, prepared, CancellationToken.None);
        await CompleteNextAsync(queue, SessionEventCommitStatus.Committed, "session.start");
        await CompleteNextAsync(queue, SessionEventCommitStatus.Committed, "session.task_complete");

        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.Success, await import);
        Assert.Equal(new byte[] { 7, 8 }, transport.Sends.Select(static send => send.Body.Single()));
        Assert.Equal(2, transport.Sends.Select(static send => send.Token).Distinct().Count());
    }

    [Fact]
    public async Task ImportAsync_ZeroBodies_WritesNothing()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var generation = Assert.IsType<CopilotRuntimeGenerationV1>(admission.PublishReadyTestCandidate(
            new FakeSkillRuntimeClient(), SkillInvocationV2TestIdentity.V1065, out _));
        var transport = new FakeBridgeTransport();
        var bridge = new SkillRuntimeCapabilityBridgeV1(admission, transport, () => 0, () => new byte[32]);
        var queue = new SessionEventQueue(2);
        var importer = new OwnedSessionPostCompletionImporterV1(
            bridge, queue, TimeSpan.FromSeconds(1), TimeProvider.System);

        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.Success, await importer.ImportAsync(generation,
            new("session-1", "1.0.65", new byte[] { 1 }, [], new byte[] { 2 }), CancellationToken.None));
        Assert.Empty(transport.Sends);
        Assert.Equal(0, queue.Count);
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("length")]
    [InlineData("sha")]
    public async Task ImportAsync_InvalidPreparedEvidenceFailsBeforeFirstSend(string mutation)
    {
        var fixture = new ImportFixture();
        var valid = Body(0, 7);
        var invalid = mutation switch
        {
            "ordinal" => valid with { Ordinal = 1 },
            "length" => valid with { Length = 2 },
            _ => valid with { Sha256 = new byte[32] },
        };

        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.PreparedBodyRejected, await fixture.Importer.ImportAsync(fixture.Generation, Prepared([invalid]), CancellationToken.None));
        Assert.Empty(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task ImportAsync_SecondV2FailureLeavesOnlyFirstValidTransportPrefixAndNoV1()
    {
        var fixture = new ImportFixture(refuseAt: 2);

        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.LaterV2ForwardUnavailable, await fixture.Importer.ImportAsync(fixture.Generation,
            Prepared([Body(0, 7), Body(1, 8), Body(2, 9)]), CancellationToken.None));

        Assert.Equal(new byte[] { 7, 8 }, fixture.Transport.Sends.Select(static send => send.Body.Single()));
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public async Task ImportAsync_TransportThrowIsSanitizedWithoutRetryAndLeavesExactValidPrefix(
        int throwAt, int expectedPrefix)
    {
        var fixture = new ImportFixture(throwAt: throwAt);

        Assert.Equal(throwAt == 1 ? OwnedSessionPostFreezeOutcomeV1.FirstV2ForwardUnavailable : OwnedSessionPostFreezeOutcomeV1.LaterV2ForwardUnavailable, await fixture.Importer.ImportAsync(fixture.Generation,
            Prepared([Body(0, 7), Body(1, 8), Body(2, 9)]), CancellationToken.None));

        Assert.Equal(throwAt, fixture.Transport.Sends.Count);
        Assert.Equal(expectedPrefix, fixture.Transport.ConsumedCount);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(1, 0, 1, 11)]
    [InlineData(2, 0, 1, 12)]
    [InlineData(0, 1, 2, 17)]
    [InlineData(0, 2, 2, 18)]
    public async Task ImportAsync_V1RejectedDispositionLeavesOnlyCommittedPrefix(
        int startStatusValue, int terminalStatusValue, int writes, int expectedValue)
    {
        var startStatus = (SessionEventCommitStatus)startStatusValue;
        var terminalStatus = (SessionEventCommitStatus)terminalStatusValue;
        var fixture = new ImportFixture();
        var import = fixture.Importer.ImportAsync(fixture.Generation, Prepared([Body(0, 7)]), CancellationToken.None);
        for (var index = 0; index < writes; index++)
        {
            var request = await fixture.Queue.Reader.ReadAsync();
            fixture.Queue.MarkDequeued();
            Assert.True(request.TryClaim());
            request.Complete(index == 0 ? startStatus : terminalStatus);
        }

        Assert.Equal((OwnedSessionPostFreezeOutcomeV1)expectedValue, await import);
        Assert.Single(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_V1StartOrTerminalTimeoutAbandonsPendingWrite(bool completeStart)
    {
        var fixture = new ImportFixture(timeout: TimeSpan.FromMilliseconds(10));
        var import = fixture.Importer.ImportAsync(fixture.Generation, Prepared([Body(0, 7)]), CancellationToken.None);
        if (completeStart)
        {
            var start = await fixture.Queue.Reader.ReadAsync();
            fixture.Queue.MarkDequeued();
            Assert.True(start.TryClaim());
            start.Complete(SessionEventCommitStatus.Committed);
        }

        Assert.Equal(completeStart ? OwnedSessionPostFreezeOutcomeV1.TerminalCommitTimeout : OwnedSessionPostFreezeOutcomeV1.StartCommitTimeout, await import);
        Assert.Single(fixture.Transport.Sends);
        Assert.Equal(1, fixture.Queue.Count);
        var abandoned = await fixture.Queue.Reader.ReadAsync();
        fixture.Queue.MarkDequeued();
        Assert.False(abandoned.TryClaim());
        Assert.Equal(SessionEventCommitStatus.Failed, await abandoned.Completion);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_V1StartOrTerminalCancellationReturnsFalseAfterValidPrefix(bool completeStart)
    {
        var fixture = new ImportFixture();
        using var cancellation = new CancellationTokenSource();
        var import = fixture.Importer.ImportAsync(fixture.Generation, Prepared([Body(0, 7)]), cancellation.Token);
        var request = await fixture.Queue.Reader.ReadAsync();
        fixture.Queue.MarkDequeued();
        if (completeStart)
        {
            Assert.True(request.TryClaim());
            request.Complete(SessionEventCommitStatus.Committed);
            request = await fixture.Queue.Reader.ReadAsync();
            fixture.Queue.MarkDequeued();
        }
        cancellation.Cancel();

        Assert.Equal(completeStart ? OwnedSessionPostFreezeOutcomeV1.TerminalCommitCanceled : OwnedSessionPostFreezeOutcomeV1.StartCommitCanceled, await import);
        Assert.Single(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task ImportAsync_CancellationAfterWriterClaimWaitsForDispositionAndDoesNotQueueTerminal()
    {
        var fixture = new ImportFixture();
        using var cancellation = new CancellationTokenSource();
        var import = fixture.Importer.ImportAsync(fixture.Generation, Prepared([Body(0, 7)]), cancellation.Token);
        var start = await fixture.Queue.Reader.ReadAsync();
        fixture.Queue.MarkDequeued();
        Assert.True(start.TryClaim());

        cancellation.Cancel();
        await Task.Delay(25);
        Assert.False(import.IsCompleted);

        start.Complete(SessionEventCommitStatus.Committed);
        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.StartCommitCanceled, await import);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task ImportAsync_TimeoutAfterWriterClaimWaitsForDispositionAndDoesNotQueueTerminal()
    {
        var fixture = new ImportFixture(timeout: TimeSpan.FromMilliseconds(10));
        var import = fixture.Importer.ImportAsync(fixture.Generation, Prepared([Body(0, 7)]), CancellationToken.None);
        var start = await fixture.Queue.Reader.ReadAsync();
        fixture.Queue.MarkDequeued();
        Assert.True(start.TryClaim());

        await Task.Delay(25);
        Assert.False(import.IsCompleted);

        start.Complete(SessionEventCommitStatus.Committed);
        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.StartCommitTimeout, await import);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Theory]
    [InlineData(false, 13)]
    [InlineData(true, 19)]
    public async Task ImportAsync_TimeoutThenCancellationDuringClaimedDrainRemainsTimeout(
        bool terminal, int expectedValue)
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        var targetTimerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedTimerArmCount = terminal ? 2 : 1;
        var timerArmCount = 0;
        clock.TimerCreated = () =>
        {
            if (Interlocked.Increment(ref timerArmCount) == expectedTimerArmCount)
                targetTimerArmed.TrySetResult();
        };
        var fixture = new ImportFixture(timeout: TimeSpan.FromMilliseconds(10), timeProvider: clock);
        using var cancellation = new CancellationTokenSource();
        var import = fixture.Importer.ImportAsync(fixture.Generation, Prepared([Body(0, 7)]), cancellation.Token);
        var request = await fixture.Queue.Reader.ReadAsync();
        fixture.Queue.MarkDequeued();
        if (terminal)
        {
            Assert.True(request.TryClaim());
            request.Complete(SessionEventCommitStatus.Committed);
            request = await fixture.Queue.Reader.ReadAsync();
            fixture.Queue.MarkDequeued();
        }
        Assert.True(request.TryClaim());

        await targetTimerArmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(import.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.False(import.IsCompleted);
        cancellation.Cancel();
        request.Complete(SessionEventCommitStatus.Committed);

        Assert.Equal((OwnedSessionPostFreezeOutcomeV1)expectedValue, await import);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_PendingFailureFollowedByDelayedRealWriterDoesNotPersist(bool cancel)
    {
        var fixture = new ImportFixture(timeout: cancel ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(10));
        using var cancellation = new CancellationTokenSource();
        var import = fixture.Importer.ImportAsync(fixture.Generation, Prepared([Body(0, 7)]), cancellation.Token);
        if (cancel)
        {
            await WaitForQueueCountAsync(fixture.Queue, 1);
            cancellation.Cancel();
        }
        Assert.Equal(cancel ? OwnedSessionPostFreezeOutcomeV1.StartCommitCanceled : OwnedSessionPostFreezeOutcomeV1.StartCommitTimeout, await import);

        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext);
        store.CreateSchema();
        var worker = new SessionEventWriterWorker(fixture.Queue, new SessionEventNormalizer(store));
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Queue.Count);
        Assert.Empty(store.ListMostRecent(10));
    }

    [Fact]
    public async Task ImportAsync_CancellationBeforeV2ReturnsFalseWithoutPublication()
    {
        var fixture = new ImportFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.FirstV2ForwardUnavailable, await fixture.Importer.ImportAsync(
            fixture.Generation, Prepared([Body(0, 7)]), cancellation.Token));
        Assert.Empty(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_V1StartOrTerminalQueueRejectionReturnsFalse(bool rejectTerminal)
    {
        var fixture = new ImportFixture(queueCapacity: 1);
        if (!rejectTerminal)
            Assert.True(fixture.Queue.TryEnqueue(Envelope("blocker"), out _));

        var import = fixture.Importer.ImportAsync(fixture.Generation, Prepared([Body(0, 7)]), CancellationToken.None);
        if (rejectTerminal)
        {
            var start = await fixture.Queue.Reader.ReadAsync();
            fixture.Queue.MarkDequeued();
            Assert.True(fixture.Queue.TryEnqueue(Envelope("blocker"), out _));
            Assert.True(start.TryClaim());
            start.Complete(SessionEventCommitStatus.Committed);
        }

        Assert.Equal(rejectTerminal ? OwnedSessionPostFreezeOutcomeV1.TerminalQueueRefused : OwnedSessionPostFreezeOutcomeV1.StartQueueRefused, await import);
        Assert.Single(fixture.Transport.Sends);
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 8)]
    public async Task ImportAsync_StartOrTerminalMalformedEnvelopeIsDistinguished(bool terminal, int expectedValue)
    {
        var fixture = new ImportFixture();
        var valid = Serialize(Envelope("session.start"));
        var prepared = new OwnedSessionPreparedImportV1("session-1", "1.0.65",
            terminal ? valid : new byte[] { 0xff }, [Body(0, 7)], terminal ? new byte[] { 0xff } : valid);
        Assert.Equal((OwnedSessionPostFreezeOutcomeV1)expectedValue, await fixture.Importer.ImportAsync(fixture.Generation, prepared, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 15)]
    public async Task ImportAsync_StartOrTerminalValidationFailureIsDistinguished(bool terminal, int expectedValue)
    {
        var fixture = new ImportFixture();
        var valid = Serialize(Envelope("session.start"));
        var invalid = Serialize(Envelope("session.start") with { SchemaVersion = 2 });
        var prepared = new OwnedSessionPreparedImportV1("session-1", "1.0.65",
            terminal ? valid : invalid, [Body(0, 7)], terminal ? invalid : valid);
        if (terminal)
        {
            var import = fixture.Importer.ImportAsync(fixture.Generation, prepared, CancellationToken.None);
            await CompleteNextAsync(fixture.Queue, SessionEventCommitStatus.Committed, "session.start");
            Assert.Equal((OwnedSessionPostFreezeOutcomeV1)expectedValue, await import);
        }
        else Assert.Equal((OwnedSessionPostFreezeOutcomeV1)expectedValue, await fixture.Importer.ImportAsync(fixture.Generation, prepared, CancellationToken.None));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    public async Task ImportAsync_ForwardFailureObservesCandidateLossWithoutChangingPrefix(int invalidateAt, int expectedValue)
    {
        var fixture = new ImportFixture(invalidateAt: invalidateAt);
        Assert.Equal((OwnedSessionPostFreezeOutcomeV1)expectedValue, await fixture.Importer.ImportAsync(fixture.Generation,
            Prepared([Body(0, 7), Body(1, 8)]), CancellationToken.None));
        Assert.Equal(invalidateAt - 1, fixture.Transport.ConsumedCount);
    }

    private static SessionIngestEnvelope Envelope(string type) => new(1, "copilot-sdk-stream", "copilot-sdk", "session-1",
        [new(Guid.NewGuid().ToString("D"), type, "2026-08-23T00:00:00Z", JsonDocument.Parse("{}").RootElement.Clone())]);

    private static byte[] Serialize(SessionIngestEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static OwnedSessionPreparedBodyV1 Body(int ordinal, byte value)
    {
        var bytes = new[] { value };
        return new(ordinal, bytes, bytes.Length, SHA256.HashData(bytes));
    }

    private static OwnedSessionPreparedImportV1 Prepared(IReadOnlyList<OwnedSessionPreparedBodyV1> bodies) =>
        new("session-1", "1.0.65", Serialize(Envelope("session.start")), bodies,
            Serialize(Envelope("session.task_complete")));

    private static async Task CompleteNextAsync(SessionEventQueue queue, SessionEventCommitStatus status, string type)
    {
        var request = await queue.Reader.ReadAsync();
        queue.MarkDequeued();
        Assert.Equal(type, Assert.Single(request.Envelope.Events!).Type);
        Assert.True(request.TryClaim());
        request.Complete(status);
    }

    private static async Task WaitForQueueCountAsync(SessionEventQueue queue, int expected)
    {
        for (var attempt = 0; attempt < 100 && queue.Count != expected; attempt++)
            await Task.Delay(1);
        Assert.Equal(expected, queue.Count);
    }


    private sealed class ImportFixture
    {
        public ImportFixture(int? refuseAt = null, int? throwAt = null, TimeSpan? timeout = null, int queueCapacity = 2,
            int? invalidateAt = null, TimeProvider? timeProvider = null)
        {
            var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
            Generation = Assert.IsType<CopilotRuntimeGenerationV1>(admission.PublishReadyTestCandidate(
                new FakeSkillRuntimeClient(), SkillInvocationV2TestIdentity.V1065, out _));
            Transport = new ConsumingTransport(refuseAt, throwAt, invalidateAt,
                () => admission.InvalidateCandidate(Generation));
            var bridge = new SkillRuntimeCapabilityBridgeV1(admission, Transport, () => 0,
                () => RandomNumberGenerator.GetBytes(32));
            Transport.Bridge = bridge;
            Queue = new SessionEventQueue(queueCapacity);
            Importer = new OwnedSessionPostCompletionImporterV1(
                bridge, Queue, timeout ?? TimeSpan.FromSeconds(1), timeProvider ?? TimeProvider.System);
        }

        public CopilotRuntimeGenerationV1 Generation { get; }
        public ConsumingTransport Transport { get; }
        public SessionEventQueue Queue { get; }
        public OwnedSessionPostCompletionImporterV1 Importer { get; }
    }

    private sealed class ConsumingTransport(int? refuseAt, int? throwAt, int? invalidateAt, Action invalidate) : ISkillRuntimeBridgeTransport
    {
        public SkillRuntimeCapabilityBridgeV1? Bridge { get; set; }
        public List<(string Token, byte[] Body)> Sends { get; } = [];
        public int ConsumedCount { get; private set; }

        public Task<bool> SendAsync(string capabilityToken, ReadOnlyMemory<byte> bodyUtf8, CancellationToken cancellationToken)
        {
            Sends.Add((capabilityToken, bodyUtf8.ToArray()));
            if (invalidateAt == Sends.Count)
            {
                invalidate();
                return Task.FromResult(false);
            }
            if (throwAt == Sends.Count) throw new InvalidOperationException("synthetic transport failure");
            if (refuseAt == Sends.Count) return Task.FromResult(false);
            Assert.True(Bridge!.TryConsume(capabilityToken, out var transfer));
            Assert.Equal(bodyUtf8.Length, transfer!.ExpectedBodyLength);
            Assert.Equal(SHA256.HashData(bodyUtf8.Span), transfer.ExpectedBodySha256);
            transfer.ReleaseTransferredCapability();
            transfer.ReleaseTransferredCapability();
            ConsumedCount++;
            return Task.FromResult(true);
        }
    }
}
